using AsterLauncher.Infrastructure;
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
