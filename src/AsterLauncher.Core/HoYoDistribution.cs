namespace AsterLauncher.Core;

public sealed class HoYoInstallation
{
    public Guid InstallationId { get; set; } = Guid.NewGuid();
    public string? InstallRoot { get; set; }
    public string? InstalledVersion { get; set; }
    public bool MaintenanceInProgress { get; set; }
    public List<string> AudioLanguages { get; set; } = ["zh-cn"];
    public bool AudioSelectionPending { get; set; }
    public bool OfficialChannelConfirmed { get; set; }
    public string? PreloadVersion { get; set; }
    public string? PreloadSourceVersion { get; set; }
    public string? PreloadContentHash { get; set; }
}

public sealed record HoYoRelease(string GameId, string PublisherId, string Version,
    string ExecutableName, string DirectoryName, string? PreloadVersion, string? AudioRecordPath = null);
public sealed record HoYoChunk(string Id, string Md5, string CompressedMd5, long Offset,
    long Size, long CompressedSize, Uri Uri, int Compression);
public sealed record HoYoFile(string Path, long Size, string Md5, IReadOnlyList<HoYoChunk> Chunks)
{
    public EndfieldManifestFile Integrity => new(Path, Size, Md5);
}
public sealed record HoYoPackage(HoYoRelease Release, IReadOnlyList<HoYoFile> Files, string ContentHash);
public sealed record HoYoPlan(HoYoPackage Package, IReadOnlyList<HoYoFile> Missing,
    IReadOnlyList<HoYoFile> Corrupt, long DownloadBytes, long RequiredFreeBytes);
public sealed record HoYoProgress(string Stage, int CompletedFiles, int TotalFiles,
    long CompletedBytes, long TotalBytes);
public interface IHoYoDistributionProvider
{
    Task<HoYoRelease?> GetReleaseAsync(string gameId, CancellationToken token = default);
    Task<HoYoPackage> GetPackageAsync(string gameId, IReadOnlyCollection<string> audioLanguages,
        bool preload = false, CancellationToken token = default);
}