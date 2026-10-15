using System.Security.Cryptography;
using System.Text;

namespace AsterLauncher.Infrastructure;

/// <summary>One writable launcher UI per data root; secondary launches only signal activation.</summary>
public sealed class DataInstanceLease : IDisposable
{
    private static readonly object LocalOwnerGate = new();
    private static readonly HashSet<string> LocalOwners = new(StringComparer.Ordinal);
    private readonly string _name;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly RegisteredWaitHandle? _registration;
    private readonly object _callbackGate = new();
    private readonly int _ownerThreadId;
    private bool _disposed;

    public DataInstanceLease(string dataRoot, Action activate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(activate);
        if (!Path.IsPathFullyQualified(dataRoot)) throw new ArgumentException("An absolute data directory is required.", nameof(dataRoot));
        var root = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        _name = "Local\\AsterLauncher.Data." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _mutex = new Mutex(initiallyOwned: false, _name + ".Owner");
        try
        {
            _activation = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, _name + ".Activate");
            lock (LocalOwnerGate)
            {
                // Named mutexes are reentrant on their owner thread. Also reject a second lease
                // in this process so tests or another window cannot accidentally acquire twice.
                if (!LocalOwners.Contains(_name))
                {
                    try { IsPrimary = _mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { IsPrimary = true; }
                    if (IsPrimary) LocalOwners.Add(_name);
                }
            }
            if (IsPrimary)
            {
                _registration = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) =>
                {
                    lock (_callbackGate)
                    {
                        if (!_disposed) activate();
                    }
                }, state: null, Timeout.Infinite, executeOnlyOnce: false);
            }
            else _activation.Set(); // Auto-reset events retain a request until the primary wait is ready.
        }
        catch
        {
            if (IsPrimary)
            {
                lock (LocalOwnerGate) { _mutex.ReleaseMutex(); LocalOwners.Remove(_name); }
            }
            _activation?.Dispose();
            _mutex.Dispose();
            throw;
        }
    }

    public bool IsPrimary { get; }

    public void Dispose()
    {
        if (IsPrimary && Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("Release the data instance lease on its owning UI thread.");
        lock (_callbackGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _registration?.Unregister(null);
        if (IsPrimary)
        {
            lock (LocalOwnerGate) { _mutex.ReleaseMutex(); LocalOwners.Remove(_name); }
        }
        _activation.Dispose();
        _mutex.Dispose();
    }
}