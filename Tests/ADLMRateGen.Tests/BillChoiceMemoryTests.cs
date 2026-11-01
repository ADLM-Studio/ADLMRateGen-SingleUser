using System.IO;
using ADLMRateGen.Services.Bill;
using ADLMRateGen.ViewModel.BillPricing;
using Xunit;

namespace ADLMRateGen.Tests
{
    /// <summary>
    /// Price a bill, after the 1 Nov 2026 report: a suggestion shows its rate and amount
    /// and counts in the bill total, Accept works whenever there is a suggestion, and
    /// the rate the user accepts or chooses for a line is proposed first next time.
    /// </summary>
    public class BillChoiceMemoryTests
    {
        private static readonly BillRate Excavation = new()
        {
            Id = "r1", Trade = "Excavation", Name = "Excavate trench not exceeding 1.5m deep", Unit = "m3", UnitKey = "m3", Rate = 1160.94m,
        };
        private static readonly BillRate Disposal = new()
        {
            Id = "r2", Trade = "Excavation", Name = "Remove surplus excavated material from site", Unit = "m3", UnitKey = "m3", Rate = 2400m,
        };
        private static readonly BillRate Block = new()
        {
            Id = "r3", Trade = "Block Works", Name = "225mm blockwall in cement and sand mortar (1:6)", Unit = "m2", UnitKey = "m2", Rate = 23500m,
        };
        private static readonly BillRate[] Rates = { Excavation, Disposal, Block };

        private const string Trench = "SUBSTRUCTURE › Excavation › Excavate trench for foundation not exceeding 1.5m deep";

        private static string TempFile() => Path.Combine(Path.GetTempPath(), "rategen-bill-choices-" + Guid.NewGuid().ToString("N") + ".json");

        private static BillLineViewModel Line(string description, string unit, string qty, string section = "SUBSTRUCTURE")
        {
            var row = new ClientBillRow
            {
                Id = "0:" + Guid.NewGuid().ToString("N").Substring(0, 6), SheetName = "Bill 1", Row = 5, ItemRef = "A",
                Description = description, Unit = unit, ExistingQty = qty, Kind = ClientBillRowKind.Item, Section = section,
            };
            return new BillLineViewModel(row, priceable: true);
        }

        // ---- the line shows its suggestion ---------------------------------------------

        [Fact]
        public void A_suggested_line_shows_its_rate_and_amount_before_it_is_accepted()
        {
            var line = Line("Excavation as before but distance not exceeding 3m", "m3", "12.5");
            line.Suggest(Excavation, 0.9, "same work");

            Assert.False(line.IsPriced);
            Assert.True(line.IsSuggestedOnly);
            Assert.Equal(1160.94m, line.UnitRate);
            Assert.Equal(14511.75m, line.Amount);
            Assert.True(line.HasAmount);
            Assert.True(line.CanAccept);
        }

        [Fact]
        public void Accepting_keeps_the_amount_and_marks_it_accepted()
        {
            var line = Line("Excavate trench", "m3", "10");
            line.Suggest(Excavation, 0.75, "");
            line.Accept();

            Assert.True(line.IsPriced);
            Assert.False(line.IsSuggestedOnly);
            Assert.False(line.CanAccept);
            Assert.Equal(11609.40m, line.Amount);
        }

        [Fact]
        public void A_line_priced_with_another_rate_can_still_take_the_suggestion()
        {
            var line = Line("Excavate trench", "m3", "10");
            line.Suggest(Excavation, 0.8, "");
            line.Use(Disposal);
            Assert.True(line.CanAccept);
            line.Accept();
            Assert.Same(Excavation, line.Rate);
        }

        [Fact]
        public void A_line_with_no_suggestion_shows_nothing_and_cannot_be_accepted()
        {
            var line = Line("Something odd", "m3", "10");
            Assert.Null(line.UnitRate);
            Assert.Equal(0m, line.Amount);
            Assert.False(line.HasAmount);
            Assert.False(line.CanAccept);
        }

        // ---- the screen's buttons --------------------------------------------------------

        [Fact]
        public void Accept_and_accept_sure_are_enabled_and_told_to_recheck_when_a_line_is_selected()
        {
            var vm = new BillPricingViewModel { MemoryFor = () => new BillChoiceMemory(null) };
            var sure = Line("Excavate trench", "m3", "10");
            sure.Suggest(Excavation, 0.9, "");
            var unsure = Line("Remove surplus", "m3", "4");
            unsure.Suggest(Disposal, 0.75, "");
            vm.Lines.Add(sure);
            vm.Lines.Add(unsure);

            var told = 0;
            vm.AcceptCommand.CanExecuteChanged += (_, __) => told++;
            vm.SelectedLine = unsure;

            Assert.True(told > 0);
            Assert.True(vm.AcceptCommand.CanExecute(null));
            Assert.True(vm.AcceptSureCommand.CanExecute(null));
            Assert.Equal(1, vm.SureCount);
            Assert.Equal(2, vm.SuggestedCount);
        }

