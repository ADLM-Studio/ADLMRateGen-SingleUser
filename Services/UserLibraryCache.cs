using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ADLMRateGen.Services
{
    /// <summary>
    /// What <see cref="UserLibrarySync"/> keeps on disk between runs: the user's
    /// own material and labour rows as last known, the library version they were
    /// read at, and whether a change made here is still waiting to reach the
    /// account.
    ///
    /// Without this the user's rows lived in memory only. A row added, harvested
    /// or deleted while offline was pushed "best effort", which meant not at all,
    /// and the next sign-in sync rebuilt the library from the account's copy: the
    /// delete came back and the add was gone. Now the change is kept here,
    /// marked pending, and pushed before the next sync reads the account.
    ///
    /// The file names its owner so a second ADLM account signing in on the same
    /// PC never has the first account's pending rows pushed into its library.
    /// </summary>
    public sealed class UserLibraryCache
    {
        public sealed class Row
        {
            public int sn { get; set; }
            public string description { get; set; } = "";
            public string unit { get; set; } = "";
            public decimal price { get; set; }
            public string? category { get; set; }
            public string? note { get; set; }
        }

        /// <summary>The account's user id (JWT subject). Empty when unknown.</summary>
        public string owner { get; set; } = "";
        public int version { get; set; }
        /// <summary>A change made on this PC has not reached the account yet.</summary>
        public bool pendingPush { get; set; }
        public List<Row> materials { get; set; } = new();
        public List<Row> labour { get; set; } = new();

        private static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public string Serialize() => JsonSerializer.Serialize(this, Opts);

        /// <summary>Null for an empty, missing or unreadable file: start clean.</summary>
        public static UserLibraryCache? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<UserLibraryCache>(json); }
            catch { return null; }
        }

        /// <summary>
        /// Whether this cache may be used for the account now signed in. A cache
        /// with no owner recorded is adopted; one recorded for someone else is not.
        /// </summary>
        public bool BelongsTo(string? ownerId) =>
            string.IsNullOrEmpty(owner) ||
            string.Equals(owner, ownerId ?? "", StringComparison.Ordinal);

        public static UserLibraryCache? Load(string path)
        {
            try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
            catch { return null; }
        }

        public void Save(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Helpers.AppPaths.AtomicWrite(path, Serialize());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UserLibraryCache] save failed: {ex.Message}");
            }
        }
    }
}
