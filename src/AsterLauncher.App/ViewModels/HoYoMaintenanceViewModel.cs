using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using Microsoft.UI.Xaml;

namespace AsterLauncher.App.ViewModels;

public sealed class HoYoMaintenanceViewModel : ObservableObject
{
    private sealed class State
    {
        public HoYoRelease? Release;
        public HoYoPlan? Plan;
        public string Remote = "尚未检查", Stage = "", PlanText = "检查文件后显示缺失、损坏数量和下载量。", Preload = "尚未检查";
        public string Download = "", Speed = "", Sharing = "尚未扫描共享资源";
        public double Progress;
        public bool Busy, Checking, Downloading, Preloading, Cleaning, ReadImportedAudio, Paused, LastWasPreload;
        public CancellationTokenSource? Cancellation;
        public TaskCompletionSource? Completion;
        public DateTimeOffset LastProgress = DateTimeOffset.MinValue, LastSpeed = DateTimeOffset.UtcNow;
        public long LastBytes;
        public long Revision;
    }
    private readonly LauncherViewModel _launcher;
    private readonly HoYoMaintenanceService _service;
    private readonly Dictionary<Guid, State> _states = [];
    private readonly State _empty = new();
    public HoYoMaintenanceViewModel(LauncherViewModel launcher, HoYoMaintenanceService service)
    {
        _launcher = launcher; _service = service;
        launcher.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(LauncherViewModel.CurrentGame) or nameof(LauncherViewModel.CanLaunch)) Refresh(); };
    }
    public bool IsSupported => _launcher.CurrentGame is { } game && HoYoDistributionProvider.Games.ContainsKey(game.Id);
    private State Current => _launcher.CurrentGame is { } game ? GetState(game) : _empty;
    private State GetState(GameCardViewModel game) => GetState(Installation(game));
    private State GetState(HoYoInstallation install) => _states.TryGetValue(install.InstallationId, out var state) ? state
        : _states[install.InstallationId] = new() { ReadImportedAudio = install.ExecutablePath is not null };
    private HoYoInstallation Installation(GameCardViewModel game)
        => game.State.HoYoInstallation ??= HoYoInstallationIdentity.Get(game.State, game.State.SelectedHoYoChannel);
    private bool IsSelected(GameCardViewModel game, HoYoInstallation install)
        => ReferenceEquals(game, _launcher.CurrentGame) && ReferenceEquals(install, game.State.HoYoInstallation);
    public string ChannelText => _launcher.CurrentGame is { } game ? Installation(game).Channel switch
        { HoYoChannel.Bilibili => "哔哩哔哩服", HoYoChannel.Official => "国服官服", _ => "渠道未确认，请在游戏右键菜单中选择" } : "";
    public string SuggestedRoot => _launcher.CurrentGame is { } game && Installation(game).InstallRoot is { } root ? root
        : string.IsNullOrWhiteSpace(_launcher.GameDownloadDirectory) ? ""
        : Path.Combine(_launcher.GameDownloadDirectory, Current.Release?.DirectoryName ?? _launcher.CurrentGame!.DisplayName,
            Installation(_launcher.CurrentGame!).Channel == HoYoChannel.Bilibili ? "Bilibili" : "Official");
    public bool ShareResources { get => _launcher.CurrentGame is { } game && Installation(game).ShareResources;
        set { if (_launcher.CurrentGame is { } game) Installation(game).ShareResources = value; } }
    public string SharingText => Current.Sharing;
    public string Title => (_launcher.CurrentGame?.DisplayName ?? "") + " · 安装与修复";
    public string InstallRoot => _launcher.CurrentGame is { } game && Installation(game).InstallRoot is { Length: > 0 } root ? root
        : string.IsNullOrWhiteSpace(_launcher.GameDownloadDirectory) ? "请先在设置中指定游戏下载目录"
        : SuggestedRoot + "（安装时创建）";
    public string LocalVersion => _launcher.CurrentGame is { } game ? Installation(game).InstalledVersion ?? "未安装 / 未确认" : "";
    public string RemoteVersion => Current.Remote;
    public string StageText => Current.Stage;
    public string PlanText => Current.PlanText;
    public string PreloadText => Current.Preload;
    public string DownloadText => Current.Download;
    public string SpeedText => Current.Speed;
    public double Progress => Current.Progress;
    public bool CanSetRoot => !Current.Busy && !Current.Cleaning;
    public bool CanRepair => !Current.Busy && !Current.Cleaning && _launcher.CurrentGame?.IsInstalled == true;
    public bool CanPreload => !Current.Busy && !Current.Cleaning && Current.Release?.PreloadVersion is not null;
    public bool IsPrimaryDownloading => Current.Downloading;
    public Visibility DownloadVisibility => IsSupported && (Current.Downloading || Current.Preloading || Current.Paused) ? Visibility.Visible : Visibility.Collapsed;
    public bool IsIndeterminate => Current.Busy && !Current.Stage.StartsWith("正在下载");
    public string PrimaryActionGlyph => Current.Downloading ? "\uE769" : StartsMaintenance ? "\uE896" : "\uE768";
    public bool StartsMaintenance => IsSupported && !Current.Busy && !Current.Cleaning && _launcher.CurrentGame is { } game
        && (!game.IsInstalled || Installation(game).MaintenanceInProgress || Installation(game).AudioSelectionPending || Current.Release is { } release && LocalVersion != release.Version);
    public string PrimaryActionText => Current.Downloading ? Current.Stage.StartsWith("正在下载") ? $"暂停下载 {Progress:0}%" : Current.Stage
        : Current.Paused ? "继续下载"
        : _launcher.CurrentGame is { } game && Installation(game).MaintenanceInProgress ? "继续下载"
        : _launcher.CurrentGame is { } audioGame && Installation(audioGame).AudioSelectionPending && gameInstalled() ? "下载所选语音"
        : Current.Release is { } release && gameInstalled() && LocalVersion != release.Version ? "更新游戏"
        : !gameInstalled() ? Current.Remote == "未开放下载" ? "未开放下载" : "下载游戏" : _launcher.LaunchButtonText;
    private bool gameInstalled() => _launcher.CurrentGame?.IsInstalled == true;
    public bool ChineseAudio { get => HasAudio("zh-cn"); set => SetAudio("zh-cn", value); }
    public bool EnglishAudio { get => HasAudio("en-us"); set => SetAudio("en-us", value); }
    public bool JapaneseAudio { get => HasAudio("ja-jp"); set => SetAudio("ja-jp", value); }
    public bool KoreanAudio { get => HasAudio("ko-kr"); set => SetAudio("ko-kr", value); }
    private bool HasAudio(string language) => _launcher.CurrentGame is { } game && Installation(game).AudioLanguages.Contains(language);
    private async void SetAudio(string language, bool selected)
    {
        if (_launcher.CurrentGame is not { } game || Current.Busy || Current.Cleaning) return;
        var install = Installation(game); var languages = install.AudioLanguages;
        if (languages.Contains(language) == selected) return;
        Current.ReadImportedAudio = false;
        if (selected && !languages.Contains(language)) languages.Add(language);
        else if (!selected) languages.Remove(language);
        Current.Plan = null; Installation(game).AudioSelectionPending = true;
        try { await _launcher.PersistHoYoInstallationAsync(game); }
        catch { GetState(game).Stage = "语音选择保存失败，请重试。"; }
        if (IsSelected(game, install)) Refresh();
    }
    public async Task CheckVersionAsync()
    {
        if (!IsSupported || _launcher.CurrentGame is not { } game) return;
        var install = Installation(game); var state = GetState(install); if (state.Checking || state.Busy) return;
        state.Checking = true; state.Remote = "检查中…"; Refresh();
        try
        {
            var revision = state.Revision;
            var checkedVersion = await Task.Run(async () =>
            {
                var local = install.InstallRoot is { } root ? HoYoMaintenanceService.ReadVersion(root) : install.InstalledVersion;
                var pending = install.InstallRoot is { } path && File.Exists(SafeGamePath.Resolve(path, ".aster-hoyo-maintenance.json"));
                return (Release: await _service.GetReleaseAsync(game.Id, install.Channel), Local: local, Pending: pending);
            });
            if (state.Busy || revision != state.Revision) return;
            state.Release = checkedVersion.Release; install.InstalledVersion = checkedVersion.Local; install.MaintenanceInProgress = checkedVersion.Pending;
            state.Remote = state.Release?.Version ?? "未开放下载";
            if (state.ReadImportedAudio && install.ExecutablePath is { } existing && state.Release?.AudioRecordPath is { } record)
            {
                var path = SafeGamePath.Resolve(Path.GetDirectoryName(existing)!, record);
                if (File.Exists(path) && new FileInfo(path).Length < 64 * 1024)
                {
                    var text = await File.ReadAllTextAsync(path);
                    var languages = new[] { ("Chinese", "zh-cn"), ("English", "en-us"), ("Japanese", "ja-jp"), ("Korean", "ko-kr") }
                        .Where(pair => text.Contains(pair.Item1, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Item2).ToList();
                    if (languages.Count > 0)
                    {
                        install.AudioLanguages = languages;
                        await _launcher.PersistHoYoInstallationAsync(game, install);
                    }
                }
            }
            state.ReadImportedAudio = false;
            state.Preload = state.Release?.PreloadVersion is { } pre ? install.PreloadVersion == pre ? $"已缓存 {pre}" : $"可预下载至 {pre}" : "未开放";
            if (state.Release is { } release && install.InstalledVersion is { } local && local != release.Version)
                state.Stage = $"发现新版本 {release.Version}";
        }
        catch { state.Remote = "检查失败"; state.Preload = "检查失败"; state.Stage = "无法获取官方版本，请稍后重试。"; }
        finally { state.Checking = false; if (IsSelected(game, install)) Refresh(); }
    }
    public async Task SetRootAsync(string root)
    {
        if (!IsSupported || _launcher.CurrentGame is not { } game || Current.Busy || Current.Cleaning) return;
        var install = Installation(game); var state = GetState(install); var peer = _launcher.OtherHoYoInstallation(game);
        if (install.Channel == HoYoChannel.Unknown) throw new InvalidOperationException("请先在游戏右键菜单中选择官服或 B 服。");
        if (install.MaintenanceInProgress && !string.Equals(install.InstallRoot, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前目录有未完成维护，请完成或取消任务后再更改目录。");
        var full = Path.GetFullPath(root);
        var values = await Task.Run(async () =>
        {
            var release = await _service.GetReleaseAsync(game.Id, install.Channel) ?? throw new InvalidOperationException("官方尚未开放 PC 下载。");
            HoYoSharingPolicy.ValidateIndependent(full, peer?.InstallRoot);
            HoYoMaintenanceService.ValidateExistingRoot(full, release.ExecutableName, install.Channel);
            var exe = SafeGamePath.Resolve(full, release.ExecutableName);
            return (Release: release, Executable: File.Exists(exe) ? exe : null, Version: HoYoMaintenanceService.ReadVersion(full));
        });
        if (!IsSelected(game, install)) return;
        if (!string.Equals(install.InstallRoot, full, StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => _service.CancelAsync(install)); await Task.Run(() => _service.CancelAsync(install, true));
            install.PreloadContentHash = install.PreloadSourceVersion = install.PreloadVersion = null;
            install.InstallRoot = full; install.ChannelConfirmed = install.OfficialChannelConfirmed = false;
            state.Plan = null; state.ReadImportedAudio = values.Executable is not null;
        }
        install.ExecutablePath = values.Executable; install.InstalledVersion = values.Version;
        state.Release = values.Release; state.Remote = values.Release.Version;
        await _launcher.PersistHoYoInstallationAsync(game, install);
        Refresh();
    }
    public async Task CheckAsync()
    {
        if (_launcher.CurrentGame is not { } game || !IsSupported) return;
        var install = Installation(game); var state = GetState(install); var peer = _launcher.OtherHoYoInstallation(game);
        await RunAsync(game, install, false, false, async token =>
        {
            state.Plan = null;
            var ProgressForCaptured = ProgressFor(game, install, token);
            var plan = await Task.Run(async () =>
            {
                if (install.InstallRoot is { } root)
                    HoYoMaintenanceService.ValidateExistingRoot(root, (await _service.GetReleaseAsync(game.Id, install.Channel, token))?.ExecutableName ?? throw new InvalidOperationException("未开放下载。"), install.Channel);
                return await _service.PlanAsync(game.Id, install, false, token, peer, ProgressForCaptured);
            }, token);
            state.Plan = plan; state.Release = plan.Package.Release; state.Remote = plan.Package.Release.Version;
            state.PlanText = $"缺失 {plan.Missing.Count} · 损坏 {plan.Corrupt.Count} · 需下载 {Bytes(plan.DownloadBytes)} · 预留空间 {Bytes(plan.RequiredFreeBytes)}";
            state.Stage = "检查完成";
            state.Sharing = plan.SharingFallback ?? $"可复用 {plan.Reusable?.Count ?? 0} 个资源文件";
        });
    }
    public bool HasPlan => Current.Plan is not null;
    public bool NeedsChannelConfirmation => _launcher.CurrentGame is { IsInstalled: true } game && !(Installation(game).ChannelConfirmed || Installation(game).OfficialChannelConfirmed);
    public async Task ConfirmOfficialChannelAsync()
    {
        if (_launcher.CurrentGame is not { } game) return;
        var install = Installation(game);
        install.ChannelConfirmed = true; install.OfficialChannelConfirmed = install.Channel == HoYoChannel.Official;
        await _launcher.PersistHoYoInstallationAsync(game);
    }
    public async Task EnsureDefaultRootAsync()
    {
        if (_launcher.CurrentGame is not { } game) return;
        if (Installation(game).InstallRoot is null) await SetRootAsync(SuggestedRoot);
        var root = Installation(game).InstallRoot ?? throw new InvalidOperationException("请先在设置中指定游戏下载目录。");
        await Task.Run(() => Directory.CreateDirectory(root));
    }
    public async Task SyncAsync(bool repair)
    {
        if (_launcher.CurrentGame is not { } game || !IsSupported) return;
        var install = Installation(game); var state = GetState(install); var peer = _launcher.OtherHoYoInstallation(game);
        await RunAsync(game, install, true, false, async token =>
        {
            await _launcher.PersistHoYoInstallationAsync(game, install);
            try
            {
                var report = ProgressFor(game, install, token);
                await Task.Run(() => _service.SyncAsync(game.Id, install, repair, report, token, peer), token);
                state.Plan = null;
                state.PlanText = "缺失 0 · 损坏 0 · 无需下载"; state.Stage = "文件维护完成"; state.Progress = 100;
                if (peer?.InstallRoot is not null)
                {
                    var summary = await Task.Run(() => _service.ScanSharing(game.Id, install, peer), token);
                    state.Sharing = $"共享 {summary.FileCount} 个文件 · {Bytes(summary.SharedBytes)} · 预计节省 {Bytes(summary.EstimatedSavedBytes)}";
                }
            }
            finally { await _launcher.PersistHoYoInstallationAsync(game, install, !install.MaintenanceInProgress); }
        });
    }
    public async Task PreloadAsync()
    {
        if (_launcher.CurrentGame is not { } game || !CanPreload) return;
        var install = Installation(game); var state = GetState(install);
        await RunAsync(game, install, false, true, async token =>
        {
            var report = ProgressFor(game, install, token);
            await Task.Run(() => _service.PreloadAsync(game.Id, install, report, token), token);
            await _launcher.PersistHoYoInstallationAsync(game, install);
            state.Preload = $"已缓存 {install.PreloadVersion}"; state.Stage = "预下载完成";
        });
    }
    private IProgress<HoYoProgress> ProgressFor(GameCardViewModel game, HoYoInstallation install, CancellationToken token)
    {
        var state = GetState(install); var revision = state.Revision;
        return new MaintenanceProgress<HoYoProgress>(p =>
        {
            if (token.IsCancellationRequested || revision != state.Revision) return;
            var now = DateTimeOffset.UtcNow;
            if ((now - state.LastProgress).TotalMilliseconds < 100 && p.CompletedFiles != p.TotalFiles) return;
            state.LastProgress = now;
            state.Stage = p.Stage + (p.TotalFiles > 0 ? $" · {p.CompletedFiles}/{p.TotalFiles}" : "");
            state.Download = p.TotalBytes > 0 ? $"{Bytes(p.CompletedBytes)} / {Bytes(p.TotalBytes)}" : "";
            state.Progress = p.TotalBytes > 0 ? Math.Clamp(100d * p.CompletedBytes / p.TotalBytes, 0, 100) : p.TotalFiles > 0 ? 100d * p.CompletedFiles / p.TotalFiles : 0;
            var seconds = (now - state.LastSpeed).TotalSeconds;
            if (seconds >= 1) { state.Speed = p.TotalBytes > 0 ? $"{Bytes((long)(Math.Max(0, p.CompletedBytes - state.LastBytes) / seconds))}/s" : ""; state.LastBytes = p.CompletedBytes; state.LastSpeed = now; }
            if (IsSelected(game, install)) Refresh();
        }, p => p.Stage);
    }
    private async Task RunAsync(GameCardViewModel game, HoYoInstallation install, bool downloading, bool preloading, Func<CancellationToken, Task> action)
    {
        var state = GetState(install); if (state.Busy || state.Cleaning) return;
        state.Busy = true; state.Paused = false; state.LastWasPreload = preloading; state.Downloading = downloading; state.Preloading = preloading; state.Revision++;
        state.Cancellation = new(); state.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        state.LastBytes = 0; state.LastSpeed = DateTimeOffset.UtcNow; state.Stage = "正在检查";
        if (IsSelected(game, install)) Refresh();
        try { await action(state.Cancellation.Token); }
        catch (OperationCanceledException) { state.Paused = downloading || preloading; state.Stage = "已暂停，继续时复用已校验的下载内容。"; }
        catch (Exception ex) { state.Stage = ex is IOException or InvalidOperationException or InvalidDataException ? ex.Message : "维护未完成，请重新检查后继续。"; }
        finally
        {
            state.Revision++; state.Busy = state.Downloading = state.Preloading = false;
            state.Cancellation.Dispose(); state.Cancellation = null; state.Completion.TrySetResult();
            if (IsSelected(game, install)) Refresh();
        }
    }
    public void Pause() => Current.Cancellation?.Cancel();
    public async Task CancelAsync(bool preload = false)
    {
        if (_launcher.CurrentGame is not { } game) return;
        var install = Installation(game); var state = GetState(install);
        if (state.Cleaning) return;
        state.Cleaning = true; preload |= state.Preloading || state.Paused && state.LastWasPreload; state.Cancellation?.Cancel();
        if (state.Completion is { } completion) await completion.Task;
        try
        {
            var result = await Task.Run(() => _service.CancelAsync(install, preload));
            if (preload) { var i = install; i.PreloadContentHash = i.PreloadSourceVersion = i.PreloadVersion = null; state.Preload = "缓存已删除"; }
            state.Paused = false; state.Progress = 0; state.Download = state.Speed = "";
            state.Stage = $"已删除 {result.DeletedFiles} 个缓存文件，释放 {Bytes(result.DeletedBytes)}。";
            await _launcher.PersistHoYoInstallationAsync(game, install);
        }
        catch { state.Stage = "缓存清理未完成，请重试。"; }
        finally { state.Cleaning = false; }
        if (IsSelected(game, install)) Refresh();
    }
    public async Task OptimizeAsync(bool unshare = false)
    {
        if (_launcher.CurrentGame is not { } game || !IsSupported) return;
        var install = Installation(game); var peer = _launcher.OtherHoYoInstallation(game); var state = GetState(install);
        await RunAsync(game, install, false, false, async token =>
        {
            if (unshare)
            {
                var count = await Task.Run(() => _service.UnshareAsync(game.Id, install, peer, token), token);
                state.Sharing = $"已解除 {count} 个硬链接";
            }
            else
            {
                if (peer?.InstallRoot is null || install.InstallRoot is null) throw new InvalidOperationException("请先设置两服的独立安装目录。");
                if (!await Task.Run(() => WindowsHardLink.CanShareVolume(install.InstallRoot, peer.InstallRoot), token))
                    throw new InvalidOperationException("跨卷或非 NTFS，无法硬链接。两服继续使用独立文件。");
                var result = await Task.Run(() => _service.OptimizeAsync(game.Id, install, peer, token), token);
                state.Sharing = $"共享 {result.FileCount} 个文件 · {Bytes(result.SharedBytes)} · 预计节省 {Bytes(result.EstimatedSavedBytes)}";
            }
        });
    }
    public async Task ScanSharingAsync()
    {
        if (_launcher.CurrentGame is not { } game) return;
        var install = Installation(game); var peer = _launcher.OtherHoYoInstallation(game); var state = GetState(install);
        if (peer?.InstallRoot is null || install.InstallRoot is null) { state.Sharing = "设置两服目录后可扫描共享资源"; Refresh(); return; }
        var summary = await Task.Run(() => _service.ScanSharing(game.Id, install, peer));
        state.Sharing = $"共享 {summary.FileCount} 个文件 · {Bytes(summary.SharedBytes)} · 预计节省 {Bytes(summary.EstimatedSavedBytes)}";
        if (IsSelected(game, install)) Refresh();
    }
    public void Refresh()
    {
        foreach (var name in new[] { nameof(ChannelText), nameof(SharingText), nameof(Title), nameof(InstallRoot), nameof(LocalVersion), nameof(RemoteVersion), nameof(StageText), nameof(PlanText), nameof(PreloadText), nameof(Progress), nameof(DownloadText), nameof(SpeedText), nameof(CanSetRoot), nameof(CanRepair), nameof(CanPreload), nameof(IsPrimaryDownloading), nameof(DownloadVisibility), nameof(IsIndeterminate), nameof(PrimaryActionText), nameof(PrimaryActionGlyph), nameof(StartsMaintenance), nameof(ChineseAudio), nameof(EnglishAudio), nameof(JapaneseAudio), nameof(KoreanAudio) }) OnPropertyChanged(name);
    }
    private static string Bytes(long value) => value >= 1073741824 ? $"{value / 1073741824d:0.00} GiB" : value >= 1048576 ? $"{value / 1048576d:0.0} MiB" : value >= 1024 ? $"{value / 1024d:0.0} KiB" : $"{value} B";
}