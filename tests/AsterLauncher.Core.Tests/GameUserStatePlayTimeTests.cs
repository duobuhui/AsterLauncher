using AsterLauncher.Core;

namespace AsterLauncher.Core.Tests;

public sealed class GameUserStatePlayTimeTests
{
    [Fact]
    public void WeeklyAndLastSessionSummariesUseRecordedSessions()
    {
        var offset = TimeSpan.FromHours(8);
        var now = new DateTimeOffset(2026, 9, 23, 20, 0, 0, offset);
        var state = new GameUserState
        {
            PlaySessions =
            [
                new GamePlaySession { StartedAt = new DateTimeOffset(2026, 9, 20, 22, 0, 0, offset), DurationSeconds = 7200 },
                new GamePlaySession { StartedAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, offset), DurationSeconds = 3600 },
                new GamePlaySession { StartedAt = new DateTimeOffset(2026, 9, 23, 18, 0, 0, offset), DurationSeconds = 1800 },
                new GamePlaySession { StartedAt = new DateTimeOffset(2026, 9, 24, 18, 0, 0, offset), DurationSeconds = 9999 }
            ]
        };

        Assert.Equal(5400, state.GetThisWeekPlaySeconds(now));
        Assert.Equal(9999, state.GetLastSessionPlaySeconds());
    }

    [Fact]
    public void EmptyHistoryHasNoLastSessionAndZeroWeeklyTime()
    {
        var state = new GameUserState();

        Assert.Null(state.GetLastSessionPlaySeconds());
        Assert.Equal(0, state.GetThisWeekPlaySeconds(DateTimeOffset.Now));
    }
}
