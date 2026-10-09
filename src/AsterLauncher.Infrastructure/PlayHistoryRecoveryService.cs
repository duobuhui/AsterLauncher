using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

public sealed record PlayHistoryRecoveryChange(
    string GameId,
    int AddedSessionCount,
    double AddedSessionSeconds,
    double PreviousTotalSeconds,
    double RecoveredTotalSeconds)
{
    public double TotalIncreaseSeconds => Math.Max(0, RecoveredTotalSeconds - PreviousTotalSeconds);
}

public sealed class PlayHistoryRecoveryPlan
{
    internal PlayHistoryRecoveryPlan(
        IReadOnlyList<PlayHistoryRecoveryChange> games,
        IReadOnlyList<RecoveredPlaySession> sessions,
        int scannedLogCount,
        int incompleteSessionCount,
        int excludedSessionCount,
        int skippedLogCount,
        bool reachedScanLimit)
    {
        Games = games;
        Sessions = sessions;
        ScannedLogCount = scannedLogCount;
        IncompleteSessionCount = incompleteSessionCount;
        ExcludedSessionCount = excludedSessionCount;
        SkippedLogCount = skippedLogCount;
        ReachedScanLimit = reachedScanLimit;
    }

    public IReadOnlyList<PlayHistoryRecoveryChange> Games { get; }
    public bool HasChanges => Games.Count > 0;
    public int ScannedLogCount { get; }
    public int CompletedSessionCount => Sessions.Count;
    public int IncompleteSessionCount { get; }
    public int ExcludedSessionCount { get; }
    public int SkippedLogCount { get; }
    public bool ReachedScanLimit { get; }
    internal IReadOnlyList<RecoveredPlaySession> Sessions { get; }
}

internal sealed record RecoveredPlaySession(
    string GameId, Guid SessionId, DateTimeOffset StartedAt, double DurationSeconds);

/// <summary>
/// Reconstructs only completed launcher-owned sessions from this data root's logs.
/// It never writes files, follows linked paths or retains arbitrary log content.
/// </summary>
public sealed class PlayHistoryRecoveryService
{
    private const int MaximumLogs = 256;
    private const int MaximumDirectoryEntries = 1024;
    private const long MaximumLogBytes = 8 * 1024 * 1024;
    private const long MaximumTotalBytes = 64 * 1024 * 1024;
    private const int MaximumLines = 250_000;
    private const int MaximumSessions = 25_000;
    private const int MaximumLineCharacters = 4096;
    private static readonly TimeSpan MaximumSessionDuration = TimeSpan.FromDays(30);
    private static readonly TimeSpan StartTolerance = TimeSpan.FromMilliseconds(250);
    private const double DurationToleranceSeconds = 0.001;

