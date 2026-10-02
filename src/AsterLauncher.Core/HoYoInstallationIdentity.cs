namespace AsterLauncher.Core;

public static class HoYoInstallationIdentity
{
    public static bool HasChannels(string gameId) => gameId is BuiltInGameIds.GenshinImpact or BuiltInGameIds.HonkaiStarRail or BuiltInGameIds.ZenlessZoneZero;
    public static void Restore(GameUserState state)
    {
        state.HoYoInstallations ??= [];
        if (state.HoYoInstallation is { } legacy && !state.HoYoInstallations.Any(i => i.InstallationId == legacy.InstallationId))
        {
            legacy.ExecutablePath ??= state.ExecutablePath;
            legacy.ChannelConfirmed |= legacy.OfficialChannelConfirmed;
            if (legacy.OfficialChannelConfirmed) legacy.Channel = HoYoChannel.Official;
            else if (HasChannels(state.GameId) && (legacy.ExecutablePath is not null || legacy.InstallRoot is not null)) legacy.Channel = HoYoChannel.Unknown;
            state.HoYoInstallations.Add(legacy); state.SelectedHoYoChannel = legacy.Channel;
        }
        else if (state.HoYoInstallations.Count == 0 && state.ExecutablePath is not null)
        {
            state.SelectedHoYoChannel = HasChannels(state.GameId) ? HoYoChannel.Unknown : HoYoChannel.Official;
            state.HoYoInstallations.Add(new() { Channel = state.SelectedHoYoChannel, ExecutablePath = state.ExecutablePath, InstallRoot = Path.GetDirectoryName(state.ExecutablePath) });
        }
        state.HoYoInstallation = Get(state, state.SelectedHoYoChannel);
    }
    public static HoYoInstallation Get(GameUserState state, HoYoChannel channel)
    {
        if (channel == HoYoChannel.Bilibili && !HasChannels(state.GameId)) throw new ArgumentException("此游戏在客户端内选择服务器。");
        var install = state.HoYoInstallations.FirstOrDefault(i => i.Channel == channel);
        if (install is null) { install = new() { Channel = channel }; state.HoYoInstallations.Add(install); }
        return install;
    }
    public static LaunchProfile EnsureProfile(LauncherConfiguration configuration, GameUserState state, HoYoInstallation installation)
    {
        var existing = configuration.LaunchProfiles.FirstOrDefault(p => p.GameId == state.GameId && p.HoYoInstallationId == installation.InstallationId);
        if (existing is not null) { installation.SelectedLaunchProfileId ??= existing.Id; return existing; }
        var source = configuration.LaunchProfiles.FirstOrDefault(p => p.GameId == state.GameId && p.HoYoInstallationId is null && p.Id == configuration.SelectedProfileId)
            ?? configuration.LaunchProfiles.FirstOrDefault(p => p.GameId == state.GameId && p.HoYoInstallationId is null);
        var profile = new LaunchProfile { GameId = state.GameId, HoYoInstallationId = installation.InstallationId,
            Name = installation.Channel == HoYoChannel.Bilibili ? "B 服启动" : "官服启动", IsDefault = false,
            GameArguments = source?.GameArguments ?? "", GameDetectionTimeoutSeconds = source?.GameDetectionTimeoutSeconds ?? 45,
            Steps = source?.Steps.Select(s => { var copy = s.Clone(); copy.Id = Guid.NewGuid(); return copy; }).ToList() ?? [] };
        configuration.LaunchProfiles.Add(profile); installation.SelectedLaunchProfileId = profile.Id; return profile;
    }
}