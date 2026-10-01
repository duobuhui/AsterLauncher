using System.ComponentModel;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.App.ViewModels;

public sealed class EndfieldMaintenanceViewModel : ObservableObject
{
    private sealed class ChannelState
    {
        public string RemoteVersion = "尚未检查";
        public string Stage = "";
        public EndfieldMaintenanceStage StageKind;
        public string Plan = "检查文件后显示缺失、损坏数量及所需下载空间。";
        public string Preload = "尚未检查";
        public string Sharing = "尚未扫描";
        public double Progress;
        public EndfieldPatch? Patch;
        public bool HasTrustedPlan;
        public bool HasUpdate;
        public bool VersionCheckInProgress;
        public bool Syncing;
        public bool Preloading;
        public bool CancelRequested;
        public string Download = "";
        public bool Busy;
        public CancellationTokenSource? Operation;
        public DateTimeOffset LastSpeedAt;
        public long LastSpeedBytes;
        public string Speed = "";
    }

    private readonly LauncherViewModel _launcher;
    private readonly EndfieldMaintenanceService _service;
    private readonly Dictionary<EndfieldChannel, ChannelState> _states = [];

    public EndfieldMaintenanceViewModel(LauncherViewModel launcher, EndfieldMaintenanceService service)
    {
        _launcher = launcher;
        _service = service;
        _launcher.PropertyChanged += LauncherOnPropertyChanged;
    }

    public EndfieldChannel Channel => _launcher.SelectedEndfieldChannel;
    public string ChannelText => Channel switch
    {
        EndfieldChannel.Official => "官服",
        EndfieldChannel.Bilibili => "哔哩哔哩服",
        _ => "旧安装：渠道待确认"
    };
    public string ChannelBadge => Channel switch
    {
        EndfieldChannel.Official => "官",
        EndfieldChannel.Bilibili => "B",
        _ => "?"
    };
    public string InstallRoot
    {
        get
        {
            if (_launcher.SelectedEndfieldInstallation?.InstallRoot is { Length: > 0 } root) return root;
            if (!HasSelectedChannel || string.IsNullOrWhiteSpace(_launcher.GameDownloadDirectory))
                return "尚未选择独立安装目录";
            var folder = Channel == EndfieldChannel.Official ? "Official" : "Bilibili";
            return Path.Combine(_launcher.GameDownloadDirectory, "AsterLauncher", "Endfield", folder)
                + "（安装时创建）";
        }
    }
    public string LocalVersion => _launcher.SelectedEndfieldInstallation?.InstalledVersion ?? "未确认";
    public string RemoteVersion => Current.RemoteVersion;
    public string StageText => Current.Stage;
    public string PlanText => Current.Plan;
    public string PreloadText => Current.Preload;
    public string SharingText => Current.Sharing;
    public double Progress => Current.Progress;
    public string SpeedText => Current.Speed;
    public string DownloadText => Current.Download;
    public bool CanCancel => HasSelectedChannel && (Current.Syncing || Current.Preloading || _launcher.SelectedEndfieldInstallation?.MaintenanceInProgress == true);
    public Microsoft.UI.Xaml.Visibility DownloadVisibility => _launcher.CurrentGame?.Id == BuiltInGameIds.Endfield && (Current.Syncing || Current.Preloading) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public bool IsBusy => Current.Busy;
    public bool HasTrustedPlan => Current.HasTrustedPlan;
    public bool HasUpdate => Current.HasUpdate;
    public bool IsPrimaryDownloading => HasSelectedChannel && Current.Syncing;
    public double PrimaryProgress => Current.Progress;
    public string PrimaryActionGlyph => _launcher.CurrentGame?.Id != BuiltInGameIds.Endfield
        ? "\uE768" : Current.Syncing ? "\uE769"
        : PrimaryStartsMaintenance ? "\uE896" : "\uE768";
    public string PrimaryActionText
    {
        get
        {
            if (_launcher.CurrentGame?.Id != BuiltInGameIds.Endfield)
                return _launcher.LaunchButtonText;
            if (!HasSelectedChannel) return "选择服务器";
            if (Current.Syncing) return Current.StageKind switch
            {
                EndfieldMaintenanceStage.Checking or EndfieldMaintenanceStage.Verifying => "正在校验…",
                EndfieldMaintenanceStage.Extracting => "正在解压…",
                EndfieldMaintenanceStage.Applying => "正在应用…",
                EndfieldMaintenanceStage.FinalVerification => "正在复验…",
                _ => $"暂停下载 {Current.Progress:0}%"
            };
            if (_launcher.SelectedEndfieldInstallation?.MaintenanceInProgress == true) return "继续下载";
            if (_launcher.CurrentGame.IsInstalled && Current.HasUpdate) return "更新游戏";
            if (!_launcher.CurrentGame.IsInstalled) return "下载游戏";
            return _launcher.LaunchButtonText;
        }
    }
    public bool PrimaryStartsMaintenance => HasSelectedChannel && !Current.Syncing
        && (_launcher.CurrentGame?.IsInstalled != true
            || Current.HasUpdate
            || _launcher.SelectedEndfieldInstallation?.MaintenanceInProgress == true);

