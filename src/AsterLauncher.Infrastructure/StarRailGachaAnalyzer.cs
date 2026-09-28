using System.Globalization;
using System.Text.Json.Nodes;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

internal static class StarRailGachaAnalyzer
{
    private static readonly (string Type, string Title, int? Maximum)[] Categories =
    [
        ("11", "角色活动跃迁", 90),
        ("12", "光锥活动跃迁", 80),
        ("1", "群星跃迁", 90),
        ("2", "始发跃迁", null),
        ("21", "特殊跃迁 · 类别 21", null),
        ("22", "特殊跃迁 · 类别 22", null)
    ];

    public static StarRailGachaAnalysis Analyze(JsonObject root)
    {
        if (root["hkrpg"] is not JsonArray users)
        {
            return StarRailGachaAnalysis.Empty;
        }

        var accounts = new List<StarRailAccountAnalysis>();
        foreach (var user in users.OfType<JsonObject>())
        {
            var uid = Value(user, "uid");
            if (string.IsNullOrWhiteSpace(uid))
            {
                continue;
            }

            var records = (user["list"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select((item, index) => new WarpRecord(
                    Value(item, "id"),
                    Value(item, "gacha_id"),
                    Value(item, "gacha_type"),
                    Value(item, "time"),
                    Value(item, "name"),
                    Value(item, "rank_type"),
                    index))
                .ToArray();

            var sections = new List<StarRailWarpSection>();
            foreach (var category in Categories)
            {
                var sorted = records.Where(record => record.Type == category.Type)
                    .OrderBy(record => record.Time, StringComparer.Ordinal)
                    .ThenBy(record => record.Id.Length)
                    .ThenBy(record => record.Id, StringComparer.Ordinal)
                    .ThenBy(record => record.OriginalIndex)
                    .ToArray();
                if (sorted.Length == 0)
                {
                    continue;
                }

                var pulls = new Dictionary<WarpRecord, StarRailFiveStarPull>();
                var previousFiveStar = -1;
                for (var index = 0; index < sorted.Length; index++)
                {
                    var record = sorted[index];
                    if (record.Rank != "5")
                    {
                        continue;
                    }

                    pulls[record] = new StarRailFiveStarPull(
                        string.IsNullOrWhiteSpace(record.Name) ? "未命名五星" : record.Name,
                        index - previousFiveStar,
                        previousFiveStar >= 0,
                        category.Maximum ?? 0,
                        DateLabel(record.Time));
                    previousFiveStar = index;
                }

                var itemsByPoolLastTime = sorted.GroupBy(record => record.PoolId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last().Time, StringComparer.Ordinal);
                var banners = sorted.GroupBy(record => record.PoolId, StringComparer.Ordinal)
                    .Select(group =>
                    {
                        var items = group.ToArray();
                        var first = items[0].Time;
                        var last = items[^1].Time;
                        var dateText = first == last
                            ? DateLabel(last)
                            : $"{DateLabel(first)} — {DateLabel(last)}";
                        return new StarRailWarpBanner(
                            group.Key,
                            $"卡池记录 · {DateLabel(last)}",
                            dateText,
                            items.Length,
                            items.Where(pulls.ContainsKey).Select(item => pulls[item]).Reverse().ToArray());
                    })
                    .OrderByDescending(banner => itemsByPoolLastTime[banner.PoolId], StringComparer.Ordinal)
                    .ThenByDescending(banner => banner.PoolId, StringComparer.Ordinal)
                    .ToArray();

                sections.Add(new StarRailWarpSection(
                    category.Type,
                    category.Title,
                    sorted.Length,
                    pulls.Count,
                    sorted.Count(record => record.Rank == "4"),
                    sorted.Count(record => record.Rank == "3"),
                    sorted.Length - 1 - previousFiveStar,
                    category.Maximum,
                    $"{DateLabel(sorted[0].Time)} — {DateLabel(sorted[^1].Time)}",
                    sorted.Where(pulls.ContainsKey).Select(record => pulls[record]).Reverse().ToArray(),
                    banners));
            }

            accounts.Add(new StarRailAccountAnalysis(uid, records.Length, sections));
        }

        return new StarRailGachaAnalysis(accounts
            .OrderByDescending(account => account.Sections.Sum(section => section.RecordCount))
            .ToArray());
    }

    private static string Value(JsonObject item, string key) => item[key]?.ToString() ?? string.Empty;

    private static string DateLabel(string value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time)
            ? time.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)
            : "日期未知";

    private sealed class WarpRecord(
        string id,
        string poolId,
        string type,
        string time,
        string name,
        string rank,
        int originalIndex)
    {
        public string Id { get; } = id;
        public string PoolId { get; } = string.IsNullOrWhiteSpace(poolId) ? "未标识卡池" : poolId;
        public string Type { get; } = type;
        public string Time { get; } = time;
        public string Name { get; } = name;
        public string Rank { get; } = rank;
        public int OriginalIndex { get; } = originalIndex;
    }
}
