using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OfficeOpenXml;
using OfficeOpenXml.Style;

namespace ADLMRateGen.Services.Bill
{
    /// <summary>One priced line to write into the client's bill.</summary>
    public sealed class BillPriceEntry
    {
        public string SheetName { get; init; } = "";
        public int Row { get; init; }
        /// <summary>Rate in the currency being written.</summary>
        public decimal Rate { get; init; }
        /// <summary>Quantity x rate in the currency being written.</summary>
        public decimal Amount { get; init; }
        /// <summary>Cell note: which RateGen rate priced the line.</summary>
        public string Note { get; init; } = "";
    }

    /// <summary>A line of the summary sheet: a sheet or section and its total.</summary>
    public sealed class BillSummaryLine
    {
        public string Label { get; init; } = "";
        public int Priced { get; init; }
        public int Items { get; init; }
        public decimal Total { get; init; }
    }

    /// <summary>
    /// Writes RateGen's rates into a COPY of the client's bill, in their layout: the
    /// rate into the bill's own Rate column (or a new one beside it when the bill has
    /// none), the amount into the Amount column unless the client's own formula works
    /// it out, a note on each rate saying which RateGen rate it is, and a summary sheet.
    /// The client's file is never written to.
    /// </summary>
    public static class BillPriceWriter
    {
        public const string NoteAuthor = "RateGen";
        public const string SummarySheet = "RateGen summary";

        public sealed class Result
        {
            public string Path { get; set; } = "";
            public int Written { get; set; }
            public int AddedColumns { get; set; }
            public List<string> Skipped { get; } = new();
            public int CalcErrors { get; set; }
        }

        /// <summary>"Bill.xlsx" -> "Bill (RateGen priced).xlsx" beside it.</summary>
        public static string DefaultTargetPath(string sourcePath)
        {
            var dir = Path.GetDirectoryName(sourcePath) ?? "";
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(sourcePath) + " (RateGen priced).xlsx");
        }

        public static Result Write(string sourcePath, string targetPath,
            IReadOnlyDictionary<string, ClientBillColumns> columnsBySheet,
            IEnumerable<BillPriceEntry> entries,
            IEnumerable<BillSummaryLine> summary, decimal grandTotal, string currencyCode, string billName)
        {
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Save the priced bill as a new file. The client's original is never overwritten.");

            // Read with shared access (the bill is often open in Excel) and build the copy in
            // memory, so a failed write leaves no half-written file.
            byte[] original;
            using (var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                original = ms.ToArray();
            }

            var result = new Result { Path = targetPath };
            using var input = new MemoryStream(original);
            using var pkg = new ExcelPackage(input);

            foreach (var sheetGroup in entries.GroupBy(e => e.SheetName))
            {
                var ws = pkg.Workbook.Worksheets[sheetGroup.Key];
                if (ws == null || !columnsBySheet.TryGetValue(sheetGroup.Key, out var cols))
                {
                    result.Skipped.Add(sheetGroup.Key + ": sheet not found");
                    continue;
                }
                var (rateCol, amountCol, added) = Columns(ws, cols, sheetGroup.Min(e => e.Row));
                result.AddedColumns += added;

                foreach (var e in sheetGroup)
                {
                    var rateCell = ws.Cells[e.Row, rateCol];
                    if (!string.IsNullOrEmpty(rateCell.Formula))
                    {
                        // A rate the client links to another sheet is theirs to change.
                        result.Skipped.Add($"{ws.Name}!{rateCell.Address}: the rate cell holds a formula");
                        continue;
                    }
                    rateCell.Value = (double)e.Rate;
                    rateCell.Style.Numberformat.Format = "#,##0.00";
                    var amountCell = ws.Cells[e.Row, amountCol];
                    // The client's "=C7*E7" works the amount out from the rate just written.
                    if (string.IsNullOrEmpty(amountCell.Formula))
                    {
                        amountCell.Value = (double)e.Amount;
                        amountCell.Style.Numberformat.Format = "#,##0.00";
                    }
                    if (!string.IsNullOrWhiteSpace(e.Note))
                    {
                        // A client's own comment on the cell is theirs: never replaced.
                        if (rateCell.Comment == null) ws.Comments.Add(rateCell, e.Note, NoteAuthor);
                        else if (rateCell.Comment.Author == NoteAuthor) rateCell.Comment.Text = e.Note;
                    }
                    result.Written++;
                }
            }

            AddSummary(pkg, summary, grandTotal, currencyCode, billName);

            // Every collection and carried-to total is the client's formula over the amounts.
            // Excel recomputes on open; cached values are refreshed here too, because a bill is
            // often first opened in a phone or WhatsApp preview that shows cached values.
            pkg.Workbook.FullCalcOnLoad = true;
            var before = CachedFormulaValues(pkg);
            try
            {
                pkg.Workbook.Calculate();
                result.CalcErrors = RestoreFailedCalculations(pkg, before);
            }
            catch
            {
                // Excel's own recalculation on open still covers it.
            }

            using var outStream = new MemoryStream();
            pkg.SaveAs(outStream);
            File.WriteAllBytes(targetPath, outStream.ToArray());
            return result;
        }

