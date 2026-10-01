namespace AsterLauncher.Core;

/// <summary>Read-only analysis of one game's UIGF archive. Archive formats remain unchanged.</summary>
public sealed record UigfGachaAnalysis(
    string GameId, string HighRarityLabel,
    IReadOnlyList<UigfPoolDefinition> Categories,
    IReadOnlyList<UigfAccountAnalysis> Accounts)
{
    public static UigfGachaAnalysis Empty { get; } = new("", "五星", [], []);
}

public sealed record UigfPoolDefinition(string Type, string Title, int? PityMaximum, bool IsFeaturedCharacter = false);
public sealed record UigfAccountAnalysis(string Uid, int RecordCount, IReadOnlyList<UigfPoolSection> Sections)
{
    public string AccountLabel => Uid.Length > 6 ? $"UID {Uid[..3]}•••{Uid[^3..]}" : "UID •••";
}

public sealed record UigfPoolSection(
    string Type, string Title, int RecordCount,
    int HighRarityCount, int MiddleRarityCount, int LowRarityCount,
    int PityCount, int? PityMaximum, bool IsFeaturedCharacter,
    string HighRarityLabel, string MiddleRarityLabel, string LowRarityLabel,
    string DateRange, IReadOnlyList<UigfHighRarityPull> HighRarityPulls,
    IReadOnlyList<UigfBannerAnalysis> Banners)
{
    public string CountText => $"{RecordCount} 抽";
    public string HighRarityStat => Stat(HighRarityLabel, HighRarityCount);
    public string MiddleRarityStat => Stat(MiddleRarityLabel, MiddleRarityCount);
    public string LowRarityStat => Stat(LowRarityLabel, LowRarityCount);
    private string Stat(string label, int count) => $"{label} {count} [{(RecordCount > 0 ? count * 100d / RecordCount : 0):0.00}%]";
    public string AverageText
    {
        get
        {
            var complete = HighRarityPulls.Where(pull => pull.HasPreviousHighRarity).ToArray();
            return complete.Length == 0 ? "均抽 —" : $"均抽 {complete.Average(pull => pull.LocalPullCount):0.0}";
        }
    }
    public string BreakdownLabel => $"按卡池编号查看 · {Banners.Count}";
    public int PreviousBannerCount => Math.Max(0, Banners.Count - 1);
    public double PityPercent => PityMaximum is > 0 ? Math.Clamp(PityCount * 100d / PityMaximum.Value, 0, 100) : 0;
}

public sealed record UigfHighRarityPull(
    string GameId, string Name, string ItemType, int LocalPullCount,
    bool HasPreviousHighRarity, int PityMaximum, string TimeText, string RecordId = "", string BannerId = "", string RawTime = "")
{
    public string PortraitKey => GameId + "\u001F" + Name;
    public string PullCountText => HasPreviousHighRarity ? $"{LocalPullCount} 抽" : $"本地 ≥{LocalPullCount} 抽";
    public double BarFraction => PityMaximum > 0 ? Math.Clamp(LocalPullCount * 1d / PityMaximum, 0, 1) : 0;
    public bool IsHighCount => PityMaximum > 0 && LocalPullCount > PityMaximum - 15;
}

public sealed record UigfBannerAnalysis(
    string PoolId, string Title, string DateText, int RecordCount,
    string HighRarityLabel, IReadOnlyList<UigfHighRarityPull> HighRarityPulls)
{
    public string CountText => $"{RecordCount} 抽 · {HighRarityPulls.Count} {HighRarityLabel}";
    public string StarNames => HighRarityPulls.Count == 0 ? $"暂无{HighRarityLabel}"
        : string.Join(" · ", HighRarityPulls.Take(3).Select(pull => pull.Name))
          + (HighRarityPulls.Count > 3 ? $" 等 {HighRarityPulls.Count} 项" : "");
    public string PoolTooltip => $"卡池编号 {PoolId}；出货间隔按同类卡池连续计算。";
}

public sealed record UigfPityIndicator(string Title, int? Count, int? Maximum, bool HasKnownHighRarity = true)
{
    public string Text => Count is null ? "—" : Maximum is > 0 ? $"{(HasKnownHighRarity ? "" : "≥")}{Count} / {Maximum}" : $"{Count} 抽";
    public double Percent => Count is int count && Maximum is > 0 ? Math.Clamp(count * 100d / Maximum.Value, 0, 100) : 0;
    public bool IsHighCount => Count is int count && Maximum is > 0 && count > Maximum - 15;
}