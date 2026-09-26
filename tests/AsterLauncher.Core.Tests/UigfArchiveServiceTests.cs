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
