using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ADLMRateGen.Helpers;           // << uses AppPaths.UserDataDir / AppPaths.CustomRatesFile
using ADLMRateGen.ViewModel.CustomRate;
using Newtonsoft.Json;

namespace ADLMRateGen.Services
{
    public static class CustomRateServices
    {
        // Events
        public static event Action<CustomRate>? OnCustomRateSaved;
        public static event Action<CustomRate>? OnCustomRateUpdated;
        /// <summary>The cloud sync rewrote the file (rates downloaded or refreshed). Anything holding the list should reload it.</summary>
        public static event Action? OnCustomRatesReplaced;

        // Thread-safety for file IO
        private static readonly object _sync = new object();

        // Centralized, roaming AppData path (e.g. %AppData%\ADLMRateGen\custom-rates.json)
        private static string FilePath => AppPaths.CustomRatesFile;

        public static IEnumerable<CustomRate> LoadCustomRates()
        {
            lock (_sync)
            {
                try
                {
                    if (!File.Exists(FilePath))
                        return new List<CustomRate>();

                    var json = File.ReadAllText(FilePath);
                    return JsonConvert.DeserializeObject<List<CustomRate>>(json) ?? new List<CustomRate>();
                }
                catch
                {
                    // If file is corrupted or unreadable, fail gracefully
                    return new List<CustomRate>();
                }
            }
        }

        public static void SaveCustomRate(CustomRate rate)
        {
            lock (_sync)
            {
                var rates = LoadCustomRates().ToList();
                rates.Add(rate);
                SaveRates(rates);
            }

            OnCustomRateSaved?.Invoke(rate);
        }

        /// <summary>
        /// Update an existing CustomRate by matching on Id.
        /// If not found, adds it. Then fires OnCustomRateUpdated.
        /// </summary>
        public static void UpdateCustomRate(CustomRate updatedRate)
        {
            lock (_sync)
            {
                var rates = LoadCustomRates().ToList();

                var idx = rates.FindIndex(r => r.Id == updatedRate.Id);
                if (idx >= 0)
                {
                    // Replace the whole object to ensure we persist all fields consistently
                    rates[idx] = updatedRate;
                }
                else
                {
                    rates.Add(updatedRate);
                }

                SaveRates(rates);
            }

            OnCustomRateUpdated?.Invoke(updatedRate);
        }

        /// <summary>Load, change and save the file under one lock, so a sync never writes back a stale list.</summary>
        public static void Mutate(Func<List<CustomRate>, bool> change, bool notify)
        {
            bool changed;
            lock (_sync)
            {
                var rates = LoadCustomRates().ToList();
                changed = change(rates);
                if (changed) SaveRates(rates);
            }

            if (changed && notify) OnCustomRatesReplaced?.Invoke();
        }

        /* ── deletions the user made here, still to be sent to the cloud ──
         *
         * The cloud sync no longer treats "missing from this PC" as "deleted":
         * that erased every rate made on the website or on another PC. A rate
         * is deleted in the cloud only when the user deleted it here, and this
         * file is that record.
         */

        private static string DeletionsPath => Path.Combine(AppPaths.UserDataDir, "custom-rates-deleted.json");

        public static HashSet<string> LoadDeletions()
        {
            lock (_sync)
            {
                try
                {
                    if (!File.Exists(DeletionsPath))
                        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var ids = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(DeletionsPath)) ?? new List<string>();
                    return new HashSet<string>(ids.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        public static void RecordDeletion(CustomRate rate)
        {
            if (rate == null) return;
            lock (_sync)
            {
                var ids = LoadDeletions();
                if (ids.Add(CloudKey(rate))) SaveDeletions(ids);
            }
        }

        public static void ClearDeletion(string cloudId)
        {
            lock (_sync)
            {
                var ids = LoadDeletions();
                if (ids.Remove(cloudId)) SaveDeletions(ids);
            }
        }

        private static void SaveDeletions(HashSet<string> ids)
        {
            var dir = Path.GetDirectoryName(DeletionsPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(DeletionsPath, JsonConvert.SerializeObject(ids.OrderBy(id => id).ToList(), Formatting.Indented));
        }

        /// <summary>The id this rate has in the cloud.</summary>
        public static string CloudKey(CustomRate rate) =>
            !string.IsNullOrWhiteSpace(rate.CloudId) ? rate.CloudId!.Trim() : rate.Id.ToString();

        public static void SaveRates(IEnumerable<CustomRate> rates)
        {
            lock (_sync)
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Atomic write: write to temp, then replace
                var json = JsonConvert.SerializeObject(rates, Formatting.Indented);
                var tmp = FilePath + ".tmp";

                File.WriteAllText(tmp, json);
                // Replace the existing file (if any) with the temp file atomically where possible
                if (File.Exists(FilePath))
                {
                    // Try a safe replace; fallback to delete+move if Replace isn't available on the platform
                    try
                    {
                        File.Replace(tmp, FilePath, null);
                    }
                    catch
                    {
                        File.Delete(FilePath);
                        File.Move(tmp, FilePath);
                    }
                }
                else
                {
                    File.Move(tmp, FilePath);
                }
            }
        }
    }
}
