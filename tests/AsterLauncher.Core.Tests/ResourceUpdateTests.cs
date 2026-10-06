using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class ResourceUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "AsterLauncher.ResourceTests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aU1cAAAAASUVORK5CYII=");
    private static ResourceCatalog Catalog(long revision=1) => new()
    {
        Revision=revision,PublishedAt=DateTimeOffset.Parse("2026-10-06T12:00:00+08:00"),
        Pools=[new(){GameId=BuiltInGameIds.Endfield,Key="冬猎",Name="冬猎",StartsAt=DateTimeOffset.Parse("2026-09-02T12:00:00+08:00"),EndsAt=DateTimeOffset.Parse("2026-09-30T11:59:00+08:00"),Source="https://endfield.hypergryph.com/news/2653"}],
        Images=[new(){GameId=BuiltInGameIds.Endfield,Kind="portrait",Key="测试角色",Aliases=["别名"],File="images/test.png",Sha256=Convert.ToHexString(SHA256.HashData(Pixel)).ToLowerInvariant(),Size=Pixel.Length,Source="https://endfield.hypergryph.com/operator",Attribution="发行商"}]
    };
    private sealed class Handler : HttpMessageHandler
    {
        public ResourceCatalog Catalog {get;set;} = ResourceUpdateTests.Catalog();
        public byte[] Image {get;set;} = Pixel;
        public bool Offline {get;set;}
        public int ImageRequests {get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(Offline)throw new HttpRequestException("offline");
            var isCatalog=request.RequestUri!.AbsolutePath.EndsWith("/catalog.json");
            if(!isCatalog)ImageRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(isCatalog?JsonSerializer.SerializeToUtf8Bytes(Catalog,ResourceUpdateService.JsonOptions):Image)});
        }
    }
    [Fact]
    public async Task IndependentFeedReusesHashCacheAcrossRevisionAndRestartsOffline()
    {
        using var handler=new Handler();using var http=new HttpClient(handler);
        var service=new ResourceUpdateService(http,_root);
        Assert.True(await service.RefreshAsync());Assert.Equal(1,handler.ImageRequests);
        Assert.NotNull(service.FindImage(BuiltInGameIds.Endfield,"portrait","别名"));
        Assert.False(await service.RefreshAsync());Assert.Equal(1,handler.ImageRequests);
        handler.Catalog=Catalog(2);Assert.True(await service.RefreshAsync());Assert.Equal(1,handler.ImageRequests);
        var restarted=new ResourceUpdateService(http,_root);await restarted.LoadAsync();
        handler.Offline=true;await Assert.ThrowsAsync<HttpRequestException>(()=>restarted.RefreshAsync());
        Assert.Equal(2,restarted.Current!.Revision);
        Assert.True(File.Exists(restarted.FindImage(BuiltInGameIds.Endfield,"portrait","测试角色")));
    }
    [Fact]
    public async Task BadHashAndRollbackDoNotActivateOrDestroyExistingSnapshot()
    {
        using var handler=new Handler();using var http=new HttpClient(handler);var service=new ResourceUpdateService(http,_root);
        await service.RefreshAsync();
        handler.Catalog=Catalog(2) with {Images=[Catalog().Images[0] with {Sha256=new string('a',64)}]};
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.RefreshAsync());Assert.Equal(1,service.Current!.Revision);
        handler.Catalog=Catalog() with {Pools=[Catalog().Pools[0] with {Name="同版偷偷变化"}]};
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.RefreshAsync());
        handler.Catalog=Catalog(2);await service.RefreshAsync();handler.Catalog=Catalog();
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.RefreshAsync());Assert.Equal(2,service.Current!.Revision);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root,"resources"),"*.tmp"));
    }
    [Fact]
    public async Task CorruptCacheIsRepairedAndInterruptedRefreshKeepsInstalledSnapshot()
    {
        using var handler=new Handler();using var http=new HttpClient(handler);var service=new ResourceUpdateService(http,_root);
        await service.RefreshAsync();var path=service.FindImage(BuiltInGameIds.Endfield,"portrait","测试角色")!;
        await File.WriteAllBytesAsync(path,new byte[Pixel.Length]);
        var restarted=new ResourceUpdateService(http,_root);await restarted.LoadAsync();Assert.Null(restarted.Current);
        await restarted.RefreshAsync();Assert.Equal(Pixel,await File.ReadAllBytesAsync(path));
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>restarted.RefreshAsync(cancellation.Token));
        Assert.Equal(1,restarted.Current!.Revision);
    }
    [Fact]
    public async Task StorageKeepsActiveCatalogAndImagesAndPrunesObsoleteImageDirectories()
    {
        using var handler=new Handler();using var http=new HttpClient(handler);var service=new ResourceUpdateService(http,_root);await service.RefreshAsync();
        var active=service.FindImage(BuiltInGameIds.Endfield,"portrait","测试角色")!;
        var stale=Path.Combine(_root,"resources","content",new string('b',64)+".png");await File.WriteAllBytesAsync(stale,Pixel);
        var storage=new LocalStorageService(_root,Path.Combine(_root,"install"),Path.Combine(_root,"temporary"));
        var scan=await storage.ScanAsync();
        Assert.DoesNotContain(scan.Files,f=>f.Path==active||f.Path.EndsWith("resources\\catalog.json"));
        Assert.Contains(scan.Files,f=>f.Path==stale);
        await storage.DeleteAsync(scan.Files.Where(f=>f.Path==stale));
        Assert.False(File.Exists(stale));Assert.True(File.Exists(active));
    }
    [Theory]
    [InlineData("../outside.png")][InlineData("images/../outside.png")][InlineData("images/a.exe")][InlineData("https://evil/a.png")][InlineData("images/a.png?token=a")][InlineData("images\\a.png")]
    public void RejectUnsafeImageLocations(string file)
        => Assert.Throws<InvalidDataException>(()=>ResourceUpdateService.Parse(JsonSerializer.SerializeToUtf8Bytes(Catalog() with {Images=[Catalog().Images[0] with {File=file}]},ResourceUpdateService.JsonOptions)));
    [Fact]
    public void RejectUnsupportedSchemaAndMalformedDates()
    {
        Assert.Throws<InvalidDataException>(()=>ResourceUpdateService.Parse(JsonSerializer.SerializeToUtf8Bytes(Catalog() with {SchemaVersion=2},ResourceUpdateService.JsonOptions)));
        Assert.Throws<InvalidDataException>(()=>ResourceUpdateService.Parse(JsonSerializer.SerializeToUtf8Bytes(Catalog() with {Pools=[Catalog().Pools[0] with {EndsAt=DateTimeOffset.Parse("2020-01-01T00:00:00Z")}]},ResourceUpdateService.JsonOptions)));
        Assert.Equal("2026/09/02 12:00 — 2026/09/30 11:59",Catalog().Pools[0].DateText);
    }
    [Fact]
    public void ExplicitServerCanBeDeclaredWithoutConfigButCannotContradictKnownChannel()
    {
        Directory.CreateDirectory(_root);File.WriteAllText(Path.Combine(_root,"YuanShen.exe"),"fixture");
        InstalledServerDeclaration.ValidateHoYo(_root,"YuanShen.exe",HoYoChannel.Bilibili);
        File.WriteAllText(Path.Combine(_root,"config.ini"),"[General]\nchannel=14\nsub_channel=0\ncps=bilibili\n");
        InstalledServerDeclaration.ValidateHoYo(_root,"YuanShen.exe",HoYoChannel.Bilibili);
        Assert.Throws<InvalidOperationException>(()=>InstalledServerDeclaration.ValidateHoYo(_root,"YuanShen.exe",HoYoChannel.Official));
        Assert.Contains("channel=14",File.ReadAllText(Path.Combine(_root,"config.ini")));
    }
    [Fact]
    public void NullAndOversizedFieldsAreRejectedAsInvalidData()
    {
        Assert.Throws<InvalidDataException>(()=>ResourceUpdateService.Parse(JsonSerializer.SerializeToUtf8Bytes(Catalog() with {Images=[Catalog().Images[0] with {Sha256=null!}]},ResourceUpdateService.JsonOptions)));
        Assert.Throws<InvalidDataException>(()=>ResourceUpdateService.Parse(JsonSerializer.SerializeToUtf8Bytes(Catalog() with {Images=[Catalog().Images[0] with {Size=long.MaxValue}]},ResourceUpdateService.JsonOptions)));
    }
    [Fact]
    public async Task EmptyResourceDirectoryScanDoesNotDuplicateOrBreakCleanup()
    {
        using var handler=new Handler{Catalog=Catalog() with {Images=[]}};using var http=new HttpClient(handler);
        await new ResourceUpdateService(http,_root).RefreshAsync();
        var storage=new LocalStorageService(_root,Path.Combine(_root,"install"),Path.Combine(_root,"temporary"));
        var scan=await storage.ScanAsync();Assert.Equal(scan.EmptyDirectories.Count,scan.EmptyDirectories.DistinctBy(d=>d.Path).Count());
        await storage.DeleteAsync([],scan.EmptyDirectories);
        Assert.False(Directory.Exists(Path.Combine(_root,"resources","content")));Assert.True(File.Exists(Path.Combine(_root,"resources","catalog.json")));
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
public sealed class AppearanceAndFilterTests
{
    [Theory]
    [InlineData(LauncherThemePreference.TyphonPurple,LauncherAccentPreference.Purple)]
    [InlineData(LauncherThemePreference.ElysiaPink,LauncherAccentPreference.Pink)]
    [InlineData(LauncherThemePreference.PaimonWhite,LauncherAccentPreference.Cream)]
    public void PreserveOldAccentWhileSeparatingCardTheme(LauncherThemePreference old,LauncherAccentPreference accent)
    {
        var config=new LauncherConfiguration{ThemePreference=old};LauncherAppearance.Normalize(config);
        Assert.Equal(LauncherThemePreference.Dark,config.ThemePreference);Assert.Equal(accent,config.AccentPreference);
        config.ThemePreference=LauncherThemePreference.Light;LauncherAppearance.Normalize(config);Assert.Equal(accent,config.AccentPreference);
    }
    [Theory]
    [InlineData(false,false,false,false,true)][InlineData(false,false,true,false,false)]
    [InlineData(true,false,true,false,true)][InlineData(true,false,false,true,false)]
    [InlineData(true,true,true,true,true)][InlineData(false,true,true,true,false)]
    public void FiltersRequireActualInstalledAndRunningStates(bool installed,bool running,bool installedOnly,bool runningOnly,bool expected)
        => Assert.Equal(expected,GameLibraryFilter.Matches("原神","米哈游",installed,running,"","全部发行商",installedOnly,runningOnly));
    [Fact]
    public void SearchAndPublisherCombineWithStateFilters()
    {
        Assert.True(GameLibraryFilter.Matches("原神","米哈游",true,false," 原 ","米哈游",true,false));
        Assert.False(GameLibraryFilter.Matches("终末地","鹰角",true,true,"","米哈游",true,true));
    }
    [Fact]
    public void ExplicitChannelAssignmentPreservesUnknownIdentityAndRejectsOccupiedServer()
    {
        var unknown=new HoYoInstallation{Channel=HoYoChannel.Unknown,ExecutablePath=@"E:\isolated\YuanShen.exe",InstallRoot=@"E:\isolated"};
        var state=new GameUserState{GameId=BuiltInGameIds.GenshinImpact,HoYoInstallations=[unknown]};
        var result=InstallationChannelAssignment.Confirm(state,HoYoChannel.Bilibili,unknown.ExecutablePath);
        Assert.Same(unknown,result);Assert.Equal(unknown.InstallationId,state.HoYoInstallation!.InstallationId);Assert.True(result.ChannelConfirmed);Assert.False(result.OfficialChannelConfirmed);
        Assert.Throws<InvalidOperationException>(()=>InstallationChannelAssignment.Confirm(state,HoYoChannel.Bilibili,@"E:\other\YuanShen.exe"));
        Assert.Equal(@"E:\isolated",result.InstallRoot);
    }
}