    private static readonly Regex LogName = new(
        @"^asterlauncher-(?<date>\d{8})\.log$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private const string LogPrefix =
        @"^(?<timestamp>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}[+-]\d{2}:\d{2}) \[Information\] AsterLauncher\.Core\.GameLaunchOrchestrator: Launch session (?<id>[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}) ";
    private static readonly Regex StartLine = new(
        LogPrefix + @"started for (?<game>[a-z0-9-]{1,80}) using profile ",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex EndLine = new(
        LogPrefix + @"ended with (?<status>Completed|Aborted|Cancelled|GameNotDetected) after (?<duration>[0-9:.]{8,30})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly string _logDirectory;

    public PlayHistoryRecoveryService(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _logDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "logs");
    }

    public Task<PlayHistoryRecoveryPlan> PreviewAsync(
        LauncherConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // The caller owns configuration on its UI thread. Snapshot before any
        // await, then keep all directory and stream access on a worker.
        var snapshot = new LauncherConfiguration
        {
            Games = (configuration.Games ?? [])
                .Where(game => game is not null)
                .Select(game => new GameUserState
                {
                    GameId = game.GameId,
                    TotalPlaySeconds = game.TotalPlaySeconds,
                    LastPlayedAt = game.LastPlayedAt,
                    PlaySessions = (game.PlaySessions ?? [])
                        .Where(session => session is not null)
                        .Select(session => new GamePlaySession
                        {
                            StartedAt = session.StartedAt,
                            DurationSeconds = session.DurationSeconds
                        }).ToList()
                }).ToList()
        };
        return Task.Run(() => ScanAsync(snapshot, cancellationToken), cancellationToken);
    }

    public static IReadOnlyList<PlayHistoryRecoveryChange> Apply(
        LauncherConfiguration configuration, PlayHistoryRecoveryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(plan);
        var merges = PrepareMerges(configuration, plan.Sessions);
        // Compute every merge before touching caller state. Saving the candidate
        // and adopting it atomically remains the caller's responsibility.
        foreach (var merge in merges)
        {
            merge.State.PlaySessions = merge.Sessions;
            merge.State.TotalPlaySeconds = merge.Change.RecoveredTotalSeconds;
            merge.State.LastPlayedAt = merge.LastPlayedAt;
        }
        return merges.Select(merge => merge.Change).ToArray();
    }

    private async Task<PlayHistoryRecoveryPlan> ScanAsync(
        LauncherConfiguration configuration, CancellationToken cancellationToken)
    {
        var evidence = new Dictionary<Guid, SessionEvidence>();
        var scanned = 0;
        var skipped = 0;
        var limited = false;
        var lineCount = 0;
        var totalBytes = 0L;
        var now = DateTimeOffset.UtcNow;
        if (!Directory.Exists(_logDirectory))
            return MakePlan();
        if (HasReparseAncestor(_logDirectory))
        {
            skipped++;
            return MakePlan();
        }

        var candidates = Directory.EnumerateFileSystemEntries(_logDirectory, "*", SearchOption.TopDirectoryOnly)
            .Take(MaximumDirectoryEntries + 1).ToArray();
        if (candidates.Length > MaximumDirectoryEntries)
        {
            limited = true;
            candidates = candidates[..MaximumDirectoryEntries];
        }
        var logs = candidates
            .Where(path => IsLogFileName(Path.GetFileName(path)))
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        if (logs.Length > MaximumLogs)
        {
            limited = true;
            logs = logs[..MaximumLogs];
        }

        foreach (var path in logs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lineCount >= MaximumLines || totalBytes >= MaximumTotalBytes || evidence.Count >= MaximumSessions)
            {
                limited = true;
                break;
            }
            try
            {
                if (HasReparseAncestor(path) || !File.Exists(path))
                {
                    skipped++;
                    continue;
                }
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var length = file.Length;
                if (length > MaximumLogBytes || length > MaximumTotalBytes - totalBytes)
                {
                    skipped++;
                    limited = true;
                    continue;
                }

                // The stream cap also handles a log growing during inspection.
                // ReadLineAsync cannot allocate beyond the bounded file budget.
                using var bounded = new BoundedReadStream(file, length);
                using var reader = new StreamReader(bounded, Encoding.UTF8, true, 4096);
                scanned++;
                totalBytes += length;
                string? pendingLine = null;
                while (lineCount < MaximumLines && evidence.Count < MaximumSessions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        // Logging always appends a newline. A snapshot ending in
                        // the middle of an active write is not complete evidence.
                        if (pendingLine is not null && bounded.LastReadByte == (byte)'\n')
                            ParseLine(pendingLine, evidence, now);
                        break;
                    }
                    if (pendingLine is not null) ParseLine(pendingLine, evidence, now);
                    lineCount++;
                    pendingLine = line.Length <= MaximumLineCharacters ? line : null;
                }
                if (lineCount >= MaximumLines || evidence.Count >= MaximumSessions) limited = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }
        return MakePlan();

        PlayHistoryRecoveryPlan MakePlan()
        {
            var complete = evidence.Values
                .Where(item => !item.Conflict && item.Start is not null && item.End is { Status: "Completed" }
                    && IsConsistent(item.Start, item.End))
                .Select(item => new RecoveredPlaySession(item.Start!.GameId, item.Id,
                    item.Start.Timestamp, item.End!.DurationSeconds))
                .OrderBy(item => item.StartedAt).ThenBy(item => item.SessionId).ToArray();
            var incomplete = evidence.Values.Count(item => !item.Conflict && (item.Start is null || item.End is null));
            var excluded = evidence.Count - complete.Length - incomplete;
            var changes = PrepareMerges(configuration, complete).Select(merge => merge.Change).ToArray();
            return new PlayHistoryRecoveryPlan(changes, complete, scanned, incomplete, excluded, skipped, limited);
        }
    }

    private static void ParseLine(string line, Dictionary<Guid, SessionEvidence> sessions, DateTimeOffset now)
    {
        var match = StartLine.Match(line);
        if (match.Success && ReadIdentity(match, now, out var id, out var timestamp))
        {
            if (!sessions.TryGetValue(id, out var evidence)) sessions.Add(id, evidence = new SessionEvidence(id));
            var start = new SessionStart(match.Groups["game"].Value, timestamp);
            if (evidence.Start is not null && evidence.Start != start) evidence.Conflict = true;
            evidence.Start ??= start;
            return;
        }
        match = EndLine.Match(line);
        if (!match.Success || !ReadIdentity(match, now, out id, out timestamp)
            || !TimeSpan.TryParseExact(match.Groups["duration"].Value, "c", CultureInfo.InvariantCulture, out var duration)
            || duration < TimeSpan.Zero || duration > MaximumSessionDuration) return;
        if (!sessions.TryGetValue(id, out var item)) sessions.Add(id, item = new SessionEvidence(id));
        var end = new SessionEnd(timestamp, match.Groups["status"].Value, duration.TotalSeconds);
        if (item.End is not null && item.End != end) item.Conflict = true;
        item.End ??= end;
    }

    private static bool ReadIdentity(Match match, DateTimeOffset now, out Guid id, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return Guid.TryParseExact(match.Groups["id"].Value, "D", out id) && id != Guid.Empty
            && DateTimeOffset.TryParseExact(match.Groups["timestamp"].Value, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out timestamp)
            && timestamp >= new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)
            && timestamp <= now.AddMinutes(5);
    }

