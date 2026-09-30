using AsterLauncher.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace AsterLauncher.Core.Tests;

[Collection("Environment variables")]
public sealed class StarRailIncrementalSyncTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME");
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "AsterLauncher.Tests", Guid.NewGuid().ToString("N"));

    public StarRailIncrementalSyncTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _root);
    }

    [Theory]
    [InlineData(false, 1, 3, 4)]
    [InlineData(true, 3, 4, 5)]
    public async Task Sync_StopsAfterKnownPage_WhileFullSyncFillsOlderHistory(
        bool fullRefresh, int pages, int imported, int total)
    {
        using var handler = new HistoryHandler();
        using var client = new HttpClient(handler);
        using var service = new UigfArchiveService(NullLogger<UigfArchiveService>.Instance, client);
        await SeedAsync(service, "100000001", "11");
        var result = await service.CaptureFromGameAsync("honkai-star-rail", await GameAsync(), fullRefresh: fullRefresh);

        Assert.True(result.Success, result.Message);
        Assert.Equal(pages, handler.Type11Pages);
        Assert.Equal(imported, result.ImportedCount);
        Assert.Equal(total, (await service.GetSummaryAsync()).StarRailCount);
        Assert.Equal(2, handler.Type12Pages); // No checkpoint for this pool: scan through its empty page.
        var archive = await File.ReadAllTextAsync(service.ArchivePath);
        Assert.DoesNotContain("test-secret", archive);
        Assert.Contains("\"150\"", archive); // Finish the whole boundary page, including this historical gap.
        Assert.Equal(fullRefresh, archive.Contains("\"100\""));

        var repeated = await service.CaptureFromGameAsync("honkai-star-rail", await GameAsync());
        Assert.True(repeated.Success, repeated.Message);
        Assert.Equal(0, repeated.ImportedCount);
        Assert.Equal(total, (await service.GetSummaryAsync()).StarRailCount);
    }

    [Theory]
    [InlineData("900000002", "11")] // Same ID in another account.
    [InlineData("100000001", "1")]  // Same ID classified under another pool.
    public async Task Sync_DoesNotUseAnotherAccountOrPoolAsCheckpoint(string uid, string type)
    {
        using var handler = new HistoryHandler();
        using var client = new HttpClient(handler);
        using var service = new UigfArchiveService(NullLogger<UigfArchiveService>.Instance, client);
        await SeedAsync(service, uid, type);

        var result = await service.CaptureFromGameAsync("honkai-star-rail", await GameAsync());

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, handler.Type11Pages);
        Assert.Contains("\"100\"", await File.ReadAllTextAsync(service.ArchivePath));
    }

    [Fact]
    public async Task FullSync_LateFailureLeavesExistingArchiveUnchanged()
    {
        using var handler = new HistoryHandler { FailOlderPage = true };
        using var client = new HttpClient(handler);
        using var service = new UigfArchiveService(NullLogger<UigfArchiveService>.Instance, client);
        await SeedAsync(service, "100000001", "11");
        var original = await File.ReadAllBytesAsync(service.ArchivePath);

        var result = await service.CaptureFromGameAsync("honkai-star-rail", await GameAsync(), fullRefresh: true);

        Assert.False(result.Success);
        Assert.DoesNotContain("test-secret", result.Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(service.ArchivePath));
    }

    private async Task SeedAsync(UigfArchiveService service, string uid, string type)
    {
        var record = Record("200", type, uid);
        var root = new JsonObject
        {
            ["info"] = new JsonObject { ["version"] = "v4.2" },
            ["hkrpg"] = new JsonArray(new JsonObject { ["uid"] = uid, ["list"] = new JsonArray(record) })
        };
        var path = Path.Combine(_root, "seed.json");
        await File.WriteAllTextAsync(path, root.ToJsonString());
        Assert.True((await service.ImportAsync(path)).Success);
    }

    private async Task<string> GameAsync()
    {
        var game = Path.Combine(_root, "StarRail");
        var cache = Path.Combine(game, "StarRail_Data", "webCaches", "1", "Cache", "Cache_Data");
        Directory.CreateDirectory(cache);
        var executable = Path.Combine(game, "StarRail.exe");
        await File.WriteAllTextAsync(executable, "");
        await File.WriteAllTextAsync(Path.Combine(cache, "data_2"),
            "https://webstatic.mihoyo.com/hkrpg/event/e20211215gacha-v2/index.html?authkey=test-secret&lang=zh-cn");
        return executable;
    }

    private static JsonObject Record(string id, string type, string uid = "100000001") =>
        new() { ["id"] = id, ["uid"] = uid, ["gacha_type"] = type, ["time"] = "2026-01-01 00:00:00" };

    private sealed class HistoryHandler : HttpMessageHandler
    {
        public int Type11Pages { get; private set; }
        public int Type12Pages { get; private set; }
        public bool FailOlderPage { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var query = request.RequestUri!.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);
            var type = query["gacha_type"];
            var end = query["end_id"];
            if (type == "11") Type11Pages++;
            if (type == "12") Type12Pages++;
            if (FailOlderPage && type == "11" && end == "150")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            JsonArray list = (type, end) switch
            {
                ("11", "0") => new(Record("300", type), Record("200", type), Record("150", type)),
                ("11", "150") => new(Record("100", type)),
                ("12", "0") => new(Record("400", type)),
                _ => new()
            };
            var body = new JsonObject { ["retcode"] = 0, ["data"] = new JsonObject { ["list"] = list } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            });
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _original);
        Directory.Delete(_root, true);
    }
}