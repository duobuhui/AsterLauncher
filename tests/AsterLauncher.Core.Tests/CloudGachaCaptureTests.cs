using System.Net;
using System.Text.Json.Nodes;
using AsterLauncher.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsterLauncher.Core.Tests;

[Collection("Environment variables")]
public sealed class CloudGachaCaptureTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME");
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? AppContext.BaseDirectory,
        "AsterLauncher.Tests", Guid.NewGuid().ToString("N"));
    private const string Secret = "fixture-cloud-private-value";

    public CloudGachaCaptureTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _root);
    }

    [Theory]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com/hk4e/event/e20190909gacha/index.html?authkey=fixture-valid-authorization&game_biz=hk4e_cn&region=cn_gf01#/log")]
    [InlineData("honkai-star-rail", "https://webstatic.mihoyo.com/hkrpg/event/e20211215gacha/index.html?authkey=fixture-valid-authorization&game_biz=hkrpg_cn&region=prod_gf_cn")]
    [InlineData("zenless-zone-zero", "https://webstatic.mihoyo.com/nap/event/e20230424gacha/index.html?authkey=fixture-valid-authorization&game_biz=nap_cn&region=prod_gf_cn")]
    [InlineData("genshin-impact", "https://public-operation-hk4e.mihoyo.com/gacha_info/api/getGachaLog?authkey=fixture-valid-authorization")]
    [InlineData("honkai-star-rail", "https://public-operation-hkrpg.mihoyo.com/common/hkrpg_gacha_record/api/getGachaLog?authkey=fixture-valid-authorization")]
    [InlineData("zenless-zone-zero", "https://public-operation-nap.mihoyo.com/common/gacha_record/api/getGachaLog?authkey=fixture-valid-authorization")]
    [InlineData("genshin-impact", "https://gs.hoyoverse.com/genshin/event/e20190909gacha/index.html?authkey=fixture-valid-authorization&game_biz=hk4e_global")]
    [InlineData("honkai-star-rail", "https://gs.hoyoverse.com/hkrpg/event/e20211215gacha/index.html?authkey=fixture-valid-authorization&game_biz=hkrpg_global")]
    [InlineData("zenless-zone-zero", "https://gs.hoyoverse.com/nap/event/e20230424gacha/index.html?authkey=fixture-valid-authorization&game_biz=nap_global")]
    [InlineData("endfield", "https://ef-webview.hypergryph.com/api/record/char?token=fixture-valid-authorization&server_id=1&pool_type=standard")]
    [InlineData("endfield", "https://ef-webview.hypergryph.com/api/record/weapon?token=fixture-valid-authorization&server_id=1&pool_id=weapon_pool")]
    [InlineData("endfield", "https://ef-webview.gryphline.com/api/record/char?token=fixture-valid-authorization&server_id=3&pool_type=limited")]
    public void Validator_AcceptsOnlyKnownGameRecordLinks(string game, string url)
    {
        Assert.True(GachaHistoryUrlValidator.TryValidate(game, url, out var result));
        Assert.Equal("https", result.Scheme);
        Assert.Empty(result.Fragment);
    }

    [Theory]
    [InlineData("genshin-impact", "http://webstatic.mihoyo.com/hk4e/event/e20190909gacha/index.html?authkey=fixture-valid-authorization")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com.evil.test/hk4e/event/e20190909gacha/index.html?authkey=fixture-valid-authorization")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com@evil.test/hk4e/event/e20190909gacha/index.html?authkey=fixture-valid-authorization")]
    [InlineData("genshin-impact", "https://user@webstatic.mihoyo.com/hk4e/event/e20190909gacha/index.html?authkey=fixture-valid-authorization")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com:444/hk4e/event/e20190909gacha/index.html?authkey=fixture-valid-authorization")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com./hk4e/event/e20190909gacha/index.html?authkey=fixture-valid-authorization")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com/hk4e/event/e20190909gacha-evil/index.html?authkey=fixture-valid-authorization")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com/hk4e/event/e20190909gacha/index.html/evil?authkey=fixture-valid-authorization")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com/other/../hk4e/event/e20190909gacha/index.html?authkey=fixture-valid-authorization")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com/hk4e%2fevent/e20190909gacha/index.html?authkey=fixture-valid-authorization")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com/hkrpg/event/e20211215gacha/index.html?authkey=fixture-valid-authorization")]
    [InlineData("genshin-impact", "https://public-operation-nap.mihoyo.com/common/gacha_record/api/getGachaLog?authkey=fixture-valid-authorization")]
    [InlineData("honkai-star-rail", "https://webstatic.mihoyo.com/hkrpg/event/e20211215gacha/index.html?authkey=fixture-valid-authorization&game_biz=nap_cn")]
    [InlineData("zenless-zone-zero", "https://webstatic.mihoyo.com/nap/event/e20230424gacha/index.html?authkey=fixture-valid-authorization&region=cn_gf01")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com/hk4e/event/e20190909gacha/index.html?authkey=fixture-valid-authorization&game_biz=hk4e_global")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com/hk4e/event/e20190909gacha/index.html?authkey=fixture-valid-authorization&authkey=other")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com/hk4e/event/e20190909gacha/index.html?authkey=fixture-valid-authorization&%61uthkey=other")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com/hk4e/event/e20190909gacha/index.html?authkey=%ZZ")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com/hk4e/event/e20190909gacha/index.html?authkey=%0d%0a")]
    [InlineData("genshin-impact", "https://webstatic.mihoyo.com/hk4e/event/e20190909gacha/index.html?authkey=")]
    [InlineData("endfield", "https://ef-webview.hypergryph.com.evil.test/api/record/char?token=fixture-valid-authorization&server_id=1&pool_type=standard")]
    [InlineData("endfield", "https://ef-webview.hypergryph.com/api/record/char/other?token=fixture-valid-authorization&server_id=1&pool_type=standard")]
    [InlineData("endfield", "https://ef-webview.hypergryph.com/api/record/char?token=fixture-valid-authorization&server_id=1")]
    [InlineData("endfield", "https://ef-webview.hypergryph.com/api/record/weapon?token=fixture-valid-authorization&server_id=1&pool_type=standard")]
    [InlineData("endfield", "https://ef-webview.hypergryph.com/api/record/char?token=fixture-valid-authorization&server_id=1&pool_type=standard&game_biz=nap_cn")]
    [InlineData("endfield", "https://ef-webview.hypergryph.com/api/record/char?token=fixture-valid-authorization&server_id=1&pool_type=standard&token=other")]
    [InlineData("endfield", "https://ef-webview.hypergryph.com:444/api/record/char?token=fixture-valid-authorization&server_id=1&pool_type=standard")]
    [InlineData("endfield", "https://user@ef-webview.hypergryph.com/api/record/char?token=fixture-valid-authorization&server_id=1&pool_type=standard")]
    [InlineData("unknown", "https://webstatic.mihoyo.com/hk4e/event/e20190909gacha/index.html?authkey=fixture-valid-authorization")]
    public void Validator_RejectsUntrustedAndCrossGameUrls(string game, string url) =>
        Assert.False(GachaHistoryUrlValidator.TryValidate(game, url, out _));

    [Theory]
    [InlineData("genshin-impact", "hk4e", "hk4e/event/e20190909gacha", "301", false, 1)]
    [InlineData("genshin-impact", "hk4e", "hk4e/event/e20190909gacha", "301", true, 3)]
    [InlineData("honkai-star-rail", "hkrpg", "hkrpg/event/e20211215gacha", "11", false, 1)]
    [InlineData("honkai-star-rail", "hkrpg", "hkrpg/event/e20211215gacha", "11", true, 3)]
    [InlineData("zenless-zone-zero", "nap", "nap/event/e20230424gacha", "2", false, 1)]
    [InlineData("zenless-zone-zero", "nap", "nap/event/e20230424gacha", "2", true, 3)]
    public async Task Uigf_PastedUrlUsesExistingPaginationAndMergeWithoutGameCache(
        string game, string key, string path, string type, bool full, int expectedPages)
    {
        using var handler = new UigfHandler(type);
        using var client = new HttpClient(handler);
        var logger = new MemoryLogger<UigfArchiveService>();
        using var service = new UigfArchiveService(logger, client);
        Directory.CreateDirectory(Path.GetDirectoryName(service.ArchivePath)!);
        await File.WriteAllTextAsync(service.ArchivePath, new JsonObject {
            ["info"] = new JsonObject { ["version"] = "v4.2" },
            [key] = new JsonArray(new JsonObject { ["uid"] = "100000001", ["list"] = new JsonArray(Record("200", type)) })
        }.ToJsonString());
        var url = $"https://webstatic.mihoyo.com/{path}/index.html?authkey={Secret}&gacha_type=999&page=9&size=999&end_id=999";
        var result = await service.CaptureFromUrlAsync(game, url, fullRefresh: full);
        Assert.True(result.Success, result.Message);
        Assert.Equal(expectedPages, handler.MainPages);
        Assert.Equal(full ? 3 : 2, result.ImportedCount);
        var archive = await File.ReadAllTextAsync(service.ArchivePath);
        Assert.Contains("\"150\"", archive); // Finish the whole page containing the checkpoint.
        Assert.Equal(full, archive.Contains("\"100\""));
        Assert.DoesNotContain(Secret, archive);
        Assert.DoesNotContain("authkey", archive);
        Assert.DoesNotContain(Secret, string.Join("", logger.Messages));
        Assert.DoesNotContain(Secret, result.Message);
        Assert.All(Directory.GetFiles(_root, "*", SearchOption.AllDirectories),
            file => Assert.Equal(service.ArchivePath, file));
    }

    [Theory]
    [InlineData(false, 1, 2)]
    [InlineData(true, 2, 3)]
    public async Task Endfield_PastedUrlNeverUsesLocalCacheAndKeepsCheckpointMerge(bool full, int pages, int imported)
    {
        using var handler = new EndfieldHandler();
        using var client = new HttpClient(handler);
        using var service = new EndfieldGachaArchiveService(
            NullLogger<EndfieldGachaArchiveService>.Instance, client, () => throw new InvalidOperationException("must not read local cache"));
        Directory.CreateDirectory(Path.GetDirectoryName(service.ArchivePath)!);
        await File.WriteAllTextAsync(service.ArchivePath, """{"characters":[{"seqId":200,"poolId":"standard"}],"weapons":[]}""");
        var url = $"https://ef-webview.hypergryph.com/api/record/char?token={Secret}&server_id=1&pool_type=standard&seq_id=999";
        var result = await service.CaptureFromUrlAsync(url, fullRefresh: full);
        Assert.True(result.Success, result.Message);
        Assert.Equal(imported, result.ImportedCount);
        Assert.Equal(pages, handler.Pages);
        var archive = await File.ReadAllTextAsync(service.ArchivePath);
        Assert.DoesNotContain(Secret, archive);
        Assert.DoesNotContain("token", archive);
        Assert.DoesNotContain(Secret, result.Message);
        Assert.All(Directory.GetFiles(_root, "*", SearchOption.AllDirectories),
            file => Assert.Equal(service.ArchivePath, file));
    }

    [Theory]
    [InlineData(false, "http")]
    [InlineData(false, "invalid")]
    [InlineData(false, "json")]
    [InlineData(true, "http")]
    [InlineData(true, "invalid")]
    [InlineData(true, "json")]
    public async Task Capture_ErrorsNeverEchoCredentialsOrPersistLink(bool endfield, string error)
    {
        using var client = new HttpClient(new FailureHandler(error));
        var uigfLogger = new MemoryLogger<UigfArchiveService>();
        var endfieldLogger = new MemoryLogger<EndfieldGachaArchiveService>();
        using var uigf = new UigfArchiveService(uigfLogger, client);
        using var ef = new EndfieldGachaArchiveService(endfieldLogger, client, () => throw new InvalidOperationException());
        var result = endfield
            ? await ef.CaptureFromUrlAsync($"https://ef-webview.hypergryph.com/api/record/char?token={Secret}&server_id=1&pool_type=standard")
            : await uigf.CaptureFromUrlAsync("genshin-impact", $"https://webstatic.mihoyo.com/hk4e/event/e20190909gacha/index.html?authkey={Secret}");
        Assert.False(result.Success);
        Assert.DoesNotContain(Secret, result.Message);
        Assert.DoesNotContain(Secret, string.Join("", uigfLogger.Messages.Concat(endfieldLogger.Messages)));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void CredentialsTooShortAreRejectedAndDoNotEraseOrdinaryRecordFields()
    {
        Assert.False(GachaHistoryUrlValidator.TryValidate("endfield",
            "https://ef-webview.hypergryph.com/api/record/char?token=1&server_id=1&pool_type=standard", out _));
        Assert.False(GachaHistoryUrlValidator.TryValidate("genshin-impact",
            "https://webstatic.mihoyo.com/hk4e/event/e20190909gacha/index.html?authkey=1", out _));
        var record = new JsonObject { ["id"] = "1231", ["name"] = "Operator 1", ["token"] = "1" };
        GachaArchivePrivacy.Strip(record,
            new Uri("https://ef-webview.hypergryph.com/api/record/char?token=1&server_id=1&pool_type=standard"));
        Assert.Equal("1231", record["id"]!.GetValue<string>());
        Assert.Equal("Operator 1", record["name"]!.GetValue<string>());
        Assert.Null(record["token"]);
    }

    [Fact]
    public async Task InvalidLinkIsRejectedBeforeHttpOrCacheAccess()
    {
        using var client = new HttpClient(new FailureHandler("must not request"));
        using var uigf = new UigfArchiveService(NullLogger<UigfArchiveService>.Instance, client);
        using var ef = new EndfieldGachaArchiveService(NullLogger<EndfieldGachaArchiveService>.Instance,
            client, () => throw new InvalidOperationException("must not read local cache"));
        Assert.False((await uigf.CaptureFromUrlAsync("genshin-impact", "https://evil.test/?authkey=" + Secret)).Success);
        Assert.False((await ef.CaptureFromUrlAsync("https://evil.test/?token=" + Secret)).Success);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    private static JsonObject Record(string id, string type) => new() {
        ["id"] = id, ["uid"] = "100000001", ["gacha_type"] = type, ["time"] = "2026-01-01 00:00:00"
    };

    private sealed class UigfHandler(string mainType) : HttpMessageHandler
    {
        public int MainPages { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(GachaHistoryUrlValidator.TryParseQuery(request.RequestUri!.Query, out var query));
            Assert.Equal("1", query["page"]);
            Assert.Equal("20", query["size"]);
            var type = query["gacha_type"];
            JsonArray list = [];
            if (type == mainType)
            {
                MainPages++;
                list = query["end_id"] switch {
                    "0" => new(Record("300", type), Record("200", type), Record("150", type)),
                    "150" => new(Record("100", type)), _ => new()
                };
                foreach (var record in list.OfType<JsonObject>()) {
                    record["note"] = Secret; record["authkey"] = Secret;
                    record["nested"] = new JsonArray("https://official.test/?authkey=" + Secret);
                }
            }
            return Task.FromResult(Response(new JsonObject {
                ["retcode"] = 0, ["data"] = new JsonObject { ["list"] = list }
            }.ToJsonString()));
        }
    }

    private sealed class EndfieldHandler : HttpMessageHandler
    {
        public int Pages { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Pages++;
            Assert.True(GachaHistoryUrlValidator.TryParseQuery(request.RequestUri!.Query, out var query));
            var continuation = query.TryGetValue("seq_id", out var seq);
            if (continuation) Assert.Equal("150", seq);
            var list = continuation
                ? new JsonArray(new JsonObject { ["seqId"] = 100, ["poolId"] = "standard" })
                : new JsonArray(300, 200, 150);
            if (!continuation) list = new JsonArray(new[] { 300, 200, 150 }.Select(id => (JsonNode)new JsonObject {
                ["seqId"] = id, ["poolId"] = "standard", ["note"] = Secret, ["token"] = Secret
            }).ToArray());
            return Task.FromResult(Response(new JsonObject {
                ["data"] = new JsonObject { ["list"] = list, ["hasMore"] = !continuation }
            }.ToJsonString()));
        }
    }

    private sealed class FailureHandler(string error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            error switch {
                "http" => throw new HttpRequestException(Secret),
                "invalid" => throw new InvalidDataException(Secret),
                "json" => throw new System.Text.Json.JsonException(Secret),
                _ => throw new Exception("Unexpected network request")
            };
    }

    private sealed class MemoryLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _original);
        Directory.Delete(_root, true);
    }
}

