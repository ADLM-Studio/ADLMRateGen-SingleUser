using System;
using System.Collections.Generic;
using System.Linq;
using ADLMRateGen.ViewModel.CustomRate;
using ADLMRateGen.ViewModel.Model;

namespace ADLMRateGen.Services
{
    /// <summary>
    /// Keeps the material / labour price libraries in step with rate build-ups.
    ///
    ///  - <see cref="CleanName"/> strips the provenance tags RateGen appends to a
    ///    line description ("[AI]", "(plant)") so it can be matched against, or
    ///    written back to, the library under its real name.
    ///  - <see cref="Harvest"/> folds priced lines that are not yet in the
    ///    library back into it, so anything typed once is reusable next time.
    /// </summary>
    public static class RateLineLibrary
    {
        /// <summary>Category stamped on entries harvested out of a custom rate.</summary>
        public const string HarvestedCategory = "Custom Rate";

        private static readonly string[] Tags = { "[AI]", "(plant)" };

        /// <summary>
        /// Returns the library-facing name for a rate line, i.e. the description
        /// with any trailing provenance tags removed.
        /// </summary>
        public static string CleanName(string? description)
        {
            var name = (description ?? string.Empty).Trim();

            bool stripped;
            do
            {
                stripped = false;
                foreach (var tag in Tags)
                {
                    if (name.EndsWith(tag, StringComparison.OrdinalIgnoreCase))
                    {
                        name = name.Substring(0, name.Length - tag.Length).TrimEnd();
                        stripped = true;
                    }
                }
            } while (stripped && name.Length > 0);

            return name;
        }

        /// <summary>
        /// The comparison key for a library name: what is left after case,
        /// spacing and stylistic punctuation are taken out.
        ///
        /// Library lookups used to be exact string equality, so a component the
        /// AI named "Cement (Portland 42.5 R)" did not match the library's
        /// "Cement (Portland 42.5R)". The line kept the AI's price, and on save
        /// Harvest wrote it as a NEW row — leaving the user with two cements,
        /// then three, one per spelling the model happened to produce. The same
        /// class of near-miss cost this product two material lookups in v2.8.0,
        /// where a single missing space before a bracket priced a component at
        /// zero.
        ///
        /// Deliberately conservative about what it removes. Brackets, commas,
        /// apostrophes, quotes and hyphens vary by who typed the row and never
        /// distinguish two real items. Decimal points, slashes and colons are
        /// KEPT, because they carry size and mix: dropping the point would make
        /// 1.2mm roofing sheet and 12mm sheet the same key, and merging those
        /// would misprice a rate rather than merely tidy the library.
        /// </summary>
        public static string NormaliseKey(string? description)
        {
            var name = CleanName(description);
            if (name.Length == 0) return string.Empty;

            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var ch in name)
            {
                if (char.IsWhiteSpace(ch)) continue;
                if (ch is '(' or ')' or '[' or ']' or ',' or '\'' or '"' or '-' or '_') continue;
                sb.Append(char.ToLowerInvariant(ch));
            }

            return sb.ToString();
        }

        /// <summary>True when two library names refer to the same item.</summary>
        public static bool SameItem(string? a, string? b)
        {
            var keyA = NormaliseKey(a);
            return keyA.Length > 0 && keyA == NormaliseKey(b);
        }

        /// <summary>
        /// Adds every priced line that is not already in the library.
        /// Entries that already exist are left untouched — a one-off rate must
        /// never silently rewrite the master price list.
        /// </summary>
        /// <returns>How many new library entries were created.</returns>
        public static int Harvest(
            IEnumerable<RateEntryItem>? materialItems,
            IEnumerable<RateEntryItem>? labourItems)
            => Harvest(materialItems, labourItems, out _, out _);

        /// <summary>
        /// As above, and hands back the rows it created, so the caller can put
        /// them in the user's ADLM library too. The library on this PC is rebuilt
        /// from the master plus the account's rows at every sign-in sync, so a
        /// harvested row that only ever existed here was gone by the next day.
        /// </summary>
        public static int Harvest(
            IEnumerable<RateEntryItem>? materialItems,
            IEnumerable<RateEntryItem>? labourItems,
            out List<MaterialModel> addedMaterials,
            out List<LabourModel> addedLabours)
        {
            addedMaterials = CollectMaterials(materialItems);
            addedLabours = CollectLabours(labourItems);

            if (addedMaterials.Count > 0)
                MaterialLibraryService.AddOrUpdateMaterials(addedMaterials);

            if (addedLabours.Count > 0)
                LabourLibraryService.AddOrUpdateLabours(addedLabours);

            return addedMaterials.Count + addedLabours.Count;
        }

        private static List<MaterialModel> CollectMaterials(IEnumerable<RateEntryItem>? items)
        {
            var added = new List<MaterialModel>();
            if (items == null) return added;

            var nextSerial = NextSerial(MaterialLibraryService.GetAllMaterials().Select(m => m.SerialNumber));

            foreach (var item in items)
            {
                var name = CleanName(item?.Description);
                if (name.Length == 0 || item!.UnitPrice <= 0m) continue;
                if (MaterialLibraryService.FindByName(name) != null) continue;
                if (added.Any(m => SameItem(m.MaterialName, name))) continue;

                added.Add(new MaterialModel
                {
                    SerialNumber = nextSerial++,
                    MaterialName = name,
                    MaterialUnit = item.Unit ?? string.Empty,
                    MaterialPrice = item.UnitPrice,
                    MaterialCategory = HarvestedCategory
                });
            }

            return added;
        }

        private static List<LabourModel> CollectLabours(IEnumerable<RateEntryItem>? items)
        {
            var added = new List<LabourModel>();
            if (items == null) return added;

            var nextSerial = NextSerial(LabourLibraryService.GetAllLabours().Select(l => l.SerialNumber));

            foreach (var item in items)
            {
                var name = CleanName(item?.Description);
                if (name.Length == 0 || item!.UnitPrice <= 0m) continue;
                if (LabourLibraryService.FindByName(name) != null) continue;
                if (added.Any(l => SameItem(l.LabourName, name))) continue;

                added.Add(new LabourModel
                {
                    SerialNumber = nextSerial++,
                    LabourName = name,
                    LabourUnit = item.Unit ?? string.Empty,
                    LabourPrice = item.UnitPrice,
                    LabourCategory = HarvestedCategory
                });
            }

            return added;
        }

        private static int NextSerial(IEnumerable<int> existing)
        {
            var max = 0;
            foreach (var n in existing)
                if (n > max) max = n;
            return max + 1;
        }
    }
}
