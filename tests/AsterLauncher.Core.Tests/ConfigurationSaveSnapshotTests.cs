using System.Text.Json;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using Microsoft.Extensions.Logging;

namespace AsterLauncher.Core.Tests;

[Collection("Environment variables")]
public sealed class ConfigurationSaveSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? AppContext.BaseDirectory,
        "AsterLauncher.Tests", "configuration-snapshot", Guid.NewGuid().ToString("N"));
    private readonly string? _originalDataHome = Environment.GetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME");
    private readonly BlockingSaveLogger _logger = new();
    private readonly JsonConfigurationStore _store;

    public ConfigurationSaveSnapshotTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _root);
        _store = new JsonConfigurationStore(_logger);
        Assert.Equal(Path.Combine(_root, "launcher.settings.json"), _store.ConfigurationPath);
    }

    [Fact]
    public async Task QueuedSaveKeepsTheOriginalObjectAndNestedCollectionsSnapshot()
    {
        await _store.SaveAsync(Configuration());
        var configuration = Configuration();
        var path = configuration.Games[0].ExecutablePath;
        var channelPath = configuration.Games[0].HoYoInstallations[0].ExecutablePath;
        var endfieldPath = configuration.EndfieldInstallations[0].ExecutablePath;
        var profileId = configuration.LaunchProfiles[0].Id;
        var heldSave = HoldNextSave();
        try
        {
            await _logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var queued = _store.SaveAsync(configuration);
            Assert.False(queued.IsCompleted);
            configuration.Games[0].ExecutablePath = Path.Combine(_root, "later", "StarRail.exe");
            configuration.Games[0].TotalPlaySeconds = 999;
            configuration.Games[0].PlaySessions[0].DurationSeconds = 999;
            configuration.Games[0].HoYoInstallations[0].ExecutablePath = Path.Combine(_root, "later-channel", "StarRail.exe");
            configuration.Games.Add(new GameUserState { GameId = "later-game" });
            configuration.HiddenGameIds.Clear();
            configuration.EndfieldInstallations[0].ExecutablePath = Path.Combine(_root, "later", "Endfield.exe");
            configuration.LaunchProfiles[0].Steps[0].Arguments = "later arguments";
            configuration.LaunchProfiles.Clear();
            configuration.CompanionTools[0].DefaultArguments = "later companion arguments";
            configuration.ThemePreference = LauncherThemePreference.Light;
            _logger.Release();
            await queued.WaitAsync(TimeSpan.FromSeconds(10));

            var restored = await _store.LoadAsync();
            var game = Assert.Single(restored.Games);
            Assert.Equal(path, game.ExecutablePath);
            Assert.Equal(120, game.TotalPlaySeconds);
            Assert.Equal(120, Assert.Single(game.PlaySessions).DurationSeconds);
            Assert.Equal("tracking-id", game.PlaySessions[0].TrackingId);
            Assert.Equal(GameLaunchTarget.OfficialCloud, game.LaunchTarget);
            Assert.Equal(channelPath, Assert.Single(game.HoYoInstallations).ExecutablePath);
            Assert.Equal(endfieldPath, Assert.Single(restored.EndfieldInstallations).ExecutablePath);
            Assert.Equal(BuiltInGameIds.GenshinImpact, Assert.Single(restored.HiddenGameIds));
            var profile = Assert.Single(restored.LaunchProfiles);
            Assert.Equal(profileId, profile.Id);
            Assert.Equal("original arguments", Assert.Single(profile.Steps).Arguments);
            Assert.Equal("original companion arguments", Assert.Single(restored.CompanionTools).DefaultArguments);
            Assert.Equal(LauncherThemePreference.Dark, restored.ThemePreference);
        }
        finally
        {
            _logger.Release();
            await heldSave.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task OrderedPlaytimeAndUserSettingSavesKeepTheNewTimePathHiddenListAndProfile()
    {
        var configuration = Configuration();
        await _store.SaveAsync(configuration);
        var heldSave = HoldNextSave();
        try
        {
            await _logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The app merges time into its live model before any later user save is requested.
            configuration.Games[0].TotalPlaySeconds = 180;
            configuration.Games[0].PlaySessions[0].DurationSeconds = 180;
            var playtimeSave = _store.SaveAsync(configuration);
            var newPath = Path.Combine(_root, "new-location", "StarRail.exe");
            configuration.Games[0].ExecutablePath = newPath;
            configuration.HiddenGameIds.Add(BuiltInGameIds.Arknights);
            configuration.LaunchProfiles[0].Steps[0].Arguments = "new profile arguments";
            var userSettingsSave = _store.SaveAsync(configuration);
            Assert.False(playtimeSave.IsCompleted);
            Assert.False(userSettingsSave.IsCompleted);
            // An unrelated, unsaved edit after both calls must affect neither queued snapshot.
            configuration.Games[0].TotalPlaySeconds = 999;
            configuration.Games[0].PlaySessions.Clear();
            configuration.Games[0].ExecutablePath = Path.Combine(_root, "unsaved", "StarRail.exe");
            configuration.HiddenGameIds.Clear();
            configuration.LaunchProfiles.Clear();
            _logger.Release();
            await Task.WhenAll(playtimeSave, userSettingsSave).WaitAsync(TimeSpan.FromSeconds(10));

            var restored = await _store.LoadAsync();
            var game = Assert.Single(restored.Games);
            Assert.Equal(180, game.TotalPlaySeconds);
            Assert.Equal(180, Assert.Single(game.PlaySessions).DurationSeconds);
            Assert.Equal(newPath, game.ExecutablePath);
            Assert.Equal(new[] { BuiltInGameIds.GenshinImpact, BuiltInGameIds.Arknights }, restored.HiddenGameIds);
            Assert.Equal("new profile arguments", Assert.Single(Assert.Single(restored.LaunchProfiles).Steps).Arguments);
            // A single normal backup still contains the immediately preceding successful snapshot.
            using var backup = JsonDocument.Parse(await File.ReadAllBytesAsync(_store.BackupPath));
            Assert.Equal(180, backup.RootElement.GetProperty("games")[0].GetProperty("totalPlaySeconds").GetDouble());
            Assert.Equal(1, backup.RootElement.GetProperty("hiddenGameIds").GetArrayLength());
        }
        finally
        {
            _logger.Release();
            await heldSave.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task CapturedQueuedSnapshotStillStopsWhenTheCurrentConfigurationBecomesUnreadable()
    {
        await _store.SaveAsync(Configuration());
        var heldSave = HoldNextSave();
        try
        {
            await _logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var backup = await File.ReadAllBytesAsync(_store.BackupPath);
            var queued = _store.SaveAsync(Configuration());
            Assert.False(queued.IsCompleted);
            const string damaged = "{damaged-current-config";
            await File.WriteAllTextAsync(_store.ConfigurationPath, damaged);
            _logger.Release();

            await Assert.ThrowsAsync<ConfigurationReadException>(() => queued);
            Assert.Equal(damaged, await File.ReadAllTextAsync(_store.ConfigurationPath));
            Assert.Equal(backup, await File.ReadAllBytesAsync(_store.BackupPath));
            await Assert.ThrowsAsync<ConfigurationReadException>(() => _store.SaveAsync(Configuration()));
        }
        finally
        {
            _logger.Release();
            await heldSave.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private Task HoldNextSave()
    {
        _logger.BlockNextSave();
        return Task.Run(() => _store.SaveAsync(Configuration()));
    }

    private LauncherConfiguration Configuration() => new()
    {
        FirstRunCompleted = true,
        ThemePreference = LauncherThemePreference.Dark,
        HiddenGameIds = [BuiltInGameIds.GenshinImpact],
        Games = [new GameUserState
        {
            GameId = BuiltInGameIds.HonkaiStarRail,
            LaunchTarget = GameLaunchTarget.OfficialCloud,
            ExecutablePath = Path.Combine(_root, "original", "StarRail.exe"),
            TotalPlaySeconds = 120,
            PlaySessions = [new GamePlaySession
            {
                TrackingId = "tracking-id", StartedAt = DateTimeOffset.UtcNow.AddHours(-1), DurationSeconds = 120
            }],
            HoYoInstallations = [new HoYoInstallation
            {
                Channel = HoYoChannel.Bilibili, ExecutablePath = Path.Combine(_root, "bilibili", "StarRail.exe")
            }]
        }],
        EndfieldInstallations = [new EndfieldInstallation
        {
            Channel = EndfieldChannel.Bilibili, ExecutablePath = Path.Combine(_root, "endfield", "Endfield.exe")
        }],
        LaunchProfiles = [new LaunchProfile
        {
            GameId = BuiltInGameIds.HonkaiStarRail,
            Steps = [new LaunchStep { Arguments = "original arguments" }]
        }],
        CompanionTools = [new CompanionTool { DefaultArguments = "original companion arguments" }]
    };

    // A logger callback runs at the end of SaveCore while its existing save gate is still held.
    // This makes a real queued save deterministic without sleeps, private reflection, or new production hooks.
    private sealed class BlockingSaveLogger : ILogger<JsonConfigurationStore>, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _blockNext;
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void BlockNextSave() => Interlocked.Exchange(ref _blockNext, 1);
        public void Release() => _release.Set();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Interlocked.Exchange(ref _blockNext, 0) != 1) return;
            Entered.TrySetResult(true);
            if (!_release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The fixture did not release its owned save.");
        }
        public void Dispose() { _release.Set(); _release.Dispose(); }
    }

    public void Dispose()
    {
        _logger.Dispose();
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _originalDataHome);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}