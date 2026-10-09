using System.Text.Json;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class PlayHistoryRecoveryTests : IDisposable
{
    private static readonly string FixtureBase = Path.Combine(AppContext.BaseDirectory, "play-history-recovery-tests");
    private readonly string _root = Path.Combine(FixtureBase, Guid.NewGuid().ToString("N"));
    // Sanitized examples keep the exact timestamp/category/message format used by
    // shipped builds. No raw URLs, profile arguments or unrelated log fields are needed.
    private const string CompletedRail = """
        2026-09-26T13:01:27.4832415+08:00 [Information] AsterLauncher.Core.GameLaunchOrchestrator: Launch session 84431e34-20be-4117-8c21-e6211e26e834 started for honkai-star-rail using profile 默认启动
        2026-09-26T13:05:48.0641805+08:00 [Information] AsterLauncher.Core.GameLaunchOrchestrator: Launch session 84431e34-20be-4117-8c21-e6211e26e834 ended with Completed after 00:04:20.5809640
        """;
    private const string CompletedEndfield = """
        2026-09-26T13:16:40.7751717+08:00 [Information] AsterLauncher.Core.GameLaunchOrchestrator: Launch session 062e2f60-669b-4b74-b2c9-d9cb2ddf4796 started for endfield using profile 默认启动
        2026-09-26T13:25:21.5071194+08:00 [Information] AsterLauncher.Core.GameLaunchOrchestrator: Launch session 062e2f60-669b-4b74-b2c9-d9cb2ddf4796 ended with Completed after 00:08:40.7314580
        """;
    private static readonly DateTimeOffset RailStartedAt = DateTimeOffset.Parse("2026-09-26T13:01:27.4832415+08:00");
    private const double RailDuration = 260.580964;

    public PlayHistoryRecoveryTests() => Directory.CreateDirectory(Path.Combine(_root, "logs"));

    [Fact]
    public async Task PreviewFromShippedLogFormatIsReadOnlyAndApplyPreservesAllOtherSettings()
    {
        await WriteLogAsync(CompletedRail + Environment.NewLine + CompletedEndfield);
        var configuration = CreateConfiguration();
        configuration.HiddenGameIds = [BuiltInGameIds.PetitPlanet];
        configuration.FirstRunCompleted = true;
        configuration.Games[0].ExecutablePath = @"E:\Games\StarRail.exe";
        var before = JsonSerializer.Serialize(configuration);
        var filesBefore = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        var service = new PlayHistoryRecoveryService(_root);

        var plan = await service.PreviewAsync(configuration);

        Assert.True(plan.HasChanges);
        Assert.Equal(2, plan.CompletedSessionCount);
        Assert.Equal(1, plan.ScannedLogCount);
        Assert.Equal(0, plan.IncompleteSessionCount);
        Assert.Equal(before, JsonSerializer.Serialize(configuration));
        Assert.Equal(filesBefore, Directory.GetFiles(_root, "*", SearchOption.AllDirectories));

        var changes = PlayHistoryRecoveryService.Apply(configuration, plan);
        Assert.Equal(2, changes.Count);
        var rail = configuration.Games[0];
        Assert.Equal(RailDuration, rail.TotalPlaySeconds, 7);
        Assert.Equal(RailStartedAt, rail.LastPlayedAt);
        Assert.Equal(RailDuration, Assert.Single(rail.PlaySessions).DurationSeconds, 7);
        Assert.Equal(@"E:\Games\StarRail.exe", rail.ExecutablePath);
        Assert.True(configuration.FirstRunCompleted);
        Assert.Equal([BuiltInGameIds.PetitPlanet], configuration.HiddenGameIds);
        Assert.Equal(520.731458, configuration.Games[1].TotalPlaySeconds, 7);
        Assert.Equal(filesBefore, Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DuplicateSessionIdsAcrossCopiedLogsAndRepeatedRecoveryNeverAddTimeTwice()
    {
        await WriteLogAsync(CompletedRail, "asterlauncher-20260926.log");
        await WriteLogAsync(CompletedRail, "asterlauncher-20260927.log");
        var configuration = CreateConfiguration();
        var service = new PlayHistoryRecoveryService(_root);
        var plan = await service.PreviewAsync(configuration);
        Assert.Equal(1, plan.CompletedSessionCount);
        Assert.Single(PlayHistoryRecoveryService.Apply(configuration, plan));
        Assert.Empty(PlayHistoryRecoveryService.Apply(configuration, plan));
        Assert.False((await service.PreviewAsync(configuration)).HasChanges);
        Assert.Single(configuration.Games[0].PlaySessions);
        Assert.Equal(RailDuration, configuration.Games[0].TotalPlaySeconds, 7);
    }

    [Fact]
    public async Task OriginalSessionTimestampBeforeLoggerTimestampIsRecognizedAsAlreadyCounted()
    {
        await WriteLogAsync(CompletedRail);
        var configuration = CreateConfiguration();
        configuration.Games[0].PlaySessions =
        [
            new GamePlaySession { StartedAt = RailStartedAt.AddTicks(-2450), DurationSeconds = RailDuration }
        ];
        configuration.Games[0].TotalPlaySeconds = RailDuration;
        var plan = await new PlayHistoryRecoveryService(_root).PreviewAsync(configuration);
        Assert.False(plan.HasChanges);
        Assert.Empty(PlayHistoryRecoveryService.Apply(configuration, plan));
    }

    [Fact]
    public async Task ApplyingAStalePreviewRechecksSessionsThatWereSavedAfterPreview()
    {
        await WriteLogAsync(CompletedRail);
        var configuration = CreateConfiguration();
        var plan = await new PlayHistoryRecoveryService(_root).PreviewAsync(configuration);
        configuration.Games[0].PlaySessions.Add(
            new GamePlaySession { StartedAt = RailStartedAt.AddMilliseconds(-10), DurationSeconds = RailDuration });
        configuration.Games[0].TotalPlaySeconds = RailDuration;
        Assert.Empty(PlayHistoryRecoveryService.Apply(configuration, plan));
        Assert.Single(configuration.Games[0].PlaySessions);
        Assert.Equal(RailDuration, configuration.Games[0].TotalPlaySeconds, 7);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(10, RailDuration)]
    public async Task LegacyUndatedTotalIsPreservedWithoutAddingRecoveredEvidenceAgain(double previous, double expected)
    {
        await WriteLogAsync(CompletedRail);
        var configuration = CreateConfiguration();
        configuration.Games[0].TotalPlaySeconds = previous;
        var plan = await new PlayHistoryRecoveryService(_root).PreviewAsync(configuration);
        var change = Assert.Single(plan.Games);
        Assert.Equal(previous, change.PreviousTotalSeconds);
        Assert.Equal(expected, change.RecoveredTotalSeconds, 7);
        Assert.Equal(RailDuration, change.AddedSessionSeconds, 7);
        PlayHistoryRecoveryService.Apply(configuration, plan);
        Assert.Equal(expected, configuration.Games[0].TotalPlaySeconds, 7);
        Assert.Single(configuration.Games[0].PlaySessions);
    }

    [Fact]
    public async Task ExistingDatedHistoryAndLaterLastPlayedTimeRemainIntact()
    {
        await WriteLogAsync(CompletedRail);
        var configuration = CreateConfiguration();
        var later = RailStartedAt.AddDays(3);
        configuration.Games[0].PlaySessions.Add(new GamePlaySession { StartedAt = later, DurationSeconds = 600 });
        configuration.Games[0].TotalPlaySeconds = 600;
        configuration.Games[0].LastPlayedAt = later;
        var plan = await new PlayHistoryRecoveryService(_root).PreviewAsync(configuration);
        PlayHistoryRecoveryService.Apply(configuration, plan);
        Assert.Equal(2, configuration.Games[0].PlaySessions.Count);
        Assert.Equal(600 + RailDuration, configuration.Games[0].TotalPlaySeconds, 7);
        Assert.Equal(later, configuration.Games[0].LastPlayedAt);
    }

    [Theory]
    [InlineData("Aborted")]
    [InlineData("Cancelled")]
    [InlineData("GameNotDetected")]
    public async Task FailedSessionsAreNeverConvertedIntoPlayTime(string status)
    {
        await WriteLogAsync(CompletedRail.Replace("ended with Completed", "ended with " + status, StringComparison.Ordinal));
        var configuration = CreateConfiguration();
        var plan = await new PlayHistoryRecoveryService(_root).PreviewAsync(configuration);
        Assert.False(plan.HasChanges);
        Assert.Equal(0, plan.CompletedSessionCount);
        Assert.Equal(1, plan.ExcludedSessionCount);
        Assert.Equal(0, configuration.Games[0].TotalPlaySeconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingEitherBoundaryDoesNotInventAStartOrDuration(bool startOnly)
    {
        var lines = CompletedRail.Split('\n');
        await WriteLogAsync(startOnly ? lines[0] : lines[1]);
        var plan = await new PlayHistoryRecoveryService(_root).PreviewAsync(CreateConfiguration());
        Assert.False(plan.HasChanges);
        Assert.Equal(1, plan.IncompleteSessionCount);
        Assert.Equal(0, plan.CompletedSessionCount);
    }

    [Fact]
    public async Task ConflictingEvidenceForTheSameSessionIdIsExcluded()
    {
        await WriteLogAsync(CompletedRail);
        await WriteLogAsync(CompletedRail.Replace("00:04:20.5809640", "00:04:21.5809640", StringComparison.Ordinal),
            "asterlauncher-20260927.log");
        var plan = await new PlayHistoryRecoveryService(_root).PreviewAsync(CreateConfiguration());
        Assert.False(plan.HasChanges);
        Assert.Equal(1, plan.ExcludedSessionCount);
    }

    [Theory]
    [InlineData("00:04:20.5809640", "31.00:00:00")]
    [InlineData("00:04:20.5809640", "NaN")]
    [InlineData("00:04:20.5809640", "-00:04:20")]
    [InlineData("00:04:20.5809640", "00:14:20.5809640")]
    [InlineData("2026-09-26", "9999-09-26")]
    public async Task InvalidOrInconsistentTimingIsNotRestored(string oldValue, string newValue)
    {
        await WriteLogAsync(CompletedRail.Replace(oldValue, newValue, StringComparison.Ordinal));
        var plan = await new PlayHistoryRecoveryService(_root).PreviewAsync(CreateConfiguration());
        Assert.False(plan.HasChanges);
        Assert.Equal(0, plan.CompletedSessionCount);
    }

    [Fact]
    public async Task AnUnterminatedLastLineFromAnActiveWriteIsNotTreatedAsACompletedSession()
    {
        var path = Path.Combine(_root, "logs", "asterlauncher-20260926.log");
        await File.WriteAllTextAsync(path, CompletedRail);
        var configuration = CreateConfiguration();
        var service = new PlayHistoryRecoveryService(_root);
        var partial = await service.PreviewAsync(configuration);
        Assert.False(partial.HasChanges);
        Assert.Equal(1, partial.IncompleteSessionCount);

        await File.AppendAllTextAsync(path, Environment.NewLine);
        var complete = await service.PreviewAsync(configuration);
        Assert.True(complete.HasChanges);
        Assert.Equal(1, complete.CompletedSessionCount);
    }
    [Fact]
    public async Task OnlyTheCurrentDataRootsTopLevelNamedLogsAreRead()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "logs", "other.log"), CompletedRail);
        await File.WriteAllTextAsync(Path.Combine(_root, "asterlauncher-20260926.log"), CompletedRail);
        Directory.CreateDirectory(Path.Combine(_root, "logs", "nested"));
        await File.WriteAllTextAsync(Path.Combine(_root, "logs", "nested", "asterlauncher-20260926.log"), CompletedRail);
        var plan = await new PlayHistoryRecoveryService(_root).PreviewAsync(CreateConfiguration());
        Assert.Equal(0, plan.ScannedLogCount);
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public async Task OversizedLogsAreSkippedAndReportedWhileOtherLogsRemainRecoverable()
    {
        await WriteLogAsync(CompletedRail);
        await using (var large = File.Create(Path.Combine(_root, "logs", "asterlauncher-20260927.log")))
            large.SetLength(8 * 1024 * 1024 + 1);
        var plan = await new PlayHistoryRecoveryService(_root).PreviewAsync(CreateConfiguration());
        Assert.True(plan.HasChanges);
        Assert.Equal(1, plan.CompletedSessionCount);
        Assert.Equal(1, plan.SkippedLogCount);
        Assert.True(plan.ReachedScanLimit);
    }

    [Fact]
    public async Task DeletedCustomGamesAreNotRecreatedFromTimingsAlone()
    {
        await WriteLogAsync(CompletedRail.Replace("honkai-star-rail", "custom-0123456789abcdef0123456789abcdef", StringComparison.Ordinal));
        var configuration = CreateConfiguration();
        var plan = await new PlayHistoryRecoveryService(_root).PreviewAsync(configuration);
        Assert.Equal(1, plan.CompletedSessionCount);
        Assert.False(plan.HasChanges);
        Assert.Empty(PlayHistoryRecoveryService.Apply(configuration, plan));
        Assert.Equal(2, configuration.Games.Count);
    }

    [Fact]
    public async Task CancellationDoesNotModifyConfiguration()
    {
        await WriteLogAsync(CompletedRail);
        var configuration = CreateConfiguration();
        var before = JsonSerializer.Serialize(configuration);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new PlayHistoryRecoveryService(_root).PreviewAsync(configuration, cancellation.Token));
        Assert.Equal(before, JsonSerializer.Serialize(configuration));
    }

    private static LauncherConfiguration CreateConfiguration() => new()
    {
        Games =
        [
            new GameUserState { GameId = BuiltInGameIds.HonkaiStarRail },
            new GameUserState { GameId = BuiltInGameIds.Endfield }
        ]
    };

    private Task WriteLogAsync(string content, string name = "asterlauncher-20260926.log") =>
        File.WriteAllTextAsync(Path.Combine(_root, "logs", name), content + Environment.NewLine);

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_root);
        if (!resolved.StartsWith(Path.GetFullPath(FixtureBase) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Recovery fixture escaped its workspace directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
