using System;

namespace ADLMRateGen.Helpers
{
    internal static class AppEnvironment
    {
        public const string DefaultApiBaseUrl = "https://api.adlmstudio.net";

        public const string DefaultAiServiceUrl =
            "https://rced0gx4rj.execute-api.us-east-1.amazonaws.com";

        private const string RetiredApiHost = "adlmweb.onrender.com";

        /// <summary>The developer's AI token override. See <see cref="AiServiceTokenOverride"/>.</summary>
        public const string AiTokenOverrideVariable = "ADLM_AI_TOKEN";

        /// <summary>
        /// True in a Debug build. The one switch that decides whether developer
        /// overrides such as ADLM_AI_TOKEN are honoured at all; an installed copy
        /// is a Release build and never sees them.
        /// </summary>
        public static bool IsDevelopmentBuild =>
#if DEBUG
            true;
#else
            false;
#endif

        /// <summary>
        /// API host, resolved from configuration rather than compiled in.
        ///
        /// Order: the product-specific override, then the fleet-wide variable
        /// the InstallerHub writes, then the current default. A value still
        /// pointing at the retired Render host is skipped rather than honoured:
        /// older RateGen installers wrote that host into HKCU\Environment, so on
        /// exactly the machines that are broken a stale variable would otherwise
        /// outrank the new default and the update would change nothing.
        /// </summary>
        public static string ApiBaseUrl =>
            (FirstLive("ADLM_RATEGEN_API_BASE_URL", "ADLM_API_BASE_URL") ?? DefaultApiBaseUrl)
                .TrimEnd('/');

        public static string ProductKey => Get("ADLM_RATEGEN_PRODUCT_KEY", "rategen");

        public static string MongoSrvConnectionString => Get("ADLM_RATEGEN_MONGO_SRV", string.Empty);

        public static string MongoStandardConnectionString => Get("ADLM_RATEGEN_MONGO_STANDARD", string.Empty);

        public static string MongoDatabaseName => Get("ADLM_RATEGEN_MONGO_DATABASE", "ADLMRateDB");

        public static string MongoUsersCollection => Get("ADLM_RATEGEN_MONGO_USERS_COLLECTION", "Users");

        public static string MongoMaterialsCollection => Get("ADLM_RATEGEN_MONGO_MATERIALS_COLLECTION", "Materials");

        public static string MongoLabourCollection => Get("ADLM_RATEGEN_MONGO_LABOUR_COLLECTION", "labours");

        public static string? OfflineLicenseSharedSecret => GetOptional("ADLM_RATEGEN_OFFLINE_LICENSE_SECRET");

        /// <summary>
        /// ADLM AI Service endpoint — the one thing that decides whether the
        /// "Build with AI" panel exists.
        ///
        /// It has a default, and must keep one. The endpoint is configuration,
        /// not a secret: it is the same public API Gateway host every ADLM
        /// product calls, and it refuses any request that arrives without a
        /// licence token. Resolving it from ADLM_AI_URL alone meant it resolved
        /// to nothing on an installed machine — nothing writes that variable, so
        /// the only box where the panel appeared was the development one that had
        /// it exported by hand, and every install hid the feature it had just
        /// shipped.
        ///
        /// Order: the product-specific override, then the fleet-wide variable,
        /// then the current default. Setting either to "off" (or none/disabled/
        /// false/0) disables the feature deliberately and hides the panel, which
        /// is now the only way it can disappear.
        ///
        /// The token comes from the signed-in licence token. ADLM_AI_TOKEN is a
        /// developer's override, honoured only when <see cref="IsDevelopmentBuild"/>;
        /// see <see cref="AiServiceTokenOverride"/>.
        /// </summary>
        public static string? AiServiceUrl
        {
            get
            {
                var configured = GetOptional("ADLM_RATEGEN_AI_URL") ?? GetOptional("ADLM_AI_URL");
                if (configured == null) return DefaultAiServiceUrl;
                return IsOff(configured) ? null : configured.TrimEnd('/');
            }
        }

        /// <summary>
        /// ADLM_AI_TOKEN as found on this machine, whatever the build. Whether it
        /// is ever SENT is decided by Services.AiTokenResolver: only a Debug build
        /// honours it, and only until the AI service rejects it once.
        ///
        /// It is a developer's convenience for calling the service without a
        /// subscription. Before this guard every build sent it ahead of the
        /// signed-in licence, so a dead token left in a user environment turned
        /// every "Build with AI" click into "Sign in again", and signing in again
        /// changed nothing because the licence was never consulted.
        /// </summary>
        public static string? AiServiceTokenOverride => GetOptional(AiTokenOverrideVariable);

