using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AsterLauncher.Core;
using Microsoft.Win32.SafeHandles;

namespace AsterLauncher.Infrastructure;

public sealed record PlaytimeTarget(string GameId, string ExecutablePath);
public sealed record PlaytimeProcessScan(IReadOnlyList<PlaytimeObservation> Processes, IReadOnlySet<string> UnavailableGameIds);
public sealed record PlaytimePollResult(IReadOnlyList<PlaytimeObservation> Observations, IReadOnlySet<string> RunningGameIds,
    IReadOnlySet<string> UnavailableGameIds)
{
    public IReadOnlySet<string> PendingSessionIds { get; init; } = new HashSet<string>();
}

/// <summary>Reads registered local process identities and delegates lifetime observation to one bounded helper.</summary>
public sealed class PlaytimeTrackingService : IDisposable
{
    private readonly PlaytimeJournal _journal;
    private readonly Func<IReadOnlyList<PlaytimeTarget>, PlaytimeProcessScan> _scan;
    private readonly Action _startObserver;
    private readonly Func<bool> _observerRunning;
    private readonly HashSet<string> _requested = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _requestedAt = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastStartAttempt = DateTimeOffset.MinValue;
    private bool _observerFailed;
    private bool _disposed;
    private sealed record PendingProcess(PlaytimeObservation Identity, SafeProcessHandle Handle, DateTimeOffset PinnedAt);
    private readonly Dictionary<string, PendingProcess> _pendingProcesses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);

    public PlaytimeTrackingService(string dataRoot, string observerScript)
    {
        _journal = new PlaytimeJournal(dataRoot);
        _scan = ScanPinned;
        _startObserver = () => StartObserver(dataRoot, observerScript);
        _observerRunning = () => IsObserverRunning(_journal.ObserverLockPath);
    }

    internal PlaytimeTrackingService(PlaytimeJournal journal,
        Func<IReadOnlyList<PlaytimeTarget>, PlaytimeProcessScan> scan, Action startObserver, Func<bool> observerRunning)
    {
        _journal = journal; _scan = scan; _startObserver = startObserver; _observerRunning = observerRunning;
    }

    public async Task<PlaytimePollResult> PollAsync(IEnumerable<PlaytimeTarget> targets, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var candidates = targets.Where(target => !string.IsNullOrWhiteSpace(target.GameId)
                    && Path.IsPathFullyQualified(target.ExecutablePath) && File.Exists(target.ExecutablePath))
                .DistinctBy(target => target.GameId.ToLowerInvariant() + "\n" + Path.GetFullPath(target.ExecutablePath).ToLowerInvariant()).ToArray();
            var scan = await Task.Run(() => _scan(candidates), token).ConfigureAwait(false);
            // A discovered process may exit before PowerShell can open it. The pinned query handle
            // still supplies its exact end; the shared receipt lease serializes this with the worker.
            foreach (var pinned in _pendingProcesses.Values)
                if (GetProcessTimes(pinned.Handle, out var created, out var exited, out _, out _)
                    && exited > 0 && DateTimeOffset.FromFileTime(created).UtcTicks == pinned.Identity.StartedAt.UtcTicks)
                    await _journal.WriteAsync(pinned.Identity with
                    {
                        ObservedUntil = DateTimeOffset.FromFileTime(exited).ToUniversalTime(), IsComplete = true
                    }, token).ConfigureAwait(false);
            var observations = await _journal.ReadAllAsync(token).ConfigureAwait(false);
            var completed = observations.Where(item => item.IsComplete).Select(item => item.SessionId).ToHashSet(StringComparer.Ordinal);
            var unavailable = new HashSet<string>(scan.UnavailableGameIds, StringComparer.OrdinalIgnoreCase);
            var running = scan.Processes.Select(item => item.GameId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var observerRunning = _observerRunning();
            if (observerRunning) _observerFailed = false;
            foreach (var process in scan.Processes)
            {
                if (completed.Contains(process.SessionId)) continue;
                var previous = observations.FirstOrDefault(item => item.SessionId == process.SessionId);
                var retryDue = _requestedAt.TryGetValue(process.SessionId, out var lastRequest)
                    && DateTimeOffset.UtcNow - lastRequest > TimeSpan.FromSeconds(15)
                    && (previous is null || DateTimeOffset.UtcNow - previous.ObservedUntil > TimeSpan.FromSeconds(40));
                if (!_requested.Contains(process.SessionId) || !observerRunning || retryDue)
                {
                    var requestedAt = DateTimeOffset.UtcNow;
                    await _journal.QueueAsync(process, token).ConfigureAwait(false);
                    _requested.Add(process.SessionId);
                    _requestedAt[process.SessionId] = requestedAt;
                }
            }
            if (scan.Processes.Count > 0 && !observerRunning)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _lastStartAttempt >= TimeSpan.FromSeconds(15))
                {
                    _lastStartAttempt = now;
                    try { _startObserver(); }
                    catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                    { _observerFailed = true; }
                }
            }
            foreach (var process in scan.Processes)
            {
                var receipt = observations.FirstOrDefault(item => item.SessionId == process.SessionId);
                if (receipt is null && _requestedAt.TryGetValue(process.SessionId, out var requestedAt)
                    && DateTimeOffset.UtcNow - requestedAt > TimeSpan.FromSeconds(5)
                    || receipt is { IsComplete: false } && DateTimeOffset.UtcNow - receipt.ObservedUntil > TimeSpan.FromSeconds(40))
                    unavailable.Add(process.GameId);
            }
            if (!observerRunning && (_observerFailed || DateTimeOffset.UtcNow - _lastStartAttempt > TimeSpan.FromSeconds(5)))
                unavailable.UnionWith(running);
            var pending = new HashSet<string>(StringComparer.Ordinal);
            foreach (var identity in scan.Processes.Concat(_pendingProcesses.Values.Select(item => item.Identity)).DistinctBy(item => item.SessionId).ToArray())
            {
                var receipt = observations.FirstOrDefault(item => item.SessionId == identity.SessionId);
                var acknowledged = receipt is not null && (receipt.IsComplete
                    || _requestedAt.TryGetValue(identity.SessionId, out var queuedAt) && receipt.ObservedUntil >= queuedAt);
                if (acknowledged)
                {
                    _acknowledged.Add(identity.SessionId);
                    if (_pendingProcesses.Remove(identity.SessionId, out var pinned)) pinned.Handle.Dispose();
                }
                else pending.Add(identity.SessionId);
            }
            foreach (var pinned in _pendingProcesses.Values.ToArray())
                if (DateTimeOffset.UtcNow - pinned.PinnedAt > TimeSpan.FromSeconds(15))
                {
                    unavailable.Add(pinned.Identity.GameId);
                    _pendingProcesses.Remove(pinned.Identity.SessionId);
                    pinned.Handle.Dispose();
                }
            return new PlaytimePollResult(observations, running, unavailable) { PendingSessionIds = pending };
        }
        finally { _gate.Release(); }
    }

    public async Task<PlaytimePollResult> WaitForObserverAsync(IEnumerable<PlaytimeTarget> targets, CancellationToken token = default)
    {
        var snapshot = targets.ToArray();
        var elapsed = Stopwatch.StartNew();
        var result = await PollAsync(snapshot, token).ConfigureAwait(false);
        while (result.PendingSessionIds.Count > 0 && elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(100, token).ConfigureAwait(false);
            result = await PollAsync(snapshot, token).ConfigureAwait(false);
        }
        if (result.PendingSessionIds.Count > 0)
            result = result with { UnavailableGameIds = result.UnavailableGameIds.Concat(result.RunningGameIds)
                .Concat(_pendingProcesses.Values.Select(item => item.Identity.GameId)).ToHashSet(StringComparer.OrdinalIgnoreCase) };
        return result;
    }

    public async Task DeleteCompletedAsync(PlaytimeObservation observation, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!await _journal.DeleteCompletedAsync(observation, token).ConfigureAwait(false)) return;
            _requested.Remove(observation.SessionId);
            _requestedAt.Remove(observation.SessionId);
            _acknowledged.Remove(observation.SessionId);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pinned in _pendingProcesses.Values) pinned.Handle.Dispose();
            _pendingProcesses.Clear();
        }
        finally { _gate.Release(); }
    }

    private PlaytimeProcessScan ScanPinned(IReadOnlyList<PlaytimeTarget> targets)
    {
        if (!_observerRunning()) _acknowledged.Clear();
        return ScanCore(targets, identity =>
        {
            if (_acknowledged.Contains(identity.SessionId) || _pendingProcesses.ContainsKey(identity.SessionId)
                || _pendingProcesses.Count >= 64) return;
            var handle = OpenProcess(0x00101000, false, identity.ProcessId);
            if (!handle.IsInvalid && GetProcessTimes(handle, out var created, out _, out _, out _)
                && DateTimeOffset.FromFileTime(created).UtcTicks == identity.StartedAt.UtcTicks)
                _pendingProcesses.Add(identity.SessionId, new PendingProcess(identity, handle, DateTimeOffset.UtcNow));
            else handle.Dispose();
        });
    }

    private static bool IsObserverRunning(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
    }

    private static void StartObserver(string dataRoot, string script)
    {
        if (!File.Exists(script)) throw new FileNotFoundException("游玩计时组件缺失。", script);
        var temporary = SafeGamePath.Resolve(dataRoot, "playtime/temp");
        Directory.CreateDirectory(temporary);
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = temporary };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", Path.GetFullPath(script), "-DataRoot", Path.GetFullPath(dataRoot) })
            start.ArgumentList.Add(argument);
        start.Environment["TEMP"] = temporary;
        start.Environment["TMP"] = temporary;
        start.Environment["PSModuleAnalysisCachePath"] = Path.Combine(temporary, "ModuleAnalysisCache");
        using var process = Process.Start(start) ?? throw new IOException("无法启动游玩计时组件。");
    }

    internal static PlaytimeProcessScan Scan(IReadOnlyList<PlaytimeTarget> targets) => ScanCore(targets, null);

    private static PlaytimeProcessScan ScanCore(IReadOnlyList<PlaytimeTarget> targets, Action<PlaytimeObservation>? pin)
    {
        var found = new List<PlaytimeObservation>();
        var unavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in targets.GroupBy(target => Path.GetFileNameWithoutExtension(target.ExecutablePath), StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key)) continue;
            foreach (var process in Process.GetProcessesByName(group.Key))
            {
                using (process)
                {
                    try
                    {
                        using var handle = OpenProcess(0x1000, false, process.Id); // Query limited information only.
                        var path = new StringBuilder(32768); var length = path.Capacity;
                        if (handle.IsInvalid || !QueryFullProcessImageName(handle, 0, path, ref length)
                            || !GetProcessTimes(handle, out var created, out _, out _, out _))
                        { unavailable.UnionWith(group.Select(target => target.GameId)); continue; }
                        var actual = Path.GetFullPath(path.ToString());
                        var started = DateTimeOffset.FromFileTime(created).ToUniversalTime();
                        foreach (var target in group.Where(target => SameExecutablePath(target.ExecutablePath, actual)))
                        {
                            var identity = new PlaytimeObservation
                            {
                                GameId = target.GameId, ExecutablePath = actual, ProcessId = process.Id, StartedAt = started,
                                ObservedUntil = started, SessionId = PlaytimeIdentity.CreateSessionId(target.GameId, actual, process.Id, started)
                            };
                            pin?.Invoke(identity); // The original query handle remains alive across the handoff.
                            found.Add(identity);
                        }
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
                    { unavailable.UnionWith(group.Select(target => target.GameId)); }
                }
            }
        }
        return new PlaytimeProcessScan(found, unavailable);
    }

    internal static bool SameExecutablePath(string registered, string actual)
        => string.Equals(Path.GetFullPath(registered), Path.GetFullPath(actual), StringComparison.OrdinalIgnoreCase)
            || string.Equals(ResolvePhysicalExecutablePath(registered), ResolvePhysicalExecutablePath(actual), StringComparison.OrdinalIgnoreCase);

    internal static string ResolvePhysicalExecutablePath(string path)
    {
        using var file = File.OpenHandle(Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var final = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(file, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        var value = final.ToString();
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + value[8..];
        return value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}