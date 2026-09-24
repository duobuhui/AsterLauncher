using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AsterLauncher.Core;
using Microsoft.Extensions.Logging;

namespace AsterLauncher.Infrastructure;

public sealed class UigfArchiveService : IUigfArchiveService, IDisposable
{
    private const string UigfVersion = "v4.2";
    private const int MaximumCacheBytes = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly IReadOnlyDictionary<string, CaptureDefinition> CaptureDefinitions =
        new Dictionary<string, CaptureDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [BuiltInGameIds.GenshinImpact] = new(
                "hk4e",
                ["https://webstatic.mihoyo.com/hk4e/event/e20190909gacha", "https://gs.hoyoverse.com/genshin/event/e20190909gacha"],
                "https://public-operation-hk4e.mihoyo.com/gacha_info/api/getGachaLog",
                "https://public-operation-hk4e-sg.hoyoverse.com/gacha_info/api/getGachaLog",
                ["100", "200", "301", "302", "500"]),
            [BuiltInGameIds.HonkaiStarRail] = new(
                "hkrpg",
                ["https://webstatic.mihoyo.com/hkrpg/event/e20211215gacha", "https://gs.hoyoverse.com/hkrpg/event/e20211215gacha"],
                "https://public-operation-hkrpg.mihoyo.com/common/hkrpg_gacha_record/api/getGachaLog",
                "https://public-operation-hkrpg-sg.hoyoverse.com/common/hkrpg_gacha_record/api/getGachaLog",
                ["1", "2", "11", "12", "21", "22"]),
            [BuiltInGameIds.ZenlessZoneZero] = new(
                "nap",
                ["https://webstatic.mihoyo.com/nap/event/e20230424gacha", "https://gs.hoyoverse.com/nap/event/e20230424gacha"],
                "https://public-operation-nap.mihoyo.com/common/gacha_record/api/getGachaLog",
                "https://public-operation-nap-sg.hoyoverse.com/common/gacha_record/api/getGachaLog",
                ["1", "2", "3", "5", "102", "103"])
        };

    private readonly ILogger<UigfArchiveService> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public UigfArchiveService(ILogger<UigfArchiveService> logger)
        : this(logger, new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, true)
    {
    }

    internal UigfArchiveService(ILogger<UigfArchiveService> logger, HttpClient httpClient)
        : this(logger, httpClient, false)
    {
    }

    private UigfArchiveService(ILogger<UigfArchiveService> logger, HttpClient httpClient, bool ownsClient)
    {
        _logger = logger;
        _httpClient = httpClient;
        _ownsClient = ownsClient;
        var dataRoot = Environment.GetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = Path.Combine(AppContext.BaseDirectory, "Data");
        }

        ArchivePath = Path.Combine(Path.GetFullPath(dataRoot), "gacha", "uigf-v4.2.json");
    }

    public string ArchivePath { get; }

    public async Task<UigfArchiveSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ArchivePath))
        {
            return new UigfArchiveSummary(UigfVersion, 0, 0, 0, null);
        }

        try
        {
            var root = await ReadRootAsync(ArchivePath, cancellationToken).ConfigureAwait(false);
            var timestamp = root["info"]?["export_timestamp"]?.GetValue<long?>();
            return new UigfArchiveSummary(
                root["info"]?["version"]?.GetValue<string>() ?? UigfVersion,
                CountRecords(root, "hk4e"),
                CountRecords(root, "hkrpg"),
                CountRecords(root, "nap"),
                timestamp is > 0 ? DateTimeOffset.FromUnixTimeSeconds(timestamp.Value) : File.GetLastWriteTimeUtc(ArchivePath));
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning("Could not summarize UIGF archive; error type {ErrorType}", exception.GetType().Name);
            return new UigfArchiveSummary("无法读取", 0, 0, 0, null);
        }
    }

    public async Task<GachaImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var incoming = await ReadRootAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            ValidateUigf(incoming);
            var before = await GetSummaryAsync(cancellationToken).ConfigureAwait(false);
            var merged = File.Exists(ArchivePath)
                ? Merge(await ReadRootAsync(ArchivePath, cancellationToken).ConfigureAwait(false), incoming)
                : Normalize(incoming);
            await SaveRootAsync(merged, ArchivePath, cancellationToken).ConfigureAwait(false);
            var after = await GetSummaryAsync(cancellationToken).ConfigureAwait(false);
            var imported = Math.Max(0, after.TotalCount - before.TotalCount);
            return new GachaImportResult(true, imported, $"UIGF {after.Version} 已合并，共 {after.TotalCount} 条记录。新增 {imported} 条。");
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

    public async Task<GachaImportResult> ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ArchivePath))
        {
            return new GachaImportResult(false, 0, "还没有可导出的本地 UIGF 档案。");
        }

        try
        {
            var root = await ReadRootAsync(ArchivePath, cancellationToken).ConfigureAwait(false);
            UpdateInfo(root);
            await SaveRootAsync(root, destinationPath, cancellationToken).ConfigureAwait(false);
            var count = CountRecords(root, "hk4e") + CountRecords(root, "hkrpg") + CountRecords(root, "nap");
            return new GachaImportResult(true, count, $"已导出 UIGF {UigfVersion}，共 {count} 条记录。");
        }
        catch (OperationCanceledException)
        {
            return new GachaImportResult(false, 0, "用户取消了导出。");
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            return new GachaImportResult(false, 0, $"导出失败：{exception.Message}");
        }
    }

    public async Task<GachaImportResult> CaptureFromGameAsync(
        string gameId,
        string executablePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!CaptureDefinitions.TryGetValue(gameId, out var definition))
        {
            return new GachaImportResult(false, 0, "当前游戏不使用米哈游 UIGF 抽卡格式。");
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return new GachaImportResult(false, 0, "请先在游戏设置中指定有效的游戏 EXE。");
        }

        try
        {
            progress?.Report("正在定位游戏 Web 缓存…");
            var cachePath = FindNewestCache(executablePath);
            if (cachePath is null)
            {
                return new GachaImportResult(false, 0, "没有找到 data_2 Web 缓存。请先在游戏内打开一次抽卡记录页面后重试。");
            }

            var capturedUrl = await ExtractHistoryUrlAsync(cachePath, definition, cancellationToken).ConfigureAwait(false);
            if (capturedUrl is null)
            {
                return new GachaImportResult(false, 0, "缓存中没有找到受支持的抽卡记录链接。请在游戏内重新打开抽卡记录页面。");
            }

            progress?.Report("已找到本地链接，正在通过官方接口同步…");
            var records = await FetchAllAsync(capturedUrl, definition, progress, cancellationToken).ConfigureAwait(false);
            if (records.Count == 0)
            {
                return new GachaImportResult(true, 0, "官方接口返回成功，但没有新的抽卡记录。");
            }

            var incoming = CreateArchive(definition.UigfKey, records);
            var root = File.Exists(ArchivePath)
                ? Merge(await ReadRootAsync(ArchivePath, cancellationToken).ConfigureAwait(false), incoming)
                : incoming;
            var beforeCount = File.Exists(ArchivePath)
                ? (await GetSummaryAsync(cancellationToken).ConfigureAwait(false)).TotalCount
                : 0;
            await SaveRootAsync(root, ArchivePath, cancellationToken).ConfigureAwait(false);
            var afterCount = (await GetSummaryAsync(cancellationToken).ConfigureAwait(false)).TotalCount;
            var imported = Math.Max(0, afterCount - beforeCount);
            return new GachaImportResult(true, imported, $"同步完成：读取 {records.Count} 条，新增 {imported} 条。授权链接未写入日志或配置。");
        }
        catch (OperationCanceledException)
        {
            return new GachaImportResult(false, 0, "用户取消了同步。");
        }
        catch (UnauthorizedAccessException)
        {
            return new GachaImportResult(false, 0, "没有权限读取游戏 Web 缓存。");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning("Gacha HTTP request failed; status {StatusCode}", exception.StatusCode);
            return new GachaImportResult(false, 0, "访问官方抽卡接口失败。链接中的授权参数不会显示；请稍后重试或重新打开游戏内记录页。");
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or InvalidOperationException or UriFormatException)
        {
            _logger.LogWarning("Gacha capture failed; error type {ErrorType}", exception.GetType().Name);
            return new GachaImportResult(false, 0, $"同步失败：{exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<List<JsonObject>> FetchAllAsync(Uri capturedUrl, CaptureDefinition definition, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var result = new List<JsonObject>();
        var isGlobal = capturedUrl.Host.Contains("hoyoverse.com", StringComparison.OrdinalIgnoreCase);
        foreach (var gachaType in definition.GachaTypes)
        {
            var endId = "0";
            for (var page = 1; page <= 200; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var endpoint = definition.GetEndpoint(isGlobal, gachaType);
                var requestUri = BuildApiUri(endpoint, capturedUrl.Query, gachaType, endId);
                using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                var retcode = root.TryGetProperty("retcode", out var code) ? code.GetInt32() : -1;
                if (retcode != 0)
                {
                    throw new InvalidDataException("官方接口拒绝了当前链接，请在游戏内重新打开记录页面。");
                }

                if (!root.TryGetProperty("data", out var data)
                    || !data.TryGetProperty("list", out var list)
                    || list.ValueKind != JsonValueKind.Array
                    || list.GetArrayLength() == 0)
                {
                    break;
                }

                string? nextEndId = null;
                foreach (var item in list.EnumerateArray())
                {
                    var node = JsonNode.Parse(item.GetRawText()) as JsonObject;
                    if (node is null)
                    {
                        continue;
                    }

                    result.Add(node);
                    nextEndId = node["id"]?.GetValue<string>();
                }

                progress?.Report($"正在同步 {gachaType}：已读取 {result.Count} 条…");
                if (string.IsNullOrWhiteSpace(nextEndId) || nextEndId == endId)
                {
                    break;
                }

                endId = nextEndId;
            }
        }

        return result;
    }

    private static Uri BuildApiUri(string endpoint, string capturedQuery, string gachaType, string endId)
    {
        var query = capturedQuery.TrimStart('?');
        var separator = string.IsNullOrWhiteSpace(query) ? string.Empty : "&";
        return new Uri($"{endpoint}?{query}{separator}gacha_type={Uri.EscapeDataString(gachaType)}&page=1&size=20&end_id={Uri.EscapeDataString(endId)}");
    }

    private static string? FindNewestCache(string executablePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        if (directory is null)
        {
            return null;
        }

        var webCaches = Path.Combine(directory, executableName + "_Data", "webCaches");
        if (!Directory.Exists(webCaches))
        {
            return null;
        }

        return Directory.EnumerateFiles(webCaches, "data_2", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static async Task<Uri?> ExtractHistoryUrlAsync(string cachePath, CaptureDefinition definition, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, true);
        var length = (int)Math.Min(stream.Length, MaximumCacheBytes);
        stream.Seek(-length, SeekOrigin.End);
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);

        var text = Encoding.UTF8.GetString(bytes);
        var bestIndex = -1;
        foreach (var prefix in definition.AcceptedPrefixes)
        {
            bestIndex = Math.Max(bestIndex, text.LastIndexOf(prefix, StringComparison.OrdinalIgnoreCase));
        }

        if (bestIndex < 0)
        {
            return null;
        }

        var end = bestIndex;
        while (end < text.Length && text[end] is not '\0' and not '"' and not '\'' and not '\r' and not '\n' and not ' ')
        {
            end++;
        }

        var candidate = WebUtility.HtmlDecode(text[bestIndex..end]).Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static JsonObject CreateArchive(string key, IReadOnlyList<JsonObject> records)
    {
        var root = NewRoot();
        var grouped = records
            .Where(item => !string.IsNullOrWhiteSpace(item["uid"]?.GetValue<string>()))
            .GroupBy(item => item["uid"]!.GetValue<string>());
        var users = (JsonArray)root[key]!;
        foreach (var group in grouped)
        {
            var uid = group.Key;
            var timezone = uid.Length > 0 && uid[0] == '6' ? -5 : uid.Length > 0 && uid[0] == '7' ? 1 : 8;
            users.Add(new JsonObject
            {
                ["uid"] = uid,
                ["timezone"] = timezone,
                ["lang"] = "zh-cn",
                ["list"] = new JsonArray(group.Select(item => item.DeepClone()).ToArray())
            });
        }

        return root;
    }

    private static JsonObject Merge(JsonObject target, JsonObject incoming)
    {
        target = Normalize(target);
        incoming = Normalize(incoming);
        foreach (var key in new[] { "hk4e", "hkrpg", "nap" })
        {
            var targetUsers = (JsonArray)target[key]!;
            foreach (var incomingUserNode in (JsonArray)incoming[key]!)
            {
                if (incomingUserNode is not JsonObject incomingUser)
                {
                    continue;
                }

                var uid = incomingUser["uid"]?.GetValue<string>() ?? string.Empty;
                var targetUser = targetUsers.OfType<JsonObject>()
                    .FirstOrDefault(user => string.Equals(user["uid"]?.GetValue<string>(), uid, StringComparison.Ordinal));
                if (targetUser is null)
                {
                    targetUsers.Add(incomingUser.DeepClone());
                    continue;
                }

                var targetList = targetUser["list"] as JsonArray ?? [];
                targetUser["list"] = targetList;
                var ids = targetList.OfType<JsonObject>()
                    .Select(item => item["id"]?.GetValue<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var item in (incomingUser["list"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var id = item["id"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(id) || ids.Add(id))
                    {
                        targetList.Add(item.DeepClone());
                    }
                }
            }
        }

        UpdateInfo(target);
        return target;
    }

    private static JsonObject Normalize(JsonObject root)
    {
        foreach (var key in new[] { "hk4e", "hkrpg", "nap" })
        {
            root[key] ??= new JsonArray();
        }

        root["info"] ??= new JsonObject();
        UpdateInfo(root);
        return root;
    }

    private static JsonObject NewRoot() => Normalize(new JsonObject());

    private static void UpdateInfo(JsonObject root)
    {
        var info = root["info"] as JsonObject ?? new JsonObject();
        root["info"] = info;
        info["export_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        info["export_app"] = "AsterLauncher";
        info["export_app_version"] = "0.4.0";
        info["version"] = UigfVersion;
    }

    private static void ValidateUigf(JsonObject root)
    {
        var version = root["info"]?["version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(version) || !version.StartsWith("v4.", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("只支持 UIGF v4.x JSON 文件。请先使用兼容工具转换旧格式。");
        }

        if (root["hk4e"] is not JsonArray && root["hkrpg"] is not JsonArray && root["nap"] is not JsonArray)
        {
            throw new InvalidDataException("文件中没有 hk4e、hkrpg 或 nap 记录集合。");
        }
    }

    private static int CountRecords(JsonObject root, string key) =>
        (root[key] as JsonArray)?.OfType<JsonObject>().Sum(user => (user["list"] as JsonArray)?.Count ?? 0) ?? 0;

    private static async Task<JsonObject> ReadRootAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject;
        return root ?? throw new InvalidDataException("JSON 根节点不是对象。");
    }

    private static async Task SaveRootAsync(JsonObject root, string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
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

    private sealed record CaptureDefinition(
        string UigfKey,
        IReadOnlyList<string> AcceptedPrefixes,
        string ChinaEndpoint,
        string GlobalEndpoint,
        IReadOnlyList<string> GachaTypes)
    {
        public string GetEndpoint(bool global, string gachaType)
        {
            var endpoint = global ? GlobalEndpoint : ChinaEndpoint;
            return UigfKey == "hkrpg" && gachaType is "21" or "22"
                ? endpoint.Replace("getGachaLog", "getLdGachaLog", StringComparison.Ordinal)
                : endpoint;
        }
    }
}
