using System.Collections.Concurrent;
using System.Text.Json;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

/// <summary>Small process receipts, separate from the launcher configuration and its write gate.</summary>
public sealed class PlaytimeJournal
{
    public const int MaximumFileBytes = 64 * 1024;
    public const int MaximumReceipts = 4096;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 16
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dataRoot;
    private SemaphoreSlim Gate => Gates.GetOrAdd(_dataRoot, _ => new(1, 1));

    public PlaytimeJournal(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _ = ObserverLockPath; // Reject an existing reparse-point chain before accepting this root.
    }

    public string RequestDirectory => SafeGamePath.Resolve(_dataRoot, "playtime/requests");
    public string ReceiptDirectory => SafeGamePath.Resolve(_dataRoot, "playtime/receipts");
    public string ObserverLockPath => SafeGamePath.Resolve(_dataRoot, "playtime/observer.lock");

    public async Task QueueAsync(PlaytimeObservation request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var initial = request with { ObservedUntil = request.StartedAt, IsComplete = false };
        Validate(initial);
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // A completed receipt is awaiting configuration settlement; do not reacquire its process.
            if (await ReadAsync(initial.SessionId, token).ConfigureAwait(false) is { IsComplete: true }) return;
            var path = RequestPath(initial.SessionId);
            Directory.CreateDirectory(RequestDirectory);
            await WriteAtomicAsync(path, initial, overwrite: false, token).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public async Task<IReadOnlyList<PlaytimeObservation>> ReadAllAsync(CancellationToken token = default)
    {
        var root = ReceiptDirectory;
        if (!Directory.Exists(root)) return [];
        var result = new List<PlaytimeObservation>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            if (++scanned > MaximumReceipts) break;
            var id = Path.GetFileNameWithoutExtension(file);
            if (!PlaytimeIdentity.IsSessionId(id)) continue;
            var observation = await ReadAsync(id, token).ConfigureAwait(false);
            if (observation is not null) result.Add(observation);
        }
        return result;
    }

    public async Task<PlaytimeObservation?> ReadAsync(string sessionId, CancellationToken token = default)
    {
        var path = ReceiptPath(sessionId);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ReadStreamAsync(stream, sessionId, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Keep malformed or temporarily busy receipts for diagnosis; never replace user configuration.
            return null;
        }
    }

    public async Task WriteAsync(PlaytimeObservation observation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Validate(observation);
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var writeLease = await AcquireWriteLeaseAsync(token).ConfigureAwait(false);
            var existing = await ReadAsync(observation.SessionId, token).ConfigureAwait(false);
            if (existing is not null && (existing.ObservedUntil > observation.ObservedUntil
                || existing.IsComplete && !observation.IsComplete)) return;
            Directory.CreateDirectory(ReceiptDirectory);
            await WriteAtomicAsync(ReceiptPath(observation.SessionId), observation, overwrite: true, token).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Call only after the merged configuration was saved successfully.</summary>
    public async Task<bool> DeleteCompletedAsync(PlaytimeObservation observation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!observation.IsComplete || !PlaytimeIdentity.IsValid(observation)) return false;
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var writeLease = await AcquireWriteLeaseAsync(token).ConfigureAwait(false);
            var path = ReceiptPath(observation.SessionId);
            if (!File.Exists(path)) return false;
            WindowsFileIdentity identity;
            long length;
            DateTime modified;
            // Deny writes/replacement while validating the exact receipt selected for settlement.
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var current = await ReadStreamAsync(stream, observation.SessionId, token).ConfigureAwait(false);
                if (current is not { IsComplete: true } || current.ObservedUntil != observation.ObservedUntil)
                    return false;
                identity = WindowsHardLink.Identity(path);
                length = stream.Length;
                modified = File.GetLastWriteTimeUtc(path);
            }
            // A worker replacement between validation and unlink has a different file identity.
            return WindowsHardLink.DeleteVerifiedEntry(path, identity, length, modified);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return false;
        }
        finally { Gate.Release(); }
    }

    private async Task<FileStream> AcquireWriteLeaseAsync(CancellationToken token)
    {
        var path = SafeGamePath.Resolve(_dataRoot, "playtime/receipts.write.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task<PlaytimeObservation?> ReadStreamAsync(FileStream stream, string sessionId, CancellationToken token)
    {
        if (stream.Length is <= 0 or > MaximumFileBytes) return null;
        var observation = await JsonSerializer.DeserializeAsync<PlaytimeObservation>(stream, JsonOptions, token).ConfigureAwait(false);
        return observation is not null && observation.SessionId == sessionId && PlaytimeIdentity.IsValid(observation)
            ? observation : null;
    }

    private async Task WriteAtomicAsync(string path, PlaytimeObservation observation, bool overwrite, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(observation, JsonOptions);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Playtime observation exceeds its size limit.");
        var parent = Path.GetDirectoryName(path)!;
        var temporary = SafeGamePath.Resolve(parent, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            try
            {
                // Move(overwrite) cannot replace an open destination on Windows, even with
                // delete sharing. Replace preserves an open reader's old file snapshot.
                if (overwrite && File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (IOException) when (!overwrite && File.Exists(path)) { }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string RequestPath(string sessionId) => SafeGamePath.Resolve(RequestDirectory, FileName(sessionId));
    private string ReceiptPath(string sessionId) => SafeGamePath.Resolve(ReceiptDirectory, FileName(sessionId));
    private static string FileName(string sessionId) => PlaytimeIdentity.IsSessionId(sessionId)
        ? sessionId + ".json" : throw new ArgumentException("Invalid playtime receipt identity.");
    private static void Validate(PlaytimeObservation observation)
    {
        if (!PlaytimeIdentity.IsValid(observation)) throw new InvalidDataException("Invalid playtime observation.");
    }
}
