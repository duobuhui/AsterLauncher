using System.Text.Json.Nodes;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class UigfGachaAnalysisTests
{
    [Theory]
    [InlineData(BuiltInGameIds.GenshinImpact,"hk4e","301")]
    [InlineData(BuiltInGameIds.HonkaiStarRail,"hkrpg","11")]
    [InlineData(BuiltInGameIds.ZenlessZoneZero,"nap","2")]
    public void PublicResourcePoolNamesAndDatesUseStableBannerIdentity(string game,string key,string type)
    {
        var root=Root(key,User("100000001",Pull("1",type,"5","角色","banner-resource")));
        var catalog=new ResourceCatalog{Revision=1,PublishedAt=DateTimeOffset.Now,Pools=[new(){GameId=game,Key="banner-resource",Name="公开卡池资料",StartsAt=DateTimeOffset.Parse("2026-10-01T00:00:00Z"),EndsAt=DateTimeOffset.Parse("2026-10-15T00:00:00Z"),Source="https://www.mihoyo.com/"}]};
        var banner=Assert.Single(Assert.Single(Assert.Single(UigfGachaAnalyzer.Analyze(root,game,catalog).Accounts).Sections).Banners);
        Assert.Equal("公开卡池资料",banner.Title);Assert.Equal("2026/10/01 08:00 — 2026/10/15 08:00",banner.DateText);
    }
    [Fact]
    public void PityIndicator_DistinguishesLocalLowerBound_MissingRecords_AndUnknownRules()
    {
        Assert.Equal("≥20 / 90", new UigfPityIndicator("角色", 20, 90, false).Text);
        Assert.Equal("20 / 90", new UigfPityIndicator("角色", 20, 90, true).Text);
        Assert.Equal("—", new UigfPityIndicator("角色", null, 90).Text);
        Assert.Equal("20 抽", new UigfPityIndicator("特殊", 20, null).Text);
        Assert.Equal(0, new UigfPityIndicator("特殊", 20, null).Percent);
    }
    [Fact]
    public void Genshin_Combines301And400_SeparatesWeaponAndChronicled_AndUsesOnlyCompleteIntervals()
    {
        var root = Root("hk4e", User("100000001",
            Pull("9", "301", "5", "迪卢克", "first"),
            Pull("10", "400", "3", "弹弓", "second"),
            Pull("11", "301", "5", "芙宁娜", "second"),
            Pull("12", "400", "4", "菲谢尔", "second"),
            Pull("13", "302", "5", "天空之翼"),
            Pull("14", "500", "5", "迪卢克")));
        var account = Assert.Single(UigfGachaAnalyzer.Analyze(root, BuiltInGameIds.GenshinImpact).Accounts);
        var role = account.Sections.Single(section => section.Type == "301");
        Assert.Equal(4, role.RecordCount);
        Assert.Equal(2, role.HighRarityCount);
        Assert.Equal(1, role.PityCount);
        Assert.Equal("均抽 2.0", role.AverageText);
        Assert.Equal(2, role.HighRarityPulls[0].LocalPullCount);
        Assert.True(role.HighRarityPulls[0].HasPreviousHighRarity);
        Assert.Equal("本地 ≥1 抽", role.HighRarityPulls[1].PullCountText);
        Assert.Equal(2, role.Banners.Count);
        Assert.Equal(80, account.Sections.Single(s => s.Type == "302").PityMaximum);
        Assert.Equal(90, account.Sections.Single(s => s.Type == "500").PityMaximum);
        Assert.Equal(6, account.RecordCount);
    }

    [Fact]
    public void Genshin_DoesNotInventBannerPeriods_OrOverrideRealTypeWithUigfType()
    {
        var weapon = Pull("2", "302", "5", "天空之翼");
        weapon["uigf_gacha_type"] = "301";
        var first = Pull("1", "400", "5", "迪卢克");
        first["time"] = "2025-01-01 00:00:00";
        var last = Pull("3", "301", "5", "芙宁娜");
        var account = Assert.Single(UigfGachaAnalyzer.Analyze(Root("hk4e", User("100000001", first, weapon, last)),
            BuiltInGameIds.GenshinImpact).Accounts);
        var roles = account.Sections.Single(s => s.Type == "301");
        Assert.Equal("未标识卡池", Assert.Single(roles.Banners).PoolId);
        Assert.Single(account.Sections.Single(s => s.Type == "302").HighRarityPulls);
    }

    [Fact]
    public void Zenless_UsesEncodedSABRanks_AndKeepsAllSixChannelsIndependent()
    {
        var account = Assert.Single(UigfGachaAnalyzer.Analyze(Root("nap", User("100000001",
            Pull("1", "2", "4", "艾莲"), Pull("2", "2", "3", "安比"), Pull("3", "2", "2", "月相"),
            Pull("4", "102", "4", "星见雅"), Pull("5", "102", "2", "月相"),
            Pull("6", "3", "4", "深海访客"), Pull("7", "103", "4", "残心青囊"),
            Pull("8", "1", "4", "丽娜"), Pull("9", "5", "4", "鲨牙布"))),
            BuiltInGameIds.ZenlessZoneZero).Accounts);
        Assert.Equal(6, account.Sections.Count);
        var exclusive = account.Sections.Single(s => s.Type == "2");
        Assert.Equal("S 1 [33.33%]", exclusive.HighRarityStat);
        Assert.Equal(1, exclusive.MiddleRarityCount);
        Assert.Equal(1, exclusive.LowRarityCount);
        Assert.Equal(2, exclusive.PityCount);
        Assert.Equal(1, account.Sections.Single(s => s.Type == "102").PityCount);
        Assert.Equal(80, account.Sections.Single(s => s.Type == "5").PityMaximum);
        Assert.All(account.Sections, section => Assert.False(section.HighRarityPulls[0].HasPreviousHighRarity));
    }

    [Theory]
    [InlineData(BuiltInGameIds.GenshinImpact, "hk4e", "5")]
    [InlineData(BuiltInGameIds.HonkaiStarRail, "hkrpg", "5")]
    [InlineData(BuiltInGameIds.ZenlessZoneZero, "nap", "4")]
    public void Analysis_PreservesUnknownPool_WithoutInventingPityMaximum(string game, string key, string rank)
    {
        var section = Assert.Single(Assert.Single(UigfGachaAnalyzer.Analyze(
            Root(key, User("100000001", Pull("1", "999", rank, "新物品"))), game).Accounts).Sections);
        Assert.Null(section.PityMaximum);
        Assert.Equal(0, section.PityPercent);
        Assert.False(Assert.Single(section.HighRarityPulls).IsHighCount);
        Assert.Equal("均抽 —", section.AverageText);
    }

    [Fact]
    public void Analysis_IsolatesGamesAndAccounts_AndOrdersLargeIdsAtSameTimestamp()
    {
        var root = Root("hk4e",
            User("100000001", Pull("10000000000000000001", "301", "5", "芙宁娜"),
                Pull("9999999999999999999", "400", "5", "迪卢克")),
            User("900000002", Pull("1", "301", "3", "弹弓")));
        root["nap"] = new JsonArray(User("100000001", Pull("1", "2", "4", "艾莲")));
        var genshin = UigfGachaAnalyzer.Analyze(root, BuiltInGameIds.GenshinImpact);
        Assert.Equal(2, genshin.Accounts.Count);
        Assert.Equal("芙宁娜", genshin.Accounts[0].Sections[0].HighRarityPulls[0].Name);
        Assert.Equal(1, genshin.Accounts[1].Sections[0].PityCount);
        Assert.Equal("艾莲", Assert.Single(UigfGachaAnalyzer.Analyze(root, BuiltInGameIds.ZenlessZoneZero)
            .Accounts).Sections[0].HighRarityPulls[0].Name);
        Assert.Empty(UigfGachaAnalyzer.Analyze(root, BuiltInGameIds.HonkaiStarRail).Accounts);
    }

    private static JsonObject Root(string key, params JsonObject[] users) =>
        new() { [key] = new JsonArray(users.Select(user => (JsonNode)user).ToArray()) };
    private static JsonObject User(string uid, params JsonObject[] pulls) =>
        new() { ["uid"] = uid, ["list"] = new JsonArray(pulls.Select(pull => (JsonNode)pull).ToArray()) };
    private static JsonObject Pull(string id, string type, string rank, string name, string banner = "") =>
        new() { ["id"] = id, ["gacha_type"] = type, ["rank_type"] = rank, ["name"] = name,
            ["gacha_id"] = banner, ["time"] = "2026-01-01 00:00:00", ["item_type"] = "test" };
}