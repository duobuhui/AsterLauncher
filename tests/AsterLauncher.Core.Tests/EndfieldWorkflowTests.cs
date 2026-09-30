using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldWorkflowTests
{
    private static string Tool => Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("DOTNET_CLI_HOME"))!, ".artifacts", "vendor", "7zip-26.03", "extra", "x64", "7za.exe");
    [Fact]
    public async Task PreloadThenFormalUpdateReusesIdenticalContentAfterUrlChanges()
    {
        var root=NewRoot();
        try
        {
            var install=new EndfieldInstallation{Channel=EndfieldChannel.Official,InstallRoot=Path.Combine(root,"game"),InstalledVersion="1.0.0"};
            Directory.CreateDirectory(install.InstallRoot);
            await File.WriteAllTextAsync(Path.Combine(install.InstallRoot,"Endfield.exe"),"old");
            var target=Encoding.UTF8.GetBytes("new executable fixture");
            var zip=ZipBytes("Endfield.exe",target);
            var hash=Md5(zip); var calls=0;
            var patch=new EndfieldPatch("1.1.0",[new(new Uri("https://beyond.hycdn.cn/patch.zip?auth_key=preload"),hash,zip.Length)],hash,zip.Length);
            var provider=new Provider(new EndfieldPackage(install.Channel,"1.1.0",new Uri("https://beyond.hycdn.cn/files"),new string('1',32),[Entry("Endfield.exe",target)],null,patch));
            using var http=new HttpClient(new Handler(_=>{calls++;return Ok(zip);}));
            var service=new EndfieldMaintenanceService(provider,new(http,root),new(),root,new(Tool));
            await service.PreloadAsync(install,patch);
            Assert.Equal("1.0.0",install.InstalledVersion);
            Assert.Equal("old",await File.ReadAllTextAsync(Path.Combine(install.InstallRoot,"Endfield.exe")));
            provider.Package=provider.Package with {Patch=patch with { Packs=[new(new Uri("https://beyond.hycdn.cn/patch.zip?auth_key=release"),hash,zip.Length)]},PrePatch=null};
            await service.SyncAsync(install,null,false);
            Assert.Equal(1,calls);
            Assert.Equal(target,await File.ReadAllBytesAsync(Path.Combine(install.InstallRoot,"Endfield.exe")));
            Assert.Equal("1.1.0",install.InstalledVersion);
            Assert.False(File.Exists(Path.Combine(install.InstallRoot,".aster-maintenance.json")));
        }
        finally { DeleteRoot(root); }
    }
    [Fact]
    public async Task SplitZipIsExtractedAsOneArchiveAndRejectsTraversal()
    {
        var root=NewRoot();
        try
        {
            var bytes=Encoding.UTF8.GetBytes(new string('x',500));
            var zip=ZipBytes("folder/resource.bin",bytes);
            var a=Path.Combine(root,"one");var b=Path.Combine(root,"two");
            await File.WriteAllBytesAsync(a,zip[..(zip.Length/2)]);await File.WriteAllBytesAsync(b,zip[(zip.Length/2)..]);
            var result=await new EndfieldArchiveService(Tool).StageAsync([a,b],Path.Combine(root,"stage"),[Entry("folder/resource.bin",bytes)],null);
            Assert.Equal(bytes,await File.ReadAllBytesAsync(result["folder/resource.bin"]));
            var malicious=Path.Combine(root,"bad.zip");await File.WriteAllBytesAsync(malicious,ZipBytes("../escape.bin",bytes));
            await Assert.ThrowsAsync<InvalidDataException>(()=>new EndfieldArchiveService(Tool).StageAsync([malicious],Path.Combine(root,"badstage"),[Entry("folder/resource.bin",bytes)],null));
            Assert.False(File.Exists(Path.Combine(root,"escape.bin")));
        }
        finally { DeleteRoot(root); }
    }
    [Fact]
    public async Task RepairFindsSameSizeDamageAndKeepsOtherLinkedInstallationAndUserFiles()
    {
        var root=NewRoot();
        try
        {
            var game=Path.Combine(root,"a");var other=Path.Combine(root,"b");Directory.CreateDirectory(game);Directory.CreateDirectory(other);
            var old=Encoding.UTF8.GetBytes("bad-content");var good=Encoding.UTF8.GetBytes("new-content");
            var relative="Endfield_Data/StreamingAssets/VFS/0123ABCD/0123456789ABCDEF0123456789ABCDEF.chk";
            var first=SafeGamePath.Resolve(game,relative);var second=SafeGamePath.Resolve(other,relative);Directory.CreateDirectory(Path.GetDirectoryName(first)!);Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            await File.WriteAllBytesAsync(first,old);Assert.True(WindowsHardLink.TryCreate(second,first));
            var exe=Encoding.UTF8.GetBytes("fixture executable");await File.WriteAllBytesAsync(Path.Combine(game,"Endfield.exe"),exe);await File.WriteAllTextAsync(Path.Combine(game,"personal-note.txt"),"keep me");
            var provider=new Provider(new(EndfieldChannel.Official,"1.0.0",new Uri("https://beyond.hycdn.cn/files"),new string('1',32),[Entry("Endfield.exe",exe),Entry(relative,good)],null,null));
            var calls=0;using var http=new HttpClient(new Handler(_=>{calls++;return Ok(good);}));
            var install=new EndfieldInstallation{Channel=EndfieldChannel.Official,InstallRoot=game,InstalledVersion="1.0.0"};
            var service=new EndfieldMaintenanceService(provider,new(http,root),new(),root);
            var (_,plan)=await service.PlanAsync(install);Assert.Single(plan.Corrupt);Assert.Equal(good.Length,plan.DownloadBytes);
            await service.SyncAsync(install,null,true);
            Assert.Equal(1,calls);Assert.Equal(good,await File.ReadAllBytesAsync(first));Assert.Equal(old,await File.ReadAllBytesAsync(second));Assert.Equal("keep me",await File.ReadAllTextAsync(Path.Combine(game,"personal-note.txt")));
        }
        finally { DeleteRoot(root); }
    }
    [Fact]
    public async Task FailureAndNewServiceResumeWithoutPublishingVersionEarly()
    {
        var root=NewRoot();
        try
        {
            var payload=Encoding.UTF8.GetBytes("new executable");var provider=new Provider(new(EndfieldChannel.Bilibili,"2.0.0",new Uri("https://beyond.hycdn.cn/files"),new string('1',32),[Entry("Endfield.exe",payload)],null,null));
            var install=new EndfieldInstallation{Channel=EndfieldChannel.Bilibili,InstallRoot=Path.Combine(root,"game"),InstalledVersion="1.0.0",MaintenanceInProgress=true};
            using var failing=new HttpClient(new Handler(_=>throw new OperationCanceledException()));
            await Assert.ThrowsAsync<OperationCanceledException>(()=>new EndfieldMaintenanceService(provider,new(failing,root),new(),root).SyncAsync(install,null,false));
            Assert.Equal("1.0.0",install.InstalledVersion);Assert.True(File.Exists(Path.Combine(install.InstallRoot,".aster-maintenance.json")));
            using var good=new HttpClient(new Handler(_=>Ok(payload)));
            await new EndfieldMaintenanceService(provider,new(good,root),new(),root).SyncAsync(install,null,false);
            Assert.Equal("2.0.0",install.InstalledVersion);Assert.False(install.MaintenanceInProgress);
        }
        finally { DeleteRoot(root); }
    }
    [Fact]
    public async Task TargetVersionChangeLeavesTransactionAndOldVersion()
    {
        var root=NewRoot();
        try
        {
            var bytes=Encoding.UTF8.GetBytes("new game");var provider=new Provider(new(EndfieldChannel.Official,"1.1.0",new Uri("https://beyond.hycdn.cn/files"),new string('1',32),[Entry("Endfield.exe",bytes)],null,null)){Latest="1.2.0"};
            using var http=new HttpClient(new Handler(_=>Ok(bytes)));var install=new EndfieldInstallation{Channel=EndfieldChannel.Official,InstallRoot=Path.Combine(root,"game"),InstalledVersion="1.0.0",MaintenanceInProgress=true};
            await Assert.ThrowsAsync<InvalidOperationException>(()=>new EndfieldMaintenanceService(provider,new(http,root),new(),root).SyncAsync(install,null,false));
            Assert.Equal("1.0.0",install.InstalledVersion);Assert.True(install.MaintenanceInProgress);Assert.True(File.Exists(Path.Combine(install.InstallRoot,".aster-maintenance.json")));
        }
        finally { DeleteRoot(root); }
    }
    [Fact]
    public async Task RepairOfOldVersionIsRefusedAndChannelIsNotGuessedFromExe()
    {
        var root=NewRoot();
        try
        {
            var game=Path.Combine(root,"game");Directory.CreateDirectory(game);await File.WriteAllTextAsync(Path.Combine(game,"Endfield.exe"),"fake");
            Assert.Null(await EndfieldInstallationInfo.ReadAsync(game));
            var hash=Md5(Encoding.UTF8.GetBytes("fake"));
            await File.WriteAllTextAsync(Path.Combine(game,"config.ini"),$"[Game]\nversion=1.0.0\nappcode=6LL0KJuqHBVz33WK\nentry=Endfield.exe\nentry_md5={hash}\nchannel=2\nsub_channel=2\n");
            Assert.Equal(EndfieldChannel.Bilibili,(await EndfieldInstallationInfo.ReadAsync(game))!.Value.Channel);
            await File.WriteAllTextAsync(Path.Combine(game,"Endfield.exe"),"tampered");Assert.Null(await EndfieldInstallationInfo.ReadAsync(game));
            var provider=new Provider(new(EndfieldChannel.Official,"2.0.0",new Uri("https://beyond.hycdn.cn/files"),new string('1',32),[Entry("Endfield.exe",[1])],null,null));
            using var http=new HttpClient(new Handler(_=>throw new InvalidOperationException("No download expected")));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>new EndfieldMaintenanceService(provider,new(http,root),new(),root).SyncAsync(new(){Channel=EndfieldChannel.Official,InstallRoot=game,InstalledVersion="1.0.0"},null,true));
        }
        finally { DeleteRoot(root); }
    }
    [Fact]
    public async Task EncryptedPatchUsesPublisherKeyAndValidatesIndependentOutput()
    {
        var root=NewRoot();
        try
        {
            var bytes=Encoding.UTF8.GetBytes("encrypted future resource");
            await File.WriteAllBytesAsync(Path.Combine(root,"Endfield.exe"),bytes);
            var start=new System.Diagnostics.ProcessStartInfo(Tool){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"a","-tzip","-pfixture-key","-mem=AES256","-y",Path.Combine(root,"encrypted.zip"),"Endfield.exe"})start.ArgumentList.Add(arg);
            using(var process=System.Diagnostics.Process.Start(start)!)
            {var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();await output;await error;Assert.Equal(0,process.ExitCode);}
            var result=await new EndfieldArchiveService(Tool).StageAsync([Path.Combine(root,"encrypted.zip")],Path.Combine(root,"stage"),[Entry("Endfield.exe",bytes)],"fixture-key");
            Assert.Equal(bytes,await File.ReadAllBytesAsync(result["Endfield.exe"]));
            await Assert.ThrowsAsync<InvalidDataException>(()=>new EndfieldArchiveService(Tool).StageAsync([Path.Combine(root,"encrypted.zip")],Path.Combine(root,"wrong"),[Entry("Endfield.exe",bytes)],"wrong-key"));
            Assert.Equal(bytes,await File.ReadAllBytesAsync(Path.Combine(root,"Endfield.exe")));
        }
        finally{DeleteRoot(root);}
    }
    [Fact]
    public async Task CommitGuardStopsSharingConversionWithoutChangingEitherInstallation()
    {
        var root=NewRoot();
        try
        {
            var a=Path.Combine(root,"a");var b=Path.Combine(root,"b");
            var path="Endfield_Data/StreamingAssets/VFS/0123ABCD/0123456789ABCDEF0123456789ABCDEF.chk";
            var data=Encoding.UTF8.GetBytes("immutable test resource");var first=SafeGamePath.Resolve(a,path);var second=SafeGamePath.Resolve(b,path);
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            await File.WriteAllBytesAsync(first,data);await File.WriteAllBytesAsync(second,data);
            var sharing=new EndfieldSharingService(()=>throw new InvalidOperationException("Game started during sharing"));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>sharing.OptimizeAsync(a,b,[Entry(path,data)],[Entry(path,data)]));
            Assert.False(WindowsHardLink.Identity(first).SameFile(WindowsHardLink.Identity(second)));
            Assert.Equal(data,await File.ReadAllBytesAsync(first));Assert.Equal(data,await File.ReadAllBytesAsync(second));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(second)!,"*.aster-link-*"));
        }
        finally{DeleteRoot(root);}
    }
    [Fact]
    public void ChannelProfilesAreIndependentAndLegacyProfileIsNotDuplicatedOnReload()
    {
        var legacy=new LaunchProfile{GameId=BuiltInGameIds.Endfield,GameArguments="--legacy",Steps=[new(){Name="tool",Arguments="original"}]};
        var configuration=new LauncherConfiguration{LaunchProfiles=[legacy],SelectedProfileId=legacy.Id};
        var official=new EndfieldInstallation{Channel=EndfieldChannel.Official};var bilibili=new EndfieldInstallation{Channel=EndfieldChannel.Bilibili};
        var first=EndfieldInstallationProfiles.Ensure(configuration,official);var second=EndfieldInstallationProfiles.Ensure(configuration,bilibili);
        first.GameArguments="official";first.Steps[0].Arguments="changed";
        Assert.Equal("--legacy",second.GameArguments);Assert.Equal("original",second.Steps[0].Arguments);Assert.Equal("--legacy",legacy.GameArguments);
        Assert.NotEqual(first.Id,second.Id);Assert.NotEqual(first.Steps[0].Id,second.Steps[0].Id);
        Assert.Same(first,EndfieldInstallationProfiles.Ensure(configuration,official));Assert.Equal(3,configuration.LaunchProfiles.Count);
    }
    [Fact]
    public async Task DeletingAnActualSharedDirectoryEntryLeavesOtherChannelReadable()
    {
        var root=NewRoot();
        try
        {
            var a=Path.Combine(root,"one.bin");var b=Path.Combine(root,"two.bin");var bytes=Encoding.UTF8.GetBytes("shared data");
            await File.WriteAllBytesAsync(a,bytes);Assert.True(WindowsHardLink.TryCreate(b,a));
            var identity=WindowsHardLink.Identity(a);Assert.True(identity.SameFile(WindowsHardLink.Identity(b)));
            File.Delete(a);Assert.Equal(bytes,await File.ReadAllBytesAsync(b));Assert.True(identity.SameFile(WindowsHardLink.Identity(b)));
        }
        finally{DeleteRoot(root);}
    }
    private sealed class Provider(EndfieldPackage package):IEndfieldDistributionProvider
    {
        public EndfieldPackage Package=package;public string? Latest;
        public Task<string> GetLatestVersionAsync(EndfieldChannel channel,string? installedVersion,CancellationToken cancellationToken=default)=>Task.FromResult(Latest??Package.Version);
        public Task<EndfieldPackage> GetPackageAsync(EndfieldChannel channel,string? installedVersion,CancellationToken cancellationToken=default)=>Task.FromResult(Package);
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> handler):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(handler(request));
    }
    private static HttpResponseMessage Ok(byte[] bytes)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
    private static string Md5(byte[] bytes)=>Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
    private static EndfieldManifestFile Entry(string name,byte[] bytes)=>new(name,bytes.Length,Md5(bytes));
    private static byte[] ZipBytes(string path,byte[] content)
    {
        using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)) {using var entry=zip.CreateEntry(path).Open();entry.Write(content);}return memory.ToArray();
    }
    private static string NewRoot(){var root=Path.Combine(Path.GetTempPath(),"aster-workflow-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root;}
    private static void DeleteRoot(string root){if(!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(),"aster-workflow-tests"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();Directory.Delete(root,true);}
}
