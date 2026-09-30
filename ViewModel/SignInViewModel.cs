using System;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ADLMRateGen.ADLM.Auth;
using ADLMRateGen.Command;
using ADLMRateGen.Helpers;
using ADLMRateGen.Properties;
using ADLMRateGen.Services;
using ADLMRateGen.ViewModel.Model;

namespace ADLMRateGen.ViewModel
{
    public class SignInViewModel : ViewModelBase
    {
        private static string ProductKey => AppEnvironment.ProductKey;

        private AuthClient _auth { get { return AuthProvider.Instance.Client; } }

        private string _email = string.Empty;
        private string _password = string.Empty;
        private bool _isLoading;

        public event Action<string>? ZonePricesApplied;
        public event EventHandler<LoginEventArgs>? LoginSucceeded;

        public ICommand LoginCommand { get; }

        public SignInViewModel()
        {
            LoginCommand = new RelayCommand(async _ => await LoginAsync());
        }

        public SignInViewModel(object _) : this() { }

        public string Email
        {
            get { return _email; }
            set { _email = value; RaisePropertyChanged(); }
        }

        public string Password
        {
            get { return _password; }
            set { _password = value; RaisePropertyChanged(); }
        }

        public bool IsLoading
        {
            get { return _isLoading; }
            set { _isLoading = value; RaisePropertyChanged(); }
        }

        private static string DeriveUsername(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return string.Empty;
            var parts = email.Split('@');
            return parts.Length > 0 ? parts[0] : email;
        }

        private async Task LoginAsync()
        {
            if (IsLoading) return;

            var email = (Email ?? string.Empty).Trim();
            var pass = Password ?? string.Empty;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(pass))
            {
                ADLMRateGen.Helpers.AppMessage.Show("Please enter your username/email and password.");
                return;
            }

            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                var cached = _auth.GetCachedLicenseToken();
                // Validate once and reuse the parsed payload for BuildUserFromLicensePayload
                // so we don't parse / verify the JWT twice on the offline path.
                var offline = string.IsNullOrEmpty(cached)
                    ? (ok: false, payload: default(JsonElement))
                    : await ValidateCachedLicenseAsync(cached);

                if (offline.ok)
                {
                    var user = BuildUserFromLicensePayload(offline.payload) ?? new UserModel
                    {
                        Email = email,
                        Username = DeriveUsername(email)
                    };

                    LoginSucceeded?.Invoke(this, new LoginEventArgs(user) { AccessToken = string.Empty });

                    ADLMRateGen.Helpers.AppMessage.Show("Signed in (offline) via cached license.");
                    return;
                }

                ADLMRateGen.Helpers.AppMessage.Show("Internet required for first sign-in (no valid offline license found).");
                return;
            }

