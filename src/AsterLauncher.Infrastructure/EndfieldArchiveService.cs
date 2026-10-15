using System.Diagnostics;
using System.Text;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

/// <summary>Extracts only manifest-owned files to private staging, never into a game directory.</summary>
public sealed class EndfieldArchiveService(string executable)
{
    public async Task<IReadOnlyDictionary<string, string>> StageAsync(
        IReadOnlyList<string> verifiedVolumes, string stage,
        IReadOnlyList<EndfieldManifestFile> targets, string? password,
        CancellationToken token = default,
        IReadOnlyDictionary<string, EndfieldV2PatchFile>? v2Files = null, string? sourceRoot = null, Action<int, int, string>? progress = null)
    {
        if (verifiedVolumes.Count == 0) return new Dictionary<string, string>();
        if (!File.Exists(executable)) throw new FileNotFoundException("缺少游戏维护解压程序。", executable);
        Directory.CreateDirectory(stage);
        var first = SafeGamePath.Resolve(stage, verifiedVolumes.Count == 1 ? "payload.zip" : "payload.zip.001");
        for (var i = 0; i < verifiedVolumes.Count; i++)
        {
            var alias = SafeGamePath.Resolve(stage, verifiedVolumes.Count == 1 ? "payload.zip" : $"payload.zip.{i + 1:000}");
            if (!WindowsHardLink.TryCreate(alias, verifiedVolumes[i])) File.Copy(verifiedVolumes[i], alias);
        }
        var listing = await RunTextAsync(["l", "-slt", "-ba", "-sccUTF-8", Password(password), first], token);
        var entries = ParseListing(listing);
        // Entries unrelated to the requested targets are never extracted; a resumed task can need one tiny file from a large ZIP.
        var budget = checked(targets.Sum(f => f.Size) * 2 + 64L * 1024 * 1024);
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in targets)
        {
            selected.Add(file.Path);
            if (v2Files is not null && v2Files.TryGetValue(file.Path, out var row))
            {
                if (row.ArchiveEntry is not null) selected.Add(row.ArchiveEntry);
                foreach (var source in row.Sources) selected.Add(source.ArchiveEntry);
            }
        }
        if (entries.Where(entry => selected.Contains(entry.Path)).Sum(entry => entry.Size) > budget)
            throw new InvalidDataException("待提取的补丁条目超过本次目标空间预算。");
        var archiveEntries = entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < targets.Count; index++)
        {
            var file = targets[index];
            progress?.Invoke(index, targets.Count, file.Path);
            token.ThrowIfCancellationRequested();
            var mapping = v2Files is not null && v2Files.TryGetValue(file.Path, out var mapped) ? mapped : null;
            var entryPath = archiveEntries.ContainsKey(file.Path) ? file.Path : mapping?.ArchiveEntry;
            if (entryPath is not null && archiveEntries.TryGetValue(entryPath, out var complete) && complete.Size == file.Size)
            {
                var destination = SafeGamePath.Resolve(stage, "files/" + file.Path);
                await ExtractAsync(first, entryPath, destination, file.Size, password, token);
                if (await VerifiedFileCommit.MatchesAsync(destination, file, token)) result[file.Path] = destination;
                else File.Delete(destination);
                continue;
            }
            if (mapping is null || string.IsNullOrWhiteSpace(sourceRoot)) continue;
            foreach (var source in mapping.Sources)
            {
                token.ThrowIfCancellationRequested();
                if (!archiveEntries.TryGetValue(source.ArchiveEntry, out var delta) || delta.Size != source.PatchSize) continue;
                var oldPath = SafeGamePath.Resolve(sourceRoot, source.BaseFile.Path);
                if (!File.Exists(oldPath)) continue;
                // This same read handle denies in-place writes and rename while hashing and patching.
                await using var old = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
                if (old.Length != source.BaseFile.Size) continue;
                var oldHash = await System.Security.Cryptography.MD5.HashDataAsync(old, token);
                if (!oldHash.AsSpan().SequenceEqual(Convert.FromHexString(source.BaseFile.Md5))) continue;
                old.Position = 0;
                var deltaPath = SafeGamePath.Resolve(stage, "delta/" + Guid.NewGuid().ToString("N") + ".hdiff");
                var destination = SafeGamePath.Resolve(stage, "files/" + file.Path);
                try
                {
                    await ExtractAsync(first, source.ArchiveEntry, deltaPath, source.PatchSize, password, token);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using (var diff = new FileStream(deltaPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        128 * 1024, FileOptions.Asynchronous))
                    {
                        await EndfieldSingleStreamPatch.ApplyAsync(old, diff, output, source.BaseFile.Size, file.Size, token);
                        await output.FlushAsync(token);
                        output.Flush(true);
                    }
                    if (!await VerifiedFileCommit.MatchesAsync(destination, file, token))
                        throw new InvalidDataException("增量输出未通过官方目标清单校验。");
                    result[file.Path] = destination;
                    break;
                }
                catch
                {
                    if (File.Exists(destination)) File.Delete(destination);
                    throw;
                }
                finally { if (File.Exists(deltaPath)) File.Delete(deltaPath); }
            }
        }
        return result;
    }

    private async Task ExtractAsync(string first, string entry, string destination, long expectedSize,
        string? password, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var process = Start(["x", "-so", "-y", "-spd", Password(password), first, entry]);
        using var cancel = token.Register(() => Kill(process));
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[128 * 1024];
            long written = 0;
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, token)) > 0)
            {
                written += read;
                if (written > expectedSize) { Kill(process); throw new InvalidDataException("补丁条目输出超过元数据大小。"); }
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            await output.FlushAsync(token);
            if (written != expectedSize) throw new InvalidDataException("补丁条目输出大小不符。");
        }
        await process.WaitForExitAsync(token);
        await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException("补丁解压或密码校验失败。");
        }
        finally { await FinishOwnedProcessAsync(process); }
    }
    private static string Password(string? value) => "-p" + (value ?? "aster-no-password");
    private Process Start(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("无法启动补丁解压程序。");
    }
    private async Task<string> RunTextAsync(string[] args, CancellationToken token)
    {
        using var process = Start(args);
        using var cancel = token.Register(() => Kill(process));
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
        var text = new StringBuilder();
        var chars = new char[4096];
        int read;
        while ((read = await process.StandardOutput.ReadAsync(chars, token)) > 0)
        {
            text.Append(chars, 0, read);
            if (text.Length > 16 * 1024 * 1024) { Kill(process); throw new InvalidDataException("压缩包清单过长。"); }
        }
        await process.WaitForExitAsync(token);
        await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException("压缩包目录或解密信息无效。");
        return text.ToString();
        }
        finally { await FinishOwnedProcessAsync(process); }
    }
    private static IReadOnlyList<(string Path, long Size)> ParseListing(string text)
    {
        var result = new List<(string, long)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in text.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = block.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains(" = "))
                .Select(line => line.Split(" = ", 2))
                .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
            if (!fields.TryGetValue("Path", out var path)) continue;
            // No symlinks, NTFS alternate streams, junctions or archive-owned hard links.
            if (fields.ContainsKey("Symbolic Link") || fields.ContainsKey("Hard Link")
                || fields.GetValueOrDefault("Attributes", "").Contains('l'))
                throw new InvalidDataException("压缩包包含文件系统链接。");
            path = path.Replace('\\', '/').TrimEnd('/');
            SafeGamePath.ValidateRelative(path);
            if (!seen.Add(path)) throw new InvalidDataException("压缩包存在重复路径。");
            if (fields.GetValueOrDefault("Folder") == "+" || fields.GetValueOrDefault("Attributes", "").Contains('D')) continue;
            if (!fields.TryGetValue("Size", out var size) || !long.TryParse(size, out var length) || length < 0)
                throw new InvalidDataException("压缩包文件大小无效。");
            result.Add((path, length));
        }
        if (result.Count == 0) throw new InvalidDataException("压缩包为空。");
        return result;
    }
    private static async Task FinishOwnedProcessAsync(Process process)
    {
        Kill(process);
        try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