    public bool HasSelectedChannel => Channel is EndfieldChannel.Official or EndfieldChannel.Bilibili;
    public bool CanPreload => HasSelectedChannel && Current.Patch is not null && !Current.Busy;
    public bool CanRepair => HasSelectedChannel && !Current.Busy
        && !string.IsNullOrWhiteSpace(_launcher.SelectedEndfieldInstallation?.InstalledVersion);
    public bool CanSetRoot => HasSelectedChannel && !Current.Busy;

    private ChannelState Current => _states.TryGetValue(Channel, out var state)
        ? state : _states[Channel] = new ChannelState();

    public Task SelectChannelAsync(EndfieldChannel channel) => _launcher.SelectEndfieldChannelAsync(channel);

    public async Task SetRootAsync(string root)
    {
        await _launcher.AssignEndfieldRootAsync(root);
        Refresh();
    }

    public async Task CheckVersionAsync()
    {
        if (!HasSelectedChannel) return;
        var channel = Channel;
        var state = State(channel);
        if (state.VersionCheckInProgress) return;
        state.VersionCheckInProgress = true;
        state.RemoteVersion = "检查中…";
        if (Channel == channel) Refresh();
        try
        {
            var installation = _launcher.GetOrCreateEndfieldInstallation(channel);
            var sourceVersion = installation.InstalledVersion;
            var version = await _service.GetLatestVersionAsync(installation);
            if (installation.InstalledVersion != sourceVersion) return;
            state.RemoteVersion = version;
            state.HasUpdate = !string.IsNullOrWhiteSpace(sourceVersion)
                && !string.Equals(sourceVersion, version, StringComparison.OrdinalIgnoreCase);
            if (state.HasUpdate) state.Stage = $"发现新版本 {version}";
        }
        catch (Exception)
        {
            state.RemoteVersion = "检查失败";
            state.HasUpdate = false;
            state.Stage = "版本检查失败，请检查网络后重试。";
        }
        finally
        {
            state.VersionCheckInProgress = false;
            if (Channel == channel) Refresh();
        }
    }
    public async Task CheckAsync()
    {
        if (!HasSelectedChannel) { SetStage(Channel, "请在游戏右键菜单中选择官服或 B 服。"); return; }
        var channel = Channel;
        State(channel).HasTrustedPlan = false;
        await RunBusyAsync(channel, async token =>
        {
            SetStage(channel, "正在读取官方文件列表并检查本地文件…");
            var installation = _launcher.GetOrCreateEndfieldInstallation(channel);
            var (package, plan) = await _service.PlanAsync(installation, token);
            var state = State(channel);
            state.RemoteVersion = package.Version;
            state.HasTrustedPlan = true;
            state.Plan = $"缺失 {plan.Missing.Count} · 损坏 {plan.Corrupt.Count} · 需下载 {FormatBytes(plan.DownloadBytes)} · 预留空间 {FormatBytes(plan.EstimatedRequiredFreeBytes)}";
            state.Patch = package.PrePatch;
            state.Preload = package.PrePatch is null ? "未开放" : $"可预下载至 {package.PrePatch.TargetVersion}";
            SetStage(channel, "检查完成");
            if (Channel == channel) Refresh();
        });
    }

