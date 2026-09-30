using AsterLauncher.Infrastructure;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsterLauncher.Core.Tests;

[Collection("Environment variables")]
public sealed class UigfArchiveServiceTests : IDisposable
{
    private readonly string _originalDataHome = Environment.GetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME") ?? string.Empty;
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? AppContext.BaseDirectory,
        "AsterLauncher.Tests",
        Guid.NewGuid().ToString("N"));

    public UigfArchiveServiceTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _root);
    }

    [Fact]
    public async Task Import_NumericUidAndStringUid_MergeWithoutDuplicatingAccount()
    {
        var input = Path.Combine(_root, "numeric-uid.json");
        using var service = new UigfArchiveService(NullLogger<UigfArchiveService>.Instance);
        await File.WriteAllTextAsync(input, """{"info":{"version":"v4.2"},"hk4e":[{"uid":100000001,"list":[{"id":"1","gacha_type":"301","rank_type":"5","name":"迪卢克"}]}]}""");
        Assert.True((await service.ImportAsync(input)).Success);
        await File.WriteAllTextAsync(input, """{"info":{"version":"v4.2"},"hk4e":[{"uid":"100000001","list":[{"id":"1","gacha_type":"301","rank_type":"5","name":"迪卢克"},{"id":"2","gacha_type":"400","rank_type":"3"}]}]}""");
        var merged = await service.ImportAsync(input);
        Assert.True(merged.Success, merged.Message);
        Assert.Equal(1, merged.ImportedCount);
        var account = Assert.Single((await service.GetAnalysisAsync("genshin-impact")).Accounts);
        Assert.Equal("100000001", account.Uid);
        Assert.Equal(2, account.RecordCount);
        Assert.Single(account.Sections);
    }
    [Fact]
    public async Task ImportAndExport_UigfV4_PreservesAllSupportedGames()
    {
        var input = Path.Combine(_root, "input.json");
        await File.WriteAllTextAsync(input, """
        {
          "info": { "export_timestamp": 1, "export_app": "test", "export_app_version": "1", "version": "v4.2" },
          "hk4e": [{ "uid": "100000001", "timezone": 8, "lang": "zh-cn", "list": [{ "id": "1", "uid": "100000001", "gacha_type": "301" }] }],
          "hkrpg": [{ "uid": "100000002", "timezone": 8, "lang": "zh-cn", "list": [{ "id": "2", "uid": "100000002", "gacha_type": "11" }] }],
          "nap": [{ "uid": "100000003", "timezone": 8, "lang": "zh-cn", "list": [{ "id": "3", "uid": "100000003", "gacha_type": "1" }] }]
        }
        """);

        using var service = new UigfArchiveService(NullLogger<UigfArchiveService>.Instance);
        var import = await service.ImportAsync(input);
        var summary = await service.GetSummaryAsync();
        var output = Path.Combine(_root, "export.json");
        var export = await service.ExportAsync(output);

        Assert.True(import.Success, import.Message);
        Assert.Equal(3, import.ImportedCount);
        Assert.Equal(3, summary.TotalCount);
        Assert.Equal("v4.2", summary.Version);
        Assert.True(export.Success, export.Message);
        Assert.True(File.Exists(output));
        Assert.Contains("\"export_app\": \"AsterLauncher\"", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Import_RejectsLegacyUigfVersion()
    {
        var input = Path.Combine(_root, "legacy.json");
        await File.WriteAllTextAsync(input, """{ "info": { "version": "v3.0" }, "hk4e": [] }""");
        using var service = new UigfArchiveService(NullLogger<UigfArchiveService>.Instance);

        var result = await service.ImportAsync(input);

        Assert.False(result.Success);
        Assert.Contains("v4", result.Message);
    }

    [Fact]
    public async Task StarRailCapture_UsesWorkingGachaEndpointForAllSixTypes()
    {
        var gameDirectory = Path.Combine(_root, "StarRail");
        var cacheDirectory = Path.Combine(gameDirectory, "StarRail_Data", "webCaches", "1", "Cache", "Cache_Data");
        Directory.CreateDirectory(cacheDirectory);
        var executable = Path.Combine(gameDirectory, "StarRail.exe");
        await File.WriteAllTextAsync(executable, "");
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "data_2"),
            "https://webstatic.mihoyo.com/hkrpg/event/e20211215gacha-v2/index.html?lang=zh-cn");

        var requestedTypes = new HashSet<string>();
        using var client = new HttpClient(new StarRailHandler(requestedTypes));
        using var service = new UigfArchiveService(NullLogger<UigfArchiveService>.Instance, client);

        var result = await service.CaptureFromGameAsync("honkai-star-rail", executable);
        var summary = await service.GetSummaryAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(6, summary.StarRailCount);
        Assert.Equal(new[] { "1", "2", "11", "12", "21", "22" }, requestedTypes.OrderBy(value => int.Parse(value)));
    }

    [Fact]
    public async Task StarRailCapture_KeepsOtherPoolsWhenOptionalTypesReturnMinus110()
    {
        var gameDirectory = Path.Combine(_root, "StarRailOptional");
        var cacheDirectory = Path.Combine(gameDirectory, "StarRail_Data", "webCaches", "1", "Cache", "Cache_Data");
        Directory.CreateDirectory(cacheDirectory);
        var executable = Path.Combine(gameDirectory, "StarRail.exe");
        await File.WriteAllTextAsync(executable, "");
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "data_2"),
            "https://webstatic.mihoyo.com/hkrpg/event/e20211215gacha-v2/index.html?authkey=test-key&lang=zh-cn");

        var handler = new StarRailOptionalRejectedHandler();
        using var client = new HttpClient(handler);
        using var service = new UigfArchiveService(NullLogger<UigfArchiveService>.Instance, client);

        var result = await service.CaptureFromGameAsync("honkai-star-rail", executable);
        var summary = await service.GetSummaryAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(4, result.ImportedCount);
        Assert.Equal(4, summary.StarRailCount);
        Assert.Contains("21、22", result.Message);
        Assert.Equal(2, handler.Type11FirstPageRequests);
        Assert.DoesNotContain("test-key", await File.ReadAllTextAsync(service.ArchivePath));
    }

    [Fact]
    public async Task StarRailAnalysis_CarriesFiveStarIntervalsAcrossBanners_ButSeparatesTypesAndAccounts()
    {
        var input = Path.Combine(_root, "analysis.json");
        await File.WriteAllTextAsync(input, """
        {
          "info": { "version": "v4.2" },
          "hkrpg": [
            { "uid": "100000001", "list": [
              { "id": "1", "gacha_id": "2001", "gacha_type": "11", "time": "2026-01-01 00:00:01", "rank_type": "3" },
              { "id": "2", "gacha_id": "2001", "gacha_type": "11", "time": "2026-01-01 00:00:02", "rank_type": "5", "name": "角色甲" },
              { "id": "9", "gacha_id": "2002", "gacha_type": "11", "time": "2026-02-01 00:00:01", "rank_type": "3" },
              { "id": "10", "gacha_id": "2002", "gacha_type": "11", "time": "2026-02-01 00:00:01", "rank_type": "5", "name": "角色乙" },
              { "id": "5", "gacha_id": "3001", "gacha_type": "12", "time": "2026-02-01 00:00:03", "rank_type": "3" },
              { "id": "6", "gacha_id": "1001", "gacha_type": "1", "time": "2026-02-01 00:00:04", "rank_type": "3" }
            ] },
            { "uid": "100000002", "list": [
              { "id": "7", "gacha_id": "2001", "gacha_type": "11", "time": "2026-02-01 00:00:05", "rank_type": "3" }
            ] }
          ]
        }
        """);
        using var service = new UigfArchiveService(NullLogger<UigfArchiveService>.Instance);
        Assert.True((await service.ImportAsync(input)).Success);

        var analysis = await service.GetStarRailAnalysisAsync();

        Assert.Equal(2, analysis.Accounts.Count);
        var first = analysis.Accounts.Single(account => account.Uid == "100000001");
        var roles = first.Sections.Single(section => section.Type == "11");
        var cones = first.Sections.Single(section => section.Type == "12");
        var standard = first.Sections.Single(section => section.Type == "1");
        Assert.Equal(0, roles.PityCount);
        Assert.Equal(4, roles.RecordCount);
        Assert.Equal(2, roles.FiveStarCount);
        Assert.Equal(0, roles.FourStarCount);
        Assert.Equal(2, roles.ThreeStarCount);
        Assert.Equal("2026/01/01 — 2026/02/01", roles.DateRange);
        Assert.Equal(new[] { "角色乙", "角色甲" }, roles.FiveStars.Select(pull => pull.Name));
        Assert.Equal("均抽 2.0", roles.AverageText);
        Assert.Equal(2, roles.Banners.Count);
        Assert.Equal("2002", roles.Banners[0].PoolId);
        Assert.Equal("角色乙", roles.Banners[0].FiveStars[0].Name);
        Assert.Equal(2, roles.Banners[0].FiveStars[0].LocalPullCount);
        Assert.True(roles.Banners[0].FiveStars[0].HasPreviousFiveStar);
        Assert.False(roles.Banners[1].FiveStars[0].HasPreviousFiveStar);
        Assert.Equal(1, cones.PityCount);
        Assert.Equal(1, standard.PityCount);
        Assert.Equal(1, analysis.Accounts.Single(account => account.Uid == "100000002")
            .Sections.Single(section => section.Type == "11").PityCount);
    }
    private sealed class StarRailOptionalRejectedHandler : HttpMessageHandler
    {
        public int Type11FirstPageRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2))
                .ToDictionary(part => part[0], part => part[1]);
            var type = query["gacha_type"];
            if (type == "11" && query["end_id"] == "0")
            {
                Type11FirstPageRequests++;
            }
            var body = type is "21" or "22" || (type == "11" && Type11FirstPageRequests == 1 && query["end_id"] == "0")
                ? """{"retcode":-110,"message":"test-key"}"""
                : query["end_id"] == "0"
                    ? $"{{\"retcode\":0,\"data\":{{\"list\":[{{\"id\":\"{type}\",\"uid\":\"100000001\",\"gacha_type\":\"{type}\"}}]}}}}"
                    : """{"retcode":0,"data":{"list":[]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StarRailHandler(HashSet<string> requestedTypes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Assert.EndsWith("/getGachaLog", uri.AbsolutePath);
            Assert.Equal("public-operation-hkrpg.mihoyo.com", uri.Host);
            var query = uri.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2))
                .ToDictionary(part => part[0], part => part[1]);
            var type = query["gacha_type"];
            requestedTypes.Add(type);
            var list = query["end_id"] == "0"
                ? $"[{{\"id\":\"{type}\",\"uid\":\"100000001\",\"gacha_type\":\"{type}\"}}]"
                : "[]";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"retcode\":0,\"data\":{{\"list\":{list}}}}}",
                    Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
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

[CollectionDefinition("Environment variables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection;
