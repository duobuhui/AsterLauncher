namespace AsterLauncher.Core;

public static class InstallationChannelAssignment
{
    /// <summary>Owner-supplied identity; preserve unknown installation ID and profile/archive references.</summary>
    public static HoYoInstallation Confirm(GameUserState state, HoYoChannel channel, string executable)
    {
        if (!HoYoInstallationIdentity.HasChannels(state.GameId) || channel is not (HoYoChannel.Official or HoYoChannel.Bilibili))
            throw new ArgumentException("请选择官服或哔哩哔哩服。");
        var full = Path.GetFullPath(executable); var root = Path.GetDirectoryName(full)!;
        var target = state.HoYoInstallations.FirstOrDefault(i=>i.Channel == channel);
        if (target?.MaintenanceInProgress == true || target?.InstallRoot is { } occupied && !Path.GetFullPath(occupied).Equals(root,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("此服务器已有安装或维护任务。");
        var unknown = state.HoYoInstallations.FirstOrDefault(i=>i.Channel == HoYoChannel.Unknown && i.ExecutablePath is { } path && Path.GetFullPath(path).Equals(full,StringComparison.OrdinalIgnoreCase));
        if (unknown is not null && target is null) { unknown.Channel = channel; target = unknown; }
        target ??= HoYoInstallationIdentity.Get(state,channel);
        target.InstallRoot = root; target.ExecutablePath = full; target.ChannelConfirmed = true;
        target.OfficialChannelConfirmed = channel == HoYoChannel.Official;
        if (unknown is not null && unknown != target) { unknown.InstallRoot = unknown.ExecutablePath = null; }
        state.SelectedHoYoChannel = channel; state.HoYoInstallation = target; state.ExecutablePath = full;
        return target;
    }
}
