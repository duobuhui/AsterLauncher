using AsterLauncher.Core;

namespace AsterLauncher.Core.Tests;

public sealed class GachaAccountSelectionTests
{
    [Theory]
    [InlineData(BuiltInGameIds.Endfield, true, false)]
    [InlineData(BuiltInGameIds.GenshinImpact, true, true)]
    [InlineData(BuiltInGameIds.HonkaiStarRail, true, true)]
    [InlineData(BuiltInGameIds.ZenlessZoneZero, true, true)]
    [InlineData(BuiltInGameIds.HonkaiImpact3rd, false, false)]
    [InlineData(BuiltInGameIds.Arknights, false, false)]
    [InlineData(BuiltInGameIds.PetitPlanet, false, false)]
    [InlineData("custom-game", false, false)]
    [InlineData(null, false, false)]
    public void RecordsAvailabilityIsExplicit(string? game, bool records, bool uigf)
    {
        Assert.Equal(records, GachaCapabilities.SupportsRecords(game));
        Assert.Equal(uigf, GachaCapabilities.SupportsUigf(game));
    }

    [Fact]
    public void SelectionSurvivesReorderingAndNewlyImportedAccountsWithoutChangingTheArchive()
    {
        var choices = new GachaAccountSelection();
        var original = Analysis(BuiltInGameIds.HonkaiStarRail, "100000001", "100000002");
        Assert.Equal("100000001", choices.Resolve(original));
        Assert.True(choices.Select(original, "100000002"));
        var refreshed = Analysis(BuiltInGameIds.HonkaiStarRail, "100000003", "100000002", "100000001");
        Assert.Equal("100000002", choices.Resolve(refreshed));
        Assert.Equal(3, refreshed.Accounts.Count);
        Assert.Equal("100000003", refreshed.Accounts[0].Uid);
    }

    [Fact]
    public void ReturningToGameRestoresItsOwnSelection()
    {
        var choices = new GachaAccountSelection();
        var starRail = Analysis(BuiltInGameIds.HonkaiStarRail, "1", "2");
        var genshin = Analysis(BuiltInGameIds.GenshinImpact, "1", "2");
        Assert.True(choices.Select(starRail, "2"));
        Assert.Equal("1", choices.Resolve(genshin));
        Assert.Equal("2", choices.Resolve(starRail));
        Assert.Equal("1", choices.Resolve(genshin));
    }

    [Fact]
    public void TransientEmptyResultsDoNotForgetTheRememberedAccount()
    {
        var choices = new GachaAccountSelection();
        var analysis = Analysis(BuiltInGameIds.ZenlessZoneZero, "1", "2");
        choices.Select(analysis, "2");
        Assert.Null(choices.Resolve(Analysis(BuiltInGameIds.ZenlessZoneZero)));
        Assert.Equal("2", choices.Resolve(analysis));
    }

    [Fact]
    public void RemovedAccountFallsBackToAnExistingAccountAndCanSelectNewAccount()
    {
        var choices = new GachaAccountSelection();
        var analysis = Analysis(BuiltInGameIds.HonkaiStarRail, "1", "2");
        choices.Select(analysis, "2");
        var changed = Analysis(BuiltInGameIds.HonkaiStarRail, "1", "3");
        Assert.Equal("1", choices.Resolve(changed));
        Assert.True(choices.Select(changed, "3"));
        Assert.Equal("3", choices.Resolve(changed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing")]
    public void InvalidSelectionDoesNotReplaceCurrentAccount(string? uid)
    {
        var choices = new GachaAccountSelection();
        var analysis = Analysis(BuiltInGameIds.GenshinImpact, "1", "2");
        choices.Select(analysis, "2");
        Assert.False(choices.Select(analysis, uid));
        Assert.Equal("2", choices.Resolve(analysis));
    }

    [Fact]
    public void EndfieldCannotBePretendedToHaveUigfAccountIdentity()
    {
        var choices = new GachaAccountSelection();
        var analysis = Analysis(BuiltInGameIds.Endfield, "invented");
        Assert.False(choices.Select(analysis, "invented"));
        Assert.Null(choices.Resolve(analysis));
    }

    [Fact]
    public void DifferentUidsRemainDistinctEvenIfTheirMaskedLabelsCollide()
    {
        var choices = new GachaAccountSelection();
        var analysis = Analysis(BuiltInGameIds.HonkaiStarRail, "123111789", "123222789");
        Assert.Equal(analysis.Accounts[0].AccountLabel, analysis.Accounts[1].AccountLabel);
        choices.Select(analysis, "123222789");
        Assert.Equal("123222789", choices.Resolve(analysis));
    }

    private static UigfGachaAnalysis Analysis(string game, params string[] uids) =>
        new(game, "五星", [], uids.Select(uid => new UigfAccountAnalysis(uid, 1, [])).ToArray());
}