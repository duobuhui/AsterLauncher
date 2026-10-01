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
        public string Download = "", Speed = "";
        public double Progress;
        public bool Busy, Checking, Downloading, Preloading, Cleaning, ReadImportedAudio;
        public CancellationTokenSource? Cancellation;
        public TaskCompletionSource? Completion;
        public DateTimeOffset LastProgress = DateTimeOffset.MinValue, LastSpeed = DateTimeOffset.UtcNow;
        public long LastBytes;
        public long Revision;
    }
    private readonly LauncherViewModel _launcher;
    private readonly HoYoMaintenanceService _service;
    private readonly Dictionary<string, State> _states = [];
    public HoYoMaintenanceViewModel(LauncherViewModel launcher, HoYoMaintenanceService service)
    {
        _launcher = launcher; _service = service;
        launcher.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(LauncherViewModel.CurrentGame) or nameof(LauncherViewModel.CanLaunch)) Refresh(); };
    }
    public bool IsSupported => _launcher.CurrentGame is { } game && HoYoDistributionProvider.Games.ContainsKey(game.Id);
    private State Current => GetState(_launcher.CurrentGame?.Id ?? "");
    private State GetState(string id) => _states.TryGetValue(id, out var state) ? state : _states[id] = new();
    private HoYoInstallation Installation(GameCardViewModel game)
    {
        var imported = game.State.HoYoInstallation is null && game.State.ExecutablePath is { Length: > 0 };
        var install = game.State.HoYoInstallation ??= new();
        if (imported) GetState(game.Id).ReadImportedAudio = true;
        if (game.State.ExecutablePath is { Length: > 0 } exe && install.InstallRoot is null)
        {
            install.InstallRoot = Path.GetDirectoryName(exe);
            install.InstalledVersion = HoYoMaintenanceService.ReadVersion(install.InstallRoot!);
        }
        if (install.InstallRoot is { Length: > 0 } root)
            install.MaintenanceInProgress = File.Exists(SafeGamePath.Resolve(root, ".aster-hoyo-maintenance.json")) || GetState(game.Id).Downloading && install.MaintenanceInProgress;
        return install;
    }
    public string Title => (_launcher.CurrentGame?.DisplayName ?? "") + " · 安装与修复";
    public string InstallRoot => _launcher.CurrentGame is { } game && Installation(game).InstallRoot is { Length: > 0 } root ? root
        : string.IsNullOrWhiteSpace(_launcher.GameDownloadDirectory) ? "请先在设置中指定游戏下载目录"
        : Path.Combine(_launcher.GameDownloadDirectory, Current.Release?.DirectoryName ?? _launcher.CurrentGame!.DisplayName) + "（安装时创建）";
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
    public Visibility DownloadVisibility => IsSupported && (Current.Downloading || Current.Preloading) ? Visibility.Visible : Visibility.Collapsed;
    public string PrimaryActionGlyph => Current.Downloading ? "\uE769" : StartsMaintenance ? "\uE896" : "\uE768";
    public bool StartsMaintenance => IsSupported && !Current.Busy && !Current.Cleaning && _launcher.CurrentGame is { } game
        && (!game.IsInstalled || Installation(game).MaintenanceInProgress || Installation(game).AudioSelectionPending || Current.Release is { } release && LocalVersion != release.Version);
    public string PrimaryActionText => Current.Downloading ? Current.Stage.StartsWith("正在下载") ? $"暂停下载 {Progress:0}%" : Current.Stage
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
        var languages = Installation(game).AudioLanguages;
        Current.ReadImportedAudio = false;
        if (selected && !languages.Contains(language)) languages.Add(language);
        else if (!selected) languages.Remove(language);
        Current.Plan = null; Installation(game).AudioSelectionPending = true;
        try { await _launcher.PersistHoYoInstallationAsync(game); }
        catch { GetState(game.Id).Stage = "语音选择保存失败，请重试。"; }
        if (ReferenceEquals(game, _launcher.CurrentGame)) Refresh();
    }
    public async Task CheckVersionAsync()
    {
        if (!IsSupported || _launcher.CurrentGame is not { } game) return;
        var state = GetState(game.Id); if (state.Checking || state.Busy) return;
        state.Checking = true; state.Remote = "检查中…"; Refresh();
        try
        {
            state.Release = await _service.GetReleaseAsync(game.Id);
            state.Remote = state.Release?.Version ?? "未开放下载";
            if (state.ReadImportedAudio && game.State.ExecutablePath is { } existing && state.Release?.AudioRecordPath is { } record)
            {
                var path = SafeGamePath.Resolve(Path.GetDirectoryName(existing)!, record);
                if (File.Exists(path) && new FileInfo(path).Length < 64 * 1024)
                {
                    var text = await File.ReadAllTextAsync(path);
                    var languages = new[] { ("Chinese", "zh-cn"), ("English", "en-us"), ("Japanese", "ja-jp"), ("Korean", "ko-kr") }
                        .Where(pair => text.Contains(pair.Item1, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Item2).ToList();
                    if (languages.Count > 0)
                    {
                        Installation(game).AudioLanguages = languages;
                        await _launcher.PersistHoYoInstallationAsync(game);
                    }
                }
            }
            state.ReadImportedAudio = false;
            state.Preload = state.Release?.PreloadVersion is { } pre ? Installation(game).PreloadVersion == pre ? $"已缓存 {pre}" : $"可预下载至 {pre}" : "未开放";
            if (state.Release is { } release && Installation(game).InstalledVersion is { } local && local != release.Version)
                state.Stage = $"发现新版本 {release.Version}";
        }
        catch { state.Remote = "检查失败"; state.Preload = "检查失败"; state.Stage = "无法获取官方版本，请稍后重试。"; }
        finally { state.Checking = false; if (ReferenceEquals(game, _launcher.CurrentGame)) Refresh(); }
    }
    public async Task SetRootAsync(string root)
    {
        if (!IsSupported || _launcher.CurrentGame is not { } game || Current.Busy || Current.Cleaning) return;
        var release = await _service.GetReleaseAsync(game.Id) ?? throw new InvalidOperationException("官方尚未开放 PC 下载。");
        HoYoMaintenanceService.ValidateExistingRoot(root, release.ExecutableName);
        var install = Installation(game);
        if (install.MaintenanceInProgress) throw new InvalidOperationException("当前目录有未完成维护，请完成任务后再更改目录。");
        if (string.Equals(install.InstallRoot, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) return;
        await _service.CancelAsync(install); await _service.CancelAsync(install, true);
        install.PreloadContentHash = install.PreloadSourceVersion = install.PreloadVersion = null;
        game.State.ExecutablePath = File.Exists(SafeGamePath.Resolve(root, release.ExecutableName)) ? SafeGamePath.Resolve(root, release.ExecutableName) : null;
        install.InstallRoot = Path.GetFullPath(root); install.InstalledVersion = HoYoMaintenanceService.ReadVersion(root);
        install.InstallationId = Guid.NewGuid(); install.OfficialChannelConfirmed = false;
        Current.Plan = null; Current.ReadImportedAudio = game.State.ExecutablePath is not null;
        await _launcher.PersistHoYoInstallationAsync(game, File.Exists(SafeGamePath.Resolve(root, release.ExecutableName)));
        Refresh();
    }
    public async Task CheckAsync()
    {
        if (_launcher.CurrentGame is not { } game || !IsSupported) return;
        await RunAsync(game, false, false, async token =>
        {
            var install = Installation(game);
            CurrentPlanReset(game);
            if (install.InstallRoot is { } root)
                HoYoMaintenanceService.ValidateExistingRoot(root, (await _service.GetReleaseAsync(game.Id, token))?.ExecutableName ?? throw new InvalidOperationException("未开放下载。"));
            var plan = await _service.PlanAsync(game.Id, install, false, token);
            var state = GetState(game.Id); state.Plan = plan; state.Release = plan.Package.Release; state.Remote = plan.Package.Release.Version;
            state.PlanText = $"缺失 {plan.Missing.Count} · 损坏 {plan.Corrupt.Count} · 需下载 {Bytes(plan.DownloadBytes)} · 预留空间 {Bytes(plan.RequiredFreeBytes)}";
            state.Stage = "检查完成";
        });
    }
    private void CurrentPlanReset(GameCardViewModel game) => GetState(game.Id).Plan = null;
    public bool HasPlan => Current.Plan is not null;
    public bool NeedsChannelConfirmation => _launcher.CurrentGame is { IsInstalled: true } game && !Installation(game).OfficialChannelConfirmed;
    public async Task ConfirmOfficialChannelAsync()
    {
        if (_launcher.CurrentGame is not { } game) return;
        Installation(game).OfficialChannelConfirmed = true;
        await _launcher.PersistHoYoInstallationAsync(game);
    }
    public async Task EnsureDefaultRootAsync()
    {
        var game = _launcher.CurrentGame ?? throw new InvalidOperationException("未选中游戏。");
        var install = Installation(game);
        if (install.InstallRoot is not null) return;
        if (string.IsNullOrWhiteSpace(_launcher.GameDownloadDirectory)) throw new InvalidOperationException("请先在设置中指定游戏下载目录。");
        var release = await _service.GetReleaseAsync(game.Id) ?? throw new InvalidOperationException("官方尚未开放 PC 下载。");
        var root = SafeGamePath.Resolve(_launcher.GameDownloadDirectory, release.DirectoryName);
        HoYoMaintenanceService.ValidateExistingRoot(root, release.ExecutableName);
        Directory.CreateDirectory(root);
        install.InstallRoot = root;
        await _launcher.PersistHoYoInstallationAsync(game);
        Refresh();
    }
    public async Task SyncAsync(bool repair)
    {
        if (_launcher.CurrentGame is not { } game || !IsSupported) return;
        await RunAsync(game, true, false, async token =>
        {
            var install = Installation(game);
            await _launcher.PersistHoYoInstallationAsync(game);
            try
            {
                await _service.SyncAsync(game.Id, install, repair, ProgressFor(game, token), token);
                var state = GetState(game.Id); state.Plan = null;
                state.PlanText = "缺失 0 · 损坏 0 · 无需下载";
            }
            finally { await _launcher.PersistHoYoInstallationAsync(game, !install.MaintenanceInProgress); }
        });
    }
    public async Task PreloadAsync()
    {
        if (_launcher.CurrentGame is not { } game || !CanPreload) return;
        await RunAsync(game, false, true, async token =>
        {
            await _service.PreloadAsync(game.Id, Installation(game), ProgressFor(game, token), token);
            await _launcher.PersistHoYoInstallationAsync(game);
            GetState(game.Id).Preload = $"已缓存 {Installation(game).PreloadVersion}";
        });
    }
    private IProgress<HoYoProgress> ProgressFor(GameCardViewModel game, CancellationToken token)
    {
        var state = GetState(game.Id); var revision = state.Revision;
        return new Progress<HoYoProgress>(p =>
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
            if (ReferenceEquals(game, _launcher.CurrentGame)) Refresh();
        });
    }
    private async Task RunAsync(GameCardViewModel game, bool downloading, bool preloading, Func<CancellationToken, Task> action)
    {
        var state = GetState(game.Id); if (state.Busy || state.Cleaning) return;
        state.Busy = true; state.Downloading = downloading; state.Preloading = preloading; state.Revision++;
        state.Cancellation = new(); state.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        state.LastBytes = 0; state.LastSpeed = DateTimeOffset.UtcNow; state.Stage = "正在检查";
        if (ReferenceEquals(game, _launcher.CurrentGame)) Refresh();
        try { await action(state.Cancellation.Token); }
        catch (OperationCanceledException) { state.Stage = "已暂停，继续时复用已校验的下载内容。"; }
        catch (Exception ex) { state.Stage = ex is IOException or InvalidOperationException or InvalidDataException ? ex.Message : "维护未完成，请重新检查后继续。"; }
        finally
        {
            state.Busy = state.Downloading = state.Preloading = false;
            state.Cancellation.Dispose(); state.Cancellation = null; state.Completion.TrySetResult();
            if (ReferenceEquals(game, _launcher.CurrentGame)) Refresh();
        }
    }
    public void Pause() => Current.Cancellation?.Cancel();
    public async Task CancelAsync(bool preload = false)
    {
        if (_launcher.CurrentGame is not { } game) return;
        var state = GetState(game.Id);
        if (state.Cleaning) return;
        state.Cleaning = true; preload |= state.Preloading; state.Cancellation?.Cancel();
        if (state.Completion is { } completion) await completion.Task;
        try
        {
            var result = await _service.CancelAsync(Installation(game), preload);
            if (preload) { var i = Installation(game); i.PreloadContentHash = i.PreloadSourceVersion = i.PreloadVersion = null; state.Preload = "缓存已删除"; }
            state.Stage = $"已删除 {result.DeletedFiles} 个缓存文件，释放 {Bytes(result.DeletedBytes)}。";
            await _launcher.PersistHoYoInstallationAsync(game);
        }
        catch { state.Stage = "缓存清理未完成，请重试。"; }
        finally { state.Cleaning = false; }
        if (ReferenceEquals(game, _launcher.CurrentGame)) Refresh();
    }
    public void Refresh()
    {
        foreach (var name in new[] { nameof(Title), nameof(InstallRoot), nameof(LocalVersion), nameof(RemoteVersion), nameof(StageText), nameof(PlanText), nameof(PreloadText), nameof(Progress), nameof(DownloadText), nameof(SpeedText), nameof(CanSetRoot), nameof(CanRepair), nameof(CanPreload), nameof(IsPrimaryDownloading), nameof(DownloadVisibility), nameof(PrimaryActionText), nameof(PrimaryActionGlyph), nameof(StartsMaintenance), nameof(ChineseAudio), nameof(EnglishAudio), nameof(JapaneseAudio), nameof(KoreanAudio) }) OnPropertyChanged(name);
    }
    private static string Bytes(long value) => value >= 1073741824 ? $"{value / 1073741824d:0.00} GiB" : value >= 1048576 ? $"{value / 1048576d:0.0} MiB" : value >= 1024 ? $"{value / 1024d:0.0} KiB" : $"{value} B";
}