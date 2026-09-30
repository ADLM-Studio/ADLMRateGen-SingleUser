using ADLMRateGen.Command;
using ADLMRateGen.Helpers;
using ADLMRateGen.Services;
using ADLMRateGen.ViewModel.Groundwork; // only for GetItemsFromDB
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;

namespace ADLMRateGen.ViewModel.CarbonOthers
{
    public class CarbonRateBreakdownLine
    {
        public string ComponentName { get; set; } = "";
        public double Quantity { get; set; }
        public string Unit { get; set; } = "";
        public double UnitPrice { get; set; }
        public double TotalPrice { get; set; }

        // keep compatibility with your "bold total line" trigger
        public bool IsTotalLine { get; set; } = false;

        /// <summary>
        /// The library row this line is priced from, kept separately from
        /// ComponentName because that is decorated for display ("material: Cement")
        /// and would not match anything if it were used as a lookup.
        /// </summary>
        public string RefName { get; set; } = "";

        /// <summary>"material" or "labour". Decides which library tab to open.</summary>
        public string RefKind { get; set; } = "";

        /// <summary>
        /// Same shared command every other section's breakdown uses, so there is
        /// one implementation rather than two that can drift.
        /// </summary>
        public System.Windows.Input.ICommand OpenInLibraryCommand
            => ADLMRateGen.Helpers.LibraryLink.OpenCommand;

        /// <summary>
        /// False for the overhead, profit and uplift rows, which are percentages
        /// rather than library items and have nothing to open.
        /// </summary>
        public bool CanOpenInLibrary => !string.IsNullOrWhiteSpace(RefName);
    }

    public class CarbonRateItem
    {
        public int ItemNo { get; set; }
        public string Description { get; set; } = "";
        public string Unit { get; set; } = "m2";

        public double NetCost { get; set; }
        public double OverheadValue { get; set; }
        public double ProfitValue { get; set; }
        public double TotalCost { get; set; }

        public ObservableCollection<CarbonRateBreakdownLine> BreakdownLines { get; set; }
            = new ObservableCollection<CarbonRateBreakdownLine>();

        public string Source { get; set; } = ""; // "Compute" | "AdminRate"
    }

    public class CarbonOthersViewModel : ViewModelBase
    {
        private const string SectionKey = SectionKeys.CarbonOthers;

        private readonly GetItemsFromDB _helper;
        private readonly ComputeItemEngine _computeEngine;

        private double _overheadPercent = 10.0;
        private double _profitPercent = 25.0;
        private string _searchTerm = "";

        private bool _isNetCostFilterOn = false;
        private SortState _currentSort = SortState.None;

        private enum SortState { None, Overhead, TotalCost }

        // ✅ popup hooks (MainWindow will subscribe)
        public event Action<CarbonRateItem>? RequestShowDetails;
        public event Action? RequestAddCustomRate;

