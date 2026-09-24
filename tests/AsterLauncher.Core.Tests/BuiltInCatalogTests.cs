using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class BuiltInCatalogTests
{
    [Fact]
    public void BuiltInCatalog_ContainsExpectedUniqueGames()
    {
        var adapters = BuiltInGameCatalog.CreateAdapters();
        var ids = adapters.Select(adapter => adapter.Definition.Id).ToArray();

        Assert.Equal(7, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(BuiltInGameIds.Endfield, ids);
        Assert.Contains(BuiltInGameIds.GenshinImpact, ids);
        Assert.Contains(BuiltInGameIds.HonkaiImpact3rd, ids);
        Assert.Contains(BuiltInGameIds.HonkaiStarRail, ids);
        Assert.Contains(BuiltInGameIds.ZenlessZoneZero, ids);
        Assert.Contains(BuiltInGameIds.PetitPlanet, ids);
        Assert.Contains(BuiltInGameIds.Arknights, ids);
    }

    [Fact]
    public void CompanionPresets_MapToRequestedGames()
    {
        var presets = CompanionToolPresetFactory.CreateDefaults();

        Assert.Contains(presets, tool => tool.GameId == BuiltInGameIds.Arknights && tool.IsMaaCliPreset);
        Assert.Contains(presets, tool => tool.GameId == BuiltInGameIds.Endfield && tool.Name == "MaaEnd");
        Assert.Contains(presets, tool => tool.GameId == BuiltInGameIds.HonkaiStarRail && tool.Name == "三月七小助手");
        Assert.Contains(presets, tool => tool.GameId == BuiltInGameIds.GenshinImpact && tool.Name == "BetterGI");
        Assert.All(presets, tool => Assert.True(tool.IsBuiltInPreset));
    }
}
