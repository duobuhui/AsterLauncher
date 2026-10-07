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
    public async Task PublishedFeedActivatesAndRestartsWithVerifiedImagesAndDayPrecision()
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(Fixture, "catalog.json"));
        var catalog = ResourceUpdateService.Parse(bytes);
        var existing = Assert.Single(catalog.Pools, p => p.Key == "绚丽异彩" && p.Phase == "1");
        Assert.Equal("2026/09/24 — 2026/10/15", existing.DateText);
        Assert.Null(existing.EndsAt);
        Assert.Equal(new DateOnly(2026, 10, 15), existing.EndsOn);
        Assert.Equal("2026/10/15 — 2026/11/05", Assert.Single(catalog.Pools, p => p.Key == "万物更新").DateText);
        Assert.Equal("2026/11/05 — 2026/11/26", Assert.Single(catalog.Pools, p => p.Key == "烟火邀星河").DateText);
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
        foreach (var name in new[] { "万物更新", "烟火邀星河", "祖泉的新流" })
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
        Assert.Equal(2, result.Pools.Count(p => p.Category == EndfieldPoolCategory.Chartered));
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
        legacy["revision"] = catalog.Revision - 1;
        foreach (var pool in legacy["pools"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>())
            foreach (var field in new[] { "startsOn", "endsOn", "category", "featuredOperator" }) pool.Remove(field);
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
            var stale = Assert.Single(service.Current!.Pools, p => p.Key == "绚丽异彩");
            Assert.Null(stale.EndsOn);
            Assert.Equal("2026/09/24 12:00 — 未公布", stale.DateText);
            Assert.True(await service.RefreshAsync());
            Assert.Equal(catalog.Revision, service.Current!.Revision);
            Assert.Equal("2026/09/24 — 2026/10/15", Assert.Single(service.Current.Pools, p => p.Key == "绚丽异彩").DateText);
            var restarted = new ResourceUpdateService(http, root);
            await restarted.LoadAsync();
            Assert.Equal(new DateOnly(2026, 10, 15), Assert.Single(restarted.Current!.Pools, p => p.Key == "绚丽异彩").EndsOn);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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