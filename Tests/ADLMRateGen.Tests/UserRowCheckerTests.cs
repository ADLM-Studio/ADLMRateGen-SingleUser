using ADLMRateGen.Services;
using ADLMRateGen.ViewModel.Model;
using Xunit;

namespace ADLMRateGen.Tests;

/// <summary>
/// A user can delete any material or labour item they added, and never one of
/// ADLM's defaults. "ADLM's defaults" are the rows ADLM shipped: what the master
/// library sent on the last sync, or the bundled catalogue before any sync.
/// These pin the rule; they never touch the real library files.
///
/// Before this rule the check ran against the cloud cache of the user's rows,
/// matched on serial number OR name. Offline, or before the first sync, the
/// cache was empty and every row the user added was refused as a master item;
/// and the cloud's serials restart at 1 like the master's, so a serial match
/// could wave a default through.
/// </summary>
[Collection(TestCollections.LibraryState)]
public class UserRowCheckerTests : IDisposable
{
    private static ISet<string> Shipped(params (string name, string unit)[] rows)
    {
        var set = UserRowChecker.NewKeySet();
        foreach (var r in rows) set.Add(UserRowChecker.Key(r.name, r.unit));
        return set;
    }

    private static readonly ISet<string> AdlmDefaults = Shipped(
        ("Cement (Portland 42.5R)", "bag"),
        ("Sharp sand", "tonne"),
        ("Granite 3/4 inch", "tonne"));

    public UserRowCheckerTests()
    {
        UserRowChecker.ShippedMaterialKeysOverride = () => AdlmDefaults;
        UserRowChecker.ShippedLabourKeysOverride = () => Shipped(("Mason", "day"), ("Labourer", "day"));
    }

    public void Dispose()
    {
        UserRowChecker.ShippedMaterialKeysOverride = null;
        UserRowChecker.ShippedLabourKeysOverride = null;
    }

    [Fact]
    public void AMaterialTheUserAdded_IsTheirsToDelete()
    {
        var mine = new MaterialModel { SerialNumber = 503, MaterialName = "Bamboo scaffold pole", MaterialUnit = "length" };

        Assert.True(UserRowChecker.IsUserMaterial(mine));
    }

    [Fact]
    public void OneOfAdlmsDefaults_IsNeverTheUsers()
    {
        var cement = new MaterialModel { SerialNumber = 503, MaterialName = "Cement (Portland 42.5R)", MaterialUnit = "bag" };

        // Same serial as the row above: the serial number says nothing about who
        // added a row, and the rule no longer looks at it.
        Assert.False(UserRowChecker.IsUserMaterial(cement));
    }

    [Fact]
    public void TheSameNameInAUnitAdlmDoesNotShip_IsTheUsers()
    {
        // The library deliberately holds a name under several units; the user's
        // per-kg copy of a per-tonne default is their own row.
        var perKg = new MaterialModel { MaterialName = "Sharp sand", MaterialUnit = "kg" };

        Assert.True(UserRowChecker.IsUserMaterial(perKg));
        Assert.False(UserRowChecker.IsUserMaterial(new MaterialModel { MaterialName = "Sharp sand", MaterialUnit = "tonne" }));
    }

    [Fact]
    public void CaseAndSurroundingSpaces_DoNotTurnADefaultIntoTheUsers()
    {
        var looksDifferent = new MaterialModel { MaterialName = "  granite 3/4 INCH ", MaterialUnit = "Tonne " };

        Assert.False(UserRowChecker.IsUserMaterial(looksDifferent));
    }

    [Fact]
    public void ARowWithNoName_IsNobodys()
    {
        Assert.False(UserRowChecker.IsUserMaterial(new MaterialModel { MaterialName = "  ", MaterialUnit = "bag" }));
        Assert.False(UserRowChecker.IsUserMaterial(null));
        Assert.False(UserRowChecker.IsUserRow(null, "bag", AdlmDefaults));
    }

    [Fact]
    public void Labour_FollowsTheSameRule()
    {
        Assert.True(UserRowChecker.IsUserLabour(new LabourModel { LabourName = "Tiler (own gang)", LabourUnit = "day" }));
        Assert.False(UserRowChecker.IsUserLabour(new LabourModel { LabourName = "Mason", LabourUnit = "day" }));
        Assert.True(UserRowChecker.IsUserLabour(new LabourModel { LabourName = "Mason", LabourUnit = "hr" }));
    }

    [Fact]
    public void WithNothingShipped_EveryNamedRowIsTheUsers()
    {
        // An install with no sync and no bundled catalogue was seeded empty, so
        // anything in it is the user's.
        var nothing = UserRowChecker.NewKeySet();

        Assert.True(UserRowChecker.IsUserRow("Anything", "unit", nothing));
        Assert.False(UserRowChecker.IsUserRow("", "unit", nothing));
    }
}