        /// <summary>
        /// The bill's own Rate and Amount columns, or new ones after its last column when it
        /// has none. A bill with only an "Amount" header has RateCol == AmountCol: that column
        /// is the amount, and the rate goes in a new column.
        /// </summary>
        private static (int Rate, int Amount, int Added) Columns(ExcelWorksheet ws, ClientBillColumns cols, int firstRow)
        {
            int rate = cols.RateCol > 0 && cols.RateCol != cols.AmountCol ? cols.RateCol : 0;
            int amount = cols.AmountCol;
            int next = (ws.Dimension?.End.Column ?? 0) + 1;
            int added = 0;
            int headerRow = cols.HeaderRow > 0 ? cols.HeaderRow : firstRow - 1;
            if (rate == 0) { rate = next++; added++; Header(ws, headerRow, rate, "Rate"); }
            if (amount == 0) { amount = next++; added++; Header(ws, headerRow, amount, "Amount"); }
            return (rate, amount, added);
        }

        private static void Header(ExcelWorksheet ws, int row, int col, string text)
        {
            if (row < 1) return;
            var c = ws.Cells[row, col];
            if (c.Value != null) return;
            c.Value = text;
            c.Style.Font.Bold = true;
            ws.Column(col).Width = Math.Max(ws.Column(col).Width, 14);
        }

        private static void AddSummary(ExcelPackage pkg, IEnumerable<BillSummaryLine> lines, decimal grandTotal, string currency, string billName)
        {
            var name = SummarySheet;
            for (int i = 2; pkg.Workbook.Worksheets[name] != null; i++) name = SummarySheet + " " + i;
            var ws = pkg.Workbook.Worksheets.Add(name);
            ws.Cells[1, 1].Value = "Priced with ADLM RateGen";
            ws.Cells[1, 1].Style.Font.Bold = true;
            ws.Cells[1, 1].Style.Font.Size = 14;
            ws.Cells[2, 1].Value = billName;
            ws.Cells[3, 1].Value = $"{DateTime.Now:d MMMM yyyy, HH:mm}  ·  amounts in {currency}";
            ws.Cells[4, 1].Value = "Rates are RateGen's all-in rates (net cost, overhead and profit). Check every line before you tender.";
            ws.Cells[4, 1].Style.Font.Italic = true;

            int r = 6;
            string[] heads = { "Section", "Items priced", "Items", "Total" };
            for (int c = 0; c < heads.Length; c++)
            {
                ws.Cells[r, c + 1].Value = heads[c];
                ws.Cells[r, c + 1].Style.Font.Bold = true;
                ws.Cells[r, c + 1].Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
            }
            foreach (var l in lines)
            {
                r++;
                ws.Cells[r, 1].Value = l.Label;
                ws.Cells[r, 2].Value = l.Priced;
                ws.Cells[r, 3].Value = l.Items;
                ws.Cells[r, 4].Value = (double)l.Total;
                ws.Cells[r, 4].Style.Numberformat.Format = "#,##0.00";
            }
            r++;
            ws.Cells[r, 1].Value = "Total";
            ws.Cells[r, 4].Value = (double)grandTotal;
            ws.Cells[r, 1, r, 4].Style.Font.Bold = true;
            ws.Cells[r, 1, r, 4].Style.Border.Top.Style = ExcelBorderStyle.Thin;
            ws.Cells[r, 4].Style.Numberformat.Format = "#,##0.00";
            ws.Column(1).Width = 48;
            ws.Column(2).Width = 14;
            ws.Column(3).Width = 10;
            ws.Column(4).Width = 20;
        }

        private static Dictionary<string, object?> CachedFormulaValues(ExcelPackage pkg)
        {
            var values = new Dictionary<string, object?>();
            foreach (var ws in pkg.Workbook.Worksheets)
            {
                if (ws.Dimension == null) continue;
                foreach (var c in ws.Cells[ws.Dimension.Address])
                    if (!string.IsNullOrEmpty(c.Formula)) values[ws.Name + "!" + c.Address] = c.Value;
            }
            return values;
        }

        /// <summary>Puts back the cached value of every formula cell EPPlus turned into a new error.</summary>
        private static int RestoreFailedCalculations(ExcelPackage pkg, Dictionary<string, object?> before)
        {
            var n = 0;
            foreach (var ws in pkg.Workbook.Worksheets)
            {
                if (ws.Dimension == null) continue;
                foreach (var c in ws.Cells[ws.Dimension.Address])
                {
                    if (string.IsNullOrEmpty(c.Formula) || c.Value is not ExcelErrorValue) continue;
                    if (before.TryGetValue(ws.Name + "!" + c.Address, out var old) && old is not ExcelErrorValue)
                    {
                        c.Value = old;
                        n++;
                    }
                }
            }
            return n;
        }
    }
}
