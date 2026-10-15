using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldNetworkProgressTests
{
    [Theory]
    [InlineData("cold")]
    [InlineData("cached")]
    [InlineData("resumed")]
    public async Task PreloadReportsOnlyResponseBytesAsNetworkTraffic(string mode)
    {
        var root = CreateRoot();
        try
        {
            var payload = Encoding.UTF8.GetBytes("an official patch object with a saved prefix");
            var pack = Object("patch", payload);
            var prefix = mode == "resumed" ? 7 : 0;
            var expectedTraffic = mode == "cached" ? 0 : payload.Length - prefix;
            await SeedAsync(root, pack, payload, mode, prefix);
            var calls = 0;
            using var client = new HttpClient(new Handler(request =>
            {
                Interlocked.Increment(ref calls);
                Assert.NotEqual("cached", mode);
                Assert.Equal(prefix == 0 ? null : (long?)prefix, request.Headers.Range?.Ranges.Single().From);
                var response = new HttpResponseMessage(prefix > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload[prefix..])
                };
                if (prefix > 0)
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(prefix, payload.Length - 1, payload.Length);
                return response;
            }));
            var patch = new EndfieldPatch("1.6.3", [pack], pack.Md5, pack.Size);
            var installation = new EndfieldInstallation
            {
                Channel = EndfieldChannel.Bilibili,
                InstalledVersion = "1.5.3"
            };
            var progress = new ProgressLog();
            var service = new EndfieldMaintenanceService(new Provider(patch),
                new EndfieldDownloadService(client, root), new EndfieldSharingService(static () => { }), root, null, static () => { });

            await service.PreloadAsync(installation, patch, progress);

            var reports = progress.Items.ToArray();
            Assert.NotEmpty(reports);
            Assert.All(reports, item => Assert.NotNull(item.NetworkBytes));
            Assert.Equal((long)expectedTraffic, reports.Max(item => item.NetworkBytes!.Value));
            Assert.Contains(reports, item => item.CompletedBytes == payload.Length
                && item.NetworkBytes == expectedTraffic);
            Assert.Equal(mode == "cached" ? 0 : 1, calls);
            Assert.Equal(EndfieldPreloadState.Completed, installation.PreloadState);
            Assert.Equal("1.5.3", installation.InstalledVersion);
            Assert.Equal(payload, await File.ReadAllBytesAsync(CachePath(root, pack)));
        }
        finally { RemoveRoot(root); }
    }

    [Fact]
    public async Task ConcurrentPreloadAggregatesNewTrafficAndExcludesCachedObjects()
    {
        var root = CreateRoot();
        try
        {
            var cached = Encoding.UTF8.GetBytes("already verified pack");
            var first = Encoding.UTF8.GetBytes("first network pack");
            var second = Encoding.UTF8.GetBytes("second network pack");
            var objects = new[] { Object("cached", cached), Object("first", first), Object("second", second) };
            await SeedAsync(root, objects[0], cached, "cached", 0);
            var calls = new ConcurrentQueue<string>();
            using var client = new HttpClient(new Handler(request =>
            {
                var name = request.RequestUri!.Segments.Last();
                calls.Enqueue(name);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(name switch
                    {
                        "first" => first,
                        "second" => second,
                        _ => throw new InvalidOperationException("The cached pack must not use the network.")
                    })
                };
            }));
            var patch = new EndfieldPatch("1.6.3", objects, null, objects.Sum(item => item.Size));
            var progress = new ProgressLog();
            var service = new EndfieldMaintenanceService(new Provider(patch),
                new EndfieldDownloadService(client, root), new EndfieldSharingService(static () => { }), root, null, static () => { });

            await service.PreloadAsync(new EndfieldInstallation
            {
                Channel = EndfieldChannel.Official,
                InstalledVersion = "1.5.3"
            }, patch, progress);

            var expected = first.Length + second.Length;
            Assert.Equal(2, calls.Count);
            Assert.All(progress.Items, item => Assert.InRange(item.NetworkBytes!.Value, 0, expected));
            Assert.Contains(progress.Items, item => item.NetworkBytes == expected);
            Assert.Contains(progress.Items, item => item.CompletedBytes == objects.Sum(pack => pack.Size));
        }
        finally { RemoveRoot(root); }
    }

    [Fact]
    public async Task ACacheHitStillReportsCompletedWorkButNoTransferredBytes()
    {
        var root = CreateRoot();
        try
        {
            var payload = Encoding.UTF8.GetBytes("verified object");
            var pack = Object("cached", payload);
            await SeedAsync(root, pack, payload, "cached", 0);
            using var client = new HttpClient(new Handler(_ =>
                throw new InvalidOperationException("A verified cache hit cannot start HTTP.")));
            var work = new ConcurrentQueue<long>();
            var traffic = new ConcurrentQueue<long>();

            await new EndfieldDownloadService(client, root).DownloadAsync(pack.Uri, pack.Md5, pack.Size,
                byteProgress: new InlineProgress<long>(work.Enqueue),
                transferredBytes: new InlineProgress<long>(traffic.Enqueue));

            Assert.Equal((long)payload.Length, Assert.Single(work));
            Assert.Empty(traffic);
        }
        finally { RemoveRoot(root); }
    }

    private static EndfieldRemoteObject Object(string name, byte[] bytes) =>
        new(new Uri("https://beyond.hycdn.cn/" + name), Convert.ToHexString(MD5.HashData(bytes)), bytes.Length);

    private static string CachePath(string root, EndfieldRemoteObject pack) =>
        Path.Combine(root, "endfield", "objects", DownloadTaskCache.ContentKey(pack.Md5, pack.Size));

    private static async Task SeedAsync(string root, EndfieldRemoteObject pack, byte[] bytes, string mode, int prefix)
    {
        if (mode == "cold") return;
        var path = CachePath(root, pack);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path + (mode == "resumed" ? ".part" : ""),
            mode == "resumed" ? bytes[..prefix] : bytes);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "aster-endfield-network-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void RemoveRoot(string root)
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "aster-endfield-network-tests"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
            throw new InvalidOperationException("Test cleanup escaped its isolated fixture.");
        Directory.Delete(resolved, recursive: true);
    }

    private sealed class ProgressLog : IProgress<EndfieldMaintenanceProgress>
    {
        public ConcurrentQueue<EndfieldMaintenanceProgress> Items { get; } = new();
        public void Report(EndfieldMaintenanceProgress value) => Items.Enqueue(value);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class Provider(EndfieldPatch patch) : IEndfieldDistributionProvider
    {
        public Task<string> GetLatestVersionAsync(EndfieldChannel channel, string? installedVersion,
            CancellationToken cancellationToken = default) => Task.FromResult(patch.TargetVersion);

        public Task<EndfieldPackage> GetPackageAsync(EndfieldChannel channel, string? installedVersion,
            CancellationToken cancellationToken = default) => Task.FromResult(new EndfieldPackage(channel,
                patch.TargetVersion, new Uri("https://beyond.hycdn.cn/files/"), new string('0', 32),
                [], null, patch));
    }
}