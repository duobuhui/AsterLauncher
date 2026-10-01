using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

public sealed class EndfieldMaintenanceService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RootGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IEndfieldDistributionProvider _provider;
    private readonly EndfieldDownloadService _downloads;
    private readonly EndfieldSharingService _sharing;
    private readonly string _dataRoot;
    private readonly EndfieldArchiveService? _archives;

    public EndfieldMaintenanceService(IEndfieldDistributionProvider provider,
        EndfieldDownloadService downloads, EndfieldSharingService sharing, string dataRoot,
        EndfieldArchiveService? archives = null)
    {
        _provider = provider;
        _downloads = downloads;
        _sharing = sharing;
        _dataRoot = dataRoot;
        _archives = archives;
    }

    public Task<string> GetLatestVersionAsync(
        EndfieldInstallation installation, CancellationToken cancellationToken = default) =>
        _provider.GetLatestVersionAsync(installation.Channel, installation.InstalledVersion, cancellationToken);
    public async Task<(EndfieldPackage Package, EndfieldRepairPlan Plan)> PlanAsync(
        EndfieldInstallation installation, CancellationToken cancellationToken = default)
    {
        var package = await _provider.GetPackageAsync(
            installation.Channel, installation.InstalledVersion, cancellationToken);
        if (package.Channel != installation.Channel || string.IsNullOrWhiteSpace(package.Version) || package.Files.Count == 0)
            throw new InvalidDataException("官方完整清单为空或渠道不一致。");
        var missing = new List<EndfieldManifestFile>();
        var corrupt = new List<EndfieldManifestFile>();
        if (string.IsNullOrWhiteSpace(installation.InstallRoot))
        {
            missing.AddRange(package.Files);
        }
        else
        {
            foreach (var file in package.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = SafeGamePath.Resolve(installation.InstallRoot, file.Path);

                if (!File.Exists(path)) missing.Add(file);
                else if (!await VerifiedFileCommit.MatchesAsync(path, file, cancellationToken)) corrupt.Add(file);
            }
        }
        return (package, new EndfieldRepairPlan(installation.Channel, package.Version,
            missing, corrupt, missing.Sum(file => file.Size) + corrupt.Sum(file => file.Size)));
    }

    public async Task<EndfieldRepairPlan> SyncAsync(
        EndfieldInstallation installation, EndfieldInstallation? otherChannel,
        bool repairOnly, IProgress<EndfieldMaintenanceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = LocalStorageGate.BeginOperation(_dataRoot);
        var root = RequireRoot(installation);
        var lockRoots = otherChannel?.InstallRoot is { Length: > 0 } otherRoot
            ? new[] { root, otherRoot } : new[] { root };
        await using var locks = await AcquireAsync(lockRoots, cancellationToken);
        EnsureNotRunning();

        Directory.CreateDirectory(root);
        var journal = SafeGamePath.Resolve(root, ".aster-maintenance.json");
        if (File.Exists(journal))
        {
            var existing = JsonSerializer.Deserialize<MaintenanceRecord>(await File.ReadAllTextAsync(journal, cancellationToken));
            if (existing is null || existing.InstallationId != installation.InstallationId || existing.Channel != installation.Channel)
                throw new InvalidOperationException("此目录有其他安装实例的未完成维护记录，请先恢复原实例。");
        }
        var defects = new List<EndfieldManifestFile>();
        long bytes = 0;
        var completedFiles = 0;
        long completedBytes = 0;
        Report(EndfieldMaintenanceStage.Checking, "正在读取官方完整清单");
        var (package, plan) = await PlanAsync(installation, cancellationToken);
        if (repairOnly && !string.Equals(installation.InstalledVersion, package.Version, StringComparison.Ordinal))
            throw new InvalidOperationException("当前安装不是最新版本；需要更新，不能用最新清单冒充旧版修复。");

        if (otherChannel is { InstallRoot: { Length: > 0 } sourceRoot }
            && Directory.Exists(sourceRoot) && WindowsHardLink.CanShareVolume(sourceRoot, root))
        {
            try
            {
                var other = await _provider.GetPackageAsync(otherChannel.Channel,
                    otherChannel.InstalledVersion, cancellationToken);
                await _sharing.OptimizeAsync(sourceRoot, root, other.Files, package.Files, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) { /* Continue with independent downloads. */ }
            catch (InvalidDataException) { /* Continue with independent downloads. */ }
        }

        // Recompute after sharing; this is also the final source-of-truth before downloads.
        Report(EndfieldMaintenanceStage.Verifying, "正在校验已有文件");
        foreach (var file in package.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = SafeGamePath.Resolve(root, file.Path);

            if (!await VerifiedFileCommit.MatchesAsync(path, file, cancellationToken)) defects.Add(file);
        }

        bytes = defects.Sum(file => file.Size);
        await WriteJournalAsync(journal, new MaintenanceRecord(installation.InstallationId, installation.Channel,
            installation.InstalledVersion, package.Version, package.ManifestMd5,false), cancellationToken);
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        var cacheDrive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_dataRoot))!);
        var patchBytes = repairOnly ? 0 : package.Patch?.Packs.Sum(p => p.Size) ?? 0;
        var cacheRequired = checked(bytes + patchBytes);
        await new DownloadTaskCache(_dataRoot).TrackAsync(TaskKey(installation,false),
            defects.Select(f=>DownloadTaskCache.ContentKey(f.Md5,f.Size))
                .Concat(repairOnly ? [] : package.Patch?.Packs.Select(p=>DownloadTaskCache.ContentKey(p.Md5,p.Size)) ?? []),cancellationToken);
        var targetRequired = checked(bytes + defects.Select(f => f.Size).DefaultIfEmpty().Max()
            + (patchBytes > 0 ? bytes + patchBytes : 0));
        var sameVolume = drive.Name.Equals(cacheDrive.Name, StringComparison.OrdinalIgnoreCase);
        var required = checked(targetRequired + (sameVolume ? cacheRequired : 0));
        if (drive.AvailableFreeSpace < required || (!sameVolume && cacheDrive.AvailableFreeSpace < cacheRequired))
            throw new IOException($"安装或缓存卷空间不足：目标卷预计需要 {required:N0} 字节，缓存需 {cacheRequired:N0} 字节。");
        var cached = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var patchStage = SafeGamePath.Resolve(root, ".aster-patch-" + Guid.NewGuid().ToString("N"));
        try
        {
        if (!repairOnly && package.Patch is { } patch && patch.TargetVersion == package.Version
            && !string.IsNullOrEmpty(installation.InstalledVersion) && _archives is not null)
        {
            Report(EndfieldMaintenanceStage.Extracting, "正在核对正式补丁并复用已验证缓存");
            foreach (var pair in await StagePatchAsync(installation, package, defects, patchStage, progress, cancellationToken))
                cached[pair.Key] = pair.Value;
        }
        var remaining = defects.Where(f => !cached.ContainsKey(f.Path)).ToArray();
        bytes = remaining.Sum(f => f.Size);
        Report(EndfieldMaintenanceStage.Downloading, remaining.Length == 0 ? "无需下载"
            : package.Patch is null ? "正在下载缺失或损坏的文件" : "未复用的增量资源改用目标完整清单同步");
        var transferBytes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long lastProgressAt = 0;
        await Parallel.ForEachAsync(remaining,
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancellationToken },
            async (file, token) =>
            {
                var uri = EndfieldDownloadService.FileUri(package.FileBaseUri, file.Path);
                var path = await _downloads.DownloadAsync(uri, file.Md5, file.Size,
                    async refreshToken =>
                    {
                        var refreshed = await _provider.GetPackageAsync(installation.Channel,
                            installation.InstalledVersion, refreshToken);
                        var current = refreshed.Files.FirstOrDefault(item =>
                            item.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
                        if (current is null || current.Size != file.Size
                            || !current.Md5.Equals(file.Md5, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Publisher changed the content while downloading.");
                        return EndfieldDownloadService.FileUri(refreshed.FileBaseUri, file.Path);
                    }, new InlineProgress<long>(value =>
                    {
                        transferBytes[file.Path] = value;
                        var now = Environment.TickCount64;
                        if (now - Volatile.Read(ref lastProgressAt) < 200) return;
                        Interlocked.Exchange(ref lastProgressAt, now);
                        progress?.Report(new EndfieldMaintenanceProgress(installation.Channel,
                            EndfieldMaintenanceStage.Downloading, Volatile.Read(ref completedFiles), remaining.Length,
                            transferBytes.Values.Sum(), bytes, "正在下载官方资源"));
                    }), cancellationToken: token);
                transferBytes[file.Path] = file.Size;
                cached[file.Path] = path;
                var done = Interlocked.Increment(ref completedFiles);
                var total = Interlocked.Add(ref completedBytes, file.Size);
                progress?.Report(new EndfieldMaintenanceProgress(
                    installation.Channel, EndfieldMaintenanceStage.Downloading,
                    done, remaining.Length, transferBytes.Values.Sum(), bytes, "正在下载"));
            });

        await WriteJournalAsync(journal, new MaintenanceRecord(installation.InstallationId, installation.Channel,
            installation.InstalledVersion, package.Version, package.ManifestMd5,true), cancellationToken);
        Report(EndfieldMaintenanceStage.Applying, "正在安全替换当前渠道文件");
        foreach (var file in defects.Where(f => !IsChannelStateFile(f.Path)))
        {
            EnsureNotRunning();
            cancellationToken.ThrowIfCancellationRequested();
            var destination = SafeGamePath.Resolve(root, file.Path);
            await VerifiedFileCommit.CommitAsync(cached[file.Path], destination, file, cancellationToken);
        }

        Report(EndfieldMaintenanceStage.FinalVerification, "正在复验完整安装");
        foreach (var file in package.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = SafeGamePath.Resolve(root, file.Path);
            if (IsChannelStateFile(file.Path)) continue;
            if (!await VerifiedFileCommit.MatchesAsync(destination, file, cancellationToken))
                throw new InvalidDataException("Final game integrity check failed.");
        }
        var exe = SafeGamePath.Resolve(root, "Endfield.exe");
        if (!File.Exists(exe)) throw new InvalidDataException("Verified manifest did not install Endfield.exe.");

        EnsureNotRunning();
        var currentVersion = await _provider.GetLatestVersionAsync(installation.Channel, null, cancellationToken);
        if (currentVersion != package.Version)
            throw new InvalidOperationException("官方目标版本已经变化，保留已验证文件；请重新检查并继续下载。");
        foreach (var file in package.Files.Where(f => IsChannelStateFile(f.Path)))
        {
            EnsureNotRunning();
            var destination = SafeGamePath.Resolve(root, file.Path);
            if (cached.TryGetValue(file.Path, out var config))
                await VerifiedFileCommit.CommitAsync(config, destination, file, cancellationToken);
            if (!await VerifiedFileCommit.MatchesAsync(destination, file, cancellationToken))
                throw new InvalidDataException("渠道状态文件复验失败。");
        }
        if (package.Files.Any(f => IsChannelStateFile(f.Path)))
        {
            var info = await EndfieldInstallationInfo.ReadAsync(root, cancellationToken);
            if (info is null || info.Value.Channel != installation.Channel || info.Value.Version != package.Version)
                throw new InvalidDataException("渠道状态文件与目标安装不一致。");
        }
        // The caller persists this only after every file passed final verification.
        installation.ExecutablePath = exe;
        installation.InstalledVersion = package.Version;
        installation.MaintenanceInProgress = false;
        var consumedPreload=installation.PreloadSourceVersion != package.Version;
        if(consumedPreload)
        { installation.PreloadState=EndfieldPreloadState.NotOpen; installation.PreloadSourceVersion=null; installation.PreloadTargetVersion=null; installation.PreloadContentHash=null; }
        File.Delete(journal);
        try
        {
            var cache=new DownloadTaskCache(_dataRoot);
            await cache.CompleteAsync(TaskKey(installation,false));
            if(consumedPreload)await cache.CompleteAsync(TaskKey(installation,true));
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { /* Verified game remains installed; damaged cache indexes stay protected for recovery. */ }
        Report(EndfieldMaintenanceStage.Completed, "安装文件已完成校验");
        return plan;
        }
        finally
        {
            if (Directory.Exists(patchStage))
            {
                SafeGamePath.Resolve(root, Path.GetFileName(patchStage) + "/probe");
                Directory.Delete(patchStage, recursive: true);
            }
        }

        void Report(EndfieldMaintenanceStage stage, string message) =>
            progress?.Report(new EndfieldMaintenanceProgress(installation.Channel, stage,
                completedFiles, defects?.Count ?? 0, completedBytes, bytes, message));
    }

    public async Task<EndfieldSharingSummary> OptimizeAsync(
        EndfieldInstallation first, EndfieldInstallation second,
        CancellationToken cancellationToken = default)
    {
        var firstRoot = RequireRoot(first);
        var secondRoot = RequireRoot(second);
        await using var locks = await AcquireAsync([firstRoot, secondRoot], cancellationToken);
        EnsureNotRunning();
        var firstPackage = await _provider.GetPackageAsync(first.Channel, first.InstalledVersion, cancellationToken);
        var secondPackage = await _provider.GetPackageAsync(second.Channel, second.InstalledVersion, cancellationToken);
        return await _sharing.OptimizeAsync(firstRoot, secondRoot,
            firstPackage.Files, secondPackage.Files, cancellationToken);
    }

    public async Task<EndfieldSharingSummary> GetSharingAsync(
        EndfieldInstallation first, EndfieldInstallation second,
        CancellationToken cancellationToken = default)
    {
        var firstRoot = RequireRoot(first);
        var secondRoot = RequireRoot(second);
        await using var locks = await AcquireAsync([firstRoot, secondRoot], cancellationToken);
        return _sharing.ScanLocal(firstRoot, secondRoot, cancellationToken);
    }
    public async Task<int> UnshareAsync(EndfieldInstallation installation,
        CancellationToken cancellationToken = default)
    {
        var root = RequireRoot(installation);
        await using var locks = await AcquireAsync([root], cancellationToken);
        EnsureNotRunning();
        return await _sharing.UnshareAsync(root, [], cancellationToken);
    }

    public static string TaskKey(EndfieldInstallation installation,bool preload)
        => installation.InstallationId.ToString("N")+(preload?"-preload":"-install");
    public async Task<DownloadCacheCleanup> CancelTaskAsync(EndfieldInstallation installation,bool preload)
    {
        using var operation=LocalStorageGate.BeginOperation(_dataRoot);
        var root=installation.InstallRoot;
        await using var locks=await AcquireAsync(string.IsNullOrWhiteSpace(root)?[]:[root],CancellationToken.None);
        MaintenanceRecord? record=null;string? journal=null;
        if(!preload&&!string.IsNullOrWhiteSpace(root))
        {
            journal=SafeGamePath.Resolve(root,".aster-maintenance.json");
            if(File.Exists(journal))
            {
                try{record=JsonSerializer.Deserialize<MaintenanceRecord>(await File.ReadAllTextAsync(journal));}
                catch(JsonException ex){throw new InvalidDataException("维护记录无法解析，保留恢复标记和缓存。",ex);}
                if(record?.InstallationId!=installation.InstallationId||record.Channel!=installation.Channel)
                    throw new InvalidOperationException("维护记录属于另一安装，未删除。");
            }
        }
        var cleanup=await new DownloadTaskCache(_dataRoot).CancelAsync(TaskKey(installation,preload));
        if(preload)
        {
            installation.PreloadState=EndfieldPreloadState.Available;
            installation.PreloadSourceVersion=null;installation.PreloadTargetVersion=null;installation.PreloadContentHash=null;
        }
        else if(record is null||record.Applying==false)
        {
            if(journal is not null&&File.Exists(journal))File.Delete(journal);
            installation.MaintenanceInProgress=false;
        }
        // Once replacement began (or a legacy marker has no stage), retain recovery state.
        return cleanup;
    }
    public async Task<EndfieldPatch?> CheckPreloadAsync(EndfieldInstallation installation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installation.InstalledVersion)) return null;
        var package = await _provider.GetPackageAsync(installation.Channel,
            installation.InstalledVersion, cancellationToken);
        return package.PrePatch;
    }

    public async Task PreloadAsync(EndfieldInstallation installation, EndfieldPatch patch,
        IProgress<EndfieldMaintenanceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = LocalStorageGate.BeginOperation(_dataRoot);
        var sourceVersion = installation.InstalledVersion
            ?? throw new InvalidOperationException("Pre-download requires an installed source version.");
        var channel = installation.Channel;
        var currentOffer = await CheckPreloadAsync(installation, cancellationToken);
        if (!SamePreloadContent(patch, currentOffer)
            || !string.Equals(installation.InstalledVersion, sourceVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The pre-download offer was replaced or withdrawn.");
        var count = 0;
        long bytes = 0;
        var total = patch.Packs.Sum(pack => pack.Size);
        await new DownloadTaskCache(_dataRoot).TrackAsync(TaskKey(installation,true),patch.Packs.Select(p=>DownloadTaskCache.ContentKey(p.Md5,p.Size)),cancellationToken);
        var transferBytes = new ConcurrentDictionary<string, long>();
        await Parallel.ForEachAsync(patch.Packs,
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancellationToken },
            async (pack, token) =>
            {
                await _downloads.DownloadAsync(pack.Uri, pack.Md5, pack.Size,
                    async refreshToken =>
                    {
                        var current = await CheckPreloadAsync(installation, refreshToken);
                        var match = current?.Packs.FirstOrDefault(item =>
                            item.Md5.Equals(pack.Md5, StringComparison.OrdinalIgnoreCase)
                            && item.Size == pack.Size);
                        return match?.Uri ?? throw new InvalidDataException("Pre-download offer changed.");
                    }, new InlineProgress<long>(value =>
                    {
                        transferBytes[pack.Md5] = value;
                        progress?.Report(new EndfieldMaintenanceProgress(installation.Channel,
                            EndfieldMaintenanceStage.Downloading, Volatile.Read(ref count), patch.Packs.Count,
                            transferBytes.Values.Sum(), total, "正在预下载官方资源"));
                    }), cancellationToken: token);
                var done = Interlocked.Increment(ref count);
                var currentBytes = Interlocked.Add(ref bytes, pack.Size);
                progress?.Report(new EndfieldMaintenanceProgress(channel,
                    EndfieldMaintenanceStage.Downloading, done, patch.Packs.Count,
                    currentBytes, total, "预下载文件已校验"));
            });
        currentOffer = await CheckPreloadAsync(installation, cancellationToken);
        if (!SamePreloadContent(patch, currentOffer)
            || !string.Equals(installation.InstalledVersion, sourceVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The pre-download offer changed before completion.");
        installation.PreloadState = EndfieldPreloadState.Completed;
        installation.PreloadSourceVersion = sourceVersion;
        installation.PreloadTargetVersion = patch.TargetVersion;
        installation.PreloadContentHash = patch.ContentMd5
            ?? string.Join("-", patch.Packs.Select(item => item.Md5));
    }

    private sealed record MaintenanceRecord(Guid InstallationId, EndfieldChannel Channel,
        string? SourceVersion, string TargetVersion, string ManifestMd5, bool? Applying = null);
    private static async Task WriteJournalAsync(string path, MaintenanceRecord record, CancellationToken token)
    {
        var temp = SafeGamePath.Resolve(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".tmp");
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(record), token);
        File.Move(temp, path, true);
    }
    private async Task<IReadOnlyDictionary<string, string>> StagePatchAsync(EndfieldInstallation install,
        EndfieldPackage package, IReadOnlyList<EndfieldManifestFile> defects, string stage, IProgress<EndfieldMaintenanceProgress>? progress, CancellationToken token)
    {
        var patch = package.Patch!;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // V2 mapping and HDiff payloads are never applied in-place; unsupported outputs use target-file sync.
        // Full files inside a patch can still be reused after matching the authoritative target manifest.
        if (patch.Packs.Sum(p => p.Size) > defects.Sum(f => f.Size)
            && install.PreloadState != EndfieldPreloadState.Completed) return result;
        var paths = new List<string>();
        long patchDone=0;var patchTotal=patch.Packs.Sum(p=>p.Size);
        foreach(var pack in patch.Packs)
        {
            paths.Add(await _downloads.DownloadAsync(pack.Uri,pack.Md5,pack.Size,async refreshToken=>
            {
                var fresh=await _provider.GetPackageAsync(install.Channel,install.InstalledVersion,refreshToken);
                if(fresh.Version!=package.Version||!SamePreloadContent(patch,fresh.Patch))throw new InvalidDataException("正式补丁已被替换。");
                return fresh.Patch!.Packs.First(p=>p.Md5==pack.Md5&&p.Size==pack.Size).Uri;
            },new InlineProgress<long>(value=>progress?.Report(new EndfieldMaintenanceProgress(install.Channel,
                EndfieldMaintenanceStage.Downloading,paths.Count,patch.Packs.Count,patchDone+value,patchTotal,"正在下载正式补丁"))),cancellationToken:token));
            patchDone+=pack.Size;
        }        try
        {
            var split = patch.Packs.All(p => System.Text.RegularExpressions.Regex.IsMatch(p.Uri.AbsolutePath, @"\.zip\.\d{3}$"));
            if (split)
            {
                var ordered = patch.Packs.Select((p, i) => (Number: int.Parse(p.Uri.AbsolutePath[^3..]), Path: paths[i]))
                    .OrderBy(p => p.Number).ToArray();
                if (!ordered.Select(p => p.Number).SequenceEqual(Enumerable.Range(1, ordered.Length)))
                    throw new InvalidDataException("补丁分卷不完整。");
                foreach (var pair in await _archives!.StageAsync(ordered.Select(p => p.Path).ToArray(), stage, defects, patch.CdKey, token))
                    result[pair.Key] = pair.Value;
            }
            else
            {
                for (var i = 0; i < paths.Count; i++)
                    foreach (var pair in await _archives!.StageAsync([paths[i]], Path.Combine(stage, i.ToString()), defects, patch.CdKey, token))
                        result[pair.Key] = pair.Value;
            }
        }
        catch (InvalidDataException) { result.Clear(); } // Explicit target-manifest fallback, no success version yet.
        return result;
    }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
    private static bool SamePreloadContent(EndfieldPatch expected, EndfieldPatch? current)
    {
        if (current is null
            || !string.Equals(expected.TargetVersion, current.TargetVersion, StringComparison.Ordinal)
            || expected.Packs.Count != current.Packs.Count) return false;
        var first = expected.Packs.OrderBy(pack => pack.Md5, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pack => pack.Size).ToArray();
        var second = current.Packs.OrderBy(pack => pack.Md5, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pack => pack.Size).ToArray();
        return first.Zip(second).All(pair =>
            pair.First.Size == pair.Second.Size
            && pair.First.Md5.Equals(pair.Second.Md5, StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsChannelStateFile(string relative)
    {
        var name = Path.GetFileName(relative.Replace('/', Path.DirectorySeparatorChar));
        return name.Equals("config.ini", StringComparison.OrdinalIgnoreCase)
            ;
    }

    private static string RequireRoot(EndfieldInstallation installation) =>
        !string.IsNullOrWhiteSpace(installation.InstallRoot)
            ? Path.GetFullPath(installation.InstallRoot)
            : throw new InvalidOperationException("Choose a separate install directory for this channel.");

    internal static void EnsureNotRunning()
    {
        using var processes = new ProcessCollection(Process.GetProcessesByName("Endfield"));
        if (processes.Count > 0)
            throw new InvalidOperationException("Close both Endfield clients before maintaining or sharing files.");
    }

    private sealed class ProcessCollection : IDisposable
    {
        private readonly Process[] _items;
        public ProcessCollection(Process[] items) => _items = items;
        public int Count => _items.Length;
        public void Dispose() { foreach (var item in _items) item.Dispose(); }
    }

    private async Task<RootLease> AcquireAsync(IEnumerable<string> roots, CancellationToken cancellationToken)
    {
        var sorted = roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var gates = new List<SemaphoreSlim>();
        var files = new List<FileStream>();
        try
        {
            var lockFolder = Path.Combine(_dataRoot, "endfield", "locks");
            Directory.CreateDirectory(lockFolder);
            foreach (var root in sorted)
            {
                var gate = RootGates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken);
                gates.Add(gate);
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root))).ToLowerInvariant();
                files.Add(new FileStream(Path.Combine(lockFolder, key + ".lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            return new RootLease(gates, files);
        }
        catch
        {
            foreach (var file in files) file.Dispose();
            foreach (var gate in gates) gate.Release();
            throw;
        }
    }

    private sealed class RootLease : IAsyncDisposable
    {
        private readonly IReadOnlyList<SemaphoreSlim> _gates;
        private readonly IReadOnlyList<FileStream> _files;
        public RootLease(IReadOnlyList<SemaphoreSlim> gates, IReadOnlyList<FileStream> files)
        {
            _gates = gates;
            _files = files;
        }
        public ValueTask DisposeAsync()
        {
            foreach (var file in _files) file.Dispose();
            foreach (var gate in _gates) gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