        [Fact]
        public void The_bill_total_counts_suggestions_and_names_the_accepted_part()
        {
            var vm = new BillPricingViewModel { MemoryFor = () => new BillChoiceMemory(null) };
            var a = Line("Excavate trench", "m3", "10");
            a.Suggest(Excavation, 0.9, "");
            var b = Line("Remove surplus", "m3", "2");
            b.Suggest(Disposal, 0.7, "");
            vm.Lines.Add(a);
            vm.Lines.Add(b);

            Assert.Equal(11609.40m + 4800m, vm.GrandTotal);
            Assert.Equal(0m, vm.AcceptedTotal);

            vm.AcceptSure();

            Assert.True(a.IsPriced);
            Assert.False(b.IsPriced);
            Assert.Equal(11609.40m, vm.AcceptedTotal);
            Assert.Equal(4800m, vm.SuggestedTotal);
        }

        [Fact]
        public void Accepting_and_choosing_teach_the_memory()
        {
            var memory = new BillChoiceMemory(null);
            var vm = new BillPricingViewModel { MemoryFor = () => memory };
            var a = Line("Excavate trench", "m3", "10");
            a.Suggest(Excavation, 0.8, "");
            var b = Line("Cart away", "m3", "3");
            vm.Lines.Add(a);
            vm.Lines.Add(b);

            vm.SelectedLine = a;
            vm.AcceptSelected();
            vm.SelectedLine = b;
            vm.UseRate(Disposal);

            Assert.Equal(2, memory.Count);
            Assert.Same(Disposal, memory.Recall(b.Context, "m3", Rates)!.Rate);
        }

        // ---- the memory ------------------------------------------------------------------

        [Fact]
        public void The_same_line_is_proposed_with_the_earlier_choice_at_high_confidence()
        {
            var memory = new BillChoiceMemory(null);
            memory.Remember(Trench, "m3", Excavation);

            var hit = memory.Recall("substructure › EXCAVATION ›  Excavate trench for foundation, not exceeding 1.5m deep.", "m3", Rates);

            Assert.NotNull(hit);
            Assert.True(hit!.Exact);
            Assert.Same(Excavation, hit.Rate);
            Assert.True(hit.Confidence >= BillPricingViewModel.SureConfidence);
            Assert.Contains("earlier choice", hit.Reason);
        }

        [Fact]
        public void A_similar_line_in_the_same_unit_is_proposed_too()
        {
            var memory = new BillChoiceMemory(null);
            memory.Remember(Trench, "m3", Excavation);

            var hit = memory.Recall("SUBSTRUCTURE › Excavation › Excavate trench for foundation not exceeding 1.5m deep in firm ground", "m3", Rates);

            Assert.NotNull(hit);
            Assert.False(hit!.Exact);
            Assert.Same(Excavation, hit.Rate);
            Assert.InRange(hit.Confidence, BillPricingViewModel.SureConfidence, 0.95);
            Assert.Contains("similar item", hit.Reason);
        }

        [Fact]
        public void Another_unit_or_other_wording_is_not_proposed()
        {
            var memory = new BillChoiceMemory(null);
            memory.Remember(Trench, "m3", Excavation);

            Assert.Null(memory.Recall(Trench, "m2", Rates));
            Assert.Null(memory.Recall("FRAME › Concrete › Reinforced concrete grade 25 in columns", "m3", Rates));
        }

        [Fact]
        public void Figures_matter_a_225mm_wall_is_not_a_150mm_wall()
        {
            var memory = new BillChoiceMemory(null);
            memory.Remember("BLOCKWORK › 225mm hollow sandcrete block wall", "m2", Block);
            Assert.Null(memory.Recall("BLOCKWORK › 150mm hollow sandcrete block wall", "m2", Rates));
        }

        [Fact]
        public void The_latest_choice_for_a_line_wins()
        {
            var memory = new BillChoiceMemory(null);
            memory.Remember(Trench, "m3", Excavation);
            memory.Remember(Trench, "m3", Disposal);
            Assert.Equal(1, memory.Count);
            Assert.Same(Disposal, memory.Recall(Trench, "m3", Rates)!.Rate);
        }

        [Fact]
        public void A_choice_is_found_by_name_today_and_priced_at_today_s_rate()
        {
            var memory = new BillChoiceMemory(null);
            memory.Remember(Trench, "m3", Excavation);
            // Next run: rate ids are renumbered and the price has moved.
            var today = new BillRate { Id = "r77", Trade = "Excavation", Name = Excavation.Name, Unit = "m3", UnitKey = "m3", Rate = 1300m };

            var hit = memory.Recall(Trench, "m3", new[] { Disposal, today });

            Assert.Same(today, hit!.Rate);
        }

        [Fact]
        public void A_choice_whose_rate_is_gone_is_not_proposed()
        {
            var memory = new BillChoiceMemory(null);
            memory.Remember(Trench, "m3", Excavation);
            Assert.Null(memory.Recall(Trench, "m3", new[] { Disposal, Block }));
        }

        [Fact]
        public void The_memory_survives_a_restart()
        {
            var path = TempFile();
            try
            {
                var first = new BillChoiceMemory(path);
                first.Remember(Trench, "m3", Excavation);
                first.Save();

                var next = new BillChoiceMemory(path);
                Assert.Equal(1, next.Count);
                Assert.Same(Excavation, next.Recall(Trench, "m3", Rates)!.Rate);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void A_damaged_memory_file_starts_clean()
        {
            var path = TempFile();
            try
            {
                File.WriteAllText(path, "{ not json");
                Assert.Equal(0, new BillChoiceMemory(path).Count);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Lines_with_no_words_are_not_remembered()
        {
            var memory = new BillChoiceMemory(null);
            memory.Remember(" › - ", "m3", Excavation);
            Assert.Equal(0, memory.Count);
        }
    }
}
