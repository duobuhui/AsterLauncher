using System.Text.Json;
using System.Text.RegularExpressions;

namespace AsterLauncher.Infrastructure;

public sealed record StorageFile(string Category,string Root,string Relative,long Size,long AllocatedBytes,
    DateTime LastWriteUtc,WindowsFileIdentity Identity,bool IsLegacy)
{
    public string Path=>System.IO.Path.Combine(Root,Relative);
}
public sealed record StorageDirectory(string Category,string Root,string Relative,bool IsLegacy)
{
    public string Path=>Relative.Length==0?Root:System.IO.Path.Combine(Root,Relative);
}
public sealed record StorageScan(IReadOnlyList<StorageFile> Files,int ProtectedFiles,IReadOnlyList<string> Notes)
{
    public IReadOnlyList<StorageDirectory> EmptyDirectories { get; init; } = [];
}
public sealed record StorageDeleteResult(int Deleted,int Skipped,long Bytes,long EstimatedFreedBytes,int DeletedDirectories=0);

/// <summary>Only named launcher-owned locations are scanned. Current data and game installations are never cleanup targets.</summary>
public sealed class LocalStorageService(string dataRoot,string installationRoot,string temporaryRoot)
{
    private sealed record Area(string Category,string Root,bool Legacy,Func<string,bool> Filter);
    public async Task<StorageScan> ScanAsync(CancellationToken token=default)
    {
        var files=new List<StorageFile>();var directories=new List<StorageDirectory>();var notes=new List<string>();var protectedCount=0;
        IReadOnlySet<string> references;
        try{references=await new DownloadTaskCache(dataRoot).ReferencedAsync(token);}
        catch(InvalidDataException){references=new HashSet<string>();notes.Add("下载任务索引损坏，终末地缓存未列入清理，请先恢复任务。");}
        var legacyMaintenance=HasUnindexedMaintenance();
        var protectedPaths=ReadProtectedPaths();
        foreach(var area in Areas())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if(!Directory.Exists(area.Root))continue;
                SafeGamePath.Resolve(area.Root,"scan-check");
                if(!Directory.EnumerateFileSystemEntries(area.Root).Any() && protectedPaths is not null && !protectedPaths.Any(p=>Overlaps(area.Root,p)))
                    directories.Add(new(area.Category,Path.GetFullPath(area.Root),"",area.Legacy));
                var pending=new Stack<string>();pending.Push(area.Root);
                while(pending.TryPop(out var directory))
                {
                    foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        var relative=Path.GetRelativePath(area.Root,entry);
                        try
                        {
                            var safe=SafeGamePath.Resolve(area.Root,relative);
                            if(protectedPaths is null || protectedPaths.Any(p=>Overlaps(safe,p))){protectedCount++;continue;}
                            if(Directory.Exists(safe))
                            {
                                if(!Directory.EnumerateFileSystemEntries(safe).Any())directories.Add(new(area.Category,Path.GetFullPath(area.Root),relative,area.Legacy));
                                else pending.Push(safe);
                                continue;
                            }
                            if(!area.Filter(relative)){protectedCount++;continue;}
                            if(area.Category=="终末地下载缓存")
                            {
                                var key=Path.GetFileNameWithoutExtension(safe);
                                if(!safe.EndsWith(".part",StringComparison.OrdinalIgnoreCase))key=Path.GetFileName(safe);
                                if(notes.Count>0||legacyMaintenance||references.Contains(key)){protectedCount++;continue;}
                            }
                            var info=new FileInfo(safe);var identity=WindowsHardLink.Identity(safe);
                            var allocation=identity.LinkCount>1?0:WindowsHardLink.EstimatedAllocatedBytes(safe);
                            files.Add(new(area.Category,Path.GetFullPath(area.Root),relative,info.Length,allocation,info.LastWriteTimeUtc,identity,area.Legacy));
                        }
                        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException){protectedCount++;}
                    }
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {notes.Add(area.Category+"：目录被占用或包含重解析点，已跳过。");}
        }
        if(legacyMaintenance)notes.Add("检测到旧版未完成下载，暂时保留终末地缓存；继续或取消原任务后可再次扫描。");
        return new(files.DistinctBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).OrderBy(f=>f.Category).ThenBy(f=>f.Path).ToArray(),protectedCount,notes) { EmptyDirectories=directories };
    }
    public Task<StorageDeleteResult> DeleteAsync(IEnumerable<StorageFile> selected,CancellationToken token=default)
        => DeleteAsync(selected,[],token);

    public async Task<StorageDeleteResult> DeleteAsync(IEnumerable<StorageFile> selected,
        IEnumerable<StorageDirectory> selectedDirectories,CancellationToken token=default)
    {
        using var cleaning=LocalStorageGate.BeginCleaning(dataRoot);
        // Re-scan from trusted roots rather than accepting UI paths as authority.
        var current=await ScanAsync(token);var allowed=current.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);
        var deleted=0;var skipped=0;long bytes=0;long freed=0;
        var cleanupRoots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var original in selected.DistinctBy(f=>f.Path,StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if(!allowed.TryGetValue(original.Path,out var file)||!file.Identity.SameFile(original.Identity)
                ||file.Size!=original.Size||file.LastWriteUtc!=original.LastWriteUtc){skipped++;continue;}
            try
            {
                var path=SafeGamePath.Resolve(file.Root,file.Relative);
                var identity=WindowsHardLink.Identity(path);
                var allocated=identity.LinkCount==1?WindowsHardLink.EstimatedAllocatedBytes(path):0;
                if(!WindowsHardLink.DeleteVerifiedEntry(path,file.Identity,file.Size,file.LastWriteUtc)){skipped++;continue;}
                deleted++;bytes+=file.Size;freed+=allocated;cleanupRoots.Add(file.Root);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException){skipped++;}
        }
        var emptyAllowed=current.EmptyDirectories.ToDictionary(d=>d.Path,StringComparer.OrdinalIgnoreCase);
        foreach(var original in selectedDirectories.DistinctBy(d=>d.Path,StringComparer.OrdinalIgnoreCase))
        {
            if(emptyAllowed.TryGetValue(original.Path,out var directory))cleanupRoots.Add(directory.Root);
            else skipped++;
        }
        var protectedPaths=ReadProtectedPaths();
        var deletedDirectories=0;
        if(protectedPaths is not null)
            foreach(var root in cleanupRoots)
                deletedDirectories+=EmptyDirectoryCleanup.Prune(root,p=>protectedPaths.Any(keep=>Overlaps(p,keep)),token);
        return new(deleted,skipped,bytes,freed,deletedDirectories);
    }
    private IEnumerable<Area> Areas()
    {
        yield return new("终末地下载缓存",Path.Combine(dataRoot,"endfield","objects"),false,
            p=>!p.Contains(Path.DirectorySeparatorChar)&&Regex.IsMatch(p,@"\A[0-9a-f]{32}-[0-9]+(?:\.part)?\z"));
        yield return new("旧版 OTA 缓存",Path.Combine(dataRoot,"updates"),false,_=>true);
        yield return new("运行日志",Path.Combine(dataRoot,"logs"),false,p=>p.EndsWith(".log",StringComparison.OrdinalIgnoreCase)||p.EndsWith(".txt",StringComparison.OrdinalIgnoreCase));
        yield return new("云壁纸缓存",Path.Combine(dataRoot,"artwork","cloud"),false,
            p=>new[]{".jpg",".jpeg",".png",".webp",".json"}.Contains(Path.GetExtension(p).ToLowerInvariant()));
        foreach(var root in new[]{Path.Combine(installationRoot,"Data"),Path.Combine(installationRoot,"App","Data")})
            if(!Overlaps(root,dataRoot))yield return new("旧数据目录（含配置与抽卡档案）",root,true,_=>true);
        // An interrupted migration must retain all rollback material.
        if(Directory.Exists(installationRoot)&&!File.Exists(Path.Combine(installationRoot,".aster-migration.json")))
        {
            foreach(var stage in Directory.EnumerateDirectories(installationRoot,".aster-update-*",SearchOption.TopDirectoryOnly))
                if(File.Exists(Path.Combine(stage,"update.log")) || (Regex.IsMatch(Path.GetFileName(stage),@"\A\.aster-update-[0-9a-f]{32}\z") && !Directory.EnumerateFileSystemEntries(stage).Any()))
                    yield return new("已结束的 OTA 暂存",stage,false,_=>true);
            yield return new("旧版程序备份",Path.Combine(installationRoot,"MigrationBackup"),true,_=>true);
        }
        var bundleRoot=Path.Combine(temporaryRoot,".net","AsterLauncher");
        if(Directory.Exists(bundleRoot))
        {
            SafeGamePath.Resolve(bundleRoot,"scan-check");
            foreach(var bundle in Directory.EnumerateDirectories(bundleRoot))
            {
                var legacy=SafeGamePath.Resolve(bundleRoot,Path.GetFileName(bundle)+"/Data");
                if(!Overlaps(legacy,dataRoot)&&(File.Exists(Path.Combine(legacy,"launcher.settings.json")) || (Directory.Exists(legacy) && !Directory.EnumerateFileSystemEntries(legacy).Any())))
                    yield return new("单文件旧版数据（含配置与抽卡档案）",legacy,true,_=>true);
            }
        }
    }
    private IReadOnlyList<string>? ReadProtectedPaths()
    {
        var config=SafeGamePath.Resolve(dataRoot,"launcher.settings.json");
        if(!File.Exists(config))return [];
        try
        {
            using var json=JsonDocument.Parse(File.ReadAllText(config));
            var result=new List<string>();Collect(json.RootElement);return result;
            void Collect(JsonElement element)
            {
                if(element.ValueKind==JsonValueKind.Object){foreach(var p in element.EnumerateObject())Collect(p.Value);}
                else if(element.ValueKind==JsonValueKind.Array){foreach(var e in element.EnumerateArray())Collect(e);}
                else if(element.ValueKind==JsonValueKind.String)
                {
                    var value=element.GetString();if(!string.IsNullOrWhiteSpace(value)&&Path.IsPathFullyQualified(value))
                        try{result.Add(Path.GetFullPath(value));}catch(ArgumentException){ }
                }
            }
        }
        catch(Exception ex) when(ex is IOException or JsonException){return null;}
    }
    private bool HasUnindexedMaintenance()
    {
        var config=SafeGamePath.Resolve(dataRoot,"launcher.settings.json");
        if(!File.Exists(config))return false;
        try
        {
            using var doc=JsonDocument.Parse(File.ReadAllText(config));
            if(!doc.RootElement.TryGetProperty("endfieldInstallations",out var installs))return false;
            foreach(var install in installs.EnumerateArray())
                if(install.TryGetProperty("maintenanceInProgress",out var flag)&&flag.ValueKind==JsonValueKind.True)
                {
                    if(!install.TryGetProperty("installationId",out var id)||!Guid.TryParse(id.GetString(),out var guid)
                        ||!File.Exists(SafeGamePath.Resolve(dataRoot,"endfield/tasks/"+guid.ToString("N")+"-install.json")))return true;
                }
            return false;
        }
        catch(Exception ex) when(ex is IOException or JsonException or InvalidOperationException){return true;}
    }
    private static bool Overlaps(string a,string b)
    {
        a=Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar);b=Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar);
        return a.Equals(b,StringComparison.OrdinalIgnoreCase)||a.StartsWith(b+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
            ||b.StartsWith(a+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
}