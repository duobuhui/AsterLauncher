using System.Net;
using System.Text;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsterLauncher.Core.Tests;

public sealed class WallpaperUpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? AppContext.BaseDirectory,
        "AsterLauncher.WallpaperTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StartupCheck_DownloadsChangedImagesOnce_AndKeepsCachedImages()
    {
        using var handler = new PublisherHandler();
        using var client = new HttpClient(handler);
        var service = new WallpaperUpdateService(client, _root,
            NullLogger<WallpaperUpdateService>.Instance);

        var first = await service.RefreshOnceAsync();
        Assert.Contains(BuiltInGameIds.HonkaiStarRail, first);
        Assert.Contains(BuiltInGameIds.Endfield, first);
        Assert.Contains(BuiltInGameIds.Arknights, first);
        Assert.Equal(3, handler.ImageRequests);
        Assert.True(File.Exists(Path.Combine(_root, "artwork", "cloud",
            BuiltInGameIds.HonkaiStarRail + ".jpg")));

        var second = await service.RefreshOnceAsync();
        Assert.Empty(second);
        Assert.Equal(3, handler.ImageRequests);
        Assert.Equal(2, handler.GameListRequests);

        handler.StarRailRevision = "new";
        var third = await service.RefreshOnceAsync();
        Assert.Equal([BuiltInGameIds.HonkaiStarRail], third);
        Assert.Equal(4, handler.ImageRequests);
    }

    [Fact]
    public void PublisherPages_SelectCurrentVersionAndHomeImage()
    {
        var endfield = """
            {"title":"旧版本更新说明","displayTime":1,"cover":"https://web.hycdn.cn/upload/image/old.jpg"}
            {"title":"新版本更新说明","displayTime":2,"cover":"https://web.hycdn.cn/upload/image/new.jpg"}
            """;
        Assert.Equal("https://web.hycdn.cn/upload/image/new.jpg",
            WallpaperUpdateService.ExtractEndfieldVersionImage(endfield));
        Assert.Equal("https://web.hycdn.cn/upload/image/current.jpg",
            WallpaperUpdateService.ExtractArknightsHomeImage(
                """<link rel="preload" as="image" href="https://web.hycdn.cn/upload/image/current.jpg"/>"""));
    }

    public void Dispose()
    {
        var temp = Path.GetFullPath(Environment.GetEnvironmentVariable("TEMP") ?? AppContext.BaseDirectory);
        var root = Path.GetFullPath(_root);
        if (root.StartsWith(temp.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class PublisherHandler : HttpMessageHandler
    {
        public string StarRailRevision { get; set; } = "initial";
        public int ImageRequests { get; private set; }
        public int GameListRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            byte[] bytes;
            if (url.Contains("/getGames?", StringComparison.Ordinal))
            {
                GameListRequests++;
                bytes = Encoding.UTF8.GetBytes(
                    """{"data":{"games":[{"biz":"hkrpg_cn","display":{"background":{"url":"https://launcher-webstatic.mihoyo.com/launcher-public/REVISION.webp"}}}]}}""".Replace("REVISION", StarRailRevision));
            }
            else if (url == "https://endfield.hypergryph.com/")
            {
                bytes = Encoding.UTF8.GetBytes(
                    """{"title":"新版本更新说明","displayTime":2,"cover":"https://web.hycdn.cn/upload/image/endfield.jpg"}""");
            }
            else if (url == "https://ak.hypergryph.com/")
            {
                bytes = Encoding.UTF8.GetBytes(
                    """<link rel="preload" as="image" href="https://web.hycdn.cn/upload/image/arknights.jpg"/>""");
            }
            else
            {
                ImageRequests++;
                bytes = new byte[50100];
                bytes[0] = 0xFF;
                bytes[1] = 0xD8;
                bytes[2] = 0xFF;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        }
    }
}