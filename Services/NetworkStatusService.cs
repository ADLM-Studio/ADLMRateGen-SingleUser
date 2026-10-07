#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using ADLMRateGen.Helpers;

namespace ADLMRateGen.Services
{
    public enum NetworkStatus
    {
        /// <summary>Reachability has not been established yet (first probe in flight).</summary>
        Checking,
        /// <summary>Internet is up and the ADLM cloud API responded.</summary>
        Online,
        /// <summary>The machine has internet but the ADLM cloud API is not responding.</summary>
        CloudUnreachable,
        /// <summary>No network adapter is connected (airplane mode, cable out, Wi-Fi off).</summary>
        NoInternet
    }

    /// <summary>
    /// Feeds the network indicator in the header bar: online / offline, and how
    /// strong the connection to ADLM Cloud is, as bars.
    ///
    /// Three sources, cheapest first:
    ///   1. NetworkChange.NetworkAvailabilityChanged - instant "cable out", free.
    ///   2. A timed GET of /health on the API, with the round trip measured. The
    ///      median of the last few trips is what the bars show, so a single
    ///      stalled request cannot flip the display.
    ///   3. `netsh wlan show interfaces`, every other probe, for the Wi-Fi SSID and
    ///      signal percentage. Shown in the tooltip only: a strong Wi-Fi signal to
    ///      a router with a dead uplink is not a strong connection, so the bars
    ///      always come from the real round trip.
    ///
    /// Everything runs off the UI thread. Both events are raised on a background
    /// thread; subscribers marshal to the dispatcher themselves.
    /// </summary>
    public sealed class NetworkStatusService : IDisposable
    {
        private static readonly TimeSpan OnlineInterval = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan OfflineInterval = TimeSpan.FromSeconds(10);

        // A Lambda cold start is 2-3 s; anything past 8 s is unreachable for
        // practical purposes and is reported as such rather than as a slow trip.
        private static readonly HttpClient ProbeClient = CreateProbeClient();

        public static NetworkStatusService Instance { get; } = new NetworkStatusService();

        private readonly object _gate = new object();
        private readonly List<double> _samples = new List<double>();
        private CancellationTokenSource _cts;
        private int _probeCount;

        private NetworkStatus _status = NetworkStatus.Checking;
        private NetworkSignalLevel _level = NetworkSignalLevel.Unknown;
        private double? _rttMs;
        private double? _lastRttMs;
        private int? _wifiSignalPercent;
        private string _wifiSsid;
        private DateTime? _lastChecked;

        private NetworkStatusService() { }

        public NetworkStatus Status { get { return _status; } }
        public NetworkSignalLevel Level { get { return _level; } }
        public int Bars { get { return NetworkSignal.BarsFor(_level); } }
        /// <summary>Median round trip of the recent probes, milliseconds.</summary>
        public double? RttMs { get { return _rttMs; } }
        /// <summary>The single most recent round trip, milliseconds.</summary>
        public double? LastRttMs { get { return _lastRttMs; } }
        /// <summary>Wi-Fi signal quality 0-100, or null when not on Wi-Fi / unknown.</summary>
        public int? WifiSignalPercent { get { return _wifiSignalPercent; } }
        public string WifiSsid { get { return _wifiSsid; } }
        public DateTime? LastChecked { get { return _lastChecked; } }
        public bool IsRunning { get { return _cts != null; } }

        /// <summary>Raised on a background thread when the online/offline status changes.</summary>
        public event EventHandler<NetworkStatus> StatusChanged;

        /// <summary>Raised on a background thread after every probe, whether or not anything changed.</summary>
        public event EventHandler Changed;

        /// <summary>Short label for the header: the level when online, else the status.</summary>
        public string ShortLabel
        {
            get
            {
                switch (_status)
                {
                    case NetworkStatus.Online: return NetworkSignal.LabelFor(_level);
                    case NetworkStatus.CloudUnreachable: return "No server";
                    case NetworkStatus.NoInternet: return "Offline";
                    default: return "Checking";
                }
            }
        }

        /// <summary>Multi-line detail for the tooltip.</summary>
        public string Tooltip
        {
            get
            {
                var lines = new List<string>();
                switch (_status)
                {
                    case NetworkStatus.Online:
                        lines.Add(_rttMs.HasValue
                            ? string.Format("Connected to ADLM Cloud: {0} ({1:0} ms round trip)", NetworkSignal.LabelFor(_level), _rttMs.Value)
                            : "Connected to ADLM Cloud.");
                        if (_level == NetworkSignalLevel.Poor)
                            lines.Add("Cloud save, sync and AI features will be slow on this connection.");
                        else if (_level == NetworkSignalLevel.Fair)
                            lines.Add("Usable, but large uploads and syncs will take a while.");
                        break;
                    case NetworkStatus.CloudUnreachable:
                        lines.Add("Internet is up but the ADLM server is not responding. Cloud save, sync and sign-in may fail.");
                        break;
                    case NetworkStatus.NoInternet:
                        lines.Add("No internet connection. Cloud features are unavailable; local work is unaffected.");
                        break;
                    default:
                        lines.Add("Checking the connection to ADLM Cloud...");
                        break;
                }

                if (_wifiSignalPercent.HasValue)
                {
                    lines.Add(string.IsNullOrEmpty(_wifiSsid)
                        ? string.Format("Wi-Fi signal: {0}%", _wifiSignalPercent.Value)
                        : string.Format("Wi-Fi: {0} ({1}% signal)", _wifiSsid, _wifiSignalPercent.Value));
                }

                if (_lastChecked.HasValue)
                    lines.Add("Last checked " + _lastChecked.Value.ToString("h:mm:ss tt") + ". Click to re-check.");
                else
                    lines.Add("Click to re-check.");

                return string.Join(Environment.NewLine, lines);
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_cts != null) return;
                _cts = new CancellationTokenSource();
                try { NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged; }
                catch { /* some hosts refuse the hook; polling still covers it */ }
                var token = _cts.Token;
                Task.Run(() => RunAsync(token));
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_cts == null) return;
                try { NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged; }
                catch { }
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }

