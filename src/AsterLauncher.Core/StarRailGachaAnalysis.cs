namespace AsterLauncher.Core;

public sealed record StarRailGachaAnalysis(IReadOnlyList<StarRailAccountAnalysis> Accounts)
{
    public static StarRailGachaAnalysis Empty { get; } = new([]);
}

public sealed record StarRailAccountAnalysis(
    string Uid,
    int RecordCount,
    IReadOnlyList<StarRailWarpSection> Sections)
{
    public string AccountLabel => Uid.Length > 6 ? $"UID {Uid[..3]}•••{Uid[^3..]}" : "UID •••";
}

public sealed record StarRailWarpSection(
    string Type,
    string Title,
    int RecordCount,
    int FiveStarCount,
    int FourStarCount,
    int ThreeStarCount,
    int PityCount,
    int? PityMaximum,
    string DateRange,
    IReadOnlyList<StarRailFiveStarPull> FiveStars,
    IReadOnlyList<StarRailWarpBanner> Banners)
{
    public string CountText => $"{RecordCount} 抽";
    public string FiveStarStat => $"5★ {FiveStarCount} [{FiveStarCount * 100d / RecordCount:0.00}%]";
    public string FourStarStat => $"4★ {FourStarCount} [{FourStarCount * 100d / RecordCount:0.00}%]";
    public string ThreeStarStat => $"3★ {ThreeStarCount} [{ThreeStarCount * 100d / RecordCount:0.00}%]";
    public string AverageText
    {
        get
        {
            var complete = FiveStars.Where(pull => pull.HasPreviousFiveStar).ToArray();
            return complete.Length == 0 ? "均抽 —" : $"均抽 {complete.Average(pull => pull.LocalPullCount):0.0}";
        }
    }
    public string BreakdownLabel => $"按卡池查看 · {Banners.Count}";
    public string PityText => PityMaximum is int maximum ? $"本地水位 {PityCount} / {maximum}" : "水位规则待核对";
    public double PityPercent => PityMaximum is int maximum ? Math.Clamp(PityCount * 100d / maximum, 0, 100) : 0;
    public bool HasPityMaximum => PityMaximum is not null;
    public IReadOnlyList<StarRailWarpBanner> RecentBanners => Banners.Take(1).ToArray();
    public IReadOnlyList<StarRailWarpBanner> PreviousBanners => Banners.Skip(1).ToArray();
    public int PreviousBannerCount => Math.Max(0, Banners.Count - 1);
    public string PreviousBannerLabel => $"往期卡池 · {PreviousBannerCount}";
}

public sealed record StarRailWarpBanner(
    string PoolId,
    string Title,
    string DateText,
    int RecordCount,
    IReadOnlyList<StarRailFiveStarPull> FiveStars)
{
    public string CountText => $"{RecordCount} 抽 · {FiveStars.Count} 五星";
    public string StarNames => FiveStars.Count == 0 ? "暂无五星" : string.Join(" · ", FiveStars.Take(3).Select(pull => pull.Name)) + (FiveStars.Count > 3 ? $" 等 {FiveStars.Count} 位" : string.Empty);
    public string PoolTooltip => $"卡池编号 {PoolId}；出货间隔按同类跃迁连续计算。";
}

public sealed record StarRailFiveStarPull(
    string Name,
    int LocalPullCount,
    bool HasPreviousFiveStar,
    int PityMaximum,
    string TimeText)
{
    public string PullCountText => HasPreviousFiveStar ? $"{LocalPullCount} 抽" : $"本地 ≥{LocalPullCount} 抽";
    public double BarPercent => PityMaximum > 0 ? Math.Clamp(LocalPullCount * 100d / PityMaximum, 0, 100) : 0;
    public double BarFraction => BarPercent / 100d;
    public bool IsHighCount => PityMaximum > 0 && LocalPullCount > PityMaximum - 15;
}
