using System.Collections.Concurrent;

namespace AsterLauncher.Infrastructure;

// In-process guard shared by download, maintenance, OTA and storage cleanup.
public static class LocalStorageGate
{
    private sealed class State { public int Operations; public bool Cleaning; }
    private static readonly ConcurrentDictionary<string,State> States=new(StringComparer.OrdinalIgnoreCase);
    public static IDisposable BeginOperation(string dataRoot) => Enter(dataRoot,false);
    public static IDisposable BeginCleaning(string dataRoot) => Enter(dataRoot,true);
    private static IDisposable Enter(string dataRoot,bool cleaning)
    {
        var state=States.GetOrAdd(Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar),_=>new());
        lock(state)
        {
            if(state.Cleaning || cleaning&&state.Operations>0)
                throw new InvalidOperationException("下载、维护或清理正在进行，请在操作结束后重试。");
            if(cleaning)state.Cleaning=true;else state.Operations++;
        }
        return new Lease(state,cleaning);
    }
    private sealed class Lease(State state,bool cleaning):IDisposable
    {
        private int _disposed;
        public void Dispose(){if(Interlocked.Exchange(ref _disposed,1)!=0)return;lock(state){if(cleaning)state.Cleaning=false;else state.Operations--;}}
    }
}