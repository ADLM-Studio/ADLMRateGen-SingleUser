using ADLMRateGen.ADLM.Auth;
using ADLMRateGen.ViewModel.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ADLMRateGen.Services
{
    public sealed class UserLibrarySync
    {
        private static readonly Lazy<UserLibrarySync> _lazy = new(() => new UserLibrarySync());
        public static UserLibrarySync Instance => _lazy.Value;

        // ✅ FIX 3: provide a shared user data folder for downloaded catalogs & cached files
        public static string UserDataFolder
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ADLMRateGen"
                );
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string CacheFile => Path.Combine(UserDataFolder, "user-library.json");

        private readonly object _gate = new();
        private int _version = 0;
        private readonly List<RateRow> _myMaterials = new();
        private readonly List<RateRow> _myLabour = new();

        // See UserLibraryCache: a change made here that the account has not got yet.
        private bool _pendingPush;
        private string _owner = "";

        private sealed class RateItemDto
        {
            public int sn { get; set; }
            public string description { get; set; } = "";
            public string unit { get; set; } = "";
            public decimal price { get; set; }
            public string? category { get; set; }

            /// <summary>The estimator's own specification for a rate they added.</summary>
            public string? note { get; set; }
        }

        private record RateRow(int sn, string description, string unit, decimal price, string? category, string? note = null);

        private sealed class LibraryPayload
        {
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public int? baseVersion { get; set; }
            public RateItemDto[] materials { get; set; } = Array.Empty<RateItemDto>();
            public RateItemDto[] labour { get; set; } = Array.Empty<RateItemDto>();
        }

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private UserLibrarySync()
        {
            // Start from what was known last time, pending changes included, so
            // an offline session's adds and deletes are not forgotten at restart.
            var cache = UserLibraryCache.Load(CacheFile);
            if (cache == null) return;
            lock (_gate)
            {
                _owner = cache.owner ?? "";
                _version = cache.version;
                _pendingPush = cache.pendingPush;
                _myMaterials.AddRange(cache.materials.Select(r => new RateRow(r.sn, r.description, r.unit, r.price, r.category, r.note)));
                _myLabour.AddRange(cache.labour.Select(r => new RateRow(r.sn, r.description, r.unit, r.price, r.category, r.note)));
            }
        }

        /// <summary>A change on this PC has not reached the account yet.</summary>
        public bool HasPendingChanges { get { lock (_gate) return _pendingPush; } }

        private void SaveCache()
        {
            UserLibraryCache snap;
            lock (_gate)
            {
                snap = new UserLibraryCache
                {
                    owner = _owner,
                    version = _version,
                    pendingPush = _pendingPush,
                    materials = _myMaterials.Select(r => new UserLibraryCache.Row { sn = r.sn, description = r.description, unit = r.unit, price = r.price, category = r.category, note = r.note }).ToList(),
                    labour = _myLabour.Select(r => new UserLibraryCache.Row { sn = r.sn, description = r.description, unit = r.unit, price = r.price, category = r.category, note = r.note }).ToList(),
                };
            }
            snap.Save(CacheFile);
        }

        private void MarkPending()
        {
            lock (_gate) _pendingPush = true;
            SaveCache();
        }

        /// <summary>
        /// Make the cache the signed-in account's. One recorded for a different
        /// account is dropped, pending or not: its rows must never be pushed into
        /// this account's library.
        /// </summary>
        private void AdoptOwner(AuthClient auth)
        {
            string? me = null;
            try { me = Helpers.JwtHelper.GetUserId(auth.AccessToken); } catch { }
            if (string.IsNullOrEmpty(me)) return;

            bool dropped = false;
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(_owner) && !string.Equals(_owner, me, StringComparison.Ordinal))
                {
                    _myMaterials.Clear();
                    _myLabour.Clear();
                    _version = 0;
                    _pendingPush = false;
                    dropped = true;
                }
                _owner = me;
            }
            if (dropped) SaveCache();
        }

        public IReadOnlyList<(int sn, string description, string unit, decimal price, string? category)> MyMaterials
        {
            get { lock (_gate) return _myMaterials.Select(r => (r.sn, r.description, r.unit, r.price, r.category)).ToList(); }
        }

        public IReadOnlyList<(int sn, string description, string unit, decimal price, string? category, string? note)> MyLabour
        {
            get { lock (_gate) return _myLabour.Select(r => (r.sn, r.description, r.unit, r.price, r.category, r.note)).ToList(); }
        }

        public async Task LoadAsync()
        {
            try
            {
                var auth = AuthProvider.Instance.Client;
                if (!auth.HasSession) return;

                AdoptOwner(auth);

                // Anything changed here while the account was out of reach goes up
                // before the account's copy is read, or the read would overwrite
                // it and the change would be lost. Still unreachable: keep ours.
                if (HasPendingChanges)
                {
                    await PushAsync();
                    if (HasPendingChanges) return;
                }

                using var doc = await auth.GetJsonAsync("/rategen/library");
                var root = doc.RootElement;

                int ver = root.TryGetProperty("version", out var v) && v.TryGetInt32(out var vv) ? vv : 1;

                var mats = new List<RateRow>();
                if (root.TryGetProperty("materials", out var mArr) && mArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in mArr.EnumerateArray())
                    {
                        mats.Add(new RateRow(
                            el.GetProperty("sn").GetInt32(),
                            el.GetProperty("description").GetString() ?? "",
                            el.GetProperty("unit").GetString() ?? "",
                            el.GetProperty("price").GetDecimal(),
                            el.TryGetProperty("category", out var c) ? (c.GetString() ?? "") : null
                        ));
                    }
                }

                var labs = new List<RateRow>();
                if (root.TryGetProperty("labour", out var lArr) && lArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in lArr.EnumerateArray())
                    {
                        labs.Add(new RateRow(
                            el.GetProperty("sn").GetInt32(),
                            el.GetProperty("description").GetString() ?? "",
                            el.GetProperty("unit").GetString() ?? "",
                            el.GetProperty("price").GetDecimal(),
                            el.TryGetProperty("category", out var c) ? (c.GetString() ?? "") : null,
                            el.TryGetProperty("note", out var n) ? n.GetString() : null
                        ));
                    }
                }

                lock (_gate)
                {
                    _version = ver;
                    _myMaterials.Clear(); _myMaterials.AddRange(mats);
                    _myLabour.Clear(); _myLabour.AddRange(labs);
                    _pendingPush = false;
                }
                SaveCache();
            }
            catch
            {
                // best-effort
            }
        }

        /// <summary>
        /// Rows harvested from a saved rate, in one push. These used to be added
        /// to the library on this PC only, so the next sign-in sync, which rebuilds
        /// the library from the master plus the account's rows, dropped them.
        /// </summary>
        public async Task TryAddAsync(IEnumerable<MaterialModel>? materials, IEnumerable<LabourModel>? labours)
        {
            var any = false;
            lock (_gate)
            {
                foreach (var m in materials ?? Enumerable.Empty<MaterialModel>())
                {
                    _myMaterials.RemoveAll(r => r.sn == m.SerialNumber);
                    _myMaterials.Add(new RateRow(m.SerialNumber, m.MaterialName ?? "", m.MaterialUnit ?? "", m.MaterialPrice,
                        string.IsNullOrWhiteSpace(m.MaterialCategory) ? null : m.MaterialCategory));
                    any = true;
                }
                foreach (var l in labours ?? Enumerable.Empty<LabourModel>())
                {
                    _myLabour.RemoveAll(r => r.sn == l.SerialNumber);
                    _myLabour.Add(new RateRow(l.SerialNumber, l.LabourName ?? "", l.LabourUnit ?? "", l.LabourPrice,
                        string.IsNullOrWhiteSpace(l.LabourCategory) ? null : l.LabourCategory,
                        string.IsNullOrWhiteSpace(l.LabourNote) ? null : l.LabourNote));
                    any = true;
                }
            }
            if (any) await PushAsync();
        }

        public async Task TryAddMaterialAsync(MaterialModel m)
        {
            lock (_gate)
            {
                _myMaterials.RemoveAll(r => r.sn == m.SerialNumber);
                _myMaterials.Add(new RateRow(
                    m.SerialNumber,
                    m.MaterialName ?? "",
                    m.MaterialUnit ?? "",
                    m.MaterialPrice,
                    string.IsNullOrWhiteSpace(m.MaterialCategory) ? null : m.MaterialCategory
                ));
            }
            await PushAsync();
        }

        public async Task TryAddLabourAsync(LabourModel l)
        {
            lock (_gate)
            {
                _myLabour.RemoveAll(r => r.sn == l.SerialNumber);
                _myLabour.Add(new RateRow(
                    l.SerialNumber,
                    l.LabourName ?? "",
                    l.LabourUnit ?? "",
                    l.LabourPrice,
                    string.IsNullOrWhiteSpace(l.LabourCategory) ? null : l.LabourCategory,
                    string.IsNullOrWhiteSpace(l.LabourNote) ? null : l.LabourNote
                ));
            }
            await PushAsync();
        }

        public async Task TryUpdateMaterialAsync(MaterialModel m)
        {
            lock (_gate)
            {
                var existing = _myMaterials.FirstOrDefault(x =>
                    x.sn == m.SerialNumber ||
                    string.Equals(x.description, m.MaterialName, StringComparison.OrdinalIgnoreCase));

                var desc = m.MaterialName ?? existing?.description ?? "";
                var unit = m.MaterialUnit ?? existing?.unit ?? "";
                var cat = !string.IsNullOrWhiteSpace(m.MaterialCategory) ? m.MaterialCategory : existing?.category;

                _myMaterials.RemoveAll(x => x.sn == m.SerialNumber);
                _myMaterials.Add(new RateRow(m.SerialNumber, desc, unit, m.MaterialPrice, cat));
            }
            await PushAsync();
        }

        public async Task TryUpdateLabourAsync(LabourModel l)
        {
            lock (_gate)
            {
                var existing = _myLabour.FirstOrDefault(x =>
                    x.sn == l.SerialNumber ||
                    string.Equals(x.description, l.LabourName, StringComparison.OrdinalIgnoreCase));

                var desc = l.LabourName ?? existing?.description ?? "";
                var unit = l.LabourUnit ?? existing?.unit ?? "";
                var cat = !string.IsNullOrWhiteSpace(l.LabourCategory) ? l.LabourCategory : existing?.category;
                var note = !string.IsNullOrWhiteSpace(l.LabourNote) ? l.LabourNote : existing?.note;

                _myLabour.RemoveAll(x => x.sn == l.SerialNumber);
                _myLabour.Add(new RateRow(l.SerialNumber, desc, unit, l.LabourPrice, cat, note));
            }
            await PushAsync();
        }

        private async Task PushAsync()
        {
            try
            {
                var auth = AuthProvider.Instance.Client;
                if (!auth.HasSession) { MarkPending(); return; }

                AdoptOwner(auth);

                int ver;
                List<RateRow> mats;
                List<RateRow> labs;

                lock (_gate)
                {
                    ver = _version;
                    mats = _myMaterials.OrderBy(r => r.sn).ToList();
                    labs = _myLabour.OrderBy(r => r.sn).ToList();
                }

                var payload = new LibraryPayload
                {
                    baseVersion = ver > 0 ? ver : (int?)null,
                    materials = mats.Select(r => new RateItemDto { sn = r.sn, description = r.description, unit = r.unit, price = r.price, category = r.category }).ToArray(),
                    labour = labs.Select(r => new RateItemDto { sn = r.sn, description = r.description, unit = r.unit, price = r.price, category = r.category, note = r.note }).ToArray()
                };

                using var doc = await auth.PutJsonAsync("/rategen/library", payload);
                var root = doc.RootElement;
                var newVer = root.TryGetProperty("version", out var v) && v.TryGetInt32(out var vv) ? vv : ver;
                lock (_gate) { _version = newVer; _pendingPush = false; }
                SaveCache();
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("409"))
            {
                try
                {
                    var auth = AuthProvider.Instance.Client;
                    using var cur = await auth.GetJsonAsync("/rategen/library");
                    var root = cur.RootElement;
                    var latest = root.TryGetProperty("version", out var v) && v.TryGetInt32(out var vv) ? vv : 1;
                    lock (_gate) _version = latest;
                }
                catch { }

                await PushAsync(); // one retry
            }
            catch
            {
                // The account could not be reached. The change stays here, marked
                // pending, and goes up before the next read of the account.
                MarkPending();
            }
        }

        public async Task DeleteMaterialAsync(int sn, string? name = null)
        {
            lock (_gate)
            {
                _myMaterials.RemoveAll(r => r.sn == sn ||
                    (!string.IsNullOrWhiteSpace(name) &&
                     r.description.Equals(name, StringComparison.OrdinalIgnoreCase)));

                CompactSerials(_myMaterials);
            }
            await PushAsync();
        }

        public async Task DeleteLabourAsync(int sn, string? name = null)
        {
            lock (_gate)
            {
                _myLabour.RemoveAll(r => r.sn == sn ||
                    (!string.IsNullOrWhiteSpace(name) &&
                     r.description.Equals(name, StringComparison.OrdinalIgnoreCase)));

                CompactSerials(_myLabour);
            }
            await PushAsync();
        }

        private static void CompactSerials(List<RateRow> rows)
        {
            int next = 1;
            foreach (var r in rows.OrderBy(r => r.sn).ThenBy(r => r.description, StringComparer.OrdinalIgnoreCase).ToList())
                rows[rows.IndexOf(r)] = r with { sn = next++ };
        }
    }
}