    public async Task SyncAsync(bool repairOnly)
    {
        if (!HasSelectedChannel) { SetStage(Channel, "请先选择渠道。"); return; }
        var channel = Channel;
        var state = State(channel);
        if (state.Busy) return;
        state.Syncing = true;
        if (Channel == channel) Refresh();
        try
        {
            await RunBusyAsync(channel, async token =>
            {
                var installation = _launcher.GetOrCreateEndfieldInstallation(channel);
                if (string.IsNullOrWhiteSpace(installation.InstallRoot))
                    throw new InvalidOperationException("请先选择当前渠道的独立安装目录。");
                var otherChannel = _launcher.OtherEndfieldInstallation;
                installation.MaintenanceInProgress = true;
                await _launcher.PersistEndfieldInstallationAsync();
                state.LastSpeedAt = DateTimeOffset.UtcNow;
                state.LastSpeedBytes = 0;
                state.Speed = "";
                var report = new Progress<EndfieldMaintenanceProgress>(item =>
                {
                    if (token.IsCancellationRequested) return;
                    var target = State(item.Channel);
                    target.Download = $"{FormatBytes(item.CompletedBytes)} / {FormatBytes(item.TotalBytes)}";
                    target.StageKind = item.Stage;
                    target.Stage = $"{item.Message} · {item.CompletedFiles}/{item.TotalFiles}";
                    target.Progress = item.TotalBytes > 0
                        ? Math.Clamp(100d * item.CompletedBytes / item.TotalBytes, 0, 100) : 0;
                    {
                        var now = DateTimeOffset.UtcNow;
                        var elapsed = (now - target.LastSpeedAt).TotalSeconds;
                        if (elapsed >= 1)
                        {
                            target.Speed = $"{FormatBytes((long)(Math.Max(0, item.CompletedBytes - target.LastSpeedBytes) / elapsed))}/s";
                            target.LastSpeedAt = now;
                            target.LastSpeedBytes = item.CompletedBytes;
                        }
                        if (Channel == item.Channel) Refresh();
                    }
                });
                await _service.SyncAsync(installation, otherChannel, repairOnly, report, token);
                await _launcher.PersistEndfieldInstallationAsync();
                state.Stage = "文件校验完成";
                state.HasUpdate = false;
                if (Channel == channel) Refresh();
            });
        }
        finally
        {
            state.Syncing = false;
            if (Channel == channel) Refresh();
        }
    }
    public async Task CheckPreloadAsync()
    {
        if (!HasSelectedChannel) { SetStage(Channel, "请先选择渠道。"); return; }
        var channel = Channel;
        await RunBusyAsync(channel, async token =>
        {
            var installation = _launcher.GetOrCreateEndfieldInstallation(channel);
            try
            {
                var patch = await _service.CheckPreloadAsync(installation, token);
                State(channel).Patch = patch;
                State(channel).Preload = patch is null ? "未开放" : $"可预下载至 {patch.TargetVersion}";
                installation.PreloadState = patch is null ? EndfieldPreloadState.NotOpen : EndfieldPreloadState.Available;
            }
            catch
            {
                State(channel).Patch = null;
                State(channel).Preload = "检查失败；无法确认当前渠道的预下载是否开放";
                installation.PreloadState = EndfieldPreloadState.CheckFailed;
                await _launcher.PersistEndfieldInstallationAsync();
                if (Channel == channel) Refresh();
                throw;
            }
            await _launcher.PersistEndfieldInstallationAsync();
            if (Channel == channel) Refresh();
        });
    }

