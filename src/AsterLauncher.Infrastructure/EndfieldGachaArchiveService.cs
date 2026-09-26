using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AsterLauncher.Core;
using Microsoft.Extensions.Logging;

namespace AsterLauncher.Infrastructure;

public sealed class EndfieldGachaArchiveService : IEndfieldGachaArchiveService, IDisposable
{
    private const int MaximumCacheBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex HistoryUrlPattern = new(
        """https://(?:ef-webview\.hypergryph\.com|ef-webview\.gryphline\.com)/api/record/(?:char|weapon)\?[^\s"'<>{}\x00]+""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "ef-webview.hypergryph.com",
        "ef-webview.gryphline.com"
    };

    private readonly ILogger<EndfieldGachaArchiveService> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Func<string?> _cachePathProvider;

    public EndfieldGachaArchiveService(ILogger<EndfieldGachaArchiveService> logger)
        : this(
            logger,
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            true,
            GetDefaultCachePath)
    {
    }

    internal EndfieldGachaArchiveService(
        ILogger<EndfieldGachaArchiveService> logger,
        HttpClient httpClient,
        Func<string?> cachePathProvider)
        : this(logger, httpClient, false, cachePathProvider)
    {
    }

    private EndfieldGachaArchiveService(
        ILogger<EndfieldGachaArchiveService> logger,
        HttpClient httpClient,
        bool ownsClient,
        Func<string?> cachePathProvider)
    {
        _logger = logger;
        _httpClient = httpClient;
        _ownsClient = ownsClient;
        _cachePathProvider = cachePathProvider;
        ArchivePath = Path.Combine(LauncherDataPaths.ResolveDataDirectory(), "gacha", "endfield-records.json");
    }

    public string ArchivePath { get; }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    public async Task<EndfieldGachaSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ArchivePath))
        {
            return new EndfieldGachaSummary(0, 0, null);
        }

        try
        {
            var root = await ReadObjectAsync(ArchivePath, cancellationToken).ConfigureAwait(false);
            return new EndfieldGachaSummary(
                (root["characters"] as JsonArray)?.Count ?? 0,
                (root["weapons"] as JsonArray)?.Count ?? 0,
                File.GetLastWriteTimeUtc(ArchivePath));
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            _logger.LogWarning("Could not summarize Endfield gacha archive; error type {ErrorType}", exception.GetType().Name);
            return new EndfieldGachaSummary(0, 0, null);
        }
    }

    public async Task<EndfieldGachaAnalysis> GetAnalysisAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ArchivePath))
        {
            return EndfieldGachaAnalysis.Empty;
        }

        try
        {
            var root = await ReadObjectAsync(ArchivePath, cancellationToken).ConfigureAwait(false);
            return EndfieldGachaAnalyzer.Analyze(root);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            _logger.LogWarning("Could not analyze Endfield gacha archive; error type {ErrorType}", exception.GetType().Name);
            return EndfieldGachaAnalysis.Empty;
        }
    }

    public async Task<GachaImportResult> CaptureFromGameAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cachePath = _cachePathProvider();
        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
        {
            return new GachaImportResult(false, 0, "没有找到终末地记录缓存。请先在游戏内打开一次抽卡记录页面。");
        }

        try
        {
            progress?.Report("正在读取终末地记录缓存…");
            var captureUrls = await ExtractCaptureUrlsAsync(cachePath, cancellationToken).ConfigureAwait(false);
            if (captureUrls.Count == 0)
            {
                return new GachaImportResult(false, 0, "缓存中没有可用的官方记录链接。请在游戏内重新打开抽卡记录页面。");
            }

            progress?.Report("已找到本地链接，正在通过官方接口同步…");
            var (incoming, failedGroups) = await FetchAllAsync(captureUrls, progress, cancellationToken).ConfigureAwait(false);
            var fetched = Count(incoming);
            if (fetched == 0)
            {
                return new GachaImportResult(true, 0, failedGroups == 0
                    ? "官方接口返回成功，但没有可保存的记录。"
                    : $"部分记录分组同步失败（{failedGroups} 个）；其他分组没有可保存的记录。请在游戏内重新打开记录页面后重试。");
            }

            var current = File.Exists(ArchivePath)
                ? await ReadObjectAsync(ArchivePath, cancellationToken).ConfigureAwait(false)
                : NewArchive();
            var before = Count(current);
            MergeArray(current, incoming, "characters");
            MergeArray(current, incoming, "weapons");
            UpdateInfo(current);
            await SaveAsync(current, ArchivePath, cancellationToken).ConfigureAwait(false);
            var after = Count(current);
            var imported = Math.Max(0, after - before);
            var partial = failedGroups == 0 ? string.Empty : $"另有 {failedGroups} 个记录分组失败，请在游戏内重新打开记录页面后重试。";
            return new GachaImportResult(true, imported, $"同步完成：读取 {fetched} 条，新增 {imported} 条。{partial}授权链接未写入日志或配置。");
        }
        catch (OperationCanceledException)
        {
            return new GachaImportResult(false, 0, "用户取消了同步。");
        }
        catch (UnauthorizedAccessException)
        {
            return new GachaImportResult(false, 0, "没有权限读取终末地记录缓存。");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning("Endfield gacha HTTP request failed; status {StatusCode}, cause {CauseType}",
                exception.StatusCode, exception.InnerException?.GetType().Name);
            return new GachaImportResult(false, 0, exception.InnerException is AuthenticationException
                ? "连接官方记录接口时 TLS 握手失败。请检查系统网络、代理或安全软件后重试。"
                : "访问终末地官方记录接口失败。请稍后重试或重新打开游戏内记录页。");
        }
        catch (InvalidDataException exception)
        {
            // Every InvalidDataException in this capture path uses our own token-free message.
            _logger.LogWarning("Endfield gacha capture failed; reason {SafeReason}", exception.Message);
            return new GachaImportResult(false, 0, $"同步失败：{exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or UriFormatException)
        {
            _logger.LogWarning("Endfield gacha capture failed; error type {ErrorType}", exception.GetType().Name);
            return new GachaImportResult(false, 0, $"同步失败：{exception.Message}");
        }
    }

    public async Task<GachaImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var incoming = await ReadImportAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            var current = File.Exists(ArchivePath)
                ? await ReadObjectAsync(ArchivePath, cancellationToken).ConfigureAwait(false)
                : NewArchive();
            var before = Count(current);
            MergeArray(current, incoming, "characters");
            MergeArray(current, incoming, "weapons");
            UpdateInfo(current);
            await SaveAsync(current, ArchivePath, cancellationToken).ConfigureAwait(false);
            var after = Count(current);
            return new GachaImportResult(true, Math.Max(0, after - before), $"已合并终末地记录，共 {after} 条。新增 {Math.Max(0, after - before)} 条。");
        }
        catch (OperationCanceledException)
        {
            return new GachaImportResult(false, 0, "用户取消了导入。");
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or InvalidOperationException)
        {
            return new GachaImportResult(false, 0, $"导入失败：{exception.Message}");
        }
    }

    public async Task<GachaImportResult> ExportJsonAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ArchivePath))
        {
            return new GachaImportResult(false, 0, "还没有终末地记录。");
        }

        try
        {
            var root = await ReadObjectAsync(ArchivePath, cancellationToken).ConfigureAwait(false);
            UpdateInfo(root);
            await SaveAsync(root, destinationPath, cancellationToken).ConfigureAwait(false);
            return new GachaImportResult(true, Count(root), $"已导出终末地 JSON，共 {Count(root)} 条记录。");
        }
        catch (OperationCanceledException)
        {
            return new GachaImportResult(false, 0, "用户取消了导出。");
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or InvalidOperationException)
        {
            return new GachaImportResult(false, 0, $"导出失败：{exception.Message}");
        }
    }

    public async Task<GachaImportResult> ExportCsvAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ArchivePath))
        {
            return new GachaImportResult(false, 0, "还没有终末地记录。");
        }

        try
        {
            var root = await ReadObjectAsync(ArchivePath, cancellationToken).ConfigureAwait(false);
            StripSensitiveFields(root);
            var records = EnumerateRecords(root).ToArray();
            var keys = records.SelectMany(item => item.Record.Select(property => property.Key))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
            var builder = new StringBuilder();
            builder.Append("category");
            foreach (var key in keys)
            {
                builder.Append(',').Append(EscapeCsv(key));
            }

            builder.AppendLine();
            foreach (var item in records)
            {
                builder.Append(EscapeCsv(item.Category));
                foreach (var key in keys)
                {
                    builder.Append(',').Append(EscapeCsv(ToCell(item.Record[key])));
                }

                builder.AppendLine();
            }

            var fullPath = Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, builder.ToString(), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
            return new GachaImportResult(true, records.Length, $"已导出终末地 CSV，共 {records.Length} 条记录。");
        }
        catch (OperationCanceledException)
        {
            return new GachaImportResult(false, 0, "用户取消了导出。");
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or InvalidOperationException)
        {
            return new GachaImportResult(false, 0, $"导出失败：{exception.Message}");
        }
    }

    private async Task<(JsonObject Archive, int FailedGroups)> FetchAllAsync(
        IReadOnlyList<Uri> captureUrls,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = NewArchive();
        var failedGroups = 0;
        var successfulGroups = 0;
        InvalidDataException? lastInvalidResponse = null;
        foreach (var captureUrl in captureUrls)
        {
            var target = captureUrl.AbsolutePath.EndsWith("/char", StringComparison.OrdinalIgnoreCase)
                ? (JsonArray)result["characters"]!
                : (JsonArray)result["weapons"]!;
            var groupRecords = new JsonArray();
            string? sequenceId = null;
            try
            {
              for (var page = 1; page <= 200; page++)
              {
                cancellationToken.ThrowIfCancellationRequested();
                var requestUri = BuildPagedUri(captureUrl, sequenceId);
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 AsterLauncher/0.5");
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!TryGetRecordList(document.RootElement, out var data, out var list))
                {
                    throw new InvalidDataException(GetRecordResponseError(document.RootElement));
                }

                if (list.GetArrayLength() == 0)
                {
                    break;
                }

                string? nextSequenceId = null;
                foreach (var item in list.EnumerateArray())
                {
                    if (JsonNode.Parse(item.GetRawText()) is JsonObject record)
                    {
                        groupRecords.Add(record);
                    }

                    if (item.ValueKind == JsonValueKind.Object
                        && (item.TryGetProperty("seqId", out var seqId) || item.TryGetProperty("seq_id", out seqId)))
                    {
                        nextSequenceId = GetScalarText(seqId);
                    }
                }

                progress?.Report($"正在同步终末地记录：已读取 {Count(result) + groupRecords.Count} 条…");
                var hasMore = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("hasMore", out var hasMoreNode)
                    ? hasMoreNode.ValueKind == JsonValueKind.True
                    : list.GetArrayLength() == 5;
                if (!hasMore || string.IsNullOrWhiteSpace(nextSequenceId) || nextSequenceId == sequenceId)
                {
                    break;
                }

                sequenceId = nextSequenceId;
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
              }
              foreach (var record in groupRecords.ToArray())
              {
                  target.Add(record?.DeepClone());
              }
              successfulGroups++;
            }
            catch (InvalidDataException exception)
            {
                failedGroups++;
                lastInvalidResponse = exception;
                _logger.LogWarning("Endfield record group rejected; endpoint {Endpoint}, reason {SafeReason}",
                    captureUrl.AbsolutePath.EndsWith("/char", StringComparison.OrdinalIgnoreCase) ? "char" : "weapon",
                    exception.Message);
            }
        }

        if (successfulGroups == 0)
        {
            throw lastInvalidResponse ?? new InvalidDataException("官方接口没有返回可用的记录分组。请在游戏内重新打开记录页面后重试。");
        }

        return (result, failedGroups);
    }

    private static bool TryGetRecordList(JsonElement root, out JsonElement data, out JsonElement list)
    {
        data = default;
        list = default;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out data)) return false;
        if (data.ValueKind == JsonValueKind.Array)
        {
            list = data;
            return true;
        }
        if (data.ValueKind != JsonValueKind.Object) return false;
        if (data.TryGetProperty("list", out list) && list.ValueKind == JsonValueKind.Array) return true;
        return data.TryGetProperty("records", out list) && list.ValueKind == JsonValueKind.Array;
    }

    private static async Task<IReadOnlyList<Uri>> ExtractCaptureUrlsAsync(string cachePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, true);
        var length = (int)Math.Min(stream.Length, MaximumCacheBytes);
        stream.Seek(-length, SeekOrigin.End);
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes);
        var matches = HistoryUrlPattern.Matches(text);
        // A single webview session uses the same token across character and weapon pools.
        // Cache order can contain a refreshed token for only one pool; use that newest
        // token for every pool on the same official host and server.
        var latestTokenByServer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var candidate = WebUtility.HtmlDecode(matches[index].Value)
                .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || !IsAllowedCaptureUri(uri)) continue;
            var query = ParseQuery(uri.Query);
            if (!query.TryGetValue("token", out var token) || string.IsNullOrWhiteSpace(token)
                || !query.TryGetValue("server_id", out var serverId) || string.IsNullOrWhiteSpace(serverId)) continue;
            latestTokenByServer.TryAdd($"{uri.Host}|{serverId}", token);
        }

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Uri>();
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var candidate = WebUtility.HtmlDecode(matches[index].Value)
                .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || !IsAllowedCaptureUri(uri))
            {
                continue;
            }

            var query = ParseQuery(uri.Query);
            if (!query.ContainsKey("token")
                || !query.TryGetValue("server_id", out var serverId)
                || !latestTokenByServer.TryGetValue($"{uri.Host}|{serverId}", out var latestToken))
            {
                continue;
            }

            query.Remove("seq_id");
            query["token"] = latestToken;
            var normalized = BuildUri(uri, query);
            var identity = string.Join('|',
                normalized.Host,
                normalized.AbsolutePath,
                query.GetValueOrDefault("server_id", string.Empty),
                query.GetValueOrDefault("pool_type", string.Empty),
                query.GetValueOrDefault("pool_id", string.Empty));
            if (unique.Add(identity))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static bool IsAllowedCaptureUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && AllowedHosts.Contains(uri.Host)
        && (uri.AbsolutePath.Equals("/api/record/char", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Equals("/api/record/weapon", StringComparison.OrdinalIgnoreCase));

    private static Uri BuildPagedUri(Uri source, string? sequenceId)
    {
        if (string.IsNullOrWhiteSpace(sequenceId))
        {
            // The official first page omits seq_id. Explicit seq_id=0 is rejected.
            return source;
        }

        var query = ParseQuery(source.Query);
        query["seq_id"] = sequenceId;

        return BuildUri(source, query);
    }

    private static string GetRecordResponseError(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("code", out var code))
        {
            // Never include server text: it can echo a URL containing the user's token.
            var value = code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var numeric)
                ? numeric.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : code.ValueKind == JsonValueKind.String
                    && int.TryParse(code.GetString(), out numeric)
                    ? numeric.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null;
            if (value is not null && value != "0")
            {
                return $"官方接口拒绝了记录请求（错误码 {value}）。请在游戏内重新打开抽卡记录页后重试。";
            }
            if (value == "0")
            {
                return "官方接口返回成功码，但数据中没有可识别的记录列表。请在游戏内重新打开抽卡记录页后重试；若仍失败，请反馈此提示，不要发送授权链接或 Token。";
            }
        }

        return "官方接口没有返回记录列表。请在游戏内重新打开抽卡记录页后重试。";
    }

    private static Uri BuildUri(Uri source, IReadOnlyDictionary<string, string> query)
    {
        var builder = new UriBuilder(source)
        {
            Query = string.Join('&', query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        };
        return builder.Uri;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            var name = separator < 0 ? segment : segment[..separator];
            var value = separator < 0 ? string.Empty : segment[(separator + 1)..];
            result[Uri.UnescapeDataString(name)] = Uri.UnescapeDataString(value);
        }

        return result;
    }

    private static string? GetScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null
    };

    private static string? GetDefaultCachePath()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localData)
            ? null
            : Path.Combine(localData, "PlatformProcess", "Cache", "data_1");
    }

    private static async Task<JsonObject> ReadImportAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (node is JsonObject root)
        {
            var characters = root["characters"] as JsonArray;
            var weapons = root["weapons"] as JsonArray;
            if (characters is null && weapons is null)
            {
                throw new InvalidDataException("文件中没有 characters 或 weapons 记录集合。");
            }

            return new JsonObject
            {
                ["characters"] = characters?.DeepClone() ?? new JsonArray(),
                ["weapons"] = weapons?.DeepClone() ?? new JsonArray()
            };
        }

        if (node is JsonArray array)
        {
            var key = Path.GetFileName(path).Contains("weapon", StringComparison.OrdinalIgnoreCase) ? "weapons"
                : Path.GetFileName(path).Contains("char", StringComparison.OrdinalIgnoreCase) ? "characters"
                : throw new InvalidDataException("数组文件名需要包含 char 或 weapon。");
            return new JsonObject
            {
                ["characters"] = key == "characters" ? array.DeepClone() : new JsonArray(),
                ["weapons"] = key == "weapons" ? array.DeepClone() : new JsonArray()
            };
        }

        throw new InvalidDataException("JSON 根节点必须是对象或数组。");
    }

    private static async Task<JsonObject> ReadObjectAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidDataException("JSON 根节点不是对象。");
    }

    private static JsonObject NewArchive() => new()
    {
        ["info"] = new JsonObject(),
        ["characters"] = new JsonArray(),
        ["weapons"] = new JsonArray()
    };

    private static void MergeArray(JsonObject target, JsonObject incoming, string key)
    {
        var targetArray = target[key] as JsonArray ?? [];
        target[key] = targetArray;
        StripSensitiveFields(targetArray);
        var identities = targetArray.OfType<JsonObject>().Select(GetIdentity).ToHashSet(StringComparer.Ordinal);
        foreach (var record in (incoming[key] as JsonArray ?? []).OfType<JsonObject>())
        {
            var safeRecord = (JsonObject)record.DeepClone();
            StripSensitiveFields(safeRecord);
            if (identities.Add(GetIdentity(safeRecord)))
            {
                targetArray.Add(safeRecord);
            }
        }
    }

    private static void StripSensitiveFields(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(property => property.Key).ToArray())
            {
                var value = obj[key];
                var normalizedKey = key.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
                var sensitiveKey = normalizedKey.EndsWith("token", StringComparison.Ordinal)
                    || normalizedKey.EndsWith("url", StringComparison.Ordinal)
                    || normalizedKey is "authkey" or "authorization";
                var containsRecordUrl = value is JsonValue scalar
                    && scalar.TryGetValue<string>(out var text)
                    && HistoryUrlPattern.IsMatch(text);
                if (sensitiveKey || containsRecordUrl)
                {
                    obj.Remove(key);
                }
                else
                {
                    StripSensitiveFields(value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var child = array[index];
                if (child is JsonValue scalar
                    && scalar.TryGetValue<string>(out var text)
                    && HistoryUrlPattern.IsMatch(text))
                {
                    array[index] = null;
                }
                else
                {
                    StripSensitiveFields(child);
                }
            }
        }
    }

    private static string GetIdentity(JsonObject record)
    {
        foreach (var key in new[] { "seqId", "seq_id", "id" })
        {
            if (record[key] is JsonValue value)
            {
                return $"{key}:{value.ToJsonString()}";
            }
        }

        return record.ToJsonString();
    }

    private static IEnumerable<(string Category, JsonObject Record)> EnumerateRecords(JsonObject root)
    {
        foreach (var record in (root["characters"] as JsonArray ?? []).OfType<JsonObject>())
        {
            yield return ("character", record);
        }

        foreach (var record in (root["weapons"] as JsonArray ?? []).OfType<JsonObject>())
        {
            yield return ("weapon", record);
        }
    }

    private static int Count(JsonObject root) =>
        ((root["characters"] as JsonArray)?.Count ?? 0) + ((root["weapons"] as JsonArray)?.Count ?? 0);

    private static void UpdateInfo(JsonObject root)
    {
        var info = root["info"] as JsonObject ?? new JsonObject();
        root["info"] = info;
        info["format"] = "AsterLauncher.EndfieldGacha.v1";
        info["export_app"] = "AsterLauncher";
        info["export_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static string ToCell(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString()
    };

    private static string EscapeCsv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static async Task SaveAsync(JsonObject root, string destinationPath, CancellationToken cancellationToken)
    {
        StripSensitiveFields(root);
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("目标路径没有父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await JsonSerializer.SerializeAsync(stream, root, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
