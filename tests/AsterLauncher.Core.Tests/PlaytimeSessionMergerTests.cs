using System.Security.Cryptography;
using System.Text;
using AsterLauncher.Core;

namespace AsterLauncher.Core.Tests;

public sealed class PlaytimeSessionMergerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-01T10:00:00+08:00");
    private static readonly DateTimeOffset Now = Start.AddDays(1);
    private static PlaytimeObservation Observation(int startSeconds, int durationSeconds, int pid = 40) => new()
    {
        GameId = "test-game", ExecutablePath = @"E:\Games\Example.exe", ProcessId = pid,
        StartedAt = Start.AddSeconds(startSeconds), ObservedUntil = Start.AddSeconds(startSeconds + durationSeconds),
        SessionId = PlaytimeIdentity.CreateSessionId("test-game", @"E:\Games\Example.exe", pid, Start.AddSeconds(startSeconds))
    };
    private static GameUserState State(double total = 0) => new() { GameId = "test-game", TotalPlaySeconds = total };

    [Fact]
    public void IdentityMatchesWorkerUtf8ContractAndNormalizesOffsetPathAndCase()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "test-game\ne:\\games\\example.exe\n40\n" + Start.UtcTicks)));
        Assert.Equal(expected, Observation(0, 1).SessionId);
        Assert.Equal(expected, PlaytimeIdentity.CreateSessionId(" TEST-GAME ", @"E:\Games\folder\..\EXAMPLE.EXE", 40, Start.ToUniversalTime()));
        Assert.NotEqual(expected, Observation(0, 1, 41).SessionId);
        Assert.NotEqual(expected, Observation(1, 1).SessionId);
    }

    [Fact]
    public void RepeatedHeartbeatExtendsOneSegmentAndFinalReceiptIsIdempotent()
    {
        var state = State(1200);
        Assert.True(PlaytimeSessionMerger.Apply(state, [Observation(0, 10)], Now));
        Assert.True(PlaytimeSessionMerger.Apply(state, [Observation(0, 20)], Now));
        Assert.True(PlaytimeSessionMerger.Apply(state, [Observation(0, 30) with { IsComplete = true }], Now));
        Assert.False(PlaytimeSessionMerger.Apply(state, [Observation(0, 30) with { IsComplete = true }], Now));
        Assert.Equal(1230, state.TotalPlaySeconds);
        Assert.Equal(30, Assert.Single(state.PlaySessions).DurationSeconds);
        Assert.Equal(Observation(0, 1).SessionId, state.PlaySessions[0].TrackingId);
        Assert.Equal(Start, state.LastPlayedAt);
    }

    [Fact]
    public void LegacyOverlapAndMultipleConcurrentProcessesOnlyAddUncoveredUnion()
    {
        var state = State(1000);
        state.PlaySessions =
        [
            new() { StartedAt = Start.AddSeconds(5), DurationSeconds = 10 },
            new() { StartedAt = Start.AddSeconds(10), DurationSeconds = 15 }
        ];
        Assert.True(PlaytimeSessionMerger.Apply(state, [Observation(0, 20), Observation(10, 20, 41), Observation(0, 20)], Now));
        Assert.Equal(1010, state.TotalPlaySeconds);
        Assert.Equal(4, state.PlaySessions.Count);
        Assert.Equal(Start.AddSeconds(10), state.LastPlayedAt);
        Assert.False(PlaytimeSessionMerger.Apply(state, [Observation(0, 20), Observation(10, 20, 41)], Now));
    }

    [Fact]
    public void DisconnectedLegacyCoverageCreatesOnlyGapsAndRetainsLegacyBase()
    {
        var state = State(3600);
        state.PlaySessions = [new() { StartedAt = Start.AddSeconds(10), DurationSeconds = 10 }];
        PlaytimeSessionMerger.Apply(state, [Observation(0, 30)], Now);
        Assert.Equal(3620, state.TotalPlaySeconds);
        Assert.Equal(3, state.PlaySessions.Count);
        Assert.Equal(20, state.PlaySessions.Where(s => s.TrackingId is not null).Sum(s => s.DurationSeconds));
    }

    [Fact]
    public void FullyCoveredObservationStillUsesActualProcessStartAsLastPlayed()
    {
        var state = State(60);
        state.PlaySessions = [new() { StartedAt = Start, DurationSeconds = 60 }];
        Assert.True(PlaytimeSessionMerger.Apply(state, [Observation(10, 10)], Now));
        Assert.Equal(60, state.TotalPlaySeconds);
        Assert.Equal(Start.AddSeconds(10), state.LastPlayedAt);
        Assert.Single(state.PlaySessions);
    }

    [Fact]
    public void HeartbeatTimeNeverReplacesActualStartAndOlderReceiptCannotMoveLastPlayedBack()
    {
        var state = State();
        state.LastPlayedAt = Start.AddHours(1);
        PlaytimeSessionMerger.Apply(state, [Observation(0, 120)], Now);
        Assert.Equal(Start.AddHours(1), state.LastPlayedAt);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("future")]
    [InlineData("negative")]
    [InlineData("schema")]
    [InlineData("game")]
    [InlineData("pid")]
    [InlineData("path")]
    public void InvalidObservationDoesNotMutateState(string invalid)
    {
        var observation = Observation(0, 20);
        observation = invalid switch
        {
            "identity" => observation with { SessionId = new string('0', 64) },
            "future" => observation with { ObservedUntil = Now.AddSeconds(1) },
            "negative" => observation with { ObservedUntil = Start.AddSeconds(-1) },
            "schema" => observation with { SchemaVersion = 2 },
            "game" => observation with { GameId = "different" },
            "pid" => observation with { ProcessId = 0 },
            _ => observation with { ExecutablePath = "../game.exe" }
        };
        var state = State(900);
        Assert.False(PlaytimeSessionMerger.Apply(state, [observation], Now));
        Assert.Equal(900, state.TotalPlaySeconds);
        Assert.Empty(state.PlaySessions);
        Assert.Null(state.LastPlayedAt);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidExistingTotalIsNotSilentlyReplaced(double total)
    {
        var state = State(total);
        Assert.False(PlaytimeSessionMerger.Apply(state, [Observation(0, 20)], Now));
        Assert.Empty(state.PlaySessions);
        Assert.Equal(total, state.TotalPlaySeconds);
    }

    [Fact]
    public void InvalidLegacySessionsDoNotBlockVerifiedNewCoverage()
    {
        var state = State(100);
        state.PlaySessions =
        [
            new() { StartedAt = Start, DurationSeconds = double.NaN },
            new() { StartedAt = Start, DurationSeconds = double.PositiveInfinity },
            new() { StartedAt = Start, DurationSeconds = -1 }
        ];
        Assert.True(PlaytimeSessionMerger.Apply(state, [Observation(0, 20)], Now));
        Assert.Equal(120, state.TotalPlaySeconds);
    }

    [Fact]
    public void UtcIntervalCrossingMidnightRetainsExactTicks()
    {
        var startedAt = Start.Date.AddHours(23).AddMinutes(59);
        var dto = new DateTimeOffset(startedAt, TimeSpan.FromHours(8)).AddTicks(7);
        var observation = Observation(0, 1) with
        {
            StartedAt = dto, ObservedUntil = dto.AddTicks(123456789),
            SessionId = PlaytimeIdentity.CreateSessionId("test-game", @"E:\Games\Example.exe", 40, dto)
        };
        var state = State();
        Assert.True(PlaytimeSessionMerger.Apply(state, [observation], Now));
        Assert.Equal(12.3456789, state.TotalPlaySeconds, 7);
        Assert.False(PlaytimeSessionMerger.Apply(state, [observation], Now));
    }
    [Fact]
    public void CoversUsesEntireExistingIntervalUnionWithoutMutatingHistory()
    {
        var state = State(1000);
        state.PlaySessions =
        [
            new() { StartedAt = Start.AddSeconds(10), DurationSeconds = 10 },
            new() { StartedAt = Start, DurationSeconds = 10 },
            new() { StartedAt = Start.AddSeconds(5), DurationSeconds = 10 }
        ];
        Assert.True(PlaytimeSessionMerger.Covers(state, Observation(0, 20), Now));
        Assert.Equal(1000, state.TotalPlaySeconds);
        Assert.Equal(3, state.PlaySessions.Count);
        Assert.Null(state.LastPlayedAt);
        Assert.False(PlaytimeSessionMerger.Covers(state, Observation(0, 21), Now));
    }

    [Fact]
    public void CoversRejectsEvenOneTickGapAndIgnoresInvalidDatedIntervals()
    {
        var state = State(1000);
        state.PlaySessions =
        [
            new() { StartedAt = Start, DurationSeconds = 10 },
            new() { StartedAt = Start.AddSeconds(10).AddTicks(1), DurationSeconds = 10 },
            new() { StartedAt = Start, DurationSeconds = double.PositiveInfinity }
        ];
        Assert.False(PlaytimeSessionMerger.Covers(state, Observation(0, 20), Now));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void CoversRejectsInvalidTotalsEvenForEmptyObservation(double total)
    {
        var state = State(total);
        Assert.False(PlaytimeSessionMerger.Covers(state, Observation(0, 0), Now));
    }

    [Fact]
    public void CoversAcceptsValidEmptyIntervalButNotFutureOrWrongIdentityOrGame()
    {
        var state = State();
        Assert.True(PlaytimeSessionMerger.Covers(state, Observation(0, 0), Now));
        Assert.False(PlaytimeSessionMerger.Covers(state, Observation(0, 0) with { SessionId = new string('0', 64) }, Now));
        Assert.False(PlaytimeSessionMerger.Covers(state, Observation(0, 0) with { GameId = "other-game" }, Now));
        Assert.False(PlaytimeSessionMerger.Covers(state, Observation(0, 0), Start.AddTicks(-1)));
        Assert.Empty(state.PlaySessions);
    }

}
