using ADLMRateGen.Services;
using Xunit;

namespace ADLMRateGen.Tests
{
    /// <summary>
    /// Upfront carbon (RICS A1-A5) as IStructE (2020) calculates it, from the
    /// factors in Data/carbonFactors.json. These pin the arithmetic and the
    /// published values the figures on screen rest on.
    /// </summary>
    public class CarbonEngineTests
    {
        [Fact]
        public void A_bag_of_cement_is_50kg_at_the_published_factor()
        {
            var c = CarbonEngine.Assess("Cement Based Products", "Cement (50kg bag)", "Bag", 1)!;
            Assert.Equal(50, c.Kg, 6);
            Assert.Equal(50 * 0.83, c.A13, 6);                         // CIDB 2021, cement (commonly used)
            Assert.Equal(50 * 0.032, c.A4, 6);                         // national, 300 km by road
            Assert.Equal(50 * 0.053 * (0.83 + 0.032 + 0.005 + 0.013), c.A5w, 6); // A5w = WF x (A1-A3 + A4 + C2 + C3-C4)
        }

        [Fact]
        public void Diesel_is_site_energy_per_litre_burnt()
        {
            var c = CarbonEngine.Assess("Fuels", "Diesel", "Litre", 304)!;
            Assert.Equal(304 * 2.66155, c.A5a, 6);                     // UK Government 2024, 100% mineral diesel
            Assert.Equal(0, c.A13);
            Assert.Equal(c.A5a, c.Total, 6);
        }

        [Fact]
        public void A_225_hollow_block_uses_its_stated_assumed_mass()
        {
            var c = CarbonEngine.Assess("Cement Based Products", "225 x 225 x 450mm (9 x 9 x 18\") Hollow blocks", "No.", 10)!;
            Assert.Equal(238, c.Kg, 6);
            Assert.True(c.Factor.MassAssumed);
        }

        [Fact]
        public void Aluminium_sheet_mass_comes_from_its_thickness()
        {
            var c = CarbonEngine.Assess("Longspan Aluminium Roofing Sheet", "0.55mm (24SWG) sheet, Stucco mill", "m2", 1)!;
            Assert.Equal(0.55 / 1000 * 2700, c.Kg, 6);
            Assert.Equal(c.Kg * 13.0, c.A13, 6);                       // IStructE 2020, worldwide consumption
        }

        [Fact]
        public void Reinforcement_is_per_tonne_at_the_worldwide_factor()
        {
            var c = CarbonEngine.Assess("High Tensile Steel Bar Reinforcement", "1/2\" diameter (93 pieces) - 12mm diameter.", "Tonne", 1)!;
            Assert.Equal(1000 * 1.99, c.A13, 6);
        }

        [Theory]
        [InlineData("MEP - Electrical - Luminaires (installed)", "LED ceiling fitting, 1 x 12W", "No.")]
        [InlineData("AMERON PAINTS - Thinners", "Amercoat 12", "1 Litre")]
        [InlineData("Cement Based Products", "Loading and unloading cement", "Bag")]
        public void No_factor_means_no_figure(string category, string name, string unit)
            => Assert.Null(CarbonEngine.Assess(category, name, unit, 1));
    }
}
