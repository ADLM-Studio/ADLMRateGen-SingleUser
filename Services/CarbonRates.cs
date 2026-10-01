using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ADLMRateGen.Helpers;
using ADLMRateGen.ViewModel;
using ADLMRateGen.ViewModel.CarbonOthers;
using ADLMRateGen.ViewModel.Model;

namespace ADLMRateGen.Services
{
    /// <summary>
    /// The carbon of every priced rate, built from that rate's own build-up: the
    /// same materials, the same quantities, the same library rows. Change a
    /// quantity and the price and the carbon move together.
    ///
    /// Practice followed (RICS whole life carbon, 2nd ed. 2023; IStructE 2020):
    /// upfront carbon A1-A5 per unit of the rate. Plant hire and labour carry no
    /// material carbon; the fuel the plant burns does (A5a). Worker transport
    /// (A5.4) is not counted. Where a line's carbon cannot be worked out (no
    /// published factor, or a mass that cannot be read), it is not guessed: it
    /// lowers the rate's coverage, which the screen shows beside the figure.
    /// </summary>
    public static class CarbonRates
    {
        // Lines that summarise the build-up rather than add to it.
        private static readonly Regex Summary = new(
            @"^(total\b|total cost|total rate|sub-?total:|net cost|overhead|profit|cost per|output per|compute po|⚠|- |check:|rate as published)",
            RegexOptions.IgnoreCase);

        // Lines that are labour or plant hire: no material carbon (their fuel is its own line).
        private static readonly Regex LabourOrPlant = new(
            @"\b(labour|labourer|operator|mason|carpenter|painter|tiler|plumber|electrician|fitter|welder|steel ?fixer|banksman|foreman|ganger|mate|operative|bulldozer|excavator|dozer|grader|roller|compactor|mixer|vibrator|crane|truck|tipper|loader|pump|hire|scaffold|gang)\b",
            RegexOptions.IgnoreCase);

        private static readonly Regex KindPrefix = new(@"^(material|labour|constant)\s*:\s*", RegexOptions.IgnoreCase);

        public static List<CarbonRateItem> Build(IEnumerable<(string Trade, object Item)> sources,
                                                 MaterialLibraryViewModel matLib, LabourLibraryViewModel labLib,
                                                 double overheadPct, double profitPct)
        {
            var mats = matLib.MaterialLibrary.ToList();
            var labs = labLib.LabourLibrary.ToList();
            var result = new List<CarbonRateItem>();

            foreach (var (trade, item) in sources)
            {
                try
                {
                    var c = Assess(trade, item, mats, labs);
                    if (c != null && c.CarbonTotal > 0) result.Add(c);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Carbon] {trade}: {ex.Message}");
                }
            }

            int n = 0;
            foreach (var c in result) c.ItemNo = ++n;
            return result;
        }

