using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ADLMRateGen.Helpers;
using ADLMRateGen.ViewModel.Model;
using Newtonsoft.Json;

namespace ADLMRateGen.Services
{
    /// <summary>
    /// Which library rows are the user's own, and so theirs to delete.
    ///
    /// ADLM's defaults are the rows ADLM shipped: what the master library sent
    /// on the last sync (the SyncBaseline, keyed on name + unit like the rest
    /// of the sync), or, on an install that has never synced, the catalogue
    /// bundled with the app. Anything else in the library is there because the
    /// user put it there: from the Add sheet, by harvesting a saved rate, or on
    /// another PC through their ADLM account.
    ///
    /// This used to be decided from the cloud cache of the user's own rows,
    /// matched on serial number OR name, and it failed both ways. The cache is
    /// empty until a sign-in sync has run and stays empty offline, so a
    /// material the user had just added was refused as a "master item"; a row
    /// harvested from a saved rate is never in the cache at all; and the cloud
    /// compacts its serials to 1..n while the local master rows also start at
    /// 1, so a serial match could wave a default through. Deciding from what
    /// ADLM shipped answers the question actually being asked.
    /// </summary>
    public static class UserRowChecker
    {
        /// <summary>Tests swap the shipped sets for in-memory ones.</summary>
        public static Func<ISet<string>>? ShippedMaterialKeysOverride { get; set; }
        public static Func<ISet<string>>? ShippedLabourKeysOverride { get; set; }

        public static bool IsUserMaterial(MaterialModel? m) =>
            m != null && IsUserRow(m.MaterialName, m.MaterialUnit, ShippedMaterialKeys());

        public static bool IsUserLabour(LabourModel? l) =>
            l != null && IsUserRow(l.LabourName, l.LabourUnit, ShippedLabourKeys());

        /// <summary>
        /// The rule itself: a row ADLM never shipped is the user's. A row with
        /// no name is nobody's and is never deletable through this path.
        /// </summary>
        public static bool IsUserRow(string? name, string? unit, ISet<string> shippedKeys)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return !shippedKeys.Contains(Key(name, unit));
        }

        /// <summary>Name + unit, trimmed, so a stray space on either side cannot
        /// turn a default into "the user's". Compared case-insensitively.</summary>
        public static string Key(string? name, string? unit) =>
            SyncBaseline.Key((name ?? "").Trim(), (unit ?? "").Trim());

        public static ISet<string> NewKeySet() => new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static ISet<string> ShippedMaterialKeys()
        {
            if (ShippedMaterialKeysOverride != null) return ShippedMaterialKeysOverride();
            // Once there has been a sync, what the server sent is the truth.
            return SyncBaseline.MaterialKeys() ?? BundledMaterialKeys.Value;
        }

        private static ISet<string> ShippedLabourKeys()
        {
            if (ShippedLabourKeysOverride != null) return ShippedLabourKeysOverride();
            return SyncBaseline.LabourKeys() ?? BundledLabourKeys.Value;
        }

        // The bundled catalogue never changes while the app runs, so it is read once.
        private static readonly Lazy<ISet<string>> BundledMaterialKeys = new(() =>
            LoadBundled<MaterialModel>(new[] { "materials.json", "defaultMaterials.json", "defaultMaterial.json" },
                                       m => Key(m.MaterialName, m.MaterialUnit)));

        private static readonly Lazy<ISet<string>> BundledLabourKeys = new(() =>
            LoadBundled<LabourModel>(new[] { "labour.json", "defaultLabours.json", "defaultLabour.json" },
                                     l => Key(l.LabourName, l.LabourUnit)));

        private static ISet<string> LoadBundled<T>(string[] fileNames, Func<T, string> keyOf)
        {
            var set = NewKeySet();
            try
            {
                var path = AppPaths.FindFirstNonEmptyDefault(fileNames);
                if (path == null) return set;   // nothing shipped: every row is the user's
                var rows = JsonConvert.DeserializeObject<List<T>>(File.ReadAllText(path)) ?? new List<T>();
                foreach (var r in rows) set.Add(keyOf(r));
            }
            catch
            {
                // Unreadable catalogue: same as none shipped.
            }
            return set;
        }
    }
}
