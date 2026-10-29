using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ADLMRateGen.Helpers;
using ADLMRateGen.ViewModel.Model;

namespace ADLMRateGen.Services
{
    /// <summary>
    /// What the library looked like the last time the cloud wrote it.
    ///
    /// Without this there is no way to tell a price the user typed from a price
    /// the server sent, so the sync could only ever overwrite everything. The
    /// baseline is the difference between "your edit" and "the old server value",
    /// and it is what lets the sync preserve one and refresh the other.
    ///
    /// CatalogMigration already does this for the INSTALLER path: its
    /// "<file>.baseline" is the shipped catalog an install was last seeded from,
    /// and it preserves edits against that on upgrade. A separate file is needed
    /// here because the two baselines answer different questions. The cloud sends
    /// state-specific prices that never touch the shipped catalog, so ".baseline"
    /// cannot say what the server last sent, and reusing it would mistake every
    /// zone price difference for a user edit.
    /// </summary>
    public static class SyncBaseline
    {
        private static string MaterialsFile => Path.Combine(AppPaths.UserDataDir, "materials.synced.json");
        private static string LabourFile => Path.Combine(AppPaths.UserDataDir, "labour.synced.json");

        private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

        /// <summary>
        /// Take the server's prices before anything modifies them.
        ///
        /// The caller overwrites master rows in place to carry user edits forward,
        /// so by the time it saves, the list no longer holds what the server sent.
        /// Writing the baseline from that mutated list would record the user's own
        /// price as the server's, the edit would look like agreement on the next
        /// sync, and it would be overwritten on the one after. Snapshot first.
        /// </summary>
        public static List<(string name, string unit, decimal price)> Snapshot(IEnumerable<MaterialModel> materials)
            => (materials ?? Enumerable.Empty<MaterialModel>())
               .Select(m => (m.MaterialName, m.MaterialUnit, m.MaterialPrice)).ToList();

        public static List<(string name, string unit, decimal price)> Snapshot(IEnumerable<LabourModel> labour)
            => (labour ?? Enumerable.Empty<LabourModel>())
               .Select(l => (l.LabourName, l.LabourUnit, l.LabourPrice)).ToList();

        /// <summary>Record the prices the SERVER sent. Materials and labour arrive
        /// in separate calls, so they are written separately.</summary>
        public static void WriteMaterials(List<(string name, string unit, decimal price)> snapshot)
            => WriteSnapshot(MaterialsFile, snapshot, "material");

        public static void WriteLabour(List<(string name, string unit, decimal price)> snapshot)
            => WriteSnapshot(LabourFile, snapshot, "labour");

