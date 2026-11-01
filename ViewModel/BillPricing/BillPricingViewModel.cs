using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using ADLMRateGen.Command;
using ADLMRateGen.Services;
using ADLMRateGen.Services.Bill;
using ADLMRateGen.View.Suite;
using Microsoft.Win32;

namespace ADLMRateGen.ViewModel.BillPricing
{
    public enum BillStage { Empty, Working, Review }

    /// <summary>
    /// Price a bill: open any client's Excel bill, ADLM AI proposes a RateGen rate for
    /// each measured item, the user accepts or changes them, and the priced bill is
    /// downloaded as a copy in the client's own layout. Suggestions show their rate and
    /// amount at once; only accepted rates go into the download. A line with no good
    /// match stays unpriced and is listed. Every rate the user accepts or chooses is
    /// remembered (BillChoiceMemory) and proposed first on the next bill.
    /// </summary>
    public sealed class BillPricingViewModel : ViewModelBase
    {
        /// <summary>The confidence at which "Accept the sure matches" takes a suggestion.</summary>
        public const double SureConfidence = 0.85;

        /// <summary>Every priced rate in RateGen, as the carbon screen gets them (set by MainViewModel).</summary>
        public Func<IEnumerable<(string Trade, object Item)>>? Sources { get; set; }

        /// <summary>Opens the signed-in account's memory of earlier choices (tests hand in their own).</summary>
        public Func<BillChoiceMemory> MemoryFor { get; set; } = BillChoiceMemory.ForCurrentUser;
        private BillChoiceMemory? _memory;

        // RelayCommand re-checks CanExecute only when told to (it is not wired to
        // CommandManager), so every state change calls Requery(). Without it the
        // buttons kept the state they had when the screen first loaded: Accept and
        // "Accept the sure matches" stayed greyed out with suggestions on screen.
        private readonly List<RelayCommand> _commands = new();
        private RelayCommand Cmd(Action<object?> run, Func<object?, bool>? can = null)
        {
            var c = new RelayCommand(run, can);
            _commands.Add(c);
            return c;
        }
        private void Requery()
        {
            foreach (var c in _commands) c.RaiseCanExecuteChanged();
        }

        public BillPricingViewModel()
        {
            LinesView = CollectionViewSource.GetDefaultView(Lines);
            LinesView.Filter = o => o is BillLineViewModel l && Passes(l);

            OpenBillCommand = Cmd(async _ => await OpenBillAsync(), _ => Stage != BillStage.Working);
            CancelCommand = Cmd(_ => _cts?.Cancel(), _ => Stage == BillStage.Working);
            AcceptSureCommand = Cmd(_ => AcceptSure(), _ => SureCount > 0);
            AcceptCommand = Cmd(_ => AcceptSelected(), _ => SelectedLine?.CanAccept == true);
            ClearCommand = Cmd(_ => SelectedLine?.Clear(), _ => SelectedLine?.IsPriced == true);
            UseRateCommand = Cmd(o => { if (o is BillRate r) UseRate(r); }, o => o is BillRate && SelectedLine?.IsPriceable == true);
            DownloadExcelCommand = Cmd(async _ => await DownloadExcelAsync(), _ => Stage == BillStage.Review && PricedCount > 0);
            StartOverCommand = Cmd(_ => StartOver(), _ => Stage == BillStage.Review);
            SaveToCloudCommand = Cmd(async _ => await SaveToCloudAsync(), _ => Stage == BillStage.Review && !_savingToCloud && ItemCount > 0);
            SetFilterCommand = Cmd(o => Filter = o as string ?? "All");
        }

        // ---- state -----------------------------------------------------------------------

