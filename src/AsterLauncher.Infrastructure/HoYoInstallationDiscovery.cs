using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

/// <summary>Binds a scan result only after its on-disk identity agrees with the selected server.</summary>
public static class HoYoInstallationDiscovery
{
    /// <summary>Imports the shared BH3 PC client without adding channel choices or replacing launch profiles.</summary>
    public static HoYoInstallation BindSingleClientVerified(GameUserState state, string executable)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.GameId != BuiltInGameIds.HonkaiImpact3rd || state.LaunchTarget != GameLaunchTarget.Local)
            throw new InvalidOperationException("此路径导入仅支持崩坏3本地客户端。");
        var full = Path.GetFullPath(executable);
        var root = Path.GetDirectoryName(full)!;
        if (!Path.GetFileName(full).Equals("BH3.exe", StringComparison.OrdinalIgnoreCase)
            || InstalledServerDeclaration.ReadVerifiedHoYoChannel(root, "BH3.exe") != HoYoChannel.Official)
            throw new InvalidOperationException("崩坏3安装信息不完整或冲突，未更改已有路径。");

        var installations = state.HoYoInstallations ?? [];
        var candidates = installations.ToList();
        if (state.HoYoInstallation is { } alias && !candidates.Contains(alias)) candidates.Add(alias);
        var official = candidates.Where(item => item.Channel == HoYoChannel.Official).ToArray();
        if (official.Select(item => item.InstallationId).Distinct().Count() > 1
            || candidates.Any(item => item.Channel != HoYoChannel.Official
                && (item.InstallRoot is not null || item.ExecutablePath is not null || item.MaintenanceInProgress))
            || state.HoYoInstallation is { Channel: not HoYoChannel.Official })
            throw new InvalidOperationException("已有安装记录需要确认，未自动更改崩坏3安装。");
        var installation = official.FirstOrDefault();
        if (candidates.Any(item => item.MaintenanceInProgress)
            || File.Exists(SafeGamePath.Resolve(root, ".aster-hoyo-maintenance.json")))
            throw new InvalidOperationException("游戏有未完成维护，请先恢复或结束任务。");
        foreach (var candidate in candidates)
        {
            if (candidate.InstallRoot is { Length: > 0 } occupiedRoot
                    && !Path.GetFullPath(occupiedRoot).TrimEnd(Path.DirectorySeparatorChar)
                        .Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || candidate.ExecutablePath is { Length: > 0 } occupiedExe
                    && !Path.GetFullPath(occupiedExe).Equals(full, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("崩坏3已绑定其他目录，请通过安装与维护中的更改目录操作确认。");
        }

        var version = HoYoMaintenanceService.ReadVersion(root);
        installation ??= new HoYoInstallation { Channel = HoYoChannel.Official };
        if (!installations.Any(item => item.InstallationId == installation.InstallationId))
            installations.Add(installation);
        state.HoYoInstallations = installations;
        installation.ExecutablePath = full;
        installation.InstallRoot = root;
        installation.InstalledVersion ??= version;
        installation.ChannelConfirmed = true;
        installation.OfficialChannelConfirmed = true;
        state.HoYoInstallation = installation;
        state.SelectedHoYoChannel = HoYoChannel.Official;
        state.ExecutablePath = full;
        return installation;
    }

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