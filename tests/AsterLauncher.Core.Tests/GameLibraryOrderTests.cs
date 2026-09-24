using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class GameLibraryOrderTests
{
    [Fact]
    public void VisibleIds_PreservesOrderAndExcludesHiddenBuiltIn()
    {
        var available = new[] { "endfield", "honkai-star-rail", "custom-1", "new-game" };
        var visible = GameLibraryOrder.VisibleIds(
            available,
            ["endfield"],
            ["custom-1", "endfield", "honkai-star-rail", "stale-game"]);

        Assert.Equal(["custom-1", "honkai-star-rail", "new-game"], visible);
    }

    [Fact]
    public void Move_ReordersOnlyWhenTargetExists()
    {
        var order = new List<string> { "endfield", "honkai-star-rail", "custom-1" };
        Assert.True(GameLibraryOrder.Move(order, "honkai-star-rail", -1));
        Assert.Equal(["honkai-star-rail", "endfield", "custom-1"], order);
        Assert.False(GameLibraryOrder.Move(order, "honkai-star-rail", -1));
        Assert.Equal(["honkai-star-rail", "endfield", "custom-1"], order);
    }

    [Fact]
    public void MoveWithin_SkipsGamesInOtherGroup()
    {
        var order = new List<string> { "active-a", "hidden-a", "active-b", "hidden-b" };
        Assert.True(GameLibraryOrder.MoveWithin(order, "hidden-b", -1, ["hidden-a", "hidden-b"]));
        Assert.Equal(["active-a", "hidden-b", "active-b", "hidden-a"], order);
    }

    [Fact]
    public void ApplyGroupOrder_UpdatesOnlyDraggedGroup()
    {
        var order = new List<string> { "active-a", "hidden-a", "active-b", "hidden-b" };
        Assert.True(GameLibraryOrder.ApplyGroupOrder(order, ["active-b", "active-a"]));
        Assert.Equal(["active-b", "hidden-a", "active-a", "hidden-b"], order);
        Assert.False(GameLibraryOrder.ApplyGroupOrder(order, ["active-b", "active-a"]));
        Assert.False(GameLibraryOrder.ApplyGroupOrder(order, ["active-a", "other"]));
    }

    [Theory]
    [InlineData(@"E:\Games\StarRail.exe", BuiltInGameIds.HonkaiStarRail)]
    [InlineData(@"E:\Games\YUANSHEN.EXE", BuiltInGameIds.GenshinImpact)]
    [InlineData(@"E:\Games\Endfield.exe", BuiltInGameIds.Endfield)]
    [InlineData(@"E:\Games\Other.exe", null)]
    public void MatchBuiltInExecutable_UsesKnownExeNames(string path, string? expectedId)
    {
        var matched = GameLibraryOrder.MatchBuiltInExecutable(BuiltInGameCatalog.CreateAdapters(), path);
        Assert.Equal(expectedId, matched?.Definition.Id);
    }
}