        private BillStage _stage = BillStage.Empty;
        public BillStage Stage
        {
            get => _stage;
            private set
            {
                _stage = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsEmpty));
                RaisePropertyChanged(nameof(IsWorking));
                RaisePropertyChanged(nameof(IsReview));
                Requery();
            }
        }
        public bool IsEmpty => _stage == BillStage.Empty;
        public bool IsWorking => _stage == BillStage.Working;
        public bool IsReview => _stage == BillStage.Review;

        /// <summary>The stages, named as they happen (Richard's loading system).</summary>
        public IList<string> Steps { get; } = new[] { "Reading the bill", "Matching to your rates", "Pricing" };
        private int _step;
        public int Step { get => _step; private set { _step = value; RaisePropertyChanged(); } }

        private string _status = "";
        public string Status { get => _status; private set { _status = value; RaisePropertyChanged(); } }

        /// <summary>A problem the user must know about (AI unavailable, sheets skipped). Empty when none.</summary>
        private string _notice = "";
        public string Notice { get => _notice; private set { _notice = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(HasNotice)); } }
        public bool HasNotice => !string.IsNullOrWhiteSpace(_notice);

        private string _billName = "";
        public string BillName { get => _billName; private set { _billName = value; RaisePropertyChanged(); } }

        public ObservableCollection<BillLineViewModel> Lines { get; } = new();
        public ICollectionView LinesView { get; }

        private string _filter = "All";
        /// <summary>"All", "Needs attention" or "Priced".</summary>
        public string Filter
        {
            get => _filter;
            set
            {
                _filter = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsFilterAll));
                RaisePropertyChanged(nameof(IsFilterAttention));
                RaisePropertyChanged(nameof(IsFilterPriced));
                LinesView.Refresh();
            }
        }
        public bool IsFilterAll { get => _filter == "All"; set { if (value) Filter = "All"; } }
        public bool IsFilterAttention { get => _filter == "Needs attention"; set { if (value) Filter = "Needs attention"; } }
        public bool IsFilterPriced { get => _filter == "Priced"; set { if (value) Filter = "Priced"; } }

        private bool Passes(BillLineViewModel l) => _filter switch
        {
            "Needs attention" => l.NeedsAttention,
            "Priced" => l.IsPriced,
            _ => true,
        };

        private BillLineViewModel? _selected;
        public BillLineViewModel? SelectedLine
        {
            get => _selected;
            set
            {
                _selected = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasSelection));
                _rateSearch = "";
                RaisePropertyChanged(nameof(RateSearch));
                RefreshRateResults();
                Requery();
            }
        }
        public bool HasSelection => _selected != null;

        private string _rateSearch = "";
        /// <summary>Words to find another rate for the selected line; blank ranks by the line's own words.</summary>
        public string RateSearch
        {
            get => _rateSearch;
            set { _rateSearch = value; RaisePropertyChanged(); RefreshRateResults(); }
        }
        public ObservableCollection<BillRate> RateResults { get; } = new();

        // ---- totals ----------------------------------------------------------------------

        public int ItemCount => Lines.Count(l => l.IsPriceable);
        public int PricedCount => Lines.Count(l => l.IsPriced);
        public int AttentionCount => Lines.Count(l => l.NeedsAttention);
        public int SuggestedCount => Lines.Count(l => l.NeedsAttention && l.Suggested != null);
        /// <summary>Suggestions "Accept the sure matches" takes: not yet accepted, at least 85% sure.</summary>
        public int SureCount => Lines.Count(IsSure);
        private static bool IsSure(BillLineViewModel l) => l.NeedsAttention && l.Suggested != null && l.Confidence >= SureConfidence;
        /// <summary>The bill total in naira as shown: accepted lines and suggestions.</summary>
        public decimal GrandTotal => Lines.Where(l => l.IsPriceable).Sum(l => l.Amount);
        /// <summary>Accepted lines only: what the downloaded bill carries.</summary>
        public decimal AcceptedTotal => Lines.Where(l => l.IsPriced).Sum(l => l.Amount);
        /// <summary>The part of the total still only suggested.</summary>
        public decimal SuggestedTotal => GrandTotal - AcceptedTotal;
        public bool HasSuggestedTotal => Lines.Any(l => l.IsSuggestedOnly && l.HasAmount);
        public ObservableCollection<BillSummaryLine> SectionTotals { get; } = new();

        // ---- commands --------------------------------------------------------------------

        public ICommand OpenBillCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand AcceptSureCommand { get; }
        public ICommand AcceptCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand UseRateCommand { get; }
        public ICommand DownloadExcelCommand { get; }
        public ICommand StartOverCommand { get; }
        public ICommand SaveToCloudCommand { get; }

        // The ADLM Cloud project this bill was saved as: saving again updates it.
        private string? _cloudProjectId;
        private bool _savingToCloud;
        /// <summary>"Save to ADLM Cloud", or "Save changes to ADLM Cloud" once it has been saved.</summary>
        public string SaveToCloudLabel => _savingToCloud ? "Saving…" : _cloudProjectId == null ? "Save to ADLM Cloud" : "Save changes to ADLM Cloud";
        public ICommand SetFilterCommand { get; }

        private CancellationTokenSource? _cts;
        private List<BillRate> _rates = new();
        private string _readPath = "";      // the .xlsx actually read (a working copy for an .xls)
        private string _originalPath = "";
        private Dictionary<string, ClientBillColumns> _columns = new();

        // ---- open, read, match -----------------------------------------------------------

        private async Task OpenBillAsync()
        {
            var ofd = new OpenFileDialog
            {
                Title = "Open a bill of quantities",
                Filter = "Excel bills (*.xlsx;*.xls)|*.xlsx;*.xls",
                CheckFileExists = true,
            };
            if (ofd.ShowDialog() != true) return;
            await PriceAsync(ofd.FileName);
        }

        /// <summary>Reads, matches and lays out a bill for review. Public for the test harness.</summary>
        public async Task PriceAsync(string path)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            Lines.Clear();
            SectionTotals.Clear();
            Notice = "";
            SelectedLine = null;
            _originalPath = path;
            _cloudProjectId = null;
            _memory = null;
            RaisePropertyChanged(nameof(SaveToCloudLabel));
            BillName = Path.GetFileName(path);
            Stage = BillStage.Working;
            Step = 0;
            Status = "Reading " + BillName + "…";

            try
            {
                // 1. Read the bill in the client's own layout.
                var read = await Task.Run(() =>
                {
                    var p = LegacyWorkbookConverter.IsLegacy(path) ? LegacyWorkbookConverter.ToXlsx(path) : path;
                    return (Path: p, Book: ClientBillReader.Read(p));
                }, ct);
                _readPath = read.Path;
                var sheets = read.Book.Sheets.Where(s => s.Problem == null && !s.IsSummary && s.Columns != null).ToList();
                if (sheets.Count == 0 && read.Book.Sheets.Any(s => s.IsTakeoffSheet && s.Problem == null)) sheets = read.Book.Sheets.Where(s => s.Problem == null).ToList();
                _columns = sheets.ToDictionary(s => s.Name, s => s.Columns);

                foreach (var s in sheets)
                    foreach (var row in s.Rows)
                    {
                        if (row.Kind == ClientBillRowKind.Bookkeeping || row.Kind == ClientBillRowKind.Derived) continue;
                        var priceable = row.Kind == ClientBillRowKind.Item && !row.NotFillable && ClientBillUnits.IsMeasured(row.Unit);
                        var line = new BillLineViewModel(row, priceable);
                        line.Changed += (_, __) => Recalculate();
                        Lines.Add(line);
                    }

                var skipped = read.Book.Sheets.Where(s => s.Problem != null && !s.IsTakeoffSheet).Select(s => $"{s.Name} ({s.Problem})").ToList();
                var notes = read.Book.Sheets.Where(s => !string.IsNullOrEmpty(s.Note)).Select(s => s.Name + ": " + s.Note).ToList();
                var items = Lines.Where(l => l.IsPriceable).Select(l => l.Row).ToList();
                if (items.Count == 0)
                {
                    Stage = BillStage.Empty;
                    SuiteDialog.Tell("No bill items found",
                        "RateGen could not find measured items (a description, a unit and a quantity) in " + BillName + "."
                        + (skipped.Count > 0 ? "\n\nSheets it could not read: " + string.Join("; ", skipped) + "." : ""),
                        SuiteDialog.Tone.Warning);
                    return;
                }

                // 2. The user's own earlier choices first; ADLM AI for the rest.
                Step = 1;
                _rates = BillRateCatalogue.Build(Sources?.Invoke() ?? Enumerable.Empty<(string, object)>());
                var memory = Memory();
                var recalled = new Dictionary<string, BillRecall>();
                foreach (var l in Lines.Where(l => l.IsPriceable))
                    if (memory.Recall(l.Context, l.UnitKey, _rates) is { } hit) recalled[l.Id] = hit;
                // A line priced word for word before needs no AI; a similar one still asks, but the memory wins.
                var toAsk = items.Where(r => !(recalled.TryGetValue(r.Id, out var h) && h.Exact)).ToList();
                Status = $"Matching {items.Count} items to your {_rates.Count} rates…";
                var progress = new Progress<string>(s => Status = s);
                var outcome = _rates.Count == 0
                    ? new BillMatchOutcome { Ok = false, Error = "RateGen has no priced rates loaded yet. Open a trade once so its rates load, then try again." }
                    : toAsk.Count == 0 ? new BillMatchOutcome { Ok = true }
                    : await BillMatcher.MatchAsync(toAsk, _rates, progress, ct);

                // 3. Lay the suggestions on the lines; nothing is accepted yet.
                Step = 2;
                Status = "Pricing…";
                var byId = _rates.ToDictionary(r => r.Id);
                var byRow = Lines.ToDictionary(l => l.Id);
                foreach (var m in outcome.Matches)
                    if (byRow.TryGetValue(m.RowId, out var line) && byId.TryGetValue(m.RateId, out var rate))
                        line.Suggest(rate, m.Confidence, m.Reason);
                foreach (var (rowId, hit) in recalled)
                    if (byRow.TryGetValue(rowId, out var line)) line.Suggest(hit.Rate, hit.Confidence, hit.Reason, fromMemory: true);

                var problems = new List<string>();
                if (recalled.Count > 0)
                    problems.Add($"{recalled.Count} {(recalled.Count == 1 ? "line is" : "lines are")} proposed from your earlier choices. Check them like any suggestion.");
                if (!outcome.Ok) problems.Add("ADLM AI could not match this bill: " + outcome.Error + " You can still price each line by choosing a rate.");
                else if (outcome.UsedFallback) problems.Add("ADLM AI's bill matcher is not live yet, so the general matcher answered. Check every suggestion.");
                if (skipped.Count > 0) problems.Add("Sheets not read: " + string.Join("; ", skipped) + ".");
                problems.AddRange(notes);
                Notice = string.Join("\n", problems);

                Step = Steps.Count;
                Filter = "All";
                Recalculate();
                Stage = BillStage.Review;
                Status = "";
            }
            catch (OperationCanceledException)
            {
                Stage = BillStage.Empty;
                Status = "";
            }
            catch (Exception ex)
            {
                Stage = BillStage.Empty;
                Status = "";
                SuiteDialog.Tell("Could not open the bill", ex.Message, SuiteDialog.Tone.Danger);
            }
        }

        private BillChoiceMemory Memory() => _memory ??= MemoryFor();

        /// <summary>Accepts every suggestion at least 85% sure. Public for the test harness.</summary>
        public void AcceptSure()
        {
            var sure = Lines.Where(IsSure).ToList();
            foreach (var l in sure)
            {
                l.Accept();
                Learn(l);
            }
            if (sure.Count > 0) Memory().Save();
        }

        /// <summary>Accepts the selected line's suggestion. Public for the test harness.</summary>
        public void AcceptSelected()
        {
            var l = SelectedLine;
            if (l == null || !l.CanAccept) return;
            l.Accept();
            Learn(l);
            Memory().Save();
        }

        /// <summary>Prices the selected line with a rate the user chose. Public for the test harness.</summary>
        public void UseRate(BillRate rate)
        {
            var l = SelectedLine;
            if (l == null || !l.IsPriceable) return;
            l.Use(rate);
            Learn(l);
            Memory().Save();
        }

        // The rate the user gave a line is what the next bill proposes for it.
        private void Learn(BillLineViewModel l)
        {
            if (l.Rate != null) Memory().Remember(l.Context, l.UnitKey, l.Rate);
        }

        private void RefreshRateResults()
        {
            RateResults.Clear();
            var line = _selected;
            if (line == null || !line.IsPriceable) return;
            var text = string.IsNullOrWhiteSpace(_rateSearch) ? line.Context : _rateSearch;
            foreach (var r in BillCandidatePicker.Search(text, line.UnitKey, _rates, 30)) RateResults.Add(r);
        }

        private IEnumerable<IGrouping<string, BillLineViewModel>> Sections() =>
            Lines.Where(l => l.IsPriceable)
                 .GroupBy(l => string.IsNullOrWhiteSpace(l.Section) ? l.SheetName : l.SheetName + " › " + l.Section);

        private List<BillSummaryLine> AcceptedSections() => Sections().Select(g => new BillSummaryLine
        {
            Label = g.Key,
            Items = g.Count(),
            Priced = g.Count(l => l.IsPriced),
            Total = g.Where(l => l.IsPriced).Sum(l => l.Amount),
        }).ToList();

        private void Recalculate()
        {
            SectionTotals.Clear();
            // On screen a section adds up what it shows: accepted lines and suggestions.
            foreach (var g in Sections())
            {
                SectionTotals.Add(new BillSummaryLine
                {
                    Label = g.Key,
                    Items = g.Count(),
                    Priced = g.Count(l => l.IsPriced),
                    Total = g.Sum(l => l.Amount),
                });
            }
            RaisePropertyChanged(nameof(ItemCount));
            RaisePropertyChanged(nameof(PricedCount));
            RaisePropertyChanged(nameof(AttentionCount));
            RaisePropertyChanged(nameof(SuggestedCount));
            RaisePropertyChanged(nameof(SureCount));
            RaisePropertyChanged(nameof(GrandTotal));
            RaisePropertyChanged(nameof(AcceptedTotal));
            RaisePropertyChanged(nameof(SuggestedTotal));
            RaisePropertyChanged(nameof(HasSuggestedTotal));
            if (_filter != "All") LinesView.Refresh();
            Requery();
        }

        private async Task SaveToCloudAsync()
        {
            var lines = Lines.Where(l => l.IsPriceable).Select(l => new CloudBillLine
            {
                SheetName = l.SheetName,
                Row = l.Row.Row,
                ItemRef = l.ItemRef,
                Section = l.Section,
                Headings = l.Row.Headings.ToList(),
                Description = l.Description,
                Unit = l.Unit,
                Qty = l.Qty,
                Rate = l.Rate,
            }).ToList();
            var name = Path.GetFileNameWithoutExtension(_originalPath);
            _savingToCloud = true;
            RaisePropertyChanged(nameof(SaveToCloudLabel));
            Requery();
            try
            {
                var wasSaved = _cloudProjectId != null;
                var result = await BillCloudSaver.SaveAsync(name, lines, _cloudProjectId, CancellationToken.None);
                _cloudProjectId = result.ProjectId;
                var rows = new List<(string, string)>
                {
                    ("Project", name),
                    ("Lines", $"{result.Lines} ({result.Priced} priced)"),
                };
                if (SuiteDialog.Ask(wasSaved ? "Changes saved to ADLM Cloud" : "Saved to ADLM Cloud",
                        wasSaved
                            ? "The project on ADLM Cloud now matches this bill."
                            : "The bill is saved as a RateGen project in your ADLM account. Saving again from here updates the same project.",
                        "Open in ADLM Cloud", "Close", SuiteDialog.Tone.Success, rows))
                    Process.Start(new ProcessStartInfo(BillCloudSaver.WebUrl(result.ProjectId)) { UseShellExecute = true });
            }
            catch (UnauthorizedAccessException ex)
            {
                SuiteDialog.Tell("Could not save to ADLM Cloud", ex.Message, SuiteDialog.Tone.Warning);
            }
            catch (InvalidOperationException)
            {
                SuiteDialog.Tell("Sign in to save to ADLM Cloud", "Your RateGen session has ended. Sign in again, then save.", SuiteDialog.Tone.Warning);
            }
            catch (Exception ex)
            {
                SuiteDialog.Tell("Could not save to ADLM Cloud",
                    "Check your internet connection and try again. The priced bill is still here; nothing is lost.\n\n" + ex.Message,
                    SuiteDialog.Tone.Danger);
            }
            finally
            {
                _savingToCloud = false;
                RaisePropertyChanged(nameof(SaveToCloudLabel));
                Requery();
            }
        }

        private void StartOver()
        {
            if (PricedCount > 0 && !SuiteDialog.Ask("Start again?",
                    "The rates you accepted on " + BillName + " are not kept unless you download the priced bill first.",
                    "Start again", "Keep working", SuiteDialog.Tone.Warning))
                return;
            Lines.Clear();
            SectionTotals.Clear();
            SelectedLine = null;
            Notice = "";
            Stage = BillStage.Empty;
            Recalculate();
        }

        // ---- download --------------------------------------------------------------------

        private async Task DownloadExcelAsync()
        {
            var sfd = new SaveFileDialog
            {
                Title = "Save the priced bill",
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                FileName = Path.GetFileName(BillPriceWriter.DefaultTargetPath(_originalPath)),
                InitialDirectory = Path.GetDirectoryName(_originalPath),
            };
            if (sfd.ShowDialog() != true) return;
            var target = sfd.FileName;

            var cur = CurrencyService.Instance;
            var entries = Lines.Where(l => l.IsPriced && l.Rate != null).Select(l => new BillPriceEntry
            {
                SheetName = l.SheetName,
                Row = l.Row.Row,
                Rate = Math.Round(cur.FromNgn(l.Rate!.Rate), 2),
                Amount = Math.Round(cur.FromNgn(l.Amount), 2),
                Note = $"RateGen: {l.Rate.Name} ({l.Rate.Trade}), {l.Rate.Unit}",
            }).ToList();
            // The download carries accepted rates only, so its summary and total do too.
            var summary = AcceptedSections().Select(s => new BillSummaryLine
            {
                Label = s.Label, Items = s.Items, Priced = s.Priced, Total = Math.Round(cur.FromNgn(s.Total), 2),
            }).ToList();
            var total = Math.Round(cur.FromNgn(AcceptedTotal), 2);

            try
            {
                var result = await Task.Run(() => BillPriceWriter.Write(_readPath, target, _columns, entries, summary, total, cur.Code, BillName));
                var rows = new List<(string, string)>
                {
                    ("Lines priced", $"{result.Written} of {ItemCount}"),
                    ("Bill total", $"{cur.Code} {total:N2}"),
                };
                if (result.AddedColumns > 0) rows.Add(("Columns added", "Rate and Amount, beside the bill's own columns"));
                if (result.Skipped.Count > 0) rows.Add(("Left as they were", result.Skipped.Count + " rate cells hold the client's formula"));
                if (SuiteDialog.Ask("Priced bill saved", Path.GetFileName(target) + " is saved. The client's original is unchanged.",
                        "Show in folder", "Close", SuiteDialog.Tone.Success, rows))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + target + "\"") { UseShellExecute = true });
            }
            catch (IOException ex)
            {
                SuiteDialog.Tell("Could not save the priced bill",
                    "The file may be open in Excel. Close it and try again.\n\n" + ex.Message, SuiteDialog.Tone.Danger);
            }
            catch (Exception ex)
            {
                SuiteDialog.Tell("Could not save the priced bill", ex.Message, SuiteDialog.Tone.Danger);
            }
        }
    }
}
