using System.Net;
using System.Security.Cryptography;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class LocalStorageTests
{
    [Fact]
    public async Task CancelDeletesOwnedCompleteAndPartialObjectsButPreservesOtherTaskReferencesAfterRestart()
    {
        var root=Root();try
        {
            var own=DownloadTaskCache.ContentKey(new string('a',32),4);var shared=DownloadTaskCache.ContentKey(new string('b',32),4);
            var first=Guid.NewGuid().ToString("N")+"-install";var second=Guid.NewGuid().ToString("N")+"-preload";
            var cache=new DownloadTaskCache(root);await cache.TrackAsync(first,[own,shared]);await cache.TrackAsync(second,[shared]);
            Write(root,"endfield/objects/"+own,"full");Write(root,"endfield/objects/"+own+".part","pa");Write(root,"endfield/objects/"+shared,"both");
            var result=await new DownloadTaskCache(root).CancelAsync(first);
            Assert.Equal(2,result.DeletedFiles);Assert.Equal(6,result.DeletedBytes);Assert.Equal(1,result.RetainedObjects);
            Assert.True(File.Exists(Path.Combine(root,"endfield","objects",shared)));
            Assert.False(File.Exists(Path.Combine(root,"endfield","objects",own)));
            Assert.Contains(shared,await new DownloadTaskCache(root).ReferencedAsync());
        }
        finally{Remove(root);}
    }
    [Fact]
    public async Task PauseKeepsPartialAndResumeReportsCumulativeBytes()
    {
        var root=Root();try
        {
            var payload=new byte[300000];Random.Shared.NextBytes(payload);var md5=Convert.ToHexString(MD5.HashData(payload));
            var key=DownloadTaskCache.ContentKey(md5,payload.Length);var dir=Path.Combine(root,"endfield","objects");Directory.CreateDirectory(dir);
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            await File.WriteAllBytesAsync(Path.Combine(dir,key+".part"),payload[..100000]);
            using var client=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(payload)}));
            var service=new EndfieldDownloadService(client,root);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.DownloadAsync(new("https://beyond.hycdn.cn/file"),md5,payload.Length,cancellationToken:cancelled.Token));
            Assert.Equal(100000,new FileInfo(Path.Combine(dir,key+".part")).Length);
            var values=new List<long>();await service.DownloadAsync(new("https://beyond.hycdn.cn/file"),md5,payload.Length,byteProgress:new InlineProgress(values.Add));
            Assert.Equal(payload.Length,values.Last());Assert.True(values.Count>=3);Assert.Equal(values.Order(),values);
        }
        finally{Remove(root);}
    }
    [Fact]
    public async Task StorageProtectsCurrentArchivesReferencedArtworkTasksAndChangedFiles()
    {
        var root=Root();try
        {
            var data=Path.Combine(root,"current");var install=Path.Combine(root,"install");
            Write(data,"gacha/uigf-v4.2.json","archive");Write(data,"launcher.settings.json","{}");
            Write(data,"updates/old.zip","cache");Write(data,"logs/app.log","log");
            var artwork=Write(install,"App/Data/custom.png","artwork");Write(install,"App/Data/launcher.settings.json","legacy config");
            await File.WriteAllTextAsync(Path.Combine(data,"launcher.settings.json"),System.Text.Json.JsonSerializer.Serialize(new{games=new[]{new{artworkPath=artwork}}}));
            var cache=new DownloadTaskCache(data);var key=DownloadTaskCache.ContentKey(new string('c',32),4);await cache.TrackAsync(Guid.NewGuid().ToString("N")+"-install",[key]);Write(data,"endfield/objects/"+key,"keep");
            var service=new LocalStorageService(data,install,Path.Combine(root,"temp"));var scan=await service.ScanAsync();
            Assert.DoesNotContain(scan.Files,f=>f.Path==artwork||f.Relative.EndsWith("uigf-v4.2.json")||f.Path==Path.Combine(data,"launcher.settings.json")||f.Path.EndsWith(key));
            Assert.Contains(scan.Files,f=>f.IsLegacy&&f.Relative=="launcher.settings.json");
            var log=scan.Files.Single(f=>f.Relative=="app.log");await File.WriteAllTextAsync(log.Path,"changed after scan");
            var result=await service.DeleteAsync(scan.Files);
            Assert.Equal(1,result.Skipped);Assert.True(File.Exists(log.Path));Assert.True(File.Exists(artwork));
            Assert.True(File.Exists(Path.Combine(data,"gacha","uigf-v4.2.json")));Assert.True(File.Exists(Path.Combine(data,"endfield","objects",key)));
        }
        finally{Remove(root);}
    }
    [Fact]
    public async Task StorageDoesNotReportFreedSpaceForAnExternalHardlinkAndRejectsBusyCleanup()
    {
        var root=Root();try
        {
            var path=Write(root,"updates/item.zip","shared");var other=Path.Combine(root,"other.zip");Assert.True(WindowsHardLink.TryCreate(other,path));
            var service=new LocalStorageService(root,Path.Combine(root,"install"),Path.Combine(root,"temp"));var scan=await service.ScanAsync();
            using(var busy=LocalStorageGate.BeginOperation(root))await Assert.ThrowsAsync<InvalidOperationException>(()=>service.DeleteAsync(scan.Files));
            var result=await service.DeleteAsync(scan.Files);Assert.Equal(1,result.Deleted);Assert.Equal(0,result.EstimatedFreedBytes);Assert.Equal("shared",await File.ReadAllTextAsync(other));
        }
        finally{Remove(root);}
    }
    [Fact]
    public async Task StorageRechecksReparsePointsAndNeverFollowsSymlinkEscape()
    {
        var root=Root();try
        {
            var path=Write(root,"updates/item.zip","cache");var service=new LocalStorageService(root,Path.Combine(root,"install"),Path.Combine(root,"temp"));var scan=await service.ScanAsync();
            var outside=Write(root,"outside/keep.txt","keep");File.Delete(path);Directory.Delete(Path.GetDirectoryName(path)!);
            var link=Path.GetDirectoryName(path)!;
            var start=new System.Diagnostics.ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true};
            start.Environment.Remove("PSModulePath");
            foreach(var arg in new[]{"-NoProfile","-Command","New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+Path.GetDirectoryName(outside)!.Replace("'","''")+"' | Out-Null"})start.ArgumentList.Add(arg);
            using(var process=System.Diagnostics.Process.Start(start)!){await process.WaitForExitAsync();Assert.Equal(0,process.ExitCode);}
            try
            {
                var result=await service.DeleteAsync(scan.Files);Assert.Equal(0,result.Deleted);Assert.Equal(1,result.Skipped);Assert.Equal("keep",await File.ReadAllTextAsync(outside));
            }
            finally{Directory.Delete(link);}        }
        finally{Remove(root);}
    }
    [Fact]
    public async Task CorruptTaskIndexFailsClosedRatherThanDeletingSharedCache()
    {
        var root=Root();try
        {
            var key=DownloadTaskCache.ContentKey(new string('d',32),4);Write(root,"endfield/objects/"+key,"keep");
            Write(root,"endfield/tasks/"+Guid.NewGuid().ToString("N")+"-install.json","not json");
            var scan=await new LocalStorageService(root,Path.Combine(root,"install"),Path.Combine(root,"temp")).ScanAsync();
            Assert.DoesNotContain(scan.Files,f=>f.Path.EndsWith(key));Assert.NotEmpty(scan.Notes);
        }
        finally{Remove(root);}
    }
    [Fact]
    public async Task EmptyLegacyAndUpdateDirectoriesRemainVisibleAndAreRemovedWithoutCurrentData()
    {
        var root=Root();try
        {
            var data=Path.Combine(root,"data");var install=Path.Combine(root,"install");
            Write(data,"launcher.settings.json","{}");Write(data,"gacha/records.json","keep");
            var stage=Path.Combine(install,".aster-update-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(Path.Combine(install,"MigrationBackup","empty","nested"));
            var service=new LocalStorageService(data,install,Path.Combine(root,"temp"));
            var scan=await service.ScanAsync();
            Assert.Contains(scan.EmptyDirectories,d=>d.Path==stage);
            var result=await service.DeleteAsync([],scan.EmptyDirectories);
            Assert.True(result.DeletedDirectories>=4);
            Assert.False(Directory.Exists(stage));
            Assert.False(Directory.Exists(Path.Combine(install,"MigrationBackup")));
            Assert.True(File.Exists(Path.Combine(data,"launcher.settings.json")));
            Assert.Equal("keep",await File.ReadAllTextAsync(Path.Combine(data,"gacha","records.json")));
        }
        finally{Remove(root);}
    }
    [Fact]
    public async Task DeletionPrunesNestedEmptyFoldersButKeepsUnselectedFiles()
    {
        var root=Root();try
        {
            var selected=Write(root,"updates/old/nested/package.zip","old");
            var keep=Write(root,"updates/keep.zip","keep");
            var service=new LocalStorageService(root,Path.Combine(root,"install"),Path.Combine(root,"temp"));
            var scan=await service.ScanAsync();
            var result=await service.DeleteAsync(scan.Files.Where(f=>f.Path==selected));
            Assert.Equal(2,result.DeletedDirectories);
            Assert.False(Directory.Exists(Path.Combine(root,"updates","old")));
            Assert.True(File.Exists(keep));
            scan=await service.ScanAsync();await service.DeleteAsync(scan.Files);
            Assert.False(Directory.Exists(Path.Combine(root,"updates")));
            Assert.True(Directory.Exists(root));
        }
        finally{Remove(root);}
    }
    [Fact]
    public async Task EmptyDirectoryChangedSinceScanIsPreservedAndConfiguredEmptyFolderIsProtected()
    {
        var root=Root();try
        {
            var protectedFolder=Path.Combine(root,"artwork","cloud","custom");
            Directory.CreateDirectory(protectedFolder);
            Write(root,"launcher.settings.json",System.Text.Json.JsonSerializer.Serialize(new{games=new[]{new{artworkPath=protectedFolder}}}));
            Directory.CreateDirectory(Path.Combine(root,"updates","empty"));
            var service=new LocalStorageService(root,Path.Combine(root,"install"),Path.Combine(root,"temp"));
            var scan=await service.ScanAsync();
            Assert.DoesNotContain(scan.EmptyDirectories,d=>d.Path==protectedFolder);
            Write(root,"updates/empty/new.zip","created after scan");
            var result=await service.DeleteAsync([],scan.EmptyDirectories);
            Assert.Equal(0,result.DeletedDirectories);
            Assert.True(result.Skipped>0);
            Assert.True(Directory.Exists(protectedFolder));
            Assert.True(File.Exists(Path.Combine(root,"updates","empty","new.zip")));
        }
        finally{Remove(root);}
    }
    [Fact]
    public async Task CompletedTasksReleaseCacheButKeepPendingPreloadAndInstalledHardlink()
    {
        var root=Root();try
        {
            var own=DownloadTaskCache.ContentKey(new string('a',32),4);
            var shared=DownloadTaskCache.ContentKey(new string('b',32),4);
            var first=Guid.NewGuid().ToString("N")+"-install";var preload=Guid.NewGuid().ToString("N")+"-preload";
            var cache=new DownloadTaskCache(root);
            await cache.TrackAsync(first,[own,shared]);await cache.TrackAsync(preload,[shared]);
            var objectPath=Write(root,"endfield/objects/"+own,"full");
            var installed=Path.Combine(root,"installed-resource");Assert.True(WindowsHardLink.TryCreate(installed,objectPath));
            Write(root,"endfield/objects/"+shared,"next");
            await cache.CompleteAsync(first);
            Assert.False(File.Exists(objectPath));Assert.Equal("full",await File.ReadAllTextAsync(installed));
            Assert.True(File.Exists(Path.Combine(root,"endfield","objects",shared)));
            await cache.CompleteAsync(preload);
            Assert.False(Directory.Exists(Path.Combine(root,"endfield","objects")));
            Assert.False(Directory.Exists(Path.Combine(root,"endfield","tasks")));
            using var client=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent("data"u8.ToArray())}));
            var downloads=new EndfieldDownloadService(client,root);
            Assert.False(Directory.Exists(Path.Combine(root,"endfield","objects")));
            var md5=Convert.ToHexString(MD5.HashData("data"u8));
            var downloaded=await downloads.DownloadAsync(new("https://beyond.hycdn.cn/file"),md5,4);
            Assert.Equal("data",await File.ReadAllTextAsync(downloaded));
        }
        finally{Remove(root);}
    }
    [Fact]
    public async Task BusyCompletedCacheDoesNotKeepAStaleTaskReference()
    {
        var root=Root();try
        {
            var key=DownloadTaskCache.ContentKey(new string('c',32),4);
            var task=Guid.NewGuid().ToString("N")+"-install";var cache=new DownloadTaskCache(root);
            await cache.TrackAsync(task,[key]);var path=Write(root,"endfield/objects/"+key,"busy");
            using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                var result=await cache.CompleteAsync(task);
                Assert.Equal(0,result.DeletedFiles);Assert.Equal(1,result.RetainedObjects);
                Assert.Empty(await cache.ReferencedAsync());Assert.True(File.Exists(path));
            }
            var storage=new LocalStorageService(root,Path.Combine(root,"install"),Path.Combine(root,"temp"));
            var scan=await storage.ScanAsync();
            Assert.Contains(scan.Files,f=>f.Path==path);
            await storage.DeleteAsync(scan.Files);
            Assert.False(File.Exists(path));
        }
        finally{Remove(root);}
    }
    private static string Root(){var root=Path.Combine(Path.GetTempPath(),"aster-storage-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root;}
    private static void Remove(string root){var allowed=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"aster-storage-tests"))+Path.DirectorySeparatorChar;if(!Path.GetFullPath(root).StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();Directory.Delete(root,true);}
    private static string Write(string root,string relative,string content){var path=SafeGamePath.Resolve(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,content);return path;}
    private sealed class InlineProgress(Action<long> report):IProgress<long>{public void Report(long value)=>report(value);}
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(respond(request));}
}