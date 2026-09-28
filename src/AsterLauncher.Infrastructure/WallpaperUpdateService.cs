using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AsterLauncher.Core;
using Microsoft.Extensions.Logging;

namespace AsterLauncher.Infrastructure;

/// <summary>Checks publisher artwork once per app launch and keeps verified images beside launcher data.</summary>
public sealed class WallpaperUpdateService
{
    private const string HoYoGamesUrl =
        "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGames?launcher_id=jGHBHlcOq1&language=zh-cn";
    private const string EndfieldHomeUrl = "https://endfield.hypergryph.com/";
    private const string ArknightsHomeUrl = "https://ak.hypergryph.com/";
    private const int MaxMetadataBytes = 2 * 1024 * 1024;
    private const int MaxImageBytes = 12 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> HoYoGameIds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hk4e_cn"] = BuiltInGameIds.GenshinImpact,
            ["bh3_cn"] = BuiltInGameIds.HonkaiImpact3rd,
            ["hkrpg_cn"] = BuiltInGameIds.HonkaiStarRail,
            ["nap_cn"] = BuiltInGameIds.ZenlessZoneZero,
            ["hyg_cn"] = BuiltInGameIds.PetitPlanet
        };

    private static readonly HashSet<string> ImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "launcher-webstatic.mihoyo.com",
        "launcher-webstatic.hoyoverse.com",
        "web.hycdn.cn"
    };

    private readonly HttpClient _http;
    private readonly ILogger<WallpaperUpdateService> _logger;
    private readonly string _cacheDirectory;
    private readonly string _indexPath;

    public WallpaperUpdateService(HttpClient http, string dataDirectory, ILogger<WallpaperUpdateService> logger)
    {
        _http = http;
        _logger = logger;
        _cacheDirectory = Path.Combine(dataDirectory, "artwork", "cloud");
        _indexPath = Path.Combine(_cacheDirectory, "sources.json");
    }

    public async Task<IReadOnlyList<string>> RefreshOnceAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var knownSources = ReadSources();
        var changed = new List<string>();

        try
        {
            var bytes = await ReadLimitedAsync(HoYoGamesUrl, MaxMetadataBytes, cancellationToken);
            foreach (var (gameId, sourceUrl) in ParseHoYoBackgrounds(bytes))
            {
                await TryRefreshGameAsync(gameId, sourceUrl, knownSources, changed, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
            or InvalidDataException or JsonException)
        {
            _logger.LogWarning("HoYoPlay wallpaper check failed: {ErrorType}", exception.GetType().Name);
        }

        await TryRefreshPublisherPageAsync(BuiltInGameIds.Endfield, EndfieldHomeUrl,
            ExtractEndfieldVersionImage, knownSources, changed, cancellationToken);
        await TryRefreshPublisherPageAsync(BuiltInGameIds.Arknights, ArknightsHomeUrl,
            ExtractArknightsHomeImage, knownSources, changed, cancellationToken);

        if (changed.Count > 0)
        {
            SaveSources(knownSources);
        }

        return changed;
    }

    private async Task TryRefreshPublisherPageAsync(
        string gameId,
        string pageUrl,
        Func<string, string?> extractImage,
        Dictionary<string, string> knownSources,
        List<string> changed,
        CancellationToken cancellationToken)
    {
        try
        {
            var html = Encoding.UTF8.GetString(
                await ReadLimitedAsync(pageUrl, MaxMetadataBytes, cancellationToken));
            var sourceUrl = extractImage(html);
            if (sourceUrl is null)
            {
                _logger.LogInformation("No verified wallpaper found on publisher page for {GameId}", gameId);
                return;
            }

            await TryRefreshGameAsync(gameId, sourceUrl, knownSources, changed, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
            or InvalidDataException)
        {
            _logger.LogWarning("Publisher wallpaper check failed for {GameId}: {ErrorType}",
                gameId, exception.GetType().Name);
        }
    }

    private async Task TryRefreshGameAsync(
        string gameId,
        string sourceUrl,
        Dictionary<string, string> knownSources,
        List<string> changed,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source)
            || source.Scheme != Uri.UriSchemeHttps
            || !ImageHosts.Contains(source.Host)
            || source.UserInfo.Length != 0)
        {
            _logger.LogWarning("Publisher returned an unsupported wallpaper location for {GameId}", gameId);
            return;
        }

        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl)));
        var imagePath = Path.Combine(_cacheDirectory, gameId + ".jpg");
        if (knownSources.TryGetValue(gameId, out var oldHash)
            && oldHash == sourceHash
            && IsJpegFile(imagePath))
        {
            return;
        }

        try
        {
            var separator = sourceUrl.Contains('?') ? "&" : "?";
            var jpegUrl = sourceUrl + separator + "x-oss-process=image/format,jpg";
            var image = await ReadLimitedAsync(jpegUrl, MaxImageBytes, cancellationToken);
            if (!IsJpeg(image))
            {
                throw new InvalidDataException("Publisher image was not JPEG.");
            }

            var temporaryPath = imagePath + ".download";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, image, cancellationToken);
                File.Move(temporaryPath, imagePath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            knownSources[gameId] = sourceHash;
            changed.Add(gameId);
            _logger.LogInformation("Wallpaper cache updated for {GameId}", gameId);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
            or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning("Wallpaper download failed for {GameId}: {ErrorType}",
                gameId, exception.GetType().Name);
        }
    }

    private async Task<byte[]> ReadLimitedAsync(string url, int limit, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Publisher returned HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is > 0
            && response.Content.Headers.ContentLength > limit)
        {
            throw new InvalidDataException("Publisher response exceeded the size limit.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > limit)
            {
                throw new InvalidDataException("Publisher response exceeded the size limit.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public static IReadOnlyDictionary<string, string> ParseHoYoBackgrounds(byte[] response)
    {
        using var document = JsonDocument.Parse(response);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("games", out var games)
            || games.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("HoYoPlay game list is missing.");
        }

        foreach (var game in games.EnumerateArray())
        {
            var biz = game.TryGetProperty("biz", out var bizNode) ? bizNode.GetString() : null;
            if (biz is null || !HoYoGameIds.TryGetValue(biz, out var gameId)
                || !game.TryGetProperty("display", out var display)
                || !display.TryGetProperty("background", out var background)
                || !background.TryGetProperty("url", out var urlNode))
            {
                continue;
            }

            var url = urlNode.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                result[gameId] = url;
            }
        }
        return result;
    }

    public static string? ExtractEndfieldVersionImage(string html)
    {
        var normalized = html.Replace("\\\"", "\"");
        var matches = Regex.Matches(normalized,
            "\"title\":\"[^\"]*版本更新说明[^\"]*\"[^{}]{0,250}\"displayTime\":(?<time>\\d+),\"cover\":\"(?<url>https://web\\.hycdn\\.cn/upload/image/[^\"]+\\.(?:jpg|png))\"",
            RegexOptions.IgnoreCase);
        return matches.Cast<Match>()
            .OrderByDescending(match => long.TryParse(match.Groups["time"].Value, out var time) ? time : 0)
            .Select(match => match.Groups["url"].Value)
            .FirstOrDefault();
    }
    public static string? ExtractArknightsHomeImage(string html)
    {
        var matches = Regex.Matches(html,
            "<link[^>]*rel=\"preload\"[^>]*as=\"image\"[^>]*href=\"(?<url>https://web\\.hycdn\\.cn/upload/image/[^\"]+\\.(?:jpg|png))\"",
            RegexOptions.IgnoreCase);
        return matches.Count > 0 ? matches[0].Groups["url"].Value : null;
    }

    private Dictionary<string, string> ReadSources()
    {
        try
        {
            return File.Exists(_indexPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_indexPath))
                    ?? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _logger.LogWarning("Wallpaper source index could not be read: {ErrorType}",
                exception.GetType().Name);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void SaveSources(Dictionary<string, string> sources)
    {
        var temporaryPath = _indexPath + ".download";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(sources));
            File.Move(temporaryPath, _indexPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Wallpaper source index could not be saved: {ErrorType}",
                exception.GetType().Name);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsJpegFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var file = File.OpenRead(path);
        if (file.Length < 50000)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[3];
        return file.Read(header) == 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
    }

    private static bool IsJpeg(ReadOnlySpan<byte> image) =>
        image.Length >= 50000
        && image[0] == 0xFF && image[1] == 0xD8 && image[2] == 0xFF;
}