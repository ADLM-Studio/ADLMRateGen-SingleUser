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
            @"^(total\b|total cost|total rate|sub-?total:|net cost|overhead|profit|cost per|output per|compute po|⚠|- |check:|rate as published|labour cost per|gang cost|labour per (sqm|m2|m3)|labour output)",
            RegexOptions.IgnoreCase);

        // Lines that are labour or plant hire: no material carbon (their fuel is its own line).
        private static readonly Regex LabourOrPlant = new(
            @"\b(labour|labourer|operator|mason|carpenter|painter|tiler|plumber|electrician|fitter|welder|steel ?fixer|banksman|foreman|ganger|mate|operative|bulldozer|excavator|dozer|grader|roller|compactor|mixer|vibrator|crane|truck|tipper|loader|pump|hire|scaffold|gang)\b",
            RegexOptions.IgnoreCase);

        private static readonly Regex KindPrefix = new(@"^(material|labour|constant)\s*:\s*", RegexOptions.IgnoreCase);

        /// <summary>A rate another build-up can reuse: its unit cost and its carbon per unit.</summary>
        private sealed record Ref(string Trade, string Description, string Unit, double NetCost, double CarbonPerUnit);

        public static List<CarbonRateItem> Build(IEnumerable<(string Trade, object Item)> sources,
                                                 MaterialLibraryViewModel matLib, LabourLibraryViewModel labLib,
                                                 double overheadPct, double profitPct)
        {
            var mats = matLib.MaterialLibrary.ToList();
            var labs = labLib.LabourLibrary.ToList();
            var src = sources.ToList();

            // Build-ups reuse one another ("Mortar per square meter" is the mortar rate,
            // "Formwork" the formwork rate, "Mixing - as before calculated" the mixer).
            // So carbon is worked out in passes: each pass can follow the rates whose
            // carbon the pass before found, until nothing more is found.
            var done = new Dictionary<object, CarbonRateItem>();
            for (int pass = 0; pass < 4; pass++)
            {
                var refs = done.Values.Where(c => c.NetCost > 0 && c.CarbonTotal > 0)
                    .Select(c => new Ref(c.Trade, c.Description, c.Unit, c.NetCost, c.CarbonTotal)).ToList();
                int before = done.Count;
                double totalBefore = done.Values.Sum(c => c.CarbonTotal);
                foreach (var (trade, item) in src)
                {
                    try
                    {
                        var c = Assess(trade, item, mats, labs, refs);
                        if (c != null && c.CarbonTotal > 0) done[item] = c;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Carbon] {trade}: {ex.Message}");
                    }
                }
                if (done.Count == before && Math.Abs(done.Values.Sum(c => c.CarbonTotal) - totalBefore) < 1e-6) break;
            }

            var result = src.Where(s => done.ContainsKey(s.Item)).Select(s => done[s.Item]).ToList();
            int n = 0;
            foreach (var c in result) c.ItemNo = ++n;
            return result;
        }

        // The library categories a trade's build-ups draw on, for matching a line by its price.
        private static readonly Dictionary<string, string[]> TradeCategories = new()
        {
            ["Ground"] = new[] { "fuels", "earthwork", "crushed rock" },
            ["Concrete"] = new[] { "cement", "earthwork", "crushed rock", "steel bar", "mesh", "timber", "plywood", "nails", "fuels" },
            ["Block Works"] = new[] { "cement", "earthwork", "crushed rock", "timber", "plywood", "nails" },
            ["Finishes"] = new[] { "finishes", "pvc floor", "cement", "earthwork", "terrazzo", "ceiling" },
            ["Roofs"] = new[] { "timber", "longspan", "nigerite", "zinc", "nails", "roof felting" },
            ["Painting"] = new[] { "paint" },
            ["Steel"] = new[] { "structural steel", "fuels" },
            ["Window and Door"] = new[] { "timber", "aluminium doors", "glasswork", "casement", "door", "plywood" },
        };

        private static readonly Regex Size = new(@"(\d+)\s*x\s*(\d+)", RegexOptions.IgnoreCase);
        private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
            { "size", "with", "from", "cost", "material", "per", "square", "meter", "metre", "length", "allow", "high", "thick", "wide", "including" };

        private static IEnumerable<string> Words(string s) =>
            Regex.Split(s.ToLowerInvariant(), @"[^a-z]+").Where(w => w.Length >= 4 && !Stop.Contains(w));

        /// <summary>
        /// The library row a line was priced from when its name is not the row's name:
        /// the rows at the line's unit price, preferring one that shares its size
        /// ("50 x 100mm" and "(50x100x3600mm)") or a word, within the trade's categories.
        /// </summary>
        private static MaterialModel? ByPrice(string trade, string name, double unitPrice, List<MaterialModel> mats)
        {
            if (unitPrice <= 0) return null;
            var tol = Math.Max(0.5, unitPrice * 0.005);
            var at = mats.Where(m => Math.Abs((double)m.MaterialPrice - unitPrice) <= tol).ToList();
            if (at.Count == 0) return null;

            var cats = TradeCategories.TryGetValue(trade, out var c) ? c : Array.Empty<string>();
            var size = Size.Match(name);
            var words = Words(name).ToHashSet();
            MaterialModel? best = null; int bestScore = 0;
            foreach (var m in at)
            {
                var mn = m.MaterialName ?? "";
                int score = 0;
                if (size.Success && Regex.IsMatch(mn, $@"\b{size.Groups[1].Value}\s*x\s*{size.Groups[2].Value}\b")) score += 3;
                score += 2 * Words(mn).Count(words.Contains);
                bool inTrade = cats.Any(k => (m.MaterialCategory ?? "").IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                if (inTrade) score += 1;
                if (name.IndexOf("softwood", StringComparison.OrdinalIgnoreCase) >= 0 && (m.MaterialCategory ?? "").IndexOf("softwood", StringComparison.OrdinalIgnoreCase) >= 0) score += 2;
                if (name.IndexOf("hardwood", StringComparison.OrdinalIgnoreCase) >= 0 && (m.MaterialCategory ?? "").IndexOf("hardwood", StringComparison.OrdinalIgnoreCase) >= 0) score += 2;
                if (score > bestScore) { best = m; bestScore = score; }
            }
            // a match on price alone only stands when it is the one row in the trade's own categories
            if (bestScore >= 2) return best;
            var inTradeOnly = at.Where(m => cats.Any(k => (m.MaterialCategory ?? "").IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            return inTradeOnly.Count == 1 ? inTradeOnly[0] : null;
        }

        /// <summary>
        /// A line that is another rate ("Mortar per square meter" at the mortar rate's
        /// cost): the rate whose unit cost it carries, or failing that, the rate sharing
        /// its main word whose cost divides the line's into the cleanest quantity
        /// ("Mortar 12mm thick" = 0.012 m3 of mortar).
        /// </summary>
        private static (Ref Ref, double Qty)? ByReference(string trade, string name, double unitPrice, double total, List<Ref> refs)
        {
            if (refs.Count == 0 || total <= 0) return null;
            // A line only reuses a rate when it says so: it names what it reuses (mortar,
            // concrete, formwork...) or points back ("as before calculated", "see blockwork").
            // An equal price on its own is a coincidence, not a reference.
            var words = Words(name).Where(w => w is "mortar" or "concrete" or "formwork" or "reinforcement" or "mixing" or "screed" or "render" or "blockwork" or "plaster" or "filling").ToList();
            bool pointsBack = PointsBack.IsMatch(name);
            if (words.Count == 0 && !pointsBack) return null;
            bool Names(Ref r) => pointsBack || words.Any(w => r.Description.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

            if (unitPrice > 0)
            {
                var tol = Math.Max(0.5, unitPrice * 0.002);
                var exact = refs.Where(r => Math.Abs(r.NetCost - unitPrice) <= tol && Names(r))
                                .OrderByDescending(r => r.Trade == trade).FirstOrDefault();
                if (exact != null) return (exact, total / exact.NetCost);
            }
            if (words.Count == 0) return null;
            (Ref Ref, double Qty)? pick = null; double bestErr = double.MaxValue;
            foreach (var r in refs.Where(r => words.Any(w => r.Description.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                var q = total / r.NetCost;
                if (q <= 0 || q > 100) continue;
                var err = Math.Abs(q * 1000 - Math.Round(q * 1000));
                if (err < bestErr) { bestErr = err; pick = (r, q); }
            }
            return bestErr < 0.05 ? pick : null;
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
        private static readonly Regex WasteAllowance = new(@"^add (for )?waste|^waste allowance|\bwaste\b.*%", RegexOptions.IgnoreCase);
        private static readonly Regex PointsBack = new(@"as before|\bsee\b", RegexOptions.IgnoreCase);

        // People on the gang: never matched to a material or another rate, whatever their price.
        private static readonly Regex People = new(
            @"\b(labour|labourer|operator|mason|masons|carpenter|painter|tiler|plumber|electrician|fitter|welder|fixer|foreman|headman|tradesman|ganger|operative|crew|gang|skilled|artisan|mate|torch|burner|compressor|machine|gear|sand pot|output)\b",
            RegexOptions.IgnoreCase);

        // Time-based units: a line paid by the hour or the day is labour or plant hire.
        private static readonly Regex TimeUnit = new(
            @"^\s*(\d+(\.\d+)?\s*)?(n/hr|no/hr|hrs?|hours?|hr/\S+|per\s*/?\s*(day|hr|hour))\b",
            RegexOptions.IgnoreCase);

        // A line costed for a day or an hour of work, which the rate divides by its output.
        // "45 lit/day", "per/day", "N/hr", "6 per Hr" - but not "0.08 hr/m2", which is time per unit.
        private static readonly Regex Batch = new(@"(^|/|\bper\s*/?\s*|\s)(day|days|hr|hrs|hour|hours)\s*\.?$", RegexOptions.IgnoreCase);

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

        private static CarbonRateItem? Assess(string trade, object item, List<MaterialModel> mats, List<LabourModel> labs, List<Ref> refs)
        {
            var self = Prop<string>(item, "Description") ?? "";
            var lines = Breakdown(item);
            if (lines == null) return null;

            double resourceCost = 0, coveredCost = 0, a13 = 0, a4 = 0, a5w = 0, a5a = 0;
            // the part of the build-up costed per day or per hour, which the rate divides by its output
            double batchCost = 0, b13 = 0, b4 = 0, b5w = 0, b5a = 0;
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
                bool batch = Batch.IsMatch(unit) || Regex.IsMatch(name, @"\bper\s*(day|hr|hour)\b", RegexOptions.IgnoreCase);
                if (batch) batchCost += total;

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

                // the gang and its plant: no material carbon, and never matched to a material or a
                // rate by price (a labourer at 962.50 is not the excavation rate that costs the same)
                bool person = kind == "labour" || Handling.IsMatch(name) || People.IsMatch(name) || TimeUnit.IsMatch(unit)
                              || labs.Any(l => string.Equals(l.LabourName, name, StringComparison.OrdinalIgnoreCase));
                if (person && !PointsBack.IsMatch(name) && CarbonEngine.Match("", name)?.Fuel != true)
                {
                    coveredCost += total;
                    bl.CarbonKg = 0;
                    bl.CarbonBasis = "Labour or plant hire: no material carbon. The plant's fuel is counted on its own line (A5a).";
                    breakdown.Add(bl);
                    continue;
                }

                CarbonEngine.LineCarbon? carbon = null;
                MaterialModel? mat = null;
                if (kind != "labour")
                {
                    if (!Handling.IsMatch(name)) mat = FindMaterial(name, unitPrice, mats) ?? ByPrice(trade, name, unitPrice, mats);
                    if (mat != null)
                    {
                        var price = (double)mat.MaterialPrice;
                        // the quantity in library units, exactly as the cost was built
                        var libQty = price > 0 && total > 0 ? total / price : qty;
                        carbon = CarbonEngine.Assess(mat.MaterialCategory, mat.MaterialName!, mat.MaterialUnit ?? "", libQty, name);
                        if (carbon != null) bl.RefName = mat.MaterialName!;
                        else mat = null;   // priced from a row with no factor: let the line's own wording try
                    }
                    carbon ??= CarbonEngine.Assess("", name, unit, qty, name);
                }

                // a line that is another rate: its carbon per unit, times the quantity this build-up uses
                if (carbon == null && kind != "labour" && !Handling.IsMatch(name) && !WasteAllowance.IsMatch(name))
                {
                    var r = ByReference(trade, name, unitPrice, total, refs.Where(x => x.Description != self).ToList());
                    if (r != null)
                    {
                        var (rf, q) = r.Value;
                        coveredCost += total;
                        var kg = rf.CarbonPerUnit * q;
                        a13 += kg;                 // carried as product carbon: the referenced rate's A1-A5 per unit
                        if (batch) b13 += kg;
                        bl.CarbonKg = kg;
                        bl.CarbonBasis = $"Uses {q:0.####} {rf.Unit} of the {rf.Trade} rate \"{rf.Description}\" at {rf.CarbonPerUnit:0.###} kgCO2e per {rf.Unit} (that rate's own upfront carbon, A1-A5).";
                        breakdown.Add(bl);
                        continue;
                    }
                }

                if (carbon != null)
                {
                    coveredCost += total;
                    a13 += carbon.A13; a4 += carbon.A4; a5w += carbon.A5w; a5a += carbon.A5a;
                    if (batch) { b13 += carbon.A13; b4 += carbon.A4; b5w += carbon.A5w; b5a += carbon.A5a; }
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
            // Lines given per unit of the rate (blocks per m2, mortar per m2) stand as they are;
            // only the day's or hour's lines are divided, by the output the price itself implies:
            // net = per-unit lines + day lines x (1 / output).
            var net = Prop<double>(item, "NetCost");
            var ratio = net > 0 ? net / resourceCost : 1;
            var unitCost = resourceCost - batchCost;
            double scale = 1.0, batchScale = 1.0;
            if (ratio < 0.95)
            {
                if (batchCost > 0 && net > unitCost && unitCost > 0)
                    batchScale = Math.Min(1.0, (net - unitCost) / batchCost);
                else
                    scale = ratio;                     // all of it is a batch (a mixer load, a day's output)
            }
            double Unit(double all, double b) => ((all - b) + b * batchScale) * scale;
            a13 = Unit(a13, b13); a4 = Unit(a4, b4); a5w = Unit(a5w, b5w); a5a = Unit(a5a, b5a);
            var perUnit = a13 + a4 + a5w + a5a;
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
                    Quantity = 1, IsTotalLine = true, CarbonKg = perUnit
                });
            else if (batchScale < 1 && b13 + b4 + b5w + b5a > 0)
                breakdown.Add(new CarbonRateBreakdownLine
                {
                    ComponentName = $"Per {unitName}: lines per {unitName} as they are, the day's lines divided by its output (x {batchScale:0.######})",
                    Quantity = 1, IsTotalLine = true, CarbonKg = perUnit
                });
            void Sum(string label, double v) =>
                breakdown.Add(new CarbonRateBreakdownLine { ComponentName = label, Quantity = 1, Unit = "kgCO2e", IsTotalLine = true, CarbonKg = v });
            Sum("Product, A1-A3", a13);
            Sum("Transport to site, A4", a4);
            Sum("Site waste, A5w", a5w);
            if (a5a > 0) Sum("Site plant fuel, A5a", a5a);
            Sum($"Upfront carbon, A1-A5, per {unitName}", perUnit);

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
                CarbonA13 = a13,
                CarbonA4 = a4,
                CarbonA5 = a5w + a5a,
                CarbonTotal = Math.Round(perUnit, 3),
                Coverage = coverage,
                HasAssumedMass = anyAssumed,
                BreakdownLines = breakdown,
                Source = "Carbon"
            };
        }
    }
}
