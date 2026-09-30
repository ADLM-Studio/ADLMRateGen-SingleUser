using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace ADLMRateGen.Helpers
{
    public static class UiErrorHelpers
    {
        public static void ShowNetworkTimeoutDialog(string verb, string path, string baseUrl, int timeoutMs, WebException ex)
        {
            // You can enhance by sending this to your logger as well.
            string message =
                $"We couldn’t reach the ADLM server within {timeoutMs / 1000} seconds.\n\n" +
                "What you can try:\n" +
                "• Check your internet connection (Wi-Fi/data, VPN, firewall)\n" +
                "• Try again in a few seconds\n\n" +
                "Details:\n" +
                $"Request: {verb} {path}\n" +
                $"Server: {baseUrl}\n" +
                $"Error: {ex.Message}";

            var result = ADLMRateGen.Helpers.AppMessage.Show(
                message,
                "Connection timed out",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Error);

            // Return the user's choice if you want (OK=Retry semantics)
            // Or expose a bool return.
        }

        public static string ToFriendlyMessage(Exception ex)
        {
            if (ex is WebException wex)
            {
                return wex.Status switch
                {
                    WebExceptionStatus.Timeout => "Network timeout. Please check your internet and try again.",
                    WebExceptionStatus.ConnectFailure => "Could not connect to server. Check your internet/VPN.",
                    WebExceptionStatus.NameResolutionFailure => "Could not resolve server address. Check your DNS/internet.",
                    _ => $"Network error: {wex.Message}"
                };
            }
            if (ex is UnauthorizedAccessException uae)
                return uae.Message; // your server returns good messages here

            return ex.Message;
        }
    }
    }
