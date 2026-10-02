namespace AsterLauncher.Core;

public enum EndfieldChannel
{
    Unknown = 0,
    Official = 1,
    Bilibili = 2
}

public enum EndfieldMaintenanceStage
{
    Idle,
    Checking,
    Verifying,
    Downloading,
    Extracting,
    Applying,
    FinalVerification,
    Completed,
    Paused,
    Failed
}

public enum EndfieldPreloadState
{
    NotOpen,
    CheckFailed,
    Available,
    Downloading,
    Extracting,
    Completed
}

public sealed class EndfieldInstallation
{
    public Guid InstallationId { get; set; } = Guid.NewGuid();
    public EndfieldChannel Channel { get; set; }
    public string? InstallRoot { get; set; }
    public string? ExecutablePath { get; set; }
    public string? InstalledVersion { get; set; }
    public string? ScreenshotDirectory { get; set; }
    public bool MaintenanceInProgress { get; set; }
    public Guid? SelectedLaunchProfileId { get; set; }
    public EndfieldPreloadState PreloadState { get; set; }
    public string? PreloadSourceVersion { get; set; }
    public string? PreloadTargetVersion { get; set; }
    public string? PreloadContentHash { get; set; }
}

public sealed record EndfieldManifestFile(string Path, long Size, string Md5);

public sealed record EndfieldPackage(
    EndfieldChannel Channel,
    string Version,
    Uri FileBaseUri,
    string ManifestMd5,
    IReadOnlyList<EndfieldManifestFile> Files,
    EndfieldPatch? Patch,
    EndfieldPatch? PrePatch,
    IReadOnlyList<EndfieldRemoteObject>? Packs = null);

public sealed record EndfieldRemoteObject(Uri Uri, string Md5, long Size);

public sealed record EndfieldPatch(string TargetVersion, IReadOnlyList<EndfieldRemoteObject> Packs,
    string? ContentMd5, long? PackageSize, string? CdKey = null,
    Uri? V2PatchInfoUri = null, string? V2PatchInfoMd5 = null);

public sealed record EndfieldMaintenanceProgress(
    EndfieldChannel Channel,
    EndfieldMaintenanceStage Stage,
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    string Message);

public sealed record EndfieldRepairPlan(
    EndfieldChannel Channel,
    string TargetVersion,
    IReadOnlyList<EndfieldManifestFile> Missing,
    IReadOnlyList<EndfieldManifestFile> Corrupt,
    long DownloadBytes)
{
    // Conservative same-volume bound: content cache, committed files and one temporary replacement.
    public long EstimatedRequiredFreeBytes => checked(2 * DownloadBytes
        + Missing.Concat(Corrupt).Select(file => file.Size).DefaultIfEmpty().Max());
}

public sealed record EndfieldSharingSummary(int FileCount, long SharedBytes, long EstimatedSavedBytes);

public interface IEndfieldDistributionProvider
{
    Task<string> GetLatestVersionAsync(EndfieldChannel channel, string? installedVersion,
        CancellationToken cancellationToken = default);
    Task<EndfieldPackage> GetPackageAsync(EndfieldChannel channel, string? installedVersion,
        CancellationToken cancellationToken = default);
}
