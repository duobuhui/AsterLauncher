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
        CancellationToken token = default)
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
        // 7-Zip opens the first volume as one archive. No volume is treated as an independent ZIP.
        var listing = await RunTextAsync(["l", "-slt", "-ba", "-sccUTF-8", Password(password), first], token);
        var entries = ParseListing(listing);
        var budget = checked(targets.Sum(f => f.Size) * 2 + 64L * 1024 * 1024);
        if (entries.Sum(e => e.Size) > budget) throw new InvalidDataException("补丁解压总量超过目标清单空间预算。");
        var targetMap = targets.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (!targetMap.TryGetValue(entry.Path, out var file) || entry.Size != file.Size) continue;
            var destination = SafeGamePath.Resolve(stage, "files/" + file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var process = Start(["x", "-so", "-y", "-spd", Password(password), first, entry.Path]);
            using var cancel = token.Register(() => Kill(process));
            var stderr = process.StandardError.ReadToEndAsync(token);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[128 * 1024];
                long written = 0;
                int read;
                while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, token)) > 0)
                {
                    written += read;
                    if (written > file.Size) { Kill(process); throw new InvalidDataException("解压输出超过目标文件大小。"); }
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
                await output.FlushAsync(token);
            }
            await process.WaitForExitAsync(token);
            await stderr; // Discard tool diagnostics, including password/paths.
            if (process.ExitCode != 0) throw new InvalidDataException("补丁解压或密码校验失败。");
            if (await VerifiedFileCommit.MatchesAsync(destination, file, token)) result[file.Path] = destination;
            else File.Delete(destination); // Diff/VFS payload is never mistaken for a complete target file.
        }
        return result;
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
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
