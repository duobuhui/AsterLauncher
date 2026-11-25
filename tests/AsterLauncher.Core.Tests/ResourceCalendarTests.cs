using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class ResourceCalendarTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "ResourceFeed");
    [Fact]
    public async Task PublishedFeedActivatesAndRestartsWithVerifiedImagesAndConfirmedBoundaryPrecision()
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(Fixture, "catalog.json"));
        var catalog = ResourceUpdateService.Parse(bytes);
        var existing = Assert.Single(catalog.Pools, p => p.Key == "绚丽异彩" && p.Phase == "1");
        Assert.Equal("2026/09/24 12:00 — 2026/10/15 06:00", existing.DateText);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T06:00:00+08:00"), existing.EndsAt);
        Assert.Null(existing.EndsOn);
        Assert.Equal("2026/10/15 — 2026/11/05", Assert.Single(catalog.Pools, p => p.Key == "万物更新").DateText);
        Assert.Equal("2026/11/05 12:00 — 2026/11/25 06:00", Assert.Single(catalog.Pools, p => p.Key == "烟火邀星河").DateText);
        Assert.Equal("2026/10/29 12:00 — 2026/11/19 11:59", Assert.Single(catalog.Pools, p => p.Key == "祖泉的新流").DateText);
        var root = Path.Combine(Environment.GetEnvironmentVariable("ASTERLAUNCHER_TEST_TEMP_ROOT") ?? Path.GetTempPath(), "resource-calendar-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new FeedHandler());
            var service = new ResourceUpdateService(http, root);
            Assert.True(await service.RefreshAsync());
            var restarted = new ResourceUpdateService(http, root);
            await restarted.LoadAsync();
            Assert.Equal(catalog.Revision, restarted.Current!.Revision);
            Assert.Equal(existing.DateText, restarted.Current.Pools.Single(p => p.Key == "绚丽异彩").DateText);
            foreach (var item in catalog.Images)
            {
                var file = restarted.FindImage(item.GameId, item.Kind, item.Key);
                Assert.NotNull(file);
                Assert.Equal(item.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))).ToLowerInvariant());
            }
            Assert.False(await restarted.RefreshAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void CalendarDatesRejectConflictsAndReversedRanges()
    {
        var good = new ResourcePool { GameId = "endfield", Key = "测试", Name = "测试", StartsOn = new(2026, 10, 15), EndsOn = new(2026, 11, 5), Source = "https://endfield.hypergryph.com/news/3805" };
        foreach (var bad in new[] { good with { StartsAt = DateTimeOffset.Parse("2026-10-15T07:00:00+08:00") }, good with { EndsAt = DateTimeOffset.Parse("2026-11-05T11:59:00+08:00") }, good with { EndsOn = new(2026, 10, 14) }, good with { StartsOn = default(DateOnly) } })
            Assert.Throws<InvalidDataException>(() => ResourceUpdateService.Parse(JsonSerializer.SerializeToUtf8Bytes(new ResourceCatalog { Revision = 1, PublishedAt = DateTimeOffset.Now, Pools = [bad] }, ResourceUpdateService.JsonOptions)));
    }
    [Fact]
    public void DayPrecisionUsesBeijingDateWhileExactBoundariesKeepTimes()
    {
        Assert.Equal("2026/09/24 — 2026/10/15", new ResourcePool { StartsAt = DateTimeOffset.Parse("2026-09-23T20:00:00Z"), EndsOn = new(2026, 10, 15) }.DateText);
        Assert.Equal("2026/10/29 12:00 — 2026/11/19 11:59", new ResourcePool { StartsAt = DateTimeOffset.Parse("2026-10-29T04:00:00Z"), EndsAt = DateTimeOffset.Parse("2026-11-19T03:59:00Z") }.DateText);
    }
    [Fact]
    public void PublishedDefinitionsApplyFeaturedOperatorsAndKeepRerunPhasesSeparate()
    {
        var catalog = ResourceUpdateService.Parse(File.ReadAllBytes(Path.Combine(Fixture, "catalog.json")));
        var characters = new System.Text.Json.Nodes.JsonArray();
        var i = 1;
        foreach (var name in new[] { "万物更新", "烟火邀星河", "祖泉的新流", "锻砧、行囊与蜜糖", "致每一个飘游之愿", "绯红瑰宝" })
        {
            var item = catalog.Pools.Single(p => p.Key == name);
            characters.Add(new System.Text.Json.Nodes.JsonObject { ["poolId"] = "sample-" + i, ["poolName"] = name,
                ["poolVersion"] = item.Phase, ["charName"] = item.FeaturedOperator, ["rarity"] = 6, ["seqId"] = i,
                ["gachaTs"] = 1800000000 + i, ["kind"] = "draw" });
            i++;
        }
        var archive = new System.Text.Json.Nodes.JsonObject { ["characters"] = characters };
        var before = archive.ToJsonString();
        var result = EndfieldGachaAnalyzer.Analyze(archive, catalog);
        foreach (var pool in result.Pools)
        {
            Assert.NotNull(pool.FeaturedOperatorName);
            Assert.True(Assert.Single(pool.SixStarPulls).IsFeatured);
            Assert.Equal(1, pool.FeaturedPaidDrawNumber);
        }
        Assert.Equal(3, result.Pools.Count(p => p.Category == EndfieldPoolCategory.Chartered));
        Assert.Equal(3, result.Pools.Count(p => p.Category == EndfieldPoolCategory.Refactor));
        Assert.Equal(EndfieldPoolCategory.Refactor, Assert.Single(result.Pools, p => p.PoolName == "祖泉的新流 #1").Category);
        Assert.Null(EndfieldPoolCatalog.Resolve("rerun_chr_sample", "祖泉的新流", catalog, "2").FeaturedOperator);
        Assert.Null(EndfieldPoolCatalog.Resolve("unknown", "未知卡池", catalog).FeaturedOperator);
        Assert.Equal(before, archive.ToJsonString());
    }
    [Fact]
    public void UnverifiedRulesAndMissingRerunPhaseAreRejected()
    {
        var good = new ResourcePool { GameId = "endfield", Key = "池", Name = "池", Category = "chartered", FeaturedOperator = "干员", Source = "https://endfield.hypergryph.com/news/3805" };
        foreach (var bad in new[] { good with { Category = "unknown" }, good with { FeaturedOperator = "" }, good with { Category = "refactor" }, good with { GameId = BuiltInGameIds.HonkaiStarRail } })
            Assert.Throws<InvalidDataException>(() => ResourceUpdateService.Parse(JsonSerializer.SerializeToUtf8Bytes(new ResourceCatalog { Revision = 1, PublishedAt = DateTimeOffset.Now, Pools = [bad] }, ResourceUpdateService.JsonOptions)));
    }
    [Fact]
    public async Task LegacyCacheWithDroppedOptionalFieldsRecoversOnNextFeedRevision()
    {
        var catalog = ResourceUpdateService.Parse(File.ReadAllBytes(Path.Combine(Fixture, "catalog.json")));
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Fixture, "catalog.json")))!;
        legacy["revision"] = 2026110601;
        // Model a valid older cached pool with a known start and a dropped date-only end.
        // New entries with a date-only start plus an exact end cannot be represented by that old cache.
        legacy["pools"] = new System.Text.Json.Nodes.JsonArray(legacy["pools"]!.AsArray()
            .OfType<System.Text.Json.Nodes.JsonObject>().Single(pool => pool["key"]!.GetValue<string>() == "烟火邀星河").DeepClone());
        foreach (var pool in legacy["pools"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>())
        {
            // The November 6 feed had only an estimated date boundary for Minghe; older clients lost it.
            pool.Remove("endsAt");
            foreach (var field in new[] { "startsOn", "endsOn", "category", "featuredOperator" }) pool.Remove(field);
        }
        _ = ResourceUpdateService.Parse(System.Text.Encoding.UTF8.GetBytes(legacy.ToJsonString()));
        var root = Path.Combine(Environment.GetEnvironmentVariable("ASTERLAUNCHER_TEST_TEMP_ROOT") ?? Path.GetTempPath(), "resource-upgrade-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = Path.Combine(root, "resources");
            Directory.CreateDirectory(Path.Combine(cache, "content"));
            await File.WriteAllTextAsync(Path.Combine(cache, "catalog.json"), legacy.ToJsonString());
            foreach (var image in catalog.Images)
                File.Copy(Path.Combine(Fixture, image.File), Path.Combine(cache, "content", image.Sha256 + Path.GetExtension(image.File)), true);
            using var http = new HttpClient(new FeedHandler());
            var service = new ResourceUpdateService(http, root);
            await service.LoadAsync();
            var stale = Assert.Single(service.Current!.Pools, p => p.Key == "烟火邀星河");
            Assert.Null(stale.EndsOn);
            Assert.Equal("2026/11/05 12:00 — 未公布", stale.DateText);
            Assert.True(await service.RefreshAsync());
            Assert.Equal(catalog.Revision, service.Current!.Revision);
            Assert.Equal("2026/11/05 12:00 — 2026/11/25 06:00", Assert.Single(service.Current.Pools, p => p.Key == "烟火邀星河").DateText);
            var restarted = new ResourceUpdateService(http, root);
            await restarted.LoadAsync();
            Assert.Equal(DateTimeOffset.Parse("2026-11-25T06:00:00+08:00"), Assert.Single(restarted.Current!.Pools, p => p.Key == "烟火邀星河").EndsAt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void PublishedDanqingDuFeedUsesConfirmedMaintenanceAndConservativeOpeningHours()
    {
        var catalog = ResourceUpdateService.Parse(File.ReadAllBytes(Path.Combine(Fixture, "catalog.json")));
        var maintenance = Assert.Single(catalog.Announcements, p => p.Key == "endfield-danqingdu-maintenance-20261015");
        Assert.Equal("maintenance", maintenance.Kind);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T06:00:00+08:00"), maintenance.StartsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T12:00:00+08:00"), maintenance.EndsAt);
        Assert.Contains("计划", maintenance.Description);
        foreach (var key in new[] { "endfield-danqingdu-version", "endfield-yan-huo-sheng-yuan-202610", "endfield-mo-fen-zhu-cai-202610", "endfield-wan-wu-you-ling-202610", "endfield-zhan-zheng-hui-xiang-yi-xiang-202610" })
        {
            var item = Assert.Single(catalog.Announcements, p => p.Key == key);
            Assert.Null(item.StartsAt);
            Assert.Equal(new DateOnly(2026, 10, 15), item.StartsOn);
        }
        var si = Assert.Single(catalog.Pools, p => p.Key == "万物更新");
        Assert.Null(si.StartsAt);
        Assert.Equal(new DateOnly(2026, 10, 15), si.StartsOn);
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T11:59:00+08:00"), si.EndsAt);
        var minghe = Assert.Single(catalog.Pools, p => p.Key == "烟火邀星河");
        Assert.Equal(DateTimeOffset.Parse("2026-11-25T06:00:00+08:00"), minghe.EndsAt);
        Assert.Null(minghe.EndsOn);
        var preload = Assert.Single(catalog.Announcements, p => p.Key == "endfield-danqingdu-preload-pc-20261013");
        Assert.Equal(new DateOnly(2026, 10, 13), preload.StartsOn);
        Assert.Null(preload.StartsAt);
        Assert.Null(preload.EndsAt);
        Assert.Null(preload.EndsOn);
        Assert.Equal("PC", Assert.Single(preload.Platforms));
    }
    [Fact]
    public void PublishedTangtangPoolCorrectsWeaponAndSeparatesChallengeAndExchangeDeadlines()
    {
        var catalog = ResourceUpdateService.Parse(File.ReadAllBytes(Path.Combine(Fixture, "catalog.json")));
        var tangtang = Assert.Single(catalog.Pools, p => p.Key == "祖泉的新流" && p.Phase == "1");
        Assert.Equal("汤汤", tangtang.FeaturedOperator);
        Assert.Equal("细枝申领", tangtang.WeaponPool);
        Assert.Equal("落草", tangtang.Weapon);
        Assert.Equal("2026/10/29 12:00 — 2026/11/19 11:59", tangtang.DateText);
        var challenge = Assert.Single(catalog.Announcements, p => p.Key == "endfield-wei-ji-he-yue-jin-lan-202611");
        Assert.Equal(DateTimeOffset.Parse("2026-11-25T06:00:00+08:00"), challenge.EndsAt);
        Assert.Null(challenge.EndsOn);
        var exchange = Assert.Single(catalog.Announcements, p => p.Key == "endfield-ji-mi-sheng-suo-jin-lan-202611");
        Assert.Equal(DateTimeOffset.Parse("2026-12-02T04:00:00+08:00"), exchange.EndsAt);
        Assert.Null(exchange.EndsOn);
        var sanity = Assert.Single(catalog.Announcements, p => p.Key == "endfield-li-zhi-bu-gei-202611");
        Assert.Equal(DateTimeOffset.Parse("2026-11-18T04:00:00+08:00"), sanity.StartsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-25T04:00:00+08:00"), sanity.EndsAt);
        var guideReward = Assert.Single(catalog.Announcements, p => p.Key == "endfield-danqingdu-guide-reward");
        Assert.Equal(DateTimeOffset.Parse("2026-10-06T21:00:00+08:00"), guideReward.StartsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T06:00:00+08:00"), guideReward.EndsAt);
        Assert.Equal("event", guideReward.Kind);
        Assert.Empty(catalog.Codes);
    }
    [Fact]
    public void PublishedFeedUsesIndependentFormalPoolBanners()
    {
        var catalog = ResourceUpdateService.Parse(File.ReadAllBytes(Path.Combine(Fixture, "catalog.json")));
        Assert.True(catalog.Revision >= 2026101501);
        var si = Assert.Single(catalog.Pools, item => item.Key == "万物更新");
        Assert.Equal("https://endfield.hypergryph.com/news/8548", si.Source);
        var banner = Assert.Single(catalog.Images, item => item.Kind == "banner" && item.Key == si.Key);
        Assert.Equal("https://web.hycdn.cn/upload/image/20261014/4aecb8b0646268c07144fc5a0acc42bf.jpg", banner.Source);
        Assert.Equal(506443, banner.Size);
        Assert.Equal("d25f39e9ce6cf375eb1ec7eeae6e6ad3eaaa09d809a1bb6b341f6282d2253c35", banner.Sha256);
        var bytes = File.ReadAllBytes(Path.Combine(Fixture, banner.File));
        Assert.Equal(banner.Size, bytes.LongLength);
        Assert.Equal(banner.Sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var minghe = Assert.Single(catalog.Images, item => item.Kind == "banner" && item.Key == "烟火邀星河");
        Assert.Equal("images/endfield/starry-flow-banner.png", minghe.File);
        Assert.Equal("https://web.hycdn.cn/upload/image/20261027/131177beccbe5a8d9e1b6f552e604ee5.png", minghe.Source);
        Assert.Equal("https://endfield.hypergryph.com/news/7204", Assert.Single(catalog.Pools, item => item.Key == minghe.Key).Source);
        Assert.Equal("images/endfield/starry-flow.png", Assert.Single(catalog.Images, item => item.Kind == "portrait" && item.Key == "明河").File);
        var tangtang = Assert.Single(catalog.Images, item => item.Kind == "banner" && item.Key == "祖泉的新流");
        Assert.Equal("https://web.hycdn.cn/upload/image/20261029/584402670a2fd8afd90412ee0bbb4dd9.png", tangtang.Source);
        Assert.Equal("https://endfield.hypergryph.com/news/3893", Assert.Single(catalog.Pools, item => item.Key == tangtang.Key).Source);
        Assert.Contains("祖泉的新流 #1", tangtang.Aliases);
        Assert.DoesNotContain("祖泉的新流 #2", tangtang.Aliases);
    }

    [Fact]
    public void PublishedOctober15FeedUsesFinalNoticeForNewlyConfirmedCalendarFacts()
    {
        var catalog = ResourceUpdateService.Parse(File.ReadAllBytes(Path.Combine(Fixture, "catalog.json")));
        const string source = "https://endfield.hypergryph.com/news/6094";
        var challenge = Assert.Single(catalog.Announcements,
            item => item.Key == "endfield-feng-bei-liu-ming-mo-lin-202610");
        Assert.Equal("丰碑留名·魇林", challenge.Title);
        Assert.Equal(source, challenge.Url);
        var spaces = Assert.Single(catalog.Announcements,
            item => item.Key == "endfield-mi-jing-xing-zhe-hua-wai-second-202611");
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T04:00:00+08:00"), spaces.StartsAt);
        Assert.Null(spaces.EndsAt);
        Assert.Null(spaces.EndsOn);
        Assert.Equal(source, spaces.Url);
        var compensation = Assert.Single(catalog.Announcements,
            item => item.Key == "endfield-danqingdu-maintenance-compensation-20261015");
        Assert.Equal(new DateOnly(2026, 10, 15), compensation.StartsOn);
        Assert.Null(compensation.EndsOn);
        Assert.Null(compensation.StartsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-25T06:00:00+08:00"), compensation.EndsAt);
        Assert.Contains("两封", compensation.Description);
        Assert.Contains("900", compensation.Description);
        Assert.Equal(source, compensation.Url);
        Assert.Empty(catalog.Codes);
    }

    [Fact]
    public void November25FeedUsesOfficialPoolAndActivityBoundariesWithoutInventingMaintenanceHours()
    {
        var catalog = ResourceUpdateService.Parse(File.ReadAllBytes(Path.Combine(Fixture, "catalog.json")));
        Assert.True(catalog.Revision >= 2026112501);
        var version = Assert.Single(catalog.Announcements, a => a.Key == "endfield-moging-version-202611");
        Assert.Equal(new DateOnly(2026, 11, 25), version.StartsOn);
        Assert.Equal(new DateOnly(2027, 1, 7), version.EndsOn);
        Assert.Null(version.StartsAt);
        Assert.Null(version.EndsAt);
        var maintenance = Assert.Single(catalog.Announcements, a => a.Key == "endfield-moging-maintenance-20261125");
        Assert.Equal(DateTimeOffset.Parse("2026-11-25T06:00:00+08:00"), maintenance.StartsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-25T12:00:00+08:00"), maintenance.EndsAt);
        var old = Assert.Single(catalog.Announcements, a => a.Key == "endfield-danqingdu-version");
        Assert.Equal(maintenance.StartsAt, old.EndsAt);
        Assert.DoesNotContain(catalog.Announcements, a => a.EndsOn == new DateOnly(2026, 11, 26));

        var kay = Assert.Single(catalog.Pools, p => p.Key == "锻砧、行囊与蜜糖");
        Assert.Equal("小刻", kay.FeaturedOperator);
        Assert.Equal("甜美交易", kay.Weapon);
        Assert.Equal("chartered", kay.Category);
        Assert.Equal(version.StartsOn, kay.StartsOn);
        Assert.Null(kay.StartsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-12-23T11:59:00+08:00"), kay.EndsAt);
        var perlica = Assert.Single(catalog.Pools, p => p.Key == "致每一个飘游之愿" && p.Phase == "1");
        Assert.Equal("洁尔佩塔", perlica.FeaturedOperator);
        Assert.Equal("使命必达", perlica.Weapon);
        Assert.Equal("refactor", perlica.Category);
        Assert.Equal(version.StartsOn, perlica.StartsOn);
        Assert.Equal(DateTimeOffset.Parse("2026-12-16T11:59:00+08:00"), perlica.EndsAt);
        var rosy = Assert.Single(catalog.Pools, p => p.Key == "绯红瑰宝" && p.Phase == "1");
        Assert.Equal("洛茜", rosy.FeaturedOperator);
        Assert.Equal("狼之绯", rosy.Weapon);
        Assert.Equal(DateTimeOffset.Parse("2026-12-16T12:00:00+08:00"), rosy.StartsAt);
        Assert.Equal(version.EndsOn, rosy.EndsOn);
        Assert.Null(rosy.EndsAt);
        Assert.Null(EndfieldPoolCatalog.Resolve("rerun_chr_sample", rosy.Key, catalog, "2").FeaturedOperator);
        var phaseOne = Assert.Single(catalog.Images, i => i.Kind == "banner" && i.Key == perlica.Key);
        Assert.Contains(perlica.Key + " #1", phaseOne.Aliases);
        Assert.DoesNotContain(perlica.Key + " #2", phaseOne.Aliases);
        Assert.Contains(catalog.Images, i => i.Kind == "banner" && i.Key == kay.Key);
        Assert.DoesNotContain(catalog.Images, i => i.Kind == "banner" && i.Key == rosy.Key);
        foreach (var portrait in new[] { "小刻", "米米诺" })
            Assert.Contains(catalog.Images, i => i.Kind == "portrait" && i.Key == portrait);

        var gift = Assert.Single(catalog.Announcements, a => a.Key == "endfield-administrator-thank-you-20261119");
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T00:00:00+08:00"), gift.StartsAt);
        Assert.Contains("3000", gift.Description);
        Assert.Contains("30天", gift.Description);
        var sanity1 = Assert.Single(catalog.Announcements, a => a.Key == "endfield-sanity-supply-20261209");
        var sanity2 = Assert.Single(catalog.Announcements, a => a.Key == "endfield-sanity-supply-20261231");
        Assert.Equal(DateTimeOffset.Parse("2026-12-16T04:00:00+08:00"), sanity1.EndsAt);
        Assert.Equal(DateTimeOffset.Parse("2027-01-07T04:00:00+08:00"), sanity2.EndsAt);
        var calendar = ResourceScheduleProjector.Build(catalog, "endfield", DateTimeOffset.Parse("2026-12-17T12:00:00+08:00"));
        var p1 = Assert.Single(calendar.Entries, a => a.Key == "pool:致每一个飘游之愿:1");
        var p2 = Assert.Single(calendar.Entries, a => a.Key == "pool:绯红瑰宝:1");
        Assert.True(p1.IsExpired);
        Assert.True(p2.IsActive);
        Assert.Empty(catalog.Codes);
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var relative = request.RequestUri!.AbsolutePath.Split("/resources/", 2)[1];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(Path.Combine(Fixture, relative))) });
        }
    }
}
