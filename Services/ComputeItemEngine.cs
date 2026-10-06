using System;
using System.Collections.Generic;
using ADLMRateGen.Services;

namespace ADLMRateGen.Services
{
    public sealed class ComputeItemEngine
    {
        private readonly Func<string, double> _getMaterialPriceByName;
        private readonly Func<string, double> _getLabourRateByName;

        public ComputeItemEngine(Func<string, double> getMaterialPriceByName,
                                 Func<string, double> getLabourRateByName)
        {
            _getMaterialPriceByName = getMaterialPriceByName ?? throw new ArgumentNullException(nameof(getMaterialPriceByName));
            _getLabourRateByName = getLabourRateByName ?? throw new ArgumentNullException(nameof(getLabourRateByName));
        }

        public sealed class LineResult
        {
            public string Kind { get; set; } = "";
            public string Name { get; set; } = "";
            public string Unit { get; set; } = "";
            public decimal Qty { get; set; }
            public decimal Factor { get; set; }
            public decimal UnitPrice { get; set; }
            public decimal Total { get; set; }
        }

        public sealed class Result
        {
            public decimal SubTotal { get; set; }
            public decimal PoPercent { get; set; }
            public decimal PoAmount { get; set; }
            public decimal NetCost { get; set; }
            public List<LineResult> Lines { get; } = new List<LineResult>();
            public List<string> Warnings { get; } = new List<string>();
        }

        public Result Compute(ComputeItemDefinition def)
        {
            var r = new Result();
            if (def == null) return r;

            decimal subtotal = 0m;

            foreach (var line in def.lines ?? new List<ComputeLine>())
            {
                var kind = (line.kind ?? "").Trim().ToLowerInvariant();
                // Price from the library row the line names (refName). The
                // description is display text ("PPR pipe 15mm incl. 5% waste")
                // and matches nothing, which priced those lines at zero.
                var name = (string.IsNullOrWhiteSpace(line.refName) ? line.description : line.refName)?.Trim() ?? "";
                var unit = (line.unit ?? "").Trim();

                var qty = line.qtyPerUnit;
                var factor = line.factor <= 0 ? 1m : line.factor;

                decimal unitPrice = 0m;

                if (kind == "constant")
                {
                    unitPrice = line.unitPriceAtBuild ?? 0m;
                }
                else if (kind == "material")
                {
                    unitPrice = (decimal)_getMaterialPriceByName(name);
                }
                else if (kind == "labour")
                {
                    unitPrice = (decimal)_getLabourRateByName(name);
                }
                else
                {
                    // unknown kind, treat as 0 but warn
                    r.Warnings.Add($"Unknown line kind '{line.kind}' for '{name}'.");
                    unitPrice = 0m;
                }

                if (unitPrice <= 0 && kind != "constant")
                    r.Warnings.Add($"No price found for {kind} '{name}'. Check name matches library.");

                var total = unitPrice * qty * factor;

                r.Lines.Add(new LineResult
                {
                    Kind = kind,
                    Name = name,
                    Unit = unit,
                    Qty = qty,
                    Factor = factor,
                    UnitPrice = unitPrice,
                    Total = total
                });

                subtotal += total;
            }

            r.SubTotal = subtotal;

            var poPct = def.poPercent < 0 ? 0 : def.poPercent;
            r.PoPercent = poPct;
            r.PoAmount = subtotal * (poPct / 100m);
            r.NetCost = subtotal + r.PoAmount;

            return r;
        }
    }
}
