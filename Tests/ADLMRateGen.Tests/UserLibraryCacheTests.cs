using ADLMRateGen.Services;
using Xunit;

namespace ADLMRateGen.Tests;

/// <summary>
/// The user's own library rows now survive a restart and an offline session:
/// an add or delete the account has not received yet is kept on disk, marked
/// pending, and pushed before the next read of the account. These pin the file
/// and the ownership rule that stops one account's rows reaching another's.
/// </summary>
public class UserLibraryCacheTests
{
    [Fact]
    public void RoundTrips_RowsVersionAndPendingFlag()
    {
        var cache = new UserLibraryCache
        {
            owner = "user-1",
            version = 7,
            pendingPush = true,
            materials = { new UserLibraryCache.Row { sn = 3, description = "Bamboo pole", unit = "length", price = 1_200m, category = "Custom Rate" } },
            labour = { new UserLibraryCache.Row { sn = 1, description = "Tiler (own gang)", unit = "day", price = 15_000m, note = "Two men" } },
        };

        var back = UserLibraryCache.Parse(cache.Serialize());

        Assert.NotNull(back);
        Assert.Equal("user-1", back!.owner);
        Assert.Equal(7, back.version);
        Assert.True(back.pendingPush);
        var m = Assert.Single(back.materials);
        Assert.Equal(("Bamboo pole", "length", 1_200m, "Custom Rate"), (m.description, m.unit, m.price, m.category));
        var l = Assert.Single(back.labour);
        Assert.Equal(("Tiler (own gang)", "day", 15_000m, "Two men"), (l.description, l.unit, l.price, l.note));
    }

    [Fact]
    public void AnEmptyOrBrokenFile_StartsClean()
    {
        Assert.Null(UserLibraryCache.Parse(null));
        Assert.Null(UserLibraryCache.Parse("   "));
        Assert.Null(UserLibraryCache.Parse("{ not json"));
    }

    [Fact]
    public void AnotherAccountsCache_IsNeverAdopted()
    {
        var theirs = new UserLibraryCache { owner = "user-1", pendingPush = true };

        Assert.True(theirs.BelongsTo("user-1"));
        Assert.False(theirs.BelongsTo("user-2"));
        Assert.False(theirs.BelongsTo(null));
    }

    [Fact]
    public void ACacheWithNoOwnerRecorded_IsAdoptedByWhoeverSignsIn()
    {
        var unowned = new UserLibraryCache();

        Assert.True(unowned.BelongsTo("user-2"));
    }
}
