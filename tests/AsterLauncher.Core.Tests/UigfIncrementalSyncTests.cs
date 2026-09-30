using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AsterLauncher.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsterLauncher.Core.Tests;

[Collection("Environment variables")]
public sealed class UigfIncrementalSyncTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME");
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!,
        "AsterLauncher.Tests", Guid.NewGuid().ToString("N"));

    public UigfIncrementalSyncTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _root);
    }

    [Theory]
    [InlineData("genshin-impact", "hk4e", "YuanShen", "301", "400", false, 1)]
    [InlineData("genshin-impact", "hk4e", "YuanShen", "301", "400", true, 3)]
    [InlineData("zenless-zone-zero", "nap", "ZenlessZoneZero", "2", "2", false, 1)]
    [InlineData("zenless-zone-zero", "nap", "ZenlessZoneZero", "2", "2", true, 3)]
    [InlineData("zenless-zone-zero", "nap", "ZenlessZoneZero", "102", "102", false, 1)]
    public async Task Sync_UsesGameAccountAndCanonicalTypeBoundary_AndFinishesWholePage(
        string game, string key, string exeName, string requestType, string returnedType, bool full, int pages)
    {
        using var handler = new HistoryHandler(requestType, returnedType);
        using var client = new HttpClient(handler);
        using var service = new UigfArchiveService(NullLogger<UigfArchiveService>.Instance, client);
        var source = Path.Combine(_root, "seed.json");
        await File.WriteAllTextAsync(source, new JsonObject {
            ["info"] = new JsonObject { ["version"] = "v4.2" },
            [key] = new JsonArray(new JsonObject { ["uid"] = "100000001",
                ["list"] = new JsonArray(Record("200", returnedType)) })
        }.ToJsonString());
        Assert.True((await service.ImportAsync(source)).Success);
        var dir = Path.Combine(_root, "game");
        var cache = Path.Combine(dir, exeName + "_Data", "webCaches");
        Directory.CreateDirectory(cache);
        var executable = Path.Combine(dir, exeName + ".exe");
        await File.WriteAllTextAsync(executable, "");
        var prefix = key == "hk4e" ? "hk4e/event/e20190909gacha" : "nap/event/e20230424gacha";
        await File.WriteAllTextAsync(Path.Combine(cache, "data_2"),
            "https://webstatic.mihoyo.com/" + prefix + "/index.html?authkey=fixture-secret&lang=zh-cn");

        var result = await service.CaptureFromGameAsync(game, executable, fullRefresh: full);
        Assert.True(result.Success, result.Message);
        Assert.Equal(pages, handler.MainPages);
        var archive = await File.ReadAllTextAsync(service.ArchivePath);
        Assert.Contains("\"150\"", archive);
        Assert.Equal(full, archive.Contains("\"100\""));
        Assert.DoesNotContain("fixture-secret", archive);
        Assert.Equal(2, handler.OtherPages); // A pool without a checkpoint completes its scan.
        var repeated = await service.CaptureFromGameAsync(game, executable);
        Assert.True(repeated.Success, repeated.Message);
        Assert.Equal(0, repeated.ImportedCount);
    }

    private static JsonObject Record(string id, string type) => new() {
        ["id"] = id, ["uid"] = "100000001", ["gacha_type"] = type, ["time"] = "2026-01-01 00:00:00"
    };

    private sealed class HistoryHandler(string mainType, string returnedType) : HttpMessageHandler
    {
        public int MainPages { get; private set; }
        public int OtherPages { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var q = request.RequestUri!.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);
            var type = q["gacha_type"]; var end = q["end_id"];
            var other = mainType == "301" ? "302" : "3";
            if (type == mainType) MainPages++;
            if (type == other) OtherPages++;
            JsonArray list = type == mainType ? end switch {
                "0" => new(Record("300", returnedType), Record("200", returnedType), Record("150", returnedType)),
                "150" => new(Record("100", returnedType)), _ => new()
            } : type == other && end == "0" ? new(Record("400", other)) : new();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(new JsonObject { ["retcode"] = 0,
                    ["data"] = new JsonObject { ["list"] = list } }.ToJsonString(), Encoding.UTF8, "application/json")
            });
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _original);
        Directory.Delete(_root, true);
    }
}