namespace AsterLauncher.Core;

public static class EndfieldInstallationProfiles
{
    public static LaunchProfile Ensure(LauncherConfiguration configuration, EndfieldInstallation installation)
    {
        var existing=configuration.LaunchProfiles.FirstOrDefault(p=>p.Id==installation.SelectedLaunchProfileId
            && p.GameId==BuiltInGameIds.Endfield && p.EndfieldInstallationId==installation.InstallationId);
        if(existing is not null)return existing;
        var source=configuration.LaunchProfiles.FirstOrDefault(p=>p.GameId==BuiltInGameIds.Endfield
            && p.EndfieldInstallationId is null && p.Id==configuration.SelectedProfileId)
            ?? configuration.LaunchProfiles.FirstOrDefault(p=>p.GameId==BuiltInGameIds.Endfield && p.EndfieldInstallationId is null);
        var profile=new LaunchProfile
        {
            GameId=BuiltInGameIds.Endfield,EndfieldInstallationId=installation.InstallationId,
            Name=installation.Channel==EndfieldChannel.Official?"官服启动":"B 服启动",IsDefault=false,
            GameArguments=source?.GameArguments??"",GameWorkingDirectory=null,
            GameDetectionTimeoutSeconds=source?.GameDetectionTimeoutSeconds??45,
            Steps=source?.Steps.Select(step=>{var copy=step.Clone();copy.Id=Guid.NewGuid();return copy;}).ToList()??[]
        };
        configuration.LaunchProfiles.Add(profile);installation.SelectedLaunchProfileId=profile.Id;
        return profile;
    }
}
