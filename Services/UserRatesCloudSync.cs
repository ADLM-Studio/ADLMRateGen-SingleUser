using ADLMRateGen.ViewModel;
using ADLMRateGen.ViewModel.CustomRate;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ADLMRateGen.Services
{
    public sealed class UserRatesCloudSync
    {
        private static readonly JsonSerializerOptions ReadJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonSerializerOptions WriteJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static UserRatesCloudSync Instance { get; } = new UserRatesCloudSync();

        // Tells the server this desktop pulls custom rates before it pushes and
        // deletes only what its user deleted, so its DELETE can be honoured.
        // Rate Gen 2.9.x and earlier deleted every cloud custom rate missing
        // from its own list, which erased rates made on the website or on
        // another PC (server/util/rategenCustomRateGuard.js in ADLMWebsite).
        internal const string SyncProtocolHeader = "X-ADLM-Rates-Sync";
        internal const string SyncProtocolVersion = "2";

        private readonly SemaphoreSlim _syncLock = new SemaphoreSlim(1, 1);

        public DateTime? LastSyncUtc { get; private set; }
        public string LastStatus { get; private set; } = "User rates cloud sync not started.";

        private UserRatesCloudSync() { }

        /// <summary>
        /// Fetches the user's saved rate overrides from the cloud and merges them into
        /// the local <see cref="UserRateEditStore"/>. Called after login so edits made
        /// on QUIV / HERON propagate back to RateGen.
        /// </summary>
        public async Task<bool> PullUserEditsAsync(CancellationToken ct = default)
        {
            var auth = AuthProvider.Instance.Client;
            if (!auth.HasSession || string.IsNullOrWhiteSpace(auth.AccessToken))
            {
                LastStatus = "User rates pull skipped: not signed in.";
                return false;
            }

            try
            {
                using var doc = await auth.GetJsonAsync("/rategen-v2/library/user-rates", ct).ConfigureAwait(false);
                var state = JsonSerializer.Deserialize<ServerLibraryState>(
                                doc.RootElement.GetRawText(),
                                ReadJsonOptions)
                            ?? new ServerLibraryState();

                var edits = new List<ADLMRateGen.ViewModel.Model.UserRateEdit>();
                foreach (var ov in state.RateOverrides ?? new List<RateOverridePayload>())
                {
                    if (ov == null) continue;
                    if (string.IsNullOrWhiteSpace(ov.SectionKey) || ov.ItemNo == null) continue;

                    foreach (var line in ov.Breakdown ?? new List<BreakdownPayload>())
                    {
                        if (line == null) continue;
                        if (string.IsNullOrWhiteSpace(line.ComponentName)) continue;
                        // Skip computed sub-total / total rows — we never override those.
                        if (line.ComponentName.IndexOf("total", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                        edits.Add(new ADLMRateGen.ViewModel.Model.UserRateEdit
                        {
                            SectionKey = ov.SectionKey,
                            ItemNo = ov.ItemNo.Value,
                            ComponentName = line.ComponentName,
                            OverrideQuantity = (double)line.Quantity,
                            EditedAtUtc = ov.ClientUpdatedAt ?? DateTime.UtcNow
                        });
                    }
                }

                UserRateEditStore.Current.ReplaceAll(edits);
                UserRateEditStore.Current.SaveToDisk();

                var pulled = 0;
                await _syncLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var http = CreateHttpClient();
                    pulled = (await SyncCustomRatesAsync(auth, http, state.CustomRates, ct).ConfigureAwait(false)).Pulled;
                }
                finally
                {
                    _syncLock.Release();
                }

                LastSyncUtc = DateTime.Now;
                LastStatus = $"User rates pulled from cloud: {edits.Count} component quantities, {pulled} custom rates downloaded.";
                return true;
            }
            catch (Exception ex)
            {
                LastStatus = $"User rates pull failed: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[UserRatesCloudSync.PullUserEditsAsync] {ex}");
                return false;
            }
        }

        public async Task<bool> PushSnapshotAsync(MainViewModel vm, CancellationToken ct = default)
        {
            if (vm == null)
            {
                LastStatus = "User rates sync skipped: main view model is unavailable.";
                return false;
            }

            var auth = AuthProvider.Instance.Client;
            if (!auth.HasSession || string.IsNullOrWhiteSpace(auth.AccessToken))
            {
                LastStatus = "User rates sync skipped: not signed in.";
                return false;
            }

            await _syncLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var snapshot = BuildSnapshot(vm);
                var serverState = await GetServerStateAsync(auth, ct).ConfigureAwait(false);
                var overrideChanges = CountRateOverrideChanges(snapshot.RateOverrides, serverState.RateOverrides);

                using var http = CreateHttpClient();

                // Custom rates sync one by one, cloud first: never by replacing
                // the cloud's list with this PC's.
                var customSync = await SyncCustomRatesAsync(auth, http, serverState.CustomRates, ct).ConfigureAwait(false);
                var customStatus =
                    $"{customSync.Pulled} custom rates downloaded, {customSync.Pushed} uploaded, {customSync.Deleted} deleted.";

                if (overrideChanges.Upserted == 0 &&
                    overrideChanges.Deleted == 0)
                {
                    LastSyncUtc = DateTime.Now;
                    LastStatus = customSync.Any
                        ? $"User rates synced: {customStatus}"
                        : "User rates sync already up to date.";
                    return true;
                }

                string? bulkFailure = null;
                try
                {
                    await PushWholeSnapshotAsync(
                        auth,
                        http,
                        snapshot,
                        serverState,
                        ct).ConfigureAwait(false);

                    LastSyncUtc = DateTime.Now;
                    LastStatus =
                        $"User rates synced live: {overrideChanges.Upserted} overrides updated, {overrideChanges.Deleted} removed, " +
                        customStatus;
                    return true;
                }
                catch (Exception ex)
                {
                    bulkFailure = ex.Message;
                }

                var overrideSync = await SyncRateOverridesAsync(
                    auth,
                    http,
                    snapshot.RateOverrides,
                    serverState.RateOverrides,
                    ct).ConfigureAwait(false);

                LastSyncUtc = DateTime.Now;
                LastStatus =
                    $"User rates synced: {overrideSync.Upserted} overrides updated, {overrideSync.Deleted} removed, " +
                    customStatus +
                    (string.IsNullOrWhiteSpace(bulkFailure) ? string.Empty : $" Fallback mode was used after bulk sync failed: {bulkFailure}");

                return true;
            }
            catch (Exception ex)
            {
                LastStatus = $"User rates sync failed: {ex.Message}";
                return false;
            }
            finally
            {
                _syncLock.Release();
            }
        }

        private static async Task<ServerLibraryState> GetServerStateAsync(
            ADLMRateGen.ADLM.Auth.AuthClient auth,
            CancellationToken ct)
        {
            using var doc = await auth.GetJsonAsync("/rategen-v2/library/user-rates", ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ServerLibraryState>(
                       doc.RootElement.GetRawText(),
                       ReadJsonOptions)
                   ?? new ServerLibraryState();
        }

        private static async Task PushWholeSnapshotAsync(
            ADLMRateGen.ADLM.Auth.AuthClient auth,
            HttpClient http,
            UserRateSnapshot snapshot,
            ServerLibraryState serverState,
            CancellationToken ct)
        {
            // Overrides only. Custom rates never go in a whole-list push: see
            // SyncCustomRatesAsync.
            var payload = new Dictionary<string, object>();

            {
                // Resolve RateIds before bulk push so the server can match
                // existing records instead of creating duplicates.
                var masterRateIds = BuildMasterRateLookup();
                var serverLookup = (serverState.RateOverrides ?? new List<RateOverridePayload>())
                    .Where(r => !string.IsNullOrWhiteSpace(BuildRateIdentityKey(r)))
                    .GroupBy(r => BuildRateIdentityKey(r), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var local in snapshot.RateOverrides)
                {
                    var key = BuildRateIdentityKey(local);
                    serverLookup.TryGetValue(key, out var server);
                    local.RateId = ResolveRateId(local, server, masterRateIds);
                }

                payload["rateOverrides"] = snapshot.RateOverrides;
                payload["ratesBaseVersion"] = serverState.RatesVersion;
            }

            await SendJsonAsync(
                auth,
                http,
                HttpMethod.Put,
                "/rategen-v2/library/user-rates",
                payload,
                ct).ConfigureAwait(false);
        }

        private static async Task<SyncCounters> SyncRateOverridesAsync(
            ADLMRateGen.ADLM.Auth.AuthClient auth,
            HttpClient http,
            IReadOnlyList<RateOverridePayload> localOverrides,
            IReadOnlyList<RateOverridePayload> serverOverrides,
            CancellationToken ct)
        {
            var masterRateIds = BuildMasterRateLookup();

            var loadedSections = new HashSet<string>(
                localOverrides
                    .Select(item => NormalizeText(item.SectionKey).ToLowerInvariant())
                    .Where(section => !string.IsNullOrWhiteSpace(section)),
                StringComparer.OrdinalIgnoreCase);

            var localKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var upserted = 0;
            var deleted = 0;

            foreach (var local in localOverrides)
            {
                var key = BuildRateIdentityKey(local);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                localKeys.Add(key);
                var server = FindServerOverride(serverOverrides, key);

                local.RateId = ResolveRateId(local, server, masterRateIds);
                local.ClientUpdatedAt = DateTime.Now;

                if (server != null && AreEquivalent(local, server))
                {
                    continue;
                }

                await SendJsonAsync(
                    auth,
                    http,
                    HttpMethod.Put,
                    $"/rategen-v2/library/user-rates/override/{Uri.EscapeDataString(local.RateId)}",
                    local,
                    ct).ConfigureAwait(false);

                upserted += 1;
            }

            foreach (var server in serverOverrides ?? Array.Empty<RateOverridePayload>())
            {
                var key = BuildRateIdentityKey(server);
                if (string.IsNullOrWhiteSpace(key) || localKeys.Contains(key))
                {
                    continue;
                }

                var sectionKey = NormalizeText(server.SectionKey).ToLowerInvariant();
                if (!loadedSections.Contains(sectionKey))
                {
                    continue;
                }

                var rateId = !string.IsNullOrWhiteSpace(server.RateId)
                    ? server.RateId
                    : BuildFallbackRateId(key);

                await SendJsonAsync(
                    auth,
                    http,
                    HttpMethod.Delete,
                    $"/rategen-v2/library/user-rates/override/{Uri.EscapeDataString(rateId)}",
                    null,
                    ct).ConfigureAwait(false);

                deleted += 1;
            }

            return new SyncCounters(upserted, deleted);
        }

        /// <summary>
        /// Custom rates, cloud first. Rates the cloud has and this PC does not are
        /// downloaded. A rate is deleted in the cloud only when the user deleted it
        /// here (CustomRateServices.RecordDeletion), never because this PC lacks
        /// it: that rule erased every rate made on the website or another PC.
        /// Only rates changed here since they last matched the cloud are uploaded.
        /// </summary>
        private static async Task<CustomRateSyncResult> SyncCustomRatesAsync(
            ADLMRateGen.ADLM.Auth.AuthClient auth,
            HttpClient http,
            IReadOnlyList<CustomRatePayload>? serverRates,
            CancellationToken ct)
        {
            var server = (serverRates ?? new List<CustomRatePayload>())
                .Where(rate => rate != null && !string.IsNullOrWhiteSpace(rate.CustomRateId))
                .ToList();
            var deletions = CustomRateServices.LoadDeletions();

            // Only worth asking when a rate this PC had in step with the cloud is
            // no longer there.
            var serverIds = new HashSet<string>(server.Select(rate => rate.CustomRateId.Trim()), StringComparer.OrdinalIgnoreCase);
            var archived = CustomRateServices.LoadCustomRates()
                .Any(rate => rate.SyncedSignature != null && !serverIds.Contains(CustomRateServices.CloudKey(rate)))
                ? await GetDeletedReasonsAsync(auth, ct).ConfigureAwait(false)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            CustomRatePlan plan = new CustomRatePlan();
            CustomRateServices.Mutate(local =>
            {
                plan = PlanCustomRateSync(local, server, deletions, archived);
                return plan.LocalChanged;
            }, notify: true);

            var deleted = 0;
            foreach (var id in plan.ToDelete)
            {
                await SendJsonAsync(
                    auth,
                    http,
                    HttpMethod.Delete,
                    $"/rategen-v2/library/custom-rates/{Uri.EscapeDataString(id)}",
                    null,
                    ct).ConfigureAwait(false);

                CustomRateServices.ClearDeletion(id);
                deleted += 1;
            }

            foreach (var id in plan.StaleDeletions)
            {
                CustomRateServices.ClearDeletion(id);
            }

            var pushed = new Dictionary<Guid, (string Signature, DateTime? CloudUpdatedAt)>();
            foreach (var rate in plan.ToPush)
            {
                var payload = ToCustomRatePayload(rate);
                if (payload.CreatedAt == default)
                {
                    payload.CreatedAt = DateTime.Now;
                }

                payload.UpdatedAt = DateTime.Now;

                var text = await SendJsonAsync(
                    auth,
                    http,
                    HttpMethod.Put,
                    $"/rategen-v2/library/custom-rates/{Uri.EscapeDataString(payload.CustomRateId)}",
                    payload,
                    ct).ConfigureAwait(false);

                pushed[rate.Id] = (Signature(payload), ReadItemUpdatedAt(text));
            }

            if (pushed.Count > 0)
            {
                // Mark what was uploaded as in step, unless the user changed it
                // again while the upload ran.
                CustomRateServices.Mutate(local =>
                {
                    var changed = false;
                    foreach (var rate in local)
                    {
                        if (pushed.TryGetValue(rate.Id, out var sent) && Signature(rate) == sent.Signature)
                        {
                            rate.SyncedSignature = sent.Signature;
                            rate.CloudUpdatedAt = sent.CloudUpdatedAt;
                            changed = true;
                        }
                    }
                    return changed;
                }, notify: false);
            }

            return new CustomRateSyncResult(
                plan.Pulled + plan.Refreshed,
                pushed.Count,
                deleted + plan.RemovedLocally);
        }

        internal sealed class CustomRatePlan
        {
            public int Pulled { get; set; }
            public int Refreshed { get; set; }
            public int RemovedLocally { get; set; }
            public bool LocalChanged { get; set; }
            public List<CustomRate> ToPush { get; } = new List<CustomRate>();
            public List<string> ToDelete { get; } = new List<string>();
            public List<string> StaleDeletions { get; } = new List<string>();
        }

        // The archive reason the server gives an explicit delete from a desktop
        // that pulls first (server/util/rategenCustomRateGuard.js).
        internal const string DeletedByClientReason = "deleted-by-client";

        /// <summary>
        /// Decides the custom-rate sync and applies the cloud's side to
        /// <paramref name="local"/>. No network: the caller sends what it returns.
        /// </summary>
        internal static CustomRatePlan PlanCustomRateSync(
            List<CustomRate> local,
            IReadOnlyList<CustomRatePayload> server,
            ISet<string> deletions,
            IReadOnlyDictionary<string, string> archivedReasons)
        {
            var plan = new CustomRatePlan();

            var serverById = server
                .Where(rate => rate != null && !string.IsNullOrWhiteSpace(rate.CustomRateId))
                .GroupBy(rate => rate.CustomRateId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var localByKey = new Dictionary<string, CustomRate>(StringComparer.OrdinalIgnoreCase);
            foreach (var rate in local)
            {
                localByKey.TryAdd(CustomRateServices.CloudKey(rate), rate);
            }

            foreach (var id in deletions)
            {
                (serverById.ContainsKey(id) ? plan.ToDelete : plan.StaleDeletions).Add(id);
            }

            // Down: what the cloud has that this PC lacks, or has newer.
            foreach (var pair in serverById)
            {
                if (deletions.Contains(pair.Key))
                {
                    continue;
                }

                if (!localByKey.TryGetValue(pair.Key, out var rate))
                {
                    rate = FromCloud(pair.Value);
                    local.Add(rate);
                    localByKey[pair.Key] = rate;
                    plan.Pulled += 1;
                    plan.LocalChanged = true;
                }
                else if (IsUnchangedSinceSync(rate) && !SameCloudVersion(rate.CloudUpdatedAt, pair.Value.UpdatedAt))
                {
                    ApplyCloud(rate, pair.Value);
                    plan.Refreshed += 1;
                    plan.LocalChanged = true;
                }
            }

            // Up: what changed here, or the cloud does not have.
            for (var i = local.Count - 1; i >= 0; i--)
            {
                var rate = local[i];
                var id = CustomRateServices.CloudKey(rate);

                if (serverById.TryGetValue(id, out var cloud))
                {
                    if (IsUnchangedSinceSync(rate))
                    {
                        continue;
                    }

                    var payload = ToCustomRatePayload(rate);
                    if (AreEquivalent(payload, cloud))
                    {
                        rate.SyncedSignature = Signature(payload);
                        rate.CloudUpdatedAt = cloud.UpdatedAt;
                        plan.LocalChanged = true;
                    }
                    else
                    {
                        plan.ToPush.Add(rate);
                    }

                    continue;
                }

                // In step with the cloud once, gone from it now, and the user
                // deleted it on another desktop: follow. Anything else (never
                // uploaded, edited here since, or dropped by an old desktop's
                // sync) is uploaded, so this PC's copy is never lost.
                if (IsUnchangedSinceSync(rate) &&
                    archivedReasons.TryGetValue(id, out var reason) &&
                    string.Equals(reason, DeletedByClientReason, StringComparison.OrdinalIgnoreCase))
                {
                    local.RemoveAt(i);
                    plan.RemovedLocally += 1;
                    plan.LocalChanged = true;
                    continue;
                }

                plan.ToPush.Add(rate);
            }

            plan.ToPush.Reverse();
            return plan;
        }

        private static bool IsUnchangedSinceSync(CustomRate rate) =>
            rate.SyncedSignature != null &&
            string.Equals(Signature(rate), rate.SyncedSignature, StringComparison.Ordinal);

        private static bool SameCloudVersion(DateTime? local, DateTime? cloud)
        {
            if (local == null || cloud == null)
            {
                return local == null && cloud == null;
            }

            // Mongo keeps milliseconds.
            return Math.Abs((local.Value.ToUniversalTime() - cloud.Value.ToUniversalTime()).TotalMilliseconds) < 1;
        }

        internal static CustomRate FromCloud(CustomRatePayload cloud)
        {
            var id = cloud.CustomRateId.Trim();
            var isGuid = Guid.TryParse(id, out var guid);
            var rate = new CustomRate
            {
                Id = isGuid ? guid : Guid.NewGuid(),
                CloudId = isGuid ? null : id
            };
            ApplyCloud(rate, cloud);
            return rate;
        }

        private static void ApplyCloud(CustomRate rate, CustomRatePayload cloud)
        {
            rate.Title = !string.IsNullOrWhiteSpace(cloud.Title) ? cloud.Title : cloud.Description;
            rate.Description = cloud.Description ?? string.Empty;
            rate.OverheadPercent = cloud.OverheadPercent;
            rate.ProfitPercent = cloud.ProfitPercent;
            if (cloud.CreatedAt != default)
            {
                rate.CreatedDate = cloud.CreatedAt.ToLocalTime();
            }

            rate.CloudUnit = cloud.Unit;
            rate.SectionKey = cloud.SectionKey;
            rate.SectionLabel = cloud.SectionLabel;
            // Plant lines (breakdown only) have no place here yet; the server
            // keeps them when this rate is pushed back.
            rate.MaterialItems = (cloud.Materials ?? new List<CustomRateLinePayload>())
                .Select(line => ToEntry(line, RateItemType.Material))
                .ToList();
            rate.LabourItems = (cloud.Labour ?? new List<CustomRateLinePayload>())
                .Select(line => ToEntry(line, RateItemType.Labour))
                .ToList();
            rate.CloudUpdatedAt = cloud.UpdatedAt;
            rate.SyncedSignature = Signature(rate);
        }

        private static RateEntryItem ToEntry(CustomRateLinePayload line, RateItemType type)
        {
            // Unit and price last: setting Description re-prices the line from
            // this PC's library and would replace the cloud's figure.
            var item = new RateEntryItem { RateType = type };
            item.Description = line.Description ?? string.Empty;
            item.Quantity = line.Quantity;
            item.Unit = line.Unit ?? string.Empty;
            item.UnitPrice = line.UnitPrice;
            return item;
        }

        private static async Task<Dictionary<string, string>> GetDeletedReasonsAsync(
            ADLMRateGen.ADLM.Auth.AuthClient auth,
            CancellationToken ct)
        {
            var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var doc = await auth.GetJsonAsync("/rategen-v2/library/custom-rates/deleted", ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var id = item.TryGetProperty("customRateId", out var idEl) ? idEl.GetString() : null;
                        var reason = item.TryGetProperty("deletedReason", out var reasonEl) ? reasonEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            reasons[id.Trim()] = reason ?? string.Empty;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // A server without the archive: every missing rate is uploaded.
                System.Diagnostics.Debug.WriteLine($"[UserRatesCloudSync.GetDeletedReasonsAsync] {ex.Message}");
            }

            return reasons;
        }

        private static DateTime? ReadItemUpdatedAt(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("item", out var item) &&
                    item.TryGetProperty("updatedAt", out var updatedAt) &&
                    updatedAt.ValueKind == JsonValueKind.String &&
                    updatedAt.TryGetDateTime(out var value))
                {
                    return value.ToUniversalTime();
                }
            }
            catch (JsonException)
            {
            }

            return null;
        }

        private readonly struct CustomRateSyncResult
        {
            public CustomRateSyncResult(int pulled, int pushed, int deleted)
            {
                Pulled = pulled;
                Pushed = pushed;
                Deleted = deleted;
            }

            public int Pulled { get; }
            public int Pushed { get; }
            public int Deleted { get; }
            public bool Any => Pulled > 0 || Pushed > 0 || Deleted > 0;
        }

        private static SyncCounters CountRateOverrideChanges(
            IReadOnlyList<RateOverridePayload> localOverrides,
            IReadOnlyList<RateOverridePayload> serverOverrides)
        {
            var loadedSections = new HashSet<string>(
                (localOverrides ?? Array.Empty<RateOverridePayload>())
                    .Select(item => NormalizeText(item.SectionKey).ToLowerInvariant())
                    .Where(section => !string.IsNullOrWhiteSpace(section)),
                StringComparer.OrdinalIgnoreCase);

            var localKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var upserted = 0;
            var deleted = 0;

            foreach (var local in localOverrides ?? Array.Empty<RateOverridePayload>())
            {
                var key = BuildRateIdentityKey(local);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                localKeys.Add(key);
                var server = FindServerOverride(serverOverrides, key);

                if (server == null || !AreEquivalent(local, server))
                {
                    upserted += 1;
                }
            }

            foreach (var server in serverOverrides ?? Array.Empty<RateOverridePayload>())
            {
                var key = BuildRateIdentityKey(server);
                if (string.IsNullOrWhiteSpace(key) || localKeys.Contains(key))
                {
                    continue;
                }

                var sectionKey = NormalizeText(server.SectionKey).ToLowerInvariant();
                if (loadedSections.Contains(sectionKey))
                {
                    deleted += 1;
                }
            }

            return new SyncCounters(upserted, deleted);
        }

        private static async Task<string> SendJsonAsync(
            ADLMRateGen.ADLM.Auth.AuthClient auth,
            HttpClient http,
            HttpMethod method,
            string path,
            object? body,
            CancellationToken ct)
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                using var request = new HttpRequestMessage(method, CombineUrl(auth.BaseUrl, path));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
                request.Headers.Add(SyncProtocolHeader, SyncProtocolVersion);

                if (body != null)
                {
                    var json = JsonSerializer.Serialize(body, WriteJsonOptions);
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                using var response = await http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct)
                    .ConfigureAwait(false);

                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return text;
                }

                if ((response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden) &&
                    attempt == 1)
                {
                    using var refreshDoc = await auth.GetJsonAsync("/rategen-v2/library/meta", ct).ConfigureAwait(false);
                    continue;
                }

                throw CreateHttpException(response.StatusCode, text);
            }

            throw new TimeoutException("User rate sync request timed out.");
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            return http;
        }

        private static Exception CreateHttpException(HttpStatusCode statusCode, string text)
        {
            var message = $"HTTP {(int)statusCode}";
            if (!string.IsNullOrWhiteSpace(text))
            {
                message += $": {Trim(text, 280)}";
            }

            return statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? new UnauthorizedAccessException(message)
                : new InvalidOperationException(message);
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            var cleanBase = (baseUrl ?? string.Empty).TrimEnd('/');
            var cleanPath = (path ?? string.Empty).TrimStart('/');
            return $"{cleanBase}/{cleanPath}";
        }

        private static RateOverridePayload? FindServerOverride(
            IReadOnlyList<RateOverridePayload> serverOverrides,
            string key)
        {
            return (serverOverrides ?? Array.Empty<RateOverridePayload>())
                .FirstOrDefault(item => string.Equals(BuildRateIdentityKey(item), key, StringComparison.OrdinalIgnoreCase));
        }

        private static UserRateSnapshot BuildSnapshot(MainViewModel vm)
        {
            var overrides = new List<RateOverridePayload>();

            foreach (var section in EnumerateSections(vm))
            {
                overrides.AddRange(BuildSectionOverrides(section.key, section.label, section.viewModel));
            }

            return new UserRateSnapshot(overrides);
        }

        private static IEnumerable<(string key, string label, object viewModel)> EnumerateSections(MainViewModel vm)
        {
            yield return (SectionKeys.Ground, "Groundwork", vm.GroundWorkViewModel);
            yield return (SectionKeys.Concrete, "Concrete Works", vm.ConcreteViewModel);
            yield return (SectionKeys.Blockwork, "Blockwork", vm.BlockworkViewModel);
            yield return (SectionKeys.Finishes, "Finishes", vm.FinishesViewModel);
            yield return (SectionKeys.Roofing, "Roofing", vm.RoofWorkViewModel);
            yield return (SectionKeys.DoorsWindows, "Windows & Doors", vm.WindowAndDoorViewModel);
            yield return (SectionKeys.Paint, "Painting", vm.PaintWorkViewModel);
            yield return (SectionKeys.Steelwork, "Steelwork", vm.SteelWorkViewModel);
            yield return (SectionKeys.CarbonOthers, "Carbon and Others", vm.CarbonOthersViewModel);
            yield return (SectionKeys.Mep, "MEP Works", vm.MepWorkViewModel);
        }

        private static IEnumerable<RateOverridePayload> BuildSectionOverrides(string sectionKey, string sectionLabel, object viewModel)
        {
            var rows = FindRowsEnumerable(viewModel);
            if (rows == null)
            {
                yield break;
            }

            var overheadPercent = GetDecimalProp(viewModel, "OverheadPercent");
            var profitPercent = GetDecimalProp(viewModel, "ProfitPercent");

            foreach (var row in rows)
            {
                if (row == null)
                {
                    continue;
                }

                var description = GetStringProp(row, "Description", "Name", "Title");
                if (string.IsNullOrWhiteSpace(description))
                {
                    continue;
                }

                yield return new RateOverridePayload
                {
                    SectionKey = sectionKey,
                    SectionLabel = sectionLabel,
                    ItemNo = GetIntProp(row, "ItemNo", "SNo"),
                    Description = description.Trim(),
                    Unit = GetStringProp(row, "Unit") ?? string.Empty,
                    NetCost = GetDecimalProp(row, "NetCost"),
                    OverheadPercent = overheadPercent,
                    ProfitPercent = profitPercent,
                    OverheadValue = GetDecimalProp(row, "OverheadValue"),
                    ProfitValue = GetDecimalProp(row, "ProfitValue"),
                    TotalCost = GetDecimalProp(row, "TotalCost", "TotalPrice", "Total"),
                    Breakdown = BuildBreakdownPayload(row).ToList()
                };
            }
        }

        private static IEnumerable<BreakdownPayload> BuildBreakdownPayload(object row)
        {
            var breakdown = FindBreakdownEnumerable(row);
            if (breakdown == null)
            {
                yield break;
            }

            foreach (var line in breakdown)
            {
                if (line == null)
                {
                    continue;
                }

                var componentName = GetStringProp(line, "ComponentName", "Description", "Name", "RefName");
                var quantity = GetDecimalProp(line, "Quantity", "Qty");
                var unitPrice = GetDecimalProp(line, "UnitPrice", "Rate", "Price");
                var lineTotal = GetDecimalProp(line, "LineTotal", "TotalPrice", "Total");

                if (string.IsNullOrWhiteSpace(componentName) && quantity <= 0 && unitPrice <= 0 && lineTotal <= 0)
                {
                    continue;
                }

                yield return new BreakdownPayload
                {
                    ComponentName = componentName ?? string.Empty,
                    Quantity = quantity,
                    Unit = GetStringProp(line, "Unit") ?? string.Empty,
                    UnitPrice = unitPrice,
                    LineTotal = lineTotal,
                    RefKind = GetStringProp(line, "RefKind") ?? string.Empty,
                    RefSn = GetIntProp(line, "RefSn", "Sn"),
                    RefName = GetStringProp(line, "RefName") ?? componentName ?? string.Empty
                };
            }
        }

        internal static CustomRatePayload ToCustomRatePayload(CustomRate rate)
        {
            var materials = (rate.MaterialItems ?? new List<RateEntryItem>())
                .Select(item => new CustomRateLinePayload
                {
                    RateType = "material",
                    Description = item.Description ?? string.Empty,
                    Quantity = item.Quantity,
                    Unit = item.Unit ?? string.Empty,
                    UnitPrice = item.UnitPrice,
                    TotalCost = item.TotalCost
                })
                .ToList();

            var labour = (rate.LabourItems ?? new List<RateEntryItem>())
                .Select(item => new CustomRateLinePayload
                {
                    RateType = "labour",
                    Description = item.Description ?? string.Empty,
                    Quantity = item.Quantity,
                    Unit = item.Unit ?? string.Empty,
                    UnitPrice = item.UnitPrice,
                    TotalCost = item.TotalCost
                })
                .ToList();

            return new CustomRatePayload
            {
                CustomRateId = CustomRateServices.CloudKey(rate),
                SectionKey = rate.SectionKey ?? string.Empty,
                SectionLabel = rate.SectionLabel ?? string.Empty,
                Title = rate.Title ?? string.Empty,
                Description = rate.Description ?? string.Empty,
                Unit = !string.IsNullOrWhiteSpace(rate.CloudUnit) ? rate.CloudUnit!.Trim() : InferUnit(rate),
                Materials = materials,
                Labour = labour,
                Breakdown = BuildCustomBreakdown(materials, labour),
                NetCost = rate.OverallTotal,
                OverheadPercent = rate.OverheadPercent,
                ProfitPercent = rate.ProfitPercent,
                TotalCost = rate.GrandTotal,
                CreatedAt = rate.CreatedDate
            };
        }

        private static List<BreakdownPayload> BuildCustomBreakdown(
            IEnumerable<CustomRateLinePayload> materials,
            IEnumerable<CustomRateLinePayload> labour)
        {
            return (materials ?? Enumerable.Empty<CustomRateLinePayload>())
                .Concat(labour ?? Enumerable.Empty<CustomRateLinePayload>())
                .Select(line => new BreakdownPayload
                {
                    ComponentName = line.Description ?? string.Empty,
                    Quantity = line.Quantity,
                    Unit = line.Unit ?? string.Empty,
                    UnitPrice = line.UnitPrice,
                    LineTotal = line.TotalCost,
                    RefKind = line.RateType ?? string.Empty,
                    RefSn = line.RefSn,
                    RefName = !string.IsNullOrWhiteSpace(line.RefName) ? line.RefName : line.Description ?? string.Empty
                })
                .ToList();
        }

        private static string InferUnit(CustomRate rate)
        {
            var units = (rate.MaterialItems ?? new List<RateEntryItem>())
                .Concat(rate.LabourItems ?? new List<RateEntryItem>())
                .Select(item => item.Unit)
                .Where(unit => !string.IsNullOrWhiteSpace(unit))
                .Select(unit => unit!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return units.Count == 1 ? units[0] : string.Empty;
        }

        private static Dictionary<string, string> BuildMasterRateLookup()
        {
            return (RateLibraryStore.Items ?? Array.Empty<RateDefinition>())
                .Where(rate => !string.IsNullOrWhiteSpace(rate.Id))
                .GroupBy(rate => BuildRateIdentityKey(rate.SectionKey, rate.ItemNo, rate.Description, rate.Unit), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Id ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }

        private static string ResolveRateId(
            RateOverridePayload local,
            RateOverridePayload? server,
            IReadOnlyDictionary<string, string> masterRateIds)
        {
            if (!string.IsNullOrWhiteSpace(server?.RateId))
            {
                return server.RateId;
            }

            var key = BuildRateIdentityKey(local);
            if (masterRateIds.TryGetValue(key, out var masterId) && !string.IsNullOrWhiteSpace(masterId))
            {
                return masterId;
            }

            return BuildFallbackRateId(key);
        }

        private static string BuildFallbackRateId(string key)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
            var hex = Convert.ToHexString(bytes).ToLowerInvariant();
            return hex.Substring(0, 24);
        }

        private static bool AreEquivalent(RateOverridePayload local, RateOverridePayload server)
        {
            var localSignature = JsonSerializer.Serialize(new
            {
                key = BuildRateIdentityKey(local),
                sectionLabel = NormalizeText(local.SectionLabel),
                netCost = Round(local.NetCost),
                overheadPercent = Round(local.OverheadPercent),
                profitPercent = Round(local.ProfitPercent),
                overheadValue = Round(local.OverheadValue),
                profitValue = Round(local.ProfitValue),
                totalCost = Round(local.TotalCost),
                breakdown = NormalizeBreakdown(local.Breakdown)
            });

            var serverSignature = JsonSerializer.Serialize(new
            {
                key = BuildRateIdentityKey(server),
                sectionLabel = NormalizeText(server.SectionLabel),
                netCost = Round(server.NetCost),
                overheadPercent = Round(server.OverheadPercent),
                profitPercent = Round(server.ProfitPercent),
                overheadValue = Round(server.OverheadValue),
                profitValue = Round(server.ProfitValue),
                totalCost = Round(server.TotalCost),
                breakdown = NormalizeBreakdown(server.Breakdown)
            });

            return string.Equals(localSignature, serverSignature, StringComparison.Ordinal);
        }

        private static bool AreEquivalent(CustomRatePayload local, CustomRatePayload server) =>
            string.Equals(Signature(local), Signature(server), StringComparison.Ordinal);

        internal static string Signature(CustomRatePayload rate) =>
            JsonSerializer.Serialize(new
            {
                id = NormalizeText(rate.CustomRateId),
                sectionKey = NormalizeText(rate.SectionKey).ToLowerInvariant(),
                sectionLabel = NormalizeText(rate.SectionLabel),
                title = NormalizeText(rate.Title),
                description = NormalizeText(rate.Description),
                unit = NormalizeText(rate.Unit),
                netCost = Round(rate.NetCost),
                overheadPercent = Round(rate.OverheadPercent),
                profitPercent = Round(rate.ProfitPercent),
                totalCost = Round(rate.TotalCost),
                materials = NormalizeCustomLines(rate.Materials),
                labour = NormalizeCustomLines(rate.Labour),
                breakdown = NormalizeBreakdown(rate.Breakdown)
            });

        internal static string Signature(CustomRate rate) => Signature(ToCustomRatePayload(rate));

        private static IEnumerable<object> NormalizeBreakdown(IEnumerable<BreakdownPayload>? lines)
        {
            return (lines ?? Enumerable.Empty<BreakdownPayload>())
                .Select(line => new
                {
                    componentName = NormalizeText(line.ComponentName),
                    quantity = Round(line.Quantity),
                    unit = NormalizeText(line.Unit),
                    unitPrice = Round(line.UnitPrice),
                    lineTotal = Round(line.LineTotal),
                    refKind = NormalizeText(line.RefKind),
                    refSn = line.RefSn,
                    refName = NormalizeText(line.RefName)
                })
                .ToList();
        }

        private static IEnumerable<object> NormalizeCustomLines(IEnumerable<CustomRateLinePayload>? lines)
        {
            return (lines ?? Enumerable.Empty<CustomRateLinePayload>())
                .Select(line => new
                {
                    rateType = NormalizeText(line.RateType).ToLowerInvariant(),
                    description = NormalizeText(line.Description),
                    quantity = Round(line.Quantity),
                    unit = NormalizeText(line.Unit),
                    unitPrice = Round(line.UnitPrice),
                    totalCost = Round(line.TotalCost),
                    category = NormalizeText(line.Category),
                    refSn = line.RefSn,
                    refName = NormalizeText(line.RefName)
                })
                .ToList();
        }

        private static decimal Round(decimal value) =>
            Math.Round(value, 4, MidpointRounding.AwayFromZero);

        private static string BuildRateIdentityKey(RateOverridePayload rate) =>
            BuildRateIdentityKey(rate.SectionKey, rate.ItemNo, rate.Description, rate.Unit);

        private static string BuildRateIdentityKey(string? sectionKey, int? itemNo, string? description, string? unit)
        {
            return string.Join("|", new[]
            {
                NormalizeText(sectionKey).ToLowerInvariant(),
                itemNo?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                NormalizeText(description).ToLowerInvariant(),
                NormalizeText(unit).ToLowerInvariant()
            });
        }

        private static string NormalizeText(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace("\r", " ").Replace("\n", " ");

        private static string Trim(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength) + "...";
        }

        private static IEnumerable? FindRowsEnumerable(object vm)
        {
            if (vm == null)
            {
                return null;
            }

            foreach (var property in vm.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.PropertyType == typeof(string))
                {
                    continue;
                }

                if (!typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
                {
                    continue;
                }

                var value = property.GetValue(vm) as IEnumerable;
                if (value == null)
                {
                    continue;
                }

                object? first = null;
                foreach (var item in value)
                {
                    if (item != null)
                    {
                        first = item;
                        break;
                    }
                }

                if (first == null)
                {
                    continue;
                }

                var itemType = first.GetType();
                var hasItemNo = itemType.GetProperty("ItemNo") != null || itemType.GetProperty("SNo") != null;
                var hasDescription =
                    itemType.GetProperty("Description") != null ||
                    itemType.GetProperty("Name") != null ||
                    itemType.GetProperty("Title") != null;
                var hasTotal =
                    itemType.GetProperty("TotalCost") != null ||
                    itemType.GetProperty("TotalPrice") != null ||
                    itemType.GetProperty("Total") != null;

                if (hasItemNo && hasDescription && hasTotal)
                {
                    return value;
                }
            }

            return null;
        }

        private static IEnumerable? FindBreakdownEnumerable(object row)
        {
            foreach (var property in row.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.PropertyType == typeof(string))
                {
                    continue;
                }

                if (!typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
                {
                    continue;
                }

                if (!property.Name.Contains("Breakdown", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return property.GetValue(row) as IEnumerable;
            }

            return null;
        }

        private static string? GetStringProp(object source, params string[] names)
        {
            foreach (var name in names)
            {
                var property = source.GetType().GetProperty(name);
                if (property == null)
                {
                    continue;
                }

                var value = property.GetValue(source);
                if (value != null)
                {
                    return value.ToString();
                }
            }

            return null;
        }

        private static decimal GetDecimalProp(object source, params string[] names)
        {
            foreach (var name in names)
            {
                var property = source.GetType().GetProperty(name);
                if (property == null)
                {
                    continue;
                }

                var value = property.GetValue(source);
                if (value == null)
                {
                    continue;
                }

                try
                {
                    return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                }
                catch
                {
                    // ignore and keep checking
                }
            }

            return 0m;
        }

        private static int? GetIntProp(object source, params string[] names)
        {
            foreach (var name in names)
            {
                var property = source.GetType().GetProperty(name);
                if (property == null)
                {
                    continue;
                }

                var value = property.GetValue(source);
                if (value == null)
                {
                    continue;
                }

                try
                {
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }
                catch
                {
                    // ignore and keep checking
                }
            }

            return null;
        }

        private sealed class UserRateSnapshot
        {
            public UserRateSnapshot(List<RateOverridePayload> rateOverrides)
            {
                RateOverrides = rateOverrides;
            }

            public List<RateOverridePayload> RateOverrides { get; }
        }

        private sealed class ServerLibraryState
        {
            public ServerRatesMeta Meta { get; set; } = new ServerRatesMeta();
            public List<RateOverridePayload> RateOverrides { get; set; } = new List<RateOverridePayload>();
            public List<CustomRatePayload> CustomRates { get; set; } = new List<CustomRatePayload>();

            public int RatesVersion => Meta?.RatesVersion ?? 0;
            public int CustomRatesVersion => Meta?.CustomRatesVersion ?? 0;
        }

        private sealed class ServerRatesMeta
        {
            public int RatesVersion { get; set; }
            public int CustomRatesVersion { get; set; }
            public DateTime? UpdatedAt { get; set; }
        }

        private sealed class RateOverridePayload
        {
            public string RateId { get; set; } = string.Empty;
            public string SectionKey { get; set; } = string.Empty;
            public string SectionLabel { get; set; } = string.Empty;
            public int? ItemNo { get; set; }
            public string Description { get; set; } = string.Empty;
            public string Unit { get; set; } = string.Empty;
            public decimal NetCost { get; set; }
            public decimal OverheadPercent { get; set; }
            public decimal ProfitPercent { get; set; }
            public decimal OverheadValue { get; set; }
            public decimal ProfitValue { get; set; }
            public decimal TotalCost { get; set; }
            public List<BreakdownPayload> Breakdown { get; set; } = new List<BreakdownPayload>();
            public DateTime? ClientUpdatedAt { get; set; }
            public DateTime? SourceUpdatedAt { get; set; }
        }

        internal sealed class CustomRatePayload
        {
            public string CustomRateId { get; set; } = string.Empty;
            public string SectionKey { get; set; } = string.Empty;
            public string SectionLabel { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string Unit { get; set; } = string.Empty;
            public List<CustomRateLinePayload> Materials { get; set; } = new List<CustomRateLinePayload>();
            public List<CustomRateLinePayload> Labour { get; set; } = new List<CustomRateLinePayload>();
            public List<BreakdownPayload> Breakdown { get; set; } = new List<BreakdownPayload>();
            public decimal NetCost { get; set; }
            public decimal OverheadPercent { get; set; }
            public decimal ProfitPercent { get; set; }
            public decimal TotalCost { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? UpdatedAt { get; set; }
        }

        internal sealed class CustomRateLinePayload
        {
            public string RateType { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public decimal Quantity { get; set; }
            public string Unit { get; set; } = string.Empty;
            public decimal UnitPrice { get; set; }
            public decimal TotalCost { get; set; }
            public string Category { get; set; } = string.Empty;
            public int? RefSn { get; set; }
            public string RefName { get; set; } = string.Empty;
        }

        internal sealed class BreakdownPayload
        {
            public string ComponentName { get; set; } = string.Empty;
            public decimal Quantity { get; set; }
            public string Unit { get; set; } = string.Empty;
            public decimal UnitPrice { get; set; }
            public decimal LineTotal { get; set; }
            public string RefKind { get; set; } = string.Empty;
            public int? RefSn { get; set; }
            public string RefName { get; set; } = string.Empty;
        }

        private readonly struct SyncCounters
        {
            public SyncCounters(int upserted, int deleted)
            {
                Upserted = upserted;
                Deleted = deleted;
            }

            public int Upserted { get; }
            public int Deleted { get; }
        }
    }
}
