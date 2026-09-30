using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

/// <summary>Reads public publisher metadata and the encrypted complete-file manifest. Signed URLs stay in memory.</summary>
public sealed class EndfieldDistributionProvider : IEndfieldDistributionProvider
{
    private const string AppCode = "6LL0KJuqHBVz33WK";
    private static readonly Uri Endpoint = new("https://launcher.hypergryph.com/api/proxy/batch_proxy");
    private readonly HttpClient _http;

    public EndfieldDistributionProvider(HttpClient http) => _http = http;

    public async Task<string> GetLatestVersionAsync(
        EndfieldChannel channel, string? installedVersion, CancellationToken cancellationToken = default)
    {
        if (channel is not (EndfieldChannel.Official or EndfieldChannel.Bilibili))
            throw new ArgumentOutOfRangeException(nameof(channel));
        using var response = await GetLatestAsync(channel, installedVersion ?? "", cancellationToken);
        return RequiredString(FindResponse(response.RootElement), "version");
    }
    public async Task<EndfieldPackage> GetPackageAsync(
        EndfieldChannel channel, string? installedVersion, CancellationToken cancellationToken = default)
    {
        if (channel is not (EndfieldChannel.Official or EndfieldChannel.Bilibili))
            throw new ArgumentOutOfRangeException(nameof(channel));

        // A versioned check can intentionally omit pkg, so the complete manifest is always fetched separately.
        using var full = await GetLatestAsync(channel, "", cancellationToken);
        var fullResponse = FindResponse(full.RootElement);
        var version = RequiredString(fullResponse, "version");
        var pkg = RequiredObject(fullResponse, "pkg");
        var baseUri = TrustedUri(RequiredString(pkg, "file_path"));
        var manifestMd5 = RequiredMd5(pkg, "game_files_md5");
        var manifestUri = EndfieldDownloadService.FileUri(baseUri, "game_files");
        var encrypted = await _http.GetByteArrayAsync(manifestUri, cancellationToken);
        var files = EndfieldManifestDecoder.Decode(encrypted, manifestMd5);

        EndfieldPatch? patch = ParsePatch(fullResponse, "patch");
        EndfieldPatch? preload = ParsePatch(fullResponse, "pre_patch");
        if (!string.IsNullOrWhiteSpace(installedVersion))
        {
            using var update = await GetLatestAsync(channel, installedVersion, cancellationToken);
            var updateResponse = FindResponse(update.RootElement);
            patch = ParsePatch(updateResponse, "patch");
            preload = ParsePatch(updateResponse, "pre_patch");
        }

        return new EndfieldPackage(channel, version, baseUri, manifestMd5, files, patch, preload,
            pkg.TryGetProperty("packs", out var packs) && packs.ValueKind == JsonValueKind.Array
                ? packs.EnumerateArray().Select(ParsePack).ToArray() : []);
    }