    public async Task PreloadAsync()
    {
        if (!HasSelectedChannel || Current.Patch is null || Current.Busy) return;
        var channel = Channel;
        var patch = Current.Patch;
        State(channel).Preloading = true;
        try { await RunBusyAsync(channel, async token =>
        {
            var installation = _launcher.GetOrCreateEndfieldInstallation(channel);
            State(channel).LastSpeedAt=DateTimeOffset.UtcNow;State(channel).LastSpeedBytes=0;State(channel).Speed="";
            installation.PreloadState = EndfieldPreloadState.Downloading;
            await _launcher.PersistEndfieldInstallationAsync();
            var report = new Progress<EndfieldMaintenanceProgress>(item =>
            {
                if(token.IsCancellationRequested) return;
                var state = State(item.Channel);
                state.Download = $"{FormatBytes(item.CompletedBytes)} / {FormatBytes(item.TotalBytes)}";
                state.StageKind=item.Stage;
                var now=DateTimeOffset.UtcNow;var elapsed=(now-state.LastSpeedAt).TotalSeconds;
                if(elapsed>=1){state.Speed=$"{FormatBytes((long)(Math.Max(0,item.CompletedBytes-state.LastSpeedBytes)/elapsed))}/s";state.LastSpeedAt=now;state.LastSpeedBytes=item.CompletedBytes;}
                state.Preload = $"{item.Message} · {item.CompletedFiles}/{item.TotalFiles}";
                state.Progress = item.TotalBytes > 0 ? 100d * item.CompletedBytes / item.TotalBytes : 0;
                if (Channel == item.Channel) Refresh();
            });
            try
            {
                await _service.PreloadAsync(installation, patch, report, token);
            }
            catch (OperationCanceledException)
            {
                installation.PreloadState = EndfieldPreloadState.Available;
                State(channel).Preload = "已暂停；已校验缓存保留，重新执行时继续使用";
                await _launcher.PersistEndfieldInstallationAsync();
                if (Channel == channel) Refresh();
                throw;
            }
            catch
            {
                installation.PreloadState = EndfieldPreloadState.CheckFailed;
                State(channel).Preload = "预下载未完成；官方内容可能已变更，请重新检查";
                await _launcher.PersistEndfieldInstallationAsync();
                if (Channel == channel) Refresh();
                throw;
            }
            await _launcher.PersistEndfieldInstallationAsync();
            State(channel).Preload = $"已缓存并校验 {patch.Packs.Count} 个包；正式更新会重新核对目标版本与内容后复用";
            if (Channel == channel) Refresh();
        }); } finally { State(channel).Preloading=false; if(Channel==channel) Refresh(); }
    }

    public async Task ScanSharingAsync()
    {
        var channel = Channel;
        var first = _launcher.SelectedEndfieldInstallation;
        var second = _launcher.OtherEndfieldInstallation;
        if (first?.InstallRoot is not { Length: > 0 } || second?.InstallRoot is not { Length: > 0 })
        {
            State(channel).Sharing = "设置两服安装目录后，可检查共享资源及节省空间。";
            if (Channel == channel) Refresh();
            return;
        }
        try
        {
            var summary = await _service.GetSharingAsync(first, second);
            State(channel).Sharing = $"硬链接 {summary.FileCount} 个 · 共享 {FormatBytes(summary.SharedBytes)} · 预计节省 {FormatBytes(summary.EstimatedSavedBytes)}";
        }
        catch (Exception)
        {
            State(channel).Sharing = "共享状态扫描失败";
        }
        if (Channel == channel) Refresh();
    }
    public async Task OptimizeAsync()
    {
        if (!HasSelectedChannel) return;
        var channel = Channel;
        await RunBusyAsync(channel, async token =>
        {
            var first = _launcher.GetOrCreateEndfieldInstallation(channel);
            var second = _launcher.OtherEndfieldInstallation
                ?? throw new InvalidOperationException("另一服尚未设置安装目录。");
            if (!WindowsHardLink.CanShareVolume(first.InstallRoot!, second.InstallRoot!))
            {
                State(channel).Sharing = "跨卷或非 NTFS：无法硬链接，使用独立文件";
                if (Channel == channel) Refresh();
                return;
            }
            var summary = await _service.OptimizeAsync(first, second, token);
            State(channel).Sharing = $"硬链接 {summary.FileCount} 个 · 共享 {FormatBytes(summary.SharedBytes)} · 预计节省 {FormatBytes(summary.EstimatedSavedBytes)}";
            if (Channel == channel) Refresh();
        });
    }

    public async Task UnshareAsync()
    {
        if (!HasSelectedChannel) return;
        var channel = Channel;
        await RunBusyAsync(channel, async token =>
        {
            var installation = _launcher.GetOrCreateEndfieldInstallation(channel);
            var count = await _service.UnshareAsync(installation, token);
            State(channel).Sharing = $"已解除 {count} 个硬链接，当前渠道文件独立";
            if (Channel == channel) Refresh();
        });
    }

