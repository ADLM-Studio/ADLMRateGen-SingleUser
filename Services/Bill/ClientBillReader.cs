// Ported from HERON (ADLMPlanswiftApp, feat/heron-3 5d3c093) on 3 Oct 2026, which took it
// from QUIV (RevitPluginArch, feat/client-bill-fill). Same reader QUIV and HERON use, so a fix
// to how firms write bills belongs in all three.
#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OfficeOpenXml;

namespace ADLMRateGen.Services.Bill
{
    /// <summary>
    /// Reads a client's bill of quantities in THEIR layout — any column order, any number of
    /// sheets, headings and dittos as a QS writes them — into rows RateGen can price. Nothing
    /// here decides a quantity; it only finds the cells and the context each item is read in.
    ///
    /// A bill description is a fragment. "Beams" means nothing on its own; under "Reinforced
    /// in-situ concrete (1:2:4)" it is a concrete item. So every item carries its section,
    /// the heading lines above it, and — for "Ditto"/lower-case continuations — the item it
    /// continues. The AI reads that context; it is never substituted into the text.
    ///
    /// Audited against ~730 live client workbooks from about 40 Nigerian QS firms (Sep 2026):
    /// every rule below that looks oddly specific is there because a real bill needed it, and
    /// the comment says which shape. The overriding rule: when the sheet cannot be read with
    /// confidence, say so — a wrong cell written into a client's bill is the one unacceptable
    /// outcome.
    /// </summary>
    public static class ClientBillReader
    {
        private const int HeaderScanRows = 200;
        private const int HeaderScanCols = 24;
        private const int MaxHeadings = 4;
        private const int MaxDittoHops = 24;
        private const int MaxStages = 4;
        private const int MaxItemRows = 4;
        private const int MaxTailRows = 2;
        private const int MaxHeadingLength = 400;
        private const int AmountScanRows = 40;

        public const string ProblemNoColumns = "No Description / Unit / Qty columns found";
        public const string ProblemPriceList = "Price list — no quantity column";
        public const string ProblemNotABill = "Not a bill (no quantity/unit pattern)";
        public const string ProblemTakeoff = "The firm's own take-off sheet — skipped";

        public static ClientBillWorkbook Read(string path)
        {

            var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            // Callers convert an .xls first (LegacyWorkbookConverter); EPPlus reads .xlsx only.
            if (ext == ".xls")
                throw new NotSupportedException(
                    "This is an old-format .xls workbook. Open it in Excel, save it as .xlsx, and import that copy.");

            // The client's bill is very often still open in Excel; share the read.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var pkg = new ExcelPackage(fs))
            {
                var book = new ClientBillWorkbook { Path = path };
                var sheets = new Dictionary<ClientBillSheet, ExcelWorksheet>();
                var index = 0;
                foreach (var ws in pkg.Workbook.Worksheets)
                {
                    index++;
                    var sheet = new ClientBillSheet { Index = index, Name = ws.Name };
                    book.Sheets.Add(sheet);
                    sheets[sheet] = ws;

                    if (ws.Hidden != eWorkSheetHidden.Visible) { sheet.Problem = "Hidden sheet"; continue; }
                    if (ws.Dimension == null) { sheet.Problem = "Empty sheet"; continue; }

                    // The firm's own take-off/dimension sheet is the measurement QUIV
                    // replaces. Reading it would offer the QS's own workings as fill targets.
                    if (IsTakeoffSheetName(ws.Name))
                    {
                        sheet.IsTakeoffSheet = true;
                        sheet.Problem = ProblemTakeoff;
                        continue;
                    }

                    ReadSheetInto(ws, sheet);
                }

                // …unless the workbook is nothing but a take-off, which a few are.
                if (!book.Sheets.Any(s => s.Rows.Any(r => r.Kind == ClientBillRowKind.Item)))
                    foreach (var sheet in book.Sheets.Where(s => s.IsTakeoffSheet))
                    {
                        sheet.Problem = null;
                        ReadSheetInto(sheets[sheet], sheet);
                        if (sheet.Problem == null) sheet.Note = "The firm's own take-off sheet — read because this workbook has no bill sheet.";
                    }

                return book;
            }
        }

