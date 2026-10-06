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
    /// downloaded as a copy in the client's own layout. Nothing is priced until the
    /// user accepts it; a line with no good match stays unpriced and is listed.
    /// </summary>
    public sealed class BillPricingViewModel : ViewModelBase
    {
        /// <summary>The confidence at which "Accept the sure matches" takes a suggestion.</summary>
        public const double SureConfidence = 0.85;

        /// <summary>Every priced rate in RateGen, as the carbon screen gets them (set by MainViewModel).</summary>
        public Func<IEnumerable<(string Trade, object Item)>>? Sources { get; set; }

        public BillPricingViewModel()
        {
            LinesView = CollectionViewSource.GetDefaultView(Lines);
            LinesView.Filter = o => o is BillLineViewModel l && Passes(l);

            OpenBillCommand = new RelayCommand(async _ => await OpenBillAsync(), _ => Stage != BillStage.Working);
            CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => Stage == BillStage.Working);
            AcceptSureCommand = new RelayCommand(_ => AcceptSure(), _ => Lines.Any(l => l.NeedsAttention && l.Suggested != null && l.Confidence >= SureConfidence));
            AcceptCommand = new RelayCommand(_ => SelectedLine?.Accept(), _ => SelectedLine?.Suggested != null && !SelectedLine.IsPriced);
            ClearCommand = new RelayCommand(_ => SelectedLine?.Clear(), _ => SelectedLine?.IsPriced == true);
            UseRateCommand = new RelayCommand(o => { if (o is BillRate r) SelectedLine?.Use(r); }, o => o is BillRate && SelectedLine?.IsPriceable == true);
            DownloadExcelCommand = new RelayCommand(async _ => await DownloadExcelAsync(), _ => Stage == BillStage.Review && PricedCount > 0);
            StartOverCommand = new RelayCommand(_ => StartOver(), _ => Stage == BillStage.Review);
            SetFilterCommand = new RelayCommand(o => Filter = o as string ?? "All");
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
                CommandManager.InvalidateRequerySuggested();
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
                CommandManager.InvalidateRequerySuggested();
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
        /// <summary>The bill total in naira: accepted lines only.</summary>
        public decimal GrandTotal => Lines.Where(l => l.IsPriced).Sum(l => l.Amount);
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

                // 2. Ask ADLM AI which of the user's rates is the same work.
                Step = 1;
                _rates = BillRateCatalogue.Build(Sources?.Invoke() ?? Enumerable.Empty<(string, object)>());
                Status = $"Matching {items.Count} items to your {_rates.Count} rates…";
                var progress = new Progress<string>(s => Status = s);
                var outcome = _rates.Count == 0
                    ? new BillMatchOutcome { Ok = false, Error = "RateGen has no priced rates loaded yet. Open a trade once so its rates load, then try again." }
                    : await BillMatcher.MatchAsync(items, _rates, progress, ct);

                // 3. Lay the suggestions on the lines; nothing is accepted yet.
                Step = 2;
                Status = "Pricing…";
                var byId = _rates.ToDictionary(r => r.Id);
                var byRow = Lines.ToDictionary(l => l.Id);
                foreach (var m in outcome.Matches)
                    if (byRow.TryGetValue(m.RowId, out var line) && byId.TryGetValue(m.RateId, out var rate))
                        line.Suggest(rate, m.Confidence, m.Reason);

                var problems = new List<string>();
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

        private void AcceptSure()
        {
            foreach (var l in Lines.Where(l => l.NeedsAttention && l.Suggested != null && l.Confidence >= SureConfidence).ToList())
                l.Accept();
        }

        private void RefreshRateResults()
        {
            RateResults.Clear();
            var line = _selected;
            if (line == null || !line.IsPriceable) return;
            var text = string.IsNullOrWhiteSpace(_rateSearch) ? line.Context : _rateSearch;
            foreach (var r in BillCandidatePicker.Search(text, line.UnitKey, _rates, 30)) RateResults.Add(r);
        }

        private void Recalculate()
        {
            SectionTotals.Clear();
            foreach (var g in Lines.Where(l => l.IsPriceable)
                         .GroupBy(l => string.IsNullOrWhiteSpace(l.Section) ? l.SheetName : l.SheetName + " › " + l.Section))
            {
                SectionTotals.Add(new BillSummaryLine
                {
                    Label = g.Key,
                    Items = g.Count(),
                    Priced = g.Count(l => l.IsPriced),
                    Total = g.Where(l => l.IsPriced).Sum(l => l.Amount),
                });
            }
            RaisePropertyChanged(nameof(ItemCount));
            RaisePropertyChanged(nameof(PricedCount));
            RaisePropertyChanged(nameof(AttentionCount));
            RaisePropertyChanged(nameof(SuggestedCount));
            RaisePropertyChanged(nameof(GrandTotal));
            if (_filter != "All") LinesView.Refresh();
            CommandManager.InvalidateRequerySuggested();
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
            var summary = SectionTotals.Select(s => new BillSummaryLine
            {
                Label = s.Label, Items = s.Items, Priced = s.Priced, Total = Math.Round(cur.FromNgn(s.Total), 2),
            }).ToList();
            var total = Math.Round(cur.FromNgn(GrandTotal), 2);

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
