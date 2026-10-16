using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

public sealed record EndfieldLaunchPlanResult(
    LaunchProfile Profile,
    Guid? ManagedStepId,
    bool UsesCustomTakeover);

/// <summary>Builds a per-launch profile without changing saved profiles or installation paths.</summary>
public static class EndfieldLaunchPlan
{
    public static EndfieldLaunchPlanResult Create(
        LaunchProfile profile,
        EndfieldInstallation installation,
        string gameExecutable,
        string launcherExecutable)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(installation);

        var temporary = new LaunchProfile
        {
            Id = profile.Id,
            GameId = profile.GameId,
            EndfieldInstallationId = profile.EndfieldInstallationId,
            HoYoInstallationId = profile.HoYoInstallationId,
            Name = profile.Name,
            IsDefault = profile.IsDefault,
            GameArguments = profile.GameArguments,
            GameWorkingDirectory = profile.GameWorkingDirectory,
            GameDetectionTimeoutSeconds = Math.Clamp(installation.LauncherDetectionTimeoutSeconds, 60, 300),
            Steps = profile.Steps.Select(step => step.Clone()).ToList()
        };

        // Match the orchestrator's existing takeover rule, including custom non-Game phases.
        if (temporary.Steps.Any(step => step.IsEnabled && step.TakesOverGameLaunch))
            return new(temporary, null, true);

        var bridge = installation.LaunchMode == EndfieldLaunchMode.ExperimentalBridge;
        var executable = bridge ? gameExecutable : launcherExecutable;
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var managed = new LaunchStep
        {
            Name = bridge ? "实验性启动桥" : "官方启动器接管",
            ExecutablePath = executable,
            // The bridge is intercepted by ManagedStepId; its launch context carries game arguments in memory.
            Arguments = string.Empty,
            WorkingDirectory = bridge && !string.IsNullOrWhiteSpace(profile.GameWorkingDirectory)
                ? profile.GameWorkingDirectory : Path.GetDirectoryName(executable),
            Phase = LaunchPhase.Game,
            TimeoutSeconds = 15,
            FailurePolicy = LaunchFailurePolicy.Abort,
            RunAsAdministrator = false,
            SkipIfAlreadyRunning = false,
            CloseOnGameExit = false,
            TakesOverGameLaunch = true,
            IsEnabled = true
        };
        temporary.Steps.Add(managed);
        return new(temporary, managed.Id, false);
    }
}