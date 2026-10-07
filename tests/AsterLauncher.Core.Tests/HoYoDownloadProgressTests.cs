using System.Collections.Concurrent;
using System.Net;
using System.IO.Compression;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class HoYoDownloadProgressTests
{
    private const string Game = BuiltInGameIds.GenshinImpact;
    private const string Resource = "YuanShen_Data/StreamingAssets/AssetBundles/blocks/shared.blk";
    private const string LocalStage = "正在检查本地文件";
    private const string PeerStage = "正在校验另一服可复用资源";
    private const string CacheStage = "正在检查下载缓存";

    [Fact]
    public async Task PlanningReportsLocalAndCacheWorkWithoutDownloadingMissingOrCorruptFiles()
    {
        using var fixture = new Fixture();
        var install = fixture.Installation("game");
        await fixture.WriteAsync(install.InstallRoot!, "YuanShen.exe", "exe");
        await fixture.WriteAsync(install.InstallRoot!, Resource, "evil");
        var reports = new Reports();

        var plan = await fixture.Service.PlanAsync(Game, install, progress: reports);

        Assert.Equal("Persistent/user-settings", Assert.Single(plan.Missing).Path);
        Assert.Equal(Resource, Assert.Single(plan.Corrupt).Path);
        Assert.Equal(8, plan.DownloadBytes);
        Assert.Empty(fixture.Requests);
        AssertCompleted(reports, LocalStage, 3);
        AssertCompleted(reports, CacheStage, 2);
        Assert.Contains(reports.Values, p => p.Stage == LocalStage && p.CurrentFile == Resource);
        Assert.All(reports.Values, p => Assert.False(p.IsNetworkTransfer));
        Assert.Equal("evil", await File.ReadAllTextAsync(SafeGamePath.Resolve(install.InstallRoot!, Resource)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeerScanVerifiesContentBeforeOfferingReuseAndCompletesItsProgress(bool corruptPeer)
    {
        using var fixture = new Fixture();
        var peer = fixture.Installation("official");
        var target = fixture.Installation("bilibili", HoYoChannel.Bilibili);
        await fixture.WriteAsync(peer.InstallRoot!, Resource, corruptPeer ? "evil" : "good");
        var reports = new Reports();

        var plan = await fixture.Service.PlanAsync(Game, target, peer: peer, progress: reports);

        if (corruptPeer)
        {
            Assert.Empty(plan.Reusable!);
            Assert.Equal(11, plan.DownloadBytes);
        }
        else
        {
            Assert.Equal(Resource, Assert.Single(plan.Reusable!).File.Path);
            Assert.Equal(7, plan.DownloadBytes);
        }
        Assert.Empty(fixture.Requests);
        AssertCompleted(reports, LocalStage, 3);
        AssertCompleted(reports, PeerStage, 1);
        AssertCompleted(reports, CacheStage, corruptPeer ? 3 : 2);
        Assert.Contains(reports.Values, p => p.Stage == "正在读取另一服资源清单");
        Assert.Contains(reports.Values, p => p.Stage == PeerStage && p.CurrentFile == Resource);
        Assert.All(reports.Values, p => Assert.False(p.IsNetworkTransfer));
    }

    [Fact]
    public async Task CacheScanCountsValidAndInvalidObjectsWithoutMutatingThemOrUsingNetwork()
    {
        using var fixture = new Fixture();
        await fixture.CacheAsync(fixture.Files[0]);
        await fixture.CacheAsync(fixture.Files[1], "evil");
        await fixture.CacheAsync(fixture.Files[2]);
        var reports = new Reports();

        var plan = await fixture.Service.PlanAsync(Game, fixture.Installation("game"), progress: reports);

        Assert.Equal(4, plan.DownloadBytes);
        Assert.Equal(3, plan.Missing.Count);
        Assert.Empty(fixture.Requests);
        AssertCompleted(reports, CacheStage, 3);
        Assert.Equal("evil", await File.ReadAllTextAsync(fixture.CachePath(fixture.Files[1])));
        Assert.All(reports.Values, p => Assert.False(p.IsNetworkTransfer));
    }

    [Fact]
    public async Task CachedInstallWithPeerReuseClosesEachStageAndPreservesIndependentMutableFiles()
    {
        using var fixture = new Fixture();
        var peer = fixture.Installation("official");
        var target = fixture.Installation("bilibili", HoYoChannel.Bilibili);
        foreach (var file in fixture.Files)
        {
            await fixture.WriteAsync(peer.InstallRoot!, file.Path, fixture.Bytes[file.Md5]);
            await fixture.CacheAsync(file);
        }
        var reports = new Reports();

        await fixture.Service.SyncAsync(Game, target, false, reports, peer: peer);

        Assert.Empty(fixture.Requests);
        Assert.All(reports.Values.Where(p => p.IsNetworkTransfer), p => Assert.Equal(0, p.TransferredBytes ?? 0));
        Assert.Equal("1.0.0", target.InstalledVersion);
        Assert.False(target.MaintenanceInProgress);
        AssertCompleted(reports, "正在复用另一服资源", 1);
        AssertCompleted(reports, "正在组装并校验文件", 2);
        AssertCompleted(reports, "正在应用文件", 2);
        AssertCompleted(reports, "正在最终校验", 3);
        Assert.True(WindowsHardLink.Identity(SafeGamePath.Resolve(peer.InstallRoot!, Resource))
            .SameFile(WindowsHardLink.Identity(SafeGamePath.Resolve(target.InstallRoot!, Resource))));
        Assert.False(WindowsHardLink.Identity(SafeGamePath.Resolve(peer.InstallRoot!, "Persistent/user-settings"))
            .SameFile(WindowsHardLink.Identity(SafeGamePath.Resolve(target.InstallRoot!, "Persistent/user-settings"))));
        Assert.Equal("good", await File.ReadAllTextAsync(SafeGamePath.Resolve(peer.InstallRoot!, Resource)));
        Assert.Equal("文件维护完成", reports.Values.Last().Stage);
        Assert.False(reports.Values.Last().IsNetworkTransfer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HashProgressCanCancelBeforeReadingWholeFile(bool cacheCheck)
    {
        using var fixture = new Fixture();
        var bytes = new byte[8 * 1024 * 1024];
        RandomNumberGenerator.Fill(bytes);
        var path = await fixture.WriteAsync(fixture.Root, "large-resource.blk", bytes);
        var entry = new EndfieldManifestFile("large-resource.blk", bytes.Length, Hash(bytes));
        using var cancellation = new CancellationTokenSource();
        var reported = new List<long>();
        var progress = new BytesProgress(value =>
        {
            reported.Add(value);
            if (value > 0 && value < bytes.Length) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cacheCheck
            ? EndfieldDownloadService.MatchesAsync(path, entry.Size, entry.Md5, cancellation.Token, progress)
            : VerifiedFileCommit.MatchesAsync(path, entry, cancellation.Token, progress));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Contains(reported, value => value > 0 && value < bytes.Length);
        Assert.DoesNotContain((long)bytes.Length, reported);
        Assert.Equal(bytes.Length, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HashProgressCompletesAndStillDetectsSameSizeDamage(bool cacheCheck)
    {
        using var fixture = new Fixture();
        var bytes = new byte[512 * 1024];
        RandomNumberGenerator.Fill(bytes);
        var entry = new EndfieldManifestFile("resource.blk", bytes.Length, Hash(bytes));
        var path = await fixture.WriteAsync(fixture.Root, entry.Path, bytes);
        var reported = new List<long>();
        var progress = new BytesProgress(reported.Add);

        async Task<bool> MatchesAsync() => cacheCheck
            ? await EndfieldDownloadService.MatchesAsync(path, entry.Size, entry.Md5, byteProgress: progress)
            : await VerifiedFileCommit.MatchesAsync(path, entry, byteProgress: progress);

        Assert.True(await MatchesAsync());
        Assert.Equal(entry.Size, reported.Last());
        Assert.True(reported.SequenceEqual(reported.Order()));
        bytes[0] ^= 0xff;
        await File.WriteAllBytesAsync(path, bytes);
        reported.Clear();
        Assert.False(await MatchesAsync());
        Assert.Equal(entry.Size, reported.Last());
    }

    [Fact]
    public async Task ChannelSdkVerificationCompletesBeforeLocalFileInspectionBegins()
    {
        using var fixture = new Fixture(withSdk: true);
        var reports = new Reports();

        var plan = await fixture.Service.PlanAsync(Game, fixture.Installation("bilibili", HoYoChannel.Bilibili), progress: reports);

        Assert.Single(fixture.Requests);
        Assert.NotNull(fixture.Sdk);
        AssertCompleted(reports, "正在校验渠道组件", 1);
        AssertCompleted(reports, LocalStage, plan.Package.Files.Count);
        Assert.Equal(5, plan.Package.Files.Count);
        var values = reports.Values.ToArray();
        var verified = Array.FindLastIndex(values, p => p.Stage == "正在校验渠道组件");
        var local = Array.FindIndex(values, p => p.Stage == LocalStage);
        Assert.True(verified >= 0 && verified < local);
        Assert.Equal(1, values[verified].CompletedFiles);
        Assert.Contains(values, p => p.Stage == "正在校验渠道组件" && p.CompletedFiles == 0);
        Assert.Equal(fixture.Sdk!.Size,
            values.Where(p => p.Stage == "正在下载渠道组件").Max(p => p.TransferredBytes ?? 0));
    }

    [Fact]
    public async Task NetworkDownloadCompletesCountsAndReportsOnlyResponseBodyBytesAsTransferred()
    {
        using var fixture = new Fixture();
        var reports = new Reports();
        var install = fixture.Installation("game");

        await fixture.Service.SyncAsync(Game, install, false, reports);

        Assert.Equal(3, fixture.Requests.Count);
        AssertCompleted(reports, "正在下载", 3, network: true);
        var downloadReports = reports.Values.Where(p => p.Stage == "正在下载").ToArray();
        var responseBytes = fixture.Requests.Sum(uri => (long)fixture.Bytes[uri.Segments.Last()].Length);
        Assert.Equal(11, responseBytes);
        Assert.Equal(responseBytes, downloadReports[^1].TransferredBytes);
        Assert.Equal(11, downloadReports[^1].CompletedBytes);
        Assert.True(downloadReports.Select(p => p.TransferredBytes ?? 0)
            .SequenceEqual(downloadReports.Select(p => p.TransferredBytes ?? 0).Order()));
        Assert.Equal("1.0.0", install.InstalledVersion);
    }

    [Fact]
    public async Task PreloadVerificationCompletesWithoutApplyingFilesOrChangingInstalledVersion()
    {
        using var fixture = new Fixture();
        var install = fixture.Installation("game");
        install.InstalledVersion = "1.0.0";
        foreach (var file in fixture.Files)
            await fixture.WriteAsync(install.InstallRoot!, file.Path, fixture.Bytes[file.Md5]);
        await fixture.WriteAsync(install.InstallRoot!, Resource, "old!");
        await fixture.WriteAsync(install.InstallRoot!, "config.ini", "[General]\nchannel=1\ncps=mihoyo\ngame_version=1.0.0\n");
        var reports = new Reports();

        await fixture.Service.PreloadAsync(Game, install, reports);

        AssertCompleted(reports, "正在校验预下载内容", 1);
        Assert.Equal("预下载完成", reports.Values.Last().Stage);
        Assert.False(reports.Values.Last().IsNetworkTransfer);
        Assert.Equal("2.0.0", install.PreloadVersion);
        Assert.Equal("1.0.0", install.PreloadSourceVersion);
        Assert.Equal("1.0.0", install.InstalledVersion);
        Assert.Equal("1.0.0", HoYoMaintenanceService.ReadVersion(install.InstallRoot!));
        Assert.Equal("old!", await File.ReadAllTextAsync(SafeGamePath.Resolve(install.InstallRoot!, Resource)));
        Assert.False(install.MaintenanceInProgress);
        Assert.Single(fixture.Requests);
    }
    [Fact]
    public async Task ResumedDownloadCountsOnlyPartialResponseBodyAndCacheHitTransfersNothing()
    {
        using var fixture = new Fixture();
        var file = fixture.Files[1];
        var bytes = fixture.Bytes[file.Md5];
        const int prefixLength = 2;
        var partial = fixture.CachePath(file) + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        await File.WriteAllBytesAsync(partial, bytes[..prefixLength]);
        using var handler = new PartialContentHandler(bytes, prefixLength);
        using var client = new HttpClient(handler);
        var downloader = new EndfieldDownloadService(client, fixture.Data, "hoyo", HoYoDistributionProvider.TrustedUri);
        var completed = new List<long>();
        var transferred = new List<long>();

        var path = await downloader.DownloadAsync(file.Chunks[0].Uri, file.Md5, file.Size,
            byteProgress: new BytesProgress(completed.Add), transferredBytes: new BytesProgress(transferred.Add));

        Assert.Equal(1, handler.Requests);
        Assert.Equal(fixture.CachePath(file), path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(prefixLength, completed[0]);
        Assert.Equal(file.Size, completed[^1]);
        Assert.Equal(file.Size - prefixLength, transferred.Sum());
        Assert.False(File.Exists(partial));

        completed.Clear();
        transferred.Clear();
        await downloader.DownloadAsync(file.Chunks[0].Uri, file.Md5, file.Size,
            byteProgress: new BytesProgress(completed.Add), transferredBytes: new BytesProgress(transferred.Add));

        Assert.Equal(1, handler.Requests);
        Assert.Equal(file.Size, Assert.Single(completed));
        Assert.Empty(transferred);
    }
    private static void AssertCompleted(Reports reports, string stage, int files, bool network = false)
    {
        var values = reports.Values.Where(p => p.Stage == stage).ToArray();
        Assert.NotEmpty(values);
        Assert.All(values, p =>
        {
            Assert.Equal(files, p.TotalFiles);
            Assert.InRange(p.CompletedFiles, 0, p.TotalFiles);
            Assert.InRange(p.CompletedBytes, 0, p.TotalBytes);
            Assert.Equal(network, p.IsNetworkTransfer);
        });
        Assert.Equal(files, values[^1].CompletedFiles);
        Assert.Equal(values[^1].TotalBytes, values[^1].CompletedBytes);
        Assert.True(values.Select(p => p.CompletedFiles).SequenceEqual(values.Select(p => p.CompletedFiles).Order()));
        Assert.True(values.Select(p => p.CompletedBytes).SequenceEqual(values.Select(p => p.CompletedBytes).Order()));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
    private sealed class BytesProgress(Action<long> receive) : IProgress<long>
    {
        public void Report(long value) => receive(value);
    }
    private sealed class Reports : IProgress<HoYoProgress>
    {
        public ConcurrentQueue<HoYoProgress> Values { get; } = new();
        public void Report(HoYoProgress value) => Values.Enqueue(value);
    }

    private sealed class PartialContentHandler(byte[] bytes, int prefixLength) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var range = Assert.Single(request.Headers.Range!.Ranges);
            Assert.Equal(prefixLength, range.From);
            Assert.Null(range.To);
            var content = new ByteArrayContent(bytes[prefixLength..]);
            content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(prefixLength, bytes.LongLength - 1, bytes.LongLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }
    }
    private sealed class Fixture : HttpMessageHandler, IHoYoDistributionProvider
    {
        private static readonly string FixtureRoot = Path.GetFullPath(@"E:\Study\Launcher\.artifacts\hoyo-progress-tests");
        private readonly HttpClient _client;
        public string Root { get; } = Path.Combine(FixtureRoot, Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(Root, "cache");
        public Dictionary<string, byte[]> Bytes { get; } = [];
        public IReadOnlyList<HoYoFile> Files { get; }
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public HoYoMaintenanceService Service { get; }
        public HoYoSdk? Sdk { get; }

        public Fixture(bool withSdk = false)
        {
            Directory.CreateDirectory(Root);
            Files = [Entry("YuanShen.exe", "exe"), Entry(Resource, "good"), Entry("Persistent/user-settings", "user")];
            if (withSdk) Sdk = CreateSdk();
            _client = new HttpClient(this, disposeHandler: false);
            Service = new(this, new EndfieldDownloadService(_client, Data, "hoyo", HoYoDistributionProvider.TrustedUri),
                new HoYoContentCodec("unused"), Data);
        }

        private HoYoSdk CreateSdk()
        {
            using var output = new MemoryStream();
            var payload = Encoding.UTF8.GetBytes("fixture bilibili login");
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var entry = zip.CreateEntry("YuanShen_Data/Plugins/PCGameSDK.dll").Open()) entry.Write(payload);
                using var marker = new StreamWriter(zip.CreateEntry("sdk_pkg_version").Open());
                marker.Write(JsonSerializer.Serialize(new
                {
                    remoteName = "YuanShen_Data/Plugins/PCGameSDK.dll", fileSize = payload.Length, md5 = Hash(payload)
                }));
            }
            var bytes = output.ToArray();
            var md5 = Hash(bytes);
            Bytes[md5] = bytes;
            return new(new("https://autopatchcn.yuanshen.com/" + md5), md5, bytes.Length, 1024, "fixture", "sdk_pkg_version");
        }
        public HoYoInstallation Installation(string folder, HoYoChannel channel = HoYoChannel.Official)
            => new() { InstallRoot = Path.Combine(Root, folder), Channel = channel };

        private HoYoFile Entry(string path, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var md5 = Hash(bytes);
            Bytes[md5] = bytes;
            return new(path, bytes.Length, md5,
                [new(md5, md5, md5, 0, bytes.Length, bytes.Length, new("https://autopatchcn.yuanshen.com/" + md5), 0)]);
        }

        public string CachePath(HoYoFile file)
            => SafeGamePath.Resolve(Data, "hoyo/objects/" + DownloadTaskCache.ContentKey(file.Md5, file.Size));

        public Task CacheAsync(HoYoFile file, string? content = null)
        {
            var path = CachePath(file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return File.WriteAllBytesAsync(path, content is null ? Bytes[file.Md5] : Encoding.UTF8.GetBytes(content));
        }

        public Task<string> WriteAsync(string root, string relative, string content)
            => WriteAsync(root, relative, Encoding.UTF8.GetBytes(content));

        public async Task<string> WriteAsync(string root, string relative, byte[] content)
        {
            var path = SafeGamePath.Resolve(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content);
            return path;
        }

        public Task<HoYoRelease?> GetReleaseAsync(string gameId, CancellationToken token = default)
            => GetReleaseAsync(gameId, HoYoChannel.Official, token);

        public Task<HoYoRelease?> GetReleaseAsync(string gameId, HoYoChannel channel, CancellationToken token = default)
            => Task.FromResult<HoYoRelease?>(new(gameId, "fixture", "1.0.0", "YuanShen.exe", "Game", "2.0.0", Channel: channel));

        public Task<HoYoPackage> GetPackageAsync(string gameId, IReadOnlyCollection<string> languages,
            bool preload = false, CancellationToken token = default)
            => GetPackageAsync(gameId, HoYoChannel.Official, languages, preload, token);

        public async Task<HoYoPackage> GetPackageAsync(string gameId, HoYoChannel channel, IReadOnlyCollection<string> languages,
            bool preload = false, CancellationToken token = default)
        {
            var release = (await GetReleaseAsync(gameId, channel, token))!;
            if (preload) release = release with { Version = "2.0.0", PreloadVersion = null };
            return new(release, Files, "fixture-content", channel == HoYoChannel.Bilibili ? Sdk : null);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(Bytes[request.RequestUri!.Segments.Last()]) });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _client.Dispose();
                var resolved = Path.GetFullPath(Root);
                if (!resolved.StartsWith(FixtureRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Refusing to remove a path outside the progress test fixture.");
                if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
            base.Dispose(disposing);
        }
    }
}
