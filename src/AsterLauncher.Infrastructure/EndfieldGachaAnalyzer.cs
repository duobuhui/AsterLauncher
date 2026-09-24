using System.Globalization;
using System.Text.Json.Nodes;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

internal static class EndfieldGachaAnalyzer
{
    public static EndfieldGachaAnalysis Analyze(JsonObject archive)
    {
        var draws = new List<DrawRecord>();
        var index = 0;
        foreach (var record in (archive["characters"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var rarity = ReadLong(record, "rarity");
            var kind = ReadText(record, "kind");
            // The official archive also contains gift events. Older imports may omit kind.
            if ((kind is not null && !kind.Equals("draw", StringComparison.OrdinalIgnoreCase))
                || (kind is null && rarity <= 0))
            {
                continue;
            }

            var poolId = ReadText(record, "poolId", "pool_id")
                ?? ReadText(record, "poolName", "pool_name")
                ?? "未分类卡池";
            var poolName = ReadText(record, "poolName", "pool_name") ?? poolId;
            var timestamp = ReadLong(record, "gachaTs", "gacha_ts");
            if (timestamp is > 0 and < 100_000_000_000)
            {
                timestamp *= 1000;
            }

            draws.Add(new DrawRecord(
                poolId,
                poolName,
                ReadText(record, "charName", "nameText", "name") ?? "未知干员",
                rarity,
                ReadBoolean(record, "isFree", "is_free"),
                timestamp,
                ReadLong(record, "seqId", "seq_id"),
                index++));
        }

        var poolDrawCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var countedDrawsSinceSixStar = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sixStars = new List<EndfieldSixStarPull>();
        var orderedDraws = draws.OrderBy(item => item.Timestamp)
                     .ThenBy(item => item.Sequence)
                     .ThenBy(item => item.Index).ToArray();
        var definitions = orderedDraws.GroupBy(item => item.PoolId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => EndfieldPoolCatalog.Resolve(group.Key, group.Last().PoolName),
                StringComparer.OrdinalIgnoreCase);
        foreach (var draw in orderedDraws)
        {
            poolDrawCounts.TryGetValue(draw.PoolId, out var count);
            var poolDrawNumber = count + 1;
            poolDrawCounts[draw.PoolId] = poolDrawNumber;
            countedDrawsSinceSixStar.TryGetValue(draw.PoolId, out var countedDraws);
            if (!draw.IsFree)
            {
                countedDraws++;
                countedDrawsSinceSixStar[draw.PoolId] = countedDraws;
            }
            if (draw.Rarity != 6)
            {
                continue;
            }

            sixStars.Add(new EndfieldSixStarPull(
                draw.OperatorName,
                draw.PoolName,
                poolDrawNumber,
                countedDraws,
                draw.IsFree,
                ToDateTime(draw.Timestamp),
                string.Equals(draw.OperatorName, definitions[draw.PoolId].FeaturedOperator, StringComparison.Ordinal),
                PoolId: draw.PoolId));
            if (!draw.IsFree)
            {
                countedDrawsSinceSixStar[draw.PoolId] = 0;
            }
        }

        var pools = orderedDraws.GroupBy(item => item.PoolId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var poolSixStars = sixStars.Where(item => string.Equals(item.PoolId, group.Key, StringComparison.OrdinalIgnoreCase))
                    .Reverse().ToArray();
                var definition = definitions[group.Key];
                var featuredName = definition.FeaturedOperator;
                var upSeriesDraws = definition.Category == EndfieldPoolCategory.Refactor
                    ? orderedDraws.Where(item => definitions[item.PoolId].UpSeries == definition.UpSeries)
                    : group;
                var paidPosition = 0;
                int? featuredPaidPosition = null;
                var hasFreeFeatured = false;
                foreach (var draw in upSeriesDraws)
                {
                    if (!draw.IsFree)
                    {
                        paidPosition++;
                    }

                    if (!string.Equals(draw.PoolId, group.Key, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(draw.OperatorName, featuredName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (draw.IsFree)
                    {
                        hasFreeFeatured = true;
                    }
                    else
                    {
                        featuredPaidPosition ??= paidPosition;
                    }
                }
                return new EndfieldPoolAnalysis(
                    group.Key,
                    group.Last().PoolName,
                    group.Count(),
                    group.Count(item => !item.IsFree),
                    group.Count(item => item.IsFree),
                    group.Count(item => item.IsFree && item.Rarity == 6),
                    definition.Category,
                    featuredName,
                    definition.BannerAssetFileName,
                    poolSixStars,
                    featuredPaidPosition,
                    hasFreeFeatured);
            })
            .OrderBy(item => item.Category == EndfieldPoolCategory.Standard ? 1 : 0)
            .ThenByDescending(item => orderedDraws.Last(draw => string.Equals(draw.PoolId, item.PoolId, StringComparison.OrdinalIgnoreCase)).Timestamp)
            .ToArray();

        var pityCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var draw in orderedDraws.Where(item => !item.IsFree))
        {
            var family = definitions[draw.PoolId].PityFamily;
            pityCounters.TryGetValue(family, out var count);
            pityCounters[family] = draw.Rarity == 6 ? 0 : count + 1;
        }

        var lastLimitedDraw = orderedDraws.LastOrDefault(item =>
            definitions[item.PoolId].Category is EndfieldPoolCategory.Chartered or EndfieldPoolCategory.Refactor);
        var limitedFamily = lastLimitedDraw is null ? null : definitions[lastLimitedDraw.PoolId].PityFamily;
        var latestUpPool = pools.FirstOrDefault(item =>
            item.Category is EndfieldPoolCategory.Chartered or EndfieldPoolCategory.Refactor);
        var upSeries = latestUpPool is null ? null : definitions[latestUpPool.PoolId].UpSeries;
        var upPaidCount = upSeries is null ? 0 : orderedDraws.Count(item =>
            !item.IsFree && definitions[item.PoolId].UpSeries == upSeries);
        var upRecorded = latestUpPool is not null && orderedDraws.Any(item =>
            !item.IsFree
            && definitions[item.PoolId].UpSeries == upSeries
            && string.Equals(item.OperatorName, latestUpPool.FeaturedOperatorName, StringComparison.Ordinal));
        var pity = new EndfieldPityOverview(
            pityCounters.TryGetValue("standard", out var standardCount) ? standardCount : null,
            limitedFamily is not null && pityCounters.TryGetValue(limitedFamily, out var limitedCount) ? limitedCount : null,
            limitedFamily switch { "chartered" => "特许", "refactor" => "重构", _ => null },
            latestUpPool?.PoolName,
            latestUpPool is not null && !upRecorded && upPaidCount < 120 ? 120 - upPaidCount : null,
            upRecorded);

        return new EndfieldGachaAnalysis(
            draws.Count,
            sixStars.Count,
            sixStars.Count(item => item.IsFree),
            sixStars.AsEnumerable().Reverse().ToArray(),
            pools,
            pity);
    }

    private static DateTimeOffset? ToDateTime(long timestamp) =>
        timestamp is > 0 and <= 253_402_300_799_999
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
            : null;

    private static string? ReadText(JsonObject record, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (record[key] is JsonValue value)
            {
                var text = value.ToString().Trim();
                if (text.Length > 0)
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static long ReadLong(JsonObject record, params string[] keys)
    {
        var text = ReadText(record, keys);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static bool ReadBoolean(JsonObject record, params string[] keys) =>
        bool.TryParse(ReadText(record, keys), out var value) && value;

    private sealed record DrawRecord(
        string PoolId,
        string PoolName,
        string OperatorName,
        long Rarity,
        bool IsFree,
        long Timestamp,
        long Sequence,
        int Index);
}
