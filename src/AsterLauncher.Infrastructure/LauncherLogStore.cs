using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AsterLauncher.Infrastructure;

public sealed record LauncherLogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message);

public sealed class LauncherLogStore : ILoggerProvider
{
    private const int MaximumEntries = 1_000;
    private readonly ConcurrentQueue<LauncherLogEntry> _entries = new();
    private readonly object _fileLock = new();
    private readonly string _logDirectory;

    public LauncherLogStore()
    {
        _logDirectory = Path.Combine(LauncherDataPaths.ResolveDataDirectory(), "logs");
        Directory.CreateDirectory(_logDirectory);
    }

    public event EventHandler<LauncherLogEntry>? EntryAdded;

    public string CurrentLogPath => Path.Combine(_logDirectory, $"asterlauncher-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new StoreLogger(this, categoryName);

    public IReadOnlyList<LauncherLogEntry> GetSnapshot() => _entries.ToArray();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var rendered = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        var entry = new LauncherLogEntry(DateTimeOffset.Now, level, category, rendered);
        _entries.Enqueue(entry);
        while (_entries.Count > MaximumEntries && _entries.TryDequeue(out _))
        {
        }

        lock (_fileLock)
        {
            File.AppendAllText(
                CurrentLogPath,
                $"{entry.Timestamp:O} [{entry.Level}] {entry.Category}: {entry.Message}{Environment.NewLine}");
        }

        EntryAdded?.Invoke(this, entry);
    }

    private sealed class StoreLogger(LauncherLogStore owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
