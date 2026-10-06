using Xunit;
using ADLMRateGen.ViewModel.BlockWork;

namespace ADLMRateGen.Tests;

/// <summary>
/// The mortar arithmetic behind the wall and render rates, against the Sep 2026
/// practice consensus: build-up formulas mined from ~690 real Nigerian QS workbooks,
/// one vote per author (template lineage, firm, or unit-rate sheet). Ranges are the
/// consensus inter-quartile ranges, so a factor drifting back out of what practising
/// QSs order fails here.
///
/// These are pure arithmetic checks: no view model, no library, no disk.
/// </summary>
public class MortarCalibrationTests
{
    [Fact]
    public void A_dry_batch_yields_less_mortar_than_its_dry_volume()
    {
        // 1:6 measured dry is 7 m3 of material and about 5.2 m3 of mortar.
        Assert.Equal(5.19, MortarMix.WetYield(7), 2);
        Assert.True(MortarMix.WetYield(7) < 7, "mortar cannot yield its full dry volume");
        Assert.Equal(0, MortarMix.WetYield(0));
    }

    [Theory]
    // mix (sand parts), cement bags per m3 of mortar: practice runs 4.2 (School, 1:6)
    // to 8.1 (Lagos, 1:5), and each mix must land between its neighbours.
    [InlineData(3, 7.0, 10.5)]   // 1:3 render mix
    [InlineData(4, 6.0, 8.5)]    // 1:4 wall mix
    [InlineData(6, 4.2, 6.5)]    // 1:6 wall mix
    public void Cement_in_a_cubic_metre_of_mortar_is_in_the_practice_range(
        double sandParts, double low, double high)
    {
        double bags = MortarMix.CementBagsPerM3(sandParts);
        Assert.InRange(bags, low, high);
    }

    [Theory]
    // Wall, mix, and the cement per m2 practice orders.
    //
    // The 225 range is the corpus inter-quartile range. The 150 range is NOT: almost
    // every source in the corpus applies one mortar figure to every wall thickness,
    // so its 150 median (0.187) is really its 225 figure wearing a different label.
    // The one source that measures mortar from the bed and perpends - the Lagos
    // build-up rate - puts a 150 wall at 0.133 bags/m2, two thirds of its 225 wall.
    // That is the honest yardstick for a thinner wall.
    [InlineData(225, 6, 0.187, 0.212)]
    [InlineData(150, 6, 0.110, 0.160)]
    public void A_wall_buys_the_cement_practice_says_it_does(
        int thickness, double sandParts, double low, double high)
    {
        double perSqM = thickness switch
        {
            225 => MortarMix.PerSqM225,
            150 => MortarMix.PerSqM150,
            _ => MortarMix.PerSqM100,
        };
        double bags = MortarMix.WallCementBagsPerSqM(perSqM, sandParts);
        Assert.InRange(bags, low, high);
    }

    [Fact]
    public void A_thinner_wall_uses_less_mortar_than_a_thicker_one()
    {
        Assert.True(MortarMix.PerSqM225 > MortarMix.PerSqM150);
        Assert.True(MortarMix.PerSqM150 > MortarMix.PerSqM100);
    }

    [Fact]
    public void Mortar_volumes_sit_between_the_sources_that_measure_them()
    {
        // Lagos build-up rate measures 0.0253 m3/m2 for a 225 wall; the School
        // unit-rate estimate 0.038. Anything outside that is not measured practice.
        Assert.InRange(MortarMix.PerSqM225, 0.0253, 0.038);
        Assert.InRange(MortarMix.PerSqM150, 0.0165, 0.025);
    }

    [Fact]
    public void Render_carries_the_cement_a_twelve_millimetre_coat_needs()
    {
        // 1:3 render. Practice: 0.13 to 0.24 bags per m2 (median 0.15).
        double bags = MortarMix.WallCementBagsPerSqM(MortarMix.RenderPerSqM, 3);
        Assert.InRange(bags, 0.1298, 0.2364);
    }

    [Fact]
    public void The_old_figures_would_now_fail_these_checks()
    {
        // What the rates carried before: a 0.013 m3/m2 bed and a batch divided by its
        // dry volume, i.e. 0.0535 bags/m2 - about a quarter of what a QS orders.
        double old = 0.013 * (MortarMix.BagsPerCubicMetreOfCement / 7);
        Assert.True(old < 0.187, $"the old 225 figure was {old:0.0000} bags/m2");
    }
}
