#nullable disable
using System;
using System.Collections.Generic;

namespace ADLMRateGen.Services
{
    /// <summary>
    /// How usable the connection to ADLM Cloud is right now, on a phone-style
    /// four-bar scale. <see cref="Offline"/> and <see cref="Unknown"/> both draw
    /// zero bars; they differ in what the label says.
    /// </summary>
    public enum NetworkSignalLevel
    {
        Unknown = -1,
        Offline = 0,
        Poor = 1,
        Fair = 2,
        Good = 3,
        Excellent = 4
    }

    /// <summary>
    /// Pure classification of a measured round trip into a signal level. No I/O,
    /// no WPF, no clock: this is the part that is unit-tested, and the part every
    /// ADLM product shares verbatim so the bars mean the same thing everywhere.
    ///
    /// The thresholds are round-trip times to GET /health on api.adlmstudio.net
    /// (Lambda behind CloudFront, eu-west-1). A warm response measured from Lagos
    /// sits around 200-300 ms, so that is "Excellent"; a mobile hotspot on a bad
    /// day sits around a second, which is "Fair" - things work, uploads crawl.
    /// </summary>
    public static class NetworkSignal
    {
        public const double ExcellentMaxMs = 300;
        public const double GoodMaxMs = 700;
        public const double FairMaxMs = 1500;

        /// <summary>Number of samples the median is taken over.</summary>
        public const int SampleWindow = 3;

        public static NetworkSignalLevel LevelFor(bool hasNetwork, bool cloudReachable, double? rttMs)
        {
            if (!hasNetwork) return NetworkSignalLevel.Offline;
            if (!cloudReachable) return NetworkSignalLevel.Poor;
            if (rttMs == null) return NetworkSignalLevel.Unknown;

            double ms = rttMs.Value;
            if (ms < 0) return NetworkSignalLevel.Unknown;
            if (ms <= ExcellentMaxMs) return NetworkSignalLevel.Excellent;
            if (ms <= GoodMaxMs) return NetworkSignalLevel.Good;
            if (ms <= FairMaxMs) return NetworkSignalLevel.Fair;
            return NetworkSignalLevel.Poor;
        }

        /// <summary>0..4 filled bars.</summary>
        public static int BarsFor(NetworkSignalLevel level)
        {
            int n = (int)level;
            if (n < 0) return 0;
            if (n > 4) return 4;
            return n;
        }

        public static string LabelFor(NetworkSignalLevel level)
        {
            switch (level)
            {
                case NetworkSignalLevel.Excellent: return "Excellent";
                case NetworkSignalLevel.Good: return "Good";
                case NetworkSignalLevel.Fair: return "Fair";
                case NetworkSignalLevel.Poor: return "Poor";
                case NetworkSignalLevel.Offline: return "Offline";
                default: return "Checking";
            }
        }

        /// <summary>
        /// Median of the samples, or null when there are none. A median rather
        /// than a mean so one stalled request does not drop the bars for a minute.
        /// </summary>
        public static double? Median(IList<double> samples)
        {
            if (samples == null || samples.Count == 0) return null;
            var sorted = new List<double>(samples);
            sorted.Sort();
            int mid = sorted.Count / 2;
            if (sorted.Count % 2 == 1) return sorted[mid];
            return (sorted[mid - 1] + sorted[mid]) / 2.0;
        }

        /// <summary>
        /// Parses the output of `netsh wlan show interfaces` into (ssid, signal %).
        /// Returns false when the machine is not on Wi-Fi or the text is not
        /// understood. Anchored on the line shape rather than the English word
        /// where it can be, so a non-English Windows still yields the percentage.
        /// </summary>
        public static bool TryParseNetshWifi(string text, out string ssid, out int signalPercent)
        {
            ssid = null;
            signalPercent = 0;
            if (string.IsNullOrEmpty(text)) return false;

            string percentLine = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();

                if (ssid == null && key.Equals("SSID", StringComparison.OrdinalIgnoreCase))
                    ssid = value;

                if (value.EndsWith("%") && !key.Equals("BSSID", StringComparison.OrdinalIgnoreCase))
                {
                    // "Signal : 87%" in English; whatever the word is elsewhere, the
                    // only percentage netsh prints on this screen is the signal.
                    if (percentLine == null || key.IndexOf("signal", StringComparison.OrdinalIgnoreCase) >= 0)
                        percentLine = value;
                }
            }

            if (percentLine == null) return false;
            int pct;
            if (!int.TryParse(percentLine.TrimEnd('%').Trim(), out pct)) return false;
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            signalPercent = pct;
            return true;
        }
    }
}
