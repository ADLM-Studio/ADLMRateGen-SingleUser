using System;
using System.Linq;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ADLMRateGen.Helpers;
using ADLMRateGen.Services;
using ADLMRateGen.View;
using ADLMRateGen.ViewModel;
using ADLMRateGen.ViewModel.BlockWork;
using ADLMRateGen.ViewModel.MepWork;
using ADLMRateGen.ViewModel.ConcreteWork;
using ADLMRateGen.ViewModel.CustomRate;
using ADLMRateGen.ViewModel.Finishes;
using ADLMRateGen.ViewModel.Groundwork;
using ADLMRateGen.ViewModel.Model;
using ADLMRateGen.ViewModel.Painting;
using ADLMRateGen.ViewModel.RoofWork;
using ADLMRateGen.ViewModel.SteelWork;
using ADLMRateGen.ViewModel.WindowAndDoor;
using ADLMRateGen.ViewModel.CarbonOthers;

namespace ADLMRateGen
{
    public partial class MainWindow : Window
    {
        private readonly LibraryShellViewModel _shellVm;
        private readonly PopupHost _popup;

        // keep a reference so we can unsubscribe from the STATIC event
        private Action<MaterialModel>? _materialPopupSavedHandler;

        // Same gestures as every other ADLM product. Ctrl+1..9 are the nine trade
        // sections in sidebar order; everything needs a signed-in session.
        private void RegisterShortcuts(MainViewModel vm)
        {
            var map = new KeyboardShortcutMap("ADLM RateGen");
            Func<bool> signedIn = () => vm.IsLoggedIn;
            Action<ICommand> run = c => { if (c != null && c.CanExecute(null)) c.Execute(null); };

            map.AddSections("Navigate",
                KeyboardShortcutMap.Section("Library", () => run(vm.SelectedMaterialLibraryViewCommand), signedIn),
                KeyboardShortcutMap.Section("Ground", () => run(vm.SelectedGroundworkViewCommand), signedIn),
                KeyboardShortcutMap.Section("Concrete", () => run(vm.SelectedConcreteWorkViewCommand), signedIn),
                KeyboardShortcutMap.Section("Block Works", () => run(vm.SelectedBlockworkViewCommand), signedIn),
                KeyboardShortcutMap.Section("Finishes", () => run(vm.SelectedFinishesViewCommand), signedIn),
                KeyboardShortcutMap.Section("Roofs", () => run(vm.SelectedRoofworkViewCommand), signedIn),
                KeyboardShortcutMap.Section("Painting", () => run(vm.SelectedPaintworkViewCommand), signedIn),
                KeyboardShortcutMap.Section("Steel", () => run(vm.SelectedSteelworkViewCommand), signedIn),
                KeyboardShortcutMap.Section("Window and Door", () => run(vm.SelectedWindowAndDoorViewCommand), signedIn));

            map.Add(Key.F, ModifierKeys.Control, "Navigate", "Search", () => AppHeader.FocusSearch(), signedIn)
               .AddCommand(Key.F5, ModifierKeys.None, "Data", "Sync from Cloud", () => vm.IsLoggedIn ? vm.RefreshCloudDataCommand : null)
               .AddCommand(Key.E, ModifierKeys.Control, "Data", "Export all rates", () => vm.IsLoggedIn ? vm.ExportAllRatesCommand : null)
               .AddCommand(Key.B, ModifierKeys.Control, "View", "Fold or unfold the rail", () => vm.ToggleSidebarCommand)
               .Add(Key.L, ModifierKeys.Control | ModifierKeys.Shift, "View", "Switch light / dark theme", () => AppHeader.ToggleTheme());

            map.AttachTo(this);
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Help, (s, e) => map.ShowSheet(this)));
        }

        public MainWindow()
        {
            InitializeComponent();

            // 1) Create your sub-VMs
            var priceVM = new MaterialPriceViewModel();
            var libraryVM = new MaterialLibraryViewModel();
            var labourVm = new LabourPriceViewModel();
            var labourLibraryVM = new LabourLibraryViewModel();

            _shellVm = new LibraryShellViewModel(libraryVM, labourLibraryVM);

            var groundworkVM = new GroundWorkViewModel(libraryVM, labourLibraryVM);
            var concreteWorkVM = new ConcreteViewModel(libraryVM, labourLibraryVM);
            var blockworkVM = new BlockworkViewModel(libraryVM, labourLibraryVM, concreteWorkVM);
            var mepWorkVM = new MepWorkViewModel(libraryVM, labourLibraryVM);
            var finishesVM = new FinishesViewModel(libraryVM, labourLibraryVM, blockworkVM);
            var roofworkVM = new RoofWorkViewModel(libraryVM, labourLibraryVM);
            var windowAndDoorVM = new WindowAndDoorViewModel(libraryVM, labourLibraryVM);
            var paintVM = new PaintWorkViewModel(libraryVM, labourLibraryVM);
            var steelWorkVM = new SteelWorkViewModel(libraryVM, labourLibraryVM);
            var carbonVM = new CarbonOthersViewModel(libraryVM, labourLibraryVM);

            var customInputVM = new CustomRateEntryViewModel();
            var customViewVM = new CustomRateListViewModel();

            // 2) Mongo service uses environment-provided connection info in public builds.
            var srvConnectionString = AppEnvironment.MongoSrvConnectionString;
            var standardConnectionString = AppEnvironment.MongoStandardConnectionString;
            var databaseName = AppEnvironment.MongoDatabaseName;
            var userColName = AppEnvironment.MongoUsersCollection;
            var matColName = AppEnvironment.MongoMaterialsCollection;
            var labColName = AppEnvironment.MongoLabourCollection;

            var mongoDbService = new MongoDbService(
                srvConnectionString,
                databaseName,
                userColName,
                matColName,
                labColName,
                standardConnectionString
            );

            // 3) Sign-in VM (same as your existing pattern)
            var signInVM = new SignInViewModel(mongoDbService);

            // ───────── event wiring ─────────
            _shellVm.RequestAddMaterial += OnRequestAddMaterial;
            _shellVm.RequestEditMaterial += OnRequestEditMaterial;
            _shellVm.RequestAddLabour += OnRequestAddLabour;

            _popup = PopupHost;

            // Carbon: open details in same popup host
            carbonVM.RequestShowDetails += item =>
            {
                _popup.Show(new CarbonRateItemDetailControl { DataContext = item });
            };

            // 4) MainViewModel
            var mainVM = new MainViewModel(
                priceVM,
                libraryVM,
                labourVm,
                labourLibraryVM,
                groundworkVM,
                concreteWorkVM,
                blockworkVM,
                mepWorkVM,
                finishesVM,
                roofworkVM,
                windowAndDoorVM,
                paintVM,
                steelWorkVM,
                carbonVM,
                _shellVm,
                customViewVM,
                customInputVM,
                mongoDbService,
                signInVM);

            // 5) Set DataContext
            DataContext = mainVM;
            RegisterShortcuts(mainVM);

            // Every section's breakdown links into the library through
            // LibraryLink. MainViewModel already pointed it at the navigation; this
            // wraps that to also close the detail popup, which only the window can
            // reach. Without it the popup stays up over the row the user was just
            // sent to look at, in all ten sections.
            //
            // Assigned after the view model is built so this wrapper wins.
            var navigate = Helpers.LibraryLink.Navigate;
            Helpers.LibraryLink.Navigate = (kind, name) =>
            {
                _popup.Hide();
                navigate?.Invoke(kind, name);
            };

            // 6) React to ToggleSidebarCommand by snapping the column width
            mainVM.PropertyChanged += OnMainVmPropertyChanged;
            SizeChanged += OnWindowSizeChanged;

            // Screenshots of this app carry the ADLM mark. Applied to the captured
            // image rather than drawn on screen, so there is nothing over the
            // figures while somebody is working. See ScreenshotWatermark.
            Loaded += (_, __) => ADLMRateGen.Services.ScreenshotWatermark.Attach(this);

            // 7) Connect to Mongo after the window is up so startup does not block on DNS/network.
            Loaded += async (_, __) =>
            {
                await mongoDbService.InitializeAsync();

                if (mongoDbService.IsConfigured && !mongoDbService.IsAvailable)
                {
                    // Offline is normal, not an error: say what works and what waits.
                    View.Suite.SuiteDialog.Tell(
                        "Working offline",
                        "ADLM Cloud cannot be reached from this network just now. RateGen works with the library already on this PC; " +
                        "signing in and syncing will work again once the connection allows it.\n\n" +
                        "For your IT support: " + (mongoDbService.LastError ?? "no further detail"),
                        View.Suite.SuiteDialog.Tone.Warning);
                }
            };
        }

        // ========== MATERIAL POPUP ==========
        private void OnRequestAddMaterial() => ShowMaterialPopup(null);
        private void OnRequestEditMaterial(MaterialModel m) => ShowMaterialPopup(m);

        private void ShowMaterialPopup(MaterialModel? existing)
        {
            // Always unsubscribe previous handler (prevents duplicates)

            if (_materialPopupSavedHandler != null)
            {
                MaterialPriceViewModel.MaterialSaved -= _materialPopupSavedHandler;
                _materialPopupSavedHandler = null;
            }

            var vm = new MaterialPriceViewModel();

            if (existing != null)
            {
                vm.EditingMaterial = existing;
                vm.MaterialName = existing.MaterialName;
                vm.MaterialUnit = existing.MaterialUnit;
                vm.MaterialPrice = existing.MaterialPrice;
                vm.NewMaterialCategory = existing.MaterialCategory;
            }

            // because MaterialSaved is STATIC, subscribe using the TYPE NAME
            _materialPopupSavedHandler = mat =>
            {
                // unsubscribe immediately after first save
                if (_materialPopupSavedHandler != null)
                {
                    MaterialPriceViewModel.MaterialSaved -= _materialPopupSavedHandler;
                    _materialPopupSavedHandler = null;
                }

                _shellVm.MaterialLibraryViewModel.AddOrUpdateMaterial(mat);

                // Show success popup instead of immediately hiding
                _popup.Show(new View.LibrarySuccessView());
            };

            MaterialPriceViewModel.MaterialSaved += _materialPopupSavedHandler;

            _popup.Show(new MaterialPriceView { DataContext = vm });
        }

        // ========== LABOUR POPUP ==========
        private void OnRequestAddLabour() => ShowLabourPopup(null);

        private void ShowLabourPopup(LabourModel? existing)
        {
            var vm = new LabourPriceViewModel();

            if (existing != null)
            {
                vm.EditingLabour = existing;
                vm.LabourName = existing.LabourName;
                vm.LabourUnit = existing.LabourUnit;
                vm.LabourPrice = existing.LabourPrice;
                vm.NewLabourCategory = existing.LabourCategory;
            }

            vm.LabourSaved += lab =>
            {
                _shellVm.LabourLibraryViewModel.AddOrUpdateLabour(lab);

                // Show success popup instead of immediately hiding
                _popup.Show(new View.LibrarySuccessView());
            };

            _popup.Show(new LabourPriceView { DataContext = vm });
        }

        private void OnPopupClose(object sender, RoutedEventArgs e)
        {
            // cleanup static handler if user closes without saving
            if (_materialPopupSavedHandler != null)
            {
                MaterialPriceViewModel.MaterialSaved -= _materialPopupSavedHandler;
                _materialPopupSavedHandler = null;
            }

            PopupHost.Hide();
        }

        /// <summary>
        /// Open ADLM Cloud: RateGen's web side, where the library prices a
        /// project. Rates are built here and used there (Richard's split,
        /// 30 Sep 2026), so this says so before it leaves for the browser.
        /// </summary>
        private void ToCmApp_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            int library = (vm?.GroundWorkViewModel?.GroundworkItems.Count ?? 0)
                        + (vm?.ConcreteViewModel?.ConcreteworkCollectionView?.Cast<object>().Count() ?? 0)
                        + (vm?.BlockworkViewModel?.BlockworkCollectionView?.Cast<object>().Count() ?? 0)
                        + (vm?.FinishesViewModel?.FinishesCollectionView?.Cast<object>().Count() ?? 0)
                        + (vm?.RoofWorkViewModel?.RoofworkCollectionView?.Cast<object>().Count() ?? 0)
                        + (vm?.PaintWorkViewModel?.PaintWorkCollectionView?.Cast<object>().Count() ?? 0)
                        + (vm?.SteelWorkViewModel?.SteelWorkCollectionView?.Cast<object>().Count() ?? 0)
                        + (vm?.WindowAndDoorViewModel?.WindowAndDoorCollectionView?.Cast<object>().Count() ?? 0)
                        + (vm?.MepWorkViewModel?.MepWorkItems.Count ?? 0)
                        + (vm?.CarbonOthersViewModel?.Items.Count ?? 0);
            int custom = vm?.CustomRateListViewModel?.CustomRates.Count ?? 0;

            var go = View.Suite.SuiteDialog.Ask(
                "Open ADLM Cloud",
                "The library prices your projects on the web. Rates are built here in RateGen; they are used there. Your custom rates stay on this PC.",
                "Open ADLM Cloud", "Not now", View.Suite.SuiteDialog.Tone.Info,
                new[] { ("Library rates on this PC", library.ToString("N0")), ("Your custom rates", custom.ToString("N0")) });
            if (!go) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CloudRateGenUrl) { UseShellExecute = true });
            }
            catch
            {
                View.Suite.SuiteDialog.Tell("No browser opened",
                    "Open " + CloudRateGenUrl + " in your browser to reach RateGen on ADLM Cloud.",
                    View.Suite.SuiteDialog.Tone.Warning);
            }
        }

        /// <summary>RateGen on the customer's ADLM account (the site titles it "RateGen").</summary>
        private const string CloudRateGenUrl = "https://adlmstudio.net/work/library";

        public void ShowPopup(UserControl content)
        {
            PopupHost.Show(content);
        }

        // ========== RESPONSIVE ==========
        // Richard's breakpoint: below 1100px the rail becomes the 64px icon rail
        // (rategen-app.css @media max-width 1100px). Folded by the window, it
        // unfolds itself again when there is room; folded by the user (Ctrl+B),
        // it stays as they left it.
        private const double RailBreakpoint = 1100;
        private bool _railFoldedForWidth;

        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            bool narrow = e.NewSize.Width < RailBreakpoint;
            if (narrow && !vm.IsSidebarCollapsed)
            {
                _railFoldedForWidth = true;
                vm.IsSidebarCollapsed = true;
            }
            else if (!narrow && _railFoldedForWidth)
            {
                _railFoldedForWidth = false;
                vm.IsSidebarCollapsed = false;
            }
        }

        // ========== RAIL FOLD ==========
        // The suite's rail is a fixed 236px; Ctrl+B (ToggleSidebarCommand) folds
        // it to the 64px icon rail and back.
        private void OnMainVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.IsSidebarCollapsed)) return;
            if (sender is not MainViewModel vm) return;
            SidebarColumn.Width = new GridLength(vm.IsSidebarCollapsed
                ? MainViewModel.SidebarCollapsedWidth
                : MainViewModel.SidebarExpandedWidth);
        }
    }
}
