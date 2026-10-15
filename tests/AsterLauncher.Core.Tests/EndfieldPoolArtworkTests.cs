using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldPoolArtworkTests
{
    private const string RefactorBannerHash = "fefe7528f847f2cf217be08efce5be973413df8c4ac657a377d9f7d733b152cf";
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "ResourceFeed");

    [Theory]
    [InlineData("绚丽异彩", "refactor", "伊冯", "1", "Gacha/refactor-vivid.png")]
    [InlineData("热烈色彩", "chartered", "伊冯", null, "Gacha/vivid.png")]
    [InlineData("临渊望北", "chartered", "诀", null, "Gacha/north.jpg")]
    [InlineData("未来特许", "chartered", "新干员", null, "endfield-pool-card.svg")]
    [InlineData("未来重构", "refactor", "新干员", "1", "endfield-pool-card.svg")]
    [InlineData("绚丽异彩", "refactor", "不同干员", "1", "endfield-pool-card.svg")]
    [InlineData("热烈色彩", "chartered", "不同干员", null, "endfield-pool-card.svg")]
    public void RuleMetadataPreservesOnlyMatchingKnownBundledArtwork(
        string name, string category, string featured, string? phase, string expectedBanner)
    {
        var catalog = new ResourceCatalog
        {
            Pools = [new ResourcePool
            {
                GameId = BuiltInGameIds.Endfield, Key = name, Name = name,
                Category = category, FeaturedOperator = featured, Phase = phase
            }]
        };

        var definition = EndfieldPoolCatalog.Resolve("sample", name, catalog, phase);

        Assert.Equal(expectedBanner, definition.BannerAssetFileName);
        Assert.Equal(featured, definition.FeaturedOperator);
        Assert.Equal(category == "refactor" ? EndfieldPoolCategory.Refactor : EndfieldPoolCategory.Chartered,
            definition.Category);
    }

    [Fact]
    public void PublishedRefactorRulesRetainBundledArtworkInAnalysisWithoutChangingRecords()
    {
        var catalog = ResourceUpdateService.Parse(File.ReadAllBytes(Path.Combine(Fixture, "catalog.json")));
        var archive = JsonNode.Parse("""
        {"characters":[
          {"seqId":1,"gachaTs":1790000000000,"kind":"draw","poolId":"rerun_chr_yvonne","poolName":"绚丽异彩","poolVersion":1,"rarity":4}
        ],"weapons":[]}
        """)!.AsObject();
        var original = archive.ToJsonString();

        var pool = Assert.Single(EndfieldGachaAnalyzer.Analyze(archive, catalog).Pools);

        Assert.Equal("绚丽异彩 #1", pool.PoolName);
        Assert.Equal("绚丽异彩", pool.BannerResourceKey);
        Assert.Equal("Gacha/refactor-vivid.png", pool.BannerAssetFileName);
        Assert.Equal("2026/09/24 12:00 — 2026/10/15 06:00", pool.DateRangeText);
        Assert.Equal("伊冯", pool.FeaturedOperatorName);
        Assert.Equal(original, archive.ToJsonString());
    }

    [Fact]
    public async Task ExistingClientCanRestorePhaseOneBannerByRefreshingTheFeedAndRestarting()
    {
        var catalog = ResourceUpdateService.Parse(await File.ReadAllBytesAsync(Path.Combine(Fixture, "catalog.json")));
        Assert.True(catalog.Revision >= 2026101502);
        var banner = Assert.Single(catalog.Images,
            item => item.GameId == BuiltInGameIds.Endfield && item.Kind == "banner" && item.Key == "绚丽异彩");
        Assert.Equal(RefactorBannerHash, banner.Sha256);
        Assert.Equal(928667, banner.Size);
        Assert.Equal("https://web.hycdn.cn/upload/image/20260921/394fc7fba6fed31434e0c65f69ecdecd.png", banner.Source);
        var imageBytes = await File.ReadAllBytesAsync(Path.Combine(Fixture, banner.File));
        Assert.Equal(banner.Size, imageBytes.LongLength);
        Assert.Equal(RefactorBannerHash, Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant());

        var oldCatalog = catalog with
        {
            Revision = 2026101501,
            Images = catalog.Images.Where(item => item != banner).ToArray()
        };
        var root = Path.Combine(Environment.GetEnvironmentVariable("ASTERLAUNCHER_TEST_TEMP_ROOT") ?? Path.GetTempPath(),
            "resource-banner-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var oldHttp = new HttpClient(new FeedHandler(oldCatalog));
            var oldService = new ResourceUpdateService(oldHttp, root);
            Assert.True(await oldService.RefreshAsync());
            Assert.Null(oldService.FindImage(BuiltInGameIds.Endfield, "banner", "绚丽异彩"));

            using var http = new HttpClient(new FeedHandler(catalog));
            var service = new ResourceUpdateService(http, root);
            await service.LoadAsync();
            Assert.Equal(2026101501, service.Current!.Revision);
            Assert.True(await service.RefreshAsync());
            var restored = service.FindImage(BuiltInGameIds.Endfield, "banner", "绚丽异彩");
            Assert.NotNull(restored);
            Assert.Equal(imageBytes, await File.ReadAllBytesAsync(restored));
            foreach (var alias in banner.Aliases)
                Assert.Equal(restored, service.FindImage(BuiltInGameIds.Endfield, "banner", alias));
            Assert.Null(service.FindImage(BuiltInGameIds.Endfield, "banner", "绚丽异彩 #2"));

            var restarted = new ResourceUpdateService(http, root);
            await restarted.LoadAsync();
            Assert.Equal(catalog.Revision, restarted.Current!.Revision);
            Assert.Equal(restored, restarted.FindImage(BuiltInGameIds.Endfield, "banner", "绚丽异彩"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FeedHandler(ResourceCatalog catalog) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var relative = request.RequestUri!.AbsolutePath.Split("/resources/", 2)[1];
            var bytes = relative == "catalog.json"
                ? JsonSerializer.SerializeToUtf8Bytes(catalog, ResourceUpdateService.JsonOptions)
                : File.ReadAllBytes(Path.Combine(Fixture, relative));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        }
    }
}