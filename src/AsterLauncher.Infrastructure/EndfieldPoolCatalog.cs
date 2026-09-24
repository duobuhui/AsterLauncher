using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

// Publisher-announced operator pools through 2026-09-24. Unknown pools still display
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

    public static EndfieldPoolDefinition Resolve(string poolId, string poolName)
    {
        if (poolId.Equals("standard", StringComparison.OrdinalIgnoreCase)
            || poolName.Equals("基础寻访", StringComparison.Ordinal))
        {
            return new(EndfieldPoolCategory.Standard, null, "standard", poolId, "endfield-pool-card.svg");
        }

        if (CharteredFeatured.TryGetValue(poolName, out var featured))
        {
            return new(EndfieldPoolCategory.Chartered, featured.Featured, "chartered", poolId, featured.Banner);
        }

        if (poolName.StartsWith("绚丽异彩", StringComparison.Ordinal))
        {
            return new(EndfieldPoolCategory.Refactor, "伊冯", "refactor", "绚丽异彩", "Gacha/refactor-vivid.png");
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
