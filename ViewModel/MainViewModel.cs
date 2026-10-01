using ADLMRateGen.Command;
using ADLMRateGen.Helpers;
using ADLMRateGen.Services;
using ADLMRateGen.ViewModel.BlockWork;
using ADLMRateGen.ViewModel.MepWork;
using ADLMRateGen.ViewModel.CarbonOthers;
using ADLMRateGen.ViewModel.ConcreteWork;
using ADLMRateGen.ViewModel.CustomRate;
using ADLMRateGen.ViewModel.Finishes;
using ADLMRateGen.ViewModel.Groundwork;
using ADLMRateGen.ViewModel.Model;
using ADLMRateGen.ViewModel.Painting;
using ADLMRateGen.ViewModel.RoofWork;
using ADLMRateGen.ViewModel.SteelWork;
using ADLMRateGen.ViewModel.WindowAndDoor;
using DocumentFormat.OpenXml.Bibliography;
using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ADLMRateGen.ViewModel
{
    public class MainViewModel : ViewModelBase
    {
        private static string API_BASE_URL => AppEnvironment.ApiBaseUrl;

        // ✅ New clean public base
        private const string RATEGEN_V2_BASE = "/rategen-v2";

        // ✅ Public compute-items endpoint (matches your server mounting)
        private const string COMPUTE_ITEMS_PATH = RATEGEN_V2_BASE + "/compute-items";

        // ✅ Public library meta (since you moved library router to /rategen-v2)
        private const string LIBRARY_META_PATH = RATEGEN_V2_BASE + "/library/meta";

        private const string RATE_LIBRARY_SYNC_PATH = RATEGEN_V2_BASE + "/library/rates/sync";



        /* ───────── injected services ───────── */
        private readonly MongoDbService _mongoDbService;

        /* ───────── rate sync ───────── */
        private readonly RateCatalogSyncService _rateSync;
        private readonly DispatcherTimer _notificationPollTimer;
        private readonly DispatcherTimer _userRatesSyncTimer;
        private bool _isServerNotificationCheckRunning;
        private int _lastMaterialsVersion;
        private int _lastLabourVersion;
        private int _lastComputeVersion;
        private int _lastRatesVersion;

        /* ───────── current user / auth ───────── */
        private bool _hasPriceNotifications;
        public bool HasPriceNotifications
        {
            get => _hasPriceNotifications;
            set { _hasPriceNotifications = value; RaisePropertyChanged(); }
        }

        private string _notificationMessage = "";
        public string NotificationMessage
        {
            get => _notificationMessage;
            set { _notificationMessage = value; RaisePropertyChanged(); }
        }

        private bool _isExportVisible;
        public bool IsExportVisible
        {
            get => _isExportVisible;
            set { _isExportVisible = value; RaisePropertyChanged(); }
        }

        private UserModel? _currentUser;
        public UserModel? CurrentUser
        {
            get => _currentUser;
            set
            {
                _currentUser = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CurrentUsername));

                if (_currentUser != null)
                    IsExportVisible = ExportVisibilityService.ShouldShowOnThisLogin(DateTime.Now);
                else
                    IsExportVisible = false;
            }
        }

        public ICommand RefreshCloudDataCommand { get; }

        private string _cloudSyncStatus = "Cloud sync: not started";
        public string CloudSyncStatus
        {
            get => _cloudSyncStatus;
            set { _cloudSyncStatus = value; RaisePropertyChanged(); }
        }

        private DateTime? _lastCloudSyncAt;
        public DateTime? LastCloudSyncAt
        {
            get => _lastCloudSyncAt;
            set { _lastCloudSyncAt = value; RaisePropertyChanged(); }
        }


        public ObservableCollection<string> Notifications { get; } = new ObservableCollection<string>();

        private bool _isNotificationsOpen;
        public bool IsNotificationsOpen
        {
            get => _isNotificationsOpen;
            set { _isNotificationsOpen = value; RaisePropertyChanged(); }
        }

        public string CurrentUsername => _currentUser?.Username ?? string.Empty;

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; RaisePropertyChanged(); }
        }

        private string _busyMessage = "Loading…";
        public string BusyMessage
        {
            get => _busyMessage;
            set { _busyMessage = value; RaisePropertyChanged(); }
        }

        /* Richard's loading system: work with stages names them, and the bar
           moves when a stage finishes (View/Suite/SuiteProgress). Null steps =
           a labelled pulse, for work that has no stages to name. */
        private IList<string>? _busySteps;
        public IList<string>? BusySteps
        {
            get => _busySteps;
            set { _busySteps = value; RaisePropertyChanged(); }
        }

        private int _busyStep;
        public int BusyStep
        {
            get => _busyStep;
            set { _busyStep = value; RaisePropertyChanged(); }
        }

        private void MarkStep(int step)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) BusyStep = step;
            else d.Invoke(() => BusyStep = step);
        }

        private static readonly string[] SyncSteps =
        {
            "Reaching ADLM and the rate library",
            "Bringing down the rate build-ups",
            "Bringing prices down for your area",
            "Keeping your custom rates as they are",
        };

        private void SetBusy(bool isBusy, string? message = null)
        {
            if (!isBusy) { BusySteps = null; BusyStep = 0; }
            var app = Application.Current;
            if (app?.Dispatcher == null)
            {
                IsBusy = isBusy;
                if (!string.IsNullOrWhiteSpace(message)) BusyMessage = message;
                return;
            }

            if (app.Dispatcher.CheckAccess())
            {
                IsBusy = isBusy;
                if (!string.IsNullOrWhiteSpace(message)) BusyMessage = message;
            }
            else
            {
                app.Dispatcher.Invoke(() =>
                {
                    IsBusy = isBusy;
                    if (!string.IsNullOrWhiteSpace(message)) BusyMessage = message;
                });
            }
        }

        /* ───────── login state ───────── */
        private bool _isLoggedIn;
        public bool IsLoggedIn
        {
            get => _isLoggedIn;
            set { _isLoggedIn = value; RaisePropertyChanged(); }
        }

        /* ───────── sidebar “active” flags ───────── */
        private bool _isLibraryShellActive;
        public bool IsLibraryShellActive { get => _isLibraryShellActive; set { _isLibraryShellActive = value; RaisePropertyChanged(); } }

        private bool _isMaterialInputActive;
        public bool IsMaterialInputActive { get => _isMaterialInputActive; set { _isMaterialInputActive = value; RaisePropertyChanged(); } }

        private bool _isMaterialLibraryActive;
        public bool IsMaterialLibraryActive { get => _isMaterialLibraryActive; set { _isMaterialLibraryActive = value; RaisePropertyChanged(); } }

        private bool _isLabourInputActive;
        public bool IsLabourInputActive { get => _isLabourInputActive; set { _isLabourInputActive = value; RaisePropertyChanged(); } }

        private bool _isLabourLibraryActive;
        public bool IsLabourLibraryActive { get => _isLabourLibraryActive; set { _isLabourLibraryActive = value; RaisePropertyChanged(); } }

        private bool _isGroundworkActive;
        public bool IsGroundworkActive { get => _isGroundworkActive; set { _isGroundworkActive = value; RaisePropertyChanged(); } }

        private bool _isConcreteViewActive;
        public bool IsConcreteViewActive { get => _isConcreteViewActive; set { _isConcreteViewActive = value; RaisePropertyChanged(); } }

        private bool _isMepWorkActive;
        public bool IsMepWorkActive { get => _isMepWorkActive; set { _isMepWorkActive = value; RaisePropertyChanged(); } }

        private bool _isBlockworkActive;
        public bool IsBlockworkActive { get => _isBlockworkActive; set { _isBlockworkActive = value; RaisePropertyChanged(); } }

        private bool _isFinishesActive;
        public bool IsFinishesActive { get => _isFinishesActive; set { _isFinishesActive = value; RaisePropertyChanged(); } }

        private bool _isRoofworkActive;
        public bool IsRoofworkActive { get => _isRoofworkActive; set { _isRoofworkActive = value; RaisePropertyChanged(); } }

        private bool _isWindowAndDoorActive;
        public bool IsWindowAndDoorActive { get => _isWindowAndDoorActive; set { _isWindowAndDoorActive = value; RaisePropertyChanged(); } }

        private bool _isPaintingActive;
        public bool IsPaintingActive { get => _isPaintingActive; set { _isPaintingActive = value; RaisePropertyChanged(); } }

        private bool _isSteelworkActive;
        public bool IsSteelworkActive { get => _isSteelworkActive; set { _isSteelworkActive = value; RaisePropertyChanged(); } }

        private bool _isCustomRateInputActive;
        public bool IsCustomRateInputActive { get => _isCustomRateInputActive; set { _isCustomRateInputActive = value; RaisePropertyChanged(); } }

        /* ───────── sidebar collapse ───────── */
        public const double SidebarExpandedWidth = 236;
        public const double SidebarCollapsedWidth = 64;
        public const double SidebarCollapseThreshold = 120;

        private bool _isSidebarCollapsed;
        public bool IsSidebarCollapsed
        {
            get => _isSidebarCollapsed;
            set { _isSidebarCollapsed = value; RaisePropertyChanged(); }
        }

        public ICommand ToggleSidebarCommand { get; }

        private bool _isCarbonOthersActive;
        public bool IsCarbonOthersActive
        {
            get => _isCarbonOthersActive;
            set { _isCarbonOthersActive = value; RaisePropertyChanged(); }
        }


        public ICommand SelectedCarbonOthersViewCommand { get; }

        /* ───────── child view-models ───────── */
        public SignInViewModel SignInViewModel { get; }
        public MaterialPriceViewModel MaterialPriceViewModel { get; }
        public MaterialLibraryViewModel MaterialLibraryViewModel { get; }
        public LabourPriceViewModel LabourPriceViewModel { get; }
        public LabourLibraryViewModel LabourLibraryViewModel { get; }
        public LibraryShellViewModel LibraryShellViewModel { get; }
        public GroundWorkViewModel GroundWorkViewModel { get; }
        public ConcreteViewModel ConcreteViewModel { get; }
        public BlockworkViewModel BlockworkViewModel { get; }
        public MepWorkViewModel MepWorkViewModel { get; }
        public FinishesViewModel FinishesViewModel { get; }
        public RoofWorkViewModel RoofWorkViewModel { get; }
        public WindowAndDoorViewModel WindowAndDoorViewModel { get; }
        public PaintWorkViewModel PaintWorkViewModel { get; }
        public SteelWorkViewModel SteelWorkViewModel { get; }
        public CustomRateEntryViewModel CustomRateEntryViewModel { get; }
        public CustomRateListViewModel CustomRateListViewModel { get; }
        public CarbonOthersViewModel CarbonOthersViewModel { get; }

        public SearchBoxViewModel GlobalSearch { get; }

        private readonly SearchIndex _index = new();

        /* ───────── View switching ───────── */
        private ViewModelBase _selectedViewModel = null!;
        /// <summary>
        /// Show the library open at one row, so a component in a rate build-up can
        /// be traced to the price behind it and edited there.
        ///
        /// Filters rather than scrolls: the name is put in the library's search box
        /// so the row is the only one on screen and stays found if the user changes
        /// its price and the list re-sorts. Any category filter already applied is
        /// cleared first, since a row outside it would otherwise be filtered out and
        /// the screen would look empty for no visible reason.
        /// </summary>
        public void OpenLibraryAt(string kind, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            var isLabour = string.Equals(kind, "labour", StringComparison.OrdinalIgnoreCase);

            // "All" rather than null: the filter treats them the same, but null
            // leaves the category dropdown showing blank, which reads as a broken
            // control rather than as no filter.
            if (isLabour)
            {
                LabourLibraryViewModel.SelectedLabourCategory = "All";
                LabourLibraryViewModel.SearchTerm = name;
                LibraryShellViewModel.IsLabourTab = true;
            }
            else
            {
                MaterialLibraryViewModel.SelectedMaterialCategory = "All";
                MaterialLibraryViewModel.SearchTerm = name;
                LibraryShellViewModel.IsMaterialTab = true;
            }

            SelectedViewModel = LibraryShellViewModel;
        }

        public ViewModelBase SelectedViewModel
        {
            get => _selectedViewModel;
            set
            {
                _selectedViewModel = value;

                IsLibraryShellActive = value == LibraryShellViewModel;

                // The pricing location is set on the website, so it can change
                // between one look at the library and the next. Re-read it on the
                // way in rather than showing whatever it was at startup.
                if (IsLibraryShellActive) LibraryShellViewModel?.RefreshLocationNote();

                IsMaterialInputActive = value == MaterialPriceViewModel;
                IsMaterialLibraryActive = value == MaterialLibraryViewModel;
                IsLabourInputActive = value == LabourPriceViewModel;
                IsLabourLibraryActive = value == LabourLibraryViewModel;
                IsGroundworkActive = value == GroundWorkViewModel;
                IsConcreteViewActive = value == ConcreteViewModel;
                IsBlockworkActive = value == BlockworkViewModel;
                IsMepWorkActive = value == MepWorkViewModel;
                RaiseServiceFlags();
                IsFinishesActive = value == FinishesViewModel;
                IsRoofworkActive = value == RoofWorkViewModel;
                IsWindowAndDoorActive = value == WindowAndDoorViewModel;
                IsPaintingActive = value == PaintWorkViewModel;
                IsSteelworkActive = value == SteelWorkViewModel;
                IsCustomRateInputActive = value == CustomRateListViewModel;
                IsCarbonOthersActive = value == CarbonOthersViewModel;


                RaisePropertyChanged();
            }
        }

        private static bool IsUnauthorizedText(string? msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return false;

            msg = msg.ToLowerInvariant();
            return msg.Contains("401") ||
                   msg.Contains("403") ||
                   msg.Contains("unauthorized") ||
                   msg.Contains("forbidden") ||
                   msg.Contains("token expired") ||
                   msg.Contains("jwt expired") ||
                   msg.Contains("expired token");
        }



        /* ───────── commands ───────── */
        public ICommand SelectedMaterialInputViewCommand { get; }
        public ICommand SelectedMaterialLibraryViewCommand { get; }
        public ICommand SelectedLabourInputViewCommand { get; }
        public ICommand SelectedLabourLibraryViewCommand { get; }
        public ICommand SelectedLibraryShellViewCommand { get; }
        public ICommand SelectedGroundworkViewCommand { get; }
        public ICommand SelectedConcreteWorkViewCommand { get; }
        public ICommand SelectedBlockworkViewCommand { get; }
        public ICommand SelectedMepWorkViewCommand { get; }

        /* The four services on the rail. One MEP engine prices them all; each entry
           sets which discipline it lists and opens it. */
        public ICommand SelectedMechanicalViewCommand { get; }
        public ICommand SelectedElectricalViewCommand { get; }
        public ICommand SelectedPlumbingViewCommand { get; }
        public ICommand SelectedFireViewCommand { get; }

        private bool IsService(string discipline) =>
            IsMepWorkActive && MepWorkViewModel?.Discipline == discipline;
        public bool IsMechanicalActive => IsService(MepWorkViewModel.Mechanical);
        public bool IsElectricalActive => IsService(MepWorkViewModel.Electrical);
        public bool IsPlumbingActive => IsService(MepWorkViewModel.Plumbing);
        public bool IsFireActive => IsService(MepWorkViewModel.Fire);

        private void RaiseServiceFlags()
        {
            RaisePropertyChanged(nameof(IsMechanicalActive));
            RaisePropertyChanged(nameof(IsElectricalActive));
            RaisePropertyChanged(nameof(IsPlumbingActive));
            RaisePropertyChanged(nameof(IsFireActive));
        }

        private void OpenService(string discipline)
        {
            MepWorkViewModel.Discipline = discipline;
            SelectedViewModel = MepWorkViewModel;
        }
        public ICommand SelectedFinishesViewCommand { get; }
        public ICommand SelectedRoofworkViewCommand { get; }
        public ICommand SelectedWindowAndDoorViewCommand { get; }
        public ICommand SelectedPaintworkViewCommand { get; }
        public ICommand SelectedSteelworkViewCommand { get; }
        public ICommand SelectedCustomRateInputViewCommand { get; }
        public ICommand SelectedCustomRateViewCommand { get; }
        /* ───────── dashboard chrome ───────── */
        private readonly UiPreferences _uiPreferences = UiPreferences.Load();

        /// <summary>
        /// Welcome banner on the dashboard. Collapsing it hands its 260px back to the
        /// rate table below, which roughly doubles the rows visible at once.
        /// </summary>
        public bool IsWelcomeBannerVisible
        {
            get => _uiPreferences.ShowWelcomeBanner;
            set
            {
                if (_uiPreferences.ShowWelcomeBanner == value) return;
                _uiPreferences.ShowWelcomeBanner = value;
                _uiPreferences.Save();
                RaisePropertyChanged();
            }
        }

        public ICommand ToggleWelcomeBannerCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand OpenYoutubeCommand { get; }
        public ICommand HelpCommand { get; }
        public ICommand ShowNotificationCommand { get; }
        public ICommand ToggleNotificationsCommand { get; }
        public ICommand DismissNotificationCommand { get; }
        public ICommand ExportAllRatesCommand { get; }
        public ICommand ExportBillCsvCommand { get; }
        public ICommand RunSanityCheckCommand { get; }

        public MainViewModel(
            MaterialPriceViewModel priceVM,
            MaterialLibraryViewModel libraryVM,
            LabourPriceViewModel labourVM,
            LabourLibraryViewModel labourLibVM,
            GroundWorkViewModel groundworkVM,
            ConcreteViewModel concreteVM,
            BlockworkViewModel blockworkVM,
            MepWorkViewModel mepWorkVM,
            FinishesViewModel finishesVM,
            RoofWorkViewModel roofVM,
            WindowAndDoorViewModel winDoorVM,
            PaintWorkViewModel paintVM,
            SteelWorkViewModel steelVM,
            CarbonOthersViewModel carbonVM,
            LibraryShellViewModel libraryShellVM,
            CustomRateListViewModel customListVM,
            CustomRateEntryViewModel customEntryVM,
            MongoDbService mongoDbService,
            SignInViewModel signInVM)
        {
            _mongoDbService = mongoDbService;

            // ✅ Create rate sync service once
            _rateSync = new RateCatalogSyncService(new HttpClient(), API_BASE_URL);
            _notificationPollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(4)
            };
            _notificationPollTimer.Tick += async (_, _) => await CheckForServerNotificationsAsync();
            _userRatesSyncTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _userRatesSyncTimer.Tick += async (_, _) =>
            {
                _userRatesSyncTimer.Stop();
                await FlushUserRatesCloudSyncAsync();
            };

            ToggleNotificationsCommand = new RelayCommand(_ =>
            {
                IsNotificationsOpen = !IsNotificationsOpen;
                if (IsNotificationsOpen)
                    HasPriceNotifications = false;
            });

            DismissNotificationCommand = new RelayCommand(idxObj =>
            {
                if (idxObj is string message)
                    Notifications.Remove(message);
                else if (idxObj is int i && i >= 0 && i < Notifications.Count)
                    Notifications.RemoveAt(i);

                if (Notifications.Count == 0)
                    HasPriceNotifications = false;

                IsNotificationsOpen = false;
            });

            ShowNotificationCommand = new RelayCommand(_ =>
            {
                ADLMRateGen.Helpers.AppMessage.Show(NotificationMessage, "Price Update");
                HasPriceNotifications = false;
            });

            GlobalSearch = new SearchBoxViewModel(_index);

            MaterialPriceViewModel = priceVM;
            MaterialLibraryViewModel = libraryVM;
            LabourPriceViewModel = labourVM;
            LabourLibraryViewModel = labourLibVM;
            LibraryShellViewModel = libraryShellVM;
            GroundWorkViewModel = groundworkVM;
            ConcreteViewModel = concreteVM;
            BlockworkViewModel = blockworkVM;
            MepWorkViewModel = mepWorkVM;
            FinishesViewModel = finishesVM;
            RoofWorkViewModel = roofVM;
            WindowAndDoorViewModel = winDoorVM;
            PaintWorkViewModel = paintVM;
            SteelWorkViewModel = steelVM;
            CarbonOthersViewModel = carbonVM;

            // Carbon rates are the carbon of every priced rate, from its own build-up
            // (Services/CarbonRates): hand the trades and services over, and rebuild
            // when any of them changes.
            carbonVM.Sources = () =>
                GroundWorkViewModel.GroundworkItems.Select(i => ("Ground", (object)i))
                .Concat(ConcreteViewModel.ConcreteWorkItems.Select(i => ("Concrete", (object)i)))
                .Concat(BlockworkViewModel.BlockworkItems.Select(i => ("Block Works", (object)i)))
                .Concat(FinishesViewModel.FinishesItems.Select(i => ("Finishes", (object)i)))
                .Concat(RoofWorkViewModel.RoofWorkItems.Select(i => ("Roofs", (object)i)))
                .Concat(PaintWorkViewModel.PaintWorkItems.Select(i => ("Painting", (object)i)))
                .Concat(SteelWorkViewModel.SteelWorkItems.Select(i => ("Steel", (object)i)))
                .Concat(WindowAndDoorViewModel.WindowAndDoorItems.Select(i => ("Window and Door", (object)i)))
                .Concat(MepWorkViewModel.MepWorkItems.Select(i => (MepWorkViewModel.DisciplineOf(i.Section), (object)i)))
                .ToList();
            foreach (var coll in new System.Collections.Specialized.INotifyCollectionChanged[]
            {
                GroundWorkViewModel.GroundworkItems, ConcreteViewModel.ConcreteWorkItems, BlockworkViewModel.BlockworkItems,
                FinishesViewModel.FinishesItems, RoofWorkViewModel.RoofWorkItems, PaintWorkViewModel.PaintWorkItems,
                SteelWorkViewModel.SteelWorkItems, WindowAndDoorViewModel.WindowAndDoorItems, MepWorkViewModel.MepWorkItems,
            })
                coll.CollectionChanged += (_, __) => carbonVM.RequestRebuild();
            UserRateEditStore.Current.OverridesChanged += (_, __) => carbonVM.RequestRebuild();
            carbonVM.RequestRebuild();
            CustomRateListViewModel = customListVM;
            CustomRateEntryViewModel = customEntryVM;
            SignInViewModel = signInVM;

            // ✅ notifications from local MongoDbService events
            _mongoDbService.MaterialPricesChanged += () => AddNotification("New material prices available");
            _mongoDbService.LabourPricesChanged += () => AddNotification("New labour prices available");

            // ✅ keep index updated when libraries change
            MaterialLibraryViewModel.LibraryChanged += () =>
            {
                _index.Rebuild(this);
                ScheduleUserRatesCloudSync();
            };
            LabourLibraryViewModel.LibraryChanged += () =>
            {
                _index.Rebuild(this);
                ScheduleUserRatesCloudSync();
            };
            CustomRateListViewModel.LibraryChanged += () =>
            {
                _index.Rebuild(this);
                ScheduleUserRatesCloudSync();
            };

            libraryVM.BusyChanged += (busy, msg) => SetBusy(busy, msg);
            labourLibVM.BusyChanged += (busy, msg) => SetBusy(busy, msg);

            MaterialPriceViewModel.MaterialSaved += m => libraryVM.AddOrUpdateMaterial(m);
            libraryVM.EditMaterialRequested += OnEditMaterialRequested;
            labourVM.LabourSaved += l => labourLibVM.AddOrUpdateLabour(l);
            labourLibVM.EditLabourRequested += OnEditLabourRequested;

            customListVM.OnViewRequested += rate => { customEntryVM.LoadRate(rate); };

            signInVM.LoginSucceeded += OnLoginSucceeded;
            signInVM.ZonePricesApplied += OnZonePricesApplied;

            SelectedViewModel = SignInViewModel;

            // ✅ Configure compute store to the ACTUAL endpoint
            ComputeCatalogStore.ConfigureApi(API_BASE_URL, COMPUTE_ITEMS_PATH);
            ComputeCatalogStore.ReloadFromDisk(); // load cached first

            // ✅ Rate library (admin-created rates)
            RateLibraryStore.ConfigureApi(API_BASE_URL, RATE_LIBRARY_SYNC_PATH);
            RateLibraryStore.ReloadFromDisk();


            // ✅ When rate catalog updates, reload libraries and rebuild index
            _rateSync.CatalogUpdated += msg =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    AddNotification(msg);

                    MaterialLibraryViewModel.ReloadFromDisk();
                    LabourLibraryViewModel.ReloadFromDisk();
                    ComputeCatalogStore.ReloadFromDisk();

                    _index.Rebuild(this);
                });
            };

            // ✅ auto-login
            if (TryAutoLogin(out var tokenUser) && tokenUser != null)
            {
                IsLoggedIn = true;
                CurrentUser = new UserModel
                {
                    Id = tokenUser.Id,
                    Email = tokenUser.Email,
                    Username = !string.IsNullOrWhiteSpace(tokenUser.Username)
                        ? tokenUser.Username
                        : (tokenUser.Email?.Split('@')[0] ?? string.Empty),
                    AvatarUrl = tokenUser.AvatarUrl,
                    FirstName = tokenUser.FirstName,
                    LastName = tokenUser.LastName,
                    Zone = tokenUser.Zone
                };

                SelectedViewModel = LibraryShellViewModel;
                _ = UserLibrarySync.Instance.LoadAsync();
                _ = RefreshCurrentUserProfileAsync();

                // ✅ optional: also check for updates on app start (auto-login path)
                _ = TryCheckUpdatesAfterLoginAsync();
                _ = InitializeNotificationStateAsync();
                StartNotificationPolling();
                ScheduleUserRatesCloudSync();
            }

            // navigation
            SelectedMaterialInputViewCommand = new RelayCommand(_ => SelectedViewModel = priceVM);
            SelectedMaterialLibraryViewCommand = new RelayCommand(_ => SelectedViewModel = LibraryShellViewModel);
            SelectedLibraryShellViewCommand = new RelayCommand(_ => SelectedViewModel = LibraryShellViewModel);
            SelectedLabourInputViewCommand = new RelayCommand(_ => SelectedViewModel = labourVM);
            SelectedLabourLibraryViewCommand = new RelayCommand(_ => SelectedViewModel = labourLibVM);
            SelectedGroundworkViewCommand = new RelayCommand(_ => SelectedViewModel = groundworkVM);
            SelectedConcreteWorkViewCommand = new RelayCommand(_ => SelectedViewModel = concreteVM);
            SelectedBlockworkViewCommand = new RelayCommand(_ => SelectedViewModel = blockworkVM);
            SelectedMepWorkViewCommand = new RelayCommand(_ => SelectedViewModel = mepWorkVM);
            SelectedMechanicalViewCommand = new RelayCommand(_ => OpenService(MepWorkViewModel.Mechanical));
            SelectedElectricalViewCommand = new RelayCommand(_ => OpenService(MepWorkViewModel.Electrical));
            SelectedPlumbingViewCommand = new RelayCommand(_ => OpenService(MepWorkViewModel.Plumbing));
            SelectedFireViewCommand = new RelayCommand(_ => OpenService(MepWorkViewModel.Fire));
            SelectedFinishesViewCommand = new RelayCommand(_ => SelectedViewModel = finishesVM);
            SelectedRoofworkViewCommand = new RelayCommand(_ => SelectedViewModel = roofVM);
            SelectedWindowAndDoorViewCommand = new RelayCommand(_ => SelectedViewModel = winDoorVM);
            SelectedPaintworkViewCommand = new RelayCommand(_ => SelectedViewModel = paintVM);
            SelectedSteelworkViewCommand = new RelayCommand(_ => SelectedViewModel = steelVM);
            SelectedCustomRateInputViewCommand = new RelayCommand(_ => SelectedViewModel = customListVM);
            SelectedCustomRateViewCommand = new RelayCommand(_ => SelectedViewModel = customEntryVM);
            RefreshCloudDataCommand = new RelayCommand(async _ => await RefreshCloudDataAsync(manual: true));

            SelectedCarbonOthersViewCommand = new RelayCommand(_ => SelectedViewModel = carbonVM);



            LogoutCommand = new RelayCommand(_ => Logout());
            ToggleSidebarCommand = new RelayCommand(_ =>
            {
                IsSidebarCollapsed = !IsSidebarCollapsed;
                // Actual column width change is handled in MainWindow code-behind
            });
            OpenYoutubeCommand = new RelayCommand(_ => OpenYoutube());
            HelpCommand = new RelayCommand(_ => SendHelpEmail());
            ToggleWelcomeBannerCommand = new RelayCommand(_ => IsWelcomeBannerVisible = !IsWelcomeBannerVisible);
            ExportAllRatesCommand = new RelayCommand(_ => ExportAllToExcel());
            ExportBillCsvCommand = new RelayCommand(_ => ExportBillToCsv());

            RunSanityCheckCommand = new RelayCommand(async _ => await RunSanityCheckAsync());

            // initial index
            _index.Rebuild(this);

            // (optional) index rebuild when any section changes
            GroundWorkViewModel.PropertyChanged += (_, __) => _index.Rebuild(this);
            ConcreteViewModel.PropertyChanged += (_, __) => _index.Rebuild(this);
            BlockworkViewModel.PropertyChanged += (_, __) => _index.Rebuild(this);
            MepWorkViewModel.PropertyChanged += (_, __) => _index.Rebuild(this);
            FinishesViewModel.PropertyChanged += (_, __) => _index.Rebuild(this);
            RoofWorkViewModel.PropertyChanged += (_, __) => _index.Rebuild(this);
            WindowAndDoorViewModel.PropertyChanged += (_, __) => _index.Rebuild(this);
            PaintWorkViewModel.PropertyChanged += (_, __) => _index.Rebuild(this);
            SteelWorkViewModel.PropertyChanged += (_, __) => _index.Rebuild(this);
            CarbonOthersViewModel.PropertyChanged += (_, __) => _index.Rebuild(this);


            // One wiring for every section's breakdown. Each line binds straight to
            // LibraryLink.OpenCommand, so nothing has to be threaded through the
            // other nine view models.
            Helpers.LibraryLink.Navigate = OpenLibraryAt;
            Helpers.LibraryLink.IsMaterial = n =>
                MaterialLibraryViewModel.MaterialLibrary.Any(
                    m => string.Equals(m.MaterialName, n, StringComparison.OrdinalIgnoreCase));
            Helpers.LibraryLink.IsLabour = n =>
                LabourLibraryViewModel.LabourLibrary.Any(
                    l => string.Equals(l.LabourName, n, StringComparison.OrdinalIgnoreCase));
            Helpers.LibraryLink.MaterialContains = n =>
                MaterialLibraryViewModel.MaterialLibrary.Any(
                    m => m.MaterialName != null &&
                         m.MaterialName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            Helpers.LibraryLink.LabourContains = n =>
                LabourLibraryViewModel.LabourLibrary.Any(
                    l => l.LabourName != null &&
                         l.LabourName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            GroundWorkViewModel.PropertyChanged += OnSectionRateStateChanged;
            ConcreteViewModel.PropertyChanged += OnSectionRateStateChanged;
            BlockworkViewModel.PropertyChanged += OnSectionRateStateChanged;
            FinishesViewModel.PropertyChanged += OnSectionRateStateChanged;
            RoofWorkViewModel.PropertyChanged += OnSectionRateStateChanged;
            WindowAndDoorViewModel.PropertyChanged += OnSectionRateStateChanged;
            PaintWorkViewModel.PropertyChanged += OnSectionRateStateChanged;
            SteelWorkViewModel.PropertyChanged += OnSectionRateStateChanged;
            CarbonOthersViewModel.PropertyChanged += OnSectionRateStateChanged;

        }

        private void OnSectionRateStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(GroundWorkViewModel.OverheadPercent) or nameof(GroundWorkViewModel.ProfitPercent))
                ScheduleUserRatesCloudSync();
        }

        private void ScheduleUserRatesCloudSync()
        {
            if (!IsLoggedIn || !AuthProvider.Instance.Client.HasSession)
                return;

            if (Application.Current?.Dispatcher == null)
                return;

            void RestartTimer()
            {
                _userRatesSyncTimer.Stop();
                _userRatesSyncTimer.Start();
            }

            if (Application.Current.Dispatcher.CheckAccess())
                RestartTimer();
            else
                Application.Current.Dispatcher.Invoke(RestartTimer);
        }

        public async Task<bool> FlushUserRatesCloudSyncAsync()
        {
            if (!IsLoggedIn || !AuthProvider.Instance.Client.HasSession)
                return false;

            var ok = await UserRatesCloudSync.Instance.PushSnapshotAsync(this).ConfigureAwait(false);

            Application.Current?.Dispatcher?.Invoke(() =>
            {
                CloudSyncStatus = UserRatesCloudSync.Instance.LastStatus;

                if (ok)
                {
                    LastCloudSyncAt = DateTime.Now;
                }
                else if (!string.IsNullOrWhiteSpace(CloudSyncStatus))
                {
                    AddNotification(CloudSyncStatus);
                }
            });

            return ok;
        }

        /// <summary>
        /// One-line reason for a sync step that threw. Unwraps the aggregate/inner
        /// exception so the caller sees the server's message rather than a wrapper.
        /// </summary>
        private static string Describe(Exception ex)
        {
            var e = ex;
            while (e.InnerException != null && (e is AggregateException || string.IsNullOrWhiteSpace(e.Message)))
                e = e.InnerException;

            var msg = (e.Message ?? "").Trim();
            if (e is TaskCanceledException || e is TimeoutException)
                msg = string.IsNullOrWhiteSpace(msg) ? "Timed out." : msg;
            if (string.IsNullOrWhiteSpace(msg))
                msg = e.GetType().Name;

            return msg.Length > 200 ? msg.Substring(0, 200) + "…" : msg;
        }

        private static bool Is404(Exception ex)
        {
            if (ex is HttpRequestException hre)
            {
#if NET8_0_OR_GREATER
                if (hre.StatusCode.HasValue && hre.StatusCode.Value == System.Net.HttpStatusCode.NotFound)
                    return true;
#endif
                // fallback if StatusCode isn't available in your build
                if ((hre.Message ?? "").Contains("404")) return true;
            }
            return (ex.Message ?? "").Contains("404");
        }

        private async Task RefreshCloudDataAsync(bool manual)
        {
            var results = new List<string>();

            try
            {
                var cfg = ConfigManager.LoadConfig() ?? new AppConfig();
                var token = cfg.AuthToken;

                // also treat expired local expiry as sign-in required
                if (string.IsNullOrWhiteSpace(token) || cfg.AuthExpiry <= DateTime.Now)
                {
                    RequireSignInAgain(manual);
                    return;
                }

                BusySteps = SyncSteps;
                BusyStep = 0;
                SetBusy(true, manual ? "Checking the library for updates" : "Bringing your library up to date");
                CloudSyncStatus = "Cloud sync: running…";

                // ✅ 1) Admin-created Rate Library (DB rates)
                bool ratesOk = false;
                try
                {
                    RateLibraryStore.ReloadFromDisk();
                    ratesOk = await RateLibraryStore.RefreshFromApiAsync(); // fetch all sections
                    RateLibraryStore.ReloadFromDisk();

                    if (!ratesOk && IsUnauthorizedText(RateLibraryStore.LastApiMessage))
                    {
                        RequireSignInAgain(manual);
                        return;
                    }

                    results.Add($"Rates: {(ratesOk ? "OK" : "FAIL")} ({RateLibraryStore.LastApiItemCount})");
                }
                catch (Exception ex)
                {
                    if (IsUnauthorizedException(ex))
                    {
                        RequireSignInAgain(manual);
                        return;
                    }

                    results.Add($"Rates: FAIL — {Describe(ex)}");
                }

                MarkStep(1);

                // ✅ 2) Compute Catalog
                bool computeOk = false;
                try
                {
                    ComputeCatalogStore.ReloadFromDisk();
                    computeOk = await ComputeCatalogStore.RefreshFromApiAsync();
                    ComputeCatalogStore.ReloadFromDisk();

                    if (!computeOk && IsUnauthorizedText(ComputeCatalogStore.LastApiMessage))
                    {
                        RequireSignInAgain(manual);
                        return;
                    }

                    results.Add($"Compute: {(computeOk ? "OK" : "FAIL")} ({ComputeCatalogStore.LastApiItemCount})");
                }
                catch (Exception ex)
                {
                    if (IsUnauthorizedException(ex))
                    {
                        RequireSignInAgain(manual);
                        return;
                    }

                    if (Is404(ex))
                        results.Add("Compute: SKIPPED (404 Not Found)");
                    else
                        results.Add($"Compute: FAIL — {Describe(ex)}");
                }

                // ✅ 3) Materials/labour master prices — single source of truth.
                // Pulls the signed-in zone's prices from /rategen/master (the
                // admin library the website, QUIV and the AI service all use)
                // and persists them into the local library files.
                // (Replaces RateCatalogSyncService.CheckAndPromptUpdateAsync,
                // which wrote raw server arrays to a folder nothing reads.)
                try
                {
                    MarkStep(2);
                    var masterSync = await Services.MasterLibrarySyncService.SyncAsync();
                    results.Add(masterSync.Ok
                        ? $"Master prices ({masterSync.Zone}): OK ({masterSync.Materials} materials, {masterSync.Labours} labour)"
                        : $"Master prices: {masterSync.Message}");
                }
                catch (Exception ex)
                {
                    if (IsUnauthorizedException(ex))
                    {
                        RequireSignInAgain(manual);
                        return;
                    }

                    if (Is404(ex))
                        results.Add("Master prices: SKIPPED (404 Not Found)");
                    else
                        // The exception already carries the HTTP status and the
                        // server's own error text; discarding it left the dialog
                        // saying only "FAIL", which is undiagnosable from a
                        // customer's screenshot.
                        results.Add($"Master prices: FAIL — {Describe(ex)}");
                }

                // Refresh local UI
                MarkStep(3);
                MaterialLibraryViewModel.ReloadFromDisk();
                LabourLibraryViewModel.ReloadFromDisk();

                // The sync is what learns the account's pricing location: the server
                // answers with the state it priced from and MasterLibrarySyncService
                // persists it. Until now only navigating into the library re-read
                // that, so a user already sitting on the library watched the note
                // keep the old state while the prices underneath were the new one.
                LibraryShellViewModel?.RefreshLocationNote();

                _index.Rebuild(this);

                LastCloudSyncAt = DateTime.Now;
                CloudSyncStatus = "Cloud sync: done";
                AddNotification(CloudSyncStatus);
                await InitializeNotificationStateAsync();
                ScheduleUserRatesCloudSync();

                MarkStep(SyncSteps.Length);
                if (manual)
                {
                    SetBusy(false);
                    View.Suite.SuiteDialog.Tell("Library checked",
                        string.Join("\n", results) + "\n\nYour custom rates were left alone.",
                        View.Suite.SuiteDialog.Tone.Success);
                }
            }
            catch (Exception ex)
            {
                // if it’s auth, show the clean message
                if (IsUnauthorizedException(ex))
                {
                    RequireSignInAgain(manual);
                    return;
                }

                CloudSyncStatus = $"Cloud sync: failed";
                AddNotification(CloudSyncStatus);

                if (manual)
                {
                    SetBusy(false);
                    View.Suite.SuiteDialog.Tell("The library could not be checked",
                        "ADLM could not be reached just now. RateGen keeps working with the prices already on this PC and will try again the next time you sync.",
                        View.Suite.SuiteDialog.Tone.Warning);
                }
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void OnZonePricesApplied(string zone)
        {
            SetBusy(true, $"Updating prices for {zone.Replace('_', ' ')}…");

            Application.Current.Dispatcher.Invoke(() =>
            {
                MaterialLibraryViewModel.ReloadFromDisk();
                LabourLibraryViewModel.ReloadFromDisk();
                ComputeCatalogStore.ReloadFromDisk();
                AddNotification($"Prices updated for {zone.Replace('_', ' ')}");
            });

            _index.Rebuild(this);
            ScheduleUserRatesCloudSync();
            SetBusy(false);
        }

        private void AddNotification(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
                return;

            Notifications.Remove(msg);
            Notifications.Insert(0, msg);
            while (Notifications.Count > 5)
                Notifications.RemoveAt(Notifications.Count - 1);

            RaisePropertyChanged(nameof(Notifications));
            HasPriceNotifications = true;
        }

        private void StartNotificationPolling()
        {
            if (!_notificationPollTimer.IsEnabled)
                _notificationPollTimer.Start();
        }

        private void StopNotificationPolling()
        {
            if (_notificationPollTimer.IsEnabled)
                _notificationPollTimer.Stop();

            _isServerNotificationCheckRunning = false;
        }

        private async Task InitializeNotificationStateAsync()
        {
            var snapshot = await FetchLibraryMetaAsync();
            if (snapshot == null)
                return;

            _lastMaterialsVersion = snapshot.MaterialsVersion;
            _lastLabourVersion = snapshot.LabourVersion;
            _lastComputeVersion = snapshot.ComputeVersion;
            _lastRatesVersion = snapshot.RatesVersion;
        }

        private async Task CheckForServerNotificationsAsync()
        {
            if (!IsLoggedIn || _isServerNotificationCheckRunning)
                return;

            _isServerNotificationCheckRunning = true;

            try
            {
                var snapshot = await FetchLibraryMetaAsync();
                if (snapshot == null)
                    return;

                var hasBaseline =
                    _lastMaterialsVersion > 0 ||
                    _lastLabourVersion > 0 ||
                    _lastComputeVersion > 0 ||
                    _lastRatesVersion > 0;

                if (!hasBaseline)
                {
                    _lastMaterialsVersion = snapshot.MaterialsVersion;
                    _lastLabourVersion = snapshot.LabourVersion;
                    _lastComputeVersion = snapshot.ComputeVersion;
                    _lastRatesVersion = snapshot.RatesVersion;
                    return;
                }

                var cfg = ConfigManager.LoadConfig() ?? new AppConfig();
                var token = cfg.AuthToken;
                var zone = ADLMRateGen.Properties.AppSettings.Zone ?? cfg.Zone ?? "Lagos";

                if (string.IsNullOrWhiteSpace(token))
                    return;

                var materialChanged = snapshot.MaterialsVersion > _lastMaterialsVersion;
                var labourChanged = snapshot.LabourVersion > _lastLabourVersion;
                var computeChanged = snapshot.ComputeVersion > _lastComputeVersion;
                var ratesChanged = snapshot.RatesVersion > _lastRatesVersion;

                if (!materialChanged && !labourChanged && !computeChanged && !ratesChanged)
                    return;

                if (materialChanged)
                    AddNotification("Material prices or materials were updated by admin.");

                if (labourChanged)
                    AddNotification("Labour prices or labour items were updated by admin.");

                if (ratesChanged)
                    AddNotification("New or updated default rates are available.");

                if (computeChanged)
                    AddNotification("Rate computation rules were updated by admin.");

                if (materialChanged || labourChanged)
                {
                    await _rateSync.CheckAndPromptUpdateAsync(
                        token,
                        zone,
                        _ => Task.FromResult(true));

                    MaterialLibraryViewModel.ReloadFromDisk();
                    LabourLibraryViewModel.ReloadFromDisk();
                }

                if (computeChanged)
                {
                    await ComputeCatalogStore.RefreshFromApiAsync();
                    ComputeCatalogStore.ReloadFromDisk();
                }

                if (ratesChanged)
                {
                    await RateLibraryStore.RefreshFromApiAsync();
                    RateLibraryStore.ReloadFromDisk();
                }

                _lastMaterialsVersion = snapshot.MaterialsVersion;
                _lastLabourVersion = snapshot.LabourVersion;
                _lastComputeVersion = snapshot.ComputeVersion;
                _lastRatesVersion = snapshot.RatesVersion;

                _index.Rebuild(this);
            }
            catch (Exception ex)
            {
                if (IsUnauthorizedException(ex))
                    RequireSignInAgain(manual: false);
            }
            finally
            {
                _isServerNotificationCheckRunning = false;
            }
        }

        private static async Task<LibraryMetaSnapshot?> FetchLibraryMetaAsync()
        {
            var cfg = ConfigManager.LoadConfig() ?? new AppConfig();
            var token = cfg.AuthToken;

            if (string.IsNullOrWhiteSpace(token))
                return null;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await http.GetAsync($"{API_BASE_URL}{LIBRARY_META_PATH}");
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new InvalidOperationException($"{(int)resp.StatusCode} {resp.ReasonPhrase}");

            if (!resp.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("meta", out var meta))
                return null;

            return new LibraryMetaSnapshot
            {
                MaterialsVersion = ReadVersion(meta, "materials"),
                LabourVersion = ReadVersion(meta, "labour"),
                ComputeVersion = ReadVersion(meta, "compute"),
                RatesVersion = ReadVersion(meta, "rates")
            };
        }

        private static int ReadVersion(JsonElement meta, string name)
        {
            if (!meta.TryGetProperty(name, out var section))
                return 0;

            return section.TryGetProperty("version", out var version) && version.TryGetInt32(out var value)
                ? value
                : 0;
        }

        private sealed class LibraryMetaSnapshot
        {
            public int MaterialsVersion { get; init; }
            public int LabourVersion { get; init; }
            public int ComputeVersion { get; init; }
            public int RatesVersion { get; init; }
        }



        private static bool IsUnauthorizedException(Exception ex)
        {
            if (ex == null) return false;

            if (ex is HttpRequestException hre)
            {
#if NET8_0_OR_GREATER
                if (hre.StatusCode.HasValue && hre.StatusCode.Value == System.Net.HttpStatusCode.Unauthorized)
                    return true;
#endif
                if (IsUnauthorizedText(hre.Message)) return true;
            }

            return IsUnauthorizedText(ex.Message);
        }

        private void RequireSignInAgain(bool manual)
        {
            try
            {
                StopNotificationPolling();
                AuthProvider.Instance.Client.SignOut();

                var cfg = ConfigManager.LoadConfig() ?? new AppConfig();
                cfg.AuthToken = null;
                cfg.AuthExpiry = DateTime.MinValue;
                ConfigManager.SaveConfig(cfg);
            }
            catch { }

            // force UI back to sign-in so user isn’t confused
            IsLoggedIn = false;
            CurrentUser = null;
            HasPriceNotifications = false;
            IsNotificationsOpen = false;
            SelectedViewModel = SignInViewModel;

            CloudSyncStatus = "Cloud sync: Please sign in again.";
            AddNotification(CloudSyncStatus);

            if (manual)
            {
                ADLMRateGen.Helpers.AppMessage.Show(
                    "Please sign in again.",
                    "Sync from Cloud",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }
        }
        private bool TryAutoLogin(out UserModel? user)
        {
            user = null;

            var cfg = ConfigManager.LoadConfig();
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.AuthToken))
                return false;

            if (cfg.AuthExpiry < DateTime.Now)
                return false;

            var decoded = JwtHelper.TryReadUser(cfg.AuthToken);
            if (decoded == null)
                return false;

            user = decoded;
            return true;
        }

        private void OnEditMaterialRequested(MaterialModel m) { }
        private void OnEditLabourRequested(LabourModel l) { }

        //private async void OnLoginSucceeded(object? s, SignInViewModel.LoginEventArgs e)
        //{
        //    if (e.LoggedInUser == null) return;

        //    string ensuredUsername = !string.IsNullOrWhiteSpace(e.LoggedInUser.Username)
        //        ? e.LoggedInUser.Username
        //        : (e.LoggedInUser.Email?.Split('@')[0] ?? string.Empty);

        //    IsLoggedIn = true;
        //    CurrentUser = new UserModel
        //    {
        //        Id = e.LoggedInUser.Id,
        //        Email = e.LoggedInUser.Email,
        //        Username = ensuredUsername
        //    };

        //    // keep your existing token storage logic
        //    var authTok = new AuthTok();
        //    ConfigManager.SaveConfig(new AppConfig
        //    {
        //        AuthToken = authTok.GenerateAuthToken(CurrentUser),
        //        AuthExpiry = DateTime.Now.AddDays(15)
        //    });

        //    SelectedViewModel = LibraryShellViewModel;

        //    // Load cached libraries immediately
        //    _ = UserLibrarySync.Instance.LoadAsync();

        //    // Now do API update checks + compute refresh
        //    await TryCheckUpdatesAfterLoginAsync();
        //}

        private async void OnLoginSucceeded(object? s, SignInViewModel.LoginEventArgs e)
        {
            if (e.LoggedInUser == null) return;

            string ensuredUsername = !string.IsNullOrWhiteSpace(e.LoggedInUser.Username)
                ? e.LoggedInUser.Username
                : (e.LoggedInUser.Email?.Split('@')[0] ?? string.Empty);

            IsLoggedIn = true;
            CurrentUser = new UserModel
            {
                Id = e.LoggedInUser.Id,
                Email = e.LoggedInUser.Email,
                Username = ensuredUsername,
                AvatarUrl = e.LoggedInUser.AvatarUrl,
                FirstName = e.LoggedInUser.FirstName,
                LastName = e.LoggedInUser.LastName,
                Zone = e.LoggedInUser.Zone
            };

            var cfg = ConfigManager.LoadConfig() ?? new AppConfig();
            cfg.AuthToken = e.AccessToken;
            cfg.AuthExpiry = JwtHelper.TryGetExpiryLocal(e.AccessToken) ?? DateTime.Now.AddMinutes(25);
            ConfigManager.SaveConfig(cfg);

            SelectedViewModel = LibraryShellViewModel;

            // Load cached first
            _ = UserLibrarySync.Instance.LoadAsync();
            await RefreshCurrentUserProfileAsync();

            // Now re-check APIs with valid token
            await TryCheckUpdatesAfterLoginAsync();

            await RefreshCloudDataAsync(manual: false);
            await InitializeNotificationStateAsync();
            StartNotificationPolling();
            ScheduleUserRatesCloudSync();

            // Pull saved rate-build-up Quantity overrides from the cloud so edits made on
            // QUIV / HERON show up here. Fires UserRateEditStore.OverridesChanged, which each
            // section VM listens to and triggers a RecomputeAll.
            try
            {
                await UserRatesCloudSync.Instance.PullUserEditsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainViewModel.OnLoginSucceeded] PullUserEditsAsync failed: {ex.Message}");
            }

        }

        // ✅ This is the real “does it fetch rates + apply to library” flow
        private async Task TryCheckUpdatesAfterLoginAsync()
        {
            try
            {
                var cfg = ConfigManager.LoadConfig() ?? new AppConfig();
                var zone = ADLMRateGen.Properties.AppSettings.Zone ?? cfg.Zone ?? "Lagos";
                var token = cfg.AuthToken;

                if (string.IsNullOrWhiteSpace(token))
                {
                    AddNotification("⚠ Update check skipped: missing token.");
                    return;
                }

                SetBusy(true, "Checking for latest library + compute updates…");

                // 1) Probe library meta endpoint (token/entitlement check)
                var metaOk = await ProbeLibraryMetaAsync(token);
                if (!metaOk)
                    AddNotification($"⚠ Token/entitlement probe failed for GET {LIBRARY_META_PATH}");

                // 2) Sync materials/labour library (writes to disk) + triggers CatalogUpdated
                await _rateSync.CheckAndPromptUpdateAsync(
                    token,
                    zone,
                    async (prompt) =>
                    {
                        var result = ADLMRateGen.Helpers.AppMessage.Show(
                            prompt,
                            "Rate Update",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Information);

                        return await Task.FromResult(result == MessageBoxResult.Yes);
                    });

                // 3) Refresh compute items
                var ok = await ComputeCatalogStore.RefreshFromApiAsync();
                AddNotification(ok
                    ? $"✅ Compute items updated ({ComputeCatalogStore.LastApiItemCount})"
                    : $"⚠ Compute update failed: {ComputeCatalogStore.LastApiMessage}");

                // 4) Ensure UI uses latest disk snapshot
                RateLibraryStore.ReloadFromDisk();
                MaterialLibraryViewModel.ReloadFromDisk();
                LabourLibraryViewModel.ReloadFromDisk();
                ComputeCatalogStore.ReloadFromDisk();

                // 3b) Refresh admin-created Rate Library (rategenrates)
                var ratesOk = await RateLibraryStore.RefreshFromApiAsync();
                AddNotification(ratesOk
                    ? $"✅ Rate library updated ({RateLibraryStore.LastApiItemCount})"
                    : $"⚠ Rate library update failed: {RateLibraryStore.LastApiMessage}");


                _index.Rebuild(this);
            }
            catch (Exception ex)
            {
                AddNotification($"Update check failed: {ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ✅ Probe using an explicit Bearer token (used by sanity check + update flow)
        private static async Task<bool> ProbeLibraryMetaAsync(string bearerToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(bearerToken))
                    return false;

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", bearerToken);
                http.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                var resp = await http.GetAsync($"{API_BASE_URL}{LIBRARY_META_PATH}");
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        // ✅ Optional: probe using your AuthProvider client (if you already use it elsewhere)
        private static async Task<bool> ProbeLibraryMetaAsync()
        {
            try
            {
                await AuthProvider.Instance.Client.GetJsonAsync(LIBRARY_META_PATH);
                return true;
            }
            catch
            {
                return false;
            }
        }



        private async Task RunSanityCheckAsync()
        {
            try
            {
                SetBusy(true, "Running sanity check...");

                var issues = new List<string>();

                // 1) ViewModels present
                if (MaterialPriceViewModel == null) issues.Add("MaterialPriceViewModel is null");
                if (MaterialLibraryViewModel == null) issues.Add("MaterialLibraryViewModel is null");
                if (LabourPriceViewModel == null) issues.Add("LabourPriceViewModel is null");
                if (LabourLibraryViewModel == null) issues.Add("LabourLibraryViewModel is null");
                if (GroundWorkViewModel == null) issues.Add("GroundWorkViewModel is null");
                if (ConcreteViewModel == null) issues.Add("ConcreteViewModel is null");
                if (BlockworkViewModel == null) issues.Add("BlockworkViewModel is null");
                if (FinishesViewModel == null) issues.Add("FinishesViewModel is null");
                if (RoofWorkViewModel == null) issues.Add("RoofWorkViewModel is null");
                if (WindowAndDoorViewModel == null) issues.Add("WindowAndDoorViewModel is null");
                if (PaintWorkViewModel == null) issues.Add("PaintWorkViewModel is null");
                if (SteelWorkViewModel == null) issues.Add("SteelWorkViewModel is null");
                if (CustomRateListViewModel == null) issues.Add("CustomRateListViewModel is null");
                if (CustomRateEntryViewModel == null) issues.Add("CustomRateEntryViewModel is null");
                if (LibraryShellViewModel == null) issues.Add("LibraryShellViewModel is null");

                // 2) Disk files existence
                var computePath = ComputeCatalogStore.FilePath;
                if (!File.Exists(computePath))
                    issues.Add($"Compute items file missing: {computePath}");

                // 3) Load compute items
                ComputeCatalogStore.ReloadFromDisk();
                var items = ComputeCatalogStore.Items; // IReadOnlyList<ComputeItemDefinition>
                if (items == null || items.Count == 0)
                    issues.Add("Compute items loaded = 0 (compute-items.json empty or invalid).");

                // 4) API reachability + auth checks
                var cfg = ConfigManager.LoadConfig();
                var token = cfg?.AuthToken;

                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                    var ping = await http.GetAsync($"{API_BASE_URL}/__debug/db");
                    if (!ping.IsSuccessStatusCode)
                        issues.Add($"API reachable but /__debug/db returned {(int)ping.StatusCode}");
                }
                catch (Exception ex)
                {
                    issues.Add($"API check failed: {ex.Message}");
                }

                // 5) Token/entitlement checks
                if (!string.IsNullOrWhiteSpace(token))
                {
                    var metaOk = await ProbeLibraryMetaAsync(token);
                    if (!metaOk)
                        issues.Add($"Token/entitlement probe failed for GET {LIBRARY_META_PATH}");

                    // Compute endpoint status check
                    try
                    {
                        using var http2 = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                        http2.DefaultRequestHeaders.Authorization =
                            new AuthenticationHeaderValue("Bearer", token);
                        http2.DefaultRequestHeaders.Accept.Add(
                            new MediaTypeWithQualityHeaderValue("application/json"));

                        var cResp = await http2.GetAsync($"{API_BASE_URL}{COMPUTE_ITEMS_PATH}");
                        if (!cResp.IsSuccessStatusCode)
                            issues.Add($"Compute endpoint failed: GET {COMPUTE_ITEMS_PATH} => {(int)cResp.StatusCode}");
                    }
                    catch (Exception ex)
                    {
                        issues.Add($"Compute endpoint check threw: {ex.Message}");
                    }
                }
                else
                {
                    issues.Add("Skipped auth checks: no saved auth token (not logged in).");
                }

                // Rate Library endpoint status check
                try
                {
                    using var http3 = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    http3.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", token);
                    http3.DefaultRequestHeaders.Accept.Add(
                        new MediaTypeWithQualityHeaderValue("application/json"));

                    var rResp = await http3.GetAsync($"{API_BASE_URL}{RATE_LIBRARY_SYNC_PATH}?limit=1&sectionKey=ground");
                    if (!rResp.IsSuccessStatusCode)
                        issues.Add($"Rate library endpoint failed: GET {RATE_LIBRARY_SYNC_PATH} => {(int)rResp.StatusCode}");
                }
                catch (Exception ex)
                {
                    issues.Add($"Rate library endpoint check threw: {ex.Message}");
                }


                if (issues.Count == 0)
                {
                    ADLMRateGen.Helpers.AppMessage.Show(
                        "✅ Sanity Check PASSED\n\nLibraries + compute file detected.\nAPI reachable.\nMeta endpoint probe passed.",
                        "ADLM RateGen · Sanity Check",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    return;
                }

                ADLMRateGen.Helpers.AppMessage.Show(
                    "⚠️ Sanity Check FOUND ISSUES:\n\n- " + string.Join("\n- ", issues),
                    "ADLM RateGen · Sanity Check",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }
            finally
            {
                SetBusy(false);
            }
        }


        private void SendHelpEmail()
        {
            if (CurrentUser?.Email == null) return;

            var to = "admin@adlmstudio.net";
            var subject = Uri.EscapeDataString("Need help with ADLM Rate Gen");
            var body = Uri.EscapeDataString($"Hello ADLM, my name is {CurrentUser.Email} and I need help with the ADLM Rate Gen.");
            var mailto = $"mailto:{to}?subject={subject}&body={body}";

            Process.Start(new ProcessStartInfo(mailto) { UseShellExecute = true });
        }

        private void ExportAllToExcel()
        {
            try
            {
                var sfd = new SaveFileDialog
                {
                    Title = "Export ADLM Rates",
                    FileName = $"ADLM_Rates_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
                    Filter = "Excel Workbook (*.xlsx)|*.xlsx"
                };
                if (sfd.ShowDialog() != true) return;

                var sheets = new List<ExcelExporter.ExportSheet>();

                void AddSheet(string name, object vm)
                {
                    var rows = FindRowsEnumerable(vm);
                    if (rows != null && rows.Cast<object>().Any())
                        sheets.Add(new ExcelExporter.ExportSheet(name, rows));
                }

                AddSheet("Ground", GroundWorkViewModel);
                AddSheet("Concrete", ConcreteViewModel);
                AddSheet("Block Works", BlockworkViewModel);
                AddSheet("Finishes", FinishesViewModel);
                AddSheet("Roofs", RoofWorkViewModel);
                AddSheet("Painting", PaintWorkViewModel);
                AddSheet("Steel", SteelWorkViewModel);
                AddSheet("Window & Door", WindowAndDoorViewModel);
                AddSheet("Carbon & Others", CarbonOthersViewModel);
                sheets.Add(new ExcelExporter.ExportSheet("Saved Rates", CustomRateListViewModel.CustomRates));

                if (!sheets.Any())
                {
                    ADLMRateGen.Helpers.AppMessage.Show("No data available to export.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                ExcelExporter.ExportWorkbook(sheets, sfd.FileName);
                ADLMRateGen.Helpers.AppMessage.Show("Export completed.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ADLMRateGen.Helpers.AppMessage.Show($"Export failed:\n{ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportBillToCsv()
        {
            try
            {
                var sfd = new SaveFileDialog
                {
                    Title = "Export to Bill (CSV)",
                    FileName = $"ADLM_Bill_{DateTime.Now:yyyyMMdd_HHmm}.csv",
                    Filter = "CSV (*.csv)|*.csv"
                };
                if (sfd.ShowDialog() != true) return;

                var sections = new List<(string Name, IEnumerable Rows)>();

                void AddSection(string name, object vm)
                {
                    var rows = FindRowsEnumerable(vm);
                    if (rows != null) sections.Add((name, rows));
                }

                AddSection("Ground", GroundWorkViewModel);
                AddSection("Concrete", ConcreteViewModel);
                AddSection("Block Works", BlockworkViewModel);
                AddSection("Finishes", FinishesViewModel);
                AddSection("Roofs", RoofWorkViewModel);
                AddSection("Painting", PaintWorkViewModel);
                AddSection("Steel", SteelWorkViewModel);
                AddSection("Window & Door", WindowAndDoorViewModel);
                AddSection("Carbon & Others", CarbonOthersViewModel);

                if (CustomRateListViewModel?.CustomRates is IEnumerable cr && cr.GetEnumerator().MoveNext())
                    sections.Add(("Saved Rates", CustomRateListViewModel.CustomRates));

                if (sections.Count == 0)
                {
                    ADLMRateGen.Helpers.AppMessage.Show("No data available to export.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("Section,Description,Total");

                foreach (var (name, rows) in sections)
                {
                    foreach (var row in rows)
                    {
                        if (row == null) continue;

                        var desc = GetStringProp(row, "Description", "Name", "Title") ?? string.Empty;
                        var total = GetDecimalProp(row, "TotalCost", "TotalPrice", "Total");

                        sb.AppendLine($"{Csv(name)},{Csv(desc)},{total.ToString("0.##", CultureInfo.InvariantCulture)}");
                    }
                }

                File.WriteAllText(sfd.FileName, sb.ToString(), new UTF8Encoding(true));
                ADLMRateGen.Helpers.AppMessage.Show("CSV exported.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ADLMRateGen.Helpers.AppMessage.Show($"Export failed:\n{ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            static string Csv(string s) => $"\"{(s ?? string.Empty).Replace("\"", "\"\"")}\"";

            static string? GetStringProp(object o, params string[] names)
            {
                var t = o.GetType();
                foreach (var n in names)
                {
                    var p = t.GetProperty(n);
                    if (p == null) continue;
                    var v = p.GetValue(o);
                    if (v != null) return v.ToString();
                }
                return null;
            }

            static decimal GetDecimalProp(object o, params string[] names)
            {
                var t = o.GetType();
                foreach (var n in names)
                {
                    var p = t.GetProperty(n);
                    if (p == null) continue;
                    var v = p.GetValue(o);
                    if (v == null) continue;
                    try { return Convert.ToDecimal(v, CultureInfo.InvariantCulture); }
                    catch { }
                }
                return 0m;
            }
        }

        private static IEnumerable? FindRowsEnumerable(object vm)
        {
            if (vm == null) return null;

            foreach (var p in vm.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var pt = p.PropertyType;
                if (pt == typeof(string)) continue;
                if (!typeof(IEnumerable).IsAssignableFrom(pt)) continue;

                var val = p.GetValue(vm) as IEnumerable;
                if (val == null) continue;

                object? first = null;
                foreach (var r in val) { first = r; if (first != null) break; }
                if (first == null) continue;

                var t = first.GetType();
                bool hasSn = t.GetProperty("ItemNo") != null || t.GetProperty("SNo") != null;
                bool hasDesc = t.GetProperty("Description") != null || t.GetProperty("Name") != null || t.GetProperty("Title") != null;
                bool hasTot = t.GetProperty("TotalCost") != null || t.GetProperty("TotalPrice") != null;

                if (hasSn && hasDesc && hasTot)
                    return val;
            }
            return null;
        }

        private async void Logout()
        {
            try
            {
                try
                {
                    await FlushUserRatesCloudSyncAsync().ConfigureAwait(false);
                }
                catch
                {
                    // best-effort on logout
                }

                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    StopNotificationPolling();
                    AuthProvider.Instance.Client.SignOut();

                    var cfg = ConfigManager.LoadConfig() ?? new AppConfig();
                    cfg.AuthToken = null;
                    cfg.AuthExpiry = DateTime.MinValue;
                    ConfigManager.SaveConfig(cfg);

                    CurrentUser = null;

                    IsLoggedIn = false;
                    HasPriceNotifications = false;
                    IsNotificationsOpen = false;
                    SelectedViewModel = SignInViewModel;
                });
            }
            catch (Exception ex)
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                    ADLMRateGen.Helpers.AppMessage.Show($"Log-out failed\n{ex.Message}"));
            }
        }

        private static void OpenYoutube()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://youtube.com/playlist?list=PLk1KkUNE9ZrO5IPh7p3-5zxfDFs1Dl9b-&si=xi4dt-Fmy17_2KsM",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ADLMRateGen.Helpers.AppMessage.Show($"Unable to open browser.\n{ex.Message}");
            }
        }

        private async Task RefreshCurrentUserProfileAsync()
        {
            if (!IsLoggedIn || CurrentUser == null || !AuthProvider.Instance.Client.HasSession)
                return;

            try
            {
                using var profileDoc = await AuthProvider.Instance.Client.GetJsonAsync("/me/profile");
                var root = profileDoc.RootElement;
                var updated = new UserModel
                {
                    Id = TryReadProfileValue(root, "_id", "id") ?? CurrentUser.Id,
                    Email = TryReadProfileValue(root, "email") ?? CurrentUser.Email,
                    Username = TryReadProfileValue(root, "username") ?? CurrentUser.Username,
                    FirstName = TryReadProfileValue(root, "firstName") ?? CurrentUser.FirstName,
                    LastName = TryReadProfileValue(root, "lastName") ?? CurrentUser.LastName,
                    AvatarUrl = TryReadProfileValue(root, "avatarUrl") ?? CurrentUser.AvatarUrl,
                    Zone = TryReadProfileValue(root, "zone") ?? CurrentUser.Zone,
                    // (profile zone is persisted to AppSettings below so every
                    // sync path prices against the account's real zone)
                    Password = CurrentUser.Password,
                    CreationAt = CurrentUser.CreationAt,
                    SubscriptionDuration = CurrentUser.SubscriptionDuration,
                    ExpirationDate = CurrentUser.ExpirationDate,
                    UpdatedAt = CurrentUser.UpdatedAt,
                    HardwareFingerprint = CurrentUser.HardwareFingerprint
                };

                // Persist the account's zone so every sync path (master price
                // sync, sign-in refresh) prices against the right location.
                if (!string.IsNullOrWhiteSpace(updated.Zone) &&
                    !string.Equals(ADLMRateGen.Properties.AppSettings.Zone, updated.Zone, StringComparison.OrdinalIgnoreCase))
                {
                    ADLMRateGen.Properties.AppSettings.Zone = updated.Zone;
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    CurrentUser = updated;
                    RaisePropertyChanged(nameof(CurrentUser));
                    RaisePropertyChanged(nameof(CurrentUsername));
                });
            }
            catch
            {
                // Keep the login flow resilient when profile enrichment is unavailable.
            }
        }

        private static string? TryReadProfileValue(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (!root.TryGetProperty(name, out var element))
                    continue;

                if (element.ValueKind == JsonValueKind.String)
                {
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            return null;
        }
    }
}
