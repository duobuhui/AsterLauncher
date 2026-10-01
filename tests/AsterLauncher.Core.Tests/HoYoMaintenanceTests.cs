using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class HoYoMaintenanceTests
{
    private const string Game = BuiltInGameIds.GenshinImpact;
    private static string Hash(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
    private static string Root() { var p=Path.Combine("E:\\Study\\Launcher\\.artifacts\\hoyo-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(p);return p; }
    private static void Remove(string root) { SafeGamePath.Resolve(root,"guard");Directory.Delete(root,true); }
    private static HoYoFile FileEntry(string path, string text)
    {
        var bytes=Encoding.UTF8.GetBytes(text);var hash=Hash(bytes);
        return new(path,bytes.Length,hash,[new(hash,hash,hash,0,bytes.Length,bytes.Length,new("https://autopatchcn.yuanshen.com/"+hash),0)]);
    }
    private static HoYoPackage Package(string version,string text="good")
    {
        var files=new[]{FileEntry("YuanShen.exe","exe"),FileEntry("data/asset",text)};
        return new(new(Game,"fake",version,"YuanShen.exe","Genshin Impact Game",null),files,Hash(Encoding.UTF8.GetBytes(version+text)));
    }
    private sealed class Provider(HoYoPackage main,HoYoPackage? pre=null) : IHoYoDistributionProvider
    {
        public HoYoPackage Main=main;
        public HoYoPackage? Pre=pre;
        public Task<HoYoRelease?> GetReleaseAsync(string gameId,CancellationToken token=default) => Task.FromResult<HoYoRelease?>(Main.Release with {PreloadVersion=Pre?.Release.Version});
        public Task<HoYoPackage> GetPackageAsync(string gameId,IReadOnlyCollection<string> languages,bool preload=false,CancellationToken token=default)
            => Task.FromResult(preload?Pre??throw new InvalidOperationException("not open"):Main);
    }
    private sealed class Handler(IEnumerable<HoYoFile> files) : HttpMessageHandler
    {
        private readonly Dictionary<string,byte[]> _bytes=files.Select(f=>Encoding.UTF8.GetBytes(f.Path=="YuanShen.exe"?"exe":f.Md5==Hash(Encoding.UTF8.GetBytes("good"))?"good":"next")).ToDictionary(Hash);
        public int Requests;
        public bool ForbiddenOnce;
        public bool InterruptOnce;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken token)
        {
            Requests++;
            if(InterruptOnce){InterruptOnce=false;throw new OperationCanceledException(token);}
            if(ForbiddenOnce){ForbiddenOnce=false;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));}
            var bytes=_bytes[req.RequestUri!.Segments.Last()];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
        }
    }
    private static HoYoMaintenanceService Service(Provider provider,Handler handler,string data)
        => new(provider,new EndfieldDownloadService(new HttpClient(handler),data,"hoyo",HoYoDistributionProvider.TrustedUri),new HoYoContentCodec("unused"),data);
    [Fact]
    public async Task FullInstallThenSameSizeCorruptionRepairPreservesUserFilesAndHardLinkPeer()
    {
        var root=Root();try
        {
            var data=Path.Combine(root,"cache");var install=new HoYoInstallation{InstallRoot=Path.Combine(root,"game")};
            var package=Package("1.0.0");var handler=new Handler(package.Files);var service=Service(new(package),handler,data);
            await service.SyncAsync(Game,install,false);
            Assert.Equal("1.0.0",install.InstalledVersion);Assert.False(install.MaintenanceInProgress);
            Assert.Equal("1.0.0",HoYoMaintenanceService.ReadVersion(install.InstallRoot));
            Assert.False(Directory.Exists(Path.Combine(data,"hoyo","objects")));
            var target=SafeGamePath.Resolve(install.InstallRoot,"data/asset");
            await System.IO.File.WriteAllTextAsync(target,"evil");
            var peer=Path.Combine(root,"peer");Assert.True(WindowsHardLink.TryCreate(peer,target));
            await System.IO.File.WriteAllTextAsync(Path.Combine(install.InstallRoot,"my-notes.txt"),"keep");
            var plan=await service.PlanAsync(Game,install);
            Assert.Single(plan.Corrupt);Assert.Empty(plan.Missing);Assert.Equal(4,plan.DownloadBytes);
            var before=handler.Requests;await service.SyncAsync(Game,install,true);
            Assert.Equal(1,handler.Requests-before);Assert.Equal("good",await System.IO.File.ReadAllTextAsync(target));
            Assert.Equal("evil",await System.IO.File.ReadAllTextAsync(peer));Assert.Equal("keep",await System.IO.File.ReadAllTextAsync(Path.Combine(install.InstallRoot,"my-notes.txt")));
            Assert.False(WindowsHardLink.Identity(target).SameFile(WindowsHardLink.Identity(peer)));
        }finally{Remove(root);}
    }
    [Fact]
    public async Task PreloadDoesNotChangeInstallationAndFormalUpdateReusesMatchingObjects()
    {
        var root=Root();try
        {
            var data=Path.Combine(root,"cache");var install=new HoYoInstallation{InstallRoot=Path.Combine(root,"game")};
            var main=Package("1.0.0");var next=Package("2.0.0","next");
            var provider=new Provider(main,next);var h1=new Handler(main.Files);await Service(provider,h1,data).SyncAsync(Game,install,false);
            var h2=new Handler(next.Files);var service=Service(provider,h2,data);
            await service.PreloadAsync(Game,install);
            Assert.Equal("1.0.0",install.InstalledVersion);Assert.Equal("1.0.0",HoYoMaintenanceService.ReadVersion(install.InstallRoot));
            Assert.Equal("good",await System.IO.File.ReadAllTextAsync(SafeGamePath.Resolve(install.InstallRoot,"data/asset")));
            Assert.Equal("2.0.0",install.PreloadVersion);Assert.Equal("1.0.0",install.PreloadSourceVersion);
            var count=h2.Requests;provider.Main=next;await service.SyncAsync(Game,install,false);
            Assert.Equal(count,h2.Requests);Assert.Equal("2.0.0",install.InstalledVersion);Assert.Null(install.PreloadContentHash);
            Assert.False(Directory.Exists(Path.Combine(data,"hoyo","objects")));
        }finally{Remove(root);}
    }
    [Fact]
    public async Task InterruptedInstallResumesAfterServiceRestartAndCancelPrunesOwnedCache()
    {
        var root=Root();try
        {
            var install=new HoYoInstallation{InstallRoot=Path.Combine(root,"game")};var data=Path.Combine(root,"cache");var p=Package("1.0.0");
            var provider=new Provider(p);var bad=new Handler(p.Files){InterruptOnce=true};
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Service(provider,bad,data).SyncAsync(Game,install,false));
            Assert.Null(install.InstalledVersion);Assert.True(install.MaintenanceInProgress);Assert.True(System.IO.File.Exists(Path.Combine(install.InstallRoot,".aster-hoyo-maintenance.json")));
            var handler=new Handler(p.Files);await Service(provider,handler,data).SyncAsync(Game,install,false);
            Assert.Equal("1.0.0",install.InstalledVersion);Assert.False(install.MaintenanceInProgress);
            var preload=new Provider(p,Package("2.0.0","next"));var service=Service(preload,new Handler(preload.Pre!.Files),data);
            await service.PreloadAsync(Game,install);await service.CancelAsync(install,true);
            Assert.False(Directory.Exists(Path.Combine(data,"hoyo","objects")));Assert.False(Directory.Exists(Path.Combine(data,"hoyo","tasks")));
        }finally{Remove(root);}
    }
    [Fact]
    public async Task CacheCorruptionAndRefreshedUriRecoverWithoutPrematureVersionWrite()
    {
        var root=Root();try
        {
            var install=new HoYoInstallation{InstallRoot=Path.Combine(root,"game")};var data=Path.Combine(root,"cache");var p=Package("1.0.0");
            var corrupt=p.Files[1].Chunks[0];var cache=SafeGamePath.Resolve(data,"hoyo/objects/"+DownloadTaskCache.ContentKey(corrupt.CompressedMd5,corrupt.Size));
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);await System.IO.File.WriteAllTextAsync(cache,"evil");
            var handler=new Handler(p.Files){ForbiddenOnce=true};await Service(new(p),handler,data).SyncAsync(Game,install,false);
            Assert.True(handler.Requests>=3);Assert.Equal("1.0.0",install.InstalledVersion);
        }finally{Remove(root);}
    }
    [Fact]
    public async Task RepairCannotUpgradeOldInstallationOrDowngradeNewInstallation()
    {
        var root=Root();try
        {
            var install=new HoYoInstallation{InstallRoot=Path.Combine(root,"game")};var data=Path.Combine(root,"cache");var one=Package("1.0.0");
            var provider=new Provider(one);await Service(provider,new Handler(one.Files),data).SyncAsync(Game,install,false);
            provider.Main=Package("2.0.0","next");
            await Assert.ThrowsAsync<InvalidOperationException>(()=>Service(provider,new Handler(provider.Main.Files),data).SyncAsync(Game,install,true));
            Assert.Equal("1.0.0",HoYoMaintenanceService.ReadVersion(install.InstallRoot));
            provider.Main=Package("0.9.0");await Assert.ThrowsAsync<InvalidOperationException>(()=>Service(provider,new Handler(provider.Main.Files),data).SyncAsync(Game,install,false));
        }finally{Remove(root);}
    }
    [Theory]
    [InlineData("2","mihoyo")]
    [InlineData("1","bilibili")]
    [InlineData("1","hoyoverse")]
    public void ForeignChannelIsNeverConverted(string channel,string cps)
    {
        var root=Root();try
        {
            System.IO.File.WriteAllText(Path.Combine(root,"config.ini"),$"[General]\nchannel={channel}\ncps={cps}\ngame_version=1.0.0\n");
            System.IO.File.WriteAllText(Path.Combine(root,"YuanShen.exe"),"exe");
            Assert.Throws<InvalidOperationException>(()=>HoYoMaintenanceService.ValidateExistingRoot(root,"YuanShen.exe"));
        }finally{Remove(root);}
    }
    [Fact]
    public async Task HoyoTaskReferencesProtectStorageAndDoNotAffectEndfieldObjects()
    {
        var root=Root();try
        {
            var key=DownloadTaskCache.ContentKey(Hash(Encoding.UTF8.GetBytes("good")),4);var task=Guid.NewGuid().ToString("N")+"-install";
            foreach(var bucket in new[]{"hoyo","endfield"}) {var path=SafeGamePath.Resolve(root,bucket+"/objects/"+key);Directory.CreateDirectory(Path.GetDirectoryName(path)!);await System.IO.File.WriteAllTextAsync(path,"good");}
            var cache=new DownloadTaskCache(root,"hoyo");await cache.TrackAsync(task,[key]);
            var storage=new LocalStorageService(root,Path.Combine(root,"app"),Path.Combine(root,"temp"));var scan=await storage.ScanAsync();
            Assert.DoesNotContain(scan.Files,f=>f.Category=="米哈游下载缓存");
            await cache.CancelAsync(task);Assert.True(System.IO.File.Exists(SafeGamePath.Resolve(root,"endfield/objects/"+key)));
            Assert.False(Directory.Exists(Path.Combine(root,"hoyo","objects")));
        }finally{Remove(root);}
    }
    [Fact]
    public void AdditiveConfigurationPreservesOldArchivesPathsAndLaunchProfiles()
    {
        var config=JsonSerializer.Deserialize<LauncherConfiguration>("{\"SchemaVersion\":4,\"Games\":[{\"GameId\":\"honkai-star-rail\",\"ExecutablePath\":\"E:\\\\game\\\\StarRail.exe\",\"TotalPlaySeconds\":123}],\"LaunchProfiles\":[{\"GameId\":\"honkai-star-rail\",\"Name\":\"my profile\"}]}")!;
        Assert.Null(config.Games[0].HoYoInstallation);config.Games[0].HoYoInstallation=new();
        var roundtrip=JsonSerializer.Deserialize<LauncherConfiguration>(JsonSerializer.Serialize(config))!;
        Assert.Equal(123,roundtrip.Games[0].TotalPlaySeconds);Assert.Equal("my profile",roundtrip.LaunchProfiles[0].Name);
        Assert.Equal("E:\\game\\StarRail.exe",roundtrip.Games[0].ExecutablePath);
    }
    [Fact]
    public async Task AudioRecordIsCommittedWithVersionAndPendingSelectionSurvivesRestart()
    {
        var root=Root();try
        {
            var package=Package("1.0.0");package=package with {Release=package.Release with {AudioRecordPath="data/audio-record"}};
            var install=new HoYoInstallation{InstallRoot=Path.Combine(root,"game"),AudioLanguages=["zh-cn","ja-jp"],AudioSelectionPending=true};
            install=JsonSerializer.Deserialize<HoYoInstallation>(JsonSerializer.Serialize(install))!;Assert.True(install.AudioSelectionPending);
            await Service(new(package),new Handler(package.Files),Path.Combine(root,"cache")).SyncAsync(Game,install,false);
            Assert.False(install.AudioSelectionPending);Assert.Equal("1.0.0",install.InstalledVersion);
            Assert.Equal(new[]{"Chinese","Japanese"},await System.IO.File.ReadAllLinesAsync(SafeGamePath.Resolve(install.InstallRoot!,"data/audio-record")));
        }finally{Remove(root);}
    }
    [Fact]
    public async Task RestartRemovesPreviousVersionsOwnedStagingBeforeApplyingNewManifest()
    {
        var root=Root();try
        {
            var install=new HoYoInstallation{InstallRoot=Path.Combine(root,"game")};Directory.CreateDirectory(install.InstallRoot);
            var name=".aster-hoyo-stage-"+install.InstallationId.ToString("N");var stage=SafeGamePath.Resolve(install.InstallRoot,name);
            var old=FileEntry("old/asset","good");var path=SafeGamePath.Resolve(stage,old.Path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);await System.IO.File.WriteAllTextAsync(path,"good");
            await System.IO.File.WriteAllTextAsync(Path.Combine(install.InstallRoot,".aster-hoyo-maintenance.json"),JsonSerializer.Serialize(new {gameId=Game,stageName=name,files=new[]{old.Integrity}}));
            var package=Package("2.0.0","next");await Service(new(package),new Handler(package.Files),Path.Combine(root,"cache")).SyncAsync(Game,install,false);
            Assert.Equal("2.0.0",install.InstalledVersion);Assert.False(Directory.Exists(stage));
        }finally{Remove(root);}
    }
    private static byte[] ProtoFile(string path,bool corruptOffset=false)
    {
        using var output=new MemoryStream();
        static void Var(Stream s,ulong v){do{var b=(byte)(v&127);v>>=7;s.WriteByte((byte)(b|(v>0?128:0)));}while(v>0);}
        static void Text(Stream s,int field,byte[] bytes){Var(s,(ulong)(field*8+2));Var(s,(ulong)bytes.Length);s.Write(bytes);}
        var hash=Hash(Encoding.UTF8.GetBytes("good"));
        using var chunk=new MemoryStream();Text(chunk,1,Encoding.UTF8.GetBytes(hash));Text(chunk,2,Encoding.UTF8.GetBytes(hash));
        if(corruptOffset){Var(chunk,24);Var(chunk,1);}Var(chunk,32);Var(chunk,4);Var(chunk,40);Var(chunk,4);Text(chunk,7,Encoding.UTF8.GetBytes(hash));
        using var file=new MemoryStream();Text(file,1,Encoding.UTF8.GetBytes(path));Text(file,2,chunk.ToArray());Var(file,32);Var(file,4);Text(file,5,Encoding.UTF8.GetBytes(hash));
        Text(output,1,file.ToArray());return output.ToArray();
    }
    [Fact]
    public void ManifestRejectsTraversalDuplicatePathsChunkGapsAndTruncation()
    {
        Uri Address(string id)=>new("https://autopatchcn.yuanshen.com/"+id);
        var bytes=ProtoFile("data/file");Assert.Single(HoYoManifestReader.Parse(bytes,Address,0));
        Assert.Throws<InvalidDataException>(()=>HoYoManifestReader.Parse(ProtoFile("../escape"),Address,0));
        Assert.Throws<InvalidDataException>(()=>HoYoManifestReader.Parse(bytes.Concat(bytes).ToArray(),Address,0));
        Assert.Throws<InvalidDataException>(()=>HoYoManifestReader.Parse(ProtoFile("data/file",true),Address,0));
        Assert.Throws<InvalidDataException>(()=>HoYoManifestReader.Parse(bytes[..^1],Address,0));
    }
    [Theory]
    [InlineData("https://autopatchcn.yuanshen.com.evil.test/x")]
    [InlineData("http://autopatchcn.bhsr.com/x")]
    [InlineData("https://user@autopatchcn.bh3.com/x")]
    public void DistributionTrustRejectsNonPublisherAddresses(string url)=>Assert.Throws<InvalidDataException>(()=>HoYoDistributionProvider.TrustedUri(url));
}