        /// <summary>
        /// On an installed (Release) build, delete a user-scope ADLM_AI_TOKEN and
        /// forget the process copy, so a development token left behind on a
        /// machine cannot linger. Returns true when something was removed.
        ///
        /// Release ignores the variable anyway, so this is housekeeping rather
        /// than a dependency, in the same spirit as
        /// <see cref="RemoveRetiredHostOverrides"/>: remove the cause, not just
        /// step over it. A Debug build keeps it, because there it is deliberate.
        ///
        /// User scope only; a machine-scope value needs administrator rights.
        /// The name and build flag are parameters so a test can exercise this on a
        /// throwaway variable without touching anyone's real one.
        /// </summary>
        public static bool RemoveStaleAiTokenOverride(string? name = null, bool? developmentBuild = null)
        {
            name ??= AiTokenOverrideVariable;
            if (developmentBuild ?? IsDevelopmentBuild) return false;

            try
            {
                var userValue = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                var processValue = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrWhiteSpace(userValue) && string.IsNullOrWhiteSpace(processValue)) return false;

                if (!string.IsNullOrWhiteSpace(userValue))
                    Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);

                // The process copy was inherited at launch and is not affected by
                // the change above; clear it so this run stops seeing it too.
                Environment.SetEnvironmentVariable(name, null);

                System.Diagnostics.Debug.WriteLine(
                    $"[AppEnvironment] Removed {name}: an installed build takes its AI token from the signed-in licence, so a leftover development token could only get in the way.");
                return true;
            }
            catch (System.Security.SecurityException)
            {
                // No permission to write the user environment. Release ignores the
                // variable regardless, so nothing is lost this launch.
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppEnvironment] Could not clean {name}: {ex.Message}");
                return false;
            }
        }

        public static string? LocalJwtSecret => GetOptional("ADLM_RATEGEN_LOCAL_JWT_SECRET");

        /// <summary>
        /// Delete any user environment variable still pointing at the retired
        /// Render host.
        ///
        /// FirstLive already ignores those values, so nothing depends on this to
        /// work today. The difference is that ignoring is permanent: the stale
        /// variable stays on the machine, and the guard above has to live forever
        /// to keep stepping over it. Removing it fixes the cause, so once enough
        /// installs have run this, the guard can go.
        ///
        /// Deletes rather than corrects, because the right value is not this
        /// method's to decide: with the variable gone, ApiBaseUrl falls through to
        /// the current default, and the InstallerHub rewrites the fleet-wide one
        /// when it next needs to.
        ///
        /// User scope only. A machine-scope value would need administrator rights,
        /// and a normal launch does not have them.
        /// </summary>
        public static void RemoveRetiredHostOverrides()
        {
            foreach (var name in new[] { "ADLM_RATEGEN_API_BASE_URL", "ADLM_API_BASE_URL" })
            {
                try
                {
                    var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    if (value.IndexOf(RetiredApiHost, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);

                    // The process copy was inherited at launch and is not affected
                    // by the change above, so clear it too. Without this the app
                    // would keep reading the stale value until it restarted.
                    Environment.SetEnvironmentVariable(name, null);

                    System.Diagnostics.Debug.WriteLine(
                        $"[AppEnvironment] Removed {name}, which pointed at the retired host {RetiredApiHost}.");
                }
                catch (System.Security.SecurityException)
                {
                    // No permission to write user environment. The guard in
                    // FirstLive still protects this launch, so carry on.
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AppEnvironment] Could not clean {name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// First variable that is set and does not name the retired host.
        /// </summary>
        private static string? FirstLive(params string[] names)
        {
            foreach (var name in names)
            {
                var value = GetOptional(name);
                if (value == null) continue;
                if (value.IndexOf(RetiredApiHost, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                return value;
            }

            return null;
        }

        /// <summary>A value that asks for a feature to be off, rather than a URL.</summary>
        private static bool IsOff(string value) =>
            value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("0", StringComparison.Ordinal);

        private static string Get(string name, string fallback)
        {
            return GetOptional(name) ?? fallback;
        }

        private static string? GetOptional(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
