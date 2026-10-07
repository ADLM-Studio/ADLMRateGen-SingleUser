using ADLMRateGen.Services;
using Xunit;

namespace ADLMRateGen.Tests
{
    public class NetworkSignalTests
    {
        [Theory]
        [InlineData(0, NetworkSignalLevel.Excellent)]
        [InlineData(300, NetworkSignalLevel.Excellent)]
        [InlineData(301, NetworkSignalLevel.Good)]
        [InlineData(700, NetworkSignalLevel.Good)]
        [InlineData(701, NetworkSignalLevel.Fair)]
        [InlineData(1500, NetworkSignalLevel.Fair)]
        [InlineData(1501, NetworkSignalLevel.Poor)]
        [InlineData(9000, NetworkSignalLevel.Poor)]
        public void Online_round_trips_map_to_the_four_bands(double ms, NetworkSignalLevel expected)
        {
            Assert.Equal(expected, NetworkSignal.LevelFor(true, true, ms));
        }

        [Fact]
        public void No_network_is_offline_regardless_of_history()
        {
            Assert.Equal(NetworkSignalLevel.Offline, NetworkSignal.LevelFor(false, true, 120));
            Assert.Equal(NetworkSignalLevel.Offline, NetworkSignal.LevelFor(false, false, null));
        }

        [Fact]
        public void Network_up_but_cloud_down_is_poor_not_offline()
        {
            Assert.Equal(NetworkSignalLevel.Poor, NetworkSignal.LevelFor(true, false, null));
        }

        [Fact]
        public void Reachable_without_a_reading_yet_is_unknown()
        {
            Assert.Equal(NetworkSignalLevel.Unknown, NetworkSignal.LevelFor(true, true, null));
            Assert.Equal(NetworkSignalLevel.Unknown, NetworkSignal.LevelFor(true, true, -1));
        }

        [Theory]
        [InlineData(NetworkSignalLevel.Unknown, 0)]
        [InlineData(NetworkSignalLevel.Offline, 0)]
        [InlineData(NetworkSignalLevel.Poor, 1)]
        [InlineData(NetworkSignalLevel.Fair, 2)]
        [InlineData(NetworkSignalLevel.Good, 3)]
        [InlineData(NetworkSignalLevel.Excellent, 4)]
        public void Bars_follow_the_level(NetworkSignalLevel level, int bars)
        {
            Assert.Equal(bars, NetworkSignal.BarsFor(level));
        }

        [Fact]
        public void Median_ignores_one_stalled_request()
        {
            Assert.Equal(250, NetworkSignal.Median(new double[] { 240, 250, 7900 }));
            Assert.Equal(245, NetworkSignal.Median(new double[] { 240, 250 }));
            Assert.Null(NetworkSignal.Median(new double[0]));
            Assert.Null(NetworkSignal.Median(null));
        }

        [Fact]
        public void Netsh_output_yields_ssid_and_signal()
        {
            const string text =
                "\r\nThere is 1 interface on the system:\r\n\r\n" +
                "    Name                   : Wi-Fi\r\n" +
                "    Description            : Intel(R) Wi-Fi 6 AX201 160MHz\r\n" +
                "    GUID                   : 1234\r\n" +
                "    Physical address       : aa:bb:cc:dd:ee:ff\r\n" +
                "    State                  : connected\r\n" +
                "    SSID                   : ADLM Office\r\n" +
                "    BSSID                  : 11:22:33:44:55:66\r\n" +
                "    Network type           : Infrastructure\r\n" +
                "    Radio type             : 802.11ax\r\n" +
                "    Authentication         : WPA2-Personal\r\n" +
                "    Cipher                 : CCMP\r\n" +
                "    Connection mode        : Auto Connect\r\n" +
                "    Channel                : 36\r\n" +
                "    Receive rate (Mbps)    : 1201\r\n" +
                "    Transmit rate (Mbps)   : 1201\r\n" +
                "    Signal                 : 87%\r\n" +
                "    Profile                : ADLM Office\r\n";

            string ssid; int pct;
            Assert.True(NetworkSignal.TryParseNetshWifi(text, out ssid, out pct));
            Assert.Equal("ADLM Office", ssid);
            Assert.Equal(87, pct);
        }

        [Fact]
        public void Netsh_output_without_a_connection_is_not_wifi()
        {
            const string text =
                "    Name                   : Wi-Fi\r\n" +
                "    State                  : disconnected\r\n" +
                "    Radio status           : Hardware On\r\n";
            string ssid; int pct;
            Assert.False(NetworkSignal.TryParseNetshWifi(text, out ssid, out pct));
            Assert.False(NetworkSignal.TryParseNetshWifi("", out ssid, out pct));
            Assert.False(NetworkSignal.TryParseNetshWifi(null, out ssid, out pct));
        }

        [Fact]
        public void Netsh_output_in_another_language_still_yields_the_percentage()
        {
            const string text =
                "    Nom                    : Wi-Fi\r\n" +
                "    SSID                   : Maison\r\n" +
                "    BSSID                  : 11:22:33:44:55:66\r\n" +
                "    Signal                 : 63%\r\n";
            string ssid; int pct;
            Assert.True(NetworkSignal.TryParseNetshWifi(text, out ssid, out pct));
            Assert.Equal("Maison", ssid);
            Assert.Equal(63, pct);
        }
    }
}