    public void Pause() => Current.Operation?.Cancel();
    public async Task CancelAsync(bool preload = false)
    {
        if(!HasSelectedChannel)return;
        var channel=Channel;var state=State(channel);
        if(state.Busy){state.CancelRequested=true;state.Operation?.Cancel();return;}
        await ClearCancelledAsync(channel,preload);
    }
    private async Task ClearCancelledAsync(EndfieldChannel channel,bool preload)
    {
        var installation=_launcher.GetOrCreateEndfieldInstallation(channel);
        try
        {
            var result=await _service.CancelTaskAsync(installation,preload);
            await _launcher.PersistEndfieldInstallationAsync();
            var state=State(channel);state.Progress=0;state.Download="";state.Speed="";
            var message=$"已取消，删除 {result.DeletedFiles} 个缓存文件（{FormatBytes(result.DeletedBytes)}）";
            if(result.RetainedObjects>0)message+=$"；{result.RetainedObjects} 项仍被另一任务引用，已保留";
            if(!preload&&installation.MaintenanceInProgress)message+="。部分文件已应用，下次继续时将重新校验安装。";
            state.Stage=message;if(preload)state.Preload=message;
            if(Channel==channel)Refresh();
        }
        catch(Exception ex) when(ex is IOException or InvalidOperationException or InvalidDataException)
        {SetStage(channel,"取消后的缓存清理未完成；文件可能正在使用，可在任务结束后重试。");}
    }

    private async Task RunBusyAsync(EndfieldChannel channel, Func<CancellationToken, Task> action)
    {
        var state = State(channel);
        if (state.Busy) return;
        state.CancelRequested = false;
        state.Busy = true;
        state.Operation = new CancellationTokenSource();
        if (Channel == channel) Refresh();
        try { await action(state.Operation.Token); }
        catch (OperationCanceledException)
        {
            if(state.CancelRequested) await ClearCancelledAsync(channel,state.Preloading);
            else SetStage(channel, "已暂停，继续下载时会使用已保存的进度。");
        }
        catch (InvalidOperationException exception)
        {
            SetStage(channel, $"失败：{exception.Message}");
        }
        catch (Exception exception)
        {
            // Do not expose signed URLs from network exceptions.
            SetStage(channel, exception switch
            {
                HttpRequestException => "无法连接官方资源服务器；已下载并校验的缓存会保留。请检查网络后重试。",
                InvalidDataException => "文件校验失败，安装尚未完成。重新检查后可继续下载。",
                IOException => "文件读写失败；请检查目标卷剩余空间、目录权限和占用情况，然后重试。",
                _ => $"操作未完成（{exception.GetType().Name}）；请查看本地日志了解详情。"
            });
        }
        finally
        {
            state.Operation.Dispose();
            state.Operation = null;
            state.Busy = false;
            if (Channel == channel) Refresh();
        }
    }
    private ChannelState State(EndfieldChannel channel) =>
        _states.TryGetValue(channel, out var state) ? state : _states[channel] = new ChannelState();

    private void SetStage(EndfieldChannel channel, string value)
    {
        State(channel).Stage = value;
        if (Channel == channel) Refresh();
    }

    private void LauncherOnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(LauncherViewModel.SelectedEndfieldChannel)
            or nameof(LauncherViewModel.SelectedEndfieldInstallation)
            or nameof(LauncherViewModel.CurrentGame)
            or nameof(LauncherViewModel.LaunchButtonText)
            or nameof(LauncherViewModel.GameDownloadDirectory))
            Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Channel));
        OnPropertyChanged(nameof(ChannelText));
        OnPropertyChanged(nameof(ChannelBadge));
        OnPropertyChanged(nameof(InstallRoot));
        OnPropertyChanged(nameof(LocalVersion));
        OnPropertyChanged(nameof(RemoteVersion));
        OnPropertyChanged(nameof(StageText));
        OnPropertyChanged(nameof(PlanText));
        OnPropertyChanged(nameof(PreloadText));
        OnPropertyChanged(nameof(SharingText));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(DownloadVisibility));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasSelectedChannel));
        OnPropertyChanged(nameof(CanPreload));
        OnPropertyChanged(nameof(CanRepair));
        OnPropertyChanged(nameof(CanSetRoot));
        OnPropertyChanged(nameof(HasTrustedPlan));
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(IsPrimaryDownloading));
        OnPropertyChanged(nameof(PrimaryProgress));
        OnPropertyChanged(nameof(PrimaryActionGlyph));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PrimaryStartsMaintenance));
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GiB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.##} MiB"
        : $"{bytes:N0} B";
}
