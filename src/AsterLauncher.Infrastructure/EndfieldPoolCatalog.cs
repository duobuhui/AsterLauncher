using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

// Bundled operator pools through 2026-09-24; verified resource definitions extend this list. Unknown pools still display
// their recorded draws, but receive no inferred featured operator or guarantee rule.
internal static class EndfieldPoolCatalog
{
    private static readonly IReadOnlyDictionary<string, (string Featured, string Banner)> CharteredFeatured =
        new Dictionary<string, (string Featured, string Banner)>(StringComparer.Ordinal)
        {
            ["熔火灼痕"] = ("莱万汀", "Gacha/molten.png"),
            ["轻飘飘的信使"] = ("洁尔佩塔", "Gacha/messenger.png"),
            ["热烈色彩"] = ("伊冯", "Gacha/vivid.png"),
            ["河流的女儿"] = ("汤汤", "Gacha/river.jpg"),
            ["狼珀"] = ("洛茜", "Gacha/wolf.jpg"),
            ["春雷动，万物生"] = ("庄方宜", "Gacha/spring-thunder.jpg"),
            ["拳出无悔"] = ("弭弗", "Gacha/unregretted.jpg"),
            ["逐罪者"] = ("卡缪", "Gacha/sinner.jpg"),
            ["临渊望北"] = ("诀", "Gacha/north.jpg"),
            ["晨星于此闪耀"] = ("梨诺", "Gacha/morning-star.jpg"),
            ["冬猎"] = ("提弗洛斯", "Gacha/winter.jpg")
        };

    public static string RefactorSeriesName(string poolName)
    {
        var trimmed = poolName.Trim();
        var hash = trimmed.LastIndexOfAny(['#', '＃']);
        var withoutPhase = hash >= 0 && int.TryParse(trimmed[(hash + 1)..].Trim(), out var phase) && phase > 0
            ? trimmed[..hash].TrimEnd()
            : trimmed;
        return withoutPhase.EndsWith("重构寻访", StringComparison.Ordinal)
            ? withoutPhase[..^"重构寻访".Length].TrimEnd()
            : withoutPhase;
    }

    public static string RefactorDisplayName(string poolName, string? poolVersion)
    {
        var series = RefactorSeriesName(poolName);
        return int.TryParse(poolVersion, out var phase) && phase > 0
            ? $"{series} #{phase}"
            : poolName;
    }

    public static int RefactorPhaseNumber(string poolName, string? poolVersion)
    {
        if (int.TryParse(poolVersion, out var version) && version > 0)
        {
            return version;
        }

        var hash = poolName.LastIndexOfAny(['#', '＃']);
        return hash >= 0 && int.TryParse(poolName[(hash + 1)..].Trim(), out var namedVersion)
            ? namedVersion
            : 0;
    }

    public static EndfieldPoolDefinition Resolve(string poolId, string poolName, ResourceCatalog? resources = null, string? phase = null)
    {
        var resource = resources?.Pools.FirstOrDefault(p => p.GameId == BuiltInGameIds.Endfield && p.Key == poolName &&
            (p.Category == "chartered" && p.Phase is null || p.Category == "refactor" &&
             p.Phase == RefactorPhaseNumber(poolName, phase).ToString()));
        if (resource?.FeaturedOperator is { Length: > 0 } featuredOperator)
        {
            var refactor = resource.Category == "refactor";
            return new(refactor ? EndfieldPoolCategory.Refactor : EndfieldPoolCategory.Chartered,
                featuredOperator, refactor ? "refactor" : "chartered",
                refactor ? $"refactor:{RefactorSeriesName(poolName)}" : poolId, "endfield-pool-card.svg");
        }
        if (poolId.Equals("standard", StringComparison.OrdinalIgnoreCase)
            || poolName.Equals("基础寻访", StringComparison.Ordinal))
        {
            return new(EndfieldPoolCategory.Standard, null, "standard", poolId, "endfield-pool-card.svg");
        }

        var refactorSeries = RefactorSeriesName(poolName);
        if (poolId.StartsWith("rerun_chr_", StringComparison.OrdinalIgnoreCase)
            || refactorSeries.Equals("绚丽异彩", StringComparison.Ordinal))
        {
            var known = refactorSeries.Equals("绚丽异彩", StringComparison.Ordinal);
            return new(EndfieldPoolCategory.Refactor, known ? "伊冯" : null, "refactor",
                $"refactor:{refactorSeries}", known ? "Gacha/refactor-vivid.png" : "endfield-pool-card.svg");
        }

        if (CharteredFeatured.TryGetValue(poolName, out var featured))
        {
            return new(EndfieldPoolCategory.Chartered, featured.Featured, "chartered", poolId, featured.Banner);
        }

        if (poolName.Equals("辉光庆典", StringComparison.Ordinal))
        {
            return new(EndfieldPoolCategory.Celebration, null, $"celebration:{poolId}", poolId, "Gacha/celebration.jpg");
        }

        return new(EndfieldPoolCategory.Other, null, $"other:{poolId}", poolId, "endfield-pool-card.svg");
    }
}

internal sealed record EndfieldPoolDefinition(
    EndfieldPoolCategory Category,
    string? FeaturedOperator,
    string PityFamily,
    string UpSeries,
    string BannerAssetFileName);
