using System;
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
               .AddCommand(Key.B, ModifierKeys.Control, "View", "Collapse or expand the sidebar", () => vm.ToggleSidebarCommand)
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
                    MessageBox.Show(
                        this,
                        "Cloud sync is not reachable on this network/PC.\n\n" +
                        "RateGen will still open, but cloud features (login/sync) may not work until the network/DNS allows MongoDB Atlas.\n\n" +
                        "Tip: Using a Standard (non-SRV) Mongo connection string fixes this on restricted networks.\n\n" +
                        "Details: " + (mongoDbService.LastError ?? "Unknown"),
                        "ADLM Rate Gen",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
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

        private void ToCmApp_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                this,
                "Coming soon.",
                "ADLM Rate Gen",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }

        public void ShowPopup(UserControl content)
        {
            PopupHost.Show(content);
        }

        // ========== SIDEBAR DRAG & COLLAPSE ==========

        /// <summary>Width to restore when un-collapsing. Defaults to 250.</summary>
        private double _sidebarExpandedWidth = MainViewModel.SidebarExpandedWidth;

        /// <summary>Guard: true while a GridSplitter drag is active.</summary>
        private bool _isDraggingSidebar;

        /// <summary>Called while the user is dragging — updates icon-only state live.</summary>
        private void SidebarSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            _isDraggingSidebar = true;
            if (DataContext is not MainViewModel vm) return;

            bool shouldCollapse = SidebarColumn.ActualWidth <= MainViewModel.SidebarCollapseThreshold;
            if (vm.IsSidebarCollapsed != shouldCollapse)
                vm.IsSidebarCollapsed = shouldCollapse;
        }

        /// <summary>Called when the user finishes dragging the GridSplitter.</summary>
        private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) { _isDraggingSidebar = false; return; }

            var currentWidth = SidebarColumn.ActualWidth;

            if (currentWidth <= MainViewModel.SidebarCollapseThreshold)
            {
                // Snap to fully collapsed
                SidebarColumn.Width = new GridLength(MainViewModel.SidebarCollapsedWidth);
                vm.IsSidebarCollapsed = true;
            }
            else
            {
                // Remember this width for restore, ensure text is visible
                _sidebarExpandedWidth = currentWidth;
                vm.IsSidebarCollapsed = false;
            }

            _isDraggingSidebar = false;
        }

        /// <summary>Reacts to the collapse toggle button in the ViewModel.</summary>
        private void OnMainVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.IsSidebarCollapsed)) return;
            if (sender is not MainViewModel vm) return;

            // Don't fight with the GridSplitter while it's being dragged
            if (_isDraggingSidebar) return;

            if (vm.IsSidebarCollapsed)
            {
                // Save current width before collapsing
                if (SidebarColumn.ActualWidth > MainViewModel.SidebarCollapseThreshold)
                    _sidebarExpandedWidth = SidebarColumn.ActualWidth;

                SidebarColumn.Width = new GridLength(MainViewModel.SidebarCollapsedWidth);
            }
            else
            {
                // Restore to previous expanded width
                SidebarColumn.Width = new GridLength(_sidebarExpandedWidth);
            }
        }
    }
}