        private static void WriteSnapshot(string path, List<(string name, string unit, decimal price)> snap, string what)
        {
            if (snap == null) return;
            try
            {
                AppPaths.AtomicWrite(path, JsonSerializer.Serialize(
                    snap.Select(r => new Row { N = r.name, U = r.unit, P = r.price }), Opts));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SyncBaseline] {what} write failed: {ex.Message}");
            }
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(MaterialsFile)) File.Delete(MaterialsFile);
                if (File.Exists(LabourFile)) File.Delete(LabourFile);
            }
            catch { }
        }

        public static bool Exists => File.Exists(MaterialsFile) || File.Exists(LabourFile);

        /// <summary>
        /// The rows the server sent last time, as name|unit keys, or null when
        /// that library has never been synced. This is what "ADLM's defaults"
        /// means to <see cref="UserRowChecker"/>: a row that is not in here, and
        /// not in the bundled catalogue either, is one the user added.
        /// </summary>
        public static ISet<string>? MaterialKeys() => Keys(MaterialsFile);
        public static ISet<string>? LabourKeys() => Keys(LabourFile);

        private static ISet<string>? Keys(string path)
        {
            if (!File.Exists(path)) return null;
            var set = UserRowChecker.NewKeySet();
            try
            {
                var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(path)) ?? new List<Row>();
                foreach (var r in rows) set.Add(UserRowChecker.Key(r.N, r.U));
            }
            catch { }
            return set;
        }

        private sealed class Row
        {
            public string N { get; set; } = "";
            public string U { get; set; } = "";
            public decimal P { get; set; }
        }

        private static Dictionary<string, decimal> Load(string path)
        {
            var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path)) return map;
                var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(path)) ?? new List<Row>();
                foreach (var r in rows) map[Key(r.N, r.U)] = r.P;
            }
            catch { }
            return map;
        }

        // Name plus unit, because the library deliberately holds rows that share a
        // name and differ by unit: structural steel is carried both per tonne and
        // per kg, and reinforcement grades share diameters.
        public static string Key(string name, string unit) => $"{name}|{unit}";

        public sealed class EditedRow
        {
            public string Name { get; set; } = "";
            public string Unit { get; set; } = "";
            public decimal YourPrice { get; set; }
            public decimal BaselinePrice { get; set; }
            public decimal ServerPrice { get; set; }
            public bool IsLabour { get; set; }

            /// <summary>
            /// The server is now proposing something different from what it sent
            /// last time. Only these are worth interrupting the user for.
            /// </summary>
            public bool ServerChanged => ServerPrice != BaselinePrice;

            public string RowKey => Key(Name, Unit);

            public string Describe() =>
                $"{Name} ({Unit}):  yours {YourPrice:N0}  ->  new {ServerPrice:N0}";
        }

        /// <summary>
        /// Every row whose local price no longer matches what the cloud last wrote.
        /// These are the user's own edits, and a sync would destroy all of them.
        ///
        /// The caller preserves the lot by default and only prompts for the subset
        /// with ServerChanged set. The split matters: if the server is sending the
        /// same price it sent last time, nothing new has happened and asking again
        /// on every launch would just train the user to click through the dialog.
        /// A price the server has actually moved is a real conflict and is worth a
        /// question.
        ///
        /// Returns empty when no baseline exists. That is deliberate: with nothing
        /// to compare against every row looks edited, and the user would be shown a
        /// list of hundreds that means nothing.
        /// </summary>
        public static List<EditedRow> FindEdits(
            IEnumerable<MaterialModel> localMaterials, IEnumerable<MaterialModel> incomingMaterials,
            IEnumerable<LabourModel> localLabour, IEnumerable<LabourModel> incomingLabour)
        {
            var edits = new List<EditedRow>();
            if (!Exists) return edits;

            var baseMat = Load(MaterialsFile);
            var baseLab = Load(LabourFile);

            var incMat = (incomingMaterials ?? Enumerable.Empty<MaterialModel>())
                .GroupBy(m => Key(m.MaterialName, m.MaterialUnit), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().MaterialPrice, StringComparer.OrdinalIgnoreCase);

            // One edit per key. A library can hold two rows with the same name and
            // unit — a duplicate import, or a row the user re-added by hand — and
            // emitting an edit for each produced a list with repeated RowKeys.
            // Every consumer builds a dictionary off RowKey, so that threw
            // "An item with the same key has already been added" and took the whole
            // sync down. The incoming side was already grouped; the local side was
            // not. Match it, keeping the first differing row as the user's intent.
            var seenMat = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in localMaterials ?? Enumerable.Empty<MaterialModel>())
            {
                var k = Key(m.MaterialName, m.MaterialUnit);
                if (!baseMat.TryGetValue(k, out var wasSynced)) continue;   // a row the user added, not a master row
                if (m.MaterialPrice == wasSynced) continue;                 // untouched
                if (!incMat.TryGetValue(k, out var incoming)) continue;     // cloud no longer sends it, nothing to clash with
                if (!seenMat.Add(k)) continue;                              // duplicate row, already recorded

                edits.Add(new EditedRow
                {
                    Name = m.MaterialName, Unit = m.MaterialUnit,
                    YourPrice = m.MaterialPrice, BaselinePrice = wasSynced,
                    ServerPrice = incoming, IsLabour = false
                });
            }

            var incLab = (incomingLabour ?? Enumerable.Empty<LabourModel>())
                .GroupBy(l => Key(l.LabourName, l.LabourUnit), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().LabourPrice, StringComparer.OrdinalIgnoreCase);

            var seenLab = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var l in localLabour ?? Enumerable.Empty<LabourModel>())
            {
                var k = Key(l.LabourName, l.LabourUnit);
                if (!baseLab.TryGetValue(k, out var wasSynced)) continue;
                if (l.LabourPrice == wasSynced) continue;
                if (!incLab.TryGetValue(k, out var incoming)) continue;
                if (!seenLab.Add(k)) continue;                              // duplicate row, already recorded

                edits.Add(new EditedRow
                {
                    Name = l.LabourName, Unit = l.LabourUnit,
                    YourPrice = l.LabourPrice, BaselinePrice = wasSynced,
                    ServerPrice = incoming, IsLabour = true
                });
            }

            return edits;
        }
    }
}