        public CarbonOthersViewModel(MaterialLibraryViewModel matLib, LabourLibraryViewModel labourLib)
        {
            _helper = new GetItemsFromDB(matLib, labourLib);

            matLib.LibraryChanged += OnLibraryChanged;
            labourLib.LibraryChanged += OnLibraryChanged;

            _computeEngine = new ComputeItemEngine(GetMaterialPrice, GetLabourRate);

            RateLibraryStore.Changed += OnLibraryChanged;

            Items = new ObservableCollection<CarbonRateItem>();
            ItemsView = CollectionViewSource.GetDefaultView(Items);
            ItemsView.Filter = FilterItem;

            RecomputeCommand = new DelegateCommand(_ => Rebuild());
            FilterCommand = new DelegateCommand(_ => ToggleNetCostFilter());
            SortCommand = new DelegateCommand(_ => CycleSort());
            RefreshRemoteCommand = new DelegateCommand(async _ => await RefreshRemoteAsync());

            // ✅ same UX as Groundwork: clicking description opens details
            ShowDetailsCommand = new DelegateCommand(o =>
            {
                if (o is CarbonRateItem item)
                    RequestShowDetails?.Invoke(item);
            });

            // ✅ needed so the Carbon view can bind the button like Groundwork
            AddCustomRateCommand = new DelegateCommand(_ => RequestAddCustomRate?.Invoke());

            CurrencyService.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CurrencyService.Rate) or nameof(CurrencyService.Code))
                    Rebuild();
            };

            ComputeCatalogStore.ReloadFromDisk();
            RateLibraryStore.ReloadFromDisk();

            Rebuild();
            _ = WarmLoadAsync();
        }

        public double OverheadPercent
        {
            get => _overheadPercent;
            set
            {
                if (_overheadPercent != value)
                {
                    _overheadPercent = value;
                    RaisePropertyChanged();
                    Rebuild();
                }
            }
        }

        public double ProfitPercent
        {
            get => _profitPercent;
            set
            {
                if (_profitPercent != value)
                {
                    _profitPercent = value;
                    RaisePropertyChanged();
                    Rebuild();
                }
            }
        }

        public ObservableCollection<CarbonRateItem> Items { get; }
        public ICollectionView ItemsView { get; }

        public string SearchTerm
        {
            get => _searchTerm;
            set
            {
                if (_searchTerm != value)
                {
                    _searchTerm = value;
                    RaisePropertyChanged();
                    ItemsView.Refresh();
                }
            }
        }

        public ICommand RecomputeCommand { get; }

        // Opening a component in the library is handled by Helpers.LibraryLink,
        // the one implementation shared by all ten sections. The command lives on
        // the breakdown line itself, so nothing is needed here.
        public ICommand ShowDetailsCommand { get; }
        public ICommand FilterCommand { get; }
        public ICommand SortCommand { get; }
        public ICommand RefreshRemoteCommand { get; }
        public ICommand AddCustomRateCommand { get; }

        private async Task WarmLoadAsync()
        {
            try
            {
                await ComputeCatalogStore.RefreshFromApiAsync(SectionKey);
                await RateLibraryStore.RefreshFromApiAsync(SectionKey);

                ComputeCatalogStore.ReloadFromDisk();
                RateLibraryStore.ReloadFromDisk();

                Rebuild();
            }
            catch { }
        }

        private async Task RefreshRemoteAsync()
        {
            try
            {
                await ComputeCatalogStore.RefreshFromApiAsync(SectionKey);
                await RateLibraryStore.RefreshFromApiAsync(SectionKey);

                ComputeCatalogStore.ReloadFromDisk();
                RateLibraryStore.ReloadFromDisk();

                Rebuild();
            }
            catch { }
        }

        private void OnLibraryChanged()
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) { Rebuild(); return; }

            if (disp.CheckAccess()) Rebuild();
            else disp.Invoke(Rebuild);
        }

        private void Rebuild()
        {
            Items.Clear();
            AppendComputeItems();
            AppendAdminRateItems();
            ItemsView.Refresh();
        }

        private bool FilterItem(object obj)
        {
            if (obj is not CarbonRateItem item) return false;
            if (string.IsNullOrWhiteSpace(SearchTerm)) return true;

            return (item.Description ?? "")
                .IndexOf(SearchTerm, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ToggleNetCostFilter()
        {
            _isNetCostFilterOn = !_isNetCostFilterOn;

            ItemsView.SortDescriptions.Clear();
            if (_isNetCostFilterOn)
            {
                ItemsView.SortDescriptions.Add(
                    new SortDescription(nameof(CarbonRateItem.NetCost), ListSortDirection.Ascending));
            }
        }

        private void CycleSort()
        {
            _currentSort = _currentSort switch
            {
                SortState.None => SortState.Overhead,
                SortState.Overhead => SortState.TotalCost,
                SortState.TotalCost => SortState.None,
                _ => SortState.None
            };

            ItemsView.SortDescriptions.Clear();

            switch (_currentSort)
            {
                case SortState.Overhead:
                    ItemsView.SortDescriptions.Add(
                        new SortDescription(nameof(CarbonRateItem.OverheadValue), ListSortDirection.Ascending));
                    break;

                case SortState.TotalCost:
                    ItemsView.SortDescriptions.Add(
                        new SortDescription(nameof(CarbonRateItem.TotalCost), ListSortDirection.Ascending));
                    break;

                default:
                    break;
            }
        }

        private void AppendComputeItems()
        {
            // Only this section: the store holds every section (see ItemsFor).
            var defs = ComputeCatalogStore.ItemsFor(SectionKey);
            if (defs == null || defs.Count == 0) return;

            int nextNo = Items.Count + 1;

            foreach (var def in defs)
            {
                if (def == null || !def.enabled) continue;

                var key = SectionNormalizer.ToSectionKey(def.section);
                if (!string.Equals(key, SectionKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var computed = _computeEngine.Compute(def);
                    var net = (double)computed.NetCost;
                    if (!(net > 0)) continue;

                    var ohp = ApplyOHP(net);

                    var breakdown = new ObservableCollection<CarbonRateBreakdownLine>();
                    foreach (var l in computed.Lines)
                    {
                        breakdown.Add(new CarbonRateBreakdownLine
                        {
                            ComponentName = $"{l.Kind}: {l.Name}",
                            Quantity = (double)l.Qty,
                            Unit = l.Unit ?? "",
                            UnitPrice = (double)l.UnitPrice,
                            TotalPrice = (double)l.Total,
                            RefName = l.Name ?? "",
                            RefKind = (l.Kind ?? "").ToString()
                        });
                    }

                    if (computed.PoPercent > 0)
                    {
                        breakdown.Add(new CarbonRateBreakdownLine
                        {
                            ComponentName = $"Compute PO/Uplift ({computed.PoPercent}%)",
                            Quantity = Convert.ToDouble(computed.PoPercent), // ✅ FIX
                            Unit = "%",
                            UnitPrice = 0,
                            TotalPrice = (double)computed.PoAmount,
                            IsTotalLine = true
                        });
                    }


                    if (computed.Warnings.Count > 0)
                    {
                        breakdown.Add(new CarbonRateBreakdownLine { ComponentName = "⚠ Warnings", IsTotalLine = true });
                        foreach (var w in computed.Warnings)
                            breakdown.Add(new CarbonRateBreakdownLine { ComponentName = $"- {w}" });
                    }

                    Items.Add(new CarbonRateItem
                    {
                        ItemNo = nextNo++,
                        Description = def.name ?? "Untitled",
                        Unit = string.IsNullOrWhiteSpace(def.outputUnit) ? "m2" : def.outputUnit!,
                        NetCost = Math.Round(net, 2),
                        OverheadValue = Math.Round(ohp.overheadVal, 0),
                        ProfitValue = Math.Round(ohp.profitVal, 0),
                        TotalCost = Math.Round(ohp.total, 0),
                        BreakdownLines = breakdown,
                        Source = "Compute"
                    });
                }
                catch { }
            }
        }

        private void AppendAdminRateItems()
        {
            var rates = RateLibraryStore.Items;
            if (rates == null || rates.Count == 0) return;

            int nextNo = Items.Count + 1;

            foreach (var r in rates)
            {
                if (r == null) continue;

                if (!string.Equals(r.SectionKey ?? "", SectionKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                var net = (double)r.NetCost;
                if (!(net > 0)) continue;

                var ohp = ApplyOHP(net);

                var breakdown = new ObservableCollection<CarbonRateBreakdownLine>();
                if (r.Breakdown != null)
                {
                    foreach (var l in r.Breakdown)
                    {
                        breakdown.Add(new CarbonRateBreakdownLine
                        {
                            ComponentName = l.ComponentName ?? "",
                            Quantity = (double)l.Quantity,
                            Unit = l.Unit ?? "",
                            UnitPrice = (double)l.UnitPrice,
                            TotalPrice = (double)(l.LineTotal != 0 ? l.LineTotal : (l.Quantity * l.UnitPrice))
                        });
                    }
                }

                // A rate ADLM published without its build-up still opens to
                // something true: one line saying what it is, rather than an
                // empty composition that looks like a fault.
                if (breakdown.Count == 0)
                {
                    breakdown.Add(new CarbonRateBreakdownLine
                    {
                        ComponentName = "Rate as published by ADLM (no build-up supplied)",
                        Quantity = 1,
                        Unit = string.IsNullOrWhiteSpace(r.Unit) ? "m2" : r.Unit!,
                        UnitPrice = net,
                        TotalPrice = net
                    });
                }

                Items.Add(new CarbonRateItem
                {
                    ItemNo = nextNo++,
                    Description = r.Description ?? "Untitled",
                    Unit = string.IsNullOrWhiteSpace(r.Unit) ? "m2" : r.Unit!,
                    NetCost = Math.Round(net, 2),
                    OverheadValue = Math.Round(ohp.overheadVal, 0),
                    ProfitValue = Math.Round(ohp.profitVal, 0),
                    TotalCost = Math.Round(ohp.total, 0),
                    BreakdownLines = breakdown,
                    Source = "AdminRate"
                });
            }
        }

        private (double overheadVal, double profitVal, double total) ApplyOHP(double netCost)
        {
            double ov = netCost * (OverheadPercent / 100);
            double pv = netCost * (ProfitPercent / 100);
            double total = netCost + ov + pv;
            return (ov, pv, total);
        }

        private double GetMaterialPrice(string name) => _helper.GetMaterialPrice(name);
        private double GetLabourRate(string name) => _helper.GetLabourRate(name);
    }
}
