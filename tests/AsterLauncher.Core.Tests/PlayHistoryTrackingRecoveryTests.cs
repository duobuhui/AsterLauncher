using System.Globalization;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class PlayHistoryTrackingRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "play-history-tracking-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Started = DateTimeOffset.Parse("2026-09-26T10:00:00+08:00");
    private static string Log
    {
        get
        {
            const string id = "84431e34-20be-4117-8c21-e6211e26e834";
            return Started.ToString("O", CultureInfo.InvariantCulture)
                + " [Information] AsterLauncher.Core.GameLaunchOrchestrator: Launch session " + id + " started for test-game using profile test"
                + Environment.NewLine + Started.AddSeconds(100).ToString("O", CultureInfo.InvariantCulture)
                + " [Information] AsterLauncher.Core.GameLaunchOrchestrator: Launch session " + id + " ended with Completed after 00:01:40"
                + Environment.NewLine;
        }
    }

    private async Task<LauncherConfiguration> ConfigurationAsync(int offsetSeconds, double duration, string? trackingId)
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        await File.WriteAllTextAsync(Path.Combine(_root, "logs", "asterlauncher-20260926.log"), Log);
        return new LauncherConfiguration
        {
            Games =
            [
                new()
                {
                    GameId = "test-game", TotalPlaySeconds = double.IsFinite(duration) ? Math.Max(0, duration) : 0,
                    PlaySessions = [new() { StartedAt = Started.AddSeconds(offsetSeconds), DurationSeconds = duration, TrackingId = trackingId }]
                }
            ]
        };
    }

    [Theory]
    [InlineData(10, 40)]
    [InlineData(-10, 30)]
    [InlineData(90, 30)]
    public async Task LogIncludingSetupOrExitTimeCannotExtendOverlappingRealProcessHistory(int offset, double duration)
    {
        var configuration = await ConfigurationAsync(offset, duration, new string('a', 64));
        var plan = await new PlayHistoryRecoveryService(_root).PreviewAsync(configuration);
        Assert.False(plan.HasChanges);
        Assert.Empty(PlayHistoryRecoveryService.Apply(configuration, plan));
        Assert.Single(configuration.Games[0].PlaySessions);
        Assert.Equal(duration, configuration.Games[0].TotalPlaySeconds);
        Assert.Equal(new string('a', 64), configuration.Games[0].PlaySessions[0].TrackingId);
    }

    [Theory]
    [InlineData(-60, 10)]
    [InlineData(150, 10)]
    [InlineData(-10, 10)]
    [InlineData(100, 10)]
    public async Task DisjointOrExactlyAdjacentTrackedSessionDoesNotBlockLegacyRecovery(int offset, double duration)
    {
        var configuration = await ConfigurationAsync(offset, duration, new string('a', 64));
        var service = new PlayHistoryRecoveryService(_root);
        var plan = await service.PreviewAsync(configuration);
        Assert.True(plan.HasChanges);
        Assert.Single(PlayHistoryRecoveryService.Apply(configuration, plan));
        Assert.Equal(2, configuration.Games[0].PlaySessions.Count);
        Assert.Equal(duration + 100, configuration.Games[0].TotalPlaySeconds);
        Assert.Empty(PlayHistoryRecoveryService.Apply(configuration, plan));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-tracking-id")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task UntrackedOrMalformedTrackingIdRetainsPreviousLogRecoveryBehavior(string? trackingId)
    {
        var configuration = await ConfigurationAsync(10, 40, trackingId);
        var plan = await new PlayHistoryRecoveryService(_root).PreviewAsync(configuration);
        Assert.True(plan.HasChanges);
        Assert.Single(PlayHistoryRecoveryService.Apply(configuration, plan));
        Assert.Equal(140, configuration.Games[0].TotalPlaySeconds);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-5)]
    [InlineData(0)]
    public async Task InvalidOrEmptyTrackedIntervalCannotBlockVerifiedLegacyLog(double duration)
    {
        var configuration = await ConfigurationAsync(10, duration, new string('a', 64));
        var plan = await new PlayHistoryRecoveryService(_root).PreviewAsync(configuration);
        Assert.True(plan.HasChanges);
        Assert.Single(PlayHistoryRecoveryService.Apply(configuration, plan));
        Assert.Equal(100, configuration.Games[0].TotalPlaySeconds);
    }

    public void Dispose()
    {
        var approved = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "play-history-tracking-tests")) + Path.DirectorySeparatorChar;
        var actual = Path.GetFullPath(_root);
        if (actual.StartsWith(approved, StringComparison.OrdinalIgnoreCase) && Directory.Exists(actual))
            Directory.Delete(actual, recursive: true);
    }
}
