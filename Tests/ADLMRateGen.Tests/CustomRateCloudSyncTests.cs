using ADLMRateGen.Services;
using ADLMRateGen.ViewModel.CustomRate;
using Xunit;
using Payload = ADLMRateGen.Services.UserRatesCloudSync.CustomRatePayload;
using Line = ADLMRateGen.Services.UserRatesCloudSync.CustomRateLinePayload;

namespace ADLMRateGen.Tests;

/// <summary>
/// Rate Gen 2.9.x never downloaded custom rates and deleted every cloud custom
/// rate missing from its own Saved Rates, so a rate built on the website or on
/// another PC was erased by the next desktop sync. The sync now pulls first and
/// deletes only what the user deleted here. These pin the plan it makes; no
/// network and no files.
/// </summary>
[Collection(TestCollections.LibraryState)]
public class CustomRateCloudSyncTests
{
    private const string WebId = "tiling-600x600-k3x9";
    private static readonly DateTime CloudTime = new(2026, 10, 3, 9, 30, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyDictionary<string, string> NoArchive =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> NoDeletions() => new(StringComparer.OrdinalIgnoreCase);

    private static Payload WebRate(string id = WebId, decimal tilePrice = 9_000m, DateTime? updatedAt = null) => new()
    {
        CustomRateId = id,
        SectionKey = "finishes",
        SectionLabel = "Finishes",
        Title = "600x600 floor tiling",
        Description = "600x600 floor tiling",
        Unit = "m2",
        Materials = { new Line { RateType = "material", Description = "Floor tile 600x600", Quantity = 1.05m, Unit = "m2", UnitPrice = tilePrice, TotalCost = 1.05m * tilePrice } },
        Labour = { new Line { RateType = "labour", Description = "Tiler", Quantity = 1, Unit = "m2", UnitPrice = 1_500m, TotalCost = 1_500m } },
        OverheadPercent = 10,
        ProfitPercent = 15,
        CreatedAt = CloudTime,
        UpdatedAt = updatedAt ?? CloudTime
    };

    private static CustomRate DesktopRate(string title = "Blockwork 225")
    {
        var item = new RateEntryItem { RateType = RateItemType.Material };
        item.Description = "Block 225";
        item.Quantity = 10;
        item.UnitPrice = 650m;
        return new CustomRate { Title = title, Description = title, MaterialItems = { item }, OverheadPercent = 10, ProfitPercent = 10 };
    }

    [Fact]
    public void ARateMadeOnTheWebsite_IsDownloaded_AndNothingIsDeleted()
    {
        Library.Load();
        var local = new List<CustomRate> { DesktopRate() };

        var plan = UserRatesCloudSync.PlanCustomRateSync(local, new[] { WebRate() }, NoDeletions(), NoArchive);

        Assert.Empty(plan.ToDelete);
        Assert.Equal(1, plan.Pulled);
        var pulled = Assert.Single(local, r => r.CloudId == WebId);
        Assert.Equal("600x600 floor tiling", pulled.Title);
        Assert.Equal("m2", pulled.CloudUnit);
        Assert.Equal("finishes", pulled.SectionKey);
        // The cloud's price, not one this PC's library would give.
        Assert.Equal(9_000m, pulled.MaterialItems.Single().UnitPrice);
        // Downloaded unchanged, so not sent back; the desktop's own new rate is.
        Assert.DoesNotContain(pulled, plan.ToPush);
        Assert.Single(plan.ToPush);
    }

    [Fact]
    public void AFreshInstall_DownloadsEverything_AndDeletesNothing()
    {
        Library.Load();
        var local = new List<CustomRate>();
        var otherPc = Guid.NewGuid().ToString();

        var plan = UserRatesCloudSync.PlanCustomRateSync(
            local, new[] { WebRate(), WebRate(otherPc) }, NoDeletions(), NoArchive);

        Assert.Equal(2, local.Count);
        Assert.Empty(plan.ToDelete);
        Assert.Empty(plan.ToPush);
        // A Guid id is kept as the rate's own Id, so it round-trips unchanged.
        Assert.Contains(local, r => r.Id.ToString() == otherPc && r.CloudId == null);
    }

    [Fact]
    public void ADownloadedRate_SendsBackItsCloudIdUnitAndSection()
    {
        Library.Load();
        var pulled = UserRatesCloudSync.FromCloud(WebRate());

        var payload = UserRatesCloudSync.ToCustomRatePayload(pulled);

        Assert.Equal(WebId, payload.CustomRateId);
        Assert.Equal("m2", payload.Unit);
        Assert.Equal("finishes", payload.SectionKey);
        Assert.Equal("Finishes", payload.SectionLabel);
    }

    [Fact]
    public void OnlyARateTheUserDeletedHere_IsDeletedInTheCloud()
    {
        Library.Load();
        var mine = DesktopRate();
        var deletions = NoDeletions();
        deletions.Add(mine.Id.ToString());
        deletions.Add("long-gone-rate");

        var plan = UserRatesCloudSync.PlanCustomRateSync(
            new List<CustomRate>(),
            new[] { UserRatesCloudSync.ToCustomRatePayload(mine), WebRate() },
            deletions,
            NoArchive);

        Assert.Equal(new[] { mine.Id.ToString() }, plan.ToDelete);
        Assert.Equal(new[] { "long-gone-rate" }, plan.StaleDeletions);
        Assert.Equal(1, plan.Pulled); // the website rate, not the deleted one
    }

    [Fact]
    public void AnEditMadeHere_IsUploaded()
    {
        Library.Load();
        var local = new List<CustomRate>();
        UserRatesCloudSync.PlanCustomRateSync(local, new[] { WebRate() }, NoDeletions(), NoArchive);
        local[0].OverheadPercent = 12;

        var plan = UserRatesCloudSync.PlanCustomRateSync(local, new[] { WebRate() }, NoDeletions(), NoArchive);

        Assert.Same(local[0], Assert.Single(plan.ToPush));
    }

    [Fact]
    public void AnEditMadeOnTheWebsite_ReplacesAnUntouchedCopyHere()
    {
        Library.Load();
        var local = new List<CustomRate>();
        UserRatesCloudSync.PlanCustomRateSync(local, new[] { WebRate() }, NoDeletions(), NoArchive);

        var edited = WebRate(tilePrice: 9_800m, updatedAt: CloudTime.AddHours(2));
        var plan = UserRatesCloudSync.PlanCustomRateSync(local, new[] { edited }, NoDeletions(), NoArchive);

        Assert.Equal(1, plan.Refreshed);
        Assert.Empty(plan.ToPush);
        Assert.Equal(9_800m, local[0].MaterialItems.Single().UnitPrice);
    }

    [Fact]
    public void ARateTheCloudLost_IsUploadedAgain_UnlessTheUserDeletedItOnAnotherDesktop()
    {
        Library.Load();
        var dropped = new List<CustomRate>();
        UserRatesCloudSync.PlanCustomRateSync(dropped, new[] { WebRate() }, NoDeletions(), NoArchive);
        var deleted = new List<CustomRate>();
        UserRatesCloudSync.PlanCustomRateSync(deleted, new[] { WebRate() }, NoDeletions(), NoArchive);

        // An old desktop's sync dropped it: put it back.
        var byOldDesktop = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [WebId] = "omitted-by-desktop-sync" };
        var plan = UserRatesCloudSync.PlanCustomRateSync(dropped, Array.Empty<Payload>(), NoDeletions(), byOldDesktop);
        Assert.Single(plan.ToPush);
        Assert.Single(dropped);

        // The user deleted it on another up-to-date desktop: follow.
        var byUser = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [WebId] = "deleted-by-client" };
        plan = UserRatesCloudSync.PlanCustomRateSync(deleted, Array.Empty<Payload>(), NoDeletions(), byUser);
        Assert.Empty(plan.ToPush);
        Assert.Empty(deleted);
        Assert.Equal(1, plan.RemovedLocally);
    }
}
