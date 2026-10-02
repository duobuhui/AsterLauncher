using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

public sealed class HoYoDistributionProvider(HttpClient http, HoYoContentCodec codec) : IHoYoDistributionProvider
{
    private const string Api = "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/";
    public static string LauncherFor(string gameId, HoYoChannel channel) => channel switch
    {
        HoYoChannel.Official => "jGHBHlcOq1",
        HoYoChannel.Bilibili => gameId switch
        {
            BuiltInGameIds.GenshinImpact => "umfgRO5gh5", BuiltInGameIds.HonkaiStarRail => "6P5gHMNyK3",
            BuiltInGameIds.ZenlessZoneZero => "xV0f4r1GT0", _ => throw new ArgumentException("此游戏无需独立 B 服安装。")
        },
        _ => throw new InvalidOperationException("请先选择游戏服务器。")
    };
    private static string Parameters(string gameId, HoYoChannel channel) => "launcher_id=" + LauncherFor(gameId, channel)
        + "&language=zh-cn&channel=" + (channel == HoYoChannel.Bilibili ? "14&sub_channel=0" : "1&sub_channel=1");
    public static IReadOnlyDictionary<string, string> Games { get; } = new Dictionary<string, string>
    {
        [BuiltInGameIds.GenshinImpact] = "hk4e_cn", [BuiltInGameIds.HonkaiImpact3rd] = "bh3_cn",
        [BuiltInGameIds.HonkaiStarRail] = "hkrpg_cn", [BuiltInGameIds.ZenlessZoneZero] = "nap_cn",
        [BuiltInGameIds.PetitPlanet] = "hyg_cn"
    };
    public static Uri TrustedUri(string raw)
    {
        var uri = new Uri(raw, UriKind.Absolute);
        if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
            || !new[] { "mihoyo.com", "yuanshen.com", "bhsr.com", "bh3.com", "juequling.com" }
                .Any(host => uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("米哈游资源地址不属于允许的官方域名。");
        return uri;
    }
    public async Task<HoYoRelease?> GetReleaseAsync(string gameId, CancellationToken token = default)
    {
        var (release, _) = await ReadBranchAsync(gameId, HoYoChannel.Official, token);
        return release;
    }
    public async Task<HoYoRelease?> GetReleaseAsync(string gameId, HoYoChannel channel, CancellationToken token = default)
    {
        var (release, _) = await ReadBranchAsync(gameId, channel, token); return release;
    }
    private async Task<(HoYoRelease?, JsonElement)> ReadBranchAsync(string gameId, HoYoChannel channel, CancellationToken token)
    {
        if (!Games.TryGetValue(gameId, out var biz)) throw new ArgumentException("Unsupported game.");
        using var branches = await JsonAsync(Api + "getGameBranches?" + Parameters(gameId, channel), token);
        var branch = branches.RootElement.GetProperty("data").GetProperty("game_branches").EnumerateArray()
            .FirstOrDefault(x => x.GetProperty("game").GetProperty("biz").GetString() == biz);
        if (branch.ValueKind == JsonValueKind.Undefined || !branch.TryGetProperty("main", out var main)
            || main.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace(Text(main, "tag"))) return (null, default);
        var id = Text(branch.GetProperty("game"), "id");
        using var configs = await JsonAsync(Api + "getGameConfigs?" + Parameters(gameId, channel) + "&game_ids%5B%5D=" + Uri.EscapeDataString(id), token);
        var config = configs.RootElement.GetProperty("data").GetProperty("launch_configs").EnumerateArray()
            .Single(x => Text(x.GetProperty("game"), "id") == id);
        var exe = Text(config, "exe_file_name"); var dir = Text(config, "installation_dir");
        SafeGamePath.ValidateRelative(exe); SafeGamePath.ValidateRelative(dir);
        if (exe.Contains('/') || exe.Contains('\\') || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("官方启动配置无效。");
        string? pre = branch.TryGetProperty("pre_download", out var p) && p.ValueKind == JsonValueKind.Object ? Text(p, "tag") : null;
        var audio = Text(config, "audio_pkg_scan_dir");
        if (!string.IsNullOrEmpty(audio)) SafeGamePath.ValidateRelative(audio);
        var release = new HoYoRelease(gameId, id, Text(main, "tag"), exe, dir, string.IsNullOrWhiteSpace(pre) ? null : pre, string.IsNullOrEmpty(audio) ? null : audio, channel);
        return (release, branch.Clone());
    }
    public async Task<HoYoPackage> GetPackageAsync(string gameId, IReadOnlyCollection<string> audioLanguages,
        bool preload = false, CancellationToken token = default)
    {
        return await GetPackageAsync(gameId, HoYoChannel.Official, audioLanguages, preload, token);
    }
    public async Task<HoYoPackage> GetPackageAsync(string gameId, HoYoChannel channel, IReadOnlyCollection<string> audioLanguages,
        bool preload = false, CancellationToken token = default)
    {
        var (release, branch) = await ReadBranchAsync(gameId, channel, token);
        if (release is null) throw new InvalidOperationException("官方尚未提供该游戏的公开 PC 下载资源。");
        var selected = branch.GetProperty(preload ? "pre_download" : "main");
        if (selected.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(Text(selected, "tag")))
            throw new InvalidOperationException("官方尚未开放预下载。");
        var version = Text(selected, "tag");
        var url = "https://downloader-api.mihoyo.com/downloader/sophon_chunk/api/getBuild?branch=" + Uri.EscapeDataString(Text(selected, "branch"))
            + "&package_id=" + Uri.EscapeDataString(Text(selected, "package_id")) + "&password=" + Uri.EscapeDataString(Text(selected, "password"));
        using var build = await JsonAsync(url, token);
        var data = build.RootElement.GetProperty("data");
        if (Text(data, "tag") != version) throw new InvalidDataException("官方分支在检查期间发生变化，请重新检查。");
        var manifests = data.GetProperty("manifests").EnumerateArray().Where(m =>
        {
            var field = Text(m, "matching_field");
            return field is not ("zh-cn" or "en-us" or "ja-jp" or "ko-kr") || audioLanguages.Contains(field);
        }).Select(m => m.Clone()).ToArray();
        if (manifests.Length == 0 || manifests.Length > 200) throw new InvalidDataException("官方资源分类清单无效。");
        var all = new System.Collections.Concurrent.ConcurrentBag<IReadOnlyList<HoYoFile>>();
        await Parallel.ForEachAsync(manifests, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (m, ct) =>
        {
            var meta = m.GetProperty("manifest"); var download = m.GetProperty("manifest_download"); var chunks = m.GetProperty("chunk_download");
            if (Number(download, "encryption") != 0 || Number(chunks, "encryption") != 0)
                throw new InvalidDataException("暂不支持此官方资源加密格式。");
            var compressed = await LimitedAsync(Address(download, Text(meta, "id")), Number(meta, "compressed_size"), 64 * 1024 * 1024, ct);
            var decoded = await codec.DecodeAsync(compressed, Number(meta, "uncompressed_size"), (int)Number(download, "compression"), ct);
            var checksum = Text(meta, "checksum");
            if (!HoYoManifestReader.ValidMd5(checksum) || !Convert.ToHexString(MD5.HashData(decoded)).Equals(checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("官方文件清单校验失败。");
            all.Add(HoYoManifestReader.Parse(decoded, id => Address(chunks, id), (int)Number(chunks, "compression")));
        });
        var result = new Dictionary<string, HoYoFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in all.SelectMany(f => f))
        {
            // Launcher state is maintained separately and never replaced by a payload config.
            if (file.Path.Equals("config.ini", StringComparison.OrdinalIgnoreCase)
                || file.Path.Equals(release.AudioRecordPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (result.TryGetValue(file.Path, out var existing) && (existing.Size != file.Size || !existing.Md5.Equals(file.Md5, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("官方资源分类之间存在冲突路径。");
            result[file.Path] = file;
        }
        if (!result.ContainsKey(release.ExecutableName)) throw new InvalidDataException("官方完整清单缺少游戏程序。");
        var files = result.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', files.Select(f => $"{f.Path}|{f.Size}|{f.Md5.ToLowerInvariant()}")))));
        HoYoSdk? sdk = null;
        if (channel == HoYoChannel.Bilibili)
        {
            using var sdkResponse = await JsonAsync(Api + "getGameChannelSDKs?" + Parameters(gameId, channel) + "&game_ids%5B%5D=" + Uri.EscapeDataString(release.PublisherId), token);
            var entry = sdkResponse.RootElement.GetProperty("data").GetProperty("game_channel_sdks").EnumerateArray()
                .Single(x => Text(x.GetProperty("game"), "id") == release.PublisherId);
            var package = entry.GetProperty("channel_sdk_pkg");
            var md5 = Text(package, "md5"); var marker = Text(entry, "pkg_version_file_name"); SafeGamePath.ValidateRelative(marker);
            var size = Number(package, "size"); var expanded = Number(package, "decompressed_size");
            if (!HoYoManifestReader.ValidMd5(md5) || size is <= 0 or > 256 * 1024 * 1024 || expanded is <= 0 or > 512 * 1024 * 1024)
                throw new InvalidDataException("官方渠道组件信息无效。");
            sdk = new(TrustedUri(Text(package, "url")), md5, size, expanded, Text(entry, "version"), marker);
        }
        return new(release with { Version = version }, files, hash, sdk);
    }
    public static Uri Address(JsonElement address, string id)
    {
        SafeGamePath.ValidateRelative(id);
        if (id.Contains('/') || id.Contains('\\')) throw new InvalidDataException("Invalid resource identity.");
        var suffix = Text(address, "url_suffix");
        return TrustedUri(Text(address, "url_prefix").TrimEnd('/') + "/" + Uri.EscapeDataString(id) + (suffix.Length > 0 ? "?" + suffix.TrimStart('?') : ""));
    }
    private async Task<JsonDocument> JsonAsync(string url, CancellationToken token)
    {
        var bytes = await LimitedAsync(TrustedUri(url), null, 4 * 1024 * 1024, token);
        var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.GetProperty("retcode").GetInt32() != 0) { doc.Dispose(); throw new InvalidDataException("官方资源接口返回错误。"); }
        return doc;
    }
    private async Task<byte[]> LimitedAsync(Uri uri, long? expected, int limit, CancellationToken token)
    {
        if (expected is < 0 || expected > limit) throw new InvalidDataException("官方响应大小无效。");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } size && (size > limit || expected is { } e && e != size))
            throw new InvalidDataException("官方响应长度发生变化。");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[128 * 1024]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("官方响应超过限制。");
            output.Write(buffer, 0, read);
        }
        if (expected is { } length && output.Length != length) throw new InvalidDataException("官方响应不完整。");
        return output.ToArray();
    }
    private static string Text(JsonElement node, string key) => node.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
    private static long Number(JsonElement node, string key) => node.GetProperty(key).ValueKind == JsonValueKind.String
        ? long.Parse(node.GetProperty(key).GetString()!, System.Globalization.CultureInfo.InvariantCulture) : node.GetProperty(key).GetInt64();
}