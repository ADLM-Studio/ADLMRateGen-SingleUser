using System.Collections.Generic;
using ADLMRateGen.Command;
using ADLMRateGen.Helpers;
using ADLMRateGen.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;                    // ← for Debug.WriteLine
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace ADLMRateGen.ViewModel.CustomRate
{
    public class CustomRateEntryViewModel : ViewModelBase
    {
        private string _rateName;
        public string RateName
        {
            get => _rateName;
            set { if (_rateName != value) { _rateName = value; RaisePropertyChanged(nameof(RateName)); } }
        }

        private bool _isEditing;
        public bool IsEditing
        {
            get => _isEditing;
            set { if (_isEditing != value) { _isEditing = value; RaisePropertyChanged(nameof(IsEditing)); } }
        }

        private CustomRate _currentRate;
        private Guid _originalId;

        // Start empty; we’ll populate in RefreshLookups()
        public ObservableCollection<string> AvailableMaterials { get; } = new();
        public ObservableCollection<string> AvailableLabourItems { get; } = new();

        public ObservableCollection<RateEntryItem> MaterialItems { get; } = new();
        public ObservableCollection<RateEntryItem> LabourItems { get; } = new();

        public event Action Saved;

        public decimal TotalMaterialCost => MaterialItems.Sum(item => item.TotalCost);
        public decimal TotalLabourCost => LabourItems.Sum(item => item.TotalCost);
        public decimal OverallTotal => TotalLabourCost + TotalMaterialCost;

        /// <summary>Cash value of the overhead percentage — shown in the totals block.</summary>
        public decimal OverheadAmount => OverallTotal * OverheadPercent / 100m;

        /// <summary>Cash value of the profit percentage — shown in the totals block.</summary>
        public decimal ProfitAmount => OverallTotal * ProfitPercent / 100m;

        public decimal GrandTotal => OverallTotal + OverheadAmount + ProfitAmount;

        private void RecomputeAll()
        {
            RaisePropertyChanged(nameof(TotalMaterialCost));
            RaisePropertyChanged(nameof(TotalLabourCost));
            RaisePropertyChanged(nameof(OverallTotal));
            RaiseMarkupChanged();
        }

        private void RaiseMarkupChanged()
        {
            RaisePropertyChanged(nameof(OverheadAmount));
            RaisePropertyChanged(nameof(ProfitAmount));
            RaisePropertyChanged(nameof(GrandTotal));
        }

        private decimal _overheadPercent = 10;
        public decimal OverheadPercent
        {
            get => _overheadPercent;
            set
            {
                if (_overheadPercent != value)
                {
                    _overheadPercent = value;
                    _overheadPercentText = FormatPercent(value);
                    RaisePropertyChanged(nameof(OverheadPercent));
                    RaisePropertyChanged(nameof(OverheadPercentText));
                    RaiseMarkupChanged();
                }
            }
        }

        private decimal _profitPercent = 10;
        public decimal ProfitPercent
        {
            get => _profitPercent;
            set
            {
                if (_profitPercent != value)
                {
                    _profitPercent = value;
                    _profitPercentText = FormatPercent(value);
                    RaisePropertyChanged(nameof(ProfitPercent));
                    RaisePropertyChanged(nameof(ProfitPercentText));
                    RaiseMarkupChanged();
                }
            }
        }

        // The Overhead / Profit boxes bind to these strings rather than to the
        // decimals directly. A decimal binding fails to convert while the box
        // holds a partial entry ("", "1.") and WPF then leaves the field looking
        // blank; keeping the raw text means what the user typed always stays on
        // screen while the parsed value drives the totals.
        private string _overheadPercentText = FormatPercent(10);
        public string OverheadPercentText
        {
            get => _overheadPercentText;
            set
            {
                if (_overheadPercentText == value) return;
                _overheadPercentText = value ?? string.Empty;
                RaisePropertyChanged(nameof(OverheadPercentText));

                _overheadPercent = ParsePercent(_overheadPercentText, _overheadPercent);
                RaisePropertyChanged(nameof(OverheadPercent));
                RaiseMarkupChanged();
            }
        }

        private string _profitPercentText = FormatPercent(10);
        public string ProfitPercentText
        {
            get => _profitPercentText;
            set
            {
                if (_profitPercentText == value) return;
                _profitPercentText = value ?? string.Empty;
                RaisePropertyChanged(nameof(ProfitPercentText));

                _profitPercent = ParsePercent(_profitPercentText, _profitPercent);
                RaisePropertyChanged(nameof(ProfitPercent));
                RaiseMarkupChanged();
            }
        }

        private static string FormatPercent(decimal value) =>
            value.ToString("0.##", CultureInfo.CurrentCulture);

        /// <summary>Empty means 0; anything unparseable keeps the previous value.</summary>
        private static decimal ParsePercent(string text, decimal fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0m;
            return decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed)
                ? parsed
                : fallback;
        }

        private string _description;
        public string Description
        {
            get => _description;
            set { if (_description != value) { _description = value; RaisePropertyChanged(nameof(Description)); } }
        }

        // ── AI assist (ADLM AI add-on) ──────────────────────────────────────
        // The AI fills this same form from a plain-language description; the
        // QS reviews every line, then saves with the normal Save flow.
        private string _aiPrompt = string.Empty;
        public string AiPrompt
        {
            get => _aiPrompt;
            set { if (_aiPrompt != value) { _aiPrompt = value; RaisePropertyChanged(nameof(AiPrompt)); } }
        }

        private string _aiStatus = string.Empty;
        public string AiStatus
        {
            get => _aiStatus;
            set { if (_aiStatus != value) { _aiStatus = value; RaisePropertyChanged(nameof(AiStatus)); } }
        }

        private bool _isAiBusy;
        public bool IsAiBusy
        {
            get => _isAiBusy;
            set { if (_isAiBusy != value) { _isAiBusy = value; RaisePropertyChanged(nameof(IsAiBusy)); RaisePropertyChanged(nameof(IsAiIdle)); } }
        }
        public bool IsAiIdle => !_isAiBusy;

        /// <summary>
        /// Checks the server ran on the build-up that it returned anyway —
        /// a line priced per day without pro-rating, a total wildly out against
        /// the closest library rate. Shown as its own block rather than folded
        /// into AiStatus: a rate that is 2.5x wrong but looks plausible is more
        /// dangerous than one that is obviously broken, so it must not read as
        /// ordinary progress text.
        /// </summary>
        public ObservableCollection<string> AiWarnings { get; } = new();

        public bool HasAiWarnings => AiWarnings.Count > 0;

        private void SetAiWarnings(IEnumerable<string>? warnings)
        {
            AiWarnings.Clear();
            foreach (var w in warnings ?? Enumerable.Empty<string>())
                AiWarnings.Add(w);

            RaisePropertyChanged(nameof(HasAiWarnings));
        }

        /// <summary>
        /// True on an ordinary install: the AI endpoint now has a default, so the
        /// section is present unless AI has been switched off deliberately
        /// (ADLM_AI_URL / ADLM_RATEGEN_AI_URL set to "off").
        /// </summary>
        public bool IsAiAvailable => AiRateService.Instance.IsConfigured;

        public ICommand BuildWithAiCommand { get; private set; }

        /// <summary>The AI draft's stages, named as it works (Richard's loading system).</summary>
        public IList<string> AiSteps { get; } = new[]
        {
            "Reading your sentence",
            "Asking ADLM AI for a build-up",
            "Filling in materials and labour",
            "Checking it against ADLM's rules",
        };

        private int _aiStep;
        public int AiStep
        {
            get => _aiStep;
            set { _aiStep = value; RaisePropertyChanged(); }
        }

        public ICommand AddMaterialItemCommand { get; }
        public ICommand AddLabourItemCommand { get; }
        public ICommand SaveCustomRateCommand { get; }
        public event Action<CustomRate> OnViewRateRequested;

        public CustomRateEntryViewModel()
        {
            // Build lookups after services are initialized (App.OnStartup)
            RefreshLookups();

            MaterialItems.CollectionChanged += OnMaterialCollectionChanged;
            LabourItems.CollectionChanged   += OnLabourCollectionChanged;

            AddMaterialItemCommand = new RelayCommand(_ => AddMaterialItem());
            AddLabourItemCommand   = new RelayCommand(_ => AddLabourItem());
            SaveCustomRateCommand  = new RelayCommand(_ => SaveCustomRate());
            BuildWithAiCommand     = new RelayCommand(async _ => await BuildWithAiAsync());

            CurrencyService.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CurrencyService.Rate) or nameof(CurrencyService.Code))
                    RecomputeAll();
            };
        }

        public void RefreshLookups()
        {
            // MATERIALS
            AvailableMaterials.Clear();
            MaterialLibraryService.Initialize(); // safe; reloads the cached list
            foreach (var n in MaterialLibraryService.GetAllMaterialNames())
                AvailableMaterials.Add(n);

            // LABOUR
            AvailableLabourItems.Clear();
            LabourLibraryService.Initialize();   // ensure loaded
            foreach (var n in LabourLibraryService.GetAllLabourNames())
                AvailableLabourItems.Add(n);

            Debug.WriteLine($"[CustomRateEntryVM] materials={AvailableMaterials.Count}, labour={AvailableLabourItems.Count}");
            Debug.WriteLine($"[PATH] Labour file used by service: {AppPaths.LabourLibraryFile}");

        }

        private void OnMaterialCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (RateEntryItem i in e.NewItems) i.PropertyChanged += OnRateEntryItemChanged;
            if (e.OldItems != null) foreach (RateEntryItem i in e.OldItems) i.PropertyChanged -= OnRateEntryItemChanged;

            RaisePropertyChanged(nameof(TotalMaterialCost));
            RaisePropertyChanged(nameof(OverallTotal));
            RaiseMarkupChanged();
        }

        private void OnLabourCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (RateEntryItem i in e.NewItems) i.PropertyChanged += OnRateEntryItemChanged;
            if (e.OldItems != null) foreach (RateEntryItem i in e.OldItems) i.PropertyChanged -= OnRateEntryItemChanged;

            RaisePropertyChanged(nameof(TotalLabourCost));
            RaisePropertyChanged(nameof(OverallTotal));
            RaiseMarkupChanged();
        }

        private void OnRateEntryItemChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RateEntryItem.TotalCost) ||
                e.PropertyName == nameof(RateEntryItem.Quantity)  ||
                e.PropertyName == nameof(RateEntryItem.UnitPrice))
            {
                RaisePropertyChanged(nameof(TotalMaterialCost));
                RaisePropertyChanged(nameof(TotalLabourCost));
                RaisePropertyChanged(nameof(OverallTotal));
                RaiseMarkupChanged();
            }
        }

        public void LoadRate(CustomRate rate)
        {
            if (rate == null) return;
            IsEditing = true;
            _currentRate = rate;
            _originalId  = rate.Id;

            MaterialItems.Clear();
            LabourItems.Clear();

            RateName        = rate.Title;
            Description     = rate.Description;
            OverheadPercent = rate.OverheadPercent;
            ProfitPercent   = rate.ProfitPercent;

            // Order matters: RateType and Description both re-query the library,
            // so the saved figures are restored last and win. (Setting RateType
            // last used to wipe the price of any line the library doesn't know.)
            foreach (var matItem in rate.MaterialItems)
                MaterialItems.Add(new RateEntryItem
                {
                    RateType    = matItem.RateType,
                    Description = matItem.Description,
                    Quantity    = matItem.Quantity,
                    Unit        = matItem.Unit,
                    UnitPrice   = matItem.UnitPrice
                });

            foreach (var labItem in rate.LabourItems)
                LabourItems.Add(new RateEntryItem
                {
                    RateType    = labItem.RateType,
                    Description = labItem.Description,
                    Quantity    = labItem.Quantity,
                    Unit        = labItem.Unit,
                    UnitPrice   = labItem.UnitPrice
                });
        }

        private void AddMaterialItem()
        {
            MaterialItems.Add(new RateEntryItem
            {
                RateType    = RateItemType.Material,
                Quantity    = 0,
                UnitPrice   = 0,
                Unit        = "",
                Description = ""
            });
            RaisePropertyChanged(nameof(TotalMaterialCost));
            RaisePropertyChanged(nameof(OverallTotal));
            RaiseMarkupChanged();
        }

        private void AddLabourItem()
        {
            LabourItems.Add(new RateEntryItem
            {
                RateType    = RateItemType.Labour,
                Quantity    = 0,
                UnitPrice   = 0,
                Unit        = "",
                Description = ""
            });
            RaisePropertyChanged(nameof(TotalLabourCost));
            RaisePropertyChanged(nameof(OverallTotal));
            RaiseMarkupChanged();
        }

        private void SaveCustomRate()
        {
            var newRate = new CustomRate
            {
                Id             = _originalId,
                Title          = RateName,
                Description    = Description,
                MaterialItems  = MaterialItems.ToList(),
                LabourItems    = LabourItems.ToList(),
                OverheadPercent= OverheadPercent,
                ProfitPercent  = ProfitPercent,
                CreatedDate    = IsEditing ? _currentRate.CreatedDate : DateTime.Now
            };

            // An edited rate keeps its place in the cloud. Without CloudId a rate
            // downloaded from the website would go back up as a new rate, and the
            // original would come down again beside it.
            if (IsEditing && _currentRate != null)
            {
                newRate.CloudId         = _currentRate.CloudId;
                newRate.CloudUnit       = _currentRate.CloudUnit;
                newRate.SectionKey      = _currentRate.SectionKey;
                newRate.SectionLabel    = _currentRate.SectionLabel;
                newRate.CloudUpdatedAt  = _currentRate.CloudUpdatedAt;
                newRate.SyncedSignature = _currentRate.SyncedSignature;
            }

            if (!IsEditing)
            {
                newRate.Id = Guid.NewGuid();
                CustomRateServices.SaveCustomRate(newRate);
            }
            else
            {
                CustomRateServices.UpdateCustomRate(newRate);
            }

            // Fold any priced line the library doesn't have yet back into it, so
            // materials and labour entered here (or drafted by AI) are available
            // to every future rate. Existing library prices are never overwritten.
            var addedToLibrary = 0;
            try
            {
                addedToLibrary = RateLineLibrary.Harvest(MaterialItems, LabourItems);
                if (addedToLibrary > 0)
                    RefreshLookups();
            }
            catch (Exception ex)
            {
                // The rate itself is already saved — a library write failure
                // must not look like a failed save.
                Debug.WriteLine($"[CustomRateEntryVM] library harvest failed: {ex}");
            }

            // Show success popup
            if (System.Windows.Application.Current.MainWindow is MainWindow mw)
            {
                var message = addedToLibrary > 0
                    ? $"New Rate Saved — {addedToLibrary} item(s) added to your library"
                    : "New Rate Saved";
                mw.PopupHost.Show(new View.LibrarySuccessView(message));
            }

            Saved?.Invoke();
            ClearForm();
        }

        public void StartNewRate()
        {
            ClearForm();
            IsEditing = false;
        }

        // ── AI assist ───────────────────────────────────────────────────────
        // Calls the ADLM AI Service and fills the form for review. Runs on the
        /// <summary>
        /// True when the prompt asks for a rate build-up in so many words.
        ///
        /// The panel bills against the account's AI allowance on every request,
        /// and the service will answer anything put to it, so a prompt that is
        /// not a work item spends a request to produce something RateGen cannot
        /// use. Requiring both words keeps the box pointed at what the product
        /// does. "Building", "builds", "rebuild" and "rates" all satisfy it,
        /// since the match is on the stem rather than the whole word.
        ///
        /// "Built" is checked separately because the stem does not cover it —
        /// the d becomes a t — and "I need a rate built for 12mm screed" is
        /// exactly how someone asks for this. A guard that turns away the
        /// natural phrasing is worse than no guard: the user has no idea what
        /// they said wrong.
        ///
        /// It is a keyword check, not comprehension: "build a rate for a
        /// birthday cake" passes it, and a bare work item a QS would naturally
        /// type — "225mm blockwork in cement mortar" — does not. The example in
        /// the message therefore shows the expected phrasing rather than just
        /// naming the rule.
        /// </summary>
        internal static bool MentionsRateAndBuild(string? prompt)
        {
            var text = prompt ?? string.Empty;
            return text.IndexOf("rate", StringComparison.OrdinalIgnoreCase) >= 0
                && (text.IndexOf("build", StringComparison.OrdinalIgnoreCase) >= 0
                 || text.IndexOf("built", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // UI thread's context after the await, so collection updates are safe.
        private async Task BuildWithAiAsync()
        {
            if (IsAiBusy) return;
            var prompt = (AiPrompt ?? string.Empty).Trim();
            if (prompt.Length < 8)
            {
                AiStatus = "Describe the work item first, e.g. \"Build a rate for 225mm hollow sandcrete blockwork in cement-sand mortar (1:6)\".";
                return;
            }

            if (!MentionsRateAndBuild(prompt))
            {
                AiStatus =
                    "Ask for a rate build-up: the request has to contain the words \"rate\" and \"build\". " +
                    "For example: \"Build a rate for 225mm hollow sandcrete blockwork in cement-sand mortar (1:6)\".";
                return;
            }

            AiStep = 1;
            IsAiBusy = true;
            AiStatus = "";
            SetAiWarnings(null);
            try
            {
                var progress = new Progress<string>(msg => AiStatus = msg);
                var result = await AiRateService.Instance.BuildRateAsync(prompt, progress: progress);

                if (!result.IsSuccess || result.Rate == null)
                {
                    AiStatus = result.Message ?? "AI request failed.";
                    return;
                }

                AiStep = 2;
                var rate = result.Rate;
                StartNewRate();            // clears the form, and the warnings with it
                RateName = rate.Title;
                Description = rate.Description;
                OverheadPercent = rate.OverheadPercent;
                ProfitPercent = rate.ProfitPercent;
                foreach (var m in rate.MaterialItems) MaterialItems.Add(m);
                foreach (var l in rate.LabourItems) LabourItems.Add(l);
                UpdateTotals();

                // An all-zero build-up is not a usable rate. It used to fill the
                // form and report "AI draft ready", so the QS could save a rate
                // whose every line priced 0.00 without anything flagging it.
                if (AiRateService.IsUnpriced(rate))
                {
                    AiStatus =
                        "ADLM AI returned this build-up with no prices — every line came back at 0.00. " +
                        "The quantities are filled in, but you must enter the rates yourself before saving, " +
                        "or pick items from the library so their prices apply.";
                    return;
                }

                // Set after StartNewRate, which clears them.
                AiStep = 3;
                SetAiWarnings(result.Warnings);
                AiStep = 4;

                var confidence = result.Confidence.HasValue
                    ? $" (confidence {result.Confidence.Value:P0})"
                    : string.Empty;
                var cachedNote = result.Status == AdlmAi.AiStatus.CachedFallback
                    ? " — cached result, service unreachable"
                    : string.Empty;

                AiStatus = HasAiWarnings
                    ? $"AI draft ready{confidence}{cachedNote}, but it did not pass ADLM's checks — " +
                      $"see below. Correct the flagged lines before saving. " +
                      (result.Disclaimer ?? string.Empty)
                    : $"AI draft ready{confidence}{cachedNote}. Review every line before saving. " +
                      (result.Disclaimer ?? string.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CustomRateEntryVM] AI build failed: {ex}");
                AiStatus = "Something went wrong contacting ADLM AI. The rest of RateGen is unaffected.";
            }
            finally
            {
                IsAiBusy = false;
            }
        }

        private void ClearForm()
        {
            // Warnings belong to the build-up that was on screen; leaving them
            // up would attach them to whatever the user types next.
            SetAiWarnings(null);
            RateName = string.Empty;
            Description = string.Empty;
            MaterialItems.Clear();
            LabourItems.Clear();
            OverheadPercent = 10;
            ProfitPercent = 10;
            // Set the text too: if the box holds an unparseable entry the decimal
            // is still 10, so the assignments above are no-ops and would leave it.
            OverheadPercentText = FormatPercent(OverheadPercent);
            ProfitPercentText = FormatPercent(ProfitPercent);
            IsEditing = false;
            _originalId = Guid.Empty;
        }

        private void ViewRate(CustomRate rate) => OnViewRateRequested?.Invoke(rate);
        private void UpdateTotals()
        {
            RaisePropertyChanged(nameof(TotalMaterialCost));
            RaisePropertyChanged(nameof(TotalLabourCost));
            RaisePropertyChanged(nameof(OverallTotal));
            RaiseMarkupChanged();
        }
    }
}
