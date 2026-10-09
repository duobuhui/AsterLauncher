using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

/// <summary>Binds a scan result only after its on-disk identity agrees with the selected server.</summary>
public static class HoYoInstallationDiscovery
{
    public static HoYoInstallation BindVerified(LauncherConfiguration configuration, GameUserState state,
        string executable, HoYoChannel scannedChannel)
    {
        if (state.SelectedHoYoChannel != scannedChannel || scannedChannel is not (HoYoChannel.Official or HoYoChannel.Bilibili)
            || state.LaunchTarget != GameLaunchTarget.Local)
            throw new InvalidOperationException("服务器已切换，请重新查找。");
        var expectedExecutable = state.GameId switch
        {
            BuiltInGameIds.GenshinImpact => "YuanShen.exe",
            BuiltInGameIds.HonkaiStarRail => "StarRail.exe",
            BuiltInGameIds.ZenlessZoneZero => "ZenlessZoneZero.exe",
            _ => throw new InvalidOperationException("此游戏不使用此渠道识别方式。")
        };
        var full = Path.GetFullPath(executable);
        var root = Path.GetDirectoryName(full)!;
        if (!Path.GetFileName(full).Equals(expectedExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("找到的程序不属于当前游戏。");
        var channel = InstalledServerDeclaration.ReadVerifiedHoYoChannel(root, expectedExecutable);
        if (channel == HoYoChannel.Unknown)
            throw new InvalidOperationException("已找到程序，但安装信息不足以确认服务器；请到游戏设置确认安装服务器。");
        if (channel != scannedChannel)
            throw new InvalidOperationException("已找到另一服务器的游戏；请切换服务器后重新查找。");
        if (state.HoYoInstallations.Any(i => i.MaintenanceInProgress)
            || File.Exists(SafeGamePath.Resolve(root, ".aster-hoyo-maintenance.json")))
            throw new InvalidOperationException("游戏有未完成维护，请先恢复或结束任务。");
        var existing = state.HoYoInstallations.FirstOrDefault(i => i.Channel == channel);
        foreach (var peer in state.HoYoInstallations.Where(i => i != existing && i.Channel != HoYoChannel.Unknown))
            HoYoSharingPolicy.ValidateIndependent(root, peer.InstallRoot);
        var version = HoYoMaintenanceService.ReadVersion(root);
        var unknown = state.HoYoInstallations.FirstOrDefault(i => i.Channel == HoYoChannel.Unknown
            && i.ExecutablePath is { } path && Path.GetFullPath(path).Equals(full, StringComparison.OrdinalIgnoreCase));
        var installation = InstallationChannelAssignment.Confirm(state, channel, full);
        if (unknown is not null && unknown != installation)
        {
            foreach (var profile in configuration.LaunchProfiles.Where(p => p.GameId == state.GameId
                && p.HoYoInstallationId == unknown.InstallationId)) profile.HoYoInstallationId = installation.InstallationId;
            installation.SelectedLaunchProfileId = unknown.SelectedLaunchProfileId ?? installation.SelectedLaunchProfileId;
        }
        installation.InstalledVersion = version;
        HoYoInstallationIdentity.EnsureProfile(configuration, state, installation);
        return installation;
    }
}