        /// <summary>Re-reads one sheet with columns the user corrected.</summary>
        public static ClientBillSheet ReadSheet(string path, string sheetName, ClientBillColumns columns)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var pkg = new ExcelPackage(fs))
            {
                var ws = pkg.Workbook.Worksheets[sheetName];
                if (ws == null) throw new InvalidOperationException("Sheet '" + sheetName + "' is no longer in the workbook.");
                var sheet = new ClientBillSheet { Index = ws.Index + 1, Name = ws.Name, Columns = columns };
                if (ws.Dimension == null) { sheet.Problem = "Empty sheet"; return sheet; }
                if (!columns.IsUsable) { sheet.Problem = "Description, Unit and Qty columns are all needed"; return sheet; }
                ReadRows(ws, sheet);
                return sheet;
            }
        }

        private static void ReadSheetInto(ExcelWorksheet ws, ClientBillSheet sheet)
        {
            string problem;
            sheet.Columns = DetectColumns(ws, out problem);
            if (sheet.Columns == null || !sheet.Columns.IsUsable)
            {
                sheet.Problem = problem ?? ProblemNoColumns;
                return;
            }
            ReadRows(ws, sheet);
        }

        // ---------------------------------------------------------------- columns

        internal static ClientBillColumns DetectColumns(ExcelWorksheet ws)
        {
            string problem;
            return DetectColumns(ws, out problem);
        }

        internal static ClientBillColumns DetectColumns(ExcelWorksheet ws, out string problem)
        {
            problem = null;
            var start = ws.Dimension.Start.Row;
            var lastRow = Math.Min(ws.Dimension.End.Row, start + HeaderScanRows);
            var lastCol = Math.Min(ws.Dimension.End.Column, HeaderScanCols);
            var sawRateSchedule = false;

            // The header is not always near the top: cover pages, notes and a whole first bill
            // page sit above it in real workbooks.
            for (var r = start; r <= lastRow; r++)
            {
                var labels = LabelsIn(ws, r, lastCol);
                // Two-tier headers ("QTY" over "Add", "ITEM" over "DESCRIPTION | QTY | UNIT")
                // only make sense read together.
                var merged = Merge(labels, LabelsIn(ws, r + 1, lastCol));
                var headerRow = r;
                if (merged.Hits > labels.Hits) { labels = merged; headerRow = r + 1; }
                if (labels.Desc == 0 || labels.Unit == 0) continue;

                var cols = BuildFromHeader(ws, headerRow, labels, lastCol);
                if (cols != null && HasUnitsBelow(ws, headerRow, cols.UnitCol)) return cols;
                if (cols == null && labels.Rate > 0) sawRateSchedule = true;
            }

            if (sawRateSchedule)
            {
                // Description + Unit + Rate and no quantity anywhere: a price list, not a bill.
                problem = ProblemPriceList;
                return null;
            }

            var inferred = InferColumns(ws);
            if (inferred == null) { problem = ProblemNoColumns; return null; }
            if (!LooksLikeABill(ws, inferred)) { problem = ProblemNotABill; return null; }
            return inferred;
        }

        private static ClientBillColumns BuildFromHeader(ExcelWorksheet ws, int headerRow, LabelRow labels, int lastCol)
        {
            var desc = labels.Desc;
            // "Description | DESCRIPTION" side by side with the text only in the second is a
            // real layout: take the labelled column that holds text.
            foreach (var c in labels.DescAlso)
                if (c != labels.Unit && c != labels.Qty && TextBelow(ws, headerRow, c) > TextBelow(ws, headerRow, desc)) desc = c;

            var qty = labels.Qty;
            var others = new List<int>(labels.MoreQty);

            // A bill measured per building or per phase puts one quantity column per block to
            // the right of the unit ("Unit | Block A | Block B | Gatehouse"). Rate and Amount
            // are excluded by their labels and by being operands of the amount formula.
            if (qty == 0)
            {
                var parallel = ParallelQtyCols(ws, headerRow, labels, lastCol);
                if (parallel.Count >= 2) { qty = parallel[0]; others = parallel.Skip(1).ToList(); }
            }
            // An unlabelled column of numbers between the description and the unit is the
            // quantity column, however the firm labels the rest.
            if (qty == 0) qty = NumericColumnBetween(ws, headerRow, desc, labels.Unit);
            // An unpriced bill has neither: the amount formula still says which column is the
            // quantity ("=C7*E7" → C is the quantity, E the rate).
            var fromFormula = false;
            if (qty == 0)
            {
                qty = QtyFromAmountFormula(ws, headerRow, desc, lastCol);
                fromFormula = qty > 0;
            }
            // A label may not override the amount formula: a bill measured per building keeps
            // its quantity in a "Total" column (= the sum of the building columns) and prices
            // it "=Total*Rate". Otherwise a rate/price/amount label is never the quantity.
            if (qty == 0 || (!fromFormula && IsRateLabel(HeaderLabel(ws.Cells[headerRow, qty].Value)))) return null;

            return new ClientBillColumns
            {
                HeaderRow = headerRow,
                ItemCol = labels.Item,
                DescriptionCol = desc,
                UnitCol = labels.Unit,
                QtyCol = qty,
                RateCol = labels.Rate,
                AmountCol = labels.Amount,
                RowSectionCol = labels.Section > 0 && labels.Section != desc ? labels.Section : 0,
                FromHeader = true,
                QtyLabel = RawLabel(ws.Cells[headerRow, qty].Value),
                OtherQtyCols = others,
                OtherQtyLabels = others.Select(c => RawLabel(ws.Cells[headerRow, c].Value)).ToList(),
            };
        }

        // Continuation sheets often repeat no header at all. The unit column is the one full
        // of unit words; the description is the wordiest column left of it; the quantity is a
        // numeric column between them, else the amount formula's own operand, else the column
        // right of the unit.
        private static ClientBillColumns InferColumns(ExcelWorksheet ws)
        {
            var lastRow = ws.Dimension.End.Row;
            var lastCol = Math.Min(ws.Dimension.End.Column, HeaderScanCols);
            var unitHits = new int[lastCol + 2];
            var textLen = new long[lastCol + 2];
            var numbers = new int[lastCol + 2];

            for (var r = ws.Dimension.Start.Row; r <= lastRow; r++)
                for (var c = 1; c <= lastCol; c++)
                {
                    var v = ws.Cells[r, c].Value;
                    if (v == null) continue;
                    // A date column must never win the "wordiest column" vote.
                    if (IsNumber(v) || v is DateTime || v is TimeSpan) { numbers[c]++; continue; }
                    var s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
                    if (ClientBillUnits.IsKnown(s) || ClientBillUnits.IsDitto(s)) unitHits[c]++;
                    else textLen[c] += s.Length;
                }

            var unitCol = 0;
            for (var c = 1; c <= lastCol; c++)
                if (unitHits[c] >= 3 && (unitCol == 0 || unitHits[c] > unitHits[unitCol])) unitCol = c;
            if (unitCol == 0) return null;

            var descCol = 0;
            for (var c = 1; c < unitCol; c++)
                if (descCol == 0 || textLen[c] > textLen[descCol]) descCol = c;
            if (descCol == 0 || textLen[descCol] == 0) return null;

            var between = new List<int>();
            for (var c = descCol + 1; c < unitCol; c++)
                if (numbers[c] >= 3) between.Add(c);

            var qtyCol = between.Count > 0 ? between[0] : QtyFromAmountFormula(ws, ws.Dimension.Start.Row - 1, descCol, lastCol);
            if (qtyCol == 0) qtyCol = unitCol + 1 <= lastCol + 1 ? unitCol + 1 : 0;

            var itemCol = descCol > 1 ? descCol - 1 : 0; // the column just left of the description

            return new ClientBillColumns
            {
                HeaderRow = 0, ItemCol = itemCol, DescriptionCol = descCol, UnitCol = unitCol, QtyCol = qtyCol, FromHeader = false,
                OtherQtyCols = between.Skip(1).ToList(),
                OtherQtyLabels = between.Skip(1).Select(c => ColumnLetters(c)).ToList(),
            };
        }

        /// <summary>
        /// Guards the inferred columns against tables that are not bills at all (schedules,
        /// cash-flow forecasts, price lists, programme sheets): on rows that carry a real
        /// unit, the quantity column must be numeric/blank and the unit column must really
        /// hold units.
        /// </summary>
        private static bool LooksLikeABill(ExcelWorksheet ws, ClientBillColumns cols)
        {
            int unitRows = 0, qtyOk = 0, unitCells = 0, unitKnown = 0;
            var last = ws.Dimension.End.Row;
            for (var r = Math.Max(cols.HeaderRow + 1, ws.Dimension.Start.Row); r <= last; r++)
            {
                var u = Text(ws.Cells[r, cols.UnitCol].Value);
                if (u.Length > 0)
                {
                    unitCells++;
                    if (ClientBillUnits.IsKnown(u)) unitKnown++;
                }
                if (!ClientBillUnits.IsMeasured(u)) continue;
                unitRows++;
                var q = ws.Cells[r, cols.QtyCol];
                if (q.Value == null || IsNumber(q.Value) || !string.IsNullOrEmpty(q.Formula)) qtyOk++;
            }
            if (unitRows < 3) return false;
            if (qtyOk * 10 < unitRows * 6) return false;
            return unitCells == 0 || unitKnown * 10 >= unitCells * 6;
        }

        private static List<int> ParallelQtyCols(ExcelWorksheet ws, int headerRow, LabelRow labels, int lastCol)
        {
            var found = new List<int>();
            var amountOperands = AmountOperands(ws, headerRow, lastCol);
            for (var c = labels.Unit + 1; c <= lastCol; c++)
            {
                var label = HeaderLabel(ws.Cells[headerRow, c].Value);
                if (label.Length == 0 || IsRateLabel(label)) continue;
                if (amountOperands.Contains(c)) continue;
                if (NumericCellsBelow(ws, headerRow, c) >= 3) found.Add(c);
            }
            return found;
        }

        private static int NumericColumnBetween(ExcelWorksheet ws, int headerRow, int descCol, int unitCol)
        {
            for (var c = descCol + 1; c < unitCol; c++)
                if (NumericCellsBelow(ws, headerRow, c) >= 3) return c;
            return 0;
        }

        // "=C7*E7", "=+C7*E7", "=ROUND(C7*E7,2)": two single-cell operands on the row itself.
        private static readonly Regex AmountFormula = new Regex(
            @"^\+?\(?\s*(?:ROUND\w*\(\s*)?\$?([A-Za-z]{1,3})\$?(\d+)\s*\*\s*\$?([A-Za-z]{1,3})\$?(\d+)\s*(?:,\s*-?\d+\s*)?\)?\)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The quantity column of an UNPRICED bill, read from its own amount formulas: the
        /// operand nearer the description is the quantity, the other is the rate. Without
        /// this, a bill sent out with both columns blank has its RATE column filled.
        /// </summary>
        private static int QtyFromAmountFormula(ExcelWorksheet ws, int headerRow, int descCol, int lastCol)
        {
            var votes = new Dictionary<string, int>();
            var first = Math.Max(headerRow + 1, ws.Dimension.Start.Row);
            var last = Math.Min(ws.Dimension.End.Row, first + AmountScanRows);
            for (var r = first; r <= last; r++)
                for (var c = 1; c <= lastCol; c++)
                {
                    int a, b;
                    if (!TryAmountOperands(ws.Cells[r, c].Formula, r, out a, out b)) continue;
                    var key = Math.Min(a, b) + ":" + Math.Max(a, b);
                    int n;
                    votes[key] = votes.TryGetValue(key, out n) ? n + 1 : 1;
                }
            if (votes.Count == 0) return 0;
            var best = votes.OrderByDescending(kv => kv.Value).First();
            if (best.Value < 2) return 0;
            var parts = best.Key.Split(':');
            var left = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var right = int.Parse(parts[1], CultureInfo.InvariantCulture);
            return Math.Abs(left - descCol) <= Math.Abs(right - descCol) ? left : right;
        }

        private static HashSet<int> AmountOperands(ExcelWorksheet ws, int headerRow, int lastCol)
        {
            var set = new HashSet<int>();
            var first = Math.Max(headerRow + 1, ws.Dimension.Start.Row);
            var last = Math.Min(ws.Dimension.End.Row, first + AmountScanRows);
            for (var r = first; r <= last; r++)
                for (var c = 1; c <= lastCol; c++)
                {
                    int a, b;
                    if (!TryAmountOperands(ws.Cells[r, c].Formula, r, out a, out b)) continue;
                    set.Add(a);
                    set.Add(b);
                    set.Add(c);
                }
            return set;
        }

        private static bool TryAmountOperands(string formula, int row, out int colA, out int colB)
        {
            colA = colB = 0;
            if (string.IsNullOrEmpty(formula)) return false;
            var m = AmountFormula.Match(formula.Trim());
            if (!m.Success) return false;
            if (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) != row) return false;
            if (int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) != row) return false;
            colA = ColumnNumber(m.Groups[1].Value);
            colB = ColumnNumber(m.Groups[3].Value);
            return colA > 0 && colB > 0 && colA != colB;
        }

        private static int NumericCellsBelow(ExcelWorksheet ws, int headerRow, int col)
        {
            var n = 0;
            var last = Math.Min(ws.Dimension.End.Row, headerRow + 200);
            for (var r = headerRow + 1; r <= last; r++)
            {
                var cell = ws.Cells[r, col];
                if (cell.Value != null && IsNumber(cell.Value)) n++;
                else if (!string.IsNullOrEmpty(cell.Formula)) n++;
            }
            return n;
        }

        private static bool HasUnitsBelow(ExcelWorksheet ws, int headerRow, int unitCol)
        {
            var n = 0;
            var last = Math.Min(ws.Dimension.End.Row, headerRow + 400);
            for (var r = headerRow + 1; r <= last && n < 3; r++)
            {
                var u = Text(ws.Cells[r, unitCol].Value);
                // A ditto mark is a unit: it repeats the one above.
                if (ClientBillUnits.IsKnown(u) || ClientBillUnits.IsDitto(u)) n++;
            }
            return n >= 3;
        }

        private static long TextBelow(ExcelWorksheet ws, int headerRow, int col)
        {
            long n = 0;
            var last = Math.Min(ws.Dimension.End.Row, headerRow + 200);
            for (var r = headerRow + 1; r <= last; r++)
            {
                var v = ws.Cells[r, col].Value;
                if (v != null && !IsNumber(v)) n += Convert.ToString(v, CultureInfo.InvariantCulture).Trim().Length;
            }
            return n;
        }

        // ---- header labels ---------------------------------------------------------

        private sealed class LabelRow
        {
            public int Item, Desc, Unit, Qty, Rate, Section, Amount;
            public List<int> MoreQty = new List<int>();
            public List<int> DescAlso = new List<int>();
            public int Hits
            {
                get
                {
                    return (Item > 0 ? 1 : 0) + (Desc > 0 ? 1 : 0) + (Unit > 0 ? 1 : 0) + (Qty > 0 ? 1 : 0)
                         + (Rate > 0 ? 1 : 0) + (Section > 0 ? 1 : 0) + MoreQty.Count;
                }
            }
        }

        private static LabelRow LabelsIn(ExcelWorksheet ws, int row, int lastCol)
        {
            var l = new LabelRow();
            if (row < 1 || row > ws.Dimension.End.Row) return l;
            // Left to right, first match wins: a schedule headed "Bill Qty | Bill Unit |
            // Material | Qty | Unit" is filled in its BILL columns, not the material ones.
            for (var c = 1; c <= lastCol; c++)
            {
                var label = HeaderLabel(ws.Cells[row, c].Value);
                if (label.Length == 0) continue;
                // The Amount column is noted on its own (RateGen writes the priced amount there);
                // the chain below still reads it as RateCol when the bill has no Rate header.
                if (l.Amount == 0 && IsAmountLabel(label)) l.Amount = c;
                if (IsDescriptionLabel(label)) { if (l.Desc == 0) l.Desc = c; else l.DescAlso.Add(c); }
                else if (l.Unit == 0 && IsUnitLabel(label)) l.Unit = c;
                // The first Qty label wins wherever it sits ("UNIT | QTY" is as common as
                // "QTY | UNIT"); further Qty labels BEFORE the unit are parallel columns
                // ("Qty Phase 1 | Qty Phase 2 | Unit").
                else if (IsQtyLabel(label)) { if (l.Qty == 0) l.Qty = c; else if (l.Unit == 0) l.MoreQty.Add(c); }
                else if (l.Rate == 0 && IsRateLabel(label)) l.Rate = c;
                else if (l.Item == 0 && IsItemLabel(label)) l.Item = c;
                else if (l.Section == 0 && IsSectionLabel(label)) l.Section = c;
            }
            return l;
        }

        private static LabelRow Merge(LabelRow a, LabelRow b)
        {
            var m = new LabelRow
            {
                Item = a.Item > 0 ? a.Item : b.Item,
                Desc = a.Desc > 0 ? a.Desc : b.Desc,
                Unit = a.Unit > 0 ? a.Unit : b.Unit,
                Qty = a.Qty > 0 ? a.Qty : b.Qty,
                Rate = a.Rate > 0 ? a.Rate : b.Rate,
                Section = a.Section > 0 ? a.Section : b.Section,
                Amount = a.Amount > 0 ? a.Amount : b.Amount,
            };
            m.MoreQty.AddRange(a.MoreQty);
            m.MoreQty.AddRange(b.MoreQty.Where(c => c != m.Qty && !m.MoreQty.Contains(c)));
            m.DescAlso.AddRange(a.DescAlso);
            m.DescAlso.AddRange(b.DescAlso.Where(c => c != m.Desc && !m.DescAlso.Contains(c)));
            return m;
        }

        private static string RawLabel(object v)
        {
            return v == null ? "" : Clip(Regex.Replace(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "", @"\s+", " ").Trim(), 40);
        }

        private static string HeaderLabel(object v)
        {
            if (v == null || IsNumber(v)) return "";
            var s = Convert.ToString(v, CultureInfo.InvariantCulture).ToLowerInvariant();
            s = Regex.Replace(s, @"\(.*?\)", " ");          // "Amount (₦)", "Qty (m2)"
            s = Regex.Replace(s, @"[^a-z/ ]", " ");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }
        private static bool IsDescriptionLabel(string l)
        {
            // "DSCRIPTIONS" is a real header in a live bill; match the stem, not the spelling.
            return l.Contains("scription") || l.StartsWith("particular") || l == "details"
                || l == "item description" || l.StartsWith("work description");
        }
        private static bool IsUnitLabel(string l)
        {
            // "unit of measure", "unit/scale" pass; "unit rate" and "unit price" do not.
            if (l == "bill unit") return true;
            if (!l.StartsWith("unit") && !l.StartsWith("uom")) return false;
            return !Regex.IsMatch(l, @"\b(rate|price|cost|amount|value)\b");
        }
        private static bool IsQtyLabel(string l)
        {
            // "Qyt" and "Qtty" are real headers in live bills.
            return l == "qty" || l == "qnty" || l == "qty s" || l.StartsWith("quantit") || l == "quant"
                || l == "qyt" || l == "qtty" || l == "qnt" || l == "qtys" || l == "q ty" || l == "qtty s"
                || l == "bill qty" || l.StartsWith("bill quantit");
        }
        private static bool IsItemLabel(string l)
        {
            return l == "item" || l == "items" || l == "ref" || l == "item no" || l == "item ref" || l == "s/n"
                || l == "sn" || l == "s/no" || l == "no" || l == "code" || l == "item code" || l == "serial";
        }
        internal static bool IsRateLabel(string l)
        {
            return l.Length > 0 && Regex.IsMatch(l, @"^(unit\s+)?(rate|price|cost|amount|total|value)\b|\b(rate|price|amount)$");
        }
        internal static bool IsAmountLabel(string l)
        {
            return l.Length > 0 && Regex.IsMatch(l, @"^(amount|total amount|total|value|extension)\b|\bamount$");
        }
        private static bool IsSectionLabel(string l)
        {
            return l == "section" || l == "element" || l == "location" || l == "room" || l == "level" || l == "floor"
                || l == "zone" || l == "block" || l == "area" || l == "building" || l == "trade" || l == "part";
        }

        // ------------------------------------------------------------------- rows

        // Collections, carried-to lines, page totals. Real spellings seen: "To Collecton",
        // "caried to summary", "SUBSTRUCTURE TO SUMMARY", "PRELIMS - TO GENERAL SUMMARY".
        // Anchored: a measured row whose description merely mentions a total is NOT furniture.
        private static readonly Regex Bookkeeping = new Regex(
            @"^\s*(to\s+)?(collect\w*|summary)\b|car+ied\s+(to|forward)\b|brought\s+forward\b"
            + @"|\bto\s+(general\s+)?(summary|collect\w*)\s*$|^\s*[bc]\s*/\s*f\b|page\s+total"
            + @"|^\s*page\s*(no|nr|number)?\.?\s*[:.]?\s*\S{0,12}\s*$|^\s*(sub[\s-]?)?total\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Tax, retention and contingency lines are priced off the bill's own total.
        private static readonly Regex TaxLine = new Regex(
            @"\bv\.?\s*a\.?\s*t\b|\bvalue\s+added\s+tax\b|\bw\.?\s*h\.?\s*t\b|\bwithholding\b|\bcontingenc|\bretention\b|^\s*add\b.*\d\s*%",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A trade title says WHAT; a stage title says WHERE — the floor, milestone, block or
        // element the items below it sit in. A floor-by-floor bill repeats "Suspended slab"
        // under every "GROUND FLOOR TO FIRST FLOOR SLAB", so the stage has to stay in the
        // item's context across the trade titles beneath it.
        private static readonly Regex FloorStage = new Regex(
            @"\b(ground|first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|\d+(st|nd|rd|th)|upper|lower|top|typical|roof|mezzanine|penthouse|pent|attic|podium|terrace)\s+(floor|level|slab)\b"
            + @"|\b(floor|level)\s+\d|\bstor(e)?y\b|\bbasement\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BillStage = new Regex(@"\bbill\s*n(o|r)?\.?\s*\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // A milestone IS an element of the works ("MILESTONE 2" supersedes "SUBSTRUCTURE",
        // which was milestone 1), so both replace each other rather than nesting.
        private static readonly Regex ElementStage = new Regex(
            @"\belement\s*(n[or]\.?|#)?\s*\d|\bsub-?structure\b|\bsuper-?structure\b|\bmilestone\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BlockStage = new Regex(@"\bblock\s+[a-z0-9]\d{0,2}\b|\bphase\s*\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Which kind of stage a title is, or null when it is a trade title.</summary>
        private static string StageKind(string s)
        {
            if (BillStage.IsMatch(s)) return "bill";
            if (ElementStage.IsMatch(s)) return "element";
            if (BlockStage.IsMatch(s)) return "block";
            if (FloorStage.IsMatch(s)) return "floor";
            return null;
        }

        // Preamble clauses above the first item — "( iv ) The Contractor is strongly advised
        // to visit the site…", "Information" — describe the contract, not the work.
        private static readonly Regex Preamble = new Regex(
            @"^\s*\(\s*[ivxlc]+\s*\)|^\s*(notes?|information|preambles?|general\s+notes?|drawings?)\s*:?\s*$"
            + @"|\b(contractor|tenderer|employer)\b|\bnature\s+and\s+location\s+of\s+the\s+work\b|\bthe\s+work\s+in\s+this\s+section\s+compris"
            + @"|\bsupplementary\s+preambles?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Priced as a whole even when the row has an item letter and no unit: "P | Allow a
        // sum for double handling of materials | … | 3,500,000".
        private static readonly Regex LumpSum = new Regex(
            @"^\s*(allow|provide|include)\b|\bprovisional\b|\bprime\s+cost\b|\bp\.?\s*c\.?\s+sum\b|\bcontingenc|\bday\s*works?\b|\blump\s+sum\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A list marker starting a sibling row: "(a)", "a.", "i.", "ii)".
        private static readonly Regex ListMarker = new Regex(@"^\s*[\(\[]?\s*([a-z]|[ivxlc]{1,4}|\d{1,2})\s*[\)\].]\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RefPattern = new Regex(@"^\s*[\(\[]?\s*([A-Za-z]{1,3}|\d{1,3}(\.\d{1,3})*|[ivxlc]{1,5})\s*[\)\].]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A cell reference inside a formula: C8, $C$8, Wall!C12, 'Substructure(1)'!C21.
        // Function names are followed by "(", so SUM( never matches.
        private static readonly Regex CellRef = new Regex(
            @"(?<![A-Za-z0-9_.])\$?([A-Za-z]{1,3})\$?(\d+)(?![\d(A-Za-z_])", RegexOptions.Compiled);

        internal static bool FormulaHasCellRef(string formula)
        {
            return !string.IsNullOrEmpty(formula) && CellRef.IsMatch(formula);
        }

        /// <summary>
        /// Every cell the formula reads is on the row itself: "=D12*E12*F12" is the QS's own
        /// timesing (Nos x Length x Breadth), the item's measurement — not a derivation from
        /// other rows. Writing over it destroys their working, so the row is shown, kept, and
        /// never auto-ticked.
        /// </summary>
        internal static bool IsInRowDimensions(string formula, int row)
        {
            if (string.IsNullOrEmpty(formula)) return false;
            var any = false;
            foreach (Match m in CellRef.Matches(formula))
            {
                any = true;
                if (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) != row) return false;
            }
            return any && formula.IndexOf('!') < 0;
        }

        // A reference into the firm's own take-off/dimension sheet: "'Take Off Sheet'!H12",
        // "'Taking Off Concrete Work'!F20", "Dims!C4". That sheet is the measurement QUIV
        // replaces, so a bill quantity fed only from it is fillable, not a QS's link.
        private static readonly Regex TakeoffRef = new Regex(
            @"(?:'[^']*(?:tak(?:e|ing)\s*-?\s*off|\bdim(?:ension)?s?\b|\babstract\b)[^']*'|[A-Za-z0-9_.]*(?:take\s*off|takeoff|dims?|abstract)[A-Za-z0-9_.]*)!\$?[A-Za-z]{1,3}\$?\d+(?::\$?[A-Za-z]{1,3}\$?\d+)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SheetRef = new Regex(
            @"(?:'([^']+)'|([A-Za-z0-9_.]+))!\$?([A-Za-z]{1,3})\$?(\d+)", RegexOptions.Compiled);

        private static readonly Regex TakeoffSheetName = new Regex(
            @"tak(e|ing)\s*-?\s*off|^\s*dims?\b|\bdimension|\babstract\b|\bt\.?o\.?\s*sheet\b|squaring",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static bool IsTakeoffSheetName(string name) { return TakeoffSheetName.IsMatch(name ?? ""); }

        /// <summary>True when every cell the formula reads is on a take-off/dimension sheet.</summary>
        public static bool FedByTakeoffSheet(string formula)
        {
            if (string.IsNullOrEmpty(formula) || !TakeoffRef.IsMatch(formula)) return false;
            return !FormulaHasCellRef(TakeoffRef.Replace(formula, "0"));
        }

        /// <summary>
        /// A materials-and-labour schedule interleaves every item with its build-up:
        ///
        ///   A | 230mm thick; stretcher bond | =119+560        | m2     item: filled
        ///     | 225mm block                 | =C6*11          | nr     build-up: derived
        ///     | Cement                      | =C6*0.2         | Bag    build-up: derived
        ///     |                             | =SUM(C6:C10)    | m3     subtotal: derived
        ///
        /// Filling the item is enough: every build-up line, amount and collection is the
        /// client's own formula over it and recalculates. So a row whose quantity is worked
        /// out from OTHER rows is never sent to be matched and never written — unless it
        /// carries an item letter, which is the QS deliberately tying one item to another
        /// ("Floor tiles =C8"): that stays in the grid, never auto-ticked.
        ///
        /// An SMM-coded bill spreads one item over several rows, with the letter on the first
        /// and the unit and quantity on the last:
        ///
        ///   C | 1.5:6.2.1 | Excavation, commencing from stripped level; …
        ///     |           | excavation; Not exceeding 2m deep; trenches   | Cu.m | 354
        ///
        /// That is ONE item, written on the row with the quantity.
        /// </summary>
        private static void ReadRows(ExcelWorksheet ws, ClientBillSheet sheet)
        {
            var cols = sheet.Columns;
            var first = Math.Max(cols.HeaderRow + 1, ws.Dimension.Start.Row);
            var last = ws.Dimension.End.Row;

            var stages = new List<string>();      // outermost first
            var stageKinds = new List<string>();
            string trade = null;
            var headings = new List<string>();
            var lastWasItem = false;
            string lastRealUnit = null;
            ClientBillRow lastItem = null;
            var tailRows = 0;

            // An item started on an earlier row (letter + first part of the text, no unit).
            ClientBillRow pending = null;
            var pendingParts = new List<string>();

            Action flushPendingAsHeading = () =>
            {
                if (pending == null) return;
                pending.Kind = ClientBillRowKind.Heading;
                var text = string.Join(" ", pendingParts);
                if (text.Length <= MaxHeadingLength && !Preamble.IsMatch(text))
                {
                    if (lastWasItem) headings.Clear();
                    headings.Add(text);
                    if (headings.Count > MaxHeadings) headings.RemoveAt(0);
                }
                sheet.Rows.Add(pending);
                pending = null;
                pendingParts.Clear();
                lastWasItem = false;
            };

            for (var r = first; r <= last; r++)
            {
                var desc = Text(ws.Cells[r, cols.DescriptionCol].Value);
                var unitRaw = Text(ws.Cells[r, cols.UnitCol].Value);
                var unitCellIsNumber = IsNumber(ws.Cells[r, cols.UnitCol].Value);
                var itemRef = cols.ItemCol > 0 ? Text(ws.Cells[r, cols.ItemCol].Value) : "";
                var qtyCell = ws.Cells[r, cols.QtyCol];
                var qtyText = Text(qtyCell.Value);
                var formula = qtyCell.Formula ?? "";
                var hasFormula = formula.Length > 0;

                // A title typed into the item-ref column (or merged across it) is still a title.
                if (desc.Length == 0 && unitRaw.Length == 0 && itemRef.Length > 12 && itemRef.Contains(" ") && !RefPattern.IsMatch(itemRef))
                {
                    desc = itemRef;
                    itemRef = "";
                }
                if (desc.Length == 0 && unitRaw.Length == 0 && qtyText.Length == 0) continue;
                // "." and "-" rows are spacing, not headings.
                if (unitRaw.Length == 0 && desc.Length > 0 && !desc.Any(char.IsLetterOrDigit)) continue;

                // A ditto in the unit column means "the unit above".
                var unit = unitRaw;
                if (ClientBillUnits.IsDitto(unitRaw) && lastRealUnit != null) unit = lastRealUnit;
                else if (ClientBillUnits.IsMeasured(unitRaw) || ClientBillUnits.IsSum(unitRaw)) lastRealUnit = unitRaw;

                var row = new ClientBillRow
                {
                    Id = sheet.Index.ToString(CultureInfo.InvariantCulture) + ":" + r.ToString(CultureInfo.InvariantCulture),
                    SheetName = sheet.Name,
                    Row = r,
                    ItemRef = itemRef,
                    Description = desc,
                    Unit = unit,
                    QtyIsFormula = hasFormula,
                    QtyIsLinked = FormulaHasCellRef(formula) && !FedByTakeoffSheet(formula) && !IsInRowDimensions(formula, r),
                    QtyFormula = hasFormula ? "=" + formula.TrimStart('=') : "",
                    ExistingQty = qtyCell.Value == null ? "" : qtyText,
                };

                // ---- a repeated header row is furniture, whatever else it looks like -------
                if (IsHeaderLabelRow(ws, r, cols))
                {
                    row.Kind = ClientBillRowKind.Bookkeeping;
                    row.Warning = "Repeated header row";
                    sheet.Rows.Add(row);
                    continue;
                }

                // ---- the second (or later) row of an item begun above ----------------------
                if (pending != null)
                {
                    var continues = itemRef.Length == 0 && !Bookkeeping.IsMatch(desc) && !IsSectionTitle(desc)
                                    && !(row.QtyIsLinked && desc.Length == 0)
                                    && !ListMarker.IsMatch(desc)
                                    && !pendingParts[pendingParts.Count - 1].TrimEnd().EndsWith(":");
                    if (continues && unit.Length == 0 && pendingParts.Count < MaxItemRows)
                    {
                        pendingParts.Add(desc);
                        continue;
                    }
                    if (continues && unit.Length > 0)
                    {
                        if (desc.Length > 0) pendingParts.Add(desc);
                        row.ItemRef = pending.ItemRef;
                        row.Description = string.Join(" ", pendingParts);
                        pending = null;
                        pendingParts.Clear();
                        lastItem = AddItem(row, stages, trade, headings, sheet, cols, ws, r);
                        lastWasItem = true;
                        tailRows = 0;
                        continue;
                    }
                    flushPendingAsHeading();
                }

                // Description wrapped onto the row above, unit on this one: the heading we
                // just pushed was really this item's description.
                if (desc.Length == 0 && !hasFormula && headings.Count > 0 && !lastWasItem)
                {
                    row.Description = desc = headings[headings.Count - 1];
                    headings.RemoveAt(headings.Count - 1);
                }

                var measured = ClientBillUnits.IsMeasured(unit);

                // ---- furniture ------------------------------------------------------------
                if ((!measured && Bookkeeping.IsMatch(desc)) || TaxLine.IsMatch(desc))
                {
                    row.Kind = ClientBillRowKind.Bookkeeping;
                    sheet.Rows.Add(row);
                    lastWasItem = false;
                    continue;
                }

                // ---- the columns are swapped on this row -----------------------------------
                if (unitCellIsNumber && ClientBillUnits.IsKnown(qtyText))
                {
                    row.Unit = qtyText;
                    row.NotFillable = true;
                    row.Warning = "This row has the unit and the quantity the other way round.";
                    row.Kind = ClientBillRowKind.Item;
                    row.Section = SectionOf(stages, trade, ws, r, cols);
                    row.Headings = new List<string>(headings);
                    sheet.Rows.Add(row);
                    lastWasItem = true;
                    lastItem = row;
                    continue;
                }

                // ---- a number is not a unit ------------------------------------------------
                if (unitCellIsNumber)
                {
                    var n = Convert.ToDouble(ws.Cells[r, cols.UnitCol].Value, CultureInfo.InvariantCulture);
                    row.Unit = unitRaw;
                    if (n > 0 && n <= 1) { row.Kind = ClientBillRowKind.Sum; }
                    else
                    {
                        row.Kind = ClientBillRowKind.Item;
                        row.NotFillable = true;
                        row.Warning = "The unit cell holds a number, so the unit is unknown.";
                        row.Section = SectionOf(stages, trade, ws, r, cols);
                        row.Headings = new List<string>(headings);
                    }
                    sheet.Rows.Add(row);
                    lastWasItem = true;
                    continue;
                }

                // ---- worked out from other rows: a build-up line, a subtotal ---------------
                if (desc.Length == 0 || (row.QtyIsLinked && itemRef.Length == 0 && !IsInRowDimensions(formula, r))
                    || (ClientBillUnits.IsResource(unit) && !HasOwnRate(ws, r, cols)))
                {
                    row.Kind = ClientBillRowKind.Derived;
                    sheet.Rows.Add(row);
                    lastWasItem = true;
                    continue;
                }

                // ---- titles ----------------------------------------------------------------
                var lettered = itemRef.Length > 0;
                var titleLike = unit.Length == 0 && qtyText.Length == 0 && !HasOwnRate(ws, r, cols);
                if (titleLike && IsSectionTitle(desc) && !(lettered && NextRowCarriesQty(ws, r, last, cols)))
                {
                    row.Kind = ClientBillRowKind.Section;
                    var kind = StageKind(desc);
                    if (kind != null)
                    {
                        // Document nesting: a new stage replaces the most recent stage of the
                        // SAME kind and drops everything inside it ("2ND FLOOR" after
                        // "1ST FLOOR › CONCRETE" leaves "BILL NO 2 › 2ND FLOOR").
                        var at = stageKinds.LastIndexOf(kind);
                        if (at >= 0) { stages.RemoveRange(at, stages.Count - at); stageKinds.RemoveRange(at, stageKinds.Count - at); }
                        stages.Add(Clip(desc, 60));
                        stageKinds.Add(kind);
                        while (stages.Count > MaxStages) { stages.RemoveAt(0); stageKinds.RemoveAt(0); }
                        trade = null;
                    }
                    else trade = Clip(desc, 60);
                    headings.Clear();
                    lastWasItem = false;
                    sheet.Rows.Add(row);
                    continue;
                }

                // ---- priced as a whole ------------------------------------------------------
                if (unit.Length == 0 && lettered && LumpSum.IsMatch(desc))
                {
                    row.Kind = ClientBillRowKind.Sum;
                    row.Section = SectionOf(stages, trade, ws, r, cols);
                    row.Headings = new List<string>(headings);
                    sheet.Rows.Add(row);
                    lastWasItem = true;
                    continue;
                }

                // ---- the first row of a multi-row item --------------------------------------
                if (unit.Length == 0 && lettered && qtyText.Length == 0)
                {
                    pending = row;
                    pendingParts.Add(desc);
                    continue;
                }

                // ---- a tail line under an item ----------------------------------------------
                if (unit.Length == 0 && !lettered && qtyText.Length == 0 && lastWasItem && lastItem != null
                    && tailRows < MaxTailRows && !ListMarker.IsMatch(desc) && OpensLowerCase.IsMatch(desc)
                    && desc.Length <= 200)
                {
                    lastItem.Description = Clip(lastItem.Description + " " + desc, 300);
                    row.Kind = ClientBillRowKind.Heading;   // kept as a row, not as context
                    sheet.Rows.Add(row);
                    tailRows++;
                    continue;
                }

                // ---- headings ----------------------------------------------------------------
                if (unit.Length == 0 && qtyText.Length == 0)
                {
                    row.Kind = ClientBillRowKind.Heading;
                    if (desc.Length <= MaxHeadingLength && !Preamble.IsMatch(desc))
                    {
                        // A heading after items starts a new group; the old one is done.
                        if (lastWasItem) headings.Clear();
                        headings.Add(desc);
                        if (headings.Count > MaxHeadings) headings.RemoveAt(0);
                    }
                    sheet.Rows.Add(row);
                    lastWasItem = false;
                    tailRows = 0;
                    continue;
                }

                // ---- an item -----------------------------------------------------------------
                if (unit.Length == 0)
                {
                    // A quantity with no unit at all: shown, never filled blind.
                    row.Kind = ClientBillRowKind.Item;
                    row.NotFillable = true;
                    row.Warning = "This row has a quantity but no unit.";
                    row.Section = SectionOf(stages, trade, ws, r, cols);
                    row.Headings = new List<string>(headings);
                    sheet.Rows.Add(row);
                    lastWasItem = true;
                    lastItem = row;
                    continue;
                }

                lastItem = AddItem(row, stages, trade, headings, sheet, cols, ws, r);
                lastWasItem = true;
                tailRows = 0;
            }
            flushPendingAsHeading();

            ResolveContinuations(sheet.Rows);
            sheet.IsSummary = LooksLikeSummary(sheet.Rows);
            NoteTypicalFloorMultiplier(ws, sheet);
        }

        private static ClientBillRow AddItem(ClientBillRow row, List<string> stages, string trade, List<string> headings,
            ClientBillSheet sheet, ClientBillColumns cols, ExcelWorksheet ws, int r)
        {
            var kind = ClientBillUnits.Kind(row.Unit);
            if (kind == UnitKind.Sum)
            {
                // "Set"/"Lot" with a quantity greater than one is measured; one of anything is
                // a lump sum.
                double q;
                var canon = ClientBillUnits.Canonical(row.Unit);
                var counted = (canon == "set" || canon == "lot")
                              && double.TryParse(row.ExistingQty.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out q) && q > 1;
                row.Kind = counted ? ClientBillRowKind.Item : ClientBillRowKind.Sum;
            }
            // A resource unit is a build-up line — unless the row carries its own rate and a
            // literal quantity, which makes it a priced item of the bill ("Cement … Bag … 9,500").
            else if (kind == UnitKind.Resource && !HasOwnRate(ws, r, cols)) row.Kind = ClientBillRowKind.Derived;
            else
            {
                row.Kind = ClientBillRowKind.Item;
                if (kind == UnitKind.Unknown)
                {
                    row.NotFillable = true;
                    row.Warning = "RateGen does not recognise the unit \"" + row.Unit + "\".";
                }
            }

            if (row.QtyIsFormula && IsInRowDimensions(ws.Cells[r, cols.QtyCol].Formula ?? "", r))
            {
                row.QtyIsLinked = true;
                row.Warning = Join(row.Warning, "The quantity is the QS's own dimensions (" + row.QtyFormula + "); writing replaces them.");
            }

            row.Section = SectionOf(stages, trade, ws, r, cols);
            row.Headings = new List<string>(headings);
            sheet.Rows.Add(row);
            return row;
        }

        private static bool HasOwnRate(ExcelWorksheet ws, int row, ClientBillColumns cols)
        {
            if (cols.RateCol <= 0) return false;
            var cell = ws.Cells[row, cols.RateCol];
            return cell.Value != null || !string.IsNullOrEmpty(cell.Formula);
        }

        private static bool NextRowCarriesQty(ExcelWorksheet ws, int row, int last, ClientBillColumns cols)
        {
            for (var r = row + 1; r <= Math.Min(last, row + 3); r++)
            {
                var desc = Text(ws.Cells[r, cols.DescriptionCol].Value);
                var unit = Text(ws.Cells[r, cols.UnitCol].Value);
                var qty = Text(ws.Cells[r, cols.QtyCol].Value);
                var refCell = cols.ItemCol > 0 ? Text(ws.Cells[r, cols.ItemCol].Value) : "";
                if (desc.Length == 0 && unit.Length == 0 && qty.Length == 0) continue;
                return refCell.Length == 0 && unit.Length > 0 && qty.Length > 0;
            }
            return false;
        }

        /// <summary>A page-repeat of the column labels is furniture, never a fillable item.</summary>
        private static bool IsHeaderLabelRow(ExcelWorksheet ws, int row, ClientBillColumns cols)
        {
            var desc = HeaderLabel(ws.Cells[row, cols.DescriptionCol].Value);
            var unit = HeaderLabel(ws.Cells[row, cols.UnitCol].Value);
            var qty = HeaderLabel(ws.Cells[row, cols.QtyCol].Value);
            var descIsLabel = desc.Length > 0 && IsDescriptionLabel(desc);
            var unitIsLabel = unit.Length > 0 && IsUnitLabel(unit);
            var qtyIsLabel = qty.Length > 0 && IsQtyLabel(qty);
            return (descIsLabel && (unitIsLabel || qtyIsLabel)) || (qtyIsLabel && unitIsLabel);
        }

        private static string SectionOf(List<string> stages, string trade, ExcelWorksheet ws, int row, ClientBillColumns cols)
        {
            var parts = new List<string>(stages);
            if (!string.IsNullOrEmpty(trade)) parts.Add(trade);
            // A per-row Section / Room / Location column is the innermost stage: on a flat
            // table the same description repeats per room, and only that column tells them apart.
            if (cols.RowSectionCol > 0)
            {
                var v = Text(ws.Cells[row, cols.RowSectionCol].Value);
                if (v.Length > 0 && v.Length <= 60) parts.Add(v);
            }
            return parts.Count == 0 ? null : string.Join(" › ", parts);
        }

        /// <summary>
        /// "Materials and Labour", "Materials Only", "Mat. N Lab. Schedule": every quantity is
        /// pulled from the bill sheets ("='Substructure(1)'!C21", "=BoQ!C13"). Filling one
        /// would be filling the bill twice.
        ///
        /// A typical-floor bill is NOT that: floors 4, 5 and 6 copy floor 3 cell for cell, at
        /// the SAME address, and they are real bill pages.
        /// </summary>
        private static bool LooksLikeSummary(List<ClientBillRow> rows)
        {
            var withQty = rows.Where(r => r.QtyIsFormula || r.ExistingQty.Length > 0).ToList();
            if (withQty.Count < 3) return false;
            var crossSheet = withQty.Count(r => IsCrossSheetEvidence(r));
            return crossSheet * 2 >= withQty.Count;
        }

        private static bool IsCrossSheetEvidence(ClientBillRow row)
        {
            var f = row.QtyFormula;
            if (f.Length == 0 || FedByTakeoffSheet(f)) return false;
            var any = false;
            foreach (Match m in SheetRef.Matches(f))
            {
                any = true;
                // Same address on another sheet: a floor copied from the floor below.
                if (int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) == row.Row) return false;
            }
            return any;
        }

        /// <summary>
        /// A typical-floor bill prices one measured floor n times ("Floor | X | 9" then
        /// "=F6*E6"). HERON fills one floor, so the QS must see the multiplier rather than have
        /// it applied silently.
        /// </summary>
        private static readonly Regex FloorMultiplier = new Regex(
            @"^\+?\s*\$?([A-Za-z]{1,3})\$?(\d+)\s*\*\s*(\d{1,2})\s*$", RegexOptions.Compiled);

        private static void NoteTypicalFloorMultiplier(ExcelWorksheet ws, ClientBillSheet sheet)
        {
            // The multiplier sits in the AMOUNT column of the collection block, not in the
            // quantity column, so the sheet itself is scanned rather than the parsed rows.
            var last = ws.Dimension.End.Row;
            var lastCol = Math.Min(ws.Dimension.End.Column, HeaderScanCols);
            for (var r = ws.Dimension.Start.Row; r <= last; r++)
                for (var c = 1; c <= lastCol; c++)
                {
                    var m = FloorMultiplier.Match((ws.Cells[r, c].Formula ?? "").Trim());
                    if (!m.Success) continue;
                    var n = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    if (n < 2 || n > 30) continue;
                    // Only when it multiplies this page's own total: a rate escalation or a
                    // contingency multiplies something else.
                    var source = ws.Cells[int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), ColumnNumber(m.Groups[1].Value)];
                    if ((source.Formula ?? "").IndexOf("SUM(", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    sheet.Note = "This sheet prices one measured floor " + n + " times (row " + r
                                 + "). RateGen's total counts the floor once; the bill's own collection multiplies it — check the multiplier before using RateGen's total.";
                    return;
                }
        }

        // "65mm ditto." follows "75mm ditto.", which follows "100mm diameter black pipe":
        // walk back past every continuation to the item the chain hangs off.
        private static void ResolveContinuations(List<ClientBillRow> rows)
        {
            var items = rows.Where(x => x.Kind == ClientBillRowKind.Item || x.Kind == ClientBillRowKind.Sum).ToList();
            for (var i = 0; i < items.Count; i++)
            {
                if (!IsElliptical(items[i].Description)) continue;
                for (int j = i - 1, hops = 0; j >= 0 && hops < MaxDittoHops; j--, hops++)
                {
                    var prev = items[j];
                    if (IsElliptical(prev.Description)) continue;
                    var parts = prev.Headings.Skip(Math.Max(0, prev.Headings.Count - 2)).ToList();
                    parts.Add(prev.Description);
                    var anchor = string.Join(" > ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
                    items[i].ContinuesFrom = Clip(anchor, 300);
                    break;
                }
            }
        }

        // Same test as the AI service's breakdown-fill: a ditto word, or a description that
        // opens in lower case ("maximum depth not exceeding 1.50m").
        private static readonly Regex ContinuationWord = new Regex(
            @"(\bditto\b|\bd°|\bdo\.|^\s*as\s+(above|before|described))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OpensLowerCase = new Regex(@"^\s*[a-z]", RegexOptions.Compiled);

        internal static bool IsElliptical(string description)
        {
            var d = description ?? "";
            return ContinuationWord.IsMatch(d) || OpensLowerCase.IsMatch(d);
        }

        internal static bool IsSectionTitle(string s)
        {
            var letters = s.Where(char.IsLetter).ToList();
            if (letters.Count < 4 || s.Length > 90) return false;
            var upper = letters.Count(char.IsUpper);
            if (upper >= letters.Count * 0.85) return true;
            // A Title-Case floor or element title is a title too: "Second Floor Slab".
            return s.Length <= 60 && StageKind(s) != null && !OpensLowerCase.IsMatch(s);
        }

        // ------------------------------------------------------------------ units

        internal static bool IsResourceUnit(string unit) { return ClientBillUnits.IsResource(unit); }
        internal static bool IsSumUnit(string unit) { return ClientBillUnits.IsSum(unit); }
        internal static bool IsKnownUnit(string unit) { return ClientBillUnits.IsKnown(unit); }

        // ---------------------------------------------------------------- helpers

        private static bool IsNumber(object v)
        {
            return v is double || v is int || v is long || v is decimal || v is float;
        }

        private static string Text(object v)
        {
            if (v == null) return "";
            if (v is double d) return d.ToString("0.###", CultureInfo.InvariantCulture);
            if (v is ExcelErrorValue) return "";
            var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            // Bills pasted from PDFs carry non-breaking spaces between every word.
            return Regex.Replace(s.Replace(' ', ' '), @"\s+", " ").Trim();
        }

        private static string Clip(string s, int max)
        {
            return s != null && s.Length > max ? s.Substring(0, max) : s;
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a)) return b;
            if (string.IsNullOrWhiteSpace(b)) return a;
            return a + " " + b;
        }

        /// <summary>"C" → 3, "AB" → 28; 0 for anything that is not a column letter.</summary>
        public static int ColumnNumber(string letters)
        {
            var s = (letters ?? "").Trim().ToUpperInvariant();
            if (s.Length == 0 || s.Length > 3 || !s.All(ch => ch >= 'A' && ch <= 'Z')) return 0;
            var n = 0;
            foreach (var ch in s) n = n * 26 + (ch - 'A' + 1);
            return n;
        }

        /// <summary>3 → "C"; "" for 0.</summary>
        public static string ColumnLetters(int col)
        {
            var s = "";
            while (col > 0) { var m = (col - 1) % 26; s = (char)('A' + m) + s; col = (col - 1) / 26; }
            return s;
        }
    }
}
