using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

/// <summary>Target-manifest synchronization; patch formats never enter the installation as raw payloads.</summary>
public sealed class HoYoMaintenanceService(IHoYoDistributionProvider provider, EndfieldDownloadService download,
    HoYoContentCodec codec, string dataRoot)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RootGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly DownloadTaskCache _tasks = new(dataRoot, "hoyo");
    public Task<HoYoRelease?> GetReleaseAsync(string gameId, CancellationToken token = default) => provider.GetReleaseAsync(gameId, token);
    public static string? ReadVersion(string root)
    {
        var path = SafeGamePath.Resolve(root, "config.ini");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("游戏配置文件过大。");
        return File.ReadLines(path).Select(line => line.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim() == "game_version")?[1].Trim();
    }
    public static void ValidateExistingRoot(string root, string executable)
    {
        SafeGamePath.Resolve(root, executable);
        var config = SafeGamePath.Resolve(root, "config.ini");
        if (!File.Exists(config))
        {
            if (File.Exists(SafeGamePath.Resolve(root, ".aster-hoyo-maintenance.json"))) return;
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new InvalidOperationException("此目录没有可识别的游戏安装配置。请选择空目录，或使用官方启动器修复安装信息后重新选择。");
            return;
        }
        if (new FileInfo(config).Length > 64 * 1024) throw new InvalidDataException("游戏配置文件过大。");
        var values = File.ReadLines(config).Select(line => line.Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
        // Never turn a Bilibili/global install into a national official install by replacing SDK files.
        if (values.GetValueOrDefault("channel") != "1" || values.GetValueOrDefault("cps", "mihoyo") != "mihoyo"
            || values.GetValueOrDefault("sub_channel", "1") != "1"
            || values.GetValueOrDefault("game_biz", "").Contains("global", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(SafeGamePath.Resolve(root, executable)))
            throw new InvalidOperationException("此安装不是已确认的国服官服。当前米哈游维护入口仅适用于国服官服；请保留原渠道并使用对应官方启动器维护。");
    }
    public async Task<HoYoPlan> PlanAsync(string gameId, HoYoInstallation install, bool preload = false, CancellationToken token = default)
    {
        var package = await provider.GetPackageAsync(gameId, install.AudioLanguages, preload, token);
        if (package.Release.GameId != gameId) throw new InvalidDataException("官方清单游戏身份不符。");
        var missing = new List<HoYoFile>(); var corrupt = new List<HoYoFile>();
        foreach (var file in package.Files)
        {
            token.ThrowIfCancellationRequested();
            if (install.InstallRoot is not { Length: > 0 } root || !File.Exists(SafeGamePath.Resolve(root, file.Path))) missing.Add(file);
            else if (!await VerifiedFileCommit.MatchesAsync(SafeGamePath.Resolve(root, file.Path), file.Integrity, token)) corrupt.Add(file);
        }
        var targets = missing.Concat(corrupt).ToArray();
        var chunks = UniqueChunks(targets);
        long remaining = 0;
        foreach (var chunk in chunks)
            if (!await EndfieldDownloadService.MatchesAsync(CachePath(chunk), chunk.CompressedSize, chunk.CompressedMd5, token))
                remaining = checked(remaining + chunk.CompressedSize);
        var outputBytes = targets.Sum(f => f.Size);
        var peak = checked(chunks.Sum(c => c.CompressedSize) + outputBytes + targets.Select(f => f.Size).DefaultIfEmpty().Max());
        return new(package, missing, corrupt, remaining, peak);
    }
    public async Task SyncAsync(string gameId, HoYoInstallation install, bool repairOnly,
        IProgress<HoYoProgress>? progress = null, CancellationToken token = default)
    {
        var root = install.InstallRoot ?? throw new InvalidOperationException("请先设置安装目录。");
        using var storage = LocalStorageGate.BeginOperation(dataRoot);
        await using var lease = await LockAsync(root, token);
        EnsureNotRunning(gameId);
        progress?.Report(new("正在检查官方清单", 0, 0, 0, 0));
        var plan = await PlanAsync(gameId, install, false, token);
        var existingJournal = SafeGamePath.Resolve(root, ".aster-hoyo-maintenance.json");
        if (File.Exists(existingJournal))
        {
            if (new FileInfo(existingJournal).Length > 16 * 1024 * 1024) throw new InvalidDataException("维护恢复记录过大。");
            using var record = JsonDocument.Parse(await File.ReadAllTextAsync(existingJournal, token));
            if (record.RootElement.GetProperty("gameId").GetString() != gameId)
                throw new InvalidOperationException("此目录存在另一游戏的未完成维护任务。");
            if (record.RootElement.TryGetProperty("stageName", out var oldStageName) && record.RootElement.TryGetProperty("files", out var oldFiles))
            {
                var name = oldStageName.GetString()!;
                if (name != ".aster-hoyo-stage-" + install.InstallationId.ToString("N")) throw new InvalidDataException("维护恢复记录与当前安装不符。");
                var entries = JsonSerializer.Deserialize<EndfieldManifestFile[]>(oldFiles.GetRawText()) ?? throw new InvalidDataException("维护恢复文件列表无效。");
                RemoveStage(SafeGamePath.Resolve(root, name), entries.Select(f => new HoYoFile(f.Path, f.Size, f.Md5, [])));
            }
        }
        ValidateExistingRoot(root, plan.Package.Release.ExecutableName);
        var local = ReadVersion(root) ?? install.InstalledVersion;
        if (local is not null && !install.OfficialChannelConfirmed) throw new InvalidOperationException("请先确认这是国服官服安装，再维护游戏文件。");
        if (repairOnly && !string.Equals(local, plan.Package.Release.Version, StringComparison.Ordinal))
            throw new InvalidOperationException("当前安装与最新版本不同，请先更新游戏；不能用新版本清单修复旧版本。");
        if (local is { Length: > 0 } && Version.TryParse(local, out var installed) && Version.TryParse(plan.Package.Release.Version, out var target) && installed > target)
            throw new InvalidOperationException("官方接口返回了较旧版本，已停止维护以避免降级。");
        CheckSpace(root, plan.RequiredFreeBytes);
        CheckSpace(dataRoot, plan.DownloadBytes);
        Directory.CreateDirectory(root);
        var journal = SafeGamePath.Resolve(root, ".aster-hoyo-maintenance.json");
        install.MaintenanceInProgress = true;
        var files = plan.Missing.Concat(plan.Corrupt).ToArray();
        var stageName = ".aster-hoyo-stage-" + install.InstallationId.ToString("N");
        await WriteAtomicAsync(journal, JsonSerializer.Serialize(new { gameId, target = plan.Package.Release.Version, plan.Package.ContentHash, stageName, files = files.Select(f => f.Integrity).ToArray() }), token);
        var task = TaskName(install, false);
        var chunks = UniqueChunks(files);
        await _tasks.TrackAsync(task, chunks.Select(c => DownloadTaskCache.ContentKey(c.CompressedMd5, c.CompressedSize)), token);
        var stage = SafeGamePath.Resolve(root, ".aster-hoyo-stage-" + install.InstallationId.ToString("N"));
        try
        {
            RemoveStage(stage, files);
            await DownloadChunksAsync(gameId, install, plan.Package, chunks, false, progress, token);
            var fresh = await provider.GetPackageAsync(gameId, install.AudioLanguages, false, token);
            if (fresh.Release.Version != plan.Package.Release.Version || fresh.ContentHash != plan.Package.ContentHash) throw new InvalidOperationException("下载期间官方版本已变化，请重新检查后继续。");
            // Stage every output before modifying any game directory entry.
            var count = 0;
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new("正在组装并校验文件", count++, files.Length, 0, 0));
                var output = SafeGamePath.Resolve(stage, file.Path);
                await AssembleAsync(file, output, token);
            }
            count = 0;
            foreach (var file in files)
            {
                EnsureNotRunning(gameId);
                progress?.Report(new("正在应用文件", count++, files.Length, 0, 0));
                await VerifiedFileCommit.CommitAsync(SafeGamePath.Resolve(stage, file.Path), SafeGamePath.Resolve(root, file.Path), file.Integrity, token);
            }
            count = 0;
            foreach (var file in plan.Package.Files)
            {
                progress?.Report(new("正在最终校验", count++, plan.Package.Files.Count, 0, 0));
                if (!await VerifiedFileCommit.MatchesAsync(SafeGamePath.Resolve(root, file.Path), file.Integrity, token))
                    throw new InvalidDataException("最终校验失败，安装状态仍为未完成。");
            }
            EnsureNotRunning(gameId);
            if (plan.Package.Release.AudioRecordPath is { } audioPath)
            {
                var audio = SafeGamePath.Resolve(root, audioPath);
                Directory.CreateDirectory(Path.GetDirectoryName(audio)!);
                var names = install.AudioLanguages.Select(language => language switch
                { "zh-cn" => "Chinese", "en-us" => "English(US)", "ja-jp" => "Japanese", "ko-kr" => "Korean", _ => throw new InvalidDataException("无效语音语言。") });
                await WriteAtomicAsync(audio, string.Join(Environment.NewLine, names) + Environment.NewLine, token);
            }
            await WriteGameConfigAsync(root, plan.Package.Release.Version, token);
            install.InstalledVersion = plan.Package.Release.Version; install.AudioSelectionPending = false; install.OfficialChannelConfirmed = true;
            File.Delete(journal); install.MaintenanceInProgress = false;
            await _tasks.CompleteAsync(task, token);
            // A formal update only consumes a matching completed preload; unrelated caches remain explicit tasks.
            if (install.PreloadVersion == install.InstalledVersion && install.PreloadContentHash == plan.Package.ContentHash)
            {
                await _tasks.CompleteAsync(TaskName(install, true), token);
                install.PreloadVersion = install.PreloadSourceVersion = install.PreloadContentHash = null;
            }
            progress?.Report(new("文件维护完成", plan.Package.Files.Count, plan.Package.Files.Count, 0, 0));
        }
        finally { RemoveStage(stage, files); }
    }
    public async Task PreloadAsync(string gameId, HoYoInstallation install,
        IProgress<HoYoProgress>? progress = null, CancellationToken token = default)
    {
        using var storage = LocalStorageGate.BeginOperation(dataRoot);
        await using var lease = await LockAsync(install.InstallRoot ?? Path.Combine(dataRoot, install.InstallationId.ToString("N")), token);
        var plan = await PlanAsync(gameId, install, true, token);
        CheckSpace(dataRoot, plan.DownloadBytes);
        var chunks = UniqueChunks(plan.Missing.Concat(plan.Corrupt));
        var task = TaskName(install, true);
        await _tasks.TrackAsync(task, chunks.Select(c => DownloadTaskCache.ContentKey(c.CompressedMd5, c.CompressedSize)), token);
        await DownloadChunksAsync(gameId, install, plan.Package, chunks, true, progress, token);
        foreach (var chunk in chunks)
        {
            token.ThrowIfCancellationRequested();
            var decoded = await codec.DecodeAsync(await File.ReadAllBytesAsync(CachePath(chunk), token), chunk.Size, chunk.Compression, token);
            if (!Convert.ToHexString(MD5.HashData(decoded)).Equals(chunk.Md5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("预下载分块校验失败。");
        }
        var current = await provider.GetPackageAsync(gameId, install.AudioLanguages, true, token);
        if (current.ContentHash != plan.Package.ContentHash || current.Release.Version != plan.Package.Release.Version)
            throw new InvalidOperationException("官方预下载内容已撤回或替换，请重新检查。");
        install.PreloadVersion = plan.Package.Release.Version;
        install.PreloadSourceVersion = install.InstalledVersion;
        install.PreloadContentHash = plan.Package.ContentHash;
        progress?.Report(new("预下载完成", chunks.Count, chunks.Count, chunks.Sum(c => c.CompressedSize), chunks.Sum(c => c.CompressedSize)));
    }
    private async Task DownloadChunksAsync(string gameId, HoYoInstallation install, HoYoPackage package,
        IReadOnlyList<HoYoChunk> chunks, bool preload, IProgress<HoYoProgress>? progress, CancellationToken token)
    {
        var values = new ConcurrentDictionary<string, long>();
        var total = chunks.Sum(c => c.CompressedSize); var completed = 0; long completedBytes = 0;
        HoYoPackage? refreshed = null; using var refreshGate = new SemaphoreSlim(1, 1);
        await Parallel.ForEachAsync(chunks, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (chunk, ct) =>
        {
            var key = DownloadTaskCache.ContentKey(chunk.CompressedMd5, chunk.CompressedSize);
            await download.DownloadAsync(chunk.Uri, chunk.CompressedMd5, chunk.CompressedSize, async refreshToken =>
            {
                await refreshGate.WaitAsync(refreshToken);
                try
                {
                    refreshed = await provider.GetPackageAsync(gameId, install.AudioLanguages, preload, refreshToken);
                    if (refreshed.ContentHash != package.ContentHash || refreshed.Release.Version != package.Release.Version)
                        throw new InvalidOperationException("官方资源内容已变化，请重新检查。");
                    return refreshed.Files.SelectMany(f => f.Chunks).First(c => c.CompressedMd5 == chunk.CompressedMd5 && c.CompressedSize == chunk.CompressedSize).Uri;
                }
                finally { refreshGate.Release(); }
            }, new ByteProgress(value =>
            {
                var previous = values.GetValueOrDefault(key); values[key] = value;
                var bytes = Interlocked.Add(ref completedBytes, value - previous);
                progress?.Report(new("正在下载", Volatile.Read(ref completed), chunks.Count, bytes, total));
            }), ct);
            Interlocked.Increment(ref completed);
        });
    }
    public async Task AssembleAsync(HoYoFile file, string output, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            await using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                foreach (var chunk in file.Chunks)
                {
                    var path = CachePath(chunk);
                    if (!await EndfieldDownloadService.MatchesAsync(path, chunk.CompressedSize, chunk.CompressedMd5, token))
                        throw new InvalidDataException("分块缓存已损坏，请重新下载。");
                    var decoded = await codec.DecodeAsync(await File.ReadAllBytesAsync(path, token), chunk.Size, chunk.Compression, token);
                    if (!Convert.ToHexString(MD5.HashData(decoded)).Equals(chunk.Md5, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("官方分块解压后校验失败。");
                    await stream.WriteAsync(decoded, token);
                }
                await stream.FlushAsync(token); stream.Flush(true);
            }
            if (!await VerifiedFileCommit.MatchesAsync(output, file.Integrity, token)) throw new InvalidDataException("组装文件校验失败。");
        }
        catch { if (File.Exists(output)) File.Delete(output); throw; }
    }
    public Task<DownloadCacheCleanup> CancelAsync(HoYoInstallation install, bool preload = false)
        => _tasks.CancelAsync(TaskName(install, preload));
    private static string TaskName(HoYoInstallation install, bool preload) => install.InstallationId.ToString("N") + (preload ? "-preload" : "-install");
    private string CachePath(HoYoChunk c) => SafeGamePath.Resolve(dataRoot, "hoyo/objects/" + DownloadTaskCache.ContentKey(c.CompressedMd5, c.CompressedSize));
    private static IReadOnlyList<HoYoChunk> UniqueChunks(IEnumerable<HoYoFile> files) => files.SelectMany(f => f.Chunks)
        .DistinctBy(c => DownloadTaskCache.ContentKey(c.CompressedMd5, c.CompressedSize)).ToArray();
    private static void RemoveStage(string stage, IEnumerable<HoYoFile> files)
    {
        foreach (var file in files)
        {
            var path = SafeGamePath.Resolve(stage, file.Path);
            if (File.Exists(path)) File.Delete(path);
        }
        EmptyDirectoryCleanup.Prune(stage);
    }
    private static void CheckSpace(string root, long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
        if (drive.AvailableFreeSpace < bytes) throw new IOException($"{drive.Name} 可用空间不足，需要至少 {bytes / 1073741824d:0.0} GiB。");
    }
    private static async Task WriteGameConfigAsync(string root, string version, CancellationToken token)
    {
        var path = SafeGamePath.Resolve(root, "config.ini");
        var lines = File.Exists(path) ? (await File.ReadAllLinesAsync(path, token)).ToList() : new List<string> { "[General]", "channel=1", "sub_channel=1", "cps=mihoyo" };
        var index = lines.FindIndex(line => line.Split('=', 2)[0].Trim() == "game_version");
        if (index < 0) lines.Add("game_version=" + version); else lines[index] = "game_version=" + version;
        await WriteAtomicAsync(path, string.Join(Environment.NewLine, lines) + Environment.NewLine, token);
    }
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken token)
    {
        var temporary = path + ".aster-" + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), token); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void EnsureNotRunning(string gameId)
    {
        var definition = BuiltInGameCatalog.CreateAdapters().Single(a => a.Definition.Id == gameId).Definition;
        foreach (var name in definition.ExecutableNames)
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
            try { if (processes.Length > 0) throw new InvalidOperationException("请关闭当前游戏后再维护文件。"); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
    private async Task<Lease> LockAsync(string root, CancellationToken token)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        SafeGamePath.Resolve(root, "lock-check");
        var gate = RootGates.GetOrAdd(root, _ => new(1, 1)); await gate.WaitAsync(token);
        try
        {
            var folder = SafeGamePath.Resolve(dataRoot, "endfield/locks"); Directory.CreateDirectory(folder);
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root))) + ".lock";
            return new(gate, new FileStream(SafeGamePath.Resolve(folder, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch { gate.Release(); throw; }
    }
    private sealed class Lease(SemaphoreSlim gate, FileStream file) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { file.Dispose(); gate.Release(); return ValueTask.CompletedTask; }
    }
    private sealed class ByteProgress(Action<long> action) : IProgress<long> { public void Report(long value) => action(value); }
}