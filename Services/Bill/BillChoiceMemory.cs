using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ADLMRateGen.Helpers;
using Newtonsoft.Json;

namespace ADLMRateGen.Services.Bill
{
    /// <summary>One bill line the user priced, and the rate they gave it.</summary>
    public sealed class BillChoice
    {
        /// <summary>The line as read (section › headings › its own words), as written in the bill.</summary>
        public string Text { get; set; } = "";
        public string UnitKey { get; set; } = "";
        /// <summary>The chosen rate, by what stays the same between runs (rate ids do not).</summary>
        public string Trade { get; set; } = "";
        public string RateName { get; set; } = "";
        public string RateUnitKey { get; set; } = "";
        public int Uses { get; set; } = 1;
        public DateTime ChosenUtc { get; set; }
    }

    /// <summary>What the memory proposes for one bill line.</summary>
    public sealed class BillRecall
    {
        public BillRate Rate { get; init; } = new();
        public double Confidence { get; init; }
        public string Reason { get; init; } = "";
        /// <summary>The same line, word for word: no need to ask ADLM AI about it.</summary>
        public bool Exact { get; init; }
    }

    /// <summary>
    /// Price a bill learns from the user: each rate they accept or choose for a line is
    /// remembered against the line's words, and the next bill proposes that rate first
    /// for the same line (word for word) or a similar one (same unit, mostly the same
    /// words). The latest choice for a line wins. Kept on this PC, one file per account,
    /// in %AppData%\ADLMRateGen; ADLM Cloud has no place for it yet.
    /// </summary>
    public sealed class BillChoiceMemory
    {
        /// <summary>How much of the wording two lines must share to count as similar.</summary>
        public const double SimilarEnough = 0.6;
        public const double ExactConfidence = 0.99;
        /// <summary>Oldest choices are dropped past this many, so the file stays small.</summary>
        public const int MaxChoices = 5000;

        private readonly string? _path;
        private readonly Dictionary<string, BillChoice> _choices = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public BillChoiceMemory(string? path)
        {
            _path = path;
            Load();
        }

        /// <summary>The signed-in account's memory, or one for this PC when the account is unknown.</summary>
        public static BillChoiceMemory ForCurrentUser()
        {
            string? id = null;
            try { id = JwtHelper.GetUserId(AuthProvider.Instance.Client.AccessToken); } catch { }
            var safe = new string((id ?? "").Where(char.IsLetterOrDigit).ToArray());
            var name = safe.Length == 0 ? "bill-choices.json" : "bill-choices-" + safe + ".json";
            return new BillChoiceMemory(Path.Combine(AppPaths.UserDataDir, name));
        }

        public int Count { get { lock (_gate) return _choices.Count; } }

        /// <summary>Remembers that <paramref name="text"/> in <paramref name="unitKey"/> is priced at <paramref name="rate"/>.</summary>
        public void Remember(string text, string unitKey, BillRate rate, DateTime? whenUtc = null)
        {
            var key = KeyOf(text, unitKey);
            if (key.Length == 0 || rate == null || string.IsNullOrWhiteSpace(rate.Name)) return;
            lock (_gate)
            {
                var same = _choices.TryGetValue(key, out var had)
                           && had.Trade == rate.Trade && had.RateName == rate.Name && had.RateUnitKey == rate.UnitKey;
                _choices[key] = new BillChoice
                {
                    Text = (text ?? "").Trim(),
                    UnitKey = unitKey ?? "",
                    Trade = rate.Trade,
                    RateName = rate.Name,
                    RateUnitKey = rate.UnitKey,
                    Uses = same ? had!.Uses + 1 : 1,
                    ChosenUtc = whenUtc ?? DateTime.UtcNow,
                };
                if (_choices.Count > MaxChoices)
                    foreach (var old in _choices.OrderBy(kv => kv.Value.ChosenUtc).Take(_choices.Count - MaxChoices).Select(kv => kv.Key).ToList())
                        _choices.Remove(old);
            }
        }