            IsLoading = true;
            try
            {
                await _auth.PingAsync();

                var ok = await _auth.LoginAsync(identifier: email, password: pass);
                if (!ok)
                {
                    ADLMRateGen.Helpers.AppMessage.Show("Sign in failed. Please verify your credentials.", "Sign in failed",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await _auth.EnsureEntitledAsync(ProductKey);

                var userOnline = new UserModel
                {
                    Id = JwtHelper.GetUserId(_auth.AccessToken) ?? string.Empty,
                    Email = email,
                    Username = DeriveUsername(email)
                };

                LoginSucceeded?.Invoke(this, new LoginEventArgs(userOnline)
                {
                    AccessToken = _auth.AccessToken
                });

                ADLMRateGen.Helpers.AppMessage.Show("Sign in successful!");

                try
                {
                    await SyncZonePricesAsync();
                }
                catch (Exception zx)
                {
                    ADLMRateGen.Helpers.AppMessage.Show("Signed in, but zone sync failed: " + zx.Message, "Zone Sync",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                ADLMRateGen.Helpers.AppMessage.Show(ex.Message, "Sign in failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                ADLMRateGen.Helpers.AppMessage.Show("Login Error: " + ex.Message, "Unexpected error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task SyncZonePricesAsync()
        {
            using (var profDoc = await _auth.GetJsonAsync("/me/profile"))
            {
                JsonElement profRoot = profDoc.RootElement;

                string serverZone = profRoot.TryGetProperty("zone", out var zEl)
                    ? (zEl.GetString() ?? string.Empty)
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(serverZone))
                {
                    ADLMRateGen.Helpers.AppMessage.Show("Your profile does not have a zone assigned.");
                    return;
                }

                string localZone = AppSettings.Zone ?? string.Empty;
                bool zoneChanged = !string.Equals(serverZone, localZone, StringComparison.OrdinalIgnoreCase);

                if (zoneChanged)
                {
                    var resp = ADLMRateGen.Helpers.AppMessage.Show(
                        "Your account zone is '" + serverZone + "'. Update your RateGen prices to this zone now?",
                        "Update prices for location",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    AppSettings.Zone = serverZone;
                    if (resp != MessageBoxResult.Yes)
                        return;
                }

                // Always refresh master prices at sign-in (silently when the
                // zone is unchanged) — prices move within a zone too, and the
                // cloud master library is the single source of truth.
                using (var masterDoc = await _auth.GetJsonAsync("/rategen/master?zone=" + Uri.EscapeDataString(serverZone)))
                {
                    JsonElement root = masterDoc.RootElement;

                    if (root.TryGetProperty("materials", out var mats))
                        DataSourceCloudSync.SaveMaterialsFromDto(mats);

                    if (root.TryGetProperty("labour", out var labs))
                        DataSourceCloudSync.SaveLaboursFromDto(labs);
                }

                ZonePricesApplied?.Invoke(serverZone);

                if (zoneChanged)
                    ADLMRateGen.Helpers.AppMessage.Show("Prices updated for zone: " + serverZone);
            }
        }

        // Validate a cached license JWT via the new dual-algo path
        // (RS256 preferred, HS256 fallback) AND check the product is
        // entitled on this device. Returns the parsed payload on
        // success so the caller can pull profile fields without
        // re-parsing.
        private async System.Threading.Tasks.Task<(bool ok, JsonElement payload)> ValidateCachedLicenseAsync(string jwt)
        {
            var (ok, payload, _) = await JwtLicenseValidator.TryValidateAsync(jwt).ConfigureAwait(false);
            if (!ok) return (false, default);

            // Cached licence tokens issued before the v2 fingerprint carry the old
            // MAC-based dfp claim. Accept either value so upgrading users are not
            // bounced back to an online sign-in; the server re-issues the token
            // with the v2 dfp on their next successful login.
            var dfp = HardwareFingerprint.Get();
            if (JwtLicenseValidator.IsEntitledForDevice(payload, ProductKey, dfp))
                return (true, payload);

            var legacyDfp = ADLMRateGen.ADLM.Auth.DeviceFingerprint.Generate();
            return (JwtLicenseValidator.IsEntitledForDevice(payload, ProductKey, legacyDfp), payload);
        }

        private UserModel? BuildUserFromLicensePayload(JsonElement payload)
        {
            var user = new UserModel();

            if (payload.TryGetProperty("email", out var emailProp))
                user.Email = emailProp.GetString() ?? string.Empty;

            if (payload.TryGetProperty("username", out var unProp))
                user.Username = unProp.GetString() ?? string.Empty;

            if (payload.TryGetProperty("avatarUrl", out var avatarProp))
                user.AvatarUrl = avatarProp.GetString() ?? string.Empty;

            if (payload.TryGetProperty("firstName", out var firstNameProp))
                user.FirstName = firstNameProp.GetString() ?? string.Empty;

            if (payload.TryGetProperty("lastName", out var lastNameProp))
                user.LastName = lastNameProp.GetString() ?? string.Empty;

            if (payload.TryGetProperty("zone", out var zoneProp))
                user.Zone = zoneProp.GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(user.Username))
                user.Username = DeriveUsername(user.Email ?? Email);

            return user;
        }

        public class LoginEventArgs : EventArgs
        {
            public UserModel LoggedInUser { get; }
            public string AccessToken { get; set; }

            public LoginEventArgs(UserModel user)
            {
                LoggedInUser = user ?? throw new ArgumentNullException(nameof(user));
                AccessToken = string.Empty;
            }
        }
    }
}
