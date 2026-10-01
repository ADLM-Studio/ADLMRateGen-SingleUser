using System;
using System.Linq;
using ADLMRateGen.Services;

namespace ADLMRateGen.Helpers
{
    /// <summary>
    /// Which building service a cloud rate belongs to.
    ///
    /// The AI service published its building-services build-ups (socket and
    /// lighting points, earthing, fans, split units, fire alarm and hydrant,
    /// pipework, valves, sanitary fittings, tank and pump) into the cloud's
    /// "carbon" section, so RateGen showed them under Carbon &amp; Others. They are
    /// Mechanical, Electrical, Plumbing and Fire rates. Each is routed by the
    /// library category of its materials ("MEP - Plumbing - ...", "MEP - Fire -
    /// ..."), which is how the library itself files them; the item's own name is
    /// the fallback only when no material is found in the library.
    /// </summary>
    public static class ServiceRouting
    {
        public const string Mechanical = "Mechanical";
        public const string Electrical = "Electrical";
        public const string Plumbing = "Plumbing";
        public const string Fire = "Fire";

        /// <summary>The service, or null when the item is not a building-services rate.</summary>
        public static string? DisciplineOf(ComputeItemDefinition def, Func<string, string?> categoryOfMaterial)
        {
            if (def == null) return null;

            foreach (var line in def.lines.Where(l => string.Equals(l.kind, "material", StringComparison.OrdinalIgnoreCase)))
            {
                var name = line.refName ?? line.description;
                var d = FromCategory(string.IsNullOrWhiteSpace(name) ? null : categoryOfMaterial(name!));
                if (d != null) return d;
            }
            return FromName(def.name);
        }

        public static string? FromCategory(string? category)
        {
            if (string.IsNullOrWhiteSpace(category) || !category.StartsWith("MEP", StringComparison.OrdinalIgnoreCase))
                return null;
            if (category.IndexOf("Mechanical", StringComparison.OrdinalIgnoreCase) >= 0) return Mechanical;
            if (category.IndexOf("Plumbing", StringComparison.OrdinalIgnoreCase) >= 0) return Plumbing;
            if (category.IndexOf("Fire", StringComparison.OrdinalIgnoreCase) >= 0) return Fire;
            if (category.IndexOf("Electrical", StringComparison.OrdinalIgnoreCase) >= 0) return Electrical;
            return null;
        }

        private static readonly (string Word, string Discipline)[] Words =
        {
            ("fire", Fire), ("smoke", Fire), ("heat detector", Fire), ("extinguisher", Fire), ("hydrant", Fire), ("sprinkler", Fire),
            ("air condition", Mechanical), ("extractor", Mechanical), ("ceiling fan", Mechanical), ("duct", Mechanical), ("ventilat", Mechanical), ("hvac", Mechanical),
            ("ppr", Plumbing), ("upvc", Plumbing), ("valve", Plumbing), ("water", Plumbing), ("closet", Plumbing), ("basin", Plumbing),
            ("shower", Plumbing), ("sink", Plumbing), ("rainwater", Plumbing), ("soil", Plumbing), ("drain", Plumbing),
            ("socket", Electrical), ("lighting", Electrical), ("earth", Electrical), ("cable", Electrical), ("conduit", Electrical), ("switch", Electrical),
        };

        public static string? FromName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var (word, d) in Words)
                if (name!.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return d;
            return null;
        }
    }
}
