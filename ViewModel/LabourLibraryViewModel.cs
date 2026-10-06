using ADLMRateGen.Command;
using ADLMRateGen.Helpers;
using ADLMRateGen.Services;
using ADLMRateGen.ViewModel.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace ADLMRateGen.ViewModel
{
    public class LabourLibraryViewModel : ViewModelBase
    {
        private readonly LabourJsonDataSource _ds = new(AppPaths.LabourLibraryFile);
        public event Action<bool, string?>? BusyChanged;


        public ObservableCollection<LabourModel> LabourLibrary { get; }
        public ICollectionView LabourCollectionView { get; }

        /* --------  names for ComboBoxes  -------- */
        public static IEnumerable<string> GetAllLabourNames() =>
            LabourLibraryService.GetAllLabourNames();

        /* --------  price lookup for RateEntryItem  -------- */
        public static decimal GetPrice(string labourName) =>
            LabourLibraryService.GetPrice(labourName);

        public ObservableCollection<string> LabourCategory { get; }

        private string _selectedLabourCategory = "All";
        public string SelectedLabourCategory
        {
            get => _selectedLabourCategory;
            set
            {
                if (_selectedLabourCategory != value)
                {
                    _selectedLabourCategory = value;
                    RaisePropertyChanged();
                    ApplyFilter();
                }
            }
        }

        private string _searchTerm = string.Empty;
        public string SearchTerm
        {
            get => _searchTerm;
            set
            {
                if (_searchTerm != value)
                {
                    _searchTerm = value;
                    RaisePropertyChanged();
                    ApplyFilter();
                }
            }
        }

        public ICommand SearchLabourCommand { get; }
        public ICommand ClearDatabaseCommand { get; }
        public ICommand DeleteLabourCommand { get; }
        public ICommand EditLabourCommand { get; }
        public ICommand UpdatePricesCommand { get; }

        // Fired when the user clicks “Edit” on a row
        public event Action<LabourModel> EditLabourRequested;
        public event Action LibraryChanged;

        /* --------  convert NGN → currently-selected currency  -------- */
        public double PriceNgnToCurrent(double baseNgn) =>
            baseNgn * CurrencyService.Instance.Rate;

        public LabourLibraryViewModel()
        {
            // Load from the shared AppData file
            LabourLibrary = new ObservableCollection<LabourModel>(_ds.LoadLabours());
            Debug.WriteLine($"[LabourLibraryVM] Loaded {LabourLibrary.Count} from {AppPaths.LabourLibraryFile}");

            ReassignSerialNumbers();   // keep S/N tidy

            /* 2. CollectionView for DataGrid + filter */
            LabourCollectionView = CollectionViewSource.GetDefaultView(LabourLibrary);
            LabourCollectionView.Filter = _ => true;
            ApplyFilter();

            LabourCategory = new ObservableCollection<string>();
            RefreshCategories();

            SearchLabourCommand = new DelegateCommand(_ => ApplyFilter());
            ClearDatabaseCommand = new DelegateCommand(_ => ClearDatabase());

            DeleteLabourCommand = new DelegateCommand(o => DeleteLabour(o));
            EditLabourCommand = new DelegateCommand(o => EditLabour(o));

            UpdatePricesCommand = new DelegateCommand(_ => UpdatePricesFromMongo());

            /* when currency changes → redraw the grid */
            CurrencyService.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CurrencyService.Rate))
                    LabourCollectionView.Refresh();
            };


            Debug.WriteLine($"[PATH] Labour file used by service: {AppPaths.LabourLibraryFile}");

        }

        /* ───────── filtering helper ───────── */

        /// <summary>
        /// Rebuild the category dropdown from the library itself, for the same reason
        /// the material one does. The hardcoded list here was only three values —
        /// "Labour", "Plant", "Small Plant" — so any other category in the data was
        /// unreachable, and when the cloud sync blanked every LabourCategory the
        /// dropdown still looked fine while matching nothing.
        /// </summary>
        private void RefreshCategories()
        {
            var selected = SelectedLabourCategory;

            var found = LabourLibrary
                .Select(l => l.LabourCategory)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            LabourCategory.Clear();
            LabourCategory.Add("All");
            foreach (var c in found) LabourCategory.Add(c);

            SelectedLabourCategory =
                selected != null && LabourCategory.Contains(selected) ? selected : "All";
        }

        private void ApplyFilter()
        {
            LabourCollectionView.Filter = o =>
            {
                if (o is not LabourModel lb) return false;

                var okCategory = SelectedLabourCategory == "All" ||
                                 string.IsNullOrEmpty(SelectedLabourCategory) ||
                                 lb.LabourCategory == SelectedLabourCategory;

                var okSearch = string.IsNullOrWhiteSpace(SearchTerm) ||
                               (lb.LabourName?.IndexOf(SearchTerm, StringComparison.OrdinalIgnoreCase) >= 0);

                return okCategory && okSearch;
            };
            LabourCollectionView.Refresh();
        }

        private void ClearDatabase()
        {
            LabourLibrary.Clear();
            _ds.SaveLabours(LabourLibrary);
            LabourLibraryService.Initialize(); // refresh static cache for other screens
            ApplyFilter();
        }

        private void EditLabour(object parameter)
        {
            if (parameter is LabourModel labour)
                EditLabourRequested?.Invoke(labour);
        }

        private async void DeleteLabour(object parameter)
        {
            if (parameter is not LabourModel labour) return;

            if (!ADLMRateGen.Services.UserRowChecker.IsUserLabour(labour.SerialNumber, labour.LabourName ?? ""))
            {
                ADLMRateGen.Helpers.AppMessage.Show("Only your own added labour/plant items can be deleted.\nMaster items cannot be removed.",
                    "Not allowed", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int uses = ADLMRateGen.ViewModel.CustomRate.CustomRateUsage.CountLabourUsage(labour.LabourName ?? "");
            if (uses > 0)
            {
                var r = ADLMRateGen.Helpers.AppMessage.Show(
                    $"{uses} custom rate item(s) use \"{labour.LabourName}\".\n" +
                    "If you proceed, they will be removed from those custom rates.\n\nProceed?",
                    "Used in Custom Rates", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (r != MessageBoxResult.Yes) return;

                ADLMRateGen.ViewModel.CustomRate.CustomRateUsage.RemoveLabourEverywhere(labour.LabourName ?? "");
            }

            LabourLibrary.Remove(labour);
            ReassignSerialNumbers();
            _ds.SaveLabours(LabourLibrary);
            LabourLibraryService.Initialize(_ds);
            LibraryChanged?.Invoke();
            ApplyFilter();

            // user feedback now
            ADLMRateGen.Helpers.AppMessage.Show("Labour item deleted successfully.", "Deleted", MessageBoxButton.OK, MessageBoxImage.Information);


            _ = UserLibrarySync.Instance.DeleteLabourAsync(labour.SerialNumber, labour.LabourName);
        }


        private void ReassignSerialNumbers()
        {
            for (int i = 0; i < LabourLibrary.Count; i++)
                LabourLibrary[i].SerialNumber = i + 1;
        }

        public ObservableCollection<LabourModel> Labours { get; } = new();

        public async void AddOrUpdateLabour(LabourModel lab)
        {
            var existing = LabourLibrary.FirstOrDefault(l => l.SerialNumber == lab.SerialNumber);
            if (existing == null)
            {
                lab.SerialNumber = LabourLibrary.Count == 0
                    ? 1
                    : LabourLibrary.Max(l => l.SerialNumber) + 1;

                LabourLibrary.Add(lab);
                Persist();

                _ = UserLibrarySync.Instance.TryAddLabourAsync(lab);
            }
            else
            {
                existing.LabourPrice = lab.LabourPrice;
                Persist();

                _ = UserLibrarySync.Instance.TryUpdateLabourAsync(existing);
            }
        }

        private void Persist()
        {
            _ds.SaveLabours(LabourLibrary);
            LabourLibraryService.Initialize(_ds);  // refresh service cache
            LibraryChanged?.Invoke();              // 🔔 notifies RateEntryItem
        }

        //private async void UpdatePricesFromMongo()
        //{
        //    var result = ADLMRateGen.Helpers.AppMessage.Show(
        //        "Override labour prices with ADLM server values for your current zone?",
        //        "Confirm",
        //        MessageBoxButton.YesNo,
        //        MessageBoxImage.Question);

        //    if (result != MessageBoxResult.Yes)
        //        return;

        //    try
        //    {

        //        if (!NetChecks.IsOnline())
        //        {
        //            ADLMRateGen.Helpers.AppMessage.Show("You appear to be offline. Connect to the Internet to update prices.",
        //                "No Internet", MessageBoxButton.OK, MessageBoxImage.Warning);
        //            return;
        //        }

        //        string zone = ADLMRateGen.Properties.AppSettings.Zone ?? "";
        //        if (string.IsNullOrWhiteSpace(zone))
        //        {
        //            ADLMRateGen.Helpers.AppMessage.Show("No user zone set. Please sign in again to sync your zone profile.");
        //            return;
        //        }



        //        var auth = ADLMRateGen.Services.AuthProvider.Instance.Client;

        //        // NEW
        //        await UserLibrarySync.Instance.LoadAsync();

        //        var masterDoc = await auth.GetJsonAsync($"/rategen/master?zone={Uri.EscapeDataString(zone)}");
        //        var root = masterDoc.RootElement;

        //        if (root.TryGetProperty("labour", out var labs))
        //            ADLMRateGen.Services.DataSourceCloudSync.SaveLaboursFromDto(labs);

        //        ReloadFromDisk();
        //        ADLMRateGen.Helpers.AppMessage.Show($"Labour prices updated for zone '{zone}'.");
        //    }
        //    catch (UnauthorizedAccessException)
        //    {
        //        ADLMRateGen.Helpers.AppMessage.Show(
        //            "Your session has expired. Please sign in again, then retry the update.",
        //            "Session expired", MessageBoxButton.OK, MessageBoxImage.Information);
        //    }
        //    catch (InvalidOperationException ex) when (ex.Message.StartsWith("401") || ex.Message.Contains("Not signed in"))
        //    {
        //        ADLMRateGen.Helpers.AppMessage.Show(
        //            "Not signed in. Please sign in again to update prices.",
        //            "Authentication required", MessageBoxButton.OK, MessageBoxImage.Warning);
        //    }
        //    catch (Exception ex)
        //    {
        //        ADLMRateGen.Helpers.AppMessage.Show($"Error updating labour prices: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        //    }
        //}

        private async void UpdatePricesFromMongo()
        {
            var result = ADLMRateGen.Helpers.AppMessage.Show(
                "Override labour prices with ADLM server values for your current zone?",
                "Confirm",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            if (!NetChecks.IsOnline())
            {
                ADLMRateGen.Helpers.AppMessage.Show("You appear to be offline. Connect to the Internet to update prices.",
                    "No Internet", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string zone = ADLMRateGen.Properties.AppSettings.Zone ?? "";
            if (string.IsNullOrWhiteSpace(zone))
            {
                ADLMRateGen.Helpers.AppMessage.Show("No user zone set. Please sign in again to sync your zone profile.");
                return;
            }

            BusyChanged?.Invoke(true, "Updating labour prices from server…");

            try
            {
                var auth = ADLMRateGen.Services.AuthProvider.Instance.Client;

                await UserLibrarySync.Instance.LoadAsync();

                var masterDoc = await auth.GetJsonAsync($"/rategen/master?zone={Uri.EscapeDataString(zone)}");
                var root = masterDoc.RootElement;

                if (root.TryGetProperty("labour", out var labs))
                    ADLMRateGen.Services.DataSourceCloudSync.SaveLaboursFromDto(labs);

                ReloadFromDisk();
                ADLMRateGen.Helpers.AppMessage.Show($"Labour prices updated for zone '{zone}'.");
            }
            catch (UnauthorizedAccessException)
            {
                ADLMRateGen.Helpers.AppMessage.Show(
                    "Your session has expired. Please sign in again, then retry the update.",
                    "Session expired", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("401") || ex.Message.Contains("Not signed in"))
            {
                ADLMRateGen.Helpers.AppMessage.Show(
                    "Not signed in. Please sign in again to update prices.",
                    "Authentication required", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                ADLMRateGen.Helpers.AppMessage.Show($"Error updating labour prices: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BusyChanged?.Invoke(false, null);
            }
        }

        public void ReloadFromDisk()
        {
            LabourLibrary.Clear();
            foreach (var l in _ds.LoadLabours()) LabourLibrary.Add(l);

            LabourLibraryService.Initialize(_ds);

            RefreshCategories();     // the cloud sync can introduce whole new categories
            ApplyFilter();
            LibraryChanged?.Invoke();
        }

        private void OpenNewLabourDialog() =>
            ADLMRateGen.Helpers.AppMessage.Show("TODO: add new labour");
    }
}
