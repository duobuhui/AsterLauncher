namespace AsterLauncher.Core;

public enum HoYoChannel { Unknown = 0, Official = 1, Bilibili = 14 }

public sealed class HoYoInstallation
{
    public Guid InstallationId { get; set; } = Guid.NewGuid();
    public HoYoChannel Channel { get; set; } = HoYoChannel.Official;
    public string? ExecutablePath { get; set; }
    public bool ChannelConfirmed { get; set; }
    public bool ShareResources { get; set; } = true;
    public Guid? SelectedLaunchProfileId { get; set; }
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
    string ExecutableName, string DirectoryName, string? PreloadVersion, string? AudioRecordPath = null,
    HoYoChannel Channel = HoYoChannel.Official);
public sealed record HoYoChunk(string Id, string Md5, string CompressedMd5, long Offset,
    long Size, long CompressedSize, Uri Uri, int Compression);
public sealed record HoYoFile(string Path, long Size, string Md5, IReadOnlyList<HoYoChunk> Chunks, HoYoSdkSource? SdkEntry = null)
{
    public EndfieldManifestFile Integrity => new(Path, Size, Md5);
}
public sealed record HoYoSdkSource(string CacheKey, string EntryPath);
public sealed record HoYoSdk(Uri Uri, string Md5, long Size, long ExpandedSize, string Version, string VersionFile);
public sealed record HoYoPackage(HoYoRelease Release, IReadOnlyList<HoYoFile> Files, string ContentHash, HoYoSdk? Sdk = null, IReadOnlyList<string>? RemovedSdkFiles = null);
public sealed record HoYoReusableFile(HoYoFile File, string SourcePath);
public sealed record HoYoPlan(HoYoPackage Package, IReadOnlyList<HoYoFile> Missing,
    IReadOnlyList<HoYoFile> Corrupt, long DownloadBytes, long RequiredFreeBytes,
    IReadOnlyList<HoYoReusableFile>? Reusable = null, string? SharingFallback = null);
public sealed record HoYoProgress(string Stage, int CompletedFiles, int TotalFiles,
    long CompletedBytes, long TotalBytes, string? CurrentFile = null, bool IsNetworkTransfer = false,
    long? TransferredBytes = null);
public interface IHoYoDistributionProvider
{
    Task<HoYoRelease?> GetReleaseAsync(string gameId, CancellationToken token = default);
    Task<HoYoRelease?> GetReleaseAsync(string gameId, HoYoChannel channel, CancellationToken token = default)
        => channel == HoYoChannel.Official ? GetReleaseAsync(gameId, token) : throw new NotSupportedException("此提供器没有该渠道。");
    Task<HoYoPackage> GetPackageAsync(string gameId, HoYoChannel channel, IReadOnlyCollection<string> languages, bool preload = false, CancellationToken token = default)
        => channel == HoYoChannel.Official ? GetPackageAsync(gameId, languages, preload, token) : throw new NotSupportedException("此提供器没有该渠道。");
    Task<HoYoPackage> GetPackageAsync(string gameId, IReadOnlyCollection<string> audioLanguages,
        bool preload = false, CancellationToken token = default);
}