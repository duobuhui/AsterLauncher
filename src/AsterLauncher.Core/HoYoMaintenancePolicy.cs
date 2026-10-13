namespace AsterLauncher.Core;

public enum HoYoVersionRelation
{
    Unknown,
    Same,
    RemoteNewer,
    LocalNewer
}

public static class HoYoMaintenancePolicy
{
    public static HoYoVersionRelation CompareVersions(string? localVersion, string? remoteVersion)
    {
        if (!Version.TryParse(localVersion?.Trim(), out var local)
            || !Version.TryParse(remoteVersion?.Trim(), out var remote))
            return HoYoVersionRelation.Unknown;
        static Version Complete(Version value) => new(value.Major, value.Minor,
            Math.Max(0, value.Build), Math.Max(0, value.Revision));
        return Complete(remote).CompareTo(Complete(local)) switch
        {
            > 0 => HoYoVersionRelation.RemoteNewer,
            < 0 => HoYoVersionRelation.LocalNewer,
            _ => HoYoVersionRelation.Same
        };
    }

    public static bool IsBoundToExecutable(string? existingExecutable, HoYoInstallation installation)
    {
        if (string.IsNullOrWhiteSpace(existingExecutable) || string.IsNullOrWhiteSpace(installation.ExecutablePath)
            || string.IsNullOrWhiteSpace(installation.InstallRoot)) return false;
        try
        {
            return SamePath(existingExecutable, installation.ExecutablePath)
                && SamePath(Path.GetDirectoryName(existingExecutable), installation.InstallRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool IsExistingDirectory(string? existingExecutable, string? requestedRoot)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(existingExecutable)
                && SamePath(Path.GetDirectoryName(existingExecutable), requestedRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool SamePath(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
        && Path.IsPathFullyQualified(first) && Path.IsPathFullyQualified(second)
        && Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    public static bool ShouldStartMaintenance(bool hasLocalGame, bool maintenanceBound,
        bool maintenanceInProgress, bool audioSelectionPending, string? localVersion, string? remoteVersion) =>
        !hasLocalGame || maintenanceBound && (maintenanceInProgress || audioSelectionPending
            || CompareVersions(localVersion, remoteVersion) == HoYoVersionRelation.RemoteNewer);
}