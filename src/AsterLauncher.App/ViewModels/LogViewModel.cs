using System.Collections.ObjectModel;
using AsterLauncher.Infrastructure;
using Microsoft.UI.Dispatching;

namespace AsterLauncher.App.ViewModels;

public sealed class LogViewModel
{
    private readonly LauncherLogStore _store;
    private DispatcherQueue? _dispatcherQueue;

    public LogViewModel(LauncherLogStore store)
    {
        _store = store;
        _store.EntryAdded += StoreOnEntryAdded;
    }

    public ObservableCollection<LauncherLogEntry> Entries { get; } = [];

    public string LogPath => _store.CurrentLogPath;

    public void Attach(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        Refresh();
    }

    public void Refresh()
    {
        Entries.Clear();
        foreach (var entry in _store.GetSnapshot().Reverse())
        {
            Entries.Add(entry);
        }
    }

    private void StoreOnEntryAdded(object? sender, LauncherLogEntry entry)
    {
        _dispatcherQueue?.TryEnqueue(() => Entries.Insert(0, entry));
    }
}
