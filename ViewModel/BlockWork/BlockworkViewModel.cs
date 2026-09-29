using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

using ADLMRateGen.Command;
using ADLMRateGen.Helpers;
using ADLMRateGen.Services;
using ADLMRateGen.View;
using ADLMRateGen.ViewModel.ConcreteWork;
using ADLMRateGen.ViewModel.CustomRate;

namespace ADLMRateGen.ViewModel.BlockWork
{
    public class BlockworkViewModel : ViewModelBase
    {
        private readonly GetItemsFromDB _helper;
        private readonly ConcreteViewModel _concreteViewModel;

        private const string SectionKey = SectionKeys.Blockwork;

        private readonly ComputeItemEngine _computeEngine;

        private double _overheadPercent = 10.0;
        private double _profitPercent = 25.0;
        private string _searchTerm = string.Empty;
        private object _selectedDetail;

        // Sorting / filtering helpers
        private bool _isNetCostFilterOn = false;
        private SortState _currentSort = SortState.None;

        private enum SortState { None, Overhead, TotalCost }

        public BlockworkViewModel(
            MaterialLibraryViewModel matLib,
            LabourLibraryViewModel labourLib,
            ConcreteViewModel concreteViewModel)
        {
            _helper = new GetItemsFromDB(matLib, labourLib);
            _concreteViewModel = concreteViewModel;

            _computeEngine = new ComputeItemEngine(GetMaterialPrice, GetLabourRate);

            // Material/Labour library updates
            matLib.LibraryChanged += OnLibraryChanged;
            labourLib.LibraryChanged += OnLibraryChanged;

            // Disk-first (offline-friendly)
            ComputeCatalogStore.ReloadFromDisk();
            RateLibraryStore.ReloadFromDisk();

            // API refresh (section filtered + fallback)
            _ = LoadComputeCatalogForSectionAsync();
            _ = LoadRateLibraryAsync();

            // Store change events (may come from bg thread)
            ComputeCatalogStore.Changed += OnLibraryChanged;
            RateLibraryStore.Changed += OnLibraryChanged;

            // Build initial list
            BuildBlockWorkItems();

            BlockworkCollectionView = CollectionViewSource.GetDefaultView(BlockworkItems);
            BlockworkCollectionView.Filter = FilterBlockWorkItem;

            RecomputeCommand = new DelegateCommand(_ => RecomputeAll());
            ShowDetailsCommand = new DelegateCommand(o => ShowDetails(o));
            FilterCommand = new DelegateCommand(_ => ToggleNetCostFilter());
            SortCommand = new DelegateCommand(_ => CycleSort());
            AddCustomRateCommand = new DelegateCommand(_ => OpenCustomRateEntry());

            CurrencyService.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CurrencyService.Rate) or nameof(CurrencyService.Code))
                    RecomputeAll();
            };

            UserRateEditStore.Current.OverridesChanged += (_, __) =>
            {
                void Refresh()
                {
                    var nos = BlockworkItems.Select(i => i.ItemNo).ToList();
                    foreach (var n in nos) RecomputeItemInPlace(n);
                }
                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp == null || disp.CheckAccess()) Refresh();
                else disp.BeginInvoke((Action)Refresh);
            };
        }

        public double OverheadPercent
        {
            get => _overheadPercent;
            set
            {
                if (Math.Abs(_overheadPercent - value) > 0.000001)
                {
                    _overheadPercent = value;
                    RaisePropertyChanged();
                    RecomputeAll();
                }
            }
        }

        public double ProfitPercent
        {
            get => _profitPercent;
            set
            {
                if (Math.Abs(_profitPercent - value) > 0.000001)
                {
                    _profitPercent = value;
                    RaisePropertyChanged();
                    RecomputeAll();
                }
            }
        }

        public ObservableCollection<BlockworkItem> BlockworkItems { get; } =
            new ObservableCollection<BlockworkItem>();

        public ICollectionView BlockworkCollectionView { get; private set; }

        public string SearchTerm
        {
            get => _searchTerm;
            set
            {
                if (_searchTerm != value)
                {
                    _searchTerm = value ?? string.Empty;
                    RaisePropertyChanged();
                    BlockworkCollectionView?.Refresh();
                }
            }
        }

        public object SelectedDetail
        {
            get => _selectedDetail;
            set
            {
                if (!Equals(_selectedDetail, value))
                {
                    _selectedDetail = value;
                    RaisePropertyChanged();
                }
            }
        }

        public ICommand RecomputeCommand { get; }
        public ICommand ShowDetailsCommand { get; }
        public ICommand FilterCommand { get; }
        public ICommand SortCommand { get; }
        public ICommand AddCustomRateCommand { get; }

        // -------------------- API loaders --------------------

        private async Task LoadRateLibraryAsync()
        {
            try
            {
                // disk-first already done, but safe
                RateLibraryStore.ReloadFromDisk();

                var ok = await RateLibraryStore.RefreshFromApiAsync(SectionKey);

                // fallback if section mismatch
                if (ok && RateLibraryStore.LastApiItemCount == 0)
                    await RateLibraryStore.RefreshFromApiAsync();

                OnLibraryChanged();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RateLibrary] load failed: {ex}");
            }
        }

        private async Task LoadComputeCatalogForSectionAsync()
        {
            try
            {
                var ok = await ComputeCatalogStore.RefreshFromApiAsync(SectionKey);

                // fallback if section mismatch / inconsistent naming
                if (ok && ComputeCatalogStore.LastApiItemCount == 0)
                    await ComputeCatalogStore.RefreshFromApiAsync();

                OnLibraryChanged();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ComputeCatalog] load failed: {ex}");
            }
        }

        // -------------------- UI actions --------------------

        private void ShowDetails(object o)
        {
            if (o is BlockworkItem item)
            {
                var detailedControl = new BlockworkItemDetailControl();
                detailedControl.DataContext = item;

                detailedControl.BackRequested += () => { SelectedDetail = null; };
                SelectedDetail = detailedControl;
            }
        }

        private void OpenCustomRateEntry()
        {
            var view = new CustomRateEntryView();
            view.DataContext = new CustomRateEntryViewModel();
            SelectedDetail = view;
        }

        private bool FilterBlockWorkItem(object obj)
        {
            if (obj is not BlockworkItem item) return false;
            if (string.IsNullOrWhiteSpace(SearchTerm)) return true;

            return (item.Description ?? string.Empty)
                .IndexOf(SearchTerm, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Thread-safe refresh
        private void OnLibraryChanged()
        {
            var disp = Application.Current?.Dispatcher;
            if (disp == null)
            {
                RecomputeAll();
                return;
            }

            if (disp.CheckAccess())
                RecomputeAll();
            else
                disp.Invoke(RecomputeAll);
        }

        private void RecomputeAll()
        {
            BlockworkItems.Clear();
            BuildBlockWorkItems();
            BlockworkCollectionView?.Refresh();
        }

        /// <summary>
        /// Re-runs the matching ComputeItemN method for the supplied ItemNo and updates the
        /// existing Item instance in place so the open popup keeps its DataContext binding live.
        /// </summary>
        public void RecomputeItemInPlace(int itemNo)
        {
            var existing = BlockworkItems.FirstOrDefault(i => i.ItemNo == itemNo);
            if (existing == null) return;

            Func<BlockworkItem>[] all =
            {
                ComputeItem1, ComputeItem2, ComputeItem3, ComputeItem4, ComputeItem5, ComputeItem6,
                ComputeItem7, ComputeItem8, ComputeItem9, ComputeItem10, ComputeItem11, ComputeItem12,
                ComputeItem13, ComputeItem14, ComputeItem15, ComputeItem16, ComputeItem17, ComputeItem18
            };

            BlockworkItem? fresh = null;
            foreach (var fn in all)
            {
                var candidate = fn();
                if (candidate.ItemNo == itemNo) { fresh = candidate; break; }
            }
            if (fresh == null) return;

            existing.NetCost = fresh.NetCost;
            existing.OverheadValue = fresh.OverheadValue;
            existing.ProfitValue = fresh.ProfitValue;
            existing.TotalCost = fresh.TotalCost;

            existing.BlockworkBreakdownLine.Clear();
            foreach (var line in fresh.BlockworkBreakdownLine)
                existing.BlockworkBreakdownLine.Add(line);

            BlockworkCollectionView?.Refresh();
        }

        private void ToggleNetCostFilter()
        {
            _isNetCostFilterOn = !_isNetCostFilterOn;

            BlockworkCollectionView.SortDescriptions.Clear();

            if (_isNetCostFilterOn)
            {
                BlockworkCollectionView.SortDescriptions.Add(
                    new SortDescription(nameof(BlockworkItem.NetCost), ListSortDirection.Ascending));
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

            BlockworkCollectionView.SortDescriptions.Clear();

            switch (_currentSort)
            {
                case SortState.Overhead:
                    BlockworkCollectionView.SortDescriptions.Add(
                        new SortDescription(nameof(BlockworkItem.OverheadValue), ListSortDirection.Ascending));
                    break;

                case SortState.TotalCost:
                    BlockworkCollectionView.SortDescriptions.Add(
                        new SortDescription(nameof(BlockworkItem.TotalCost), ListSortDirection.Ascending));
                    break;

                case SortState.None:
                default:
                    break;
            }
        }

        // -------------------- Build items --------------------

        private void BuildBlockWorkItems()
        {
            // 1) Built-in ComputeItem1..ComputeItem18 (reflection, so no hard refs)
            AddBuiltInItemsByReflection();

            // 2) Compute catalog pushed items (API/disk)
            AppendApiComputeItems();

            // 3) Admin custom rates (API/disk)
            AppendAdminRateItems();
        }

        private void AddBuiltInItemsByReflection()
        {
            // Finds ComputeItem1..ComputeItem99 methods returning BlockworkItem and having no parameters.
            var t = GetType();

            var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name.StartsWith("ComputeItem", StringComparison.OrdinalIgnoreCase))
                .Where(m => m.GetParameters().Length == 0)
                .Where(m => typeof(BlockworkItem).IsAssignableFrom(m.ReturnType))
                .Select(m => new
                {
                    Method = m,
                    No = ParseTrailingInt(m.Name)
                })
                .Where(x => x.No > 0)
                .OrderBy(x => x.No)
                .ToList();

            foreach (var m in methods)
            {
                try
                {
                    var item = m.Method.Invoke(this, null) as BlockworkItem;
                    if (item != null) BlockworkItems.Add(item);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Blockwork] Skip {m.Method.Name}: {ex.Message}");
                }
            }
        }

        private static int ParseTrailingInt(string name)
        {
            // ComputeItem13 -> 13
            if (string.IsNullOrWhiteSpace(name)) return 0;
            int i = name.Length - 1;
            while (i >= 0 && char.IsDigit(name[i])) i--;
            var digits = name[(i + 1)..];
            return int.TryParse(digits, out var n) ? n : 0;
        }

        private void AppendApiComputeItems()
        {
            if (_computeEngine == null) return;

            // Only this section: the store holds every section (see ItemsFor).
            var defs = ComputeCatalogStore.ItemsFor(SectionKey);
            if (defs == null || defs.Count == 0) return;

            int nextNo = BlockworkItems.Count + 1;

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
                    var ohp = ApplyOHP(net);

                    var breakdown = new ObservableCollection<BlockworkBreakdownLine>();

                    foreach (var l in computed.Lines)
                    {
                        breakdown.Add(new BlockworkBreakdownLine
                        {
                            ComponentName = $"{l.Kind}: {l.Name}",
                            Quantity = (double)l.Qty,
                            Unit = string.IsNullOrWhiteSpace(l.Unit) ? "" : l.Unit,
                            UnitPrice = (double)l.UnitPrice,
                            TotalPrice = (double)l.Total
                        });
                    }

                    if (computed.PoPercent > 0)
                    {
                        breakdown.Add(new BlockworkBreakdownLine
                        {
                            ComponentName = $"Compute PO/Uplift ({computed.PoPercent}%)",
                            Quantity = (double)computed.PoPercent,
                            Unit = "%",
                            UnitPrice = 0,
                            TotalPrice = (double)computed.PoAmount
                        });
                    }

                    if (computed.Warnings.Count > 0)
                    {
                        breakdown.Add(new BlockworkBreakdownLine { ComponentName = "⚠ Warnings", TotalPrice = 0 });
                        foreach (var w in computed.Warnings)
                            breakdown.Add(new BlockworkBreakdownLine { ComponentName = $"- {w}", TotalPrice = 0 });
                    }

                    BlockworkItems.Add(new BlockworkItem
                    {
                        ItemNo = nextNo++,
                        Description = def.name,
                        Unit = string.IsNullOrWhiteSpace(def.outputUnit) ? "m2" : def.outputUnit,
                        NetCost = Math.Round(net, 2),
                        OverheadValue = Math.Round(ohp.overheadVal, 0),
                        ProfitValue = Math.Round(ohp.profitVal, 0),
                        TotalCost = Math.Round(ohp.total, 0),
                        BlockworkBreakdownLine = breakdown
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ComputeCatalog] Skip '{def?.name}': {ex.Message}");
                }
            }
        }

        private void AppendAdminRateItems()
        {
            var rates = RateLibraryStore.Items;
            if (rates == null || rates.Count == 0) return;

            int nextNo = BlockworkItems.Count + 1;

            foreach (var r in rates)
            {
                if (r == null) continue;

                if (!string.Equals(r.SectionKey ?? "", SectionKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                var net = (double)r.NetCost;
                if (!(net > 0)) continue;

                var ohp = ApplyOHP(net);

                var breakdown = new ObservableCollection<BlockworkBreakdownLine>();
                if (r.Breakdown != null)
                {
                    foreach (var l in r.Breakdown)
                    {
                        breakdown.Add(new BlockworkBreakdownLine
                        {
                            ComponentName = l.ComponentName,
                            Quantity = (double)l.Quantity,
                            Unit = l.Unit,
                            UnitPrice = (double)l.UnitPrice,
                            TotalPrice = (double)(l.LineTotal != 0 ? l.LineTotal : (l.Quantity * l.UnitPrice))
                        });
                    }
                }

                BlockworkItems.Add(new BlockworkItem
                {
                    ItemNo = nextNo++,
                    Description = r.Description,
                    Unit = string.IsNullOrWhiteSpace(r.Unit) ? "m2" : r.Unit,
                    NetCost = Math.Round(net, 2),
                    OverheadValue = Math.Round(ohp.overheadVal, 0),
                    ProfitValue = Math.Round(ohp.profitVal, 0),
                    TotalCost = Math.Round(ohp.total, 0),
                    BlockworkBreakdownLine = breakdown
                });
            }
        }

        // -------------------- pricing helpers --------------------

        private (double overheadVal, double profitVal, double total) ApplyOHP(double netCost)
        {
            double ov = netCost * (OverheadPercent / 100);
            double pv = netCost * (ProfitPercent / 100);
            double total = netCost + ov + pv;
            return (ov, pv, total);
        }

        private double GetMaterialPrice(string name) => _helper.GetMaterialPrice(name);
        private double GetLabourRate(string name) => _helper.GetLabourRate(name);

        // -------------------- your existing utility methods (fixed) --------------------

        public double GetNetValue(Func<BlockworkItem> computeItemFunc)
        {
            if (computeItemFunc == null) return 0d;

            try
            {
                var item = computeItemFunc();
                return item?.NetCost ?? 0d;
            }
            catch
            {
                return 0d;
            }
        }

        // ✅ FIX: ConcreteViewModel no longer takes Func<ConcreteworkItem>,
        // so just evaluate the func and return NetCost.
        public double GetConcreteNetValue(Func<ConcreteworkItem> computeFunc)
        {
            if (computeFunc == null) return 0d;

            try
            {
                var item = computeFunc();
                return item?.NetCost ?? 0d;
            }
            catch
            {
                return 0d;
            }
        }

        // ✅ OPTIONAL overloads (use these if you want to query concrete by itemNo/description)
        public double GetConcreteNetValue(int itemNo)
        {
            try { return _concreteViewModel?.GetConcreteNetValue(itemNo) ?? 0d; }
            catch { return 0d; }
        }

        public double GetConcreteNetValue(string descriptionContains, string unit = null)
        {
            try { return _concreteViewModel?.GetConcreteNetValue(descriptionContains, unit) ?? 0d; }
            catch { return 0d; }
        }

        // ✅ FIX: this was wrongly typed as Func<ConcreteworkItem> in your screenshot.
        // Blockwork net value should use BlockworkItem.
        public double GetBlockworkNetValue(Func<BlockworkItem> computeFunc)
        {
            if (computeFunc == null) return 0d;

            try
            {
                var item = computeFunc();
                return item?.NetCost ?? 0d;
            }
            catch
            {
                return 0d;
            }
        }


        // -------------------- YOUR EXISTING ComputeItem1..ComputeItem18 METHODS GO HERE --------------------

        #region Compute Methods
        private BlockworkItem ComputeItem1()
        {
            double mixerQty = UserRateEditStore.Current.Qty(SectionKey, 1, "Concrete mixer 10/7", 1);
            double mixerCost = GetLabourRate("Concrete mixer 10/7");
            double mixerLineTotal = mixerCost * mixerQty;

            double dieselPrice = GetMaterialPrice("Diesel");
            double literPerDay = UserRateEditStore.Current.Qty(SectionKey, 1, "Fuel (Diesel)", 40);
            double fuelCost = dieselPrice * literPerDay;

            double oilPct = UserRateEditStore.Current.Qty(SectionKey, 1, "Oil and consumables (per day)", 3);
            double oilCost = (oilPct / 100.0) * fuelCost;

            double operatorQty = UserRateEditStore.Current.Qty(SectionKey, 1, "Operator (per day)", 1);
            double operatorCost = GetLabourRate("Heavy plant operator") * 1.4;
            double operatorTotal = operatorCost * operatorQty;

            double totalPlantDay = mixerLineTotal + fuelCost + oilCost + operatorTotal;

            double workHr = 8;
            double costPerHr = totalPlantDay / workHr;

            double volPerHr = 5.66;
            double netCostPerm3 = costPerHr / volPerHr;
            var ohp = ApplyOHP(netCostPerm3);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
                new BlockworkBreakdownLine { ComponentName="Concrete mixer 10/7", Quantity=mixerQty, Unit="N/day", UnitPrice=mixerCost, TotalPrice=mixerLineTotal },
                new BlockworkBreakdownLine { ComponentName="Fuel (Diesel)", Quantity=literPerDay, Unit="hr/m3", UnitPrice=dieselPrice, TotalPrice=fuelCost },
                new BlockworkBreakdownLine { ComponentName="Oil and consumables (per day)", Quantity=oilPct, Unit="%", TotalPrice=oilCost },
                new BlockworkBreakdownLine { ComponentName="Operator (per day)", Quantity=operatorQty, Unit="Nr/Day", UnitPrice=operatorCost, TotalPrice=operatorTotal },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Cost per day", Quantity=1, Unit="", TotalPrice=totalPlantDay },
                new BlockworkBreakdownLine { ComponentName="Cost per hour (8 hour Working Day)", Quantity=workHr, Unit="N/Hr", UnitPrice=totalPlantDay, TotalPrice=costPerHr },
                new BlockworkBreakdownLine { ComponentName="Total", Quantity=volPerHr, Unit="m3", TotalPrice=netCostPerm3 },
            };

            return new BlockworkItem
            {
                ItemNo = 1,
                Description = "Calculating plant and labour cost for mixing concrete or mortar, using 14/10 mixer. Note mixer is running a 3 minute circle prior to pouring out mix.",
                Unit = "m3",
                NetCost = Math.Round(netCostPerm3, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        public BlockworkItem ComputeItem2()
        {
            //MATERIAL COST
            double cementPrice = GetMaterialPrice("Cement (50kg bag)");
            double cementLoadingPrice = GetMaterialPrice("Loading and unloading cement");
            double sandPrice = GetMaterialPrice("Sharp Sand");

            double cementPerM3 = UserRateEditStore.Current.Qty(SectionKey, 2, "Cement", 28.82);
            double sandPerM3 = UserRateEditStore.Current.Qty(SectionKey, 2, "Sand", 3);
            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 2, "Add for waste.", 25);

            double cementCost = cementPerM3 * cementPrice;
            double cementLoadingCost = cementLoadingPrice * cementPerM3;
            double sandCost = sandPerM3 * sandPrice;

            double totalMaterialCost = cementCost + cementLoadingCost + sandCost;
            double waste = totalMaterialCost * (wastePer / 100);
            double finalMaterialCost = totalMaterialCost + waste;

            // Dry material shrinks when mixed: a 4 m3 batch yields about 2.96 m3 of mortar.
            double materialCostPerCum = finalMaterialCost / MortarMix.WetYield(4);

            //LABOUR COST
            double mixerCost = GetLabourRate("Concrete mixer 10/7");
            double dieselPrice = GetMaterialPrice("Diesel");
            double literPerDay = 30;
            double fuelCost = dieselPrice * literPerDay;
            double operatorCost = GetLabourRate("Heavy plant operator") * 1.4;

            double totalPlantDay = mixerCost + fuelCost +
                (0.03 * fuelCost) + (2 * operatorCost);

            double workHr = 8;
            double costPerHr = totalPlantDay / workHr;

            double volPerHr = 5.66;
            double netCostPerUseRaw = costPerHr / volPerHr;

            double plantLabourQty = UserRateEditStore.Current.Qty(SectionKey, 2, "Cost of plant and labour as before calculated.", 1);
            double netCostPerUse = netCostPerUseRaw * plantLabourQty;

            double netCostPerm3 = materialCostPerCum + netCostPerUse;

            var ohp = ApplyOHP(netCostPerm3);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="Cement", Quantity=cementPerM3, Unit="bag/m3", UnitPrice=cementPrice, TotalPrice=cementCost },
                new BlockworkBreakdownLine { ComponentName="Loading and Unloading cement", Quantity=cementPerM3, Unit="bag/m3", UnitPrice=cementLoadingPrice, TotalPrice=cementLoadingCost },
                new BlockworkBreakdownLine { ComponentName="Sand", Quantity=sandPerM3, Unit="m3", UnitPrice=sandPrice, TotalPrice=sandCost},
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material", Quantity=1, Unit="", TotalPrice=totalMaterialCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material", Quantity=1, Unit="", TotalPrice=finalMaterialCost },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material cost per m3", Quantity=1, Unit="m3", TotalPrice=materialCostPerCum },

				//MIXING
				new BlockworkBreakdownLine { ComponentName="Cost of plant and labour as before calculated.", Quantity=plantLabourQty, Unit="", TotalPrice=netCostPerUse },


                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m3", TotalPrice=netCostPerm3 },
            };

            return new BlockworkItem
            {
                ItemNo = 2,
                Description = "Mortar Mix (1:3)",
                Unit = "m3",
                NetCost = Math.Round(netCostPerm3, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        public BlockworkItem ComputeItem3()
        {
            //MATERIAL COST
            double cementPrice = GetMaterialPrice("Cement (50kg bag)");
            double cementLoadingPrice = GetMaterialPrice("Loading and unloading cement");
            double sandPrice = GetMaterialPrice("Sharp Sand");

            double cementPerM3 = UserRateEditStore.Current.Qty(SectionKey, 3, "Cement", 28.82);
            double sandPerM3 = UserRateEditStore.Current.Qty(SectionKey, 3, "Sand", 4);
            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 3, "Add for waste.", 25);

            double cementCost = cementPerM3 * cementPrice;
            double cementLoadingCost = cementLoadingPrice * cementPerM3;
            double sandCost = sandPerM3 * sandPrice;

            double totalMaterialCost = cementCost + cementLoadingCost + sandCost;
            double waste = totalMaterialCost * (wastePer / 100);
            double finalMaterialCost = totalMaterialCost + waste;

            // Dry material shrinks when mixed: a 5 m3 batch yields about 3.70 m3 of mortar.
            double materialCostPerCum = finalMaterialCost / MortarMix.WetYield(5);

            //LABOUR COST
            double mixerCost = GetLabourRate("Concrete mixer 10/7");
            double dieselPrice = GetMaterialPrice("Diesel");
            double literPerDay = 30;
            double fuelCost = dieselPrice * literPerDay;
            double operatorCost = GetLabourRate("Heavy plant operator") * 1.4;

            double totalPlantDay = mixerCost + fuelCost +
                (0.03 * fuelCost) + (2 * operatorCost);

            double workHr = 8;
            double costPerHr = totalPlantDay / workHr;

            double volPerHr = 5.66;
            double netCostPerUseRaw = costPerHr / volPerHr;

            double plantLabourQty = UserRateEditStore.Current.Qty(SectionKey, 3, "Cost of plant and labour as before calculated.", 1);
            double netCostPerUse = netCostPerUseRaw * plantLabourQty;

            double netCostPerm3 = materialCostPerCum + netCostPerUse;

            var ohp = ApplyOHP(netCostPerm3);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="Cement", Quantity=cementPerM3, Unit="bag/m3", UnitPrice=cementPrice, TotalPrice=cementCost },
                new BlockworkBreakdownLine { ComponentName="Loading and Unloading cement", Quantity=cementPerM3, Unit="bag/m3", UnitPrice=cementLoadingPrice, TotalPrice=cementLoadingCost },
                new BlockworkBreakdownLine { ComponentName="Sand", Quantity=sandPerM3, Unit="m3", UnitPrice=sandPrice, TotalPrice=sandCost},
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material", Quantity=1, Unit="", TotalPrice=totalMaterialCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material", Quantity=1, Unit="", TotalPrice=finalMaterialCost },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material cost per m3", Quantity=1, Unit="m3", TotalPrice=materialCostPerCum },

				//MIXING
				new BlockworkBreakdownLine { ComponentName="Cost of plant and labour as before calculated.", Quantity=plantLabourQty, Unit="", TotalPrice=netCostPerUse },


                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m3", TotalPrice=netCostPerm3 },
            };

            return new BlockworkItem
            {
                ItemNo = 3,
                Description = "Mortar Mix (1:4)",
                Unit = "m3",
                NetCost = Math.Round(netCostPerm3, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        public BlockworkItem ComputeItem4()
        {
            //MATERIAL COST
            double cementPrice = GetMaterialPrice("Cement (50kg bag)");
            double cementLoadingPrice = GetMaterialPrice("Loading and unloading cement");
            double sandPrice = GetMaterialPrice("Sharp Sand");

            double cementPerM3 = UserRateEditStore.Current.Qty(SectionKey, 4, "Cement", 28.82);
            double sandPerM3 = UserRateEditStore.Current.Qty(SectionKey, 4, "Sand", 6);
            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 4, "Add for waste.", 25);

            double cementCost = cementPerM3 * cementPrice;
            double cementLoadingCost = cementLoadingPrice * cementPerM3;
            double sandCost = sandPerM3 * sandPrice;

            double totalMaterialCost = cementCost + cementLoadingCost + sandCost;
            double waste = totalMaterialCost * (wastePer / 100);
            double finalMaterialCost = totalMaterialCost + waste;

            // Dry material shrinks when mixed: a 7 m3 batch yields about 5.19 m3 of mortar.
            double materialCostPerCum = finalMaterialCost / MortarMix.WetYield(7);

            //LABOUR COST
            double mixerCost = GetLabourRate("Concrete mixer 10/7");
            double dieselPrice = GetMaterialPrice("Diesel");
            double literPerDay = 30;
            double fuelCost = dieselPrice * literPerDay;
            double operatorCost = GetLabourRate("Heavy plant operator") * 1.4;

            double totalPlantDay = mixerCost + fuelCost +
                (0.03 * fuelCost) + (2 * operatorCost);

            double workHr = 8;
            double costPerHr = totalPlantDay / workHr;

            double volPerHr = 5.66;
            double netCostPerUseRaw = costPerHr / volPerHr;

            double plantLabourQty = UserRateEditStore.Current.Qty(SectionKey, 4, "Cost of plant and labour as before calculated.", 1);
            double netCostPerUse = netCostPerUseRaw * plantLabourQty;

            double netCostPerm3 = materialCostPerCum + netCostPerUse;

            var ohp = ApplyOHP(netCostPerm3);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="Cement", Quantity=cementPerM3, Unit="bag/m3", UnitPrice=cementPrice, TotalPrice=cementCost },
                new BlockworkBreakdownLine { ComponentName="Loading and Unloading cement", Quantity=cementPerM3, Unit="bag/m3", UnitPrice=cementLoadingPrice, TotalPrice=cementLoadingCost },
                new BlockworkBreakdownLine { ComponentName="Sand", Quantity=sandPerM3, Unit="m3", UnitPrice=sandPrice, TotalPrice=sandCost},
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material", Quantity=1, Unit="", TotalPrice=totalMaterialCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material", Quantity=1, Unit="", TotalPrice=finalMaterialCost },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material cost per m3", Quantity=1, Unit="m3", TotalPrice=materialCostPerCum },

				//MIXING
				new BlockworkBreakdownLine { ComponentName="Cost of plant and labour as before calculated.", Quantity=plantLabourQty, Unit="", TotalPrice=netCostPerUse },


                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m3", TotalPrice=netCostPerm3 },
            };

            return new BlockworkItem
            {
                ItemNo = 4,
                Description = "Mortar Mix (1:6)",
                Unit = "m3",
                NetCost = Math.Round(netCostPerm3, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        public BlockworkItem ComputeItem5()
        {
            //MATERIAL COST
            double cementPrice = GetMaterialPrice("Cement (50kg bag)");
            double cementLoadingPrice = GetMaterialPrice("Loading and unloading cement");
            double sandPrice = GetMaterialPrice("Sharp Sand");

            double cementPerM3 = UserRateEditStore.Current.Qty(SectionKey, 5, "Cement", 28.82);
            double sandPerM3 = UserRateEditStore.Current.Qty(SectionKey, 5, "Sand", 1);
            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 5, "Add for waste.", 25);

            double cementCost = cementPerM3 * cementPrice;
            double cementLoadingCost = cementLoadingPrice * cementPerM3;
            double sandCost = sandPerM3 * sandPrice;

            double totalMaterialCost = cementCost + cementLoadingCost + sandCost;
            double waste = totalMaterialCost * (wastePer / 100);
            double finalMaterialCost = totalMaterialCost + waste;

            // Dry material shrinks when mixed: a 2 m3 batch yields about 1.48 m3 of mortar.
            double materialCostPerCum = finalMaterialCost / MortarMix.WetYield(2);

            //LABOUR COST
            double mixerCost = GetLabourRate("Concrete mixer 10/7");
            double dieselPrice = GetMaterialPrice("Diesel");
            double literPerDay = 30;
            double fuelCost = dieselPrice * literPerDay;
            double operatorCost = GetLabourRate("Heavy plant operator") * 1.4;

            double totalPlantDay = mixerCost + fuelCost +
                (0.03 * fuelCost) + (2 * operatorCost);

            double workHr = 8;
            double costPerHr = totalPlantDay / workHr;

            double volPerHr = 5.66;
            double netCostPerUseRaw = costPerHr / volPerHr;

            double plantLabourQty = UserRateEditStore.Current.Qty(SectionKey, 5, "Cost of plant and labour as before calculated.", 1);
            double netCostPerUse = netCostPerUseRaw * plantLabourQty;

            double netCostPerm3 = materialCostPerCum + netCostPerUse;

            var ohp = ApplyOHP(netCostPerm3);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="Cement", Quantity=cementPerM3, Unit="bag/m3", UnitPrice=cementPrice, TotalPrice=cementCost },
                new BlockworkBreakdownLine { ComponentName="Loading and Unloading cement", Quantity=cementPerM3, Unit="bag/m3", UnitPrice=cementLoadingPrice, TotalPrice=cementLoadingCost },
                new BlockworkBreakdownLine { ComponentName="Sand", Quantity=sandPerM3, Unit="m3", UnitPrice=sandPrice, TotalPrice=sandCost},
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material", Quantity=1, Unit="", TotalPrice=totalMaterialCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material", Quantity=1, Unit="", TotalPrice=finalMaterialCost },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material cost per m3", Quantity=1, Unit="m3", TotalPrice=materialCostPerCum },

				//MIXING
				new BlockworkBreakdownLine { ComponentName="Cost of plant and labour as before calculated.", Quantity=plantLabourQty, Unit="", TotalPrice=netCostPerUse },


                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m3", TotalPrice=netCostPerm3 },
            };

            return new BlockworkItem
            {
                ItemNo = 5,
                Description = "Mortar Mix (1:1)",
                Unit = "m3",
                NetCost = Math.Round(netCostPerm3, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        private BlockworkItem ComputeItem6()
        {
            //MATERIAL COST
            double cementPrice = GetMaterialPrice("Cement (50kg bag)");
            double cementLoadingPrice = GetMaterialPrice("Loading and unloading cement");
            double sandPrice = GetMaterialPrice("Sharp Sand");

            double cementPerM3 = UserRateEditStore.Current.Qty(SectionKey, 6, "Cement", 28.82);
            double sandPerM3 = UserRateEditStore.Current.Qty(SectionKey, 6, "Sand", 12);
            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 6, "Add for waste.", 25);

            double cementCost = cementPerM3 * cementPrice;
            double cementLoadingCost = cementLoadingPrice * cementPerM3;
            double sandCost = sandPerM3 * sandPrice;

            double totalMaterialCost = cementCost + cementLoadingCost + sandCost;
            double waste = totalMaterialCost * (wastePer / 100);
            double finalMaterialCost = totalMaterialCost + waste;

            // Dry material shrinks when mixed: a 13 m3 batch yields about 9.63 m3 of mortar.
            double materialCostPerCum = finalMaterialCost / MortarMix.WetYield(13);

            //LABOUR COST
            double mixerCost = GetLabourRate("Concrete mixer 10/7");
            double dieselPrice = GetMaterialPrice("Diesel");
            double literPerDay = 30;
            double fuelCost = dieselPrice * literPerDay;
            double operatorCost = GetLabourRate("Heavy plant operator") * 1.4;

            double totalPlantDay = mixerCost + fuelCost +
                (0.03 * fuelCost) + (2 * operatorCost);

            double workHr = 8;
            double costPerHr = totalPlantDay / workHr;

            double volPerHr = 5.66;
            double netCostPerUseRaw = costPerHr / volPerHr;

            double plantLabourQty = UserRateEditStore.Current.Qty(SectionKey, 6, "Cost of plant and labour as before calculated.", 1);
            double netCostPerUse = netCostPerUseRaw * plantLabourQty;

            double netCostPerm3 = materialCostPerCum + netCostPerUse;

            var ohp = ApplyOHP(netCostPerm3);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="Cement", Quantity=cementPerM3, Unit="bag/m3", UnitPrice=cementPrice, TotalPrice=cementCost },
                new BlockworkBreakdownLine { ComponentName="Loading and Unloading cement", Quantity=cementPerM3, Unit="bag/m3", UnitPrice=cementLoadingPrice, TotalPrice=cementLoadingCost },
                new BlockworkBreakdownLine { ComponentName="Sand", Quantity=sandPerM3, Unit="m3", UnitPrice=sandPrice, TotalPrice=sandCost},
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material", Quantity=1, Unit="", TotalPrice=totalMaterialCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material", Quantity=1, Unit="", TotalPrice=finalMaterialCost },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material cost per m3", Quantity=1, Unit="m3", TotalPrice=materialCostPerCum },

				//MIXING
				new BlockworkBreakdownLine { ComponentName="Cost of plant and labour as before calculated.", Quantity=plantLabourQty, Unit="", TotalPrice=netCostPerUse },


                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m3", TotalPrice=netCostPerm3 },
            };

            return new BlockworkItem
            {
                ItemNo = 6,
                Description = "Mortar Mix (1:12)",
                Unit = "m3",
                NetCost = Math.Round(netCostPerm3, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        private BlockworkItem ComputeItem7()
        {
            //MATERIAL COST
            double blockPrice = GetMaterialPrice("225 x 225 x 450mm (9 x 9 x 18\") Hollow blocks");
            double blockLoadingPrice = GetMaterialPrice("Loading and unloading blocks");

            double blockPerM2 = UserRateEditStore.Current.Qty(SectionKey, 7, "Blocks per square meter.", 10);
            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 7, "Add for waste.", 10);

            double blockCost = blockPrice * blockPerM2;
            double blockLoadingCost = blockLoadingPrice * blockPerM2;

            double totalMaterialCost = blockCost;
            double waste = totalMaterialCost * (wastePer / 100);
            double finalMaterialCost = totalMaterialCost + waste+ blockLoadingCost;

            double mortarCost = GetNetValue(ComputeItem4);
            double mortarQty = UserRateEditStore.Current.Qty(SectionKey, 7, "Mortar per square meter", MortarMix.PerSqM225);
            double totalMortarCost = mortarCost * mortarQty;
            double mortarWastePer = UserRateEditStore.Current.Qty(SectionKey, 7, "Add for waste.", 5);
            double mortarWaste = totalMortarCost * (mortarWastePer / 100);
            double finalCost = totalMortarCost + mortarWaste;

            //LABOUR COST
            double masonCost = GetLabourRate("Skilled/Artisan") * 1.4;
            double labourCost = GetLabourRate("Labourer") * 1.4;

            double masonQty = UserRateEditStore.Current.Qty(SectionKey, 7, "Masons", 3);
            double labourQty = UserRateEditStore.Current.Qty(SectionKey, 7, "Labour", 2);

            double masonPrice = masonCost * masonQty;
            double labourPrice = labourCost * labourQty;
            double totalLabourCost = masonPrice + labourPrice;

            double outputPerDay = 140;
            double areaPerDay = outputPerDay / blockPerM2;
            double labourCostPerM2 = totalLabourCost/areaPerDay;

            double netCostPerm2 = finalMaterialCost+finalCost+labourCostPerM2;

            var ohp = ApplyOHP(netCostPerm2);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="Blocks per square meter.", Quantity=blockPerM2, Unit="No", UnitPrice=blockPrice, TotalPrice=blockCost },
                new BlockworkBreakdownLine { ComponentName="Loading and unloading blocks", Quantity=blockPerM2, Unit="No", UnitPrice=blockLoadingPrice, TotalPrice=blockLoadingCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Block", Quantity=1, Unit="", TotalPrice=finalMaterialCost },

                new BlockworkBreakdownLine { ComponentName="Mortar per square meter", Quantity=mortarQty, Unit="m3/m2", UnitPrice=mortarCost, TotalPrice=totalMortarCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=mortarWastePer, Unit="%", TotalPrice=mortarWaste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Mortar", Quantity=1, Unit="", TotalPrice=finalCost },

                new BlockworkBreakdownLine { ComponentName="Masons", Quantity=masonQty, Unit="per day", UnitPrice=masonCost, TotalPrice=masonPrice },
                new BlockworkBreakdownLine { ComponentName="Labour", Quantity=labourQty, Unit="per day", UnitPrice=labourCost, TotalPrice=labourPrice },
                new BlockworkBreakdownLine { ComponentName="Labour cost per day", Quantity=1, Unit="per day", TotalPrice=totalLabourCost },

                new BlockworkBreakdownLine { ComponentName="Sub-total: Output 140 blocks per day @ 10 blocks/m2", Quantity=areaPerDay, Unit="m2", UnitPrice=totalLabourCost, TotalPrice=labourCostPerM2 },

                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m2", TotalPrice=netCostPerm2 },
            };

            return new BlockworkItem
            {
                ItemNo = 7,
                Description = "225mm blockwall in cement and sand mortar (1:6)",
                Unit = "m2",
                NetCost = Math.Round(netCostPerm2, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        private BlockworkItem ComputeItem8()
        {
            double concreteFillingQty = UserRateEditStore.Current.Qty(SectionKey, 8, "Concrete filling in 225mm blockwall", 0.1030);
            double concreteCost = GetConcreteNetValue(_concreteViewModel.ComputeItem3);
            double concreteTotal = concreteFillingQty * concreteCost;

            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 8, "Add for waste.", 2.5);
            double waste = concreteTotal * (wastePer / 100);
            double finalMaterialCost = concreteTotal + waste;

            //LABOUR COST
            double labourCost = (GetLabourRate("Labourer")/8) * 1.4;
            double labourDuration = UserRateEditStore.Current.Qty(SectionKey, 8, "Labour filling 225mm blockwall", 0.5);
            double labourPrice = labourCost * labourDuration;

            double netFillingCost = finalMaterialCost + labourPrice;
            double blockCost = GetNetValue(ComputeItem7);

            double netCostPerm2 = netFillingCost + blockCost;

            var ohp = ApplyOHP(netCostPerm2);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="Concrete filling in 225mm blockwall", Quantity=concreteFillingQty, Unit="m3/m2", UnitPrice=concreteCost, TotalPrice=concreteTotal },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material", Quantity=1, Unit="", TotalPrice=finalMaterialCost },

                new BlockworkBreakdownLine { ComponentName="Labour filling 225mm blockwall", Quantity=labourDuration, Unit="hr/m2", UnitPrice=labourCost, TotalPrice=labourPrice },

                new BlockworkBreakdownLine { ComponentName="Sub-total: Filling Cost", Quantity=1, Unit="", TotalPrice=netFillingCost },

                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m2", TotalPrice=netCostPerm2 },
            };

            return new BlockworkItem
            {
                ItemNo = 8,
                Description = "Concrete filling in 225mm blockwall",
                Unit = "m2",
                NetCost = Math.Round(netCostPerm2, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };

        }
        private BlockworkItem ComputeItem9()
        {
            //MATERIAL COST
            double blockPrice = GetMaterialPrice("150 x 225 x 450mm (6 x 9 x 18\") Hollow blocks");
            double blockLoadingPrice = GetMaterialPrice("Loading and unloading blocks");

            double blockPerM2 = UserRateEditStore.Current.Qty(SectionKey, 9, "Blocks per square meter.", 10);
            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 9, "Add for waste.", 10);

            double blockCost = blockPrice * blockPerM2;
            double blockLoadingCost = blockLoadingPrice * blockPerM2;

            double totalMaterialCost = blockCost;
            double waste = totalMaterialCost * (wastePer / 100);
            double finalMaterialCost = totalMaterialCost + waste + blockLoadingCost;

            double mortarCost = GetNetValue(ComputeItem4);
            double mortarQty = UserRateEditStore.Current.Qty(SectionKey, 9, "Mortar per square meter", MortarMix.PerSqM150);
            double totalMortarCost = mortarCost * mortarQty;
            double mortarWastePer = UserRateEditStore.Current.Qty(SectionKey, 9, "Add for waste.", 5);
            double mortarWaste = totalMortarCost * (mortarWastePer / 100);
            double finalCost = totalMortarCost + mortarWaste;

            //LABOUR COST
            double masonCost = GetLabourRate("Skilled/Artisan") * 1.4;
            double labourCost = GetLabourRate("Labourer") * 1.4;

            double masonQty = UserRateEditStore.Current.Qty(SectionKey, 9, "Masons", 3);
            double labourQty = UserRateEditStore.Current.Qty(SectionKey, 9, "Labour", 2);

            double masonPrice = masonCost * masonQty;
            double labourPrice = labourCost * labourQty;
            double totalLabourCost = masonPrice + labourPrice;

            double outputPerDay = 160;
            double areaPerDay = outputPerDay / blockPerM2;
            double labourCostPerM2 = totalLabourCost / areaPerDay;

            double netCostPerm2 = finalMaterialCost + finalCost + labourCostPerM2;

            var ohp = ApplyOHP(netCostPerm2);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="Blocks per square meter.", Quantity=blockPerM2, Unit="No", UnitPrice=blockPrice, TotalPrice=blockCost },
                new BlockworkBreakdownLine { ComponentName="Loading and unloading blocks", Quantity=blockPerM2, Unit="No", UnitPrice=blockLoadingPrice, TotalPrice=blockLoadingCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Block", Quantity=1, Unit="", TotalPrice=finalMaterialCost },

                new BlockworkBreakdownLine { ComponentName="Mortar per square meter", Quantity=mortarQty, Unit="m3/m2", UnitPrice=mortarCost, TotalPrice=totalMortarCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=mortarWastePer, Unit="%", TotalPrice=mortarWaste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Mortar", Quantity=1, Unit="", TotalPrice=finalCost },

                new BlockworkBreakdownLine { ComponentName="Masons", Quantity=masonQty, Unit="per day", UnitPrice=masonCost, TotalPrice=masonPrice },
                new BlockworkBreakdownLine { ComponentName="Labour", Quantity=labourQty, Unit="per day", UnitPrice=labourCost, TotalPrice=labourPrice },
                new BlockworkBreakdownLine { ComponentName="Labour cost per day", Quantity=1, Unit="per day", TotalPrice=totalLabourCost },

                new BlockworkBreakdownLine { ComponentName="Sub-total: Output 160 blocks per day @ 10 blocks/m2", Quantity=areaPerDay, Unit="m2", UnitPrice=totalLabourCost, TotalPrice=labourCostPerM2 },

                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m2", TotalPrice=netCostPerm2 },
            };

            return new BlockworkItem
            {
                ItemNo = 9,
                Description = "150mm blockwall in cement and sand mortar (1:6)",
                Unit = "m2",
                NetCost = Math.Round(netCostPerm2, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        private BlockworkItem ComputeItem10()
        {
            double concreteFillingQty = UserRateEditStore.Current.Qty(SectionKey, 10, "Concrete filling in 150mm blockwall", 0.0618);
            double concreteCost = GetConcreteNetValue(_concreteViewModel.ComputeItem3);
            double concreteTotal = concreteFillingQty * concreteCost;

            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 10, "Add for waste.", 2.5);
            double waste = concreteTotal * (wastePer / 100);
            double finalMaterialCost = concreteTotal + waste;

            //LABOUR COST
            double labourCost = (GetLabourRate("Labourer") / 8) * 1.4;
            double labourDuration = UserRateEditStore.Current.Qty(SectionKey, 10, "Labour filling 150mm blockwall", 0.33);
            double labourPrice = labourCost * labourDuration;

            double netFillingCost = finalMaterialCost + labourPrice;
            double blockCost = GetNetValue(ComputeItem7);

            double netCostPerm2 = netFillingCost + blockCost;

            var ohp = ApplyOHP(netCostPerm2);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="Concrete filling in 150mm blockwall", Quantity=concreteFillingQty, Unit="m3/m2", UnitPrice=concreteCost, TotalPrice=concreteTotal },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material", Quantity=1, Unit="", TotalPrice=finalMaterialCost },

                new BlockworkBreakdownLine { ComponentName="Labour filling 150mm blockwall", Quantity=labourDuration, Unit="hr/m2", UnitPrice=labourCost, TotalPrice=labourPrice },

                new BlockworkBreakdownLine { ComponentName="Sub-total: Filling Cost", Quantity=1, Unit="", TotalPrice=netFillingCost },

                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m2", TotalPrice=netCostPerm2 },
            };

            return new BlockworkItem
            {
                ItemNo = 10,
                Description = "Concrete filling in 150mm blockwall",
                Unit = "m2",
                NetCost = Math.Round(netCostPerm2, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        private BlockworkItem ComputeItem11()
        {
            //MATERIAL COST
            double blockPrice = GetMaterialPrice("100 x 225 x 450mm (4 x 9 x 18\") Hollow blocks");
            double blockLoadingPrice = GetMaterialPrice("Loading and unloading blocks");

            double blockPerM2 = UserRateEditStore.Current.Qty(SectionKey, 11, "Blocks per square meter.", 10);
            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 11, "Add for waste.", 10);

            double blockCost = blockPrice * blockPerM2;
            double blockLoadingCost = blockLoadingPrice * blockPerM2;

            double totalMaterialCost = blockCost;
            double waste = totalMaterialCost * (wastePer / 100);
            double finalMaterialCost = totalMaterialCost + waste + blockLoadingCost;

            double mortarCost = GetNetValue(ComputeItem4);
            double mortarQty = UserRateEditStore.Current.Qty(SectionKey, 11, "Mortar per square meter", MortarMix.PerSqM100);
            double totalMortarCost = mortarCost * mortarQty;
            double mortarWastePer = UserRateEditStore.Current.Qty(SectionKey, 11, "Add for waste.", 5);
            double mortarWaste = totalMortarCost * (mortarWastePer / 100);
            double finalCost = totalMortarCost + mortarWaste;

            //LABOUR COST
            double masonCost = GetLabourRate("Skilled/Artisan") * 1.4;
            double labourCost = GetLabourRate("Labourer") * 1.4;

            double masonQty = UserRateEditStore.Current.Qty(SectionKey, 11, "Masons", 3);
            double labourQty = UserRateEditStore.Current.Qty(SectionKey, 11, "Labour", 2);

            double masonPrice = masonCost * masonQty;
            double labourPrice = labourCost * labourQty;
            double totalLabourCost = masonPrice + labourPrice;

            double outputPerDay = 200;
            double areaPerDay = outputPerDay / blockPerM2;
            double labourCostPerM2 = totalLabourCost / areaPerDay;

            double netCostPerm2 = finalMaterialCost + finalCost + labourCostPerM2;

            var ohp = ApplyOHP(netCostPerm2);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="Blocks per square meter.", Quantity=blockPerM2, Unit="No", UnitPrice=blockPrice, TotalPrice=blockCost },
                new BlockworkBreakdownLine { ComponentName="Loading and unloading blocks", Quantity=blockPerM2, Unit="No", UnitPrice=blockLoadingPrice, TotalPrice=blockLoadingCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Block", Quantity=1, Unit="", TotalPrice=finalMaterialCost },

                new BlockworkBreakdownLine { ComponentName="Mortar per square meter", Quantity=mortarQty, Unit="m3/m2", UnitPrice=mortarCost, TotalPrice=totalMortarCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=mortarWastePer, Unit="%", TotalPrice=mortarWaste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Mortar", Quantity=1, Unit="", TotalPrice=finalCost },

                new BlockworkBreakdownLine { ComponentName="Masons", Quantity=masonQty, Unit="per day", UnitPrice=masonCost, TotalPrice=masonPrice },
                new BlockworkBreakdownLine { ComponentName="Labour", Quantity=labourQty, Unit="per day", UnitPrice=labourCost, TotalPrice=labourPrice },
                new BlockworkBreakdownLine { ComponentName="Labour cost per day", Quantity=1, Unit="per day", TotalPrice=totalLabourCost },

                new BlockworkBreakdownLine { ComponentName="Sub-total: Output 160 blocks per day @ 10 blocks/m2", Quantity=areaPerDay, Unit="m2", UnitPrice=totalLabourCost, TotalPrice=labourCostPerM2 },

                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m2", TotalPrice=netCostPerm2 },
            };

            return new BlockworkItem
            {
                ItemNo = 11,
                Description = "100mm blockwall in cement and sand mortar (1:6)",
                Unit = "m2",
                NetCost = Math.Round(netCostPerm2, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        private BlockworkItem ComputeItem12()
        {
            double concreteFillingQty = UserRateEditStore.Current.Qty(SectionKey, 12, "Concrete filling in 100mm blockwall", 0.0442);
            double concreteCost = GetConcreteNetValue(_concreteViewModel.ComputeItem3);
            double concreteTotal = concreteFillingQty * concreteCost;

            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 12, "Add for waste.", 2.5);
            double waste = concreteTotal * (wastePer / 100);
            double finalMaterialCost = concreteTotal + waste;

            //LABOUR COST
            double labourCost = (GetLabourRate("Labourer") / 8) * 1.4;
            double labourDuration = UserRateEditStore.Current.Qty(SectionKey, 12, "Labour filling 100mm blockwall", 0.25);
            double labourPrice = labourCost * labourDuration;

            double netFillingCost = finalMaterialCost + labourPrice;
            double blockCost = GetNetValue(ComputeItem7);

            double netCostPerm2 = netFillingCost + blockCost;

            var ohp = ApplyOHP(netCostPerm2);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="Concrete filling in 100mm blockwall", Quantity=concreteFillingQty, Unit="m3/m2", UnitPrice=concreteCost, TotalPrice=concreteTotal },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material", Quantity=1, Unit="", TotalPrice=finalMaterialCost },

                new BlockworkBreakdownLine { ComponentName="Labour filling 100mm blockwall", Quantity=labourDuration, Unit="hr/m2", UnitPrice=labourCost, TotalPrice=labourPrice },

                new BlockworkBreakdownLine { ComponentName="Sub-total: Filling Cost", Quantity=1, Unit="", TotalPrice=netFillingCost },

                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m2", TotalPrice=netCostPerm2 },
            };

            return new BlockworkItem
            {
                ItemNo = 12,
                Description = "Concrete filling in 100mm blockwall",
                Unit = "m2",
                NetCost = Math.Round(netCostPerm2, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        private BlockworkItem ComputeItem13()
        {
            //MATERIAL COST
            double blockPrice = GetMaterialPrice("225 x 225 x 450mm (9 x 9 x 18\") Hollow blocks");
            double blockLoadingPrice = GetMaterialPrice("Loading and unloading blocks");

            double blockPerM2 = UserRateEditStore.Current.Qty(SectionKey, 13, "Blocks per square meter.", 10);
            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 13, "Add for waste.", 10);

            double blockCost = blockPrice * blockPerM2;
            double blockLoadingCost = blockLoadingPrice * blockPerM2;

            double totalMaterialCost = blockCost;
            double waste = totalMaterialCost * (wastePer / 100);
            double finalMaterialCost = totalMaterialCost + waste + blockLoadingCost;

            double mortarCost = GetNetValue(ComputeItem3);
            double mortarQty = UserRateEditStore.Current.Qty(SectionKey, 13, "Mortar per square meter", MortarMix.PerSqM225);
            double totalMortarCost = mortarCost * mortarQty;
            double mortarWastePer = UserRateEditStore.Current.Qty(SectionKey, 13, "Add for waste.", 5);
            double mortarWaste = totalMortarCost * (mortarWastePer / 100);
            double finalCost = totalMortarCost + mortarWaste;

            //LABOUR COST
            double masonCost = GetLabourRate("Skilled/Artisan") * 1.4;
            double labourCost = GetLabourRate("Labourer") * 1.4;

            double masonQty = UserRateEditStore.Current.Qty(SectionKey, 13, "Masons", 3);
            double labourQty = UserRateEditStore.Current.Qty(SectionKey, 13, "Labour", 2);

            double masonPrice = masonCost * masonQty;
            double labourPrice = labourCost * labourQty;
            double totalLabourCost = masonPrice + labourPrice;

            double outputPerDay = 140;
            double areaPerDay = outputPerDay / blockPerM2;
            double labourCostPerM2 = totalLabourCost / areaPerDay;

            double netCostPerm2 = finalMaterialCost + finalCost + labourCostPerM2;

            var ohp = ApplyOHP(netCostPerm2);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="Blocks per square meter.", Quantity=blockPerM2, Unit="No", UnitPrice=blockPrice, TotalPrice=blockCost },
                new BlockworkBreakdownLine { ComponentName="Loading and unloading blocks", Quantity=blockPerM2, Unit="No", UnitPrice=blockLoadingPrice, TotalPrice=blockLoadingCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Block", Quantity=1, Unit="", TotalPrice=finalMaterialCost },

                new BlockworkBreakdownLine { ComponentName="Mortar per square meter", Quantity=mortarQty, Unit="m3/m2", UnitPrice=mortarCost, TotalPrice=totalMortarCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=mortarWastePer, Unit="%", TotalPrice=mortarWaste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Mortar", Quantity=1, Unit="", TotalPrice=finalCost },

                new BlockworkBreakdownLine { ComponentName="Masons", Quantity=masonQty, Unit="per day", UnitPrice=masonCost, TotalPrice=masonPrice },
                new BlockworkBreakdownLine { ComponentName="Labour", Quantity=labourQty, Unit="per day", UnitPrice=labourCost, TotalPrice=labourPrice },
                new BlockworkBreakdownLine { ComponentName="Labour cost per day", Quantity=1, Unit="per day", TotalPrice=totalLabourCost },

                new BlockworkBreakdownLine { ComponentName="Sub-total: Output 140 blocks per day @ 10 blocks/m2", Quantity=areaPerDay, Unit="m2", UnitPrice=totalLabourCost, TotalPrice=labourCostPerM2 },

                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m2", TotalPrice=netCostPerm2 },
            };

            return new BlockworkItem
            {
                ItemNo = 13,
                Description = "225mm blockwall in cement and sand mortar (1:4)",
                Unit = "m2",
                NetCost = Math.Round(netCostPerm2, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        private BlockworkItem ComputeItem14()
        {
            //MATERIAL COST
            double blockPrice = GetMaterialPrice("150 x 225 x 450mm (6 x 9 x 18\") Hollow blocks");
            double blockLoadingPrice = GetMaterialPrice("Loading and unloading blocks");

            double blockPerM2 = UserRateEditStore.Current.Qty(SectionKey, 14, "Blocks per square meter.", 10);
            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 14, "Add for waste.", 10);

            double blockCost = blockPrice * blockPerM2;
            double blockLoadingCost = blockLoadingPrice * blockPerM2;

            double totalMaterialCost = blockCost;
            double waste = totalMaterialCost * (wastePer / 100);
            double finalMaterialCost = totalMaterialCost + waste + blockLoadingCost;

            double mortarCost = GetNetValue(ComputeItem3);
            double mortarQty = UserRateEditStore.Current.Qty(SectionKey, 14, "Mortar per square meter", MortarMix.PerSqM150);
            double totalMortarCost = mortarCost * mortarQty;
            double mortarWastePer = UserRateEditStore.Current.Qty(SectionKey, 14, "Add for waste.", 5);
            double mortarWaste = totalMortarCost * (mortarWastePer / 100);
            double finalCost = totalMortarCost + mortarWaste;

            //LABOUR COST
            double masonCost = GetLabourRate("Skilled/Artisan") * 1.4;
            double labourCost = GetLabourRate("Labourer") * 1.4;

            double masonQty = UserRateEditStore.Current.Qty(SectionKey, 14, "Masons", 3);
            double labourQty = UserRateEditStore.Current.Qty(SectionKey, 14, "Labour", 2);

            double masonPrice = masonCost * masonQty;
            double labourPrice = labourCost * labourQty;
            double totalLabourCost = masonPrice + labourPrice;

            double outputPerDay = 160;
            double areaPerDay = outputPerDay / blockPerM2;
            double labourCostPerM2 = totalLabourCost / areaPerDay;

            double netCostPerm2 = finalMaterialCost + finalCost + labourCostPerM2;

            var ohp = ApplyOHP(netCostPerm2);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="Blocks per square meter.", Quantity=blockPerM2, Unit="No", UnitPrice=blockPrice, TotalPrice=blockCost },
                new BlockworkBreakdownLine { ComponentName="Loading and unloading blocks", Quantity=blockPerM2, Unit="No", UnitPrice=blockLoadingPrice, TotalPrice=blockLoadingCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Block", Quantity=1, Unit="", TotalPrice=finalMaterialCost },

                new BlockworkBreakdownLine { ComponentName="Mortar per square meter", Quantity=mortarQty, Unit="m3/m2", UnitPrice=mortarCost, TotalPrice=totalMortarCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=mortarWastePer, Unit="%", TotalPrice=mortarWaste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Mortar", Quantity=1, Unit="", TotalPrice=finalCost },

                new BlockworkBreakdownLine { ComponentName="Masons", Quantity=masonQty, Unit="per day", UnitPrice=masonCost, TotalPrice=masonPrice },
                new BlockworkBreakdownLine { ComponentName="Labour", Quantity=labourQty, Unit="per day", UnitPrice=labourCost, TotalPrice=labourPrice },
                new BlockworkBreakdownLine { ComponentName="Labour cost per day", Quantity=1, Unit="per day", TotalPrice=totalLabourCost },

                new BlockworkBreakdownLine { ComponentName="Sub-total: Output 160 blocks per day @ 10 blocks/m2", Quantity=areaPerDay, Unit="m2", UnitPrice=totalLabourCost, TotalPrice=labourCostPerM2 },

                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m2", TotalPrice=netCostPerm2 },
            };

            return new BlockworkItem
            {
                ItemNo = 14,
                Description = "150mm blockwall in cement and sand mortar (1:4)",
                Unit = "m2",
                NetCost = Math.Round(netCostPerm2, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        private BlockworkItem ComputeItem15()
        {
            //MATERIAL COST
            double blockPrice = GetMaterialPrice("100 x 225 x 450mm (4 x 9 x 18\") Hollow blocks");
            double blockLoadingPrice = GetMaterialPrice("Loading and unloading blocks");

            double blockPerM2 = UserRateEditStore.Current.Qty(SectionKey, 15, "Blocks per square meter.", 10);
            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 15, "Add for waste.", 10);

            double blockCost = blockPrice * blockPerM2;
            double blockLoadingCost = blockLoadingPrice * blockPerM2;

            double totalMaterialCost = blockCost;
            double waste = totalMaterialCost * (wastePer / 100);
            double finalMaterialCost = totalMaterialCost + waste + blockLoadingCost;

            double mortarCost = GetNetValue(ComputeItem3);
            double mortarQty = UserRateEditStore.Current.Qty(SectionKey, 15, "Mortar per square meter", MortarMix.PerSqM100);
            double totalMortarCost = mortarCost * mortarQty;
            double mortarWastePer = UserRateEditStore.Current.Qty(SectionKey, 15, "Add for waste.", 5);
            double mortarWaste = totalMortarCost * (mortarWastePer / 100);
            double finalCost = totalMortarCost + mortarWaste;

            //LABOUR COST
            double masonCost = GetLabourRate("Skilled/Artisan") * 1.4;
            double labourCost = GetLabourRate("Labourer") * 1.4;

            double masonQty = UserRateEditStore.Current.Qty(SectionKey, 15, "Masons", 3);
            double labourQty = UserRateEditStore.Current.Qty(SectionKey, 15, "Labour", 2);

            double masonPrice = masonCost * masonQty;
            double labourPrice = labourCost * labourQty;
            double totalLabourCost = masonPrice + labourPrice;

            double outputPerDay = 200;
            double areaPerDay = outputPerDay / blockPerM2;
            double labourCostPerM2 = totalLabourCost / areaPerDay;

            double netCostPerm2 = finalMaterialCost + finalCost + labourCostPerM2;

            var ohp = ApplyOHP(netCostPerm2);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="Blocks per square meter.", Quantity=blockPerM2, Unit="No", UnitPrice=blockPrice, TotalPrice=blockCost },
                new BlockworkBreakdownLine { ComponentName="Loading and unloading blocks", Quantity=blockPerM2, Unit="No", UnitPrice=blockLoadingPrice, TotalPrice=blockLoadingCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Block", Quantity=1, Unit="", TotalPrice=finalMaterialCost },

                new BlockworkBreakdownLine { ComponentName="Mortar per square meter", Quantity=mortarQty, Unit="m3/m2", UnitPrice=mortarCost, TotalPrice=totalMortarCost },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=mortarWastePer, Unit="%", TotalPrice=mortarWaste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Mortar", Quantity=1, Unit="", TotalPrice=finalCost },

                new BlockworkBreakdownLine { ComponentName="Masons", Quantity=masonQty, Unit="per day", UnitPrice=masonCost, TotalPrice=masonPrice },
                new BlockworkBreakdownLine { ComponentName="Labour", Quantity=labourQty, Unit="per day", UnitPrice=labourCost, TotalPrice=labourPrice },
                new BlockworkBreakdownLine { ComponentName="Labour cost per day", Quantity=1, Unit="per day", TotalPrice=totalLabourCost },

                new BlockworkBreakdownLine { ComponentName="Sub-total: Output 160 blocks per day @ 10 blocks/m2", Quantity=areaPerDay, Unit="m2", UnitPrice=totalLabourCost, TotalPrice=labourCostPerM2 },

                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m2", TotalPrice=netCostPerm2 },
            };

            return new BlockworkItem
            {
                ItemNo = 15,
                Description = "100mm blockwall in cement and sand mortar (1:4)",
                Unit = "m2",
                NetCost = Math.Round(netCostPerm2, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        private BlockworkItem ComputeItem16()
        {
            //MATERIAL COST
            double plywoodPrice = GetMaterialPrice("3/4\"x4x8' (18x1200x2400mm)");
            double hardwoodPrice = GetMaterialPrice("2x2\"x12' (50x50x3600mm) - Hardwood");
            double solignumPrice = GetMaterialPrice("Solignum (normal)")/20;

            double panelPer100m2 = UserRateEditStore.Current.Qty(SectionKey, 16, "18mm plywood", 35);
            double hardwoodPer100m2 = UserRateEditStore.Current.Qty(SectionKey, 16, "50 x 50mm hardwood", 130);
            double solignumPer100m2 = UserRateEditStore.Current.Qty(SectionKey, 16, "Solignum", 50);

            double panelCost = plywoodPrice * panelPer100m2;
            double hardwoodCost = hardwoodPer100m2 * hardwoodPrice;
            double solignumCost = solignumPrice * solignumPer100m2;
            double nailPer = UserRateEditStore.Current.Qty(SectionKey, 16, "Add for nail.", 2.5);
            double nails = (panelCost+hardwoodCost) * (nailPer / 100);
            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 16, "Add for waste.", 5);
            double waste = (panelCost + hardwoodCost+solignumCost+nails) * (wastePer / 100);

            double totalMaterial = panelCost + hardwoodCost + solignumCost + nails + waste;
            double materialPerM2 = totalMaterial / 100;

            //LABOUR COST
            double masonCost = GetLabourRate("Skilled/Artisan") * 1.4;
            double labourCost = GetLabourRate("Labourer") * 1.4;

            double masonQty = UserRateEditStore.Current.Qty(SectionKey, 16, "Masons", 3);
            double labourQty = UserRateEditStore.Current.Qty(SectionKey, 16, "Labour", 2);

            double masonPrice = masonCost * masonQty;
            double labourPrice = labourCost * labourQty;
            double totalLabourCost = masonPrice + labourPrice;

            double outputPerDay = 30;
            double labourPerM2 = totalLabourCost / outputPerDay;

            double netCostPerm2 = materialPerM2 + labourPerM2;

            var ohp = ApplyOHP(netCostPerm2);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="18mm plywood", Quantity=panelPer100m2, Unit="No", UnitPrice=plywoodPrice, TotalPrice=panelCost },
                new BlockworkBreakdownLine { ComponentName="50 x 50mm hardwood", Quantity=hardwoodPer100m2, Unit="No", UnitPrice=hardwoodPrice, TotalPrice=hardwoodCost },
                new BlockworkBreakdownLine { ComponentName="Solignum", Quantity=solignumPer100m2, Unit="No", UnitPrice=solignumPrice, TotalPrice=solignumCost },
                new BlockworkBreakdownLine { ComponentName="Add for nail.", Quantity=nailPer, Unit="%", TotalPrice=nails },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material per 100m2", Quantity=1, Unit="", TotalPrice=totalMaterial },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material per m2", Quantity=1, Unit="", TotalPrice=materialPerM2 },

                new BlockworkBreakdownLine { ComponentName="Masons", Quantity=masonQty, Unit="per day", UnitPrice=masonCost, TotalPrice=masonPrice },
                new BlockworkBreakdownLine { ComponentName="Labour", Quantity=labourQty, Unit="per day", UnitPrice=labourCost, TotalPrice=labourPrice },
                new BlockworkBreakdownLine { ComponentName="Output 30m2 per day", Quantity=outputPerDay, Unit="m2", UnitPrice=totalLabourCost, TotalPrice=labourPerM2 },

                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m2", TotalPrice=netCostPerm2 },
            };

            return new BlockworkItem
            {
                ItemNo = 16,
                Description = "Single face timber paneling to wall comprising 18mm plywood, and 50 x 50mm timber framing at 600mm centers and including treating with solignum (Panel area to be 100m2)",
                Unit = "m2",
                NetCost = Math.Round(netCostPerm2, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        private BlockworkItem ComputeItem17()
        {
            //MATERIAL COST
            double plywoodPrice = GetMaterialPrice("3/4\"x4x8' (18x1200x2400mm)");
            double hardwoodPrice = GetMaterialPrice("2x2\"x12' (50x50x3600mm) - Hardwood");
            double solignumPrice = GetMaterialPrice("Solignum (normal)") / 20;

            double panelPer100m2 = UserRateEditStore.Current.Qty(SectionKey, 17, "18mm plywood", 35);
            double hardwoodPer100m2 = UserRateEditStore.Current.Qty(SectionKey, 17, "50 x 50mm hardwood", 80);
            double solignumPer100m2 = UserRateEditStore.Current.Qty(SectionKey, 17, "Solignum", 50);

            double panelCost = plywoodPrice * panelPer100m2;
            double hardwoodCost = hardwoodPer100m2 * hardwoodPrice;
            double solignumCost = solignumPrice * solignumPer100m2;
            double nailPer = UserRateEditStore.Current.Qty(SectionKey, 17, "Add for nail.", 2.5);
            double nails = (panelCost + hardwoodCost) * (nailPer / 100);
            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 17, "Add for waste.", 5);
            double waste = (panelCost + hardwoodCost + solignumCost + nails) * (wastePer / 100);

            double totalMaterial = panelCost + hardwoodCost + solignumCost + nails + waste;
            double materialPerM2 = totalMaterial / 100;

            //LABOUR COST
            double masonCost = GetLabourRate("Skilled/Artisan") * 1.4;
            double labourCost = GetLabourRate("Labourer") * 1.4;

            double masonQty = UserRateEditStore.Current.Qty(SectionKey, 17, "Masons", 3);
            double labourQty = UserRateEditStore.Current.Qty(SectionKey, 17, "Labour", 2);

            double masonPrice = masonCost * masonQty;
            double labourPrice = labourCost * labourQty;
            double totalLabourCost = masonPrice + labourPrice;

            double outputPerDay = 40;
            double labourPerM2 = totalLabourCost / outputPerDay;

            double netCostPerm2 = materialPerM2 + labourPerM2;

            var ohp = ApplyOHP(netCostPerm2);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="18mm plywood", Quantity=panelPer100m2, Unit="No", UnitPrice=plywoodPrice, TotalPrice=panelCost },
                new BlockworkBreakdownLine { ComponentName="50 x 50mm hardwood", Quantity=hardwoodPer100m2, Unit="No", UnitPrice=hardwoodPrice, TotalPrice=hardwoodCost },
                new BlockworkBreakdownLine { ComponentName="Solignum", Quantity=solignumPer100m2, Unit="No", UnitPrice=solignumPrice, TotalPrice=solignumCost },
                new BlockworkBreakdownLine { ComponentName="Add for nail.", Quantity=nailPer, Unit="%", TotalPrice=nails },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material per 100m2", Quantity=1, Unit="", TotalPrice=totalMaterial },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material per m2", Quantity=1, Unit="", TotalPrice=materialPerM2 },

                new BlockworkBreakdownLine { ComponentName="Masons", Quantity=masonQty, Unit="per day", UnitPrice=masonCost, TotalPrice=masonPrice },
                new BlockworkBreakdownLine { ComponentName="Labour", Quantity=labourQty, Unit="per day", UnitPrice=labourCost, TotalPrice=labourPrice },
                new BlockworkBreakdownLine { ComponentName="Output 30m2 per day", Quantity=outputPerDay, Unit="m2", UnitPrice=totalLabourCost, TotalPrice=labourPerM2 },

                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m2", TotalPrice=netCostPerm2 },
            };

            return new BlockworkItem
            {
                ItemNo = 17,
                Description = "Single face timber paneling to wall comprising 18mm plywood, and 50 x 50mm timber framing at 1200mm centers and including treating with solignum (Panel area to be 100m2)",
                Unit = "m2",
                NetCost = Math.Round(netCostPerm2, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }
        private BlockworkItem ComputeItem18()
        {
            //MATERIAL COST
            double plywoodPrice = GetMaterialPrice("3/4\"x4x8' (18x1200x2400mm)");
            double hardwoodPrice = GetMaterialPrice("2x2\"x12' (50x50x3600mm) - Hardwood");
            double solignumPrice = GetMaterialPrice("Solignum (normal)") / 20;

            double panelPer100m2 = UserRateEditStore.Current.Qty(SectionKey, 18, "18mm plywood", 70);
            double hardwoodPer100m2 = UserRateEditStore.Current.Qty(SectionKey, 18, "50 x 50mm hardwood", 80);
            double solignumPer100m2 = UserRateEditStore.Current.Qty(SectionKey, 18, "Solignum", 50);

            double panelCost = plywoodPrice * panelPer100m2;
            double hardwoodCost = hardwoodPer100m2 * hardwoodPrice;
            double solignumCost = solignumPrice * solignumPer100m2;
            double nailPer = UserRateEditStore.Current.Qty(SectionKey, 18, "Add for nail.", 2.5);
            double nails = (panelCost + hardwoodCost) * (nailPer / 100);
            double wastePer = UserRateEditStore.Current.Qty(SectionKey, 18, "Add for waste.", 5);
            double waste = (panelCost + hardwoodCost + solignumCost + nails) * (wastePer / 100);

            double totalMaterial = panelCost + hardwoodCost + solignumCost + nails + waste;
            double materialPerM2 = totalMaterial / 100;

            //LABOUR COST
            double masonCost = GetLabourRate("Skilled/Artisan") * 1.4;
            double labourCost = GetLabourRate("Labourer") * 1.4;

            double masonQty = UserRateEditStore.Current.Qty(SectionKey, 18, "Masons", 3);
            double labourQty = UserRateEditStore.Current.Qty(SectionKey, 18, "Labour", 2);

            double masonPrice = masonCost * masonQty;
            double labourPrice = labourCost * labourQty;
            double totalLabourCost = masonPrice + labourPrice;

            double outputPerDay = 25;
            double labourPerM2 = totalLabourCost / outputPerDay;

            double netCostPerm2 = materialPerM2 + labourPerM2;

            var ohp = ApplyOHP(netCostPerm2);

            var breakdown = new ObservableCollection<BlockworkBreakdownLine>
            {
				//MATERIALCOST
				new BlockworkBreakdownLine { ComponentName="18mm plywood", Quantity=panelPer100m2, Unit="No", UnitPrice=plywoodPrice, TotalPrice=panelCost },
                new BlockworkBreakdownLine { ComponentName="50 x 50mm hardwood", Quantity=hardwoodPer100m2, Unit="No", UnitPrice=hardwoodPrice, TotalPrice=hardwoodCost },
                new BlockworkBreakdownLine { ComponentName="Solignum", Quantity=solignumPer100m2, Unit="No", UnitPrice=solignumPrice, TotalPrice=solignumCost },
                new BlockworkBreakdownLine { ComponentName="Add for nail.", Quantity=nailPer, Unit="%", TotalPrice=nails },
                new BlockworkBreakdownLine { ComponentName="Add for waste.", Quantity=wastePer, Unit="%", TotalPrice=waste },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material per 100m2", Quantity=1, Unit="", TotalPrice=totalMaterial },
                new BlockworkBreakdownLine { ComponentName="Sub-total: Material per m2", Quantity=1, Unit="", TotalPrice=materialPerM2 },

                new BlockworkBreakdownLine { ComponentName="Masons", Quantity=masonQty, Unit="per day", UnitPrice=masonCost, TotalPrice=masonPrice },
                new BlockworkBreakdownLine { ComponentName="Labour", Quantity=labourQty, Unit="per day", UnitPrice=labourCost, TotalPrice=labourPrice },
                new BlockworkBreakdownLine { ComponentName="Output 30m2 per day", Quantity=outputPerDay, Unit="m2", UnitPrice=totalLabourCost, TotalPrice=labourPerM2 },

                new BlockworkBreakdownLine { ComponentName="Total", Quantity=1, Unit="m2", TotalPrice=netCostPerm2 },
            };

            return new BlockworkItem
            {
                ItemNo = 18,
                Description = "Double face timber paneling to wall comprising 18mm plywood, and 50 x 50mm timber framing at 1200mm centers and including treating with solignum (Panel area to be 100m2)",
                Unit = "m2",
                NetCost = Math.Round(netCostPerm2, 2),
                OverheadValue = Math.Round(ohp.overheadVal, 0),
                ProfitValue = Math.Round(ohp.profitVal, 0),
                TotalCost = Math.Round(ohp.total, 2),
                BlockworkBreakdownLine = breakdown
            };
        }

        #endregion
    }
}
