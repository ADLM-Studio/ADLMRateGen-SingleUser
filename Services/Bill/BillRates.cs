using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ADLMRateGen.Services.Bill
{
    /// <summary>One of the user's all-in rates a bill line can be priced from.</summary>
    public sealed class BillRate
    {
        public string Id { get; init; } = "";
        public string Trade { get; init; } = "";
        public string Name { get; init; } = "";
        public string Unit { get; init; } = "";
        /// <summary>The unit's family ("m2", "m3", "nr"...), "" when unknown.</summary>
        public string UnitKey { get; init; } = "";
        /// <summary>All-in rate per unit in naira: net cost plus overhead and profit, as the trade screen shows it.</summary>
        public decimal Rate { get; init; }

        public override string ToString() => Name;
    }

    /// <summary>
    /// Every priced rate RateGen shows, from the trades and the services, as the
    /// carbon screen collects them (MainViewModel hands over the same sources).
    /// A rate the user edited carries the edit, because the trade item does.
    /// </summary>
    public static class BillRateCatalogue
    {
        public static List<BillRate> Build(IEnumerable<(string Trade, object Item)> sources)
        {
            var list = new List<BillRate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var (trade, item) in sources)
            {
                n++;
                if (item == null) continue;
                var name = (Prop<string>(item, "Description") ?? "").Trim();
                var unit = (Prop<string>(item, "Unit") ?? "").Trim();
                var total = Prop<double>(item, "TotalCost");
                if (total <= 0) total = Prop<double>(item, "NetCost");
                if (name.Length == 0 || total <= 0) continue;
                // The same rate listed twice (a cloud copy beside the local one) is offered once.
                if (!seen.Add(trade + "|" + name + "|" + unit)) continue;
                list.Add(new BillRate
                {
                    Id = "r" + n.ToString(CultureInfo.InvariantCulture),
                    Trade = trade,
                    Name = name,
                    Unit = unit,
                    UnitKey = ClientBillUnits.Canonical(unit),
                    Rate = Math.Round((decimal)total, 2),
                });
            }
            // Bills measure steel by the kg as often as by the tonne. A tonne rate is offered for
            // kg lines (and a kg rate for tonne lines) at the same money: 1 t = 1,000 kg.
            foreach (var r in list.Where(r => r.UnitKey == "t" || r.UnitKey == "kg").ToList())
            {
                var toKg = r.UnitKey == "t";
                list.Add(new BillRate
                {
                    Id = r.Id + (toKg ? "kg" : "t"),
                    Trade = r.Trade,
                    Name = r.Name + (toKg ? " (per kg, from the per-tonne rate)" : " (per tonne, from the per-kg rate)"),
                    Unit = toKg ? "kg" : "t",
                    UnitKey = toKg ? "kg" : "t",
                    Rate = Math.Round(toKg ? r.Rate / 1000m : r.Rate * 1000m, 2),
                });
            }
            return list;
        }

        private static T? Prop<T>(object o, string name)
        {
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) return default;
            var v = p.GetValue(o);
            if (v == null) return default;
            if (v is T t) return t;
            try { return (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture); } catch { return default; }
        }
    }

    /// <summary>
    /// Narrows the rates offered to the AI for a batch of bill lines: same unit
    /// family only, ranked by the words and figures the line shares with the rate
    /// (rarer words count more: "sandcrete" says more than "in"). The AI then picks
    /// from a short list it can actually read, and the request stays small.
    /// </summary>
    public static class BillCandidatePicker
    {
        private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
        {
            "the", "and", "or", "of", "in", "to", "on", "at", "for", "with", "a", "an", "as", "by", "be", "is",
            "all", "per", "not", "exceeding", "including", "complete", "supply", "fix", "fixing", "provide",
            "procure", "install", "installed", "place", "placed", "laid", "lay", "ditto", "do", "item",
            "same", "above", "described", "measured", "separately", "over", "under", "into", "from",
        };

        private static readonly Regex TokenRx = new(@"\d+(?::\d+(?:\.\d+)?)+|\d+(?:\.\d+)?|[a-z]+", RegexOptions.Compiled);

        /// <summary>Lower-case words and figures, ratios kept whole ("1:2:4"), sizes split ("225mm" -> "225", "mm").</summary>
        public static List<string> Tokens(string text)
        {
            var s = (text ?? "").ToLowerInvariant().Replace('×', 'x');
            var list = new List<string>();
            foreach (Match m in TokenRx.Matches(s))
            {
                var t = m.Value;
                if (t.Length < 2 && !char.IsDigit(t[0])) continue;
                if (Stop.Contains(t)) continue;
                list.Add(t);
            }
            return list;
        }

        /// <summary>The text a bill line is read as: section, headings, the item it continues, its own words.</summary>
        public static string ContextOf(ClientBillRow row)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(row.Section)) parts.Add(row.Section);
            parts.AddRange(row.Headings.Where(h => !string.IsNullOrWhiteSpace(h)));
            if (!string.IsNullOrWhiteSpace(row.ContinuesFrom)) parts.Add(row.ContinuesFrom);
            parts.Add(row.Description ?? "");
            return string.Join(" › ", parts);
        }

        /// <summary>
        /// Up to <paramref name="perRow"/> rates per line, in the line's own unit, best first;
        /// the batch's union is capped at <paramref name="max"/>.
        /// </summary>
        public static List<BillRate> For(IEnumerable<(string Text, string UnitKey)> rows, IReadOnlyList<BillRate> rates,
                                         int perRow = 8, int max = 400)
        {
            var idf = Idf(rates);
            var rateTokens = rates.ToDictionary(r => r.Id, r => new HashSet<string>(Tokens(r.Name + " " + r.Trade)));
            var picked = new List<BillRate>();
            var have = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (text, unitKey) in rows)
            {
                if (string.IsNullOrEmpty(unitKey)) continue;
                foreach (var r in Rank(text, unitKey, rates, rateTokens, idf).Take(perRow))
                {
                    if (have.Add(r.Id)) picked.Add(r);
                    if (picked.Count >= max) return picked;
                }
            }
            return picked;
        }

        /// <summary>The rates of one unit family ranked against free text (the review screen's search uses it too).</summary>
        public static IEnumerable<BillRate> Search(string text, string unitKey, IReadOnlyList<BillRate> rates, int take = 30)
        {
            var idf = Idf(rates);
            var rateTokens = rates.ToDictionary(r => r.Id, r => new HashSet<string>(Tokens(r.Name + " " + r.Trade)));
            return Rank(text, unitKey, rates, rateTokens, idf, keepZero: string.IsNullOrWhiteSpace(text)).Take(take);
        }

        private static IEnumerable<BillRate> Rank(string text, string unitKey, IReadOnlyList<BillRate> rates,
            Dictionary<string, HashSet<string>> rateTokens, Dictionary<string, double> idf, bool keepZero = false)
        {
            var words = new HashSet<string>(Tokens(text));
            return rates
                .Where(r => string.IsNullOrEmpty(unitKey) || r.UnitKey == unitKey)
                .Select(r => (Rate: r, Score: words.Where(rateTokens[r.Id].Contains).Sum(w => idf.TryGetValue(w, out var v) ? v : 1.0)))
                .Where(x => keepZero || x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Rate.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Rate);
        }

        private static Dictionary<string, double> Idf(IReadOnlyList<BillRate> rates)
        {
            var df = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in rates)
                foreach (var t in new HashSet<string>(Tokens(r.Name + " " + r.Trade)))
                    df[t] = df.TryGetValue(t, out var c) ? c + 1 : 1;
            var n = Math.Max(1, rates.Count);
            return df.ToDictionary(kv => kv.Key, kv => Math.Log(1.0 + n / (double)kv.Value));
        }
    }

    /// <summary>The money arithmetic of a priced bill, in one place.</summary>
    public static class BillMoney
    {
        /// <summary>The bill's quantity as written: "1,250.5" -> 1250.5; blank, text or a dash -> null.</summary>
        public static decimal? ParseQty(string? text)
        {
            var s = (text ?? "").Trim().Replace(",", "").Replace(" ", "");
            if (s.Length == 0) return null;
            return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        }

        /// <summary>Quantity x rate, to the kobo.</summary>
        public static decimal Amount(decimal qty, decimal rate) => Math.Round(qty * rate, 2, MidpointRounding.AwayFromZero);
    }
}
