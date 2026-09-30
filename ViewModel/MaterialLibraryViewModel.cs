using ADLMRateGen.Command;
using ADLMRateGen.Helpers;
using ADLMRateGen.Services;
using ADLMRateGen.ViewModel.Model;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;                    // ← needed for LINQ
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace ADLMRateGen.ViewModel
{
    public class MaterialLibraryViewModel : ViewModelBase
    {
        /* ---------- data source (AppData file) ---------- */
        private readonly MaterialJsonDataSource _ds = new(AppPaths.MaterialLibraryFile);

        // bound to the grid
        public ObservableCollection<MaterialModel> MaterialLibrary { get; } = new();
        public event Action<bool, string?>? BusyChanged;


        public ICollectionView MaterialCollectionView { get; private set; }

        public ICommand SearchMaterialCommand { get; }
        public ICommand ClearDatabaseCommand { get; }
        public ICommand DeleteMaterialCommand { get; }
        public ICommand EditMaterialCommand { get; }
        public ICommand UpdatePricesCommand { get; }

        public event Action<MaterialModel> EditMaterialRequested;
        public event Action LibraryChanged;

        public double PriceNgnToCurrent(double baseNgn) => baseNgn * CurrencyService.Instance.Rate;

        public double GetMaterialPrice(string name)
        {
            var mat = MaterialLibraryService
                        .GetAllMaterials()
                        .FirstOrDefault(m => m.MaterialName == name);

            return mat == null ? 0 : (double)mat.MaterialPrice * CurrencyService.Instance.Rate;
        }

        public ICommand AddNewCommand { get; }

        public ObservableCollection<string> MaterialCategory { get; set; }

        private string _selectedMaterialCategory;
        public string SelectedMaterialCategory
        {
            get => _selectedMaterialCategory;
            set
            {
                if (_selectedMaterialCategory != value)
                {
                    _selectedMaterialCategory = value;
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

        public void RequestEdit(MaterialModel toEdit) => EditMaterialRequested?.Invoke(toEdit);

        public MaterialLibraryViewModel()
        {
            // Load from AppData file and point the shared service at the SAME DS
            foreach (var m in _ds.LoadMaterials()) MaterialLibrary.Add(m);
            MaterialLibraryService.Initialize(_ds);

            MaterialCollectionView = CollectionViewSource.GetDefaultView(MaterialLibrary);
            ApplyFilter();

            MaterialPriceViewModel.MaterialSaved += AddOrUpdateMaterial;
            MaterialCollectionView.Filter = _ => true;

            // Public builds read from the local library file and use API sync for cloud updates.
            MaterialLibrary.Clear();
            foreach (var m in MaterialLibraryService.GetAllMaterials()) MaterialLibrary.Add(m);

            _selectedMaterialCategory = "All";

            EditMaterialCommand = new DelegateCommand(o => EditMaterial(o));
            DeleteMaterialCommand = new DelegateCommand(o => DeleteMaterial(o));
            SearchMaterialCommand = new DelegateCommand(o => ApplyFilter());
            ClearDatabaseCommand = new DelegateCommand(o => ClearDatabase());
            UpdatePricesCommand = new DelegateCommand(_ => UpdatePricesFromMongo());

            MaterialCategory = new ObservableCollection<string>();
            RefreshCategories();


            // refresh the grid on currency change
            CurrencyService.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CurrencyService.Rate))
                    MaterialCollectionView.Refresh();
            };
        }


        /// <summary>
        /// Rebuild the category dropdown from the library itself.
        ///
        /// This was a hardcoded list of ~70 strings, which failed in two ways. Any
        /// category the list did not know about was unreachable — "Custom Rate" rows
        /// harvested from a saved rate, and the MEP categories added in catalog
        /// 2026.08, could never be filtered to. And when the cloud sync blanked every
        /// category (the server was not sending one), the dropdown still looked
        /// perfectly healthy while matching nothing, which is what made the bug so
        /// hard to see. Deriving the list from the data means the dropdown can never
        /// again disagree with what is in the library.
        /// </summary>
        private void RefreshCategories()
        {
            var selected = SelectedMaterialCategory;

            var found = MaterialLibrary
                .Select(m => m.MaterialCategory)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            MaterialCategory.Clear();
            MaterialCategory.Add("All");
            foreach (var c in found) MaterialCategory.Add(c);

            // Keep the user's selection if it still exists, otherwise fall back to All
            // rather than leaving the box on a value that now filters everything out.
            SelectedMaterialCategory =
                selected != null && MaterialCategory.Contains(selected) ? selected : "All";
        }

        private void ApplyFilter()
        {
            if (MaterialCollectionView == null) return;

            MaterialCollectionView.Filter = o =>
            {
                if (o is not MaterialModel material) return false;

                bool matchesCategory = SelectedMaterialCategory == "All" ||
                                       string.IsNullOrEmpty(SelectedMaterialCategory) ||
                                       material.MaterialCategory == SelectedMaterialCategory;

                bool matchesText = string.IsNullOrEmpty(SearchTerm) ||
                                   (!string.IsNullOrEmpty(material.MaterialName) &&
                                    material.MaterialName.IndexOf(SearchTerm, StringComparison.OrdinalIgnoreCase) >= 0);

                return matchesCategory && matchesText;
            };

            MaterialCollectionView.Refresh();
        }

        private void ClearDatabase()
        {
            MaterialLibrary.Clear();
            _ds.SaveMaterials(MaterialLibrary);
            MaterialLibraryService.Initialize(_ds); // refresh service cache & raise LibraryChanged
            ApplyFilter();
        }

        private async void DeleteMaterial(object parameter)
        {
            if (parameter is not MaterialModel material) return;

            // only user-added rows are deletable
            if (!ADLMRateGen.Services.UserRowChecker.IsUserMaterial(material.SerialNumber, material.MaterialName ?? ""))
            {
                ADLMRateGen.Helpers.AppMessage.Show("Only your own added materials can be deleted.\nMaster items cannot be removed.",
                    "Not allowed", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // warn if used in custom rates
            int uses = ADLMRateGen.ViewModel.CustomRate.CustomRateUsage.CountMaterialUsage(material.MaterialName ?? "");
            if (uses > 0)
            {
                var r = ADLMRateGen.Helpers.AppMessage.Show(
                    $"{uses} custom rate item(s) use \"{material.MaterialName}\".\n" +
                    "If you proceed, they will be removed from those custom rates.\n\nProceed?",
                    "Used in Custom Rates", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (r != MessageBoxResult.Yes) return;

                // remove from custom rates to keep things consistent
                ADLMRateGen.ViewModel.CustomRate.CustomRateUsage.RemoveMaterialEverywhere(material.MaterialName ?? "");
            }

            // local remove
            MaterialLibrary.Remove(material);

            // keep local numbering tidy for UI
            ReassignSerialNumbers();
            _ds.SaveMaterials(MaterialLibrary);
            MaterialLibraryService.Initialize(_ds);
            LibraryChanged?.Invoke();
            ApplyFilter();

            // user feedback now
            ADLMRateGen.Helpers.AppMessage.Show("Material deleted successfully.", "Deleted", MessageBoxButton.OK, MessageBoxImage.Information);


            // remove from server user library (best-effort)
            _ = UserLibrarySync.Instance.DeleteMaterialAsync(material.SerialNumber, material.MaterialName);
        }


        private void EditMaterial(object parameter)
        {
            if (parameter is MaterialModel material)
                EditMaterialRequested?.Invoke(material);
        }

        private void ReassignSerialNumbers()
        {
            int serial = 1;
            foreach (var material in MaterialLibrary)
                material.SerialNumber = serial++;
        }

        public async void AddOrUpdateMaterial(MaterialModel mat)
        {
            var existing = MaterialLibrary.FirstOrDefault(m => m.SerialNumber == mat.SerialNumber);

            if (existing == null)
            {
                // keep numbering continuous (master + existing user items already in collection)
                mat.SerialNumber = MaterialLibrary.Count == 0
                    ? 1
                    : MaterialLibrary.Max(m => m.SerialNumber) + 1;

                MaterialLibrary.Add(mat);
                Persist();

                // mirror to server (best-effort)
                _ = UserLibrarySync.Instance.TryAddMaterialAsync(mat);
            }
            else
            {
                existing.MaterialPrice = mat.MaterialPrice; // editable field
                Persist();

                // if this row belongs to user's library, reflect update
                _ = UserLibrarySync.Instance.TryUpdateMaterialAsync(existing);
            }
        }


        private void Persist()
        {
            _ds.SaveMaterials(MaterialLibrary);
            MaterialLibraryService.Initialize(_ds);
            LibraryChanged?.Invoke();
            ApplyFilter();
        }

        //private async void UpdatePricesFromMongo()
        //{
        //    var result = ADLMRateGen.Helpers.AppMessage.Show(
        //        "Override prices with ADLM server values for your current zone?",
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

        //        // NEW: refresh user rows from server in case another device added something
        //        await UserLibrarySync.Instance.LoadAsync();

        //        var masterDoc = await auth.GetJsonAsync($"/rategen/master?zone={Uri.EscapeDataString(zone)}");
        //        var root = masterDoc.RootElement;

        //        if (root.TryGetProperty("materials", out var mats))
        //            ADLMRateGen.Services.DataSourceCloudSync.SaveMaterialsFromDto(mats);

        //        ReloadFromDisk();
        //        ADLMRateGen.Helpers.AppMessage.Show($"Material prices updated for zone '{zone}'.");
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
        //        ADLMRateGen.Helpers.AppMessage.Show($"Error updating materials: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        //    }
        //}


        private async void UpdatePricesFromMongo()
        {
            var result = ADLMRateGen.Helpers.AppMessage.Show(
                "Override prices with ADLM server values for your current zone?",
                "Confirm",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            // Validate first (don’t show loader if we’ll immediately exit)
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

            BusyChanged?.Invoke(true, "Updating material prices from server…");

            try
            {
                var auth = ADLMRateGen.Services.AuthProvider.Instance.Client;

                // refresh user rows (another device could have added)
                await UserLibrarySync.Instance.LoadAsync();

                var masterDoc = await auth.GetJsonAsync($"/rategen/master?zone={Uri.EscapeDataString(zone)}");
                var root = masterDoc.RootElement;

                if (root.TryGetProperty("materials", out var mats))
                    ADLMRateGen.Services.DataSourceCloudSync.SaveMaterialsFromDto(mats);

                ReloadFromDisk();
                ADLMRateGen.Helpers.AppMessage.Show($"Material prices updated for zone '{zone}'.");
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
                ADLMRateGen.Helpers.AppMessage.Show($"Error updating materials: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BusyChanged?.Invoke(false, null);
            }
        }


        public void ReloadFromDisk()
        {
            MaterialLibrary.Clear();
            foreach (var m in _ds.LoadMaterials()) MaterialLibrary.Add(m);

            // keep the shared service in sync too
            MaterialLibraryService.Initialize(_ds);

            RefreshCategories();     // the cloud sync can introduce whole new categories
            ApplyFilter();           // refresh CollectionView / grid
            LibraryChanged?.Invoke();// keep search index, etc., up to date
        }

        private void OpenNewMaterialDialog()
            => ADLMRateGen.Helpers.AppMessage.Show("TODO: open *Add Material* dialog");
    }
}