        /// <summary>
        /// The rate the user chose before for this line, found in today's <paramref name="rates"/>
        /// (so its price is today's), or null. A word-for-word line is proposed at 99%; a
        /// similar one at 89-95% by how much wording it shares.
        /// </summary>
        public BillRecall? Recall(string text, string unitKey, IReadOnlyList<BillRate> rates)
        {
            var key = KeyOf(text, unitKey);
            if (key.Length == 0) return null;
            List<BillChoice> all;
            lock (_gate)
            {
                if (_choices.TryGetValue(key, out var exact))
                {
                    var r = Find(exact, rates);
                    if (r != null)
                        return new BillRecall { Rate = r, Confidence = ExactConfidence, Exact = true, Reason = "From your earlier choice for this same item." };
                }
                all = _choices.Values.Where(c => c.UnitKey == (unitKey ?? "")).ToList();
            }

            var words = Words(text);
            if (words.Count == 0) return null;
            var best = all
                .Select(c => (Choice: c, Score: Similarity(words, Words(c.Text))))
                .Where(x => x.Score >= SimilarEnough)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Choice.ChosenUtc)
                .Select(x => (x.Choice, x.Score, Rate: Find(x.Choice, rates)))
                .FirstOrDefault(x => x.Rate != null);
            if (best.Rate == null) return null;
            return new BillRecall
            {
                Rate = best.Rate,
                Confidence = Math.Round(0.80 + 0.15 * best.Score, 2),
                Reason = "From your earlier choice for a similar item: \"" + Clip(LastPart(best.Choice.Text), 90) + "\".",
            };
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(_path)) return;
            string json;
            lock (_gate) json = JsonConvert.SerializeObject(new Stored { Choices = _choices.Values.ToList() }, Formatting.Indented);
            try { AppPaths.AtomicWrite(_path, json); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BillChoiceMemory] Save failed: " + ex.Message); }
        }

        private void Load()
        {
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return;
            try
            {
                var stored = JsonConvert.DeserializeObject<Stored>(File.ReadAllText(_path));
                foreach (var c in stored?.Choices ?? new List<BillChoice>())
                {
                    var key = KeyOf(c.Text, c.UnitKey);
                    if (key.Length > 0 && !string.IsNullOrWhiteSpace(c.RateName)) _choices[key] = c;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BillChoiceMemory] Load failed: " + ex.Message); }
        }

        private sealed class Stored
        {
            public int Version { get; set; } = 1;
            public List<BillChoice> Choices { get; set; } = new();
        }

        // Exactly the chosen rate if it is still there; else the same name in the same unit
        // (a trade renamed, a cloud copy), so a choice survives small library changes.
        private static BillRate? Find(BillChoice c, IReadOnlyList<BillRate> rates) =>
            rates.FirstOrDefault(r => r.UnitKey == c.RateUnitKey && r.Trade == c.Trade && r.Name == c.RateName)
            ?? rates.FirstOrDefault(r => r.UnitKey == c.RateUnitKey && string.Equals(r.Name, c.RateName, StringComparison.OrdinalIgnoreCase));

        /// <summary>The words that make a line itself: case, spacing and punctuation do not.</summary>
        public static string KeyOf(string? text, string? unitKey)
        {
            var words = BillCandidatePicker.Tokens(text ?? "");
            return words.Count == 0 ? "" : (unitKey ?? "") + "|" + string.Join(" ", words);
        }

        private static HashSet<string> Words(string? text) => new(BillCandidatePicker.Tokens(text ?? ""), StringComparer.Ordinal);

        /// <summary>
        /// Shared words over all words (Jaccard), 0 to 1. The figures must be the same: a 225mm
        /// wall is not a 150mm wall, and a trench 1.5m deep is not one 3m deep.
        /// </summary>
        public static double Similarity(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            if (!a.Where(IsFigure).ToHashSet().SetEquals(b.Where(IsFigure))) return 0;
            var shared = a.Count(b.Contains);
            return shared / (double)(a.Count + b.Count - shared);
        }

        private static bool IsFigure(string token) => token.Length > 0 && char.IsDigit(token[0]);

        private static string LastPart(string text)
        {
            var i = text.LastIndexOf('›');
            return i < 0 ? text : text.Substring(i + 1).Trim();
        }

        private static string Clip(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }
}
