using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
namespace AsterLauncher.Core.Tests;

public sealed class HoYoChannelTests
{
    private const string Game = BuiltInGameIds.GenshinImpact;
    private const string Resource = "YuanShen_Data/StreamingAssets/AssetBundles/blocks/shared.blk";
    private static string Hash(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
    private static string Root() { var root = Path.Combine(@"E:\Study\Launcher\.artifacts\hoyo-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private sealed class Fixtures : HttpMessageHandler, IHoYoDistributionProvider
    {
        public readonly Dictionary<string, byte[]> Bytes = [];
        public string OfficialText = "good", BilibiliText = "good";
        public bool WithSdk = true;
        public int Requests;
        private HoYoFile Entry(string path, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text); var hash = Hash(bytes); Bytes[hash] = bytes;
            return new(path, bytes.Length, hash, [new(hash, hash, hash, 0, bytes.Length, bytes.Length, new("https://autopatchcn.yuanshen.com/" + hash), 0)]);
        }
        public Task<HoYoRelease?> GetReleaseAsync(string id, CancellationToken token = default) => GetReleaseAsync(id, HoYoChannel.Official, token);
        public Task<HoYoRelease?> GetReleaseAsync(string id, HoYoChannel channel, CancellationToken token = default)
            => Task.FromResult<HoYoRelease?>(new(id, channel.ToString(), "1.0.0", "YuanShen.exe", "Game", null, Channel: channel));
        public Task<HoYoPackage> GetPackageAsync(string id, IReadOnlyCollection<string> languages, bool preload = false, CancellationToken token = default)
            => GetPackageAsync(id, HoYoChannel.Official, languages, preload, token);
        public async Task<HoYoPackage> GetPackageAsync(string id, HoYoChannel channel, IReadOnlyCollection<string> languages, bool preload = false, CancellationToken token = default)
        {
            HoYoSdk? sdk = null;
            if (channel == HoYoChannel.Bilibili && WithSdk)
            {
                using var output = new MemoryStream();
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    var payload = Encoding.UTF8.GetBytes("bilibili login");
                    using (var stream = zip.CreateEntry("YuanShen_Data/Plugins/PCGameSDK.dll").Open()) stream.Write(payload);
                    using var marker = new StreamWriter(zip.CreateEntry("sdk_pkg_version").Open());
                    marker.Write(JsonSerializer.Serialize(new { remoteName = "YuanShen_Data/Plugins/PCGameSDK.dll", fileSize = payload.Length, md5 = Hash(payload) }));
                }
                var bytes = output.ToArray(); var hash = Hash(bytes); Bytes[hash] = bytes;
                sdk = new(new("https://autopatchcn.yuanshen.com/" + hash), hash, bytes.Length, 1024, "test", "sdk_pkg_version");
            }
            return new((await GetReleaseAsync(id, channel, token))!,
                [Entry("YuanShen.exe", "exe"), Entry(Resource, channel == HoYoChannel.Official ? OfficialText : BilibiliText), Entry("Persistent/user-settings", "user")],
                channel + OfficialText + BilibiliText, sdk);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes[request.RequestUri!.Segments.Last()]) });
        }
    }
    private static HoYoMaintenanceService Service(Fixtures fixture, string data)
        => new(fixture, new EndfieldDownloadService(new HttpClient(fixture), data, "hoyo", HoYoDistributionProvider.TrustedUri), new HoYoContentCodec("unused"), data);
    [Fact]
    public async Task SecondChannelReusesVerifiedResourceWithRealFileIdAndIndependentSdkThenRepairDetaches()
    {
        var root = Root();
        try
        {
            var fixture = new Fixtures(); var service = Service(fixture, Path.Combine(root, "cache"));
            var a = new HoYoInstallation { InstallRoot = Path.Combine(root, "official") };
            var b = new HoYoInstallation { InstallRoot = Path.Combine(root, "bilibili"), Channel = HoYoChannel.Bilibili };
            await service.SyncAsync(Game, a, false);
            var plan = await service.PlanAsync(Game, b, peer: a);
            Assert.Single(plan.Reusable!); Assert.Equal(Resource, plan.Reusable![0].File.Path);
            await service.SyncAsync(Game, b, false, peer: a);
            var first = SafeGamePath.Resolve(a.InstallRoot!, Resource); var second = SafeGamePath.Resolve(b.InstallRoot!, Resource);
            var identity = WindowsHardLink.Identity(first);
            Assert.True(identity.SameFile(WindowsHardLink.Identity(second))); Assert.True(identity.LinkCount >= 2);
            Assert.False(WindowsHardLink.Identity(Path.Combine(a.InstallRoot!, "Persistent/user-settings"))
                .SameFile(WindowsHardLink.Identity(Path.Combine(b.InstallRoot!, "Persistent/user-settings"))));
            Assert.Equal("bilibili login", await File.ReadAllTextAsync(SafeGamePath.Resolve(b.InstallRoot!, "YuanShen_Data/Plugins/PCGameSDK.dll")));
            Assert.Contains("channel=14", await File.ReadAllTextAsync(Path.Combine(b.InstallRoot!, "config.ini")));
            var summary = service.ScanSharing(Game, a, b);
            Assert.Equal(1, summary.FileCount); Assert.Equal(4, summary.SharedBytes); Assert.True(summary.EstimatedSavedBytes > 0);
            fixture.BilibiliText = "next";
            await service.SyncAsync(Game, b, true, peer: a);
            Assert.Equal("good", await File.ReadAllTextAsync(first)); Assert.Equal("next", await File.ReadAllTextAsync(second));
            Assert.False(WindowsHardLink.Identity(first).SameFile(WindowsHardLink.Identity(second)));
            Assert.Equal("1.0.0", HoYoMaintenanceService.ReadVersion(a.InstallRoot!));
            File.Delete(second); Assert.Equal("good", await File.ReadAllTextAsync(first));
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class DirectProgress(Action<HoYoProgress> receive) : IProgress<HoYoProgress> { public void Report(HoYoProgress value) => receive(value); }
    [Fact]
    public async Task CommittedStagingCopiesAreReleasedDuringApplyToRespectPeakSpacePlan()
    {
        var root = Root();
        try
        {
            var fixture = new Fixtures { WithSdk = false }; var service = Service(fixture, Path.Combine(root, "cache"));
            var install = new HoYoInstallation { InstallRoot = Path.Combine(root, "game") }; var observed = false;
            await service.SyncAsync(Game, install, false, new DirectProgress(progress =>
            {
                if (progress.Stage != "正在应用文件" || progress.CompletedFiles != 1) return;
                Assert.True(File.Exists(Path.Combine(install.InstallRoot!, "YuanShen.exe")));
                Assert.False(File.Exists(Path.Combine(install.InstallRoot!, ".aster-hoyo-stage-" + install.InstallationId.ToString("N"), "YuanShen.exe")));
                observed = true;
            }));
            Assert.True(observed);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ExistingInstallOptimizationCanRescanAndUnshareWithoutIndex()
    {
        var root = Root();
        try
        {
            var fixture = new Fixtures(); var service = Service(fixture, Path.Combine(root, "cache"));
            var a = new HoYoInstallation { InstallRoot = Path.Combine(root, "a"), ShareResources = false };
            var b = new HoYoInstallation { InstallRoot = Path.Combine(root, "b"), Channel = HoYoChannel.Bilibili, ShareResources = false };
            await service.SyncAsync(Game, a, false); await service.SyncAsync(Game, b, false);
            var result = await service.OptimizeAsync(Game, b, a); Assert.Equal(1, result.FileCount);
            var restarted = Service(fixture, Path.Combine(root, "cache"));
            Assert.Equal(1, restarted.ScanSharing(Game, a, b).FileCount);
            Assert.Equal(1, await restarted.UnshareAsync(Game, b, a));
            Assert.Equal(0, restarted.ScanSharing(Game, a, b).FileCount);
            Assert.Equal("good", await File.ReadAllTextAsync(SafeGamePath.Resolve(a.InstallRoot!, Resource)));
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("genshin-impact", "YuanShen_Data/StreamingAssets/AssetBundles/blocks/a.blk", true)]
    [InlineData("honkai-star-rail", "StarRail_Data/StreamingAssets/Asb/Windows/a.block", true)]
    [InlineData("honkai-star-rail", "StarRail_Data/StreamingAssets/Asb/Windows/index.bytes", false)]
    [InlineData("zenless-zone-zero", "ZenlessZoneZero_Data/StreamingAssets/Blocks/a.blk", true)]
    [InlineData("genshin-impact", "YuanShen_Data/Persistent/a.blk", false)]
    [InlineData("genshin-impact", "YuanShen_Data/Plugins/PCGameSDK.dll", false)]
    [InlineData("genshin-impact", "config.ini", false)]
    public void MutableFilesAndChannelComponentsNeverShare(string game, string path, bool expected)
        => Assert.Equal(expected, HoYoSharingPolicy.IsShareable(game, path));
    [Fact]
    public void OldConfigurationKeepsUnknownInstallationAndIndependentProfilesWithoutDuplicates()
    {
        var legacy = new HoYoInstallation { InstallRoot = @"E:\old-game" };
        var state = new GameUserState { GameId = Game, ExecutablePath = @"E:\old-game\YuanShen.exe", HoYoInstallation = legacy, TotalPlaySeconds = 123 };
        var config = new LauncherConfiguration { Games = [state], LaunchProfiles = [new() { GameId = Game, Name = "original", GameArguments = "-windowed" }] };
        HoYoInstallationIdentity.Restore(state);
        Assert.Equal(HoYoChannel.Unknown, state.SelectedHoYoChannel); Assert.Equal(legacy.InstallationId, state.HoYoInstallation!.InstallationId);
        var official = HoYoInstallationIdentity.Get(state, HoYoChannel.Official); var bilibili = HoYoInstallationIdentity.Get(state, HoYoChannel.Bilibili);
        var first = HoYoInstallationIdentity.EnsureProfile(config, state, official); var second = HoYoInstallationIdentity.EnsureProfile(config, state, bilibili);
        Assert.NotEqual(first.Id, second.Id); Assert.Equal("-windowed", second.GameArguments);
        HoYoInstallationIdentity.EnsureProfile(config, state, official); Assert.Equal(3, config.LaunchProfiles.Count);
        var restored = JsonSerializer.Deserialize<LauncherConfiguration>(JsonSerializer.Serialize(config))!;
        HoYoInstallationIdentity.Restore(restored.Games[0]); Assert.Equal(3, restored.Games[0].HoYoInstallations.Count);
        Assert.Equal(123, restored.Games[0].TotalPlaySeconds);
        Assert.Throws<ArgumentException>(() => HoYoDistributionProvider.LauncherFor(BuiltInGameIds.HonkaiImpact3rd, HoYoChannel.Bilibili));
    }
    [Fact]
    public void ChannelRootsMustBeIndependentAndCrossVolumeCannotClaimSharing()
    {
        Assert.Throws<InvalidOperationException>(() => HoYoSharingPolicy.ValidateIndependent(@"E:\game", @"E:\game\b"));
        Assert.Throws<InvalidOperationException>(() => HoYoSharingPolicy.ValidateIndependent(@"E:\game", @"E:\game"));
        Assert.False(WindowsHardLink.CanShareVolume(@"E:\game", @"Z:\game"));
    }
    [Fact]
    public async Task SdkDeletionListIsBoundedAndCannotRemoveUserFiles()
    {
        var root = Root();
        try
        {
            var path = Path.Combine(root, "sdk.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("deletefiles.txt").Open()); writer.Write("StarRail_Data/Plugins/PCGameSDK.dll");
            }
            Assert.Equal(["StarRail_Data/Plugins/PCGameSDK.dll"], HoYoSdkArchive.ReadRemovedFiles(path));
            File.Delete(path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("deletefiles.txt").Open()); writer.Write("my-notes.txt");
            }
            Assert.Throws<InvalidDataException>(() => HoYoSdkArchive.ReadRemovedFiles(path));
            await Task.CompletedTask;
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task SdkRejectsArchiveHashDamageAndTraversalBeforeExtracting()
    {
        var root = Root();
        try
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) { using var text = new StreamWriter(zip.CreateEntry("../escape").Open()); text.Write("unsafe"); }
            var bytes = output.ToArray(); var path = Path.Combine(root, "sdk.zip"); await File.WriteAllBytesAsync(path, bytes);
            var sdk = new HoYoSdk(new("https://autopatchcn.yuanshen.com/sdk"), Hash(bytes), bytes.Length, 10, "x", "sdk_pkg_version");
            await Assert.ThrowsAsync<InvalidDataException>(() => HoYoSdkArchive.ReadAsync(path, sdk, default));
            await File.WriteAllBytesAsync(path, new byte[bytes.Length]);
            await Assert.ThrowsAsync<InvalidDataException>(() => HoYoSdkArchive.ReadAsync(path, sdk, default));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escape")));
        }
        finally { Directory.Delete(root, true); }
    }
}