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

    [Fact]
    public async Task NestedMultiFileApp_UsesRootDataAndKeepsRelocationPointerOutsideApp()
    {
        var release = Path.Combine(_root, "release");
        var app = Path.Combine(release, "App");
        var tools = Path.Combine(release, "MigrationTools");
        var target = Path.Combine(_root, "chosen");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(tools);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(release, "AsterLauncher.exe"), "shim");
        await File.WriteAllTextAsync(Path.Combine(tools, "AsterLauncher.Migrator.dll"), "tool");
        var source = Path.Combine(release, "Data");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "launcher.settings.json"), "{}");

        Assert.Equal(source, LauncherDataPaths.ResolveDataDirectory(app, null));
        Assert.True(await LauncherDataPaths.RelocateAsync(target, executableDirectory: app,
            sourceDirectory: source));
        Assert.Equal(target, LauncherDataPaths.ResolveDataDirectory(app, null));
        Assert.True(File.Exists(Path.Combine(release, "asterlauncher.data-location.json")));
        Assert.False(File.Exists(Path.Combine(app, "asterlauncher.data-location.json")));
    }
    [Fact]
    public async Task Relocate_ActivePlaytimeObserverBlocksBeforeCopyOrSelectionCommit()
    {
        var executableDirectory = Path.Combine(_root, "release");
        var source = Path.Combine(executableDirectory, "Data");
        var target = Path.Combine(_root, "chosen");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var journal = new PlaytimeJournal(source);
        Directory.CreateDirectory(Path.GetDirectoryName(journal.ObserverLockPath)!);
        var settings = Path.Combine(source, "launcher.settings.json");
        await File.WriteAllTextAsync(settings, "original settings");
        using var observer = new FileStream(journal.ObserverLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LauncherDataPaths.RelocateAsync(target, executableDirectory: executableDirectory, sourceDirectory: source));

        Assert.Contains("退出游戏", error.Message);
        Assert.Contains("计时", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.False(File.Exists(Path.Combine(executableDirectory, "asterlauncher.data-location.json")));
        Assert.Equal("original settings", await File.ReadAllTextAsync(settings));
    }

    [Fact]
    public async Task Relocate_UnlockedObserverFileAllowsMoveAndPreservesReceiptsWithoutCopyingRuntimeLock()
    {
        var executableDirectory = Path.Combine(_root, "release");
        var source = Path.Combine(executableDirectory, "Data");
        var target = Path.Combine(_root, "chosen");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var journal = new PlaytimeJournal(source);
        Directory.CreateDirectory(journal.ReceiptDirectory);
        Directory.CreateDirectory(journal.RequestDirectory);
        await File.WriteAllTextAsync(journal.ObserverLockPath, string.Empty);
        var receiptName = new string('a', 64) + ".json";
        var requestName = new string('b', 64) + ".json";
        await File.WriteAllTextAsync(Path.Combine(journal.ReceiptDirectory, receiptName), "durable receipt");
        await File.WriteAllTextAsync(Path.Combine(journal.RequestDirectory, requestName), "pending request");

        Assert.True(await LauncherDataPaths.RelocateAsync(target, executableDirectory: executableDirectory, sourceDirectory: source));

        var migrated = new PlaytimeJournal(target);
        Assert.Equal("durable receipt", await File.ReadAllTextAsync(Path.Combine(migrated.ReceiptDirectory, receiptName)));
        Assert.Equal("pending request", await File.ReadAllTextAsync(Path.Combine(migrated.RequestDirectory, requestName)));
        Assert.False(File.Exists(migrated.ObserverLockPath));
        Assert.Equal(target, LauncherDataPaths.ResolveDataDirectory(executableDirectory, null));
        using var available = new FileStream(journal.ObserverLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Relocate_SameDirectoryStillReturnsFalseWhenObserverIsActive()
    {
        var executableDirectory = Path.Combine(_root, "release");
        var source = Path.Combine(executableDirectory, "Data");
        var journal = new PlaytimeJournal(source);
        Directory.CreateDirectory(Path.GetDirectoryName(journal.ObserverLockPath)!);
        using var observer = new FileStream(journal.ObserverLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.False(await LauncherDataPaths.RelocateAsync(source, executableDirectory: executableDirectory, sourceDirectory: source));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _originalDataHome);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
