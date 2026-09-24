using AsterLauncher.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;

namespace AsterLauncher.Core.Tests;

[Collection("Environment variables")]
public sealed class EndfieldGachaArchiveServiceTests : IDisposable
{
    private readonly string _originalDataHome = Environment.GetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME") ?? string.Empty;
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? AppContext.BaseDirectory,
        "AsterLauncher.Tests",
        Guid.NewGuid().ToString("N"));

    public EndfieldGachaArchiveServiceTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _root);
    }

    [Fact]
    public async Task ImportMergeAndExport_PreservesCharacterAndWeaponRecords()
    {
        var input = Path.Combine(_root, "akerecord.json");
        await File.WriteAllTextAsync(input, """
        {
          "characters": [
            { "seqId": 101, "poolType": "standard", "name": "Operator A", "rarity": 6, "token": "private-token" },
            { "seqId": 102, "poolType": "special", "name": "Operator B", "rarity": 5 }
          ],
          "weapons": [
            { "seqId": 201, "name": "Weapon A", "rarity": 6 }
          ]
        }
        """);
        var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance);

        var first = await service.ImportAsync(input);
        var second = await service.ImportAsync(input);
        var summary = await service.GetSummaryAsync();
        var jsonPath = Path.Combine(_root, "export.json");
        var csvPath = Path.Combine(_root, "export.csv");
        var jsonExport = await service.ExportJsonAsync(jsonPath);
        var csvExport = await service.ExportCsvAsync(csvPath);

        Assert.True(first.Success, first.Message);
        Assert.Equal(3, first.ImportedCount);
        Assert.True(second.Success, second.Message);
        Assert.Equal(0, second.ImportedCount);
        Assert.Equal(2, summary.CharacterCount);
        Assert.Equal(1, summary.WeaponCount);
        Assert.True(jsonExport.Success, jsonExport.Message);
        Assert.True(csvExport.Success, csvExport.Message);
        Assert.Contains("AsterLauncher.EndfieldGacha.v1", await File.ReadAllTextAsync(jsonPath));
        Assert.Contains("\"character\"", await File.ReadAllTextAsync(csvPath));
        Assert.Contains("\"weapon\"", await File.ReadAllTextAsync(csvPath));
        Assert.DoesNotContain("private-token", await File.ReadAllTextAsync(jsonPath));
        Assert.DoesNotContain("private-token", await File.ReadAllTextAsync(csvPath));
    }

    [Fact]
    public async Task Import_TopLevelArrayRequiresCategoryInFileName()
    {
        var input = Path.Combine(_root, "unknown.json");
        await File.WriteAllTextAsync(input, """[{ "seqId": 1 }]""");
        var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance);

        var result = await service.ImportAsync(input);

        Assert.False(result.Success);
        Assert.Contains("char", result.Message);
    }

    [Fact]
    public async Task Analysis_CountsDrawsPerPoolAndExcludesGiftEvents()
    {
        var input = Path.Combine(_root, "analysis.json");
        await File.WriteAllTextAsync(input, """
        {
          "characters": [
            { "seqId": 7, "gachaTs": 1700000007000, "kind": "draw", "poolId": "standard", "poolName": "基础寻访", "rarity": 6, "charName": "Operator C" },
            { "seqId": 6, "gachaTs": 1700000006000, "kind": "draw", "poolId": "limited", "poolName": "限定寻访", "rarity": 4 },
            { "seqId": 5, "gachaTs": 1700000005000, "kind": "draw", "poolId": "limited", "poolName": "限定寻访", "rarity": 6, "charName": "Operator B", "isFree": true },
            { "seqId": 4, "gachaTs": 1700000004000, "kind": "draw", "poolId": "limited", "poolName": "限定寻访", "rarity": 5, "isFree": true },
            { "seqId": 3, "gachaTs": 1700000003000, "kind": "gift_intel_book", "poolId": "limited", "poolName": "限定寻访" },
            { "seqId": 2, "gachaTs": 1700000002000, "kind": "draw", "poolId": "limited", "poolName": "限定寻访", "rarity": 6, "charName": "Operator A" },
            { "seqId": 1, "gachaTs": 1700000001000, "poolId": "limited", "poolName": "限定寻访", "rarity": 4 }
          ],
          "weapons": []
        }
        """);
        var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance);

        var imported = await service.ImportAsync(input);
        var analysis = await service.GetAnalysisAsync();

        Assert.True(imported.Success, imported.Message);
        Assert.Equal(6, analysis.CharacterDrawCount);
        Assert.Equal(3, analysis.SixStarCount);
        Assert.Equal(1, analysis.FreeSixStarCount);
        Assert.Equal("Operator C", analysis.SixStarPulls[0].OperatorName);
        Assert.Equal(1, analysis.SixStarPulls[0].PoolDrawNumber);
        Assert.Equal(1, analysis.SixStarPulls[0].CountedDrawsSincePaidSixStar);
        Assert.Equal("Operator B", analysis.SixStarPulls[1].OperatorName);
        Assert.Equal(4, analysis.SixStarPulls[1].PoolDrawNumber);
        Assert.Equal(0, analysis.SixStarPulls[1].CountedDrawsSincePaidSixStar);
        Assert.True(analysis.SixStarPulls[1].IsFree);
        Assert.Equal("Operator A", analysis.SixStarPulls[2].OperatorName);
        Assert.Equal(2, analysis.SixStarPulls[2].PoolDrawNumber);
        Assert.Equal(2, analysis.Pools.Count);
        var limited = Assert.Single(analysis.Pools, pool => pool.PoolId == "limited");
        Assert.Equal(5, limited.DrawCount);
        Assert.Equal(3, limited.PaidDrawCount);
        Assert.Equal(2, limited.FreeDrawCount);
        Assert.Equal(2, limited.SixStarPulls.Count);
        Assert.Contains("Operator B", limited.OperatorSummary);
        Assert.All(limited.SixStarPulls, item => Assert.InRange(item.BarPercent, 0, 100));
    }

    [Fact]
    public async Task Analysis_FreeTenDoesNotInflateRecordedSixStarInterval()
    {
        var input = Path.Combine(_root, "free-ten-interval.json");
        var records = Enumerable.Range(1, 130).Select(position => new
        {
            seqId = position,
            gachaTs = 1700000000000L + position * 1000L,
            kind = "draw",
            poolId = "winter",
            poolName = "冬猎",
            rarity = position is 84 or 130 ? 6 : 4,
            charName = position == 84 ? "黎风" : position == 130 ? "提弗洛斯" : "四星",
            isFree = position is >= 31 and <= 40
        });
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new { characters = records, weapons = Array.Empty<object>() }));
        var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance);

        Assert.True((await service.ImportAsync(input)).Success);
        var pool = Assert.Single((await service.GetAnalysisAsync()).Pools);
        var earlier = Assert.Single(pool.SixStarPulls, item => item.OperatorName == "黎风");
        var later = Assert.Single(pool.SixStarPulls, item => item.OperatorName == "提弗洛斯");

        Assert.Equal(10, pool.FreeDrawCount);
        Assert.Equal(84, earlier.PoolDrawNumber);
        Assert.Equal(74, earlier.CountedDrawsSincePaidSixStar);
        Assert.Equal("74 抽", earlier.RecordedSpanText);
        Assert.Equal(92.5, earlier.BarPercent);
        Assert.True(earlier.IsOver65);
        Assert.Equal(46, later.CountedDrawsSincePaidSixStar);
        Assert.Equal(57.5, later.BarPercent);
        Assert.False(later.IsOver65);
    }

    [Fact]
    public async Task Analysis_WithoutArchiveIsEmpty()
    {
        var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance);

        var analysis = await service.GetAnalysisAsync();

        Assert.Equal(0, analysis.CharacterDrawCount);
        Assert.Empty(analysis.SixStarPulls);
        Assert.Empty(analysis.Pools);
        Assert.Equal("—", analysis.Pity.StandardText);
    }

    [Fact]
    public async Task Analysis_SeparatesStandardAndCharteredCountersAndKeepsStandardLast()
    {
        var input = Path.Combine(_root, "pity-families.json");
        await File.WriteAllTextAsync(input, """
        {"characters":[
          {"seqId":1,"gachaTs":1700000001000,"kind":"draw","poolId":"standard","poolName":"基础寻访","rarity":4},
          {"seqId":2,"gachaTs":1700000002000,"kind":"draw","poolId":"special_a","poolName":"逐罪者","rarity":4},
          {"seqId":3,"gachaTs":1700000003000,"kind":"draw","poolId":"special_a","poolName":"逐罪者","rarity":6,"charName":"黎风","isFree":true},
          {"seqId":4,"gachaTs":1700000004000,"kind":"draw","poolId":"standard","poolName":"基础寻访","rarity":6,"charName":"余烬"},
          {"seqId":5,"gachaTs":1700000005000,"kind":"draw","poolId":"special_a","poolName":"逐罪者","rarity":4},
          {"seqId":6,"gachaTs":1700000006000,"kind":"draw","poolId":"standard","poolName":"基础寻访","rarity":4},
          {"seqId":7,"gachaTs":1700000007000,"kind":"draw","poolId":"special_b","poolName":"临渊望北","rarity":4},
          {"seqId":8,"gachaTs":1700000008000,"kind":"draw","poolId":"event","poolName":"辉光庆典","rarity":4}
        ],"weapons":[]}
        """);
        var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance);

        Assert.True((await service.ImportAsync(input)).Success);
        var analysis = await service.GetAnalysisAsync();

        Assert.Equal(1, analysis.Pity.StandardSinceSixStar);
        Assert.Equal(3, analysis.Pity.LimitedSinceSixStar);
        Assert.Equal("特许", analysis.Pity.LimitedFamily);
        Assert.Equal("临渊望北", analysis.Pity.UpPoolName);
        Assert.Equal(119, analysis.Pity.UpRemaining);
        Assert.Equal("基础寻访", analysis.Pools[^1].PoolName);
        var first = Assert.Single(analysis.Pools, pool => pool.PoolName == "逐罪者");
        Assert.Equal(1, first.FreeSixStarCount);
        Assert.Equal(2, first.PaidDrawCount);
        Assert.Equal(EndfieldPoolCategory.Celebration,
            Assert.Single(analysis.Pools, pool => pool.PoolName == "辉光庆典").Category);
    }

    [Fact]
    public async Task Analysis_RecognizesPublisherAnnouncedPoolsSinceLaunch()
    {
        var announced = new (string Pool, string Operator)[]
        {
            ("熔火灼痕", "莱万汀"), ("轻飘飘的信使", "洁尔佩塔"),
            ("热烈色彩", "伊冯"), ("河流的女儿", "汤汤"),
            ("狼珀", "洛茜"), ("春雷动，万物生", "庄方宜"),
            ("拳出无悔", "弭弗"), ("逐罪者", "卡缪"),
            ("临渊望北", "诀"), ("晨星于此闪耀", "梨诺"),
            ("冬猎", "提弗洛斯")
        };
        var input = Path.Combine(_root, "announced-pools.json");
        var records = announced.Select((entry, index) => new
        {
            seqId = index + 1,
            gachaTs = 1700000001000L + index * 1000L,
            kind = "draw",
            poolId = $"special_{index}",
            poolName = entry.Pool,
            rarity = 6,
            charName = entry.Operator
        });
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new { characters = records, weapons = Array.Empty<object>() }));
        var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance);

        Assert.True((await service.ImportAsync(input)).Success);
        var analysis = await service.GetAnalysisAsync();

        Assert.Equal(announced.Length, analysis.Pools.Count);
        Assert.Equal(announced.Length, analysis.Pools.Select(pool => pool.BannerAssetFileName).Distinct().Count());
        Assert.All(analysis.Pools, pool =>
        {
            Assert.Equal(EndfieldPoolCategory.Chartered, pool.Category);
            Assert.StartsWith("Gacha/", pool.BannerAssetFileName);
            Assert.True(Assert.Single(pool.SixStarPulls).IsFeatured);
            Assert.Equal(1, pool.FeaturedPaidDrawNumber);
        });
    }

    [Fact]
    public async Task Analysis_RefactorSharesItsSeriesCountButKeepsPoolResultsSeparate()
    {
        var input = Path.Combine(_root, "refactor-series.json");
        await File.WriteAllTextAsync(input, """
        {"characters":[
          {"seqId":1,"gachaTs":1700000001000,"kind":"draw","poolId":"refactor_1","poolName":"绚丽异彩#1","rarity":4},
          {"seqId":2,"gachaTs":1700000002000,"kind":"draw","poolId":"refactor_1","poolName":"绚丽异彩#1","rarity":6,"charName":"伊冯","isFree":true},
          {"seqId":3,"gachaTs":1700000003000,"kind":"draw","poolId":"refactor_2","poolName":"绚丽异彩#2","rarity":4},
          {"seqId":4,"gachaTs":1700000004000,"kind":"draw","poolId":"refactor_2","poolName":"绚丽异彩#2","rarity":6,"charName":"伊冯"}
        ],"weapons":[]}
        """);
        var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance);

        Assert.True((await service.ImportAsync(input)).Success);
        var analysis = await service.GetAnalysisAsync();
        var first = Assert.Single(analysis.Pools, pool => pool.PoolName == "绚丽异彩#1");
        var second = Assert.Single(analysis.Pools, pool => pool.PoolName == "绚丽异彩#2");

        Assert.Equal(EndfieldPoolCategory.Refactor, first.Category);
        Assert.Equal(1, first.FreeDrawCount);
        Assert.Equal(1, first.FreeSixStarCount);
        Assert.True(first.HasFreeFeatured);
        Assert.Null(first.FeaturedPaidDrawNumber);
        Assert.Equal(3, second.FeaturedPaidDrawNumber);
        Assert.Equal("重构", analysis.Pity.LimitedFamily);
        Assert.Equal(0, analysis.Pity.LimitedSinceSixStar);
        Assert.True(analysis.Pity.UpRecorded);
        Assert.Equal("已出", analysis.Pity.UpText);
    }

    [Fact]
    public async Task Analysis_MarksOnlyVerifiedFeaturedOperatorAndKeepsFreeDrawSeparate()
    {
        var input = Path.Combine(_root, "featured-analysis.json");
        await File.WriteAllTextAsync(input, """
        {"characters":[
          {"seqId":1,"gachaTs":1700000001000,"kind":"draw","poolId":"special_1_4_1","poolName":"临渊望北","rarity":4},
          {"seqId":2,"gachaTs":1700000002000,"kind":"draw","poolId":"special_1_4_1","poolName":"临渊望北","rarity":6,"charName":"诀","isFree":true},
          {"seqId":3,"gachaTs":1700000003000,"kind":"draw","poolId":"special_1_4_1","poolName":"临渊望北","rarity":6,"charName":"余烬"},
          {"seqId":4,"gachaTs":1700000004000,"kind":"draw","poolId":"special_unknown","poolName":"未知特许池","rarity":6,"charName":"诀"},
          {"seqId":5,"gachaTs":1700000005000,"kind":"draw","poolId":"special_1_4_1","poolName":"临渊望北","rarity":6,"charName":"诀"}
        ],"weapons":[]}
        """);
        var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance);

        Assert.True((await service.ImportAsync(input)).Success);
        var analysis = await service.GetAnalysisAsync();
        var known = Assert.Single(analysis.Pools, pool => pool.PoolName == "临渊望北");

        Assert.Equal("诀", known.FeaturedOperatorName);
        Assert.Equal(3, known.PaidDrawCount);
        Assert.Equal(1, known.FreeDrawCount);
        Assert.Equal(3, known.FeaturedPaidDrawNumber);
        Assert.True(known.HasFreeFeatured);
        Assert.Equal("诀", known.SixStarPulls[0].OperatorName);
        Assert.True(known.SixStarPulls[0].IsFeatured);
        Assert.False(known.SixStarPulls[0].IsFree);
        Assert.Equal("余烬", known.SixStarPulls[1].OperatorName);
        Assert.False(known.SixStarPulls[1].IsFeatured);
        Assert.True(known.SixStarPulls[2].IsFeatured);
        Assert.True(known.SixStarPulls[2].IsFree);
        Assert.False(Assert.Single(analysis.Pools, pool => pool.PoolName == "未知特许池").SixStarPulls[0].IsFeatured);
    }

    [Fact]
    public async Task CaptureFromGame_UsesOnlyValidatedOfficialUrlsAndMergesRecords()
    {
        var cache = Path.Combine(_root, "data_1");
        await File.WriteAllTextAsync(cache, """
        cache-prefix
        https://ef-webview.hypergryph.com/api/record/char?lang=zh-cn&pool_type=standard&token=test-token&server_id=1
        https://ef-webview.hypergryph.com/api/record/weapon?lang=zh-cn&pool_id=weapon-pool&token=test-token&server_id=1
        """);
        var handler = new EndfieldRecordHandler();
        using var client = new HttpClient(handler);
        using var service = new EndfieldGachaArchiveService(
            NullLogger<EndfieldGachaArchiveService>.Instance,
            client,
            () => cache);

        var result = await service.CaptureFromGameAsync();
        var summary = await service.GetSummaryAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, result.ImportedCount);
        Assert.Equal(2, summary.CharacterCount);
        Assert.Equal(1, summary.WeaponCount);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, uri => Assert.Equal("ef-webview.hypergryph.com", uri.Host));
        Assert.All(handler.Requests, uri => Assert.DoesNotContain("seq_id=", uri.Query));
        Assert.DoesNotContain("private-token", await File.ReadAllTextAsync(service.ArchivePath));
    }

    [Fact]
    public async Task CaptureFromGame_ReportsRejectedTokenWithoutEchoingResponse()
    {
        var cache = Path.Combine(_root, "data_1");
        await File.WriteAllTextAsync(cache, "https://ef-webview.hypergryph.com/api/record/char?token=private-token&server_id=1&pool_type=standard");
        using var client = new HttpClient(new StaticRecordHandler("""{"code":401,"message":"private-token"}"""));
        using var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance, client, () => cache);

        var result = await service.CaptureFromGameAsync();

        Assert.False(result.Success);
        Assert.Contains("401", result.Message);
        Assert.DoesNotContain("private-token", result.Message);
    }

    [Fact]
    public async Task CaptureFromGame_KeepsValidGroupsWhenAnotherGroupIsRejected()
    {
        var cache = Path.Combine(_root, "data_1");
        await File.WriteAllTextAsync(cache, """
        https://ef-webview.hypergryph.com/api/record/char?token=private-token&server_id=1&pool_type=standard
        https://ef-webview.hypergryph.com/api/record/weapon?token=private-token&server_id=1&pool_id=weapon-pool
        """);
        using var client = new HttpClient(new PartialRecordHandler());
        using var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance, client, () => cache);

        var result = await service.CaptureFromGameAsync();
        var summary = await service.GetSummaryAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, summary.CharacterCount);
        Assert.Contains("1 个记录分组失败", result.Message);
        Assert.DoesNotContain("private-token", result.Message);
    }

    [Fact]
    public async Task CaptureFromGame_UsesNewestSessionTokenAcrossCachedPools()
    {
        var cache = Path.Combine(_root, "data_1");
        await File.WriteAllTextAsync(cache, """
        https://ef-webview.hypergryph.com/api/record/char?token=old-test-token&server_id=1&pool_type=standard&seq_id=999
        https://ef-webview.hypergryph.com/api/record/weapon?token=old-test-token&server_id=1&pool_id=weapon-pool
        https://ef-webview.hypergryph.com/api/record/char?token=new-test-token&server_id=1&pool_type=limited
        """);
        var handler = new LatestSessionRecordHandler();
        using var client = new HttpClient(handler);
        using var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance, client, () => cache);

        var result = await service.CaptureFromGameAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, uri => Assert.Contains("token=new-test-token", uri.Query));
        Assert.All(handler.Requests, uri => Assert.DoesNotContain("seq_id=", uri.Query));
    }

    [Fact]
    public async Task CaptureFromGame_PaginatesFiveRecordPagesWithoutHasMore()
    {
        var cache = Path.Combine(_root, "data_1");
        await File.WriteAllTextAsync(cache, "https://ef-webview.hypergryph.com/api/record/char?token=test-token&server_id=1&pool_type=standard");
        var handler = new PagedRecordHandler();
        using var client = new HttpClient(handler);
        using var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance, client, () => cache);

        var result = await service.CaptureFromGameAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(6, result.ImportedCount);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("seq_id=5", handler.Requests[1].Query);
    }

    [Fact]
    public async Task CaptureFromGame_ReportsTlsHandshakeFailurePrecisely()
    {
        var cache = Path.Combine(_root, "data_1");
        await File.WriteAllTextAsync(cache, "https://ef-webview.hypergryph.com/api/record/char?token=test-token&server_id=1&pool_type=standard");
        using var client = new HttpClient(new TlsFailureHandler());
        using var service = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance, client, () => cache);

        var result = await service.CaptureFromGameAsync();

        Assert.False(result.Success);
        Assert.Contains("TLS 握手失败", result.Message);
        Assert.DoesNotContain("test-token", result.Message);
    }

    private sealed class TlsFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("TLS error", new AuthenticationException("No credentials"));
    }

    private sealed class StaticRecordHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private sealed class PartialRecordHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/char", StringComparison.OrdinalIgnoreCase)
                ? """{"data":{"records":[{"seqId":1,"name":"Operator A"}]}}"""
                : """{"code":401,"message":"private-token"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class LatestSessionRecordHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = Assert.IsType<Uri>(request.RequestUri);
            Requests.Add(uri);
            var body = uri.Query.Contains("token=new-test-token", StringComparison.Ordinal)
                ? """{"data":{"list":[{"seqId":1}]}}"""
                : """{"code":40100}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class PagedRecordHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = Assert.IsType<Uri>(request.RequestUri);
            Requests.Add(uri);
            var body = !uri.Query.Contains("seq_id=", StringComparison.Ordinal)
                ? """{"data":{"list":[{"seqId":1},{"seqId":2},{"seqId":3},{"seqId":4},{"seqId":5}]}}"""
                : """{"data":{"list":[{"seqId":6}]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class EndfieldRecordHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = Assert.IsType<Uri>(request.RequestUri);
            Requests.Add(uri);
            var body = uri.AbsolutePath.EndsWith("/char", StringComparison.OrdinalIgnoreCase)
                ? """{"data":{"list":[{"seqId":101,"name":"Operator A","token":"private-token","nested":{"authUrl":"https://ef-webview.hypergryph.com/api/record/char?token=private-token&server_id=1"}},{"seqId":102,"name":"Operator B"}],"hasMore":false}}"""
                : """{"data":{"list":[{"seqId":201,"name":"Weapon A"}],"hasMore":false}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", string.IsNullOrEmpty(_originalDataHome) ? null : _originalDataHome);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
