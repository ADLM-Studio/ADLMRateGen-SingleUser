using System.IO;
using ADLMRateGen.Services.Bill;
using OfficeOpenXml;
using Xunit;

namespace ADLMRateGen.Tests
{
    /// <summary>
    /// Price a bill: the parts where a mistake costs money or a client's file. Units
    /// never cross, quantities read as written, amounts to the kobo, the rate goes in
    /// the bill's own Rate column, the client's amount formulas are kept, and the
    /// original is never written to.
    /// </summary>
    public class BillPricingTests
    {
        static BillPricingTests()
        {
            // RateGen sets this once at start-up (App.xaml.cs); the test host has no App.
            try { ExcelPackage.License.SetNonCommercialOrganization("ADLM Studio (ADLMRateGen tests)"); } catch { }
        }

        private sealed class FakeRate
        {
            public string Description { get; set; } = "";
            public string Unit { get; set; } = "";
            public double NetCost { get; set; }
            public double TotalCost { get; set; }
        }

        private static List<BillRate> Rates() => BillRateCatalogue.Build(new (string, object)[]
        {
            ("Block Works", new FakeRate { Description = "225mm blockwall in cement and sand mortar (1:6)", Unit = "m2", TotalCost = 23500 }),
            ("Block Works", new FakeRate { Description = "150mm blockwall in cement and sand mortar (1:6)", Unit = "m2", TotalCost = 18200 }),
            ("Block Works", new FakeRate { Description = "Concrete filling in 225mm blockwall", Unit = "m2", TotalCost = 12000 }),
            ("Concrete", new FakeRate { Description = "Concrete (1:2:4) grade 20 in foundation or slab.", Unit = "m3", TotalCost = 132700 }),
            ("Finishes", new FakeRate { Description = "Cement and sand (1:3) render to wall 12mm thick.", Unit = "m2", TotalCost = 4500 }),
            ("Finishes", new FakeRate { Description = "Rate with no price", Unit = "m2", TotalCost = 0 }),
            ("Block Works", new FakeRate { Description = "225mm blockwall in cement and sand mortar (1:6)", Unit = "m2", TotalCost = 23500 }),
        });

        [Fact]
        public void The_catalogue_skips_unpriced_rates_and_duplicates()
        {
            var rates = Rates();
            Assert.Equal(5, rates.Count);
            Assert.All(rates, r => Assert.True(r.Rate > 0));
            Assert.Equal("m3", rates.Single(r => r.Trade == "Concrete").UnitKey);
        }

        [Theory]
        [InlineData("Sq.m", "m2")]
        [InlineData("m²", "m2")]
        [InlineData("Cu.m", "m3")]
        [InlineData("No.", "nr")]
        [InlineData("L.S", "sum")]
        public void Bill_units_reduce_to_one_family(string unit, string family)
            => Assert.Equal(family, ClientBillUnits.Canonical(unit));

        [Fact]
        public void The_picker_offers_only_rates_in_the_line_unit_best_match_first()
        {
            var rates = Rates();
            var row = new ClientBillRow
            {
                Section = "BLOCKWORK",
                Headings = { "Sandcrete blocks in cement and sand (1:6) mortar" },
                Description = "225mm hollow block walls",
                Unit = "Sq.m",
            };
            var picked = BillCandidatePicker.For(new[] { (BillCandidatePicker.ContextOf(row), ClientBillUnits.Canonical(row.Unit)) }, rates);
            Assert.NotEmpty(picked);
            Assert.All(picked, r => Assert.Equal("m2", r.UnitKey));
            Assert.Equal("225mm blockwall in cement and sand mortar (1:6)", picked[0].Name);
        }

        [Theory]
        [InlineData("1,250.50", 1250.50)]
        [InlineData(" 12 ", 12)]
        public void Quantities_read_as_written(string text, double expected)
            => Assert.Equal((decimal)expected, BillMoney.ParseQty(text));

        [Theory]
        [InlineData("")]
        [InlineData("-")]
        [InlineData("Item")]
        [InlineData("0")]
        public void A_line_with_no_quantity_has_none(string text) => Assert.Null(BillMoney.ParseQty(text));

        [Fact]
        public void Amounts_round_to_the_kobo()
            => Assert.Equal(1234.57m, BillMoney.Amount(10.5m, 117.578m));

        // ---- reading and writing a real workbook ----------------------------------------

        private static string MakeBill(bool withRateColumns)
        {
            var path = Path.Combine(Path.GetTempPath(), "rategen-bill-" + Guid.NewGuid().ToString("N") + ".xlsx");
            using var pkg = new ExcelPackage();
            var ws = pkg.Workbook.Worksheets.Add("Bill No. 2");
            string[] head = withRateColumns
                ? new[] { "Item", "Description", "Qty", "Unit", "Rate", "Amount" }
                : new[] { "Item", "Description", "Qty", "Unit" };
            for (int c = 0; c < head.Length; c++) ws.Cells[1, c + 1].Value = head[c];
            ws.Cells[2, 2].Value = "BLOCKWORK";
            ws.Cells[3, 2].Value = "Sandcrete blocks in cement and sand (1:6) mortar";
            ws.Cells[4, 1].Value = "A"; ws.Cells[4, 2].Value = "225mm hollow block walls"; ws.Cells[4, 3].Value = 120.5; ws.Cells[4, 4].Value = "m2";
            ws.Cells[5, 1].Value = "B"; ws.Cells[5, 2].Value = "150mm ditto"; ws.Cells[5, 3].Value = 40; ws.Cells[5, 4].Value = "m2";
            // The reader takes a header only with at least three units below it (a real-bill rule).
            ws.Cells[6, 1].Value = "C"; ws.Cells[6, 2].Value = "Cement and sand (1:3) render 12mm thick"; ws.Cells[6, 3].Value = 85; ws.Cells[6, 4].Value = "m2";
            if (withRateColumns)
                for (int r = 4; r <= 6; r++) ws.Cells[r, 6].Formula = $"C{r}*E{r}";
            pkg.SaveAs(new FileInfo(path));
            return path;
        }

