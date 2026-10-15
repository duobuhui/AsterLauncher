using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AsterLauncher.Core;

/// <summary>A process observation persisted independently of configuration settlement.</summary>
public sealed record PlaytimeObservation
{
    public string SessionId { get; init; } = string.Empty;
    public string GameId { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset ObservedUntil { get; init; }
    public bool IsComplete { get; init; }
    public int SchemaVersion { get; init; } = 1;
}

public static class PlaytimeIdentity
{
    public static string CreateSessionId(string gameId, string executablePath, int processId, DateTimeOffset startedAt)
    {
        if (string.IsNullOrWhiteSpace(gameId) || gameId.Length > 256 || gameId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(executablePath) || executablePath.Length > 32768
            || executablePath.Any(char.IsControl) || !Path.IsPathFullyQualified(executablePath)
            || processId <= 0 || startedAt < DateTimeOffset.UnixEpoch)
            throw new ArgumentException("Invalid playtime process identity.");
        var fullPath = Path.GetFullPath(executablePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Playtime identity must refer to an executable.");
        var value = gameId.Trim().ToLowerInvariant() + "\n" + fullPath.ToLowerInvariant() + "\n"
            + processId.ToString(CultureInfo.InvariantCulture) + "\n"
            + startedAt.UtcTicks.ToString(CultureInfo.InvariantCulture);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public static bool IsValid(PlaytimeObservation observation, DateTimeOffset? now = null)
    {
        if (observation.SchemaVersion != 1 || !IsSessionId(observation.SessionId)
            || observation.ObservedUntil < observation.StartedAt
            || observation.ObservedUntil > (now ?? DateTimeOffset.UtcNow))
            return false;
        try
        {
            return string.Equals(observation.SessionId,
                CreateSessionId(observation.GameId, observation.ExecutablePath, observation.ProcessId, observation.StartedAt),
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool IsSessionId(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>Imports only interval coverage that has not already been settled for this game.</summary>
public static class PlaytimeSessionMerger
{
    public static bool Apply(GameUserState state, IEnumerable<PlaytimeObservation> observations, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observations);
        if (!double.IsFinite(state.TotalPlaySeconds) || state.TotalPlaySeconds < 0) return false;
        var observedNow = now ?? DateTimeOffset.UtcNow;
        var accepted = observations.Where(o => o is not null
                && string.Equals(o.GameId?.Trim(), state.GameId?.Trim(), StringComparison.OrdinalIgnoreCase)
                && PlaytimeIdentity.IsValid(o, observedNow))
            .OrderBy(o => o.StartedAt).ThenBy(o => o.ObservedUntil).ToArray();
        if (accepted.Length == 0) return false;
        var sessions = state.PlaySessions ?? [];
        var coverage = new List<(long Start, long End)>();
        foreach (var session in sessions)
            if (TryInterval(session, out var interval)) Insert(coverage, interval);
        var addedTicks = 0L;
        var changed = false;
        foreach (var observation in accepted)
        {
            var end = observation.ObservedUntil.UtcTicks;
            var cursor = observation.StartedAt.UtcTicks;
            var pieces = new List<(long Start, long End)>();
            foreach (var existing in coverage)
            {
                if (existing.End <= cursor) continue;
                if (existing.Start >= end) break;
                if (existing.Start > cursor) pieces.Add((cursor, Math.Min(existing.Start, end)));
                cursor = Math.Max(cursor, existing.End);
                if (cursor >= end) break;
            }
            if (cursor < end) pieces.Add((cursor, end));
            foreach (var piece in pieces)
            {
                var adjacent = sessions.LastOrDefault(s => s.TrackingId == observation.SessionId
                    && TryInterval(s, out var interval) && interval.End == piece.Start);
                if (adjacent is not null)
                    adjacent.DurationSeconds = (piece.End - adjacent.StartedAt.UtcTicks) / (double)TimeSpan.TicksPerSecond;
                else
                    sessions.Add(new GamePlaySession
                    {
                        TrackingId = observation.SessionId,
                        StartedAt = new DateTimeOffset(piece.Start, TimeSpan.Zero),
                        DurationSeconds = (piece.End - piece.Start) / (double)TimeSpan.TicksPerSecond
                    });
                addedTicks = checked(addedTicks + piece.End - piece.Start);
                Insert(coverage, piece);
                changed = true;
            }
            if (state.LastPlayedAt is null || observation.StartedAt > state.LastPlayedAt)
            {
                state.LastPlayedAt = observation.StartedAt;
                changed = true;
            }
        }
        if (!changed) return false;
        state.PlaySessions = sessions;
        // Preserve undated legacy time instead of reconstructing the total from dated sessions.
        state.TotalPlaySeconds += addedTicks / (double)TimeSpan.TicksPerSecond;
        return true;
    }

    /// <summary>True only when a valid observation is already represented by durable dated coverage.</summary>
    public static bool Covers(GameUserState state, PlaytimeObservation observation, DateTimeOffset? now = null)
    {
        if (state is null || observation is null || !double.IsFinite(state.TotalPlaySeconds) || state.TotalPlaySeconds < 0
            || !string.Equals(observation.GameId?.Trim(), state.GameId?.Trim(), StringComparison.OrdinalIgnoreCase)
            || !PlaytimeIdentity.IsValid(observation, now))
            return false;
        if (observation.StartedAt == observation.ObservedUntil) return true;
        var coverage = new List<(long Start, long End)>();
        foreach (var session in state.PlaySessions ?? [])
            if (TryInterval(session, out var interval)) Insert(coverage, interval);
        return coverage.Any(interval => interval.Start <= observation.StartedAt.UtcTicks
            && interval.End >= observation.ObservedUntil.UtcTicks);
    }

    private static bool TryInterval(GamePlaySession session, out (long Start, long End) interval)
    {
        interval = default;
        if (session is null || !double.IsFinite(session.DurationSeconds) || session.DurationSeconds <= 0
            || session.StartedAt < DateTimeOffset.UnixEpoch) return false;
        var ticks = session.DurationSeconds * TimeSpan.TicksPerSecond;
        if (!double.IsFinite(ticks) || ticks > DateTimeOffset.MaxValue.UtcTicks - session.StartedAt.UtcTicks) return false;
        var duration = (long)Math.Round(ticks, MidpointRounding.AwayFromZero);
        if (duration <= 0) return false;
        interval = (session.StartedAt.UtcTicks, session.StartedAt.UtcTicks + duration);
        return true;
    }

    private static void Insert(List<(long Start, long End)> coverage, (long Start, long End) value)
    {
        var index = 0;
        while (index < coverage.Count && coverage[index].End < value.Start) index++;
        while (index < coverage.Count && coverage[index].Start <= value.End)
        {
            value = (Math.Min(value.Start, coverage[index].Start), Math.Max(value.End, coverage[index].End));
            coverage.RemoveAt(index);
        }
        coverage.Insert(index, value);
    }
}
