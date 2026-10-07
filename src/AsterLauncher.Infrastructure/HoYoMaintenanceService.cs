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
    public static void ValidateExistingRoot(string root, string executable, HoYoChannel channel = HoYoChannel.Official)
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
        if (values.GetValueOrDefault("channel") != (channel == HoYoChannel.Bilibili ? "14" : "1")
            || values.GetValueOrDefault("cps", "mihoyo") != (channel == HoYoChannel.Bilibili ? "bilibili" : "mihoyo")
            || values.GetValueOrDefault("sub_channel", "1") != (channel == HoYoChannel.Bilibili ? "0" : "1")
            || values.GetValueOrDefault("game_biz", "").Contains("global", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(SafeGamePath.Resolve(root, executable)))
            throw new InvalidOperationException("安装配置与所选渠道不符。请保留原目录，为当前渠道选择独立目录。");
    }
    public async Task<HoYoPlan> PlanAsync(string gameId, HoYoInstallation install, bool preload = false, CancellationToken token = default, HoYoInstallation? peer = null, IProgress<HoYoProgress>? progress = null)
    {
        using var storage = LocalStorageGate.BeginOperation(dataRoot);
        var package = await PreparePackageAsync(gameId, install, preload, progress, token);
        if (package.Release.GameId != gameId) throw new InvalidDataException("官方清单游戏身份不符。");
        var missing = new List<HoYoFile>(); var corrupt = new List<HoYoFile>();
        var localProgress = new FileProgress(progress, "正在检查本地文件", package.Files.Count, package.Files.Sum(f => f.Size));
        foreach (var file in package.Files)
        {
            token.ThrowIfCancellationRequested();
            localProgress.Begin(file.Path);
            if (install.InstallRoot is not { Length: > 0 } root || !File.Exists(SafeGamePath.Resolve(root, file.Path))) missing.Add(file);
            else if (!await VerifiedFileCommit.MatchesAsync(SafeGamePath.Resolve(root, file.Path), file.Integrity, token, localProgress)) corrupt.Add(file);
            localProgress.Complete(file.Size);
        }
        var targets = missing.Concat(corrupt).ToArray();
        var reused = new List<HoYoReusableFile>(); string? fallback = null;
        if (!preload && install.ShareResources && peer?.InstallRoot is { Length: > 0 } sourceRoot && install.InstallRoot is { } targetRoot)
        {
            HoYoSharingPolicy.ValidateIndependent(targetRoot, sourceRoot);
            if (!WindowsHardLink.CanShareVolume(sourceRoot, targetRoot)) fallback = "跨卷或非 NTFS，使用独立文件。";
            else
            {
                progress?.Report(new("正在读取另一服资源清单", 0, 0, 0, 0));
                var sourcePackage = await provider.GetPackageAsync(gameId, peer.Channel, peer.AudioLanguages, false, token);
                var sourceFiles = sourcePackage.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
                var candidates = targets.Where(f => HoYoSharingPolicy.IsShareable(gameId, f.Path)
                    && sourceFiles.TryGetValue(f.Path, out var other) && other.Size == f.Size
                    && other.Md5.Equals(f.Md5, StringComparison.OrdinalIgnoreCase)).ToArray();
                var reuseProgress = new FileProgress(progress, "正在校验另一服可复用资源", candidates.Length, candidates.Sum(f => f.Size));
                foreach (var file in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    reuseProgress.Begin(file.Path);
                    var path = SafeGamePath.Resolve(sourceRoot, file.Path);
                    if (await VerifiedFileCommit.MatchesAsync(path, file.Integrity, token, reuseProgress)) reused.Add(new(file, path));
                    reuseProgress.Complete(file.Size);
                }
            }
        }
        var reusablePaths = reused.Select(f => f.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chunks = UniqueChunks(targets.Where(f => !reusablePaths.Contains(f.Path)));
        long remaining = 0;
        var cacheProgress = new FileProgress(progress, "正在检查下载缓存", chunks.Count, chunks.Sum(c => c.CompressedSize));
        foreach (var chunk in chunks)
        {
            token.ThrowIfCancellationRequested();
            cacheProgress.Begin(null);
            if (!await EndfieldDownloadService.MatchesAsync(CachePath(chunk), chunk.CompressedSize, chunk.CompressedMd5, token, cacheProgress))
                remaining = checked(remaining + chunk.CompressedSize);
            cacheProgress.Complete(chunk.CompressedSize);
        }
        var outputBytes = targets.Where(f => !reusablePaths.Contains(f.Path)).Sum(f => f.Size);
        var peak = checked(chunks.Sum(c => c.CompressedSize) + outputBytes + targets.Select(f => f.Size).DefaultIfEmpty().Max());
        return new(package, missing, corrupt, remaining, peak, reused, fallback);
    }
    public async Task SyncAsync(string gameId, HoYoInstallation install, bool repairOnly,
        IProgress<HoYoProgress>? progress = null, CancellationToken token = default, HoYoInstallation? peer = null)
    {
        var root = install.InstallRoot ?? throw new InvalidOperationException("请先设置安装目录。");
        using var storage = LocalStorageGate.BeginOperation(dataRoot);
        HoYoSharingPolicy.ValidateIndependent(root, peer?.InstallRoot);
        await using var lease = await LockRootsAsync(root, peer?.InstallRoot, token);
        EnsureNotRunning(gameId);
        progress?.Report(new("正在检查官方清单", 0, 0, 0, 0));
        var plan = await PlanAsync(gameId, install, false, token, peer, progress);
        var existingJournal = SafeGamePath.Resolve(root, ".aster-hoyo-maintenance.json");
        if (File.Exists(existingJournal))
        {
            if (new FileInfo(existingJournal).Length > 16 * 1024 * 1024) throw new InvalidDataException("维护恢复记录过大。");
            using var record = JsonDocument.Parse(await File.ReadAllTextAsync(existingJournal, token));
            if (record.RootElement.GetProperty("gameId").GetString() != gameId || (record.RootElement.TryGetProperty("channel", out var savedChannel) && savedChannel.GetInt32() != (int)install.Channel))
                throw new InvalidOperationException("此目录存在另一游戏的未完成维护任务。");
            if (record.RootElement.TryGetProperty("stageName", out var oldStageName) && record.RootElement.TryGetProperty("files", out var oldFiles))
            {
                var name = oldStageName.GetString()!;
                if (name != ".aster-hoyo-stage-" + install.InstallationId.ToString("N")) throw new InvalidDataException("维护恢复记录与当前安装不符。");
                var entries = JsonSerializer.Deserialize<EndfieldManifestFile[]>(oldFiles.GetRawText()) ?? throw new InvalidDataException("维护恢复文件列表无效。");
                RemoveStage(SafeGamePath.Resolve(root, name), entries.Select(f => new HoYoFile(f.Path, f.Size, f.Md5, [])));
            }
        }
        ValidateExistingRoot(root, plan.Package.Release.ExecutableName, install.Channel);
        var local = ReadVersion(root) ?? install.InstalledVersion;
        if (local is not null && !(install.ChannelConfirmed || install.OfficialChannelConfirmed)) throw new InvalidOperationException("请先确认当前安装的渠道，再维护游戏文件。");
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
        await WriteAtomicAsync(journal, JsonSerializer.Serialize(new { gameId, channel = install.Channel, installationId = install.InstallationId, target = plan.Package.Release.Version, plan.Package.ContentHash, stageName, files = files.Select(f => f.Integrity).ToArray() }), token);
        var task = TaskName(install, false);
        var sharedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (plan.Reusable is { Count: > 0 } reusable && peer?.InstallRoot is { } peerRoot)
        {
            var engine = SharingEngine(gameId);
            var reuseProgress = new FileProgress(progress, "正在复用另一服资源", reusable.Count, reusable.Sum(f => f.File.Size));
            foreach (var item in reusable)
            {
                token.ThrowIfCancellationRequested();
                reuseProgress.Begin(item.File.Path);
                EnsureNotRunning(gameId);
                var linked = await engine.OptimizeAsync(peerRoot, root, [item.File.Integrity], [item.File.Integrity], token, reuseProgress);
                if (linked.FileCount > 0) sharedPaths.Add(item.File.Path);
                reuseProgress.Complete(item.File.Size);
            }
        }
        files = files.Where(f => !sharedPaths.Contains(f.Path)).ToArray();
        var chunks = UniqueChunks(files);
        CheckSpace(dataRoot, chunks.Sum(c => c.CompressedSize));
        CheckSpace(root, checked(chunks.Sum(c => c.CompressedSize) + files.Sum(f => f.Size) + files.Select(f => f.Size).DefaultIfEmpty().Max()));
        await _tasks.TrackAsync(task, chunks.Select(c => DownloadTaskCache.ContentKey(c.CompressedMd5, c.CompressedSize)), token);
        var stage = SafeGamePath.Resolve(root, ".aster-hoyo-stage-" + install.InstallationId.ToString("N"));
        try
        {
            RemoveStage(stage, files);
            await DownloadChunksAsync(gameId, install, plan.Package, chunks, false, progress, token);
            var fresh = await PreparePackageAsync(gameId, install, false, progress, token);
            if (fresh.Release.Version != plan.Package.Release.Version || fresh.ContentHash != plan.Package.ContentHash) throw new InvalidOperationException("下载期间官方版本已变化，请重新检查后继续。");
            // Stage every output before modifying any game directory entry.
            var assemblyProgress = new FileProgress(progress, "正在组装并校验文件", files.Length, files.Sum(f => f.Size));
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                assemblyProgress.Begin(file.Path);
                var output = SafeGamePath.Resolve(stage, file.Path);
                await AssembleAsync(file, output, token, assemblyProgress);
                assemblyProgress.Complete(file.Size);
            }
            var applyProgress = new FileProgress(progress, "正在应用文件", files.Length, files.Sum(f => f.Size));
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                applyProgress.Begin(file.Path);
                EnsureNotRunning(gameId);
                await VerifiedFileCommit.CommitAsync(SafeGamePath.Resolve(stage, file.Path), SafeGamePath.Resolve(root, file.Path), file.Integrity, token, applyProgress);
                // Release the committed staging copy so staged + installed outputs stay within the planned peak.
                File.Delete(SafeGamePath.Resolve(stage, file.Path));
                applyProgress.Complete(file.Size);
            }
            var finalProgress = new FileProgress(progress, "正在最终校验", plan.Package.Files.Count, plan.Package.Files.Sum(f => f.Size));
            foreach (var file in plan.Package.Files)
            {
                token.ThrowIfCancellationRequested();
                finalProgress.Begin(file.Path);
                if (!await VerifiedFileCommit.MatchesAsync(SafeGamePath.Resolve(root, file.Path), file.Integrity, token, finalProgress))
                    throw new InvalidDataException("最终校验失败，安装状态仍为未完成。");
                finalProgress.Complete(file.Size);
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
            foreach (var obsolete in plan.Package.RemovedSdkFiles ?? [])
            {
                token.ThrowIfCancellationRequested(); EnsureNotRunning(gameId);
                File.Delete(SafeGamePath.Resolve(root, obsolete));
            }
            await WriteGameConfigAsync(root, plan.Package.Release.Version, install.Channel, token);
            install.InstalledVersion = plan.Package.Release.Version; install.AudioSelectionPending = false; install.ChannelConfirmed = true; install.OfficialChannelConfirmed = install.Channel == HoYoChannel.Official;
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
        var plan = await PlanAsync(gameId, install, true, token, progress: progress);
        CheckSpace(dataRoot, plan.DownloadBytes);
        var chunks = UniqueChunks(plan.Missing.Concat(plan.Corrupt));
        var task = TaskName(install, true);
        await _tasks.TrackAsync(task, chunks.Select(c => DownloadTaskCache.ContentKey(c.CompressedMd5, c.CompressedSize)), token);
        await DownloadChunksAsync(gameId, install, plan.Package, chunks, true, progress, token);
        var verifyProgress = new FileProgress(progress, "正在校验预下载内容", chunks.Count, chunks.Sum(c => c.CompressedSize));
        foreach (var chunk in chunks)
        {
            token.ThrowIfCancellationRequested();
            var decoded = await codec.DecodeAsync(await File.ReadAllBytesAsync(CachePath(chunk), token), chunk.Size, chunk.Compression, token);
            if (!Convert.ToHexString(MD5.HashData(decoded)).Equals(chunk.Md5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("预下载分块校验失败。");
            verifyProgress.Complete(chunk.CompressedSize);
        }
        var current = await PreparePackageAsync(gameId, install, true, progress, token);
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
        if (chunks.Count == 0) return;
        var values = new Dictionary<string, long>();
        var progressGate = new object();
        var total = chunks.Sum(c => c.CompressedSize); var completed = 0; long completedBytes = 0, transferredBytes = 0;
        progress?.Report(new("正在下载", 0, chunks.Count, 0, total, IsNetworkTransfer: true, TransferredBytes: 0));
        HoYoPackage? refreshed = null; using var refreshGate = new SemaphoreSlim(1, 1);
        await Parallel.ForEachAsync(chunks, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (chunk, ct) =>
        {
            var key = DownloadTaskCache.ContentKey(chunk.CompressedMd5, chunk.CompressedSize);
            await download.DownloadAsync(chunk.Uri, chunk.CompressedMd5, chunk.CompressedSize, async refreshToken =>
            {
                await refreshGate.WaitAsync(refreshToken);
                try
                {
                    refreshed = await PreparePackageAsync(gameId, install, preload, progress, refreshToken);
                    if (refreshed.ContentHash != package.ContentHash || refreshed.Release.Version != package.Release.Version)
                        throw new InvalidOperationException("官方资源内容已变化，请重新检查。");
                    return refreshed.Files.SelectMany(f => f.Chunks).First(c => c.CompressedMd5 == chunk.CompressedMd5 && c.CompressedSize == chunk.CompressedSize).Uri;
                }
                finally { refreshGate.Release(); }
            }, new ByteProgress(value =>
            {
                lock (progressGate)
                {
                    var previous = values.GetValueOrDefault(key); values[key] = value;
                    completedBytes += value - previous;
                    progress?.Report(new("正在下载", completed, chunks.Count, completedBytes, total,
                        IsNetworkTransfer: true, TransferredBytes: transferredBytes));
                }
            }), ct, new ByteProgress(bytes => { lock (progressGate) transferredBytes += bytes; }));
            lock (progressGate)
            {
                completed++;
                progress?.Report(new("正在下载", completed, chunks.Count, completedBytes, total,
                    IsNetworkTransfer: true, TransferredBytes: transferredBytes));
            }
        });
    }
    public async Task AssembleAsync(HoYoFile file, string output, CancellationToken token = default, IProgress<long>? byteProgress = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            if (file.SdkEntry is { } sdkFile)
                await HoYoSdkArchive.ExtractAsync(SafeGamePath.Resolve(dataRoot, "hoyo/objects/" + sdkFile.CacheKey), file, output, token);
            else await using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                long assembled = 0;
                foreach (var chunk in file.Chunks)
                {
                    var path = CachePath(chunk);
                    if (!await EndfieldDownloadService.MatchesAsync(path, chunk.CompressedSize, chunk.CompressedMd5, token))
                        throw new InvalidDataException("分块缓存已损坏，请重新下载。");
                    var decoded = await codec.DecodeAsync(await File.ReadAllBytesAsync(path, token), chunk.Size, chunk.Compression, token);
                    if (!Convert.ToHexString(MD5.HashData(decoded)).Equals(chunk.Md5, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("官方分块解压后校验失败。");
                    await stream.WriteAsync(decoded, token);
                    assembled += decoded.Length;
                    byteProgress?.Report(assembled / 2);
                }
                await stream.FlushAsync(token); stream.Flush(true);
            }
            if (!await VerifiedFileCommit.MatchesAsync(output, file.Integrity, token,
                byteProgress is null ? null : new ByteProgress(bytes => byteProgress.Report(file.Size / 2 + bytes / 2))))
                throw new InvalidDataException("组装文件校验失败。");
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
    private static async Task WriteGameConfigAsync(string root, string version, HoYoChannel channel, CancellationToken token)
    {
        var path = SafeGamePath.Resolve(root, "config.ini");
        var lines = File.Exists(path) ? (await File.ReadAllLinesAsync(path, token)).ToList() : new List<string> { "[General]", "channel=" + (int)channel, "sub_channel=" + (channel == HoYoChannel.Bilibili ? "0" : "1"), "cps=" + (channel == HoYoChannel.Bilibili ? "bilibili" : "mihoyo") };
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
    public Task<HoYoRelease?> GetReleaseAsync(string gameId, HoYoChannel channel, CancellationToken token = default)
        => provider.GetReleaseAsync(gameId, channel, token);
    private async Task<HoYoPackage> PreparePackageAsync(string gameId, HoYoInstallation installation, bool preload,
        IProgress<HoYoProgress>? progress, CancellationToken token)
    {
        if (installation.Channel == HoYoChannel.Unknown) throw new InvalidOperationException("请在游戏右键菜单中选择安装渠道。");
        progress?.Report(new("正在读取官方资源清单", 0, 0, 0, 0));
        var package = await provider.GetPackageAsync(gameId, installation.Channel, installation.AudioLanguages, preload, token);
        if (package.Sdk is not { } sdk) return package;
        var key = DownloadTaskCache.ContentKey(sdk.Md5, sdk.Size);
        var path = SafeGamePath.Resolve(dataRoot, "hoyo/objects/" + key);
        CheckSpace(dataRoot, sdk.Size);
        await _tasks.TrackAsync(TaskName(installation, preload), [key], token);
        long sdkTransferred = 0;
        progress?.Report(new("正在下载渠道组件", 0, 1, 0, sdk.Size, IsNetworkTransfer: true, TransferredBytes: 0));
        await download.DownloadAsync(sdk.Uri, sdk.Md5, sdk.Size, async refreshToken =>
        {
            var refreshed = await provider.GetPackageAsync(gameId, installation.Channel, installation.AudioLanguages, preload, refreshToken);
            if (refreshed.Sdk is not { } replacement || replacement.Md5 != sdk.Md5 || replacement.Size != sdk.Size)
                throw new InvalidOperationException("渠道组件已变化，请重新检查。");
            return replacement.Uri;
        }, new ByteProgress(bytes => progress?.Report(new("正在下载渠道组件", 0, 1, bytes, sdk.Size,
            IsNetworkTransfer: true, TransferredBytes: sdkTransferred))), token,
            new ByteProgress(bytes => sdkTransferred += bytes));
        var sdkProgress = new FileProgress(progress, "正在校验渠道组件", 1, sdk.Size);
        var components = await HoYoSdkArchive.ReadAsync(path, sdk, token, sdkProgress);
        sdkProgress.Complete(sdk.Size);
        var files = package.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var removed = HoYoSdkArchive.ReadRemovedFiles(path);
        var prefix = Path.GetFileNameWithoutExtension(package.Release.ExecutableName) + "_Data/Plugins/";
        if (components.Any(f => f.Path != sdk.VersionFile && !f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || removed.Any(p => !p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("渠道组件路径与当前游戏不符。");
        foreach (var obsolete in removed) files.Remove(obsolete);
        foreach (var file in components) files[file.Path] = file;
        if (removed.Any(files.ContainsKey)) throw new InvalidDataException("渠道组件清单与删除列表冲突。");
        var ordered = files.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", ordered.Select(f => $"{f.Path}|{f.Size}|{f.Md5}")) + "\nremoved:" + string.Join("|", removed))));
        return new(package.Release, ordered, fingerprint, sdk, removed);
    }
    private static ResourceSharingService SharingEngine(string gameId)
        => new(path => HoYoSharingPolicy.IsShareable(gameId, path), () => EnsureNotRunning(gameId));
    public async Task<EndfieldSharingSummary> OptimizeAsync(string gameId, HoYoInstallation target, HoYoInstallation source, CancellationToken token = default)
    {
        using var storage = LocalStorageGate.BeginOperation(dataRoot);
        var root = target.InstallRoot ?? throw new InvalidOperationException("请先设置当前服目录。");
        var peer = source.InstallRoot ?? throw new InvalidOperationException("请先设置另一服目录。");
        HoYoSharingPolicy.ValidateIndependent(root, peer);
        await using var lease = await LockRootsAsync(root, peer, token);
        EnsureNotRunning(gameId);
        var first = await provider.GetPackageAsync(gameId, source.Channel, source.AudioLanguages, false, token);
        var second = await provider.GetPackageAsync(gameId, target.Channel, target.AudioLanguages, false, token);
        ValidateExistingRoot(peer, first.Release.ExecutableName, source.Channel);
        ValidateExistingRoot(root, second.Release.ExecutableName, target.Channel);
        return await SharingEngine(gameId).OptimizeAsync(peer, root, first.Files.Select(f => f.Integrity).ToArray(), second.Files.Select(f => f.Integrity).ToArray(), token);
    }
    public EndfieldSharingSummary ScanSharing(string gameId, HoYoInstallation first, HoYoInstallation second)
        => SharingEngine(gameId).ScanLocal(first.InstallRoot!, second.InstallRoot!);
    public async Task<int> UnshareAsync(string gameId, HoYoInstallation install, HoYoInstallation? peer, CancellationToken token = default)
    {
        using var storage = LocalStorageGate.BeginOperation(dataRoot);
        var root = install.InstallRoot ?? throw new InvalidOperationException("请先设置安装目录。");
        await using var lease = await LockRootsAsync(root, peer?.InstallRoot, token);
        EnsureNotRunning(gameId);
        return await SharingEngine(gameId).UnshareAsync(root, [], token);
    }
    private async Task<RootLeases> LockRootsAsync(string root, string? peer, CancellationToken token)
    {
        var leases = new List<Lease>();
        try
        {
            foreach (var path in new[] { root, peer }.Where(p => p is not null).Select(p => Path.GetFullPath(p!))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                leases.Add(await LockAsync(path, token));
            return new(leases);
        }
        catch { foreach (var lease in leases) await lease.DisposeAsync(); throw; }
    }
    private sealed class RootLeases(List<Lease> leases) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { foreach (var lease in leases.AsEnumerable().Reverse()) await lease.DisposeAsync(); }
    }
    // Bytes measure work completed in this stage; skipped/missing files still count as inspected.
    private sealed class FileProgress : IProgress<long>
    {
        private readonly IProgress<HoYoProgress>? _progress;
        private readonly string _stage;
        private readonly int _totalFiles;
        private readonly long _totalBytes;
        private int _completedFiles;
        private long _completedBytes;
        private string? _file;

        public FileProgress(IProgress<HoYoProgress>? progress, string stage, int totalFiles, long totalBytes)
        {
            _progress = progress; _stage = stage; _totalFiles = totalFiles; _totalBytes = totalBytes;
            Report(0);
        }
        public void Begin(string? file) { _file = file; Report(0); }
        public void Report(long bytes) => _progress?.Report(new(_stage, _completedFiles, _totalFiles,
            Math.Min(_totalBytes, _completedBytes + bytes), _totalBytes, _file));
        public void Complete(long size) { _completedFiles++; _completedBytes += size; Report(0); }
    }
    private sealed class ByteProgress(Action<long> action) : IProgress<long> { public void Report(long value) => action(value); }
}