namespace AsterLauncher.App.ViewModels;

/// <summary>Coalesces worker reports before posting to the UI, avoiding thousands of queued callbacks.</summary>
internal sealed class MaintenanceProgress<T>(Action<T> receive, Func<T, string> stage, Func<T, bool>? isComplete = null) : IProgress<T>
{
    private readonly IProgress<T> _ui = new Progress<T>(receive);
    private readonly object _gate = new();
    private long _lastTicks;
    private string? _lastStage;
    public void Report(T value)
    {
        lock (_gate)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var current = stage(value);
            if (_lastStage == current && now - _lastTicks < System.Diagnostics.Stopwatch.Frequency / 10 && isComplete?.Invoke(value) != true) return;
            _lastStage = current; _lastTicks = now; _ui.Report(value);
        }
    }
}