        [Fact]
        public void The_reader_finds_the_bill_rate_and_amount_columns()
        {
            var path = MakeBill(withRateColumns: true);
            try
            {
                var sheet = ClientBillReader.Read(path).Sheets.Single();
                Assert.Null(sheet.Problem);
                Assert.Equal(3, sheet.Columns.QtyCol);
                Assert.Equal(5, sheet.Columns.RateCol);
                Assert.Equal(6, sheet.Columns.AmountCol);
                var items = sheet.Rows.Where(r => r.Kind == ClientBillRowKind.Item).ToList();
                Assert.Equal(3, items.Count);
                Assert.Equal(120.5m, BillMoney.ParseQty(items[0].ExistingQty));
                Assert.Equal("BLOCKWORK", items[0].Section);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void The_writer_fills_the_rate_keeps_the_client_formula_and_never_touches_the_original()
        {
            var path = MakeBill(withRateColumns: true);
            var target = BillPriceWriter.DefaultTargetPath(path);
            try
            {
                var before = File.ReadAllBytes(path);
                var sheet = ClientBillReader.Read(path).Sheets.Single();
                var cols = new Dictionary<string, ClientBillColumns> { [sheet.Name] = sheet.Columns };
                var entries = new[]
                {
                    new BillPriceEntry { SheetName = sheet.Name, Row = 4, Rate = 23500m, Amount = BillMoney.Amount(120.5m, 23500m), Note = "RateGen: 225mm blockwall" },
                };
                var result = BillPriceWriter.Write(path, target, cols, entries,
                    new[] { new BillSummaryLine { Label = "Bill No. 2 › BLOCKWORK", Items = 2, Priced = 1, Total = 2831750m } },
                    2831750m, "NGN", "test bill");

                Assert.Equal(1, result.Written);
                Assert.Equal(0, result.AddedColumns);
                Assert.Equal(before, File.ReadAllBytes(path));

                using var pkg = new ExcelPackage(new FileInfo(target));
                var ws = pkg.Workbook.Worksheets[sheet.Name];
                Assert.Equal(23500d, Convert.ToDouble(ws.Cells[4, 5].Value));
                Assert.Equal("C4*E4", ws.Cells[4, 6].Formula);
                Assert.Equal(2831750d, Convert.ToDouble(ws.Cells[4, 6].Value), 2);
                Assert.NotNull(ws.Cells[4, 5].Comment);
                Assert.Null(ws.Cells[5, 5].Value);
                Assert.NotNull(pkg.Workbook.Worksheets[BillPriceWriter.SummarySheet]);
            }
            finally { File.Delete(path); if (File.Exists(target)) File.Delete(target); }
        }

        [Fact]
        public void A_bill_with_no_rate_columns_gets_them_beside_its_own()
        {
            var path = MakeBill(withRateColumns: false);
            var target = BillPriceWriter.DefaultTargetPath(path);
            try
            {
                var sheet = ClientBillReader.Read(path).Sheets.Single();
                Assert.Equal(0, sheet.Columns.RateCol);
                var cols = new Dictionary<string, ClientBillColumns> { [sheet.Name] = sheet.Columns };
                var result = BillPriceWriter.Write(path, target, cols,
                    new[] { new BillPriceEntry { SheetName = sheet.Name, Row = 5, Rate = 18200m, Amount = 728000m } },
                    Array.Empty<BillSummaryLine>(), 728000m, "NGN", "test bill");

                Assert.Equal(2, result.AddedColumns);
                using var pkg = new ExcelPackage(new FileInfo(target));
                var ws = pkg.Workbook.Worksheets[sheet.Name];
                Assert.Equal("Rate", ws.Cells[1, 5].Value);
                Assert.Equal("Amount", ws.Cells[1, 6].Value);
                Assert.Equal(18200d, Convert.ToDouble(ws.Cells[5, 5].Value));
                Assert.Equal(728000d, Convert.ToDouble(ws.Cells[5, 6].Value));
            }
            finally { File.Delete(path); if (File.Exists(target)) File.Delete(target); }
        }

        [Fact]
        public void The_original_is_never_the_target()
        {
            var path = MakeBill(withRateColumns: true);
            try
            {
                Assert.Throws<InvalidOperationException>(() => BillPriceWriter.Write(path, path,
                    new Dictionary<string, ClientBillColumns>(), Array.Empty<BillPriceEntry>(),
                    Array.Empty<BillSummaryLine>(), 0m, "NGN", "x"));
            }
            finally { File.Delete(path); }
        }
    }
}