        private static T? Prop<T>(object o, string name)
        {
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) return default;
            var v = p.GetValue(o);
            if (v == null) return default;
            if (v is T t) return t;
            try { return (T)Convert.ChangeType(v, typeof(T)); } catch { return default; }
        }

        private static IEnumerable? Breakdown(object item)
        {
            foreach (var p in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.PropertyType == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(p.PropertyType)) continue;
                var el = p.PropertyType.IsGenericType ? p.PropertyType.GetGenericArguments().FirstOrDefault() : null;
                if (el?.GetProperty("ComponentName") != null) return p.GetValue(item) as IEnumerable;
            }
            return null;
        }

        private static readonly Regex Handling = new(@"\b(loading|unloading|handling|offloading)\b", RegexOptions.IgnoreCase);
        private static readonly Regex WasteAllowance = new(@"^add for waste|^waste allowance|\bwaste\b.*%", RegexOptions.IgnoreCase);

        private static MaterialModel? FindMaterial(string name, double unitPrice, List<MaterialModel> mats)
        {
            var exact = mats.FirstOrDefault(m => string.Equals(m.MaterialName, name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            foreach (var alt in RateNameAliases.Alternates(name))
            {
                var a = mats.FirstOrDefault(m => string.Equals(m.MaterialName, alt, StringComparison.OrdinalIgnoreCase));
                if (a != null) return a;
            }
            // "Fuel (Diesel)" names the library's "Diesel": the longest library name inside the line.
            // Not "Oil & Consumables (3% of Diesel)": lubricant is not fuel burnt.
            if (Regex.IsMatch(name, @"\boil\b|consumable", RegexOptions.IgnoreCase)) return null;
            var inside = mats.Where(m => (m.MaterialName ?? "").Length >= 5 &&
                                         name.IndexOf(m.MaterialName!, StringComparison.OrdinalIgnoreCase) >= 0)
                             .OrderByDescending(m => m.MaterialName!.Length)
                             .FirstOrDefault();
            if (inside != null) return inside;
            // "Cement" priced from "Cement (50kg bag)": a library row that starts with the
            // line's name and was priced at the line's unit price is the row it used.
            var starts = mats.Where(m => (m.MaterialName ?? "").StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (unitPrice > 0)
            {
                var byPrice = starts.FirstOrDefault(m => Math.Abs((double)m.MaterialPrice - unitPrice) <= Math.Max(1, unitPrice * 0.01));
                if (byPrice != null) return byPrice;
            }
            return starts.Count == 1 ? starts[0] : null;
        }

        private static bool IsLabour(string name, string unit, List<LabourModel> labs) =>
            labs.Any(l => string.Equals(l.LabourName, name, StringComparison.OrdinalIgnoreCase)) ||
            LabourOrPlant.IsMatch(name);

        private static CarbonRateItem? Assess(string trade, object item, List<MaterialModel> mats, List<LabourModel> labs)
        {
            var lines = Breakdown(item);
            if (lines == null) return null;

            double resourceCost = 0, coveredCost = 0, a13 = 0, a4 = 0, a5w = 0, a5a = 0;
            bool anyAssumed = false;
            var breakdown = new ObservableCollection<CarbonRateBreakdownLine>();

            foreach (var line in lines)
            {
                if (line == null) continue;
                var raw = Prop<string>(line, "ComponentName") ?? "";
                var total = Prop<double>(line, "TotalPrice");
                var qty = Prop<double>(line, "Quantity");
                var unit = Prop<string>(line, "Unit") ?? "";
                var unitPrice = Prop<double>(line, "UnitPrice");
                bool isTotal = Prop<bool>(line, "IsTotalLine") || Summary.IsMatch(raw.Trim());
                if (isTotal || string.IsNullOrWhiteSpace(raw)) continue;

                var name = KindPrefix.Replace(raw, "").Trim();
                var kind = KindPrefix.Match(raw).Success ? KindPrefix.Match(raw).Groups[1].Value.ToLowerInvariant() : "";
                resourceCost += total;

                var bl = new CarbonRateBreakdownLine
                {
                    ComponentName = name,
                    Quantity = qty,
                    Unit = unit,
                    UnitPrice = unitPrice,
                    TotalPrice = total,
                    RefName = name,
                    RefKind = kind
                };

                CarbonEngine.LineCarbon? carbon = null;
                MaterialModel? mat = null;
                if (kind != "labour")
                {
                    if (!Handling.IsMatch(name)) mat = FindMaterial(name, unitPrice, mats);
                    if (mat != null)
                    {
                        var price = (double)mat.MaterialPrice;
                        // the quantity in library units, exactly as the cost was built
                        var libQty = price > 0 && total > 0 ? total / price : qty;
                        carbon = CarbonEngine.Assess(mat.MaterialCategory, mat.MaterialName!, mat.MaterialUnit ?? "", libQty);
                        if (carbon != null) bl.RefName = mat.MaterialName!;
                    }
                    carbon ??= CarbonEngine.Assess("", name, unit, qty);
                }

                if (carbon != null)
                {
                    coveredCost += total;
                    a13 += carbon.A13; a4 += carbon.A4; a5w += carbon.A5w; a5a += carbon.A5a;
                    anyAssumed |= carbon.Factor.MassAssumed;
                    bl.CarbonKg = carbon.Total;
                    bl.CarbonBasis = carbon.Basis;
                }
                else if (WasteAllowance.IsMatch(name))
                {
                    coveredCost += total;          // material wasted on site is A5w, worked out per material above
                    bl.CarbonKg = 0;
                    bl.CarbonBasis = "Waste allowance: the carbon of material wasted on site is counted as A5w on each material line.";
                }
                else if (mat == null && (kind == "labour" || Handling.IsMatch(name) || IsLabour(name, unit, labs)))
                {
                    coveredCost += total;          // labour and plant hire: no material carbon
                    bl.CarbonKg = 0;
                    bl.CarbonBasis = "Labour or plant hire: no material carbon. The plant's fuel is counted on its own line (A5a).";
                }
                else
                {
                    bl.CarbonBasis = "No published carbon factor for this line, or its mass cannot be read from the library: not counted. It lowers this rate's coverage.";
                }
                breakdown.Add(bl);
            }

            var carbonOfBuildUp = a13 + a4 + a5w + a5a;
            if (carbonOfBuildUp <= 0 || resourceCost <= 0) return null;

            // A build-up written for a batch (a day's output, a mixer load) is divided down
            // to one unit for its price; the carbon is divided the same way.
            var net = Prop<double>(item, "NetCost");
            var ratio = net > 0 ? net / resourceCost : 1;
            var scale = ratio < 0.95 ? ratio : 1.0;
            var unitName = Prop<string>(item, "Unit") ?? "";

            breakdown.Add(new CarbonRateBreakdownLine
            {
                ComponentName = "Carbon of the build-up above",
                Quantity = 1, IsTotalLine = true, CarbonKg = carbonOfBuildUp
            });
            if (scale < 1)
                breakdown.Add(new CarbonRateBreakdownLine
                {
                    ComponentName = $"Per {unitName}: the build-up divided down as its price is (x {scale:0.######})",
                    Quantity = 1, IsTotalLine = true, CarbonKg = carbonOfBuildUp * scale
                });
            void Sum(string label, double v) =>
                breakdown.Add(new CarbonRateBreakdownLine { ComponentName = label, Quantity = 1, Unit = "kgCO2e", IsTotalLine = true, CarbonKg = v * scale });
            Sum("Product, A1-A3", a13);
            Sum("Transport to site, A4", a4);
            Sum("Site waste, A5w", a5w);
            if (a5a > 0) Sum("Site plant fuel, A5a", a5a);
            Sum($"Upfront carbon, A1-A5, per {unitName}", carbonOfBuildUp);

            var coverage = Math.Min(1.0, coveredCost / resourceCost);
            return new CarbonRateItem
            {
                Trade = trade,
                Description = Prop<string>(item, "Description") ?? "Untitled",
                Unit = unitName,
                NetCost = net,
                OverheadValue = Prop<double>(item, "OverheadValue"),
                ProfitValue = Prop<double>(item, "ProfitValue"),
                TotalCost = Prop<double>(item, "TotalCost"),
                CarbonA13 = a13 * scale,
                CarbonA4 = a4 * scale,
                CarbonA5 = (a5w + a5a) * scale,
                CarbonTotal = Math.Round(carbonOfBuildUp * scale, 3),
                Coverage = coverage,
                HasAssumedMass = anyAssumed,
                BreakdownLines = breakdown,
                Source = "Carbon"
            };
        }
    }
}