        public void Dispose() { Stop(); }

        /// <summary>Forces an immediate re-check (the indicator is clickable).</summary>
        public void CheckNow()
        {
            var cts = _cts;
            var ct = cts != null ? cts.Token : CancellationToken.None;
            Task.Run(() => ProbeAndPublishAsync(ct, true));
        }

        private static HttpClient CreateProbeClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-ADLM-Client", "rategen-network-indicator");
            return client;
        }

        private static string ProbeUrl
        {
            get
            {
                // A per-request query string so nothing between here and the
                // Lambda answers from a cache on the server's behalf.
                return AppEnvironment.ApiBaseUrl + "/health" + "?t=" + DateTime.UtcNow.Ticks.ToString();
            }
        }

        private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            if (!e.IsAvailable)
            {
                lock (_gate) { _samples.Clear(); }
                Publish(NetworkStatus.NoInternet, null, false);
            }
            else
            {
                // Adapter came back: verify the cloud now rather than waiting out the interval.
                CheckNow();
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await ProbeAndPublishAsync(ct, false).ConfigureAwait(false);

                var delay = _status == NetworkStatus.Online ? OnlineInterval : OfflineInterval;
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task ProbeAndPublishAsync(CancellationToken ct, bool forced)
        {
            bool hasNetwork;
            try { hasNetwork = NetworkInterface.GetIsNetworkAvailable(); }
            catch { hasNetwork = true; }

            if (!hasNetwork)
            {
                lock (_gate) { _samples.Clear(); }
                Publish(NetworkStatus.NoInternet, null, true);
                return;
            }

            double? rtt = null;
            bool reachable;
            var sw = Stopwatch.StartNew();
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, ProbeUrl))
                using (var resp = await ProbeClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    sw.Stop();
                    // Any HTTP status proves the server answered; only transport failures mean unreachable.
                    reachable = true;
                    rtt = sw.Elapsed.TotalMilliseconds;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // service stopped mid-probe; keep the last reading
            }
            catch
            {
                reachable = false;
            }

            int count;
            lock (_gate)
            {
                if (rtt.HasValue)
                {
                    _samples.Add(rtt.Value);
                    while (_samples.Count > NetworkSignal.SampleWindow) _samples.RemoveAt(0);
                }
                else
                {
                    _samples.Clear();
                }
                count = ++_probeCount;
            }

            // Wi-Fi is a process spawn, so every other probe, or when the user asks.
            if (forced || count % 2 == 1)
                RefreshWifi();

            _lastRttMs = rtt;
            Publish(reachable ? NetworkStatus.Online : NetworkStatus.CloudUnreachable, rtt, true);
        }

        private void RefreshWifi()
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) { _wifiSignalPercent = null; _wifiSsid = null; return; }
                    string text = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(3000))
                    {
                        try { p.Kill(); } catch { }
                        return;
                    }
                    string ssid; int pct;
                    if (NetworkSignal.TryParseNetshWifi(text, out ssid, out pct))
                    {
                        _wifiSsid = ssid;
                        _wifiSignalPercent = pct;
                    }
                    else
                    {
                        _wifiSsid = null;
                        _wifiSignalPercent = null;
                    }
                }
            }
            catch
            {
                // No wlan service, no netsh, or a locked-down host: the bars still
                // come from the round trip, so this is purely informational.
                _wifiSignalPercent = null;
                _wifiSsid = null;
            }
        }

        private void Publish(NetworkStatus status, double? rtt, bool probed)
        {
            double? median;
            lock (_gate) { median = NetworkSignal.Median(_samples); }

            bool hasNetwork = status != NetworkStatus.NoInternet;
            bool reachable = status == NetworkStatus.Online;
            _rttMs = reachable ? median : null;
            _level = NetworkSignal.LevelFor(hasNetwork, reachable, _rttMs);
            if (probed) _lastChecked = DateTime.Now;

            bool changed = _status != status;
            _status = status;

            if (changed)
            {
                var h = StatusChanged;
                if (h != null) { try { h(this, status); } catch { } }
            }
            var c = Changed;
            if (c != null) { try { c(this, EventArgs.Empty); } catch { } }
        }
    }
}
