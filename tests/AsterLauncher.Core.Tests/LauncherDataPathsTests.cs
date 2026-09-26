using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

[Collection("Environment variables")]
public sealed class LauncherDataPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AsterLauncher.Tests", Guid.NewGuid().ToString("N"));
    private readonly string? _originalDataHome = Environment.GetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME");

    [Fact]
    public void DefaultDataDirectory_UsesExecutableDirectory_NotBundleExtractionDirectory()
    {
        var executableDirectory = Path.Combine(_root, "release");
        var result = LauncherDataPaths.ResolveDataDirectory(executableDirectory, null);

        Assert.Equal(Path.Combine(executableDirectory, "Data"), result);
    }

    [Fact]
    public async Task Relocate_CopiesDataAndPersistsSelectionBesideExecutable()
    {
        var executableDirectory = Path.Combine(_root, "release");
        var source = Path.Combine(executableDirectory, "Data");
        var target = Path.Combine(_root, "chosen");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(source, "launcher.settings.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(source, "uigf-v4.2.json"), "records");

        Assert.True(await LauncherDataPaths.RelocateAsync(target, executableDirectory: executableDirectory,
            sourceDirectory: source));
        Assert.Equal(target, LauncherDataPaths.ResolveDataDirectory(executableDirectory, null));
        Assert.Equal("records", await File.ReadAllTextAsync(Path.Combine(target, "uigf-v4.2.json")));
        Assert.True(File.Exists(Path.Combine(source, "launcher.settings.json")));
    }

    [Fact]
    public async Task Migration_PicksRecentBundleDataWithoutDeletingLegacyFiles()
    {
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", null);
        var executableDirectory = Path.Combine(_root, "release");
        var legacyData = Path.Combine(_root, "temp", ".net", "AsterLauncher", "bundle", "Data");
        Directory.CreateDirectory(executableDirectory);
        Directory.CreateDirectory(legacyData);
        await File.WriteAllTextAsync(Path.Combine(legacyData, "launcher.settings.json"), "old settings");
        await File.WriteAllTextAsync(Path.Combine(legacyData, "gacha.json"), "old records");

        Assert.True(LauncherDataPaths.MigrateLegacyBundleDataIfNeeded(executableDirectory,
            Path.Combine(_root, "temp")));
        Assert.Equal("old settings", await File.ReadAllTextAsync(
            Path.Combine(executableDirectory, "Data", "launcher.settings.json")));
        Assert.True(File.Exists(Path.Combine(legacyData, "gacha.json")));
        Assert.False(LauncherDataPaths.MigrateLegacyBundleDataIfNeeded(executableDirectory,
            Path.Combine(_root, "temp")));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _originalDataHome);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