    private async Task<JsonDocument> GetLatestAsync(
        EndfieldChannel channel, string version, CancellationToken cancellationToken)
    {
        var number = channel == EndfieldChannel.Official ? "1" : "2";
        var body = new
        {
            proxy_reqs = new[]
            {
                new
                {
                    kind = "get_latest_game",
                    get_latest_game_req = new
                    {
                        appcode = AppCode,
                        channel = number,
                        sub_channel = number,
                        version,
                        launcher_appcode = ""
                    }
                }
            }
        };
        using var response = await _http.PostAsJsonAsync(Endpoint, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    internal static JsonElement FindResponse(JsonElement root)
    {
        if (!root.TryGetProperty("proxy_rsps", out var replies) || replies.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Publisher response has no proxy_rsps.");
        foreach (var reply in replies.EnumerateArray())
        {
            if (reply.TryGetProperty("kind", out var kind)
                && kind.GetString() == "get_latest_game"
                && reply.TryGetProperty("get_latest_game_rsp", out var payload)
                && payload.ValueKind == JsonValueKind.Object)
                return payload;
        }
        throw new InvalidDataException("Publisher response has no get_latest_game_rsp.");
    }

    private static EndfieldPatch? ParsePatch(JsonElement response, string name)
    {
        if (!response.TryGetProperty(name, out var raw) || raw.ValueKind != JsonValueKind.Object)
            return null;
        var version = OptionalString(raw, "version");
        var packs = new List<EndfieldRemoteObject>();
        if (raw.TryGetProperty("patches", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var pack in list.EnumerateArray())
                packs.Add(ParsePack(pack));
        }
        else if (!string.IsNullOrWhiteSpace(OptionalString(raw, "url")))
        {
            packs.Add(ParsePack(raw));
        }

        // A target label without a verifiable object is not a usable pre-download.
        if (string.IsNullOrWhiteSpace(version) || packs.Count == 0) return null;
        return new EndfieldPatch(version, packs,
            OptionalMd5(raw, "md5"), OptionalLong(raw, "package_size"), OptionalString(raw, "cd_key"),
            OptionalString(raw, "v2_patch_info_url") is { Length: > 0 } info ? TrustedUri(info) : null,
            OptionalMd5(raw, "v2_patch_info_md5"));
    }

    private static EndfieldRemoteObject ParsePack(JsonElement raw) =>
        new(TrustedUri(RequiredString(raw, "url")), RequiredMd5(raw, "md5"),
            RequiredLong(raw, "package_size"));

    internal static Uri TrustedUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || (!uri.IsDefaultPort && uri.Port != 443) || uri.UserInfo.Length > 0
            || !(uri.Host.Equals("beyond.hycdn.cn", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".hycdn.cn", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".hypergryph.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".gryphline.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Publisher supplied an untrusted resource address.");
        return uri;
    }

    private static JsonElement RequiredObject(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Publisher response has no {name} object.");
        return property;
    }

    private static string RequiredString(JsonElement value, string name) =>
        OptionalString(value, name) is { Length: > 0 } text
            ? text : throw new InvalidDataException($"Publisher response has no {name}.");

    private static string? OptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static string RequiredMd5(JsonElement value, string name) =>
        OptionalMd5(value, name) ?? throw new InvalidDataException($"Publisher response has no valid {name}.");

    private static string? OptionalMd5(JsonElement value, string name)
    {
        var md5 = OptionalString(value, name);
        return md5 is { Length: 32 } && md5.All(Uri.IsHexDigit)
            ? md5.ToLowerInvariant() : null;
    }

    private static long RequiredLong(JsonElement value, string name) =>
        OptionalLong(value, name) is { } number && number >= 0
            ? number : throw new InvalidDataException($"Publisher response has no valid {name}.");

    private static long? OptionalLong(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(property.GetString(), out var number) => number,
            _ => null
        };
    }
}

/// <summary>Small independent decoder for the publisher's public integrity list.</summary>
public static class EndfieldManifestDecoder
{
    // The publicly documented game_files AES-256-CBC key and IV; see ENDFIELD_DOWNLOAD_RESEARCH.md.
    private static readonly byte[] Key =
    [
        0xC0, 0xF3, 0x0E, 0x1C, 0xE7, 0x63, 0xBB, 0xC2, 0x1C, 0xC3, 0x55, 0xA3, 0x43, 0x03, 0xAC, 0x50,
        0x39, 0x94, 0x44, 0xBF, 0xF6, 0x8C, 0x4A, 0x22, 0xAF, 0x39, 0x8C, 0x0A, 0x16, 0x6E, 0xE1, 0x43
    ];
    private static readonly byte[] Iv =
    [
        0x33, 0x46, 0x78, 0x61, 0x19, 0x27, 0x50, 0x64,
        0x95, 0x01, 0x93, 0x72, 0x64, 0x60, 0x84, 0x00
    ];

    public static IReadOnlyList<EndfieldManifestFile> Decode(byte[] encrypted, string expectedMd5)
    {
        if (encrypted.Length == 0 || !MD5.HashData(encrypted).AsSpan().SequenceEqual(Convert.FromHexString(expectedMd5)))
            throw new InvalidDataException("Encrypted game_files MD5 mismatch.");
        var text = new UTF8Encoding(false, true).GetString(Decrypt(encrypted));
        var files = new List<EndfieldManifestFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var doc = JsonDocument.Parse(line);
            var node = doc.RootElement;
            var path = node.GetProperty("path").GetString() ?? "";
            var md5 = node.GetProperty("md5").GetString() ?? "";
            var size = node.GetProperty("size").GetInt64();
            SafeGamePath.ValidateRelative(path);
            if (size < 0 || md5.Length != 32 || !md5.All(Uri.IsHexDigit) || !seen.Add(path))
                throw new InvalidDataException("game_files contains an invalid or duplicate file.");
            files.Add(new EndfieldManifestFile(path.Replace('\\', '/'), size, md5.ToLowerInvariant()));
        }
        if (files.Count == 0) throw new InvalidDataException("game_files is empty.");
        return files;
    }
    public static byte[] Decrypt(byte[] encrypted)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
    }
}
