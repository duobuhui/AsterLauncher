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

    Task<StarRailGachaAnalysis> GetStarRailAnalysisAsync(CancellationToken cancellationToken = default);
    Task<UigfGachaAnalysis> GetAnalysisAsync(string gameId, CancellationToken cancellationToken = default);

    Task<GachaImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);

    Task<GachaImportResult> ExportAsync(string destinationPath, CancellationToken cancellationToken = default);

    Task<GachaImportResult> CaptureFromUrlAsync(
        string gameId,
        string historyUrl,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool fullRefresh = false);

    Task<GachaImportResult> CaptureFromGameAsync(
        string gameId,
        string executablePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool fullRefresh = false);
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

    Task<GachaImportResult> CaptureFromUrlAsync(
        string historyUrl,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool fullRefresh = false);

    Task<GachaImportResult> CaptureFromGameAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool fullRefresh = false);

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
    public IReadOnlyList<EndfieldWeaponPoolStatistics> WeaponPools { get; init; } = [];
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
    bool UpRecorded,
    int? UpPaidCount = null,
    bool UpIsRefactor = false,
    bool UpOperatorKnown = true)
{
    public static EndfieldPityOverview Empty { get; } = new(null, null, null, null, null, false);

    public string StandardText => StandardSinceSixStar is int count ? $"{count} / 80" : "—";

    public string LimitedText => LimitedSinceSixStar is int count ? $"{count} / 80" : "—";

    public string LimitedLabel => LimitedFamily == "重构" ? "重构六星 · 共用" :
        LimitedFamily is null ? "小保底已垫" : $"小保底已垫 · {LimitedFamily}";

    public string UpText => UpPoolName is null ? "—" : !UpOperatorKnown ? "待核对" : UpRecorded ? "已出" :
        UpIsRefactor && UpPaidCount is >= 120 ? "待核对" :
        UpIsRefactor && UpPaidCount is int paid ? $"{paid} / 120" :
        UpRemaining is int count ? $"剩 {count} 抽" : "—";

    public string UpLabel => UpIsRefactor && UpPoolName is not null ? $"同名首 UP · {UpPoolName}" :
        UpPoolName is null ? "UP池剩余" : $"UP池剩余 · {UpPoolName}";

    public double LimitedPercent => Math.Clamp((LimitedSinceSixStar ?? 0) * 100d / 80d, 0d, 100d);

    public double UpPercent => Math.Clamp((UpPaidCount ?? 0) * 100d / 120d, 0d, 100d);

    public bool LimitedOver65 => LimitedSinceSixStar > 65;
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
    bool HasFreeFeatured,
    string PoolKey = "",
    string? PoolVersion = null,
    int? SeriesPaidDrawCount = null,
    bool SeriesUpRecorded = false,
    string SeriesKey = "",
    IReadOnlyList<EndfieldPoolAnalysis>? PreviousPhases = null)
{
    public string BannerResourceKey { get; init; } = "";
    public string BannerImageKey => BannerResourceKey + "\u001F" + BannerAssetFileName;
    public string DateRangeText { get; init; } = "";
    public int DateSectionCount => string.IsNullOrEmpty(DateRangeText) ? 0 : 1;
    public int DetailSectionCount => string.IsNullOrEmpty(DetailText) ? 0 : 1;
    public string? AnnouncementUri { get; init; }
    public EndfieldSignatureWeaponAnalysis? SignatureWeapon { get; init; }
    public string WeaponSummaryText => SignatureWeapon?.Summary ?? "";
    public int WeaponSectionCount => SignatureWeapon is null ? 0 : 1;
    public int PreviousPhaseCount => PreviousPhases?.Count ?? 0;

    public string PreviousPhaseLabel => $"往期记录 · {PreviousPhaseCount} 期";

    public string CountText => $"{DrawCount} 抽 · {SixStarPulls.Count} 六星";

    public string DetailText => Category == EndfieldPoolCategory.Refactor
        ? $"本期计数 {PaidDrawCount} · 同名累计 {SeriesPaidDrawCount ?? PaidDrawCount}" + (FreeDrawCount > 0 ? $" · 免费 {FreeDrawCount}（六星 {FreeSixStarCount}）" : "")
        : FreeDrawCount > 0
            ? $"计数 {PaidDrawCount} · 免费 {FreeDrawCount}（六星 {FreeSixStarCount}）"
            : "";

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

    public string RefactorSeriesStatus => SeriesUpRecorded ? "已出" :
        SeriesPaidDrawCount is >= 120 ? "待核对" : $"{SeriesPaidDrawCount ?? 0}/120";

    public string StatusText => Category == EndfieldPoolCategory.Refactor
        ? FeaturedOperatorName is null ? "重构共用 80 · 同名首次 UP 待核对"
            : $"重构共用 80 · 同名首次 UP {RefactorSeriesStatus} · {FeaturedOperatorName} 本期{FeaturedObservationText}"
        : FeaturedOperatorName is null ? RuleText
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
    string PoolId = "",
    string PoolKey = "")
{
    public string OperatorDisplayName => IsFree ? $"{OperatorName}（免费）" : OperatorName;

    public string PoolDrawText => $"第 {PoolDrawNumber} 抽";

    public string IntervalText => $"{CountedDrawsSincePaidSixStar} 抽";

    public string SourceText => IsFree ? "免费" : "常规";

    public string ObtainedAtText => ObtainedAt?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy/MM/dd HH:mm") ?? "—";

    public string BarTitle => IsFree ? (IsFeatured ? "免费 UP 六星" : "免费六星") : IsFeatured ? "UP 六星" : "六星";

    public string RecordedSpanText => IsFree
        ? $"免费 · 已垫 {CountedDrawsSincePaidSixStar}"
        : $"{CountedDrawsSincePaidSixStar} 抽";

    public double BarPercent => Math.Clamp(CountedDrawsSincePaidSixStar * 100d / 80d, 0d, 100d);

    public bool IsOver65 => CountedDrawsSincePaidSixStar > 65;
}

public sealed record EndfieldWeaponResult(string Name, bool IsFeatured, int RecordPosition, DateTimeOffset? ObtainedAt)
{
    public string DisplayName => Name + (IsFeatured ? " · UP" : "");
    public string PositionText => $"第 {RecordPosition} 件记录";
    public string DateText => ObtainedAt?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy/MM/dd HH:mm") ?? "时间未记录";
}
public sealed record EndfieldSignatureWeaponAnalysis(string WeaponName, string PoolName, int RecordedWeapons,
    int FeaturedCount, IReadOnlyList<EndfieldWeaponResult> SixStars, bool IsRefactor)
{
    public string Title => $"专武 · {WeaponName}";
    public string CountText => $"{RecordedWeapons} 件 · 六星 {SixStars.Count} · 专武 {FeaturedCount}";
    public string Summary => RecordedWeapons == 0 ? $"{WeaponName} · 暂无对应申领记录" : $"{WeaponName} · {RecordedWeapons} 件记录 · 六星 {SixStars.Count} · 专武 {FeaturedCount}";
    public string RulesText => IsRefactor
        ? "一次申领包含 10 件武器。当期最多 4 次申领获得六星；同名重构申领首次 8 次内获得 UP，首次 UP 计数可跨同名期数继承。"
        : "一次申领包含 10 件武器。武器池的开放时间可跨 3 次特许寻访，记录按对应申领池汇总，不与角色池混算。";
    public string RecordNote => "位置按本地记录累计件数显示，不代表申领次数或游戏中的保底进度。奖励、兑换获得的武器不计入。";
}