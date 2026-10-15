using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

public sealed record EndfieldV2PatchSource(
    EndfieldManifestFile BaseFile, string ArchiveEntry, long PatchSize);

public sealed record EndfieldV2PatchFile(
    EndfieldManifestFile File, string? ArchiveEntry, IReadOnlyList<EndfieldV2PatchSource> Sources);

/// <summary>Validates the publisher's V2 VFS mapping against the authoritative complete-file manifest.</summary>
public static class EndfieldV2PatchManifest
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private const int MaximumEntries = 200_000;
    private const string VfsRoot = "Endfield_Data/StreamingAssets/VFS";
    private const string FullArchivePrefix = "vfs_files/files/";
    private const string PatchArchivePrefix = "vfs_files/vfs_patch/";

    public static IReadOnlyDictionary<string, EndfieldV2PatchFile> Read(
        byte[] metadata, string expectedVersion, IReadOnlyList<EndfieldManifestFile> targetManifest)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(targetManifest);
        if (metadata.Length is 0 or > MaximumBytes)
            throw new InvalidDataException("V2 patch metadata has an invalid size.");
        if (string.IsNullOrWhiteSpace(expectedVersion))
            throw new InvalidDataException("V2 patch target version is missing.");

        try
        {
            using var document = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !string.Equals(RequiredText(root, "version"), expectedVersion, StringComparison.Ordinal))
                throw new InvalidDataException("V2 patch metadata targets a different release.");
            if (!string.Equals(Relative(RequiredText(root, "vfs_base_path")), VfsRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("V2 patch metadata uses an unsupported VFS root.");
            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array
                || files.GetArrayLength() > MaximumEntries)
                throw new InvalidDataException("V2 patch metadata has an invalid file list.");

            var targets = new Dictionary<string, EndfieldManifestFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targetManifest)
            {
                var path = Relative(target.Path);
                ValidateHash(target.Md5);
                if (target.Size < 0 || !targets.TryAdd(path, target))
                    throw new InvalidDataException("Complete manifest contains invalid or duplicate targets.");
            }

            var result = new Dictionary<string, EndfieldV2PatchFile>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = files.GetArrayLength();
            foreach (var row in files.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("V2 patch file entry is invalid.");
                var type = RequiredNonnegativeLong(row, "diffType");
                if (type is not (1 or 2))
                    throw new InvalidDataException("V2 patch file kind is unsupported.");
                var name = VfsName(RequiredText(row, "name"), requireChunk: type == 1);
                var path = VfsRoot + "/" + name;
                if (!seen.Add(path))
                    throw new InvalidDataException("V2 patch metadata contains duplicate targets.");

                var size = RequiredNonnegativeLong(row, "size");
                var md5 = RequiredHash(row, "md5");
                var archiveEntry = OptionalText(row, "local_path");
                if (archiveEntry is not null)
                {
                    archiveEntry = Relative(archiveEntry);
                    if (!string.Equals(archiveEntry, FullArchivePrefix + path, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("V2 full-file archive entry does not match its target.");
                }

                var sources = new List<EndfieldV2PatchSource>();
                if (row.TryGetProperty("patch", out var patches) && patches.ValueKind != JsonValueKind.Null)
                {
                    if (patches.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("V2 patch source list is invalid.");
                    foreach (var patch in patches.EnumerateArray())
                    {
                        if (++count > MaximumEntries || patch.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException("V2 patch metadata has too many or invalid entries.");
                        var basePath = VfsRoot + "/" + VfsName(RequiredText(patch, "base_file"), requireChunk: true);
                        var baseSize = RequiredNonnegativeLong(patch, "base_size");
                        var baseMd5 = RequiredHash(patch, "base_md5");
                        var patchPath = Relative(RequiredText(patch, "patch"));
                        var parts = patchPath.Split('/');
                        if (parts.Length != 2 || !parts[0].StartsWith("diff_", StringComparison.Ordinal)
                            || parts[0].Length == "diff_".Length
                            || !parts[1].EndsWith(".chk_patch", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("V2 delta archive entry is unsupported.");
                        var patchSize = RequiredNonnegativeLong(patch, "patch_size");
                        sources.Add(new EndfieldV2PatchSource(
                            new EndfieldManifestFile(basePath, baseSize, baseMd5),
                            PatchArchivePrefix + patchPath, patchSize));
                    }
                }

                // Only the authoritative target manifest decides which files are installed.
                // Metadata-only entries are never commands to delete local files.
                if (!targets.TryGetValue(path, out var target)) continue;
                if (size != target.Size || !string.Equals(md5, target.Md5, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("V2 patch target integrity differs from the complete manifest.");

                result.Add(path, new EndfieldV2PatchFile(target, archiveEntry, sources.ToArray()));
            }
            return new ReadOnlyDictionary<string, EndfieldV2PatchFile>(result);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("V2 patch metadata is malformed.", error);
        }
    }

    private static string VfsName(string value, bool requireChunk)
    {
        var path = Relative(value);
        if (path.Split('/').Length != 2 || (requireChunk && !path.EndsWith(".chk", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("V2 metadata uses an unsupported VFS file path.");
        return path;
    }

    private static string Relative(string value)
    {
        var path = value.Replace('\\', '/');
        SafeGamePath.ValidateRelative(path);
        return path;
    }

    private static string RequiredText(JsonElement value, string name) =>
        OptionalText(value, name) is { Length: > 0 } text
            ? text : throw new InvalidDataException($"V2 patch metadata has no {name}.");

    private static string? OptionalText(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"V2 patch metadata has an invalid {name}.");
        return property.GetString();
    }

    private static long RequiredNonnegativeLong(JsonElement value, string name)
    {
        if (value.TryGetProperty(name, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) && number >= 0)
                return number;
            if (property.ValueKind == JsonValueKind.String
                && long.TryParse(property.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number)
                && number >= 0)
                return number;
        }
        throw new InvalidDataException($"V2 patch metadata has an invalid {name}.");
    }

    private static string RequiredHash(JsonElement value, string name)
    {
        var hash = RequiredText(value, name);
        ValidateHash(hash);
        return hash.ToLowerInvariant();
    }

    private static void ValidateHash(string hash)
    {
        if (hash.Length != 32 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("V2 patch metadata has an invalid MD5.");
    }
}