    private static bool IsConsistent(SessionStart start, SessionEnd end) =>
        end.Timestamp >= start.Timestamp
        && Math.Abs((end.Timestamp - start.Timestamp).TotalSeconds - end.DurationSeconds) <= 5;

    private static List<PendingMerge> PrepareMerges(
        LauncherConfiguration configuration, IReadOnlyList<RecoveredPlaySession> recovered)
    {
        var result = new List<PendingMerge>();
        foreach (var group in recovered.GroupBy(session => session.GameId, StringComparer.Ordinal))
        {
            var state = (configuration.Games ?? []).FirstOrDefault(game => game is not null && game.GameId == group.Key);
            if (state is null) continue; // A deleted custom game cannot be reconstructed from timing alone.
            var merged = (state.PlaySessions ?? []).ToList();
            var added = new List<GamePlaySession>();
            var index = IndexSessions(merged);
            foreach (var session in group)
            {
                var bucket = session.StartedAt.UtcTicks / StartTolerance.Ticks;
                var duplicate = false;
                for (var nearby = bucket - 1; nearby <= bucket + 1; nearby++)
                    if (index.TryGetValue(nearby, out var matches)
                        && ContainsEquivalentSession(matches, session.StartedAt, session.DurationSeconds))
                    {
                        duplicate = true;
                        break;
                    }
                if (duplicate) continue;
                var restored = new GamePlaySession { StartedAt = session.StartedAt, DurationSeconds = session.DurationSeconds };
                merged.Add(restored);
                added.Add(restored);
                if (!index.TryGetValue(bucket, out var entries)) index.Add(bucket, entries = []);
                entries.Add(restored);
            }
            var datedTotal = SumValidDurations(merged);
            var oldTotal = double.IsFinite(state.TotalPlaySeconds) && state.TotalPlaySeconds >= 0 ? state.TotalPlaySeconds : 0;
            // A legacy total can already include sessions missing from the dated
            // history. Taking the maximum recovers evidence without counting it twice.
            var newTotal = Math.Max(oldTotal, datedTotal);
            if (added.Count == 0 && newTotal <= oldTotal) continue;
            var latest = merged.Where(session => session is not null)
                .Select(session => (DateTimeOffset?)session.StartedAt).Max();
            var lastPlayedAt = state.LastPlayedAt is { } previous && (latest is null || previous > latest) ? previous : latest;
            var change = new PlayHistoryRecoveryChange(group.Key, added.Count, SumValidDurations(added), oldTotal, newTotal);
            result.Add(new PendingMerge(state, merged, lastPlayedAt, change));
        }
        return result;
    }

    private static Dictionary<long, List<GamePlaySession>> IndexSessions(IEnumerable<GamePlaySession> sessions)
    {
        var index = new Dictionary<long, List<GamePlaySession>>();
        foreach (var session in sessions)
        {
            if (session is null) continue;
            var bucket = session.StartedAt.UtcTicks / StartTolerance.Ticks;
            if (!index.TryGetValue(bucket, out var entries)) index.Add(bucket, entries = []);
            entries.Add(session);
        }
        return index;
    }
    public static bool ContainsEquivalentSession(
        IEnumerable<GamePlaySession>? sessions, DateTimeOffset startedAt, double durationSeconds) =>
        sessions?.Any(existing => existing is not null
            && Math.Abs((existing.StartedAt - startedAt).TotalMilliseconds) <= StartTolerance.TotalMilliseconds
            && Math.Abs(existing.DurationSeconds - durationSeconds) <= DurationToleranceSeconds) == true;
    private static double SumValidDurations(IEnumerable<GamePlaySession> sessions)
    {
        var sum = 0.0;
        foreach (var session in sessions)
        {
            if (session is null || !double.IsFinite(session.DurationSeconds) || session.DurationSeconds < 0) continue;
            sum = Math.Min(double.MaxValue, sum + session.DurationSeconds);
        }
        return sum;
    }

    private static bool IsLogFileName(string name)
    {
        var match = LogName.Match(name);
        return match.Success && DateOnly.TryParseExact(match.Groups["date"].Value, "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static bool HasReparseAncestor(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private sealed class SessionEvidence(Guid id)
    {
        public Guid Id { get; } = id;
        public SessionStart? Start { get; set; }
        public SessionEnd? End { get; set; }
        public bool Conflict { get; set; }
    }
    private sealed record SessionStart(string GameId, DateTimeOffset Timestamp);
    private sealed record SessionEnd(DateTimeOffset Timestamp, string Status, double DurationSeconds);
    private sealed record PendingMerge(GameUserState State, List<GamePlaySession> Sessions,
        DateTimeOffset? LastPlayedAt, PlayHistoryRecoveryChange Change);

    private sealed class BoundedReadStream(Stream inner, long remaining) : Stream
    {
        public byte? LastReadByte { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            if (read > 0) LastReadByte = buffer[offset + read - 1];
            remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken).ConfigureAwait(false);
            if (read > 0) LastReadByte = buffer.Span[read - 1];
            remaining -= read;
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
