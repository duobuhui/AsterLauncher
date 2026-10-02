using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AsterLauncher.Core;
namespace AsterLauncher.Infrastructure;

public static class HoYoSdkArchive
{
    public static async Task<IReadOnlyList<HoYoFile>> ReadAsync(string archivePath, HoYoSdk sdk, CancellationToken token)
    {
        if (!await EndfieldDownloadService.MatchesAsync(archivePath, sdk.Size, sdk.Md5, token)) throw new InvalidDataException("渠道组件压缩包校验失败。");
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > 2048) throw new InvalidDataException("渠道组件文件数量无效。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var path = entry.FullName.TrimEnd('/'); SafeGamePath.ValidateRelative(path);
            if (((entry.ExternalAttributes >> 16) & 0xF000) is 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("渠道组件包含文件系统链接。");
            if (entry.FullName.EndsWith('/')) continue;
            if (entry.Length is < 0 or > 256 * 1024 * 1024 || !entries.TryAdd(path, entry)) throw new InvalidDataException("渠道组件存在无效或重复文件。");
            expanded = checked(expanded + entry.Length);
            if (expanded > 512 * 1024 * 1024) throw new InvalidDataException("渠道组件解压大小超出限制。");
        }
        if (!entries.TryGetValue(sdk.VersionFile, out var marker) || marker.Length > 512 * 1024) throw new InvalidDataException("渠道组件缺少可信文件清单。");
        byte[] bytes;
        await using (var source = marker.Open()) { using var output = new MemoryStream(); await source.CopyToAsync(output, token); bytes = output.ToArray(); }
        var files = new Dictionary<string, HoYoFile>(StringComparer.OrdinalIgnoreCase);
        var key = DownloadTaskCache.ContentKey(sdk.Md5, sdk.Size);
        using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(bytes)); string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            token.ThrowIfCancellationRequested(); if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line); var node = doc.RootElement;
            var path = node.GetProperty("remoteName").GetString()!; SafeGamePath.ValidateRelative(path);
            var hash = node.GetProperty("md5").GetString()!; var size = node.GetProperty("fileSize").GetInt64();
            if (!HoYoManifestReader.ValidMd5(hash) || !entries.TryGetValue(path, out var entry) || entry.Length != size
                || path.Equals("config.ini", StringComparison.OrdinalIgnoreCase) || !files.TryAdd(path, new(path, size, hash, [], new(key, path))))
                throw new InvalidDataException("渠道组件文件清单不符。");
        }
        if (files.Count == 0 || entries.Keys.Any(path => !path.Equals(sdk.VersionFile, StringComparison.OrdinalIgnoreCase) && !path.Equals("deletefiles.txt", StringComparison.OrdinalIgnoreCase) && !files.ContainsKey(path)))
            throw new InvalidDataException("渠道组件含有未声明文件。");
        files[sdk.VersionFile] = new(sdk.VersionFile, bytes.Length, Convert.ToHexString(MD5.HashData(bytes)), [], new(key, sdk.VersionFile));
        return files.Values.ToArray();
    }
    public static IReadOnlyList<string> ReadRemovedFiles(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var entry = zip.GetEntry("deletefiles.txt");
        if (entry is null) return [];
        if (entry.Length > 4096) throw new InvalidDataException("渠道组件删除列表过大。");
        using var reader = new StreamReader(entry.Open());
        var paths = reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var path in paths)
        {
            SafeGamePath.ValidateRelative(path);
            // Official SDK cleanup is restricted to legacy login DLLs, never arbitrary user or resource files.
            if (!(path.StartsWith("YuanShen_Data/Plugins/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("StarRail_Data/Plugins/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("ZenlessZoneZero_Data/Plugins/", StringComparison.OrdinalIgnoreCase))
                || !path.EndsWith("/PCGameSDK.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("渠道组件请求删除不允许的文件。");
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static async Task ExtractAsync(string archivePath, HoYoFile file, string output, CancellationToken token)
    {
        var source = file.SdkEntry ?? throw new InvalidDataException("缺少渠道组件来源。");
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry(source.EntryPath) ?? throw new InvalidDataException("渠道组件文件已变化。");
        if (entry.Length != file.Size) throw new InvalidDataException("渠道组件文件大小不符。");
        await using var input = entry.Open(); await using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(target, token); await target.FlushAsync(token); target.Flush(true);
    }
}