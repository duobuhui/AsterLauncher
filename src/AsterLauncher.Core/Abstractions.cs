namespace AsterLauncher.Core;

public interface IGameAdapter
{
    GameDefinition Definition { get; }

    IGameInstallLocator InstallLocator { get; }

    IGameProcessDetector ProcessDetector { get; }

    InstallScanResult ValidateManualExecutable(string executablePath);
}

public interface IStateAwareGameAdapter
{
    IGameAdapter WithSavedExecutablePath(string? executablePath);
}

public interface IUpdatableInstallLocator
{
    void UpdateSavedPath(string? executablePath);
}

public interface IGameInstallLocator
{
    Task<IReadOnlyList<InstallScanResult>> ScanAsync(
        IProgress<ScanObservation>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IGameProcessDetector
{
    Task<bool> IsRunningAsync(
        GameDefinition game,
        string? expectedExecutablePath,
        CancellationToken cancellationToken = default);

    Task<bool> WaitForStartAsync(
        GameDefinition game,
        string? expectedExecutablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task WaitForExitAsync(
        GameDefinition game,
        string? expectedExecutablePath,
        CancellationToken cancellationToken = default);
}

public interface ICompanionProcessService
{
    Task<ProcessLaunchResult> StartAsync(
        LaunchStep step,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task WaitForExitAsync(OwnedProcessToken process, CancellationToken cancellationToken = default);

    Task StopOwnedProcessesAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public interface ILaunchDecisionService
{
    Task<bool> ShouldContinueAsync(
        LaunchStep failedStep,
        ProcessLaunchResult failure,
        CancellationToken cancellationToken = default);
}

public interface IConfigurationStore
{
    string ConfigurationPath { get; }

    Task<LauncherConfiguration> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(LauncherConfiguration configuration, CancellationToken cancellationToken = default);
}

public interface IGachaProvider
{
    string GameId { get; }

    Task<GachaImportResult> ImportAsync(CancellationToken cancellationToken = default);
}

public sealed record GachaImportResult(bool Success, int ImportedCount, string Message);

public interface IUigfArchiveService
{
    string ArchivePath { get; }

    Task<UigfArchiveSummary> GetSummaryAsync(CancellationToken cancellationToken = default);

    Task<GachaImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);

    Task<GachaImportResult> ExportAsync(string destinationPath, CancellationToken cancellationToken = default);

    Task<GachaImportResult> CaptureFromGameAsync(
        string gameId,
        string executablePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record UigfArchiveSummary(
    string Version,
    int GenshinCount,
    int StarRailCount,
    int ZenlessCount,
    DateTimeOffset? UpdatedAt)
{
    public int TotalCount => GenshinCount + StarRailCount + ZenlessCount;
}

public interface IEndfieldGachaArchiveService
{
    string ArchivePath { get; }

    Task<EndfieldGachaSummary> GetSummaryAsync(CancellationToken cancellationToken = default);

    Task<EndfieldGachaAnalysis> GetAnalysisAsync(CancellationToken cancellationToken = default);

    Task<GachaImportResult> CaptureFromGameAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<GachaImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);

    Task<GachaImportResult> ExportJsonAsync(string destinationPath, CancellationToken cancellationToken = default);

    Task<GachaImportResult> ExportCsvAsync(string destinationPath, CancellationToken cancellationToken = default);
}

public sealed record EndfieldGachaSummary(int CharacterCount, int WeaponCount, DateTimeOffset? UpdatedAt)
{
    public int TotalCount => CharacterCount + WeaponCount;
}

public sealed record EndfieldGachaAnalysis(
    int CharacterDrawCount,
    int SixStarCount,
    int FreeSixStarCount,
    IReadOnlyList<EndfieldSixStarPull> SixStarPulls,
    IReadOnlyList<EndfieldPoolAnalysis> Pools,
    EndfieldPityOverview Pity)
{
    public static EndfieldGachaAnalysis Empty { get; } = new(0, 0, 0, [], [], EndfieldPityOverview.Empty);
}

public enum EndfieldPoolCategory
{
    Standard,
    Chartered,
    Refactor,
    Celebration,
    Other
}

public sealed record EndfieldPityOverview(
    int? StandardSinceSixStar,
    int? LimitedSinceSixStar,
    string? LimitedFamily,
    string? UpPoolName,
    int? UpRemaining,
    bool UpRecorded)
{
    public static EndfieldPityOverview Empty { get; } = new(null, null, null, null, null, false);

    public string StandardText => StandardSinceSixStar is int count ? $"{count} / 80" : "—";

    public string LimitedText => LimitedSinceSixStar is int count ? $"{count} / 80" : "—";

    public string LimitedLabel => LimitedFamily is null ? "小保底已垫" : $"小保底已垫 · {LimitedFamily}";

    public string UpText => UpRecorded ? "已出" : UpRemaining is int count ? $"{count} 抽" : "—";

    public string UpLabel => UpPoolName is null ? "UP池剩余" : $"UP池剩余 · {UpPoolName}";
}

public sealed record EndfieldPoolAnalysis(
    string PoolId,
    string PoolName,
    int DrawCount,
    int PaidDrawCount,
    int FreeDrawCount,
    int FreeSixStarCount,
    EndfieldPoolCategory Category,
    string? FeaturedOperatorName,
    string BannerAssetFileName,
    IReadOnlyList<EndfieldSixStarPull> SixStarPulls,
    int? FeaturedPaidDrawNumber,
    bool HasFreeFeatured)
{
    public string CountText => $"{DrawCount} 抽 · {SixStarPulls.Count} 六星";

    public string DetailText => FreeDrawCount > 0
        ? $"计数 {PaidDrawCount} · 免费 {FreeDrawCount}（六星 {FreeSixStarCount}）"
        : $"计数 {PaidDrawCount} · 免费无记录";

    public string FeaturedText => FeaturedOperatorName is null ? Category switch
    {
        EndfieldPoolCategory.Standard => "常驻 · 独立计数",
        EndfieldPoolCategory.Celebration => "特殊寻访 · 独立计数",
        _ => "本池记录"
    } : $"UP {FeaturedOperatorName}";

    public string FeaturedObservationText => FeaturedOperatorName is null
        ? ""
        : FeaturedPaidDrawNumber is int drawNumber
            ? $"第 {drawNumber} 抽"
            : HasFreeFeatured
                ? "仅免费出"
                : "未见";

    public string StatusText => FeaturedOperatorName is null
        ? RuleText
        : $"{RuleText} · {FeaturedOperatorName} {FeaturedObservationText}";

    public string OperatorSummary => SixStarPulls.Count == 0
        ? "暂无六星记录"
        : string.Join("  ·  ", SixStarPulls.Select(item => item.OperatorName + (item.IsFeatured ? " UP" : "")));

    public string RuleText => Category switch
    {
        EndfieldPoolCategory.Chartered => "80 六星 · 本池 120 UP",
        EndfieldPoolCategory.Refactor => "80 六星 · 同名系列 120 UP",
        EndfieldPoolCategory.Celebration => "80 六星 · 120 抽自选凭证",
        EndfieldPoolCategory.Standard => "常驻保底独立",
        _ => "条长按本池记录比较"
    };
}

public sealed record EndfieldSixStarPull(
    string OperatorName,
    string PoolName,
    int PoolDrawNumber,
    int CountedDrawsSincePaidSixStar,
    bool IsFree,
    DateTimeOffset? ObtainedAt,
    bool IsFeatured = false,
    string PoolId = "")
{
    public string OperatorDisplayName => IsFree ? $"{OperatorName}（免费）" : OperatorName;

    public string PoolDrawText => $"第 {PoolDrawNumber} 抽";

    public string IntervalText => $"{CountedDrawsSincePaidSixStar} 抽";

    public string SourceText => IsFree ? "免费" : "常规";

    public string ObtainedAtText => ObtainedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "—";

    public string BarTitle => IsFree ? (IsFeatured ? "免费 UP 六星" : "免费六星") : IsFeatured ? "UP 六星" : "六星";

    public string RecordedSpanText => IsFree
        ? $"免费 · 已垫 {CountedDrawsSincePaidSixStar}"
        : $"{CountedDrawsSincePaidSixStar} 抽";

    public double BarPercent => Math.Clamp(CountedDrawsSincePaidSixStar * 100d / 80d, 0d, 100d);

    public bool IsOver65 => CountedDrawsSincePaidSixStar > 65;
}
