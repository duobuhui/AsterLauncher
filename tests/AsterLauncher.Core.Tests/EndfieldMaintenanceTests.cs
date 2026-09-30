using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldMaintenanceTests
{
    [Fact]
    public void FileAddressesPreserveFreshSignatureQuery()
    {
        var address = EndfieldDownloadService.FileUri(
            new Uri("https://ak-uc.hypergryph.com/resources/base/?signature=fresh"),
            "Endfield_Data/StreamingAssets/a b.chk");
        Assert.Equal("?signature=fresh", address.Query);
        Assert.Equal("/resources/base/Endfield_Data/StreamingAssets/a%20b.chk", address.AbsolutePath);
    }

    [Fact]
    public async Task HardLinksUseRealFileIdentityAndSafeCommitPreservesOtherChannel()
    {
        var root = CreateRoot();
        try
        {
            var official = Path.Combine(root, "official");
            var bilibili = Path.Combine(root, "bilibili");
            var relative = "Endfield_Data/StreamingAssets/VFS/0123ABCD/0123456789ABCDEF0123456789ABCDEF.chk";
            var first = SafeGamePath.Resolve(official, relative);
            var second = SafeGamePath.Resolve(bilibili, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            var original = Encoding.UTF8.GetBytes("immutable resource");
            await File.WriteAllBytesAsync(first, original);
            var file = Entry(relative, original);
            var summary = await new EndfieldSharingService().OptimizeAsync(
                official, bilibili, [file], [file]);
            Assert.Equal(1, summary.FileCount);
            Assert.Equal(original.Length, summary.SharedBytes);
            Assert.Equal(WindowsHardLink.EstimatedAllocatedBytes(first), summary.EstimatedSavedBytes);
            Assert.True(WindowsHardLink.Identity(first).SameFile(WindowsHardLink.Identity(second)));
            Assert.True(WindowsHardLink.Identity(first).LinkCount >= 2);

            var changed = Encoding.UTF8.GetBytes("new version content");
            var staged = Path.Combine(root, "staged");
            await File.WriteAllBytesAsync(staged, changed);
            await VerifiedFileCommit.CommitAsync(staged, first, Entry(relative, changed));
            Assert.False(WindowsHardLink.Identity(first).SameFile(WindowsHardLink.Identity(second)));
            Assert.Equal(original, await File.ReadAllBytesAsync(second));
            File.Delete(first);
            Assert.Equal(original, await File.ReadAllBytesAsync(second));
        }
        finally { RemoveRoot(root); }
    }

    [Fact]
    public async Task DifferentAndMutableFilesStayIndependent_AndUnshareCopiesOnlyOwnEntry()
    {
        var root = CreateRoot();
        try
        {
            var a = Path.Combine(root, "official");
            var b = Path.Combine(root, "bilibili");
            var shared = "Endfield_Data/StreamingAssets/VFS/0123ABCD/0123456789ABCDEF0123456789ABCDEF.chk";
            var config = "config.ini";
            var different = "Endfield_Data/StreamingAssets/VFS/ABCDEF12/FEDCBA9876543210FEDCBA9876543210.chk";
            var bytes = Encoding.UTF8.GetBytes("identical");
            foreach (var dir in new[] { a, b })
            {
                var path = SafeGamePath.Resolve(dir, shared);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes);
                await File.WriteAllBytesAsync(SafeGamePath.Resolve(dir, config), bytes);
                var differentPath = SafeGamePath.Resolve(dir, different);
                Directory.CreateDirectory(Path.GetDirectoryName(differentPath)!);
                await File.WriteAllBytesAsync(differentPath, bytes);
            }
            var filesA = new[] { Entry(shared, bytes), Entry(config, bytes), Entry(different, bytes) };
            var filesB = new[] { Entry(shared, bytes), Entry(config, bytes),
                Entry(different, Encoding.UTF8.GetBytes("different")) };
            var sharing = new EndfieldSharingService();
            var result = await sharing.OptimizeAsync(a, b, filesA, filesB);
            Assert.Equal(1, result.FileCount);
            Assert.False(WindowsHardLink.Identity(SafeGamePath.Resolve(a, config))
                .SameFile(WindowsHardLink.Identity(SafeGamePath.Resolve(b, config))));
            Assert.False(WindowsHardLink.Identity(SafeGamePath.Resolve(a, different))
                .SameFile(WindowsHardLink.Identity(SafeGamePath.Resolve(b, different))));
            File.Delete(SafeGamePath.Resolve(b, config));
            Assert.True(WindowsHardLink.TryCreate(SafeGamePath.Resolve(b, config), SafeGamePath.Resolve(a, config)));
            Assert.Equal(2, await sharing.UnshareAsync(b, []));
            Assert.False(WindowsHardLink.Identity(SafeGamePath.Resolve(a, config))
                .SameFile(WindowsHardLink.Identity(SafeGamePath.Resolve(b, config))));
            Assert.Equal(0, sharing.ScanLocal(a, b).FileCount);
            Assert.False(WindowsHardLink.Identity(SafeGamePath.Resolve(a, shared))
                .SameFile(WindowsHardLink.Identity(SafeGamePath.Resolve(b, shared))));
        }
        finally { RemoveRoot(root); }
    }

    [Fact]
    public async Task SameSizeCorruptionIsDetected_AndCrossVolumeIsNotClaimed()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "same-size");
            var original = Encoding.UTF8.GetBytes("abc");
            await File.WriteAllBytesAsync(path, original);
            Assert.True(await VerifiedFileCommit.MatchesAsync(path, Entry("same-size", original)));
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("abd"));
            Assert.False(await VerifiedFileCommit.MatchesAsync(path, Entry("same-size", original)));
            Assert.False(WindowsHardLink.CanShareVolume(root, @"Z:\no-such-install"));
            var summary = await new EndfieldSharingService().OptimizeAsync(
                root, @"Z:\no-such-install", [], []);
            Assert.Equal(0, summary.EstimatedSavedBytes);
        }
        finally { RemoveRoot(root); }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("safe/../../outside")]
    [InlineData("config.ini:stream")]
    [InlineData("CON.txt")]
    [InlineData("folder./file")]
    public void RejectsUnsafeManifestPaths(string path) =>
        Assert.Throws<InvalidDataException>(() => SafeGamePath.ValidateRelative(path));

    [Fact]
    public void OnlyObservedHexNamedVfsChunksAreShareable()
    {
        Assert.True(EndfieldSharingService.IsShareable(
            "Endfield_Data/StreamingAssets/VFS/0123ABCD/0123456789ABCDEF0123456789ABCDEF.chk"));
        Assert.False(EndfieldSharingService.IsShareable(
            "Endfield_Data/StreamingAssets/index_main.json"));
        Assert.False(EndfieldSharingService.IsShareable(
            "Endfield_Data/StreamingAssets/VFS/0123ABCD/0123ABCD.blc"));
        Assert.False(EndfieldSharingService.IsShareable("config.ini"));
        Assert.False(EndfieldSharingService.IsShareable(
            "Endfield_Data/StreamingAssets/VFS/0123ABCD/user-settings.chk"));
    }
    [Fact]
    public async Task RangeResumeAndSignatureRefreshKeepContentIdentity()
    {
        var root = CreateRoot();
        try
        {
            var payload = Encoding.UTF8.GetBytes("0123456789abcdef");
            var md5 = Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
            var key = md5 + "-" + payload.Length;
            var cache = Path.Combine(root, "endfield", "objects");
            Directory.CreateDirectory(cache);
            await File.WriteAllBytesAsync(Path.Combine(cache, key + ".part"), payload[..6]);
            var calls = 0;
            using var client = new HttpClient(new Handler(request =>
            {
                calls++;
                if (calls == 1)
                {
                    Assert.Equal(6, request.Headers.Range?.Ranges.Single().From);
                    var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                    {
                        Content = new ByteArrayContent(payload[6..])
                    };
                    response.Content.Headers.ContentRange =
                        new ContentRangeHeaderValue(6, payload.Length - 1, payload.Length);
                    return response;
                }
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }));
            var downloader = new EndfieldDownloadService(client, root);
            var uri = new Uri("https://beyond.hycdn.cn/file?auth_key=old");
            var result = await downloader.DownloadAsync(uri, md5, payload.Length);
            Assert.Equal(payload, await File.ReadAllBytesAsync(result));
            Assert.Equal(1, calls);

            File.Delete(result);
            var signatureCalls = 0;
            using var refreshClient = new HttpClient(new Handler(request =>
            {
                signatureCalls++;
                return request.RequestUri!.Query.Contains("fresh")
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }
                    : new HttpResponseMessage(HttpStatusCode.Forbidden);
            }));
            var refreshed = new EndfieldDownloadService(refreshClient, root);
            var downloaded = await refreshed.DownloadAsync(uri, md5, payload.Length,
                _ => Task.FromResult(new Uri("https://beyond.hycdn.cn/file?auth_key=fresh")));
            Assert.Equal(payload, await File.ReadAllBytesAsync(downloaded));
            Assert.Equal(2, signatureCalls);
        }
        finally { RemoveRoot(root); }
    }

    [Fact]
    public async Task Returned200InsteadOfRangeRestartsPartialFile()
    {
        var root = CreateRoot();
        try
        {
            var payload = Encoding.UTF8.GetBytes("actual content");
            var md5 = Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
            var cache = Path.Combine(root, "endfield", "objects");
            Directory.CreateDirectory(cache);
            await File.WriteAllBytesAsync(Path.Combine(cache, md5 + "-" + payload.Length + ".part"),
                Encoding.UTF8.GetBytes("wrong"));
            using var client = new HttpClient(new Handler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
            var path = await new EndfieldDownloadService(client, root).DownloadAsync(
                new Uri("https://beyond.hycdn.cn/file"), md5, payload.Length);
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        }
        finally { RemoveRoot(root); }
    }

    [Fact]
    public void LegacyConfigurationRemainsUnassigned()
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<LauncherConfiguration>(
            "{\"games\":[{\"gameId\":\"endfield\",\"executablePath\":\"E:\\\\Old\\\\Endfield.exe\"}]}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(EndfieldChannel.Unknown, config!.SelectedEndfieldChannel);
        Assert.Empty(config.EndfieldInstallations);
        Assert.Equal(@"E:\Old\Endfield.exe", config.Games.Single().ExecutablePath);
    }

    [Fact]
    public async Task VersionCheckUsesOnlyChannelMetadataWithoutDownloadingManifest()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var game = document.RootElement.GetProperty("proxy_reqs")[0]
                .GetProperty("get_latest_game_req");
            Assert.Equal("2", game.GetProperty("channel").GetString());
            Assert.Equal("2", game.GetProperty("sub_channel").GetString());
            Assert.Equal("1.0.0", game.GetProperty("version").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"proxy_rsps\":[{\"kind\":\"ignored\"},{\"kind\":\"get_latest_game\",\"get_latest_game_rsp\":{\"version\":\"1.0.1\"}}]}")
            };
        }));
        var provider = new EndfieldDistributionProvider(client);
        Assert.Equal("1.0.1", await provider.GetLatestVersionAsync(EndfieldChannel.Bilibili, "1.0.0"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Range416RestartsAndCorruptCacheIsReplaced()
    {
        var root = CreateRoot();
        try
        {
            var payload = Encoding.UTF8.GetBytes("verified content");
            var md5 = Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
            var cache = Path.Combine(root, "endfield", "objects");
            Directory.CreateDirectory(cache);
            var key = md5 + "-" + payload.Length;
            await File.WriteAllBytesAsync(Path.Combine(cache, key), Encoding.UTF8.GetBytes("damaged content!"));
            await File.WriteAllBytesAsync(Path.Combine(cache, key + ".part"), payload[..4]);
            var calls = 0;
            using var client = new HttpClient(new Handler(request =>
            {
                calls++;
                return calls == 1
                    ? new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            }));
            var result = await new EndfieldDownloadService(client, root).DownloadAsync(
                new Uri("https://beyond.hycdn.cn/test"), md5, payload.Length);
            Assert.Equal(2, calls);
            Assert.Equal(payload, await File.ReadAllBytesAsync(result));
        }
        finally { RemoveRoot(root); }
    }

    [Fact]
    public async Task PredownloadCachesVerifiedObjectWithoutChangingInstalledVersionOrFiles()
    {
        var root = CreateRoot();
        try
        {
            var installRoot = Path.Combine(root, "official");
            Directory.CreateDirectory(installRoot);
            var gameFile = Path.Combine(installRoot, "Endfield.exe");
            await File.WriteAllTextAsync(gameFile, "old game");
            var bytes = Encoding.UTF8.GetBytes("future patch");
            var md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
            using var client = new HttpClient(new Handler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            var download = new EndfieldDownloadService(client, root);
            var patch = new EndfieldPatch("1.1.0",
                [new EndfieldRemoteObject(new Uri("https://beyond.hycdn.cn/patch"), md5, bytes.Length)],
                md5, bytes.Length);
            var service = new EndfieldMaintenanceService(new FixedPreloadProvider(patch), download,
                new EndfieldSharingService(), root);
            var installation = new EndfieldInstallation
            {
                Channel = EndfieldChannel.Official,
                InstallRoot = installRoot,
                InstalledVersion = "1.0.0"
            };
            await service.PreloadAsync(installation, patch);
            Assert.Equal("1.0.0", installation.InstalledVersion);
            Assert.Equal("old game", await File.ReadAllTextAsync(gameFile));
            Assert.Equal(EndfieldPreloadState.Completed, installation.PreloadState);
            Assert.True(await EndfieldDownloadService.MatchesAsync(
                Path.Combine(root, "endfield", "objects", md5 + "-" + bytes.Length),
                bytes.Length, md5));
        }
        finally { RemoveRoot(root); }
    }
    [Fact]
    public async Task PredownloadRefusesWithdrawnOfferBeforeTouchingGameFiles()
    {
        var root = CreateRoot();
        try
        {
            var installRoot = Path.Combine(root, "official");
            Directory.CreateDirectory(installRoot);
            var gameFile = Path.Combine(installRoot, "Endfield.exe");
            await File.WriteAllTextAsync(gameFile, "old game");
            var bytes = Encoding.UTF8.GetBytes("future patch");
            var md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
            var patch = new EndfieldPatch("1.1.0",
                [new EndfieldRemoteObject(new Uri("https://beyond.hycdn.cn/patch"), md5, bytes.Length)],
                md5, bytes.Length);
            using var client = new HttpClient(new Handler(_ =>
                throw new InvalidOperationException("No download should start.")));
            var service = new EndfieldMaintenanceService(new FixedPreloadProvider(null),
                new EndfieldDownloadService(client, root), new EndfieldSharingService(), root);
            var installation = new EndfieldInstallation
            {
                Channel = EndfieldChannel.Official,
                InstallRoot = installRoot,
                InstalledVersion = "1.0.0"
            };
            await Assert.ThrowsAsync<InvalidDataException>(() => service.PreloadAsync(installation, patch));
            Assert.Equal("1.0.0", installation.InstalledVersion);
            Assert.Equal("old game", await File.ReadAllTextAsync(gameFile));
            Assert.NotEqual(EndfieldPreloadState.Completed, installation.PreloadState);
        }
        finally { RemoveRoot(root); }
    }
    private static EndfieldManifestFile Entry(string path, byte[] bytes) =>
        new(path, bytes.Length, Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant());

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "aster-endfield-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void RemoveRoot(string root)
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "aster-endfield-tests"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its isolated directory.");
        Directory.Delete(root, recursive: true);
    }

    private sealed class FixedPreloadProvider(EndfieldPatch? patch) : IEndfieldDistributionProvider
    {
        public Task<string> GetLatestVersionAsync(EndfieldChannel channel, string? installedVersion,
            CancellationToken cancellationToken = default) => Task.FromResult(patch?.TargetVersion ?? "1.1.0");
        public Task<EndfieldPackage> GetPackageAsync(EndfieldChannel channel, string? installedVersion,
            CancellationToken cancellationToken = default) => Task.FromResult(new EndfieldPackage(
                channel, patch?.TargetVersion ?? "1.1.0", new Uri("https://beyond.hycdn.cn/files/"),
                new string('0', 32), [], null, patch));
    }
    private sealed class UnusedProvider : IEndfieldDistributionProvider
    {
        public Task<string> GetLatestVersionAsync(EndfieldChannel channel, string? installedVersion,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Not expected");
        public Task<EndfieldPackage> GetPackageAsync(EndfieldChannel channel, string? installedVersion,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Not expected");
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
