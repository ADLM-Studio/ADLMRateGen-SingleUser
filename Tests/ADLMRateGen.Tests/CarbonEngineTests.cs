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
            Assert.Equal(275, c.Kg, 6);   // NIS 87:2007 size and webs at 1,920 kg/m3
            Assert.True(c.Factor.MassAssumed);
        }

        // ---- services: copper conductors and plastic pipe, weighed from the name ----------

        [Theory]
        [InlineData("4 core 35mm2 PVC/SWA/PVC copper cable", 4 * 35e-6 * 8890)]
        [InlineData("1 core 16mm2 PVC/PVC copper cable", 16e-6 * 8890)]
        [InlineData("35mm2 bare copper earth conductor for ground grid", 35e-6 * 8890)]
        [InlineData("8mm copper round wire, earthing", 3.141592653589793 / 4 * 64e-6 * 8890)]
        public void A_cable_counts_its_copper_conductor(string name, double kgPerMetre)
        {
            var c = CarbonEngine.Assess("", name, "m", 10)!;
            Assert.Equal("copper-conductor", c.Factor.Id);
            Assert.Equal(10 * kgPerMetre, c.Kg, 6);
            Assert.Equal(10 * kgPerMetre * 2.71, c.A13, 6);
        }

        [Theory]
        [InlineData("PPR pressure pipe, PN10 to BS EN ISO 15874, 32mm", "ppr-pipe", 32, 2.9, 900)]
        [InlineData("PPR pipe 25mm incl. 5% waste", "ppr-pipe", 25, 2.3, 900)]
        [InlineData("PPR pipe 15mm incl. 5% waste", "ppr-pipe", 20, 1.9, 900)]
        [InlineData("uPVC soil, waste and vent pipe to BS 4514, 100mm", "upvc-soil-pipe", 110, 3.2, 1400)]
        public void A_pipe_is_weighed_from_its_standard_size(string name, string id, double od, double wall, double density)
        {
            var c = CarbonEngine.Assess("", name, "m", 1)!;
            Assert.Equal(id, c.Factor.Id);
            Assert.Equal(System.Math.PI * (od - wall) * wall * 1e-6 * density, c.Kg, 6);
        }

        [Theory]
        [InlineData("uPVC rigid rainwater downpipe, 75mm")]   // size not verified: left out
        [InlineData("PPR equal tee, 25mm")]                     // a fitting, not pipe
        [InlineData("Water closet (WC) suite, complete with cistern, seat and connections")]
        [InlineData("Split unit air conditioner, 1HP, complete with pipework and brackets")]
        public void Equipment_and_unverified_sizes_carry_no_carbon(string name)
            => Assert.Null(CarbonEngine.Assess("", name, "m", 1));

        [Fact]
        public void Cement_carries_a_Nigerian_low_end()
        {
            var c = CarbonEngine.Assess("Cement Based Products", "Cement (50kg bag)", "Bag", 1)!;
            Assert.Equal(50 * 0.83, c.A13, 6);
            Assert.True(c.TotalLow < c.Total);
            Assert.Equal(50 * (0.57 - 0.83) * (1 + c.Factor.Wf), c.TotalLow - c.Total, 6);
        }

        [Fact]
        public void A_tile_takes_its_thickness_from_the_build_up_line()
        {
            var c = CarbonEngine.Assess("Finishes - Ceramic floor tiles", "Ceramic floor tiles", "m2", 1, "600 x 600 x 10mm vitrified floor tiles")!;
            Assert.Equal(20, c.Kg, 6);    // 0.010 m x 2,000 kg/m3
        }

        [Fact]
        public void Paint_in_litres_per_square_metre_is_weighed()
        {
            var c = CarbonEngine.Assess("", "Emulsion paint", "Lit/m2", 2, "Emulsion paint")!;
            Assert.Equal(2.6, c.Kg, 6);   // 2 litres x 1.3 kg/l
        }

        [Fact]
        public void Aluminium_sheet_mass_comes_from_its_thickness()
        {
            var c = CarbonEngine.Assess("Longspan Aluminium Roofing Sheet", "0.55mm (24SWG) sheet, Stucco mill", "m2", 1)!;
            Assert.Equal(0.55 / 1000 * 2700, c.Kg, 6);
            Assert.Equal(c.Kg * 13.0, c.A13, 6);                       // IStructE 2020, worldwide consumption
        }

        [Fact]
        public void Reinforcement_is_per_tonne_at_the_scrap_EAF_factor()
        {
            var c = CarbonEngine.Assess("High Tensile Steel Bar Reinforcement", "1/2\" diameter (93 pieces) - 12mm diameter.", "Tonne", 1)!;
            Assert.Equal(1000 * 0.821, c.A13, 6);   // CARES EPD 0060, scrap-based EAF
        }

        [Theory]
        [InlineData("MEP - Electrical - Luminaires (installed)", "LED ceiling fitting, 1 x 12W", "No.")]
        [InlineData("AMERON PAINTS - Thinners", "Amercoat 12", "1 Litre")]
        [InlineData("Cement Based Products", "Loading and unloading cement", "Bag")]
        public void No_factor_means_no_figure(string category, string name, string unit)
            => Assert.Null(CarbonEngine.Assess(category, name, unit, 1));
    }
}
