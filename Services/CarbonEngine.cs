using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ADLMRateGen.Services
{
    /// <summary>
    /// Upfront embodied carbon (RICS whole life carbon, modules A1-A5) of a build-up
    /// line, from the factors in Data/carbonFactors.json. Every factor carries its
    /// published source; every mass that is an assumption rather than stated by the
    /// item is marked, so the screen can say so.
    ///
    ///   A1-A3  product        = kg x factor
    ///   A4     transport      = kg x a4 (0.005 local / 0.032 national, kgCO2e/kg)
    ///   A5w    site waste     = kg x wf x (factor + a4 + C2 + C3-C4)
    ///   A5a    site energy    = fuel burnt x fuel factor (diesel, petrol, LPG)
    /// </summary>
    public static class CarbonEngine
    {
        public sealed class Factor
        {
            public string Id = "", Label = "", Pattern = "", Source = "", MassBasis = "", WasteBasis = "", LowSource = "";
            public double Value, A4, Wf, C34;
            /// <summary>The low end of a range (Nigerian cement, Scope 1 only), or null when the factor is single.</summary>
            public double? ValueLow;
            public bool Fuel, MassAssumed;
            public JObject Mass = new();
            public Regex Rx = null!;
        }

        public sealed class LineCarbon
        {
            public Factor Factor = null!;
            public double Kg, A13, A4, A5w, A5a;
            public double Total => A13 + A4 + A5w + A5a;
            /// <summary>The same line with the factor's low end (equal to Total when the factor has none).</summary>
            public double TotalLow;
            public string Basis = "";
        }

        private static List<Factor>? _factors;
        private static double _c2 = 0.005;
        public static string Method { get; private set; } = "";
        public static IReadOnlyList<string> Sources { get; private set; } = Array.Empty<string>();

        public static IReadOnlyList<Factor> Factors
        {
            get { Load(); return _factors!; }
        }

        public static string FilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "carbonFactors.json");

        private static void Load()
        {
            if (_factors != null) return;
            var list = new List<Factor>();
            try
            {
                var doc = JObject.Parse(File.ReadAllText(FilePath));
                _c2 = doc.Value<double?>("c2") ?? 0.005;
                Method = doc.Value<string>("method") ?? "";
                Sources = doc["sources"]?.Select(s => (string?)s ?? "").ToList() ?? new List<string>();
                foreach (var f in doc["factors"] ?? new JArray())
                {
                    var pattern = f.Value<string>("pattern") ?? "";
                    list.Add(new Factor
                    {
                        Id = f.Value<string>("id") ?? "",
                        Label = f.Value<string>("label") ?? "",
                        Pattern = pattern,
                        Source = f.Value<string>("factorSource") ?? "",
                        MassBasis = f.Value<string>("massBasis") ?? "",
                        WasteBasis = f.Value<string>("wfBasis") ?? "",
                        Value = f.Value<double?>("factor") ?? 0,
                        ValueLow = f.Value<double?>("factorLow"),
                        LowSource = f.Value<string>("lowSource") ?? "",
                        A4 = f.Value<double?>("a4") ?? 0,
                        Wf = f.Value<double?>("wf") ?? 0,
                        C34 = f.Value<double?>("c34") ?? 0.013,
                        Fuel = f.Value<bool?>("fuel") ?? false,
                        MassAssumed = f.Value<bool?>("massAssumed") ?? false,
                        Mass = f["mass"] as JObject ?? new JObject(),
                        Rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Carbon] factors not loaded: {ex.Message}");
            }
            _factors = list;
        }

        /// <summary>The factor for a library row ("category :: name"), or null.</summary>
        public static Factor? Match(string? category, string name)
        {
            var key = ((category ?? "").Trim() + " :: " + (name ?? "").Trim()).ToLowerInvariant();
            return Factors.FirstOrDefault(f => f.Rx.IsMatch(key));
        }

        /// <summary>
        /// Carbon of <paramref name="qty"/> library units of the item, or null when the
        /// item has no factor or its mass cannot be worked out.
        /// </summary>
        public static LineCarbon? Assess(string? category, string name, string unit, double qty, string? hint = null)
        {
            var f = Match(category, name);
            if (f == null || qty <= 0) return null;
            var perUnit = MassPerUnit(f, name, unit, hint);
            if (perUnit == null || perUnit <= 0) return null;

            var amount = qty * perUnit.Value;      // kg of material, or litres / kg of fuel
            var r = new LineCarbon { Factor = f };
            var inv = CultureInfo.InvariantCulture;
            if (f.Fuel)
            {
                r.A5a = amount * f.Value;
                r.TotalLow = r.A5a;
                r.Basis = $"{f.Label}: {amount.ToString("0.###", inv)} x {f.Value.ToString("0.#####", inv)} kgCO2e (site energy, A5a). {f.Source}.";
                return r;
            }
            r.Kg = amount;
            r.A13 = amount * f.Value;
            r.A4 = amount * f.A4;
            r.A5w = amount * f.Wf * (f.Value + f.A4 + _c2 + f.C34);
            var low = f.ValueLow ?? f.Value;
            r.TotalLow = amount * low + r.A4 + amount * f.Wf * (low + f.A4 + _c2 + f.C34);
            r.Basis =
                $"{f.Label}: {amount.ToString("0.###", inv)} kg ({perUnit.Value.ToString("0.###", inv)} kg per {unit}; {f.MassBasis}" +
                (f.MassAssumed ? ", assumed" : "") + $") x {f.Value.ToString("0.####", inv)} kgCO2e/kg. {f.Source}. " +
                $"Transport {f.A4.ToString("0.###", inv)} kgCO2e/kg; waste: {f.WasteBasis}." +
                (f.ValueLow.HasValue
                    ? $" Low end {r.TotalLow.ToString("0.###", inv)} kgCO2e at {f.ValueLow.Value.ToString("0.###", inv)} kgCO2e/kg: {f.LowSource}."
                    : "");
            return r;
        }

        private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        private static bool IsMetre(string u) =>
            u is "m" or "lm" or "lin m" or "metre" or "metres" or "meter" or "meters" or "mtr" or "rm" or "l.m";

        /// <summary>kg (or litres of fuel) per library unit.</summary>
        public static double? MassPerUnit(Factor f, string name, string unit, string? hint = null)
        {
            var u = (unit ?? "").Trim().ToLowerInvariant().TrimEnd('.');
            var n = (name ?? "").ToLowerInvariant();
            var h = (hint ?? "").ToLowerInvariant();
            var m = f.Mass;

            // a tile's thickness, where the line or the item names one ("600 x 600 x 10mm",
            // "1.3mm floor flex"), comes before the per-m2 fallback
            if (m.Value<string>("rule") == "tile" && (u == "m2" || u.StartsWith("m2")))
            {
                foreach (var text in new[] { h, n })
                {
                    var t = Regex.Match(text, @"x\s*(\d+(?:\.\d+)?)\s*mm(?!.*x\s*\d)");
                    if (!t.Success) t = Regex.Match(text, @"^(\d+(?:\.\d+)?)\s*mm\b");
                    if (t.Success) return Num(t.Groups[1].Value) / 1000.0 * (m.Value<double?>("density") ?? 2000);
                }
            }

            // stated per unit
            if (m["perUnit"] is JObject per)
            {
                foreach (var p in per.Properties())
                    if (string.Equals(p.Name, u, StringComparison.OrdinalIgnoreCase)) return (double)p.Value;
                if (u.EndsWith("litre") && per["litre"] != null)
                {
                    var lm = Regex.Match(u, @"^(\d+(?:\.\d+)?)\s*litre");
                    return (lm.Success ? Num(lm.Groups[1].Value) : 1) * (double)per["litre"]!;
                }
            }

            // paint: litres in the unit x density ("4 Litre", "Lit/m2", "litre/m2")
            if (m.Value<bool?>("litres") == true)
            {
                var lm = Regex.Match(u, @"^(\d+(?:\.\d+)?)?\s*(litre|ltr|lit|l)\b");
                if (u == "gal") return 4.546 * (m.Value<double?>("density") ?? 1.3);
                if (!lm.Success) return null;
                var litres = lm.Groups[1].Success ? Num(lm.Groups[1].Value) : 1;
                return litres * (m.Value<double?>("density") ?? 1.3);
            }

            switch (m.Value<string>("rule"))
            {
                case "mesh":
                {
                    var d = Regex.Match(n, @"\ba(142|193|252)\b");
                    if (!d.Success) return null;
                    var kgm2 = d.Groups[1].Value switch { "142" => 2.22, "193" => 3.02, _ => 3.95 };
                    return kgm2 * 4.8 * 2.4;
                }
                case "plate":
                {
                    var p = Regex.Match(n, @"(\d+(?:\.\d+)?)\s*x\s*(\d+(?:\.\d+)?)\s*x\s*(\d+)(?:-(\d+))?\s*mm");
                    if (!p.Success) return null;
                    var t = p.Groups[4].Success ? (Num(p.Groups[3].Value) + Num(p.Groups[4].Value)) / 2 : Num(p.Groups[3].Value);
                    return Num(p.Groups[1].Value) * Num(p.Groups[2].Value) * t / 1000.0 * 7850;
                }
                case "block":
                {
                    // NIS 87:2007 sizes at 1,920 kg/m3 (see the factor's massBasis)
                    if (n.StartsWith("225")) return 27.5;
                    if (n.StartsWith("150")) return 18.2;
                    if (n.StartsWith("100")) return 19.4;
                    return null;
                }
                case "timber":
                {
                    var t = Regex.Match(n, @"\((\d+)x(\d+)x(\d+)mm\)");
                    if (!t.Success) return null;
                    var m3 = Num(t.Groups[1].Value) * Num(t.Groups[2].Value) * Num(t.Groups[3].Value) / 1e9;
                    return m3 * (m.Value<double?>("density") ?? 500);
                }
                case "board":
                {
                    var t = Regex.Match(n, @"\((\d+)x(\d+)x(\d+)mm\)");
                    if (!t.Success) return null;
                    var m3 = Num(t.Groups[1].Value) * Num(t.Groups[2].Value) * Num(t.Groups[3].Value) / 1e9;
                    return m3 * (m.Value<double?>("density") ?? 600);
                }
                case "aluminium":
                {
                    if (n.Contains("angle ridge")) return u == "m" ? 0.0007 * 0.6 * 2700 : null;
                    var t = Regex.Match(n, @"(\d\.\d+)mm");
                    if (!t.Success || u != "m2") return null;
                    return Num(t.Groups[1].Value) / 1000.0 * 2700;
                }
                case "glass":
                {
                    var g = Regex.Match(n, @"\((\d+)x(\d+)mm\).*?(\d)mm");
                    if (!g.Success) return null;
                    return Num(g.Groups[1].Value) / 1000.0 * Num(g.Groups[2].Value) / 1000.0 * Num(g.Groups[3].Value) / 1000.0 * 2500;
                }
                case "cable":
                {
                    // Copper conductor only, from the cross-section the name gives:
                    // "4 core 35mm2 ... copper cable", "35mm2 bare copper earth conductor",
                    // "8mm copper round wire". Insulation, sheath and armour are not counted.
                    if (!IsMetre(u)) return null;
                    var density = m.Value<double?>("density") ?? 8890;
                    var core = Regex.Match(n, @"(\d+)\s*core\s+(\d+(?:\.\d+)?)\s*mm2");
                    if (core.Success) return Num(core.Groups[1].Value) * Num(core.Groups[2].Value) * 1e-6 * density;
                    var bare = Regex.Match(n, @"(\d+(?:\.\d+)?)\s*mm2\b.*\bcopper\b");
                    if (bare.Success) return Num(bare.Groups[1].Value) * 1e-6 * density;
                    var round = Regex.Match(n, @"(\d+(?:\.\d+)?)\s*mm\s+copper\s+round\s+wire");
                    if (round.Success)
                    {
                        var d = Num(round.Groups[1].Value);
                        return Math.PI / 4 * d * d * 1e-6 * density;
                    }
                    return null;
                }
                case "pipe":
                {
                    // Outside diameter x wall from the factor's own size table (a standard's
                    // dimensions), keyed by the first size the name gives: "PPR ... 32mm".
                    if (!IsMetre(u) || m["sizes"] is not JObject sizes) return null;
                    foreach (Match s in Regex.Matches(n, @"(\d+)\s*mm\b"))
                    {
                        if (sizes[s.Groups[1].Value] is not JArray dims || dims.Count != 2) continue;
                        double od = (double)dims[0], wall = (double)dims[1];
                        return Math.PI * (od - wall) * wall * 1e-6 * (m.Value<double?>("density") ?? 1000);
                    }
                    return null;
                }
            }
            return null;
        }
    }
}
