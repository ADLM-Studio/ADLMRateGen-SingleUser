using ADLMRateGen.Helpers;
using ADLMRateGen.Services;
using OfficeOpenXml;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.ComponentModel;
using System.Windows.Threading;
using ADLMRateGen.ViewModel;

namespace ADLMRateGen
{
    public partial class App : Application
    {
        private readonly Uri _lightUri = new Uri("/Resources/ADLMStylesTheme.xaml", UriKind.Relative);
        private readonly Uri _darkUri = new Uri("/Resources/ADLMStylesDarkTheme.xaml", UriKind.Relative);

        private ResourceDictionary? _lightDict;
        private readonly ResourceDictionary _darkDict = new(); // created once
        private bool _isDark; // false = light, true = dark

        private Mutex? _mutex;
        private MainWindow? _mainWindow;
        private MainViewModel? _mainViewModel;
        private SignInWindow? _signInWindow;

        // use a stable unique name (include company + app)
        private const string MutexName = "Global\\ADLMStudio_ADLMRateGen_SingleInstance";

        private static string LogDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "ADLMRateGen", "Logs");

        private static string NewLogFilePath()
        {
            Directory.CreateDirectory(LogDir);
            return Path.Combine(LogDir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        }

        public bool IsDarkTheme => _isDark;

        protected override async void OnStartup(StartupEventArgs e)
        {
            // 1) Global exception hooks
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            bool createdNew;
            _mutex = new Mutex(true, MutexName, out createdNew);

            if (!createdNew)
            {
                TryBringExistingToFront();
                MessageBox.Show(
                    "ADLM Rate Gen is already running.",
                    "ADLM Rate Gen",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                Shutdown();
                return;
            }

            base.OnStartup(e);

            SplashWindow? splash = null;

            try
            {
                splash = new SplashWindow();
                splash.Show();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

                // 2) ✅ EPPlus 8+ license MUST be set via ExcelPackage.License (NOT LicenseContext)
                // Choose ONE of these based on your usage:

                // Noncommercial / personal testing:
                ExcelPackage.License.SetNonCommercialOrganization("ADLM Studio (ADLMRateGen)");

                // Commercial use (uncomment and put your key):
                // ExcelPackage.License.SetCommercial("<YOUR_EPPLUS_LICENSE_KEY>");

                // Clear any leftover API host variable from an old installer,
                // before anything reads a URL. FirstLive would ignore it anyway,
                // but leaving it on the machine means the guard can never be
                // retired. See AppEnvironment.RemoveRetiredHostOverrides.
                Helpers.AppEnvironment.RemoveRetiredHostOverrides();

                // 3) Migrations + data initialization
                // The splash names each step as it starts (Richard's loading system).
                splash.Say("Preparing your library…");
                DataMigrator.EnsureMigrated();

                MaterialLibraryService.Initialize(new MaterialJsonDataSource(AppPaths.MaterialLibraryFile));
                LabourLibraryService.Initialize(new LabourJsonDataSource(AppPaths.LabourLibraryFile));

                splash.Say("Loading the rate build-ups…");
                ComputeCatalogStore.ReloadFromDisk();


                Debug.WriteLine($"[PATH] Using materials: {AppPaths.MaterialLibraryFile}");
                Debug.WriteLine($"[PATH] Using labour   : {AppPaths.LabourLibraryFile}");

                // 4) Seed materials if empty (safe)
                try
                {
                    if (!MaterialLibraryService.GetAllMaterials().Any())
                    {
                        var seedPath = AppPaths.FindFirstNonEmptyDefault(
                            "materials.json", "defaultMaterials.json", "defaultMaterial.json"
                        );

                        if (seedPath != null)
                        {
                            var shipped = new MaterialJsonDataSource(seedPath).LoadMaterials().ToList();
                            if (shipped.Any())
                            {
                                MaterialLibraryService.AddOrUpdateMaterials(shipped);

                                // re-load from the main library file to refresh in-memory state
                                MaterialLibraryService.Initialize(new MaterialJsonDataSource(AppPaths.MaterialLibraryFile));

                                Debug.WriteLine($"[SEED] Imported {shipped.Count} materials from {seedPath}");
                            }
                            else
                            {
                                Debug.WriteLine($"[SEED] {seedPath} existed but contained 0 rows (after parse).");
                            }
                        }
                        else
                        {
                            Debug.WriteLine("[SEED] No non-empty shipped materials file found.");
                        }
                    }
                }
                catch (Exception seedEx)
                {
                    Debug.WriteLine($"[SEED] Failed: {seedEx}");
                }

                // 5) Theme dictionary references (safe)
                _lightDict = FindMergedDictionaryByEndsWith("ADLMStylesTheme.xaml");
                if (_lightDict == null)
                {
                    _lightDict = new ResourceDictionary { Source = _lightUri };
                    Resources.MergedDictionaries.Add(_lightDict);
                }

                _darkDict.Source = _darkUri; // prepare but do not merge initially
                _isDark = false;
                ApplyTheme(ConfigManager.LoadConfig()?.IsDarkTheme == true, persist: false);

                // 6) Show main window
                splash.Say("Opening RateGen…");
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                var wnd = new MainWindow();
                MainWindow = wnd;
                _mainWindow = wnd;
                _mainViewModel = wnd.DataContext as MainViewModel;

                if (_mainViewModel != null)
                {
                    _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
                }

                if (_mainViewModel?.IsLoggedIn == true)
                {
                    ShowMainWindow();
                }
                else
                {
                    ShowSignInWindow();
                }

                await Task.Delay(650);
                splash.Close();
            }
            catch (Exception ex)
            {
                if (splash != null)
                {
                    splash.Close();
                }

                ReportFatal(ex, "OnStartup");
                Shutdown(-1);
            }
        }

        private ResourceDictionary? FindMergedDictionaryByEndsWith(string endsWith)
        {
            foreach (var d in Resources.MergedDictionaries)
            {
                var src = d.Source?.OriginalString ?? "";
                if (src.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        public bool ToggleTheme()
        {
            ApplyTheme(!_isDark, persist: true);
            return _isDark;
        }

        private void ApplyTheme(bool useDark, bool persist)
        {
            if (_lightDict == null) return;

            var merged = Resources.MergedDictionaries;

            if (useDark)
            {
                if (merged.Contains(_lightDict)) merged.Remove(_lightDict);
                if (!merged.Contains(_darkDict)) merged.Add(_darkDict);
            }
            else
            {
                if (merged.Contains(_darkDict)) merged.Remove(_darkDict);
                if (!merged.Contains(_lightDict)) merged.Add(_lightDict);
            }

            _isDark = useDark;

            if (!persist) return;

            var cfg = ConfigManager.LoadConfig() ?? new AppConfig();
            cfg.IsDarkTheme = _isDark;
            ConfigManager.SaveConfig(cfg);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            ReportFatal(e.Exception, "DispatcherUnhandledException");
            e.Handled = true;
            Shutdown(-1);
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                ReportFatal(ex, "AppDomain.UnhandledException");
            else
                ReportFatal(new Exception("Unknown fatal error"), "AppDomain.UnhandledException");

            Shutdown(-1);
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            ReportFatal(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        }

        private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.IsLoggedIn))
            {
                return;
            }

            Dispatcher.Invoke(() =>
            {
                if (_mainViewModel?.IsLoggedIn == true)
                {
                    ShowMainWindow();
                }
                else
                {
                    ShowSignInWindow();
                }
            });
        }

        private void ShowMainWindow()
        {
            if (_mainWindow == null)
            {
                return;
            }

            if (_signInWindow != null)
            {
                _signInWindow.Closed -= OnSignInWindowClosed;
                _signInWindow.Close();
                _signInWindow = null;
            }

            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Activate();
        }

        private void ShowSignInWindow()
        {
            if (_mainViewModel == null)
            {
                return;
            }

            if (_mainWindow != null && _mainWindow.IsVisible)
            {
                _mainWindow.Hide();
            }

            if (_signInWindow == null)
            {
                _signInWindow = new SignInWindow(_mainViewModel.SignInViewModel);
                _signInWindow.Closed += OnSignInWindowClosed;
            }

            if (!_signInWindow.IsVisible)
            {
                _signInWindow.Show();
            }

            if (_signInWindow.WindowState == WindowState.Minimized)
            {
                _signInWindow.WindowState = WindowState.Normal;
            }

            _signInWindow.Activate();
        }

        private void OnSignInWindowClosed(object? sender, EventArgs e)
        {
            if (_signInWindow != null)
            {
                _signInWindow.Closed -= OnSignInWindowClosed;
                _signInWindow = null;
            }

            if (_mainViewModel?.IsLoggedIn != true)
            {
                Shutdown();
            }
        }

        public static void ReportFatal(Exception ex, string where)
        {
            var path = NewLogFilePath();

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("ADLM Rate Gen - Fatal Error");
                sb.AppendLine($"Where: {where}");
                sb.AppendLine($"Time:  {DateTime.Now:O}");
                sb.AppendLine();
                sb.AppendLine(ex.ToString());

                File.WriteAllText(path, sb.ToString());

                MessageBox.Show(
                    "ADLM Rate Gen could not start.\n\n" +
                    $"A crash log has been saved to:\n{path}\n\n" +
                    "Please send this log to admin/support.",
                    "Startup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                try
                {
                    MessageBox.Show(
                        "ADLM Rate Gen could not start due to a fatal error.",
                        "Startup Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch { }
            }

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", LogDir) { UseShellExecute = true });
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (Current?.MainWindow?.DataContext is ViewModel.MainViewModel mainVm)
                {
                    try
                    {
                        // Use a timeout so the app doesn't hang on exit
                        var flushTask = mainVm.FlushUserRatesCloudSyncAsync();
                        flushTask.Wait(TimeSpan.FromSeconds(5));
                    }
                    catch
                    {
                        // best-effort on shutdown
                    }
                }

                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
            catch { }
            finally
            {
                _mutex = null;
            }

            base.OnExit(e);
        }

        private static void TryBringExistingToFront()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                var other = Process.GetProcessesByName(current.ProcessName)
                                   .FirstOrDefault(p => p.Id != current.Id);

                if (other == null) return;

                // sometimes MainWindowHandle is not ready immediately
                other.WaitForInputIdle(1000);

                var hWnd = other.MainWindowHandle;
                if (hWnd == IntPtr.Zero) return;

                ShowWindow(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
            }
            catch { }
        }

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
