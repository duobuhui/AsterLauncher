using System.Text.Json;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsterLauncher.Core.Tests;

[Collection("Environment variables")]
public sealed class ConfigurationStoreTests : IDisposable
{
    private static readonly string FixtureDirectory = Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? AppContext.BaseDirectory, "AsterLauncher.Tests", "configuration-store");
    private readonly string _root = Path.Combine(FixtureDirectory, Guid.NewGuid().ToString("N"));
    private readonly string? _originalDataHome = Environment.GetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME");
    private readonly JsonConfigurationStore _store;

    public ConfigurationStoreTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _root);
        _store = new JsonConfigurationStore(NullLogger<JsonConfigurationStore>.Instance);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"games\":")]
    [InlineData("{\"games\":null}")]
    [InlineData("{\"games\":[null]}")]
    [InlineData("{\"launchProfiles\":null}")]
    public async Task FailedReadNeverReturnsDefaultsOrAllowsLaterSave(string invalidJson)
    {
        await _store.SaveAsync(Configuration(1));
        await _store.SaveAsync(Configuration(2));
        var backup = await File.ReadAllBytesAsync(_store.BackupPath);
        await File.WriteAllTextAsync(_store.ConfigurationPath, invalidJson);

        var error = await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.LoadAsync());
        Assert.True(error.BackupExists);
        Assert.Null(error.InnerException);
        Assert.Equal(_store.ConfigurationPath, error.ConfigurationPath);
        await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.SaveAsync(Configuration(3)));

        Assert.Equal(invalidJson, await File.ReadAllTextAsync(_store.ConfigurationPath));
        Assert.Equal(backup, await File.ReadAllBytesAsync(_store.BackupPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task SaveWithoutLoadAlsoRejectsExistingDamagedConfiguration()
    {
        await File.WriteAllTextAsync(_store.ConfigurationPath, "null");
        await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.SaveAsync(Configuration(1)));
        Assert.Equal("null", await File.ReadAllTextAsync(_store.ConfigurationPath));
        Assert.False(File.Exists(_store.BackupPath));
    }

    [Fact]
    public async Task EachSuccessfulReplacementKeepsExactlyOnePreviousValidConfiguration()
    {
        await _store.SaveAsync(Configuration(1));
        Assert.False(File.Exists(_store.BackupPath));
        for (var generation = 2; generation <= 5; generation++)
        {
            var previousBytes = await File.ReadAllBytesAsync(_store.ConfigurationPath);
            await _store.SaveAsync(Configuration(generation));
            Assert.Equal(previousBytes, await File.ReadAllBytesAsync(_store.BackupPath));
            Assert.Equal(generation, Assert.Single((await _store.LoadAsync()).Games).TotalPlaySeconds);
            Assert.Equal(2, Directory.GetFiles(_root).Length);
        }
    }

    [Fact]
    public async Task FileDamagedAfterLoadIsRevalidatedBeforeSaveWithoutRotatingBackup()
    {
        await _store.SaveAsync(Configuration(1));
        await _store.SaveAsync(Configuration(2));
        var current = await _store.LoadAsync();
        var backup = await File.ReadAllBytesAsync(_store.BackupPath);
        await File.WriteAllTextAsync(_store.ConfigurationPath, "{truncated");
        current.Games[0].TotalPlaySeconds = 99;

        await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.SaveAsync(current));
        Assert.Equal("{truncated", await File.ReadAllTextAsync(_store.ConfigurationPath));
        Assert.Equal(backup, await File.ReadAllBytesAsync(_store.BackupPath));
    }
    [Fact]
    public async Task ExplicitSuccessfulReloadIsRequiredAfterRestoringAFile()
    {
        await _store.SaveAsync(Configuration(1));
        await _store.SaveAsync(Configuration(2));
        await File.WriteAllTextAsync(_store.ConfigurationPath, "{broken");
        await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.LoadAsync());

        File.Copy(_store.BackupPath, _store.ConfigurationPath, overwrite: true);
        await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.SaveAsync(Configuration(3)));
        var recovered = await _store.LoadAsync();
        Assert.Equal(1, Assert.Single(recovered.Games).TotalPlaySeconds);
        await _store.SaveAsync(Configuration(3));
        Assert.Equal(3, Assert.Single((await _store.LoadAsync()).Games).TotalPlaySeconds);
    }

    [Fact]
    public async Task RemovingFailedFileDoesNotUnlockWritesOrCreateFirstRunDefaults()
    {
        await File.WriteAllTextAsync(_store.ConfigurationPath, "null");
        await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.LoadAsync());
        File.Delete(_store.ConfigurationPath);

        await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.LoadAsync());
        await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.SaveAsync(Configuration(1)));
        Assert.False(File.Exists(_store.ConfigurationPath));
    }

    [Fact]
    public async Task MissingMainFileWithExistingBackupRequiresExplicitRecoveryEvenInNewInstance()
    {
        await _store.SaveAsync(Configuration(1));
        await _store.SaveAsync(Configuration(2));
        File.Delete(_store.ConfigurationPath);
        var newStore = new JsonConfigurationStore(NullLogger<JsonConfigurationStore>.Instance);

        await Assert.ThrowsAsync<ConfigurationReadException>(() => newStore.LoadAsync());
        await Assert.ThrowsAsync<ConfigurationReadException>(() => newStore.SaveAsync(Configuration(3)));
        Assert.False(File.Exists(_store.ConfigurationPath));
        Assert.True(File.Exists(_store.BackupPath));
    }

    [Fact]
    public async Task ReadIoFailureBlocksSavesUntilSuccessfulReload()
    {
        await _store.SaveAsync(Configuration(1));
        await using (var locked = new FileStream(_store.ConfigurationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.LoadAsync());
            await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.SaveAsync(Configuration(2)));
        }

        await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.SaveAsync(Configuration(2)));
        Assert.Equal(1, Assert.Single((await _store.LoadAsync()).Games).TotalPlaySeconds);
        await _store.SaveAsync(Configuration(2));
    }

    [Fact]
    public async Task EmptyObjectAndUnknownPropertiesRemainCompatibleWithoutTreatingThemAsFirstRun()
    {
        await File.WriteAllTextAsync(_store.ConfigurationPath, "{\"futureOption\":{\"value\":true}}");
        var configuration = await _store.LoadAsync();
        Assert.Null(configuration.FirstRunCompleted);
        Assert.Empty(configuration.Games);
        Assert.False(File.Exists(_store.BackupPath));
        Assert.Contains("futureOption", await File.ReadAllTextAsync(_store.ConfigurationPath));
    }

    [Fact]
    public async Task NewDataDirectoryCreatesDefaultsOnlyOnceAndSubsequentLoadDoesNotWrite()
    {
        var configuration = await _store.LoadAsync();
        Assert.False(configuration.FirstRunCompleted);
        var original = await File.ReadAllBytesAsync(_store.ConfigurationPath);
        Assert.False(File.Exists(_store.BackupPath));
        await _store.LoadAsync();
        Assert.Equal(original, await File.ReadAllBytesAsync(_store.ConfigurationPath));
        Assert.False(File.Exists(_store.BackupPath));
    }

    [Fact]
    public async Task RealStreamingConfigurationAndBoundedBackupPreserveLongHistory()
    {
        var configuration = Configuration(4321);
        configuration.HiddenGameIds = [BuiltInGameIds.Arknights, BuiltInGameIds.PetitPlanet];
        configuration.Games[0].PlaySessions = Enumerable.Range(0, 768)
            .Select(index => new GamePlaySession
            {
                StartedAt = DateTimeOffset.Parse("2026-10-08T12:00:00+08:00").AddHours(-index),
                DurationSeconds = 60 + index
            }).ToList();
        await _store.SaveAsync(configuration);
        var original = await File.ReadAllBytesAsync(_store.ConfigurationPath);
        Assert.True(original.Length > 32 * 1024);

        var restored = await _store.LoadAsync();
        Assert.True(restored.FirstRunCompleted);
        Assert.Equal(configuration.HiddenGameIds, restored.HiddenGameIds);
        Assert.Equal(768, Assert.Single(restored.Games).PlaySessions.Count);
        restored.Games[0].TotalPlaySeconds++;
        await _store.SaveAsync(restored);
        Assert.Equal(original, await File.ReadAllBytesAsync(_store.BackupPath));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public async Task ParseFailureDoesNotExposeConfigurationValuesInExceptionOrLog()
    {
        const string secret = "private-authkey-value-not-for-logs";
        var logger = new RecordingLogger();
        var store = new JsonConfigurationStore(logger);
        await File.WriteAllTextAsync(store.ConfigurationPath,
            "{\"themePreference\":\"" + secret + "\",\"games\":[]}");

        var error = await Assert.ThrowsAsync<ConfigurationReadException>(() => store.LoadAsync());
        Assert.DoesNotContain(secret, error.ToString());
        Assert.DoesNotContain(secret, string.Join(Environment.NewLine, logger.Messages));
        Assert.False(error.BackupExists);
    }

    [Fact]
    public async Task BackupPreviewShowsOnlySummaryAndDoesNotModifyAnyFile()
    {
        var first = Configuration(3600);
        first.HiddenGameIds = [BuiltInGameIds.Arknights];
        first.Games[0].PlaySessions = [new GamePlaySession { DurationSeconds = 3600 }];
        await _store.SaveAsync(first);
        await _store.SaveAsync(Configuration(2));
        var current = await File.ReadAllBytesAsync(_store.ConfigurationPath);
        var backup = await File.ReadAllBytesAsync(_store.BackupPath);

        var preview = Assert.IsType<ConfigurationBackupPreview>(await _store.ReadBackupAsync());
        Assert.Equal(1, preview.GameCount);
        Assert.Equal(1, preview.HiddenGameCount);
        Assert.Equal(3600, preview.TotalPlaySeconds);
        Assert.Equal(1, preview.PlaySessionCount);
        Assert.True(preview.FirstRunCompleted);
        Assert.True(preview.HasCurrentConfiguration);
        Assert.True(preview.LastWrittenAt <= DateTimeOffset.UtcNow);
        Assert.Equal(current, await File.ReadAllBytesAsync(_store.ConfigurationPath));
        Assert.Equal(backup, await File.ReadAllBytesAsync(_store.BackupPath));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public async Task ExplicitRestoreKeepsBrokenOriginalAndValidBackupThenRequiresReload()
    {
        await _store.SaveAsync(Configuration(1));
        await _store.SaveAsync(Configuration(2));
        var backup = await File.ReadAllBytesAsync(_store.BackupPath);
        const string broken = "{\"games\":";
        await File.WriteAllTextAsync(_store.ConfigurationPath, broken);
        await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.LoadAsync());
        var preview = Assert.IsType<ConfigurationBackupPreview>(await _store.ReadBackupAsync());

        var restored = await _store.RestoreBackupAsync(preview);
        Assert.Equal(1, Assert.Single(restored.Games).TotalPlaySeconds);
        Assert.Equal(backup, await File.ReadAllBytesAsync(_store.ConfigurationPath));
        Assert.Equal(backup, await File.ReadAllBytesAsync(_store.BackupPath));
        Assert.Equal(broken, await File.ReadAllTextAsync(_store.RecoveryOriginalPath));
        await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.SaveAsync(Configuration(3)));
        Assert.Equal(1, Assert.Single((await _store.LoadAsync()).Games).TotalPlaySeconds);
        await _store.SaveAsync(Configuration(3));
    }

    [Fact]
    public async Task RepeatedExplicitRestoresKeepOnlyOneOriginalFile()
    {
        await _store.SaveAsync(Configuration(1));
        await _store.SaveAsync(Configuration(2));
        var backup = await File.ReadAllBytesAsync(_store.BackupPath);
        for (var index = 0; index < 3; index++)
        {
            var broken = "{broken-" + index;
            await File.WriteAllTextAsync(_store.ConfigurationPath, broken);
            var preview = Assert.IsType<ConfigurationBackupPreview>(await _store.ReadBackupAsync());
            await _store.RestoreBackupAsync(preview);
            Assert.Equal(broken, await File.ReadAllTextAsync(_store.RecoveryOriginalPath));
            Assert.Equal(backup, await File.ReadAllBytesAsync(_store.BackupPath));
            Assert.Equal(3, Directory.GetFiles(_root).Length);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedBackupOrMainRejectsStalePreviewWithoutTouchingAnyFiles(bool changeBackup)
    {
        await _store.SaveAsync(Configuration(1));
        await _store.SaveAsync(Configuration(2));
        var preview = Assert.IsType<ConfigurationBackupPreview>(await _store.ReadBackupAsync());
        if (changeBackup)
            await File.WriteAllTextAsync(_store.BackupPath, "{\"firstRunCompleted\":true,\"games\":[]}");
        else
            await File.WriteAllTextAsync(_store.ConfigurationPath, "{newly-changed-main");
        var current = await File.ReadAllBytesAsync(_store.ConfigurationPath);
        var backup = await File.ReadAllBytesAsync(_store.BackupPath);

        await Assert.ThrowsAsync<ConfigurationRecoveryException>(() => _store.RestoreBackupAsync(preview));
        Assert.Equal(current, await File.ReadAllBytesAsync(_store.ConfigurationPath));
        Assert.Equal(backup, await File.ReadAllBytesAsync(_store.BackupPath));
        Assert.False(File.Exists(_store.RecoveryOriginalPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task BackupPreviewCannotBeUsedWithAnotherDataDirectory()
    {
        await _store.SaveAsync(Configuration(1));
        await _store.SaveAsync(Configuration(2));
        var preview = Assert.IsType<ConfigurationBackupPreview>(await _store.ReadBackupAsync());
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", Path.Combine(_root, "other"));
        var other = new JsonConfigurationStore(NullLogger<JsonConfigurationStore>.Instance);

        await Assert.ThrowsAsync<ConfigurationRecoveryException>(() => other.RestoreBackupAsync(preview));
        Assert.False(Directory.Exists(Path.GetDirectoryName(other.ConfigurationPath)));
        Assert.False(File.Exists(_store.RecoveryOriginalPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{broken")]
    public async Task InvalidBackupCannotBePreviewedOrReplaceMain(string invalid)
    {
        await _store.SaveAsync(Configuration(1));
        await _store.SaveAsync(Configuration(2));
        var preview = Assert.IsType<ConfigurationBackupPreview>(await _store.ReadBackupAsync());
        var current = await File.ReadAllBytesAsync(_store.ConfigurationPath);
        await File.WriteAllTextAsync(_store.BackupPath, invalid);

        await Assert.ThrowsAsync<ConfigurationRecoveryException>(() => _store.ReadBackupAsync());
        await Assert.ThrowsAsync<ConfigurationRecoveryException>(() => _store.RestoreBackupAsync(preview));
        Assert.Equal(current, await File.ReadAllBytesAsync(_store.ConfigurationPath));
        Assert.Equal(invalid, await File.ReadAllTextAsync(_store.BackupPath));
        Assert.False(File.Exists(_store.RecoveryOriginalPath));
    }

    [Fact]
    public async Task MissingMainCanBeExplicitlyRestoredWithoutCreatingAnOriginal()
    {
        await _store.SaveAsync(Configuration(1));
        await _store.SaveAsync(Configuration(2));
        var backup = await File.ReadAllBytesAsync(_store.BackupPath);
        File.Delete(_store.ConfigurationPath);
        var preview = Assert.IsType<ConfigurationBackupPreview>(await _store.ReadBackupAsync());
        Assert.False(preview.HasCurrentConfiguration);

        await _store.RestoreBackupAsync(preview);
        Assert.Equal(backup, await File.ReadAllBytesAsync(_store.ConfigurationPath));
        Assert.Equal(backup, await File.ReadAllBytesAsync(_store.BackupPath));
        Assert.False(File.Exists(_store.RecoveryOriginalPath));
    }

    [Fact]
    public async Task MissingBackupReturnsNoPreviewAndDeletedBackupCannotRestore()
    {
        Assert.Null(await _store.ReadBackupAsync());
        await _store.SaveAsync(Configuration(1));
        await _store.SaveAsync(Configuration(2));
        var preview = Assert.IsType<ConfigurationBackupPreview>(await _store.ReadBackupAsync());
        var current = await File.ReadAllBytesAsync(_store.ConfigurationPath);
        File.Delete(_store.BackupPath);

        await Assert.ThrowsAsync<ConfigurationRecoveryException>(() => _store.RestoreBackupAsync(preview));
        Assert.Equal(current, await File.ReadAllBytesAsync(_store.ConfigurationPath));
        Assert.False(File.Exists(_store.RecoveryOriginalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedDestinationPreventsRestoreWithoutDamagingBackupOrPriorOriginal(bool lockOriginal)
    {
        await _store.SaveAsync(Configuration(1));
        await _store.SaveAsync(Configuration(2));
        await File.WriteAllTextAsync(_store.RecoveryOriginalPath, "previous-recovery-original");
        var preview = Assert.IsType<ConfigurationBackupPreview>(await _store.ReadBackupAsync());
        var current = await File.ReadAllBytesAsync(_store.ConfigurationPath);
        var backup = await File.ReadAllBytesAsync(_store.BackupPath);
        await using (var locked = new FileStream(lockOriginal ? _store.RecoveryOriginalPath : _store.ConfigurationPath,
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<ConfigurationRecoveryException>(() => _store.RestoreBackupAsync(preview));

        Assert.Equal(current, await File.ReadAllBytesAsync(_store.ConfigurationPath));
        Assert.Equal(backup, await File.ReadAllBytesAsync(_store.BackupPath));
        Assert.Equal("previous-recovery-original", await File.ReadAllTextAsync(_store.RecoveryOriginalPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task InvalidBackupDoesNotExposeValuesInRecoveryErrorOrLog()
    {
        const string secret = "private-authkey-value-not-for-recovery-logs";
        var logger = new RecordingLogger();
        var store = new JsonConfigurationStore(logger);
        await store.SaveAsync(Configuration(1));
        await store.SaveAsync(Configuration(2));
        var preview = Assert.IsType<ConfigurationBackupPreview>(await store.ReadBackupAsync());
        await File.WriteAllTextAsync(store.BackupPath, "{\"themePreference\":\"" + secret + "\"}");

        var previewError = await Assert.ThrowsAsync<ConfigurationRecoveryException>(() => store.ReadBackupAsync());
        var restoreError = await Assert.ThrowsAsync<ConfigurationRecoveryException>(() => store.RestoreBackupAsync(preview));
        Assert.DoesNotContain(secret, previewError.ToString());
        Assert.DoesNotContain(secret, restoreError.ToString());
        Assert.DoesNotContain(secret, string.Join(Environment.NewLine, logger.Messages));
    }
    [Theory]
    [InlineData("main-changed")]
    [InlineData("backup-changed")]
    [InlineData("backup-damaged")]
    [InlineData("main-locked")]
    [InlineData("backup-locked")]
    [InlineData("original-locked")]
    [InlineData("cancelled")]
    public async Task RejectedRecoveryKeepsHealthyStoreWritable(string failure)
    {
        await _store.SaveAsync(Configuration(1));
        await _store.SaveAsync(Configuration(2));
        var preview = Assert.IsType<ConfigurationBackupPreview>(await _store.ReadBackupAsync());
        FileStream? locked = null;
        using var cancellation = new CancellationTokenSource();
        try
        {
            switch (failure)
            {
                case "main-changed":
                    await File.WriteAllTextAsync(_store.ConfigurationPath, JsonSerializer.Serialize(Configuration(9)));
                    break;
                case "backup-changed":
                    await File.WriteAllTextAsync(_store.BackupPath, JsonSerializer.Serialize(Configuration(9)));
                    break;
                case "backup-damaged":
                    await File.WriteAllTextAsync(_store.BackupPath, "{damaged");
                    break;
                case "main-locked":
                    locked = new FileStream(_store.ConfigurationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    break;
                case "backup-locked":
                    locked = new FileStream(_store.BackupPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    break;
                case "original-locked":
                    await File.WriteAllTextAsync(_store.RecoveryOriginalPath, "existing-original");
                    locked = new FileStream(_store.RecoveryOriginalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    break;
                case "cancelled":
                    cancellation.Cancel();
                    break;
            }

            if (failure == "cancelled")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => _store.RestoreBackupAsync(preview, cancellation.Token));
            else
                await Assert.ThrowsAsync<ConfigurationRecoveryException>(() => _store.RestoreBackupAsync(preview));
        }
        finally
        {
            if (locked is not null) await locked.DisposeAsync();
        }

        // Failed recovery must not disable unrelated later settings writes.
        await _store.SaveAsync(Configuration(3));
        Assert.Equal(3, Assert.Single((await _store.LoadAsync()).Games).TotalPlaySeconds);
    }

    [Fact]
    public async Task RejectedRecoveryDoesNotUnlockAStoreAlreadyBlockedByFailedRead()
    {
        await _store.SaveAsync(Configuration(1));
        await _store.SaveAsync(Configuration(2));
        var validMain = await File.ReadAllBytesAsync(_store.ConfigurationPath);
        await File.WriteAllTextAsync(_store.ConfigurationPath, "{damaged-main");
        await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.LoadAsync());
        var preview = Assert.IsType<ConfigurationBackupPreview>(await _store.ReadBackupAsync());
        await File.WriteAllTextAsync(_store.BackupPath, "{damaged-backup");

        await Assert.ThrowsAsync<ConfigurationRecoveryException>(() => _store.RestoreBackupAsync(preview));
        await File.WriteAllBytesAsync(_store.ConfigurationPath, validMain);
        await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.SaveAsync(Configuration(3)));
        await _store.LoadAsync();
        await _store.SaveAsync(Configuration(3));
    }
    private static LauncherConfiguration Configuration(int generation) => new()
    {
        FirstRunCompleted = true,
        Games =
        [
            new GameUserState
            {
                GameId = BuiltInGameIds.Endfield,
                LaunchTarget = GameLaunchTarget.Local,
                TotalPlaySeconds = generation,
                ExecutablePath = @"E:\Games\Endfield\Endfield.exe"
            }
        ]
    };

    private sealed class RecordingLogger : ILogger<JsonConfigurationStore>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null) Messages.Add(exception.ToString());
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _originalDataHome);
        var resolved = Path.GetFullPath(_root);
        if (!resolved.StartsWith(Path.GetFullPath(FixtureDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test fixture escaped its workspace directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}