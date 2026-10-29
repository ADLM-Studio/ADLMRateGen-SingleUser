using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ADLMRateGen.Helpers;
using ADLMRateGen.Properties;

namespace ADLMRateGen.Services
{
    /// <summary>
    /// Syncs the material/labour libraries from the single source of truth:
    /// the ADLM Cloud master library (/rategen/master, zone-priced from the
    /// RateGen admin database — the same data the website admin edits, QUIV
    /// budget pricing reads, and the ADLM AI Service grounds on).
    ///
    /// Fetches the signed-in user's zone prices and persists them through
    /// DataSourceCloudSync (which merges the user's own rows and refreshes
    /// MaterialLibraryService / LabourLibraryService in place).
    /// </summary>
    public static class MasterLibrarySyncService
    {
        public sealed class SyncResult
        {
            public bool Ok { get; set; }
            public string Zone { get; set; } = string.Empty;
            public string State { get; set; } = string.Empty;
            public int Materials { get; set; }
            public int Labours { get; set; }
            public string Message { get; set; } = string.Empty;
        }

        /// <summary>
        /// Raised after a sync has written both libraries, so every screen that
        /// shows them reloads, whichever button started the sync.
        /// </summary>
        public static event Action<SyncResult>? Synced;

        public static string ResolveZone()
        {
            var zone = AppSettings.Zone;
            if (string.IsNullOrWhiteSpace(zone))
            {
                try { zone = ConfigManager.LoadConfig()?.Zone; } catch { }
            }
            return string.IsNullOrWhiteSpace(zone) ? "Lagos" : zone.Trim();
        }

        /// <summary>
        /// The state key to price against, normalised to something the server
        /// recognises.
        ///
        /// The setting has always been called Zone and has always held a STATE
        /// name: it ships defaulted to "Lagos", which is not one of the six
        /// geopolitical zone keys. So the server's normalizeZone rejected it and
        /// fell back to the account zone, and the value the user could see was
        /// never the value that priced their work. Reading it as a state is what
        /// it always meant.
        /// </summary>
        public static string ResolveState()
            => NigerianStates.Normalize(ResolveZone()) ?? NigerianStates.DefaultKey;

        /// <summary>Persist the chosen state and return its key.</summary>
        public static string SetState(string keyOrLabel)
        {
            var key = NigerianStates.Normalize(keyOrLabel) ?? NigerianStates.DefaultKey;
            var label = NigerianStates.Find(key)?.Label ?? key;

            AppSettings.Zone = label;
            try
            {
                var cfg = ConfigManager.LoadConfig();
                if (cfg != null) { cfg.Zone = label; ConfigManager.SaveConfig(cfg); }
            }
            catch { }
            return key;
        }

        public static async Task<SyncResult> SyncAsync(CancellationToken ct = default)
        {
            var result = new SyncResult { Zone = ResolveZone(), State = ResolveState() };

            var auth = AuthProvider.Instance.Client;
            if (auth == null || !auth.HasSession)
            {
                result.Message = "Sign in to sync master prices.";
                return result;
            }

            // The pricing location lives on the ADLM profile and nowhere else, so
            // ask the profile first and price from exactly what it says.
            //
            // This used to ask for "my prices" without naming a state and let the
            // server fall back to the account's. That fallback reads the state
            // from the access token's claims, minted at sign-in, so a location
            // changed on the website since then was invisible to it: Sync from
            // Cloud ran, reported success, and brought back the old location's
            // prices until the user signed out and in again. /me/profile reads
            // the database. Sending its state is not a local setting overriding
            // the account, which is what this path was built to prevent; it IS
            // the account, read a moment ago. When the profile cannot be read the
            // request goes without a state, as before.
            var profileState = await ReadProfileStateAsync(auth, ct);
            if (profileState != null)
            {
                SetState(profileState);
                result.State = profileState;
                result.Zone = NigerianStates.Find(profileState)?.Label ?? result.Zone;
            }

            var path = profileState == null
                ? "/rategen/master"
                : "/rategen/master?state=" + Uri.EscapeDataString(profileState);

            using (var doc = await auth.GetJsonAsync(path, ct))
            {
                var root = doc.RootElement;

                // GetJsonRawAsync turns 404 and 204 into the literal "[]", so a
                // missing or empty response arrives here as an ARRAY. Every
                // TryGetProperty below throws on a non-object, which surfaced as a
                // bare "Master prices: FAIL" that read like a crash rather than
                // "the server had nothing for you". Say so instead.
                if (root.ValueKind != JsonValueKind.Object)
                {
                    result.Message =
                        "The server returned no master prices (empty or missing response).";
                    return result;
                }

                // Record where the server actually priced from, so the library can
                // say so on screen. This is the app following the account, never
                // the other way round.
                if (root.TryGetProperty("state", out var st) &&
                    st.ValueKind == JsonValueKind.String)
                {
                    var key = NigerianStates.Normalize(st.GetString());
                    if (key != null)
                    {
                        SetState(key);
                        result.State = key;
                        result.Zone = NigerianStates.Find(key)?.Label ?? result.Zone;
                    }
                }

                if (root.TryGetProperty("materials", out var mats) &&
                    mats.ValueKind == JsonValueKind.Array)
                {
                    DataSourceCloudSync.SaveMaterialsFromDto(mats);
                    result.Materials = mats.GetArrayLength();
                }

                JsonElement labs;
                var hasLabs =
                    (root.TryGetProperty("labour", out labs) && labs.ValueKind == JsonValueKind.Array) ||
                    (root.TryGetProperty("labours", out labs) && labs.ValueKind == JsonValueKind.Array);
                if (hasLabs)
                {
                    DataSourceCloudSync.SaveLaboursFromDto(labs);
                    result.Labours = labs.GetArrayLength();
                }
            }

            result.Ok = result.Materials > 0 || result.Labours > 0;
            result.Message = result.Ok
                ? $"Master prices for '{result.Zone}': {result.Materials} materials, {result.Labours} labour rates."
                : $"No master prices returned for zone '{result.Zone}'.";

            if (result.Ok)
            {
                try { Synced?.Invoke(result); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MasterLibrarySync] Synced handler: {ex.Message}"); }
            }
            return result;
        }

        /// <summary>
        /// The state on the ADLM profile, as a key the server recognises, or null
        /// when the profile has none set or cannot be read right now. Only the
        /// state is read: the profile's "zone" is the geopolitical zone derived
        /// from it, which is not a state and would not normalise to one.
        /// </summary>
        public static Task<string?> ReadProfileStateAsync(CancellationToken ct = default)
        {
            var auth = AuthProvider.Instance.Client;
            if (auth == null || !auth.HasSession) return Task.FromResult<string?>(null);
            return ReadProfileStateAsync(auth, ct);
        }

        private static async Task<string?> ReadProfileStateAsync(ADLM.Auth.AuthClient auth, CancellationToken ct)
        {
            try
            {
                using var doc = await auth.GetJsonAsync("/me/profile", ct);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (!root.TryGetProperty("state", out var st) || st.ValueKind != JsonValueKind.String) return null;
                return NigerianStates.Normalize(st.GetString());
            }
            catch
            {
                return null;
            }
        }
    }
}
