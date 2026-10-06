using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ADLMRateGen.Services.Bill
{
    /// <summary>One bill item as it goes to ADLM Cloud.</summary>
    public sealed class CloudBillLine
    {
        public string SheetName { get; init; } = "";
        public int Row { get; init; }
        public string ItemRef { get; init; } = "";
        public string Section { get; init; } = "";
        public IReadOnlyList<string> Headings { get; init; } = Array.Empty<string>();
        public string Description { get; init; } = "";
        public string Unit { get; init; } = "";
        public decimal? Qty { get; init; }
        /// <summary>The accepted RateGen rate, or null for a line left unpriced.</summary>
        public BillRate? Rate { get; init; }
    }

    /// <summary>
    /// Saves a priced bill to ADLM Cloud as a RateGen project (POST /api/projects/rategen),
    /// and later saves over the same project (PUT /api/projects/rategen/{id}).
    ///
    /// Every measured item goes up, priced or not, so the cloud bill is the whole bill.
    /// A priced line carries its rate the way the website marks a rate the QS chose:
    /// rate, appliedRateKey (the RateGen rate's name) and rateLockedAt, so the website
    /// never re-derives it. Rates are in naira, whatever currency the screen shows.
    /// </summary>
    public static class BillCloudSaver
    {
        public const string ProductKey = "rategen";
        public const string Origin = "rategen-bill";

        public sealed class Result
        {
            public string ProjectId { get; init; } = "";
            public int Version { get; init; }
            public int Lines { get; init; }
            public int Priced { get; init; }
        }

        /// <summary>The project's bill items. Pure: no network.</summary>
        public static List<Dictionary<string, object?>> BuildItems(IEnumerable<CloudBillLine> lines, DateTime pricedAtUtc)
        {
            var items = new List<Dictionary<string, object?>>();
            var codes = new HashSet<string>(StringComparer.Ordinal);
            int sn = 0;
            foreach (var l in lines)
            {
                sn++;
                var code = Code(l.SheetName, l.Row);
                // Two lines can never share a code: the website finds a bill line by it.
                for (int i = 2; !codes.Add(code); i++) code = Code(l.SheetName, l.Row) + "-" + i.ToString(CultureInfo.InvariantCulture);

                var item = new Dictionary<string, object?>
                {
                    ["sn"] = sn,
                    ["code"] = code,
                    ["description"] = l.Description,
                    ["unit"] = l.Unit,
                    ["qty"] = (double)(l.Qty ?? 0m),
                    ["rate"] = l.Rate == null ? 0d : (double)l.Rate.Rate,
                    // The bill's own grouping: the section (else the sheet) and the heading lines above the item.
                    ["category"] = string.IsNullOrWhiteSpace(l.Section) ? l.SheetName : l.Section,
                    ["takeoffLine"] = string.Join(" / ", l.Headings.Where(h => !string.IsNullOrWhiteSpace(h))),
                };
                if (l.Rate != null)
                {
                    item["appliedRateKey"] = l.Rate.Name;
                    item["rateLockedAt"] = pricedAtUtc.ToString("o", CultureInfo.InvariantCulture);
                    item["trade"] = l.Rate.Trade;
                }
                items.Add(item);
            }
            return items;
        }

        /// <summary>A short, stable code for a bill row: the same row always gets the same code.</summary>
        public static string Code(string sheet, int row)
        {
            var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(sheet + "|" + row.ToString(CultureInfo.InvariantCulture)));
            return "RG-" + Convert.ToHexString(bytes, 0, 5);
        }

        /// <summary>
        /// Creates the project, or saves over it when <paramref name="projectId"/> is given.
        /// Throws UnauthorizedAccessException (signed out, no RateGen subscription, the
        /// project limit, view-only) and HttpRequestException with the website's message.
        /// </summary>
        public static async Task<Result> SaveAsync(string name, IReadOnlyList<CloudBillLine> lines, string? projectId, CancellationToken ct)
        {
            var items = BuildItems(lines, DateTime.UtcNow);
            var body = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["origin"] = Origin,
                ["items"] = items,
            };
            var client = AuthProvider.Instance.Client;
            using var doc = string.IsNullOrWhiteSpace(projectId)
                ? await client.SendJsonAsync(HttpMethod.Post, "/api/projects/" + ProductKey, body, ct).ConfigureAwait(false)
                : await client.SendJsonAsync(HttpMethod.Put, "/api/projects/" + ProductKey + "/" + Uri.EscapeDataString(projectId), body, ct).ConfigureAwait(false);

            var root = doc.RootElement;
            string id = root.TryGetProperty("_id", out var idEl) ? idEl.ToString()
                      : root.TryGetProperty("id", out var id2) ? id2.ToString() : projectId ?? "";
            int version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            if (string.IsNullOrWhiteSpace(id))
                throw new HttpRequestException("ADLM Cloud saved the bill but did not say where. Check your projects on adlmstudio.net.");
            return new Result { ProjectId = id, Version = version, Lines = items.Count, Priced = lines.Count(l => l.Rate != null) };
        }

        /// <summary>Where the saved project opens on the website.</summary>
        public static string WebUrl(string projectId) => "https://adlmstudio.net/work/project/" + ProductKey + "/" + Uri.EscapeDataString(projectId);
    }
}
