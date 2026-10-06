using ADLMRateGen.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ADLMRateGen.Services
{
    /// <summary>
    /// Disk-backed store for admin-created rates (rategenrates collection).
    /// Pulls from: /rategen-v2/library/rates/sync
    /// </summary>
    public static class RateLibraryStore
    {
        public static event Action? Changed;

        public static IReadOnlyList<RateDefinition> Items { get; private set; }
            = Array.Empty<RateDefinition>();

        // Trades refresh their sections concurrently at start-up; the cache is
        // read, merged and written under this, so two refreshes cannot interleave.
        private static readonly object CacheGate = new();

        public static string FilePath =>
            Path.Combine(UserLibrarySync.UserDataFolder, "rate-library.json");

        private static string _apiBaseUrl = AppEnvironment.ApiBaseUrl;
        private static string _ratesSyncPath = "/rategen-v2/library/rates/sync";

        public static void ConfigureApi(string baseUrl, string? ratesSyncPath = null)
        {
            if (!string.IsNullOrWhiteSpace(baseUrl))
                _apiBaseUrl = baseUrl.TrimEnd('/');

            if (!string.IsNullOrWhiteSpace(ratesSyncPath))
                _ratesSyncPath = ratesSyncPath.StartsWith("/") ? ratesSyncPath : "/" + ratesSyncPath;
        }

        public static DateTime? LastApiSyncUtc { get; private set; }
        public static int LastApiStatusCode { get; private set; }
        public static int LastApiItemCount { get; private set; }
        public static string LastApiMessage { get; private set; } = "";

        public static void EnsureStoreExists()
        {
            Directory.CreateDirectory(UserLibrarySync.UserDataFolder);
            if (!File.Exists(FilePath))
                File.WriteAllText(FilePath, "[]");
        }

        public static void ReloadFromDisk()
        {
            try
            {
                EnsureStoreExists();

                var json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json))
                    json = "[]";

                var items = JsonConvert.DeserializeObject<List<RateDefinition>>(json)
                            ?? new List<RateDefinition>();

                Items = items;
                Debug.WriteLine($"[RateLibrary] ReloadFromDisk OK. Items={Items.Count}. File={FilePath}");
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RateLibrary] ReloadFromDisk FAILED: {ex}");
                Changed?.Invoke();
            }
        }

        public static void SaveToDisk(IEnumerable<RateDefinition> items)
        {
            EnsureStoreExists();

            var list = (items ?? Array.Empty<RateDefinition>()).ToList();
            var json = JsonConvert.SerializeObject(list, Formatting.Indented);
            File.WriteAllText(FilePath, json);

            Debug.WriteLine($"[RateLibrary] SaveToDisk OK. Items={list.Count}. File={FilePath}");

            ReloadFromDisk();
        }

        public static Task<bool> RefreshFromApiAsync(CancellationToken ct = default)
            => RefreshFromApiInternalAsync(sectionKey: null, ct);

        public static Task<bool> RefreshFromApiAsync(string sectionKey, CancellationToken ct = default)
            => RefreshFromApiInternalAsync(sectionKey, ct);

        private static async Task<bool> RefreshFromApiInternalAsync(string? sectionKey, CancellationToken ct)
        {
            LastApiSyncUtc = null;
            LastApiStatusCode = 0;
            LastApiItemCount = 0;
            LastApiMessage = "";

            var cfg = ConfigManager.LoadConfig();
            var token = cfg?.AuthToken ?? "";

            Debug.WriteLine($"[RateLibrary] Refresh START. Base={_apiBaseUrl}, Path={_ratesSyncPath}, Section={sectionKey ?? "(all)"}, TokenPresent={(token.Length > 20)}");

            if (string.IsNullOrWhiteSpace(token))
            {
                LastApiMessage = "Skipped rate-library refresh: missing AuthToken (user not logged in).";
                Debug.WriteLine($"[RateLibrary] {LastApiMessage}");
                return false;
            }

            var all = new List<RateDefinition>();
            string? cursor = null;
            int page = 0;

            try
            {
                using var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                };

                using var http = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(25)
                };

                // reduce weirdness on some networks
                http.DefaultRequestVersion = HttpVersion.Version11;
                http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

                http.DefaultRequestHeaders.Accept.Clear();
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                while (true)
                {
                    page++;
                    var url = $"{_apiBaseUrl}{_ratesSyncPath}?limit=500";

                    if (!string.IsNullOrWhiteSpace(sectionKey))
                        url += "&sectionKey=" + Uri.EscapeDataString(sectionKey.Trim());

                    if (!string.IsNullOrWhiteSpace(cursor))
                        url += "&cursor=" + Uri.EscapeDataString(cursor);

                    Debug.WriteLine($"[RateLibrary] GET page={page}: {url}");

                    var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                    LastApiStatusCode = (int)resp.StatusCode;

                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Debug.WriteLine($"[RateLibrary] HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. BodyLen={(body?.Length ?? 0)}");

                    if (!resp.IsSuccessStatusCode)
                    {
                        LastApiMessage =
                            $"Rate library API failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. " +
                            $"Body: {Trim(body, 350)}";

                        Debug.WriteLine($"[RateLibrary] FAIL: {LastApiMessage}");
                        if ((int)resp.StatusCode is 401 or 403)
                            Debug.WriteLine("[RateLibrary] Hint: 401/403 usually means AuthToken invalid or user lacks rategen entitlement.");

                        return false;
                    }

                    var parsed = TryParseSyncResponse(body);
                    if (parsed == null)
                    {
                        LastApiMessage = "Rate library API returned JSON but could not parse items.";
                        Debug.WriteLine($"[RateLibrary] FAIL: {LastApiMessage}. Body={Trim(body, 350)}");
                        return false;
                    }

                    if (parsed.Items.Count > 0)
                    {
                        all.AddRange(parsed.Items);
                        Debug.WriteLine($"[RateLibrary] Parsed items={parsed.Items.Count} (running total={all.Count})");
                    }
                    else
                    {
                        Debug.WriteLine("[RateLibrary] Parsed 0 items on this page.");
                    }

                    if (string.IsNullOrWhiteSpace(parsed.NextCursor))
                    {
                        Debug.WriteLine("[RateLibrary] No nextCursor. Done.");
                        break;
                    }

                    cursor = parsed.NextCursor;
                    Debug.WriteLine($"[RateLibrary] nextCursor={cursor}");
                }

                // de-dup by Id
                all = all
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                    .GroupBy(x => x.Id)
                    .Select(g => g.First())
                    .ToList();

                LastApiItemCount = all.Count;
                LastApiSyncUtc = DateTime.UtcNow;

                // A section-scoped refresh (each trade asks for its own on open)
                // replaces that section only. Saving it as the whole file used to
                // wipe every other trade's rates from the cache until the next full
                // sync: the last trade to refresh won, and the rest showed empty.
                lock (CacheGate)
                {
                var toSave = all;
                if (!string.IsNullOrWhiteSpace(sectionKey))
                {
                    var section = sectionKey.Trim();
                    ReloadFromDisk();
                    toSave = Items
                        .Where(x => x != null && !string.Equals((x.SectionKey ?? "").Trim(), section, StringComparison.OrdinalIgnoreCase))
                        .Concat(all)
                        .GroupBy(x => x.Id)
                        .Select(g => g.Last())
                        .ToList();
                }

                LastApiMessage = string.IsNullOrWhiteSpace(sectionKey)
                    ? $"Rate library OK. Items={all.Count}. Saved to disk."
                    : $"Rate library OK. Items={all.Count} in '{sectionKey}', {toSave.Count} cached in all. Saved to disk.";

                Debug.WriteLine($"[RateLibrary] SUCCESS: {LastApiMessage}");

                SaveToDisk(toSave);
                }
                return true;
            }
            catch (TaskCanceledException ex)
            {
                LastApiMessage = "Rate library API timed out or was cancelled.";
                Debug.WriteLine($"[RateLibrary] TIMEOUT/CANCEL: {ex}");
                return false;
            }
            catch (Exception ex)
            {
                LastApiMessage = $"Rate library API exception: {ex.Message}";
                Debug.WriteLine($"[RateLibrary] EXCEPTION: {ex}");
                return false;
            }
        }

        private sealed class SyncParsed
        {
            public List<RateDefinition> Items { get; set; } = new();
            public string? NextCursor { get; set; }
        }

        private static SyncParsed? TryParseSyncResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new SyncParsed();

            try
            {
                var jo = JObject.Parse(json);

                var itemsTok = jo["items"] ?? jo["data"];
                var nextCur = jo["nextCursor"]?.ToString();

                var items = itemsTok != null
                    ? itemsTok.ToObject<List<RateDefinition>>() ?? new List<RateDefinition>()
                    : new List<RateDefinition>();

                return new SyncParsed { Items = items, NextCursor = nextCur };
            }
            catch
            {
                // fallback: raw array
                try
                {
                    var list = JsonConvert.DeserializeObject<List<RateDefinition>>(json);
                    if (list != null) return new SyncParsed { Items = list, NextCursor = null };
                }
                catch { }

                return null;
            }
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }

    public sealed class RateDefinition
    {
        [JsonProperty("id")] public string? Id { get; set; }

        [JsonProperty("_id")]
        private string? _id
        {
            set { if (string.IsNullOrWhiteSpace(Id)) Id = value; }
        }

        [JsonProperty("sectionKey")] public string SectionKey { get; set; } = "";
        [JsonProperty("sectionLabel")] public string SectionLabel { get; set; } = "";

        [JsonProperty("itemNo")] public int? ItemNo { get; set; }
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("unit")] public string Unit { get; set; } = "";

        [JsonProperty("netCost")] public decimal NetCost { get; set; }
        [JsonProperty("overheadPercent")] public decimal OverheadPercent { get; set; }
        [JsonProperty("profitPercent")] public decimal ProfitPercent { get; set; }

        [JsonProperty("overheadValue")] public decimal OverheadValue { get; set; }
        [JsonProperty("profitValue")] public decimal ProfitValue { get; set; }
        [JsonProperty("totalCost")] public decimal TotalCost { get; set; }

        [JsonProperty("updatedAt")] public DateTime? UpdatedAt { get; set; }

        [JsonProperty("breakdown")] public List<RateBreakdownLine> Breakdown { get; set; } = new();
    }

    public sealed class RateBreakdownLine
    {
        [JsonProperty("componentName")] public string ComponentName { get; set; } = "";
        [JsonProperty("quantity")] public decimal Quantity { get; set; }
        [JsonProperty("unit")] public string Unit { get; set; } = "";
        [JsonProperty("unitPrice")] public decimal UnitPrice { get; set; }
        [JsonProperty("lineTotal")] public decimal LineTotal { get; set; }

        [JsonProperty("refKind")] public string? RefKind { get; set; }
        [JsonProperty("refSn")] public int? RefSn { get; set; }
        [JsonProperty("refName")] public string? RefName { get; set; }
    }
}
