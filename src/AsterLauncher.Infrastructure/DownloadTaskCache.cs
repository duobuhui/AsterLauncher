using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AsterLauncher.Infrastructure;

public sealed record DownloadCacheCleanup(int DeletedFiles,long DeletedBytes,int RetainedObjects);

/// <summary>Persistent content references, independent of expiring download URLs.</summary>
public sealed class DownloadTaskCache(string dataRoot)
{
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> Gates=new(StringComparer.OrdinalIgnoreCase);
    private string Root => SafeGamePath.Resolve(dataRoot,"endfield/tasks");
    private SemaphoreSlim Gate=>Gates.GetOrAdd(Path.GetFullPath(dataRoot),_=>new(1,1));
    public static string ContentKey(string md5,long size)
    {
        if(md5.Length!=32||!md5.All(Uri.IsHexDigit)||size<0)throw new InvalidDataException("无效的缓存内容标识。");
        return md5.ToLowerInvariant()+"-"+size.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    public async Task TrackAsync(string task,IEnumerable<string> keys,CancellationToken token=default)
    {
        using var operation=LocalStorageGate.BeginOperation(dataRoot);
        await Gate.WaitAsync(token);
        try
        {
            var path=TaskPath(task); var content=keys.Concat(await ReadTaskAsync(path,token)).Distinct(StringComparer.Ordinal).ToArray();
            if(content.Any(k=>!ValidKey(k)))throw new InvalidDataException("无效的缓存内容标识。");
            Directory.CreateDirectory(Root);
            var temp=SafeGamePath.Resolve(Root,task+".json.tmp");
            await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(content),token);
            File.Move(temp,path,true);
        }
        finally{Gate.Release();}
    }
    public async Task<IReadOnlySet<string>> ReferencedAsync(CancellationToken token=default)
    {
        await Gate.WaitAsync(token);
        try{return await ReferencesExceptAsync(null,token);}
        finally{Gate.Release();}
    }
    public async Task ForgetAsync(string task,CancellationToken token=default)
    {
        await Gate.WaitAsync(token);
        try{var path=TaskPath(task);if(File.Exists(path))File.Delete(path);}
        finally{Gate.Release();}
    }
    public async Task<DownloadCacheCleanup> CancelAsync(string task,CancellationToken token=default)
    {
        // Caller stops and awaits all task workers before entering here.
        using var cleaning=LocalStorageGate.BeginOperation(dataRoot);
        await Gate.WaitAsync(token);
        try
        {
            var path=TaskPath(task); var owned=await ReadTaskAsync(path,token);
            var referenced=await ReferencesExceptAsync(path,token);
            var count=0;long bytes=0;var retained=0;
            foreach(var key in owned)
            {
                token.ThrowIfCancellationRequested();
                if(referenced.Contains(key)){retained++;continue;}
                foreach(var suffix in new[]{"",".part"})
                {
                    var file=SafeGamePath.Resolve(dataRoot,"endfield/objects/"+key+suffix);
                    if(!File.Exists(file))continue;
                    var info=new FileInfo(file);var length=info.Length;var modified=info.LastWriteTimeUtc;var identity=WindowsHardLink.Identity(file);
                    // Only unlink our cache entry; never change shared content or attributes.
                    if(!WindowsHardLink.DeleteVerifiedEntry(file,identity,length,modified))throw new IOException("缓存文件在清理前发生变化，请重试。");
                    count++;bytes+=length;
                }
            }
            if(File.Exists(path))File.Delete(path);
            return new(count,bytes,retained);
        }
        finally{Gate.Release();}
    }
    private async Task<HashSet<string>> ReferencesExceptAsync(string? excluded,CancellationToken token)
    {
        var result=new HashSet<string>(StringComparer.Ordinal);
        if(!Directory.Exists(Root))return result;
        foreach(var file in Directory.EnumerateFiles(Root,"*.json",SearchOption.TopDirectoryOnly))
        {
            if(string.Equals(file,excluded,StringComparison.OrdinalIgnoreCase))continue;
            var safe=SafeGamePath.Resolve(Root,Path.GetFileName(file));
            result.UnionWith(await ReadTaskAsync(safe,token));
        }
        return result;
    }
    private static async Task<string[]> ReadTaskAsync(string path,CancellationToken token)
    {
        if(!File.Exists(path))return [];
        try
        {
            var values=JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(path,token));
            if(values is null||values.Any(k=>!ValidKey(k)))throw new InvalidDataException("缓存任务索引损坏，保留缓存；请检查任务记录。");
            return values;
        }
        catch(JsonException ex){throw new InvalidDataException("缓存任务索引损坏，保留缓存；请检查任务记录。",ex);}
    }
    private string TaskPath(string task)
    {
        if(!Regex.IsMatch(task,@"\A[0-9a-f]{32}-(install|preload)\z"))throw new ArgumentException("无效的任务标识。");
        return SafeGamePath.Resolve(Root,task+".json");
    }
    private static bool ValidKey(string key)=>Regex.IsMatch(key,@"\A[0-9a-f]{32}-[0-9]+\z");
}