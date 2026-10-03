// Ported from HERON (ADLMPlanswiftApp, feat/heron-3 5d3c093) on 3 Oct 2026, which took it
// from QUIV (RevitPluginArch, feat/client-bill-fill). Same reader QUIV and HERON use, so a fix
// to how firms write bills belongs in all three.
#nullable disable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ADLMRateGen.Services.Bill
{
    /// <summary>
    /// One unit vocabulary for the whole feature: the column detector, the row classifier
    /// and the review grid must agree on what "Sq. M", "LIN.M", "No" and "Bag" are, or a
    /// sheet is read with the wrong columns and its items are classified the wrong way.
    ///
    /// Spellings collected from ~730 real client workbooks. Anything that does not resolve
    /// is Unknown: shown to the QS, never filled silently.
    /// </summary>
    public enum UnitKind
    {
        /// <summary>Measured off the model: m, m2, m3, nr, kg, t.</summary>
        Measured,
        /// <summary>Priced as a whole: Item, Sum, Provisional Sum, %, and time (hr/day/week).</summary>
        Sum,
        /// <summary>A build-up line's unit: bag, roll, trip, length, pkt…</summary>
        Resource,
        /// <summary>Not a unit this reader knows.</summary>
        Unknown,
    }

    public static class ClientBillUnits
    {
        /// <summary>Lower-case, no spaces, no dots: "Sq. M" → "sqm", "No." → "no".</summary>
        public static string Key(string unit)
        {
            var s = (unit ?? "").Trim().ToLowerInvariant();
            s = s.Replace(" ", "");
            s = Regex.Replace(s, @"[\s\.]+", "");
            return s;
        }

        /// <summary>The canonical unit ("sq.m", "SQM", "Sq M" → "m2"), or "" when unknown.</summary>
        public static string Canonical(string unit)
        {
            var k = Key(unit);
            if (k.Length == 0) return "";
            string canon;
            if (Families.TryGetValue(k, out canon)) return canon;
            // Compound rate units are measured as they stand: "m3/km", "m2/day".
            if (k.IndexOf('/') > 0)
            {
                var parts = k.Split('/');
                string head;
                if (Families.TryGetValue(parts[0], out head) && MeasuredCanon.Contains(head)) return k;
            }
            return "";
        }

        public static UnitKind Kind(string unit)
        {
            var c = Canonical(unit);
            if (c.Length == 0) return UnitKind.Unknown;
            if (MeasuredCanon.Contains(c) || c.IndexOf('/') > 0) return UnitKind.Measured;
            if (SumCanon.Contains(c)) return UnitKind.Sum;
            if (ResourceCanon.Contains(c)) return UnitKind.Resource;
            return UnitKind.Unknown;
        }

        public static bool IsMeasured(string unit) { return Kind(unit) == UnitKind.Measured; }
        public static bool IsSum(string unit) { return Kind(unit) == UnitKind.Sum; }
        public static bool IsResource(string unit) { return Kind(unit) == UnitKind.Resource; }
        public static bool IsKnown(string unit) { return Kind(unit) != UnitKind.Unknown; }

        /// <summary>A ditto mark in a unit cell: the unit of the row above applies.</summary>
        public static bool IsDitto(string unit)
        {
            var s = (unit ?? "").Trim();
            if (s.Length == 0) return false;
            if (s == "\"" || s == "''" || s == "″" || s == "”" || s == "-\"-" || s == ",,") return true;
            return Regex.IsMatch(s, @"^-?\s*(do|ditto|d°)\s*\.?\s*-?$", RegexOptions.IgnoreCase);
        }

        private static readonly HashSet<string> MeasuredCanon = new HashSet<string>(StringComparer.Ordinal)
        {
            "m", "m2", "m3", "nr", "kg", "t",
        };
        private static readonly HashSet<string> SumCanon = new HashSet<string>(StringComparer.Ordinal)
        {
            "item", "sum", "%", "hr", "day", "week", "month", "lot", "set",
        };
        private static readonly HashSet<string> ResourceCanon = new HashSet<string>(StringComparer.Ordinal)
        {
            "bag", "roll", "sheet", "pkt", "drum", "tin", "litre", "gal", "trip", "load", "bundle", "carton", "length", "plank",
        };

        private static readonly Dictionary<string, string> Families = Build();

        private static Dictionary<string, string> Build()
        {
            var t = new Dictionary<string, string>(StringComparer.Ordinal);
            Action<string, string[]> add = (canon, spellings) =>
            {
                t[canon] = canon;
                foreach (var s in spellings) t[Key(s)] = canon;
            };

            add("m", new[] { "lm", "linm", "lin m", "l.m", "rm", "rmt", "mtr", "mtrs", "metre", "metres", "meter", "meters", "lin", "linear m", "run m", "r.m" });
            add("m2", new[] { "m2", "m²", "sqm", "sq m", "sq.m", "sqmt", "sqmtr", "sm", "squarem", "square metre", "square meter", "sqmetre", "m^2", "m 2" });
            add("m3", new[] { "m3", "m³", "cum", "cu m", "cu.m", "cumt", "cubm", "cubic metre", "cubic meter", "cm3", "m^3", "m 3" });
            // "unit"/"units" is deliberately NOT here: it is a column label, and a header row
            // repeated mid-sheet must not read as a measured item.
            add("nr", new[] { "nr", "no", "nos", "no.", "number", "numbers", "each", "ea", "pcs", "pc", "piece", "pieces", "pt", "pts", "point", "points", "n0", "nº" });
            add("kg", new[] { "kg", "kgs", "kilogram", "kilogramme", "kilo", "kilos" });
            add("t", new[] { "t", "tonne", "tonnes", "ton", "tons", "mt", "metric ton", "tne" });

            add("item", new[] { "item", "items", "itm" });
            add("sum", new[] { "sum", "sums", "ls", "l.s", "l/s", "lsum", "l.sum", "lumpsum", "lump sum", "ps", "p.s", "psum", "p.sum", "prov", "prov sum", "prov.sum", "provisional", "provisional sum", "pcsum", "pc sum", "p.c.sum", "sm.", "sumn", "summ" });
            add("%", new[] { "%", "percent", "pct" });
            add("hr", new[] { "hr", "hrs", "hour", "hours" });
            add("day", new[] { "day", "days", "dy" });
            add("week", new[] { "week", "weeks", "wk", "wks" });
            add("month", new[] { "month", "months", "mth", "mths" });
            add("lot", new[] { "lot", "lots" });
            add("set", new[] { "set", "sets" });

            add("bag", new[] { "bag", "bags", "bg" });
            add("roll", new[] { "roll", "rolls" });
            add("sheet", new[] { "sheet", "sheets", "sht", "shts" });
            add("pkt", new[] { "pkt", "pkts", "packet", "packets", "pack", "packs" });
            add("drum", new[] { "drum", "drums" });
            add("tin", new[] { "tin", "tins" });
            add("litre", new[] { "litre", "litres", "liter", "liters", "ltr", "ltrs", "l" });
            add("gal", new[] { "gal", "gals", "gallon", "gallons" });
            add("trip", new[] { "trip", "trips" });
            add("load", new[] { "load", "loads" });
            add("bundle", new[] { "bundle", "bundles", "budl", "bdl", "bndl" });
            add("carton", new[] { "carton", "cartons", "ctn" });
            add("length", new[] { "length", "lengths", "lgth", "lgths" });
            add("plank", new[] { "plank", "planks" });
            return t;
        }
    }
}
