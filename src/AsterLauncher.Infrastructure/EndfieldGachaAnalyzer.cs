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
            var poolVersion = ReadText(record, "poolVersion", "pool_version");
            var definition = EndfieldPoolCatalog.Resolve(poolId, poolName);
            // The observed rerun record includes poolVersion; later phases may reuse poolId/name.
            // Without a version, a distinct suffixed name is the only safe fallback.
            var poolKey = definition.Category == EndfieldPoolCategory.Refactor
                ? $"{poolId}\u001f{poolVersion ?? poolName}"
                : poolId;
            var timestamp = ReadLong(record, "gachaTs", "gacha_ts");
            if (timestamp is > 0 and < 100_000_000_000)
            {
                timestamp *= 1000;
            }

            draws.Add(new DrawRecord(
                poolId,
                poolName,
                poolVersion,
                poolKey,
                definition,
                ReadText(record, "charName", "nameText", "name") ?? "未知干员",
                rarity,
                ReadBoolean(record, "isFree", "is_free"),
                timestamp,
                ReadLong(record, "seqId", "seq_id"),
                index++));
        }

        var orderedDraws = draws.OrderBy(item => item.Timestamp)
            .ThenBy(item => item.Sequence)
            .ThenBy(item => item.Index).ToArray();
        var poolDrawCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var countedDrawsSinceSixStar = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sixStars = new List<EndfieldSixStarPull>();
        foreach (var draw in orderedDraws)
        {
            poolDrawCounts.TryGetValue(draw.PoolKey, out var poolCount);
            var poolDrawNumber = poolCount + 1;
            poolDrawCounts[draw.PoolKey] = poolDrawNumber;

            // Six-star pity follows the guarantee family, not an individual rerun phase.
            var family = draw.Definition.PityFamily;
            countedDrawsSinceSixStar.TryGetValue(family, out var countedDraws);
            if (!draw.IsFree)
            {
                countedDraws++;
                countedDrawsSinceSixStar[family] = countedDraws;
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
                string.Equals(draw.OperatorName, draw.Definition.FeaturedOperator, StringComparison.Ordinal),
                PoolId: draw.PoolId,
                PoolKey: draw.PoolKey));
            if (!draw.IsFree)
            {
                countedDrawsSinceSixStar[family] = 0;
            }
        }

        var phasePools = orderedDraws.GroupBy(item => item.PoolKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var last = group.Last();
                var definition = last.Definition;
                var featuredName = definition.FeaturedOperator;
                var poolSixStars = sixStars
                    .Where(item => string.Equals(item.PoolKey, group.Key, StringComparison.OrdinalIgnoreCase))
                    .Reverse().ToArray();
                IEnumerable<DrawRecord> upSeriesDraws = definition.Category == EndfieldPoolCategory.Refactor
                    ? orderedDraws.Where(item => string.Equals(item.Definition.UpSeries, definition.UpSeries, StringComparison.OrdinalIgnoreCase))
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

                    if (!string.Equals(draw.PoolKey, group.Key, StringComparison.OrdinalIgnoreCase)
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

                var seriesPaidCount = definition.Category == EndfieldPoolCategory.Refactor
                    ? orderedDraws.Count(item => !item.IsFree
                        && string.Equals(item.Definition.UpSeries, definition.UpSeries, StringComparison.OrdinalIgnoreCase))
                    : (int?)null;
                var seriesUpRecorded = definition.Category == EndfieldPoolCategory.Refactor
                    && featuredName is not null
                    && orderedDraws.Any(item => !item.IsFree
                        && string.Equals(item.Definition.UpSeries, definition.UpSeries, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(item.OperatorName, featuredName, StringComparison.Ordinal));
                var displayName = definition.Category == EndfieldPoolCategory.Refactor
                    ? EndfieldPoolCatalog.RefactorDisplayName(last.PoolName, last.PoolVersion)
                    : last.PoolName;
                return new EndfieldPoolAnalysis(
                    last.PoolId,
                    displayName,
                    group.Count(),
                    group.Count(item => !item.IsFree),
                    group.Count(item => item.IsFree),
                    group.Count(item => item.IsFree && item.Rarity == 6),
                    definition.Category,
                    featuredName,
                    definition.BannerAssetFileName,
                    poolSixStars,
                    featuredPaidPosition,
                    hasFreeFeatured,
                    PoolKey: group.Key,
                    PoolVersion: last.PoolVersion,
                    SeriesPaidDrawCount: seriesPaidCount,
                    SeriesUpRecorded: seriesUpRecorded,
                    SeriesKey: definition.UpSeries);
            })
            .OrderBy(item => item.Category == EndfieldPoolCategory.Standard ? 1 : 0)
            .ThenByDescending(item => orderedDraws.Last(draw =>
                string.Equals(draw.PoolKey, item.PoolKey, StringComparison.OrdinalIgnoreCase)).Timestamp)
            .ToArray();

        // Keep one visible card per named rerun series. Phase records remain separate in
        // the archive and analysis; only the newest phase is visible until history opens.
        var pools = phasePools.GroupBy(item => item.Category == EndfieldPoolCategory.Refactor
                ? item.SeriesKey : item.PoolKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                if (group.First().Category != EndfieldPoolCategory.Refactor)
                {
                    return group.First();
                }

                var phases = group.OrderByDescending(item => EndfieldPoolCatalog.RefactorPhaseNumber(
                        item.PoolName, item.PoolVersion))
                    .ThenByDescending(item => orderedDraws.Last(draw => string.Equals(
                        draw.PoolKey, item.PoolKey, StringComparison.OrdinalIgnoreCase)).Timestamp)
                    .ToArray();
                return phases[0] with { PreviousPhases = phases.Skip(1).ToArray() };
            })
            .ToArray();

        var pityCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var draw in orderedDraws.Where(item => !item.IsFree))
        {
            var family = draw.Definition.PityFamily;
            pityCounters.TryGetValue(family, out var count);
            pityCounters[family] = draw.Rarity == 6 ? 0 : count + 1;
        }

        var lastLimitedDraw = orderedDraws.LastOrDefault(item =>
            item.Definition.Category is EndfieldPoolCategory.Chartered or EndfieldPoolCategory.Refactor);
        var limitedFamily = lastLimitedDraw?.Definition.PityFamily;
        var latestUpPool = pools.FirstOrDefault(item =>
            item.Category is EndfieldPoolCategory.Chartered or EndfieldPoolCategory.Refactor);
        var latestUpDraw = latestUpPool is null ? null : orderedDraws.Last(item =>
            string.Equals(item.PoolKey, latestUpPool.PoolKey, StringComparison.OrdinalIgnoreCase));
        var upSeries = latestUpDraw?.Definition.UpSeries;
        var upPaidCount = upSeries is null ? 0 : orderedDraws.Count(item =>
            !item.IsFree && string.Equals(item.Definition.UpSeries, upSeries, StringComparison.OrdinalIgnoreCase));
        var upKnown = latestUpPool?.FeaturedOperatorName is not null;
        var upRecorded = upKnown && orderedDraws.Any(item =>
            !item.IsFree
            && string.Equals(item.Definition.UpSeries, upSeries, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.OperatorName, latestUpPool!.FeaturedOperatorName, StringComparison.Ordinal));
        var isRefactor = latestUpPool?.Category == EndfieldPoolCategory.Refactor;
        var upPoolName = isRefactor && latestUpDraw is not null
            ? EndfieldPoolCatalog.RefactorSeriesName(latestUpDraw.PoolName)
            : latestUpPool?.PoolName;
        var pity = new EndfieldPityOverview(
            pityCounters.TryGetValue("standard", out var standardCount) ? standardCount : null,
            limitedFamily is not null && pityCounters.TryGetValue(limitedFamily, out var limitedCount) ? limitedCount : null,
            limitedFamily switch { "chartered" => "特许", "refactor" => "重构", _ => null },
            upPoolName,
            latestUpPool is not null && !upRecorded && upPaidCount < 120 ? 120 - upPaidCount : null,
            upRecorded,
            latestUpPool is null ? null : upPaidCount,
            isRefactor,
            upKnown);

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
        string? PoolVersion,
        string PoolKey,
        EndfieldPoolDefinition Definition,
        string OperatorName,
        long Rarity,
        bool IsFree,
        long Timestamp,
        long Sequence,
        int Index);
}
