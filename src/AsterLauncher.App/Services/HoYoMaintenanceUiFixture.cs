#if DEBUG
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
namespace AsterLauncher.App.Services;
internal sealed class HoYoMaintenanceUiFixture : HttpMessageHandler, IHoYoDistributionProvider
{
    private readonly byte[] _exe = new byte[1024 * 1024];
    private readonly byte[] _data = new byte[12 * 1024 * 1024];
    public static bool Enabled => Environment.GetEnvironmentVariable("ASTERLAUNCHER_UI_TEST_HOYO") == "1"
        && LauncherDataPaths.ResolveDataDirectory().StartsWith(@"E:\Study\Launcher\.artifacts\ui-refresh\hoyo-", StringComparison.OrdinalIgnoreCase);
    public Task<HoYoRelease?> GetReleaseAsync(string gameId, CancellationToken token = default)
        => Task.FromResult<HoYoRelease?>(Release(gameId));
    private static HoYoRelease Release(string id) => new(id, "fixture", "1.0.0", BuiltInGameCatalog.CreateAdapters().Single(a => a.Definition.Id == id).Definition.ExecutableNames[0], id, "2.0.0");
    public Task<HoYoPackage> GetPackageAsync(string id, IReadOnlyCollection<string> languages, bool preload = false, CancellationToken token = default)
    {
        if (Environment.GetEnvironmentVariable("ASTERLAUNCHER_UI_TEST_HOYO_SLOW_CHECK") == "1")
            for (var i = 0; i < 60; i++) { token.ThrowIfCancellationRequested(); Thread.Sleep(50); }
        var r = Release(id); if (preload) r = r with { Version = "2.0.0" };
        return Task.FromResult(new HoYoPackage(r, [Entry(r.ExecutableName, _exe), Entry("fixture/data", _data)], preload ? "future" : "current"));
    }
    public Task<HoYoRelease?> GetReleaseAsync(string gameId, HoYoChannel channel, CancellationToken token = default)
        => Task.FromResult<HoYoRelease?>(Release(gameId) with { Channel = channel });
    public async Task<HoYoPackage> GetPackageAsync(string id, HoYoChannel channel, IReadOnlyCollection<string> languages, bool preload = false, CancellationToken token = default)
    {
        var package = await GetPackageAsync(id, languages, preload, token);
        return package with { Release = package.Release with { Channel = channel } };
    }
    private static HoYoFile Entry(string path, byte[] bytes)
    {
        var md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        return new(path, bytes.Length, md5, [new(md5, md5, md5, 0, bytes.Length, bytes.Length, new("https://autopatchcn.yuanshen.com/" + md5), 0)]);
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var exeHash = Convert.ToHexString(MD5.HashData(_exe));
        var bytes = request.RequestUri!.Segments.Last().Equals(exeHash, StringComparison.OrdinalIgnoreCase) ? _exe : _data;
        var offset = (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
        var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(new Slow(bytes, offset)) };
        response.Content.Headers.ContentLength = bytes.Length - offset;
        if (offset > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
        return Task.FromResult(response);
    }
    private sealed class Slow(byte[] bytes, int offset) : Stream
    {
        private int _position = offset;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => bytes.Length; public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Delay(Environment.GetEnvironmentVariable("ASTERLAUNCHER_UI_TEST_HOYO_SLOW_CHECK") == "1" ? 200 : 45, token); var count = Math.Min(64 * 1024, Math.Min(buffer.Length, bytes.Length - _position));
            bytes.AsMemory(_position, count).CopyTo(buffer); _position += count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
#endif