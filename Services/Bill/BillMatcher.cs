using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ADLMRateGen.Helpers;

namespace ADLMRateGen.Services.Bill
{
    /// <summary>What ADLM AI proposed for one bill line.</summary>
    public sealed class BillMatch
    {
        public string RowId { get; init; } = "";
        public string RateId { get; init; } = "";
        public double Confidence { get; init; }
        public string Reason { get; init; } = "";
    }

    public sealed class BillMatchOutcome
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public List<BillMatch> Matches { get; } = new();
        public int Requests { get; set; }
        /// <summary>True when the service did not have /bill-match yet and the labour matcher answered instead.</summary>
        public bool UsedFallback { get; set; }
    }

    /// <summary>
    /// Sends a bill's measured items to ADLM AI in batches with a short list of the
    /// user's rates each (BillCandidatePicker), and collects which rate the AI says
    /// is the same work. The AI only picks; prices come from RateGen.
    ///
    /// /api/ai/bill-match is the route written for this. Until it is deployed the
    /// service answers 404 and the batch goes to /api/ai/budget-match, which takes the
    /// same rows read as one sentence each; its answers are weaker and are marked so.
    /// </summary>
    public static class BillMatcher
    {
        public const int RowsPerRequest = 40;
        public const int ParallelRequests = 3;
        public const int RatesPerRow = 8;

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

        public static async Task<BillMatchOutcome> MatchAsync(
            IReadOnlyList<ClientBillRow> items, IReadOnlyList<BillRate> rates,
            IProgress<string>? progress, CancellationToken ct)
        {
            var outcome = new BillMatchOutcome();
            var baseUrl = AiRateService.ServiceUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
                return Fail(outcome, "ADLM AI is switched off on this machine (ADLM_AI_URL is set to \"off\"). Clear that variable to price bills with AI.");
            var token = AiRateService.CurrentToken();
            if (string.IsNullOrWhiteSpace(token))
                return Fail(outcome, "Sign in to RateGen to price a bill with ADLM AI.");
            baseUrl = baseUrl.Trim().TrimEnd('/');

            var chunks = Enumerable.Range(0, (items.Count + RowsPerRequest - 1) / RowsPerRequest)
                .Select(i => items.Skip(i * RowsPerRequest).Take(RowsPerRequest).ToList())
                .ToList();
            var gate = new SemaphoreSlim(ParallelRequests);
            var sync = new object();
            string? firstError = null;
            bool fallback = false;
            int done = 0;
            progress?.Report($"Matching {items.Count} bill lines to your rates…");

            var tasks = chunks.Select(async chunk =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    lock (sync) { if (firstError != null) return; }
                    var cands = BillCandidatePicker.For(
                        chunk.Select(r => (BillCandidatePicker.ContextOf(r), ClientBillUnits.Canonical(r.Unit))),
                        rates, RatesPerRow, 400);
                    if (cands.Count == 0) { lock (sync) { done += chunk.Count; } return; }

                    bool useFallback; lock (sync) { useFallback = fallback; }
                    var one = useFallback ? null : await SendAsync(baseUrl + "/api/ai/bill-match", token, BillBody(chunk, cands), ct).ConfigureAwait(false);
                    if (one != null && one.NotFound) { lock (sync) { fallback = true; } useFallback = true; one = null; }
                    if (useFallback)
                        one = await SendAsync(baseUrl + "/api/ai/budget-match", token, BudgetBody(chunk, cands), ct).ConfigureAwait(false);

                    lock (sync)
                    {
                        if (one!.Error != null) { firstError ??= one.Error; return; }
                        outcome.Requests++;
                        var known = new HashSet<string>(chunk.Select(r => r.Id));
                        var ids = new HashSet<string>(cands.Select(c => c.Id));
                        foreach (var m in one.Matches)
                            if (known.Contains(m.RowId) && ids.Contains(m.RateId)) outcome.Matches.Add(m);
                        done += chunk.Count;
                        progress?.Report($"Matched {done} of {items.Count} bill lines…");
                    }
                }
                finally { gate.Release(); }
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);

            if (firstError != null) return Fail(outcome, firstError);
            outcome.UsedFallback = fallback;
            outcome.Ok = true;
            return outcome;
        }

        private static object BillBody(List<ClientBillRow> chunk, List<BillRate> cands) => new
        {
            rows = chunk.Select(r => new
            {
                id = r.Id,
                description = r.Description,
                unit = ClientBillUnits.Canonical(r.Unit),
                section = r.Section ?? "",
                headings = r.Headings ?? new List<string>(),
                continuesFrom = r.ContinuesFrom ?? "",
            }),
            candidates = cands.Select(c => new { id = c.Id, name = c.Name, unit = c.UnitKey, trade = c.Trade, rate = (double)c.Rate }),
        };

        // The labour matcher reads one sentence per row, so the context goes into the text.
        private static object BudgetBody(List<ClientBillRow> chunk, List<BillRate> cands) => new
        {
            rows = chunk.Select(r => new { id = r.Id, description = Clip(BillCandidatePicker.ContextOf(r), 200), unit = ClientBillUnits.Canonical(r.Unit) }),
            candidates = cands.Select(c => new { id = c.Id, name = c.Name, unit = c.UnitKey, rate = (double)c.Rate }),
        };

        private sealed class Reply
        {
            public string? Error;
            public bool NotFound;
            public List<BillMatch> Matches = new();
        }

        private static async Task<Reply> SendAsync(string url, string token, object body, CancellationToken ct)
        {
            var json = JsonConvert.SerializeObject(body);
            var reply = new Reply();
            JObject? parsed = null;
            // One retry on a network error or a 5xx (cold start, throttle); a 4xx is final.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    req.Headers.TryAddWithoutValidation("x-adlm-product", AppEnvironment.ProductKey);
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                    var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode) { parsed = JObject.Parse(text); reply.Error = null; break; }
                    if (resp.StatusCode == HttpStatusCode.NotFound) { reply.NotFound = true; return reply; }
                    reply.Error = Describe(resp.StatusCode, text);
                    if ((int)resp.StatusCode < 500) break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { reply.Error = "Could not reach ADLM AI (" + ex.Message + ")."; }
            }
            if (parsed == null) { reply.Error ??= "ADLM AI returned nothing."; return reply; }
            // Quota reached is a 200 with ok:false on this service.
            if (parsed.Value<bool?>("ok") == false)
            {
                reply.Error = parsed.Value<string>("message") ?? parsed.Value<string>("error") ?? "ADLM AI declined the request.";
                return reply;
            }
            var result = parsed["result"] as JObject ?? parsed;
            foreach (var m in result["matches"] as JArray ?? new JArray())
            {
                reply.Matches.Add(new BillMatch
                {
                    RowId = m.Value<string>("rowId") ?? "",
                    RateId = m.Value<string>("candidateId") ?? "",
                    Confidence = m.Value<double?>("confidence") ?? 0,
                    Reason = m.Value<string>("reason") ?? "",
                });
            }
            reply.Error = null;
            return reply;
        }

        private static string Describe(HttpStatusCode status, string body)
        {
            string? code = null, message = null;
            try
            {
                var j = JObject.Parse(body);
                code = j.Value<string>("code");
                message = j.Value<string>("error") ?? j.Value<string>("message");
            }
            catch { }
            if (code == "AI_NOT_ENTITLED" || status == HttpStatusCode.Forbidden)
                return "Your account does not include ADLM AI. Contact ADLM to add it.";
            if (status == HttpStatusCode.Unauthorized)
                return "Your RateGen session has expired. Sign in again and retry.";
            if (code == "CREDIT_THROTTLED" || (int)status == 503)
                return "ADLM AI is busy right now. Try again in a few minutes.";
            return "ADLM AI error " + (int)status + (string.IsNullOrWhiteSpace(message) ? "." : ": " + message);
        }

        private static string Clip(string s, int n) => s.Length <= n ? s : s.Substring(0, n);

        private static BillMatchOutcome Fail(BillMatchOutcome o, string error)
        {
            o.Ok = false;
            o.Error = error;
            return o;
        }
    }
}
