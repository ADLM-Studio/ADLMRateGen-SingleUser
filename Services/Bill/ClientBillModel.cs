// Ported from HERON (ADLMPlanswiftApp, feat/heron-3 5d3c093) on 3 Oct 2026, which took it
// from QUIV (RevitPluginArch, feat/client-bill-fill). Same reader QUIV and HERON use, so a fix
// to how firms write bills belongs in all three.
#nullable disable
using System.Collections.Generic;

namespace ADLMRateGen.Services.Bill
{
    /// <summary>What a row of the client's bill is, as far as filling it goes.</summary>
    public enum ClientBillRowKind
    {
        /// <summary>A measured item: description + a measurable unit. The only kind HERON fills.</summary>
        Item,
        /// <summary>A priced-as-a-whole line (Item, Sum, Prov. Sum, %) — never has a measured quantity.</summary>
        Sum,
        /// <summary>An unpriced line above items that carries their material/spec.</summary>
        Heading,
        /// <summary>A trade/section title (all capitals). Resets the heading trail.</summary>
        Section,
        /// <summary>Collection, carried-to, page total and similar bookkeeping rows.</summary>
        Bookkeeping,
        /// <summary>
        /// Worked out from other cells: a material/labour build-up line under an item
        /// ("Cement =C6*0.2 Bag"), a subtotal ("=SUM(C6:C10)"), a kg→ton conversion row.
        /// Never filled — it recalculates once the items above it are.
        /// </summary>
        Derived,
    }

    /// <summary>1-based column numbers of a bill sheet, 0 when absent.</summary>
    public sealed class ClientBillColumns
    {
        public int HeaderRow { get; set; }
        public int ItemCol { get; set; }
        public int DescriptionCol { get; set; }
        public int UnitCol { get; set; }
        public int QtyCol { get; set; }
        /// <summary>Rate/price column when the header names one, else 0 — tells a build-up line from a priced item.</summary>
        public int RateCol { get; set; }
        /// <summary>
        /// Amount / Total column when the header names one, else 0. When a bill has an Amount
        /// header and no Rate header, RateCol and AmountCol are the same column.
        /// </summary>
        public int AmountCol { get; set; }
        /// <summary>A per-row Section / Room / Location / Block column left of the description, else 0.</summary>
        public int RowSectionCol { get; set; }
        /// <summary>Header label of the filled quantity column ("Block A", "Phase 1"), when it has one.</summary>
        public string QtyLabel { get; set; }
        /// <summary>Header labels of <see cref="OtherQtyCols"/>, same order.</summary>
        public List<string> OtherQtyLabels { get; set; } = new List<string>();
        /// <summary>True when found from header labels; false when inferred from the content.</summary>
        public bool FromHeader { get; set; }
        /// <summary>
        /// Other columns that also hold quantities (a phased or multi-block bill puts Phase 1
        /// and Phase 2 side by side). QtyCol is filled; the QS picks another to fill it too.
        /// </summary>
        public List<int> OtherQtyCols { get; set; } = new List<int>();

        public bool IsUsable
        {
            get { return DescriptionCol > 0 && UnitCol > 0 && QtyCol > 0; }
        }
    }

    public sealed class ClientBillRow
    {
        /// <summary>Stable id across the whole workbook: "sheetIndex:row".</summary>
        public string Id { get; set; }
        public string SheetName { get; set; }
        public int Row { get; set; }
        public string ItemRef { get; set; }
        public string Description { get; set; }
        public string Unit { get; set; }
        public ClientBillRowKind Kind { get; set; }

        /// <summary>Nearest enclosing section title, if any.</summary>
        public string Section { get; set; }
        /// <summary>Heading lines above this item, outermost first.</summary>
        public List<string> Headings { get; set; } = new List<string>();
        /// <summary>For a "Ditto"/lower-case continuation: the item it continues, with its headings.</summary>
        public string ContinuesFrom { get; set; }

        /// <summary>Whatever the qty cell already holds (number or text), for the review grid.</summary>
        public string ExistingQty { get; set; }
        /// <summary>The qty cell holds a formula of any kind.</summary>
        public bool QtyIsFormula { get; set; }
        /// <summary>
        /// The qty formula points at other cells ("=C8", "=(679+Wall!C12)*2"): the QS tied
        /// this item to another on purpose, so it is never auto-ticked.
        /// </summary>
        public bool QtyIsLinked { get; set; }
        /// <summary>The qty formula text, e.g. "=119+560", for the review grid.</summary>
        public string QtyFormula { get; set; }
        /// <summary>
        /// The row is shown but must never be written: the bill's own columns are swapped on
        /// it, its unit is unreadable, or writing would destroy the QS's dimensions.
        /// </summary>
        public bool NotFillable { get; set; }
        /// <summary>Why the QS should look at this row (set with <see cref="NotFillable"/>, or on its own).</summary>
        public string Warning { get; set; }
    }

    public sealed class ClientBillSheet
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public ClientBillColumns Columns { get; set; }
        public List<ClientBillRow> Rows { get; set; } = new List<ClientBillRow>();
        /// <summary>Why the sheet could not be read, when it could not.</summary>
        public string Problem { get; set; }
        /// <summary>
        /// A summary/schedule sheet whose quantities are pulled from the bill sheets
        /// ("='Substructure(1)'!C21"). Left out by default: it recalculates by itself.
        /// </summary>
        public bool IsSummary { get; set; }
        /// <summary>The firm's own take-off/dimension sheet: skipped unless the workbook has no bill.</summary>
        public bool IsTakeoffSheet { get; set; }
        /// <summary>Something about the whole sheet the QS must know (e.g. a typical-floor multiplier).</summary>
        public string Note { get; set; }
    }

    public sealed class ClientBillWorkbook
    {
        public string Path { get; set; }
        public List<ClientBillSheet> Sheets { get; set; } = new List<ClientBillSheet>();
    }

    /// <summary>One quantity to write back: the target cell and where it came from.</summary>
    public sealed class ClientBillFill
    {
        public string SheetName { get; set; }
        public int Row { get; set; }
        public int QtyCol { get; set; }
        /// <summary>The quantity to write; null clears the cell (a figure left over from another job).</summary>
        public double? Qty { get; set; }
        /// <summary>Cell note text: the HERON lines summed into the quantity.</summary>
        public string Note { get; set; }
    }
}
