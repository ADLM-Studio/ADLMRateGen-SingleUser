using System.Collections.Generic;
using ADLMRateGen.Helpers;
using ADLMRateGen.Services;
using Xunit;

namespace ADLMRateGen.Tests
{
    /// <summary>
    /// The cloud publishes building-services rates into its "carbon" section;
    /// RateGen files each under its service by the library category of its
    /// materials, and falls back to the item's name only when no material is
    /// in the library.
    /// </summary>
    public class ServiceRoutingTests
    {
        private static ComputeItemDefinition Def(string name, params string[] materials)
        {
            var d = new ComputeItemDefinition { name = name, section = "carbon" };
            foreach (var m in materials) d.lines.Add(new ComputeLine { kind = "material", refName = m, description = m });
            d.lines.Add(new ComputeLine { kind = "labour", refName = "Plumber (skilled)", description = "Plumber (skilled)" });
            return d;
        }

        private static readonly Dictionary<string, string> Categories = new()
        {
            ["PPR pressure pipe, PN10 to BS EN ISO 15874, 15mm"] = "MEP - Plumbing - Water Pipework (supply)",
            ["Smoke/heat detector, complete with base and connection"] = "MEP - Fire - Protection & Alarm (installed)",
            ["Ceiling fan complete with regulator"] = "MEP - Mechanical - Air Conditioning & Ventilation (installed)",
            ["Point wiring, socket/power point, concealed PVC conduit"] = "MEP - Electrical - Point Wiring (installed)",
            ["Cement (50kg bag)"] = "Cement and aggregates",
        };

        private static string? Cat(string n) => Categories.TryGetValue(n, out var c) ? c : null;

        [Theory]
        [InlineData("PPR pressure pipe PN10, 15mm", "PPR pressure pipe, PN10 to BS EN ISO 15874, 15mm", "Plumbing")]
        [InlineData("Smoke or heat detector", "Smoke/heat detector, complete with base and connection", "Fire")]
        [InlineData("Ceiling fan", "Ceiling fan complete with regulator", "Mechanical")]
        [InlineData("13A switched socket outlet point", "Point wiring, socket/power point, concealed PVC conduit", "Electrical")]
        public void Files_by_the_library_category_of_its_materials(string name, string material, string expected)
            => Assert.Equal(expected, ServiceRouting.DisciplineOf(Def(name, material), Cat));

        [Fact]
        public void Category_beats_the_name()
            // a "water" word in the name, but the material is a fire item
            => Assert.Equal("Fire", ServiceRouting.DisciplineOf(Def("Water mist detector", "Smoke/heat detector, complete with base and connection"), Cat));

        [Fact]
        public void Falls_back_to_the_name_when_no_material_is_in_the_library()
            => Assert.Equal("Plumbing", ServiceRouting.DisciplineOf(Def("Wash hand basin with tap", "Unlisted basin"), Cat));

        [Fact]
        public void A_non_services_item_stays_where_it_is()
            => Assert.Null(ServiceRouting.DisciplineOf(Def("Embodied carbon assessment for concrete", "Cement (50kg bag)"), Cat));
    }
}
