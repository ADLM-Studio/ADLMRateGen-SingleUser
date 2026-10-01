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
            public string Id = "", Label = "", Pattern = "", Source = "", MassBasis = "", WasteBasis = "";
            public double Value, A4, Wf, C34;
            public bool Fuel, MassAssumed;
            public JObject Mass = new();
            public Regex Rx = null!;
        }

        public sealed class LineCarbon
        {
            public Factor Factor = null!;
            public double Kg, A13, A4, A5w, A5a;
            public double Total => A13 + A4 + A5w + A5a;
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
        public static LineCarbon? Assess(string? category, string name, string unit, double qty)
        {
            var f = Match(category, name);
            if (f == null || qty <= 0) return null;
            var perUnit = MassPerUnit(f, name, unit);
            if (perUnit == null || perUnit <= 0) return null;

            var amount = qty * perUnit.Value;      // kg of material, or litres / kg of fuel
            var r = new LineCarbon { Factor = f };
            var inv = CultureInfo.InvariantCulture;
            if (f.Fuel)
            {
                r.A5a = amount * f.Value;
                r.Basis = $"{f.Label}: {amount.ToString("0.###", inv)} x {f.Value.ToString("0.#####", inv)} kgCO2e (site energy, A5a). {f.Source}.";
                return r;
            }
            r.Kg = amount;
            r.A13 = amount * f.Value;
            r.A4 = amount * f.A4;
            r.A5w = amount * f.Wf * (f.Value + f.A4 + _c2 + f.C34);
            r.Basis =
                $"{f.Label}: {amount.ToString("0.###", inv)} kg ({perUnit.Value.ToString("0.###", inv)} kg per {unit}; {f.MassBasis}" +
                (f.MassAssumed ? ", assumed" : "") + $") x {f.Value.ToString("0.####", inv)} kgCO2e/kg. {f.Source}. " +
                $"Transport {f.A4.ToString("0.###", inv)} kgCO2e/kg; waste: {f.WasteBasis}.";
            return r;
        }

        private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        /// <summary>kg (or litres of fuel) per library unit.</summary>
        public static double? MassPerUnit(Factor f, string name, string unit)
        {
            var u = (unit ?? "").Trim().ToLowerInvariant().TrimEnd('.');
            var n = (name ?? "").ToLowerInvariant();
            var m = f.Mass;

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

            // paint: litres in the unit x density
            if (m.Value<bool?>("litres") == true)
            {
                var lm = Regex.Match(u, @"^(\d+(?:\.\d+)?)?\s*(litre|ltr|l)\b");
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
                    if (n.StartsWith("225")) return 23.8;
                    if (n.StartsWith("150")) return 17.3;
                    if (n.StartsWith("100")) return 13.5;
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
            }
            return null;
        }
    }
}
