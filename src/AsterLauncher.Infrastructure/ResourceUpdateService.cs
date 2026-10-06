using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

/// <summary>Independent, bounded public GitHub data feed. Activate after all content verifies; retain offline snapshot.</summary>
public sealed class ResourceUpdateService : IResourceCatalogProvider
{
    public const string FeedUrl = "https://raw.githubusercontent.com/duobuhui/AsterLauncher/main/resources/catalog.json";
    private static readonly Uri RepositoryRoot = new("https://raw.githubusercontent.com/duobuhui/AsterLauncher/main/resources/");
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly HttpClient _http;
    private readonly string _dataRoot;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1);
    private ResourceCatalog? _current;
    public ResourceCatalog? Current => Volatile.Read(ref _current);
    public event EventHandler? Updated;
    public ResourceUpdateService(HttpClient http, string dataRoot)
    {
        _http = http; _dataRoot = Path.GetFullPath(dataRoot); _root = Path.Combine(_dataRoot, "resources");
    }
    public async Task LoadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var path = SafeGamePath.Resolve(_root, "catalog.json");
            if (!File.Exists(path)) return;
            var bytes = await ReadBoundedAsync(File.OpenRead(path), 1024 * 1024, token).ConfigureAwait(false);
            var catalog = Parse(bytes);
            foreach (var image in catalog.Images)
                if (!await VerifyAsync(ImagePath(image), image, token).ConfigureAwait(false)) return;
            Volatile.Write(ref _current, catalog);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { }
        finally { _gate.Release(); }
    }
    public async Task<bool> RefreshAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            using var lease = LocalStorageGate.BeginOperation(_dataRoot);
            var bytes = await DownloadAsync(new Uri(FeedUrl), 1024 * 1024, token).ConfigureAwait(false);
            var catalog = Parse(bytes);
            var current = Current;
            if (current is not null && catalog.Revision < current.Revision)
                throw new InvalidDataException("资源版本低于本地缓存，已保留当前资源。");
            if (current is not null && catalog.Revision == current.Revision &&
                !JsonSerializer.SerializeToUtf8Bytes(current, JsonOptions).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions)))
                throw new InvalidDataException("同一资源版本的内容发生变化，请发布新的资源版本。");
            Directory.CreateDirectory(_root);
            var content = SafeGamePath.Resolve(_root, "content"); Directory.CreateDirectory(content);
            foreach (var image in catalog.Images)
            {
                var path = ImagePath(image);
                if (await VerifyAsync(path, image, token).ConfigureAwait(false)) continue;
                var contentBytes = await DownloadAsync(new Uri(RepositoryRoot, image.File), checked((int)image.Size), token).ConfigureAwait(false);
                if (contentBytes.LongLength != image.Size || !Hash(contentBytes).Equals(image.Sha256, StringComparison.OrdinalIgnoreCase) || !IsImage(contentBytes, Path.GetExtension(image.File)))
                    throw new InvalidDataException("资源图片校验失败。");
                temporary = SafeGamePath.Resolve(content, Guid.NewGuid().ToString("N") + ".tmp");
                await File.WriteAllBytesAsync(temporary, contentBytes, token).ConfigureAwait(false);
                File.Move(temporary, path, true); temporary = null;
            }
            if (current is not null && catalog.Revision == current.Revision) { Updated?.Invoke(this, EventArgs.Empty); return false; } // verified/repaired cache without redownloading healthy images
            temporary = SafeGamePath.Resolve(_root, Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions), token).ConfigureAwait(false);
            File.Move(temporary, SafeGamePath.Resolve(_root, "catalog.json"), true); temporary = null;
            Volatile.Write(ref _current, catalog);
            Updated?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }
    public string? FindImage(string gameId, string kind, string key)
    {
        var image = Current?.Images.FirstOrDefault(i => i.GameId == gameId && i.Kind == kind &&
            (i.Key == key || i.Aliases.Contains(key, StringComparer.OrdinalIgnoreCase)));
        if (image is null) return null;
        var path = ImagePath(image);
        return File.Exists(path) ? path : null;
    }
    private string ImagePath(ResourceImage image) => SafeGamePath.Resolve(_root, "content/" + image.Sha256.ToLowerInvariant() + Path.GetExtension(image.File).ToLowerInvariant());
    public static ResourceCatalog Parse(byte[] bytes)
    {
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("资源清单过大。");
        var value = JsonSerializer.Deserialize<ResourceCatalog>(bytes, JsonOptions) ?? throw new InvalidDataException("资源清单为空。");
        if (value.SchemaVersion != 1 || value.Revision <= 0 || value.PublishedAt == default ||
            value.Pools is null || value.Images is null || value.Codes is null || value.Announcements is null ||
            value.Pools.Count > 2000 || value.Images.Count > 500 || value.Codes.Count > 200 || value.Announcements.Count > 500)
            throw new InvalidDataException("不支持的资源清单。");

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in value.Images)
        {
            if (image is null || image.Sha256 is null || image.File is null || !Game(image.GameId) || image.Kind is not ("portrait" or "banner") || !Text(image.Key) ||
                image.Aliases is null || image.Aliases.Count > 30 || image.Aliases.Any(a => !Text(a)) ||
                !keys.Add(image.GameId + ":" + image.Kind + ":" + image.Key) ||
                image.Size is <= 0 or > 8 * 1024 * 1024 || image.Sha256.Length != 64 || !image.Sha256.All(Uri.IsHexDigit) ||
                !RelativeImage(image.File) || !PublicSource(image.Source) || !Text(image.Attribution))
                throw new InvalidDataException("资源图片信息无效。");
        }
        if (value.Images.Sum(i => i.Size) > 64 * 1024 * 1024) throw new InvalidDataException("资源图片总大小超过限制。");
        keys.Clear();
        foreach (var pool in value.Pools)
            if (pool is null || !Game(pool.GameId) || !Text(pool.Key) || !Text(pool.Name) || !PublicSource(pool.Source) ||
                !keys.Add(pool.GameId + ":" + pool.Key + ":" + pool.Phase) || pool.EndsAt is not null && (pool.StartsAt is null || pool.EndsAt <= pool.StartsAt) ||
                pool.Weapon?.Length > 160 || pool.WeaponPool?.Length > 160)
                throw new InvalidDataException("卡池信息无效。");
        foreach (var item in value.Announcements)
            if (item is null || !Game(item.GameId) || !Text(item.Title) || !Text(item.Region) || item.Kind is not ("event" or "maintenance" or "livestream") ||
                item.StartsAt == default || item.EndsAt <= item.StartsAt || !PublicSource(item.Url)) throw new InvalidDataException("活动信息无效。");
        foreach (var item in value.Codes)
            if (item is null || !Game(item.GameId) || !Text(item.Code) || !Text(item.Reward) || !Text(item.Region) ||
                item.Platforms is null || item.Platforms.Count > 10 || item.Platforms.Any(p => !Text(p)) || !PublicSource(item.Source))
                throw new InvalidDataException("兑换码信息无效。");
        return value;
    }
    private static bool Game(string value) => value is BuiltInGameIds.Endfield or BuiltInGameIds.GenshinImpact or BuiltInGameIds.HonkaiStarRail or BuiltInGameIds.ZenlessZoneZero or BuiltInGameIds.HonkaiImpact3rd or BuiltInGameIds.Arknights or BuiltInGameIds.PetitPlanet;
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 160 && !value.Any(char.IsControl);
    private static bool PublicSource(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && uri.IsDefaultPort && !IPAddress.TryParse(uri.Host, out _) &&
        uri.Host.Contains('.') && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
    private static bool RelativeImage(string value) => value.Length is > 0 and < 240 && !value.Contains('\\') &&
        value.Split('/').All(s => s.Length > 0 && s is not ("." or "..") && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) &&
        Path.GetExtension(value).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp";
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static bool IsImage(byte[] value, string extension)
    {
        static bool Dimensions(uint w,uint h) => w is >0 and <=8192 && h is >0 and <=8192 && (ulong)w*h<=16_777_216;

        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            return value.Length >= 24 && value.AsSpan(0,8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) &&
                Dimensions(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(16,4)),System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(20,4)));
        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            if(value.Length<30 || System.Text.Encoding.ASCII.GetString(value,0,4)!="RIFF" || System.Text.Encoding.ASCII.GetString(value,8,4)!="WEBP")return false;
            var kind=System.Text.Encoding.ASCII.GetString(value,12,4);
            if(kind=="VP8X")return Dimensions(1u+value[24]+((uint)value[25]<<8)+((uint)value[26]<<16),1u+value[27]+((uint)value[28]<<8)+((uint)value[29]<<16));
            if(kind=="VP8L" && value[20]==0x2f)return Dimensions(1u+value[21]+(((uint)value[22]&0x3f)<<8),1u+((uint)value[22]>>6)+((uint)value[23]<<2)+(((uint)value[24]&0xf)<<10));
            if(kind=="VP8 " && value[23]==0x9d && value[24]==1 && value[25]==0x2a)return Dimensions((uint)(value[26]+(value[27]<<8))&0x3fff,(uint)(value[28]+(value[29]<<8))&0x3fff);
            return false;
        }
        if(value.Length<4 || value[0]!=255 || value[1]!=216 || value[^2]!=255 || value[^1]!=217)return false;
        for(int i=2;i+8<value.Length;)
        {
            if(value[i++]!=255)return false;
            while(i<value.Length && value[i]==255)i++;
            if(i>=value.Length)return false;
            var marker=value[i++]; if(marker is 0xd8 or 0xd9 || marker is >=0xd0 and <=0xd7)continue;
            if(i+2>value.Length)return false;
            var length=(value[i]<<8)+value[i+1];if(length<2 || i+length>value.Length)return false;
            if(marker is 0xc0 or 0xc1 or 0xc2)return Dimensions((uint)((value[i+5]<<8)+value[i+6]),(uint)((value[i+3]<<8)+value[i+4]));
            i+=length;
        }
        return false;
    }
    private static async Task<bool> VerifyAsync(string path, ResourceImage image, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != image.Size) return false;
        var bytes = await ReadBoundedAsync(File.OpenRead(path), checked((int)image.Size), token).ConfigureAwait(false);
        return Hash(bytes).Equals(image.Sha256, StringComparison.OrdinalIgnoreCase) && IsImage(bytes, Path.GetExtension(image.File));
    }
    private async Task<byte[]> DownloadAsync(Uri uri, int limit, CancellationToken token)
    {
        if (!uri.AbsoluteUri.StartsWith(RepositoryRoot.AbsoluteUri, StringComparison.Ordinal) || uri.Query.Length != 0)
            throw new InvalidDataException("资源地址不在公开仓库中。");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("资源超过大小限制。");
        return await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), limit, token).ConfigureAwait(false);
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        using (stream)
        using (var output = new MemoryStream())
        {
            var buffer = new byte[32768]; int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > limit) throw new InvalidDataException("资源超过大小限制。");
                await output.WriteAsync(buffer.AsMemory(0,count), token).ConfigureAwait(false);
            }
            return output.ToArray();
        }
    }
}
