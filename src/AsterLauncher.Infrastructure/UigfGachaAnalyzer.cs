using System.Globalization;
using System.Text.Json.Nodes;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

internal static class UigfGachaAnalyzer
{
    public static UigfGachaAnalysis Analyze(JsonObject root, string gameId)
    {
        var (key, high, middle, low, categories) = Definition(gameId);
        var highLabel = gameId == BuiltInGameIds.ZenlessZoneZero ? "S级" : "五星";
        var labels = gameId == BuiltInGameIds.ZenlessZoneZero ? new[] { "S", "A", "B" } : new[] { "5★", "4★", "3★" };
        var accounts = new List<UigfAccountAnalysis>();
        foreach (var user in (root[key] as JsonArray ?? []).OfType<JsonObject>())
        {
            var uid = Value(user, "uid");
            if (uid.Length == 0) continue;
            var records = (user["list"] as JsonArray ?? []).OfType<JsonObject>()
                .Select((item, index) => new Pull(
                    Value(item, "id"), Value(item, "gacha_id"), CanonicalType(gameId, item),
                    Value(item, "time"), Value(item, "name"), Value(item, "item_type"),
                    Value(item, "rank_type"), index)).ToArray();
            var allCategories = categories.Concat(records.Select(r => r.Type).Distinct(StringComparer.Ordinal)
                .Where(type => categories.All(c => c.Type != type))
                .OrderBy(type => type, StringComparer.Ordinal)
                .Select(type => new UigfPoolDefinition(type, $"其他卡池 · 类型 {(type.Length == 0 ? "未标识" : type)}", null)));
            var sections = new List<UigfPoolSection>();
            foreach (var category in allCategories)
            {
                var sorted = records.Where(r => r.Type == category.Type)
                    .OrderBy(r => r.Time, StringComparer.Ordinal).ThenBy(r => r.Id.Length)
                    .ThenBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Index).ToArray();
                if (sorted.Length == 0) continue;
                var pulls = new Dictionary<Pull, UigfHighRarityPull>();
                var previous = -1;
                for (var i = 0; i < sorted.Length; i++)
                {
                    var r = sorted[i];
                    if (r.Rank != high) continue;
                    pulls[r] = new UigfHighRarityPull(gameId,
                        r.Name.Length > 0 ? r.Name : $"未命名{highLabel}", r.ItemType,
                        i - previous, previous >= 0, category.PityMaximum ?? 0, DateLabel(r.Time));
                    previous = i;
                }
                // Only a real gacha_id identifies a banner. Dates and raw 301/400 codes do not identify a release period.
                var banners = sorted.GroupBy(r => r.PoolId, StringComparer.Ordinal)
                    .OrderByDescending(group => group.Last().Time, StringComparer.Ordinal)
                    .Select(group =>
                    {
                        var items = group.ToArray();
                        var first = DateLabel(items[0].Time); var last = DateLabel(items[^1].Time);
                        return new UigfBannerAnalysis(group.Key, $"卡池记录 · {last}",
                            first == last ? last : $"{first} — {last}", items.Length, highLabel,
                            items.Where(pulls.ContainsKey).Select(r => pulls[r]).Reverse().ToArray());
                    }).ToArray();
                sections.Add(new UigfPoolSection(category.Type, category.Title, sorted.Length,
                    pulls.Count, sorted.Count(r => r.Rank == middle), sorted.Count(r => r.Rank == low),
                    sorted.Length - 1 - previous, category.PityMaximum, category.IsFeaturedCharacter,
                    labels[0], labels[1], labels[2], $"{DateLabel(sorted[0].Time)} — {DateLabel(sorted[^1].Time)}",
                    sorted.Where(pulls.ContainsKey).Select(r => pulls[r]).Reverse().ToArray(), banners));
            }
            accounts.Add(new UigfAccountAnalysis(uid, records.Length, sections));
        }
        return new UigfGachaAnalysis(gameId, highLabel, categories, accounts.OrderByDescending(a => a.RecordCount).ToArray());
    }

    private static string CanonicalType(string gameId, JsonObject item)
    {
        var type = Value(item, "gacha_type");
        if (gameId == BuiltInGameIds.GenshinImpact)
        {
            if (type is "301" or "400") return "301";
            if (type.Length == 0) return Value(item, "uigf_gacha_type");
        }
        return type;
    }

    private static (string Key, string High, string Middle, string Low, UigfPoolDefinition[] Categories) Definition(string gameId) => gameId switch
    {
        BuiltInGameIds.GenshinImpact => ("hk4e", "5", "4", "3",
        [
            new("301", "角色活动祈愿", 90, true), new("302", "武器活动祈愿", 80),
            new("200", "常驻祈愿", 90), new("500", "集录祈愿", 90), new("100", "初行者祈愿", null)
        ]),
        BuiltInGameIds.HonkaiStarRail => ("hkrpg", "5", "4", "3",
        [
            new("11", "角色活动跃迁", 90, true), new("12", "光锥活动跃迁", 80),
            new("1", "群星跃迁", 90), new("2", "始发跃迁", null),
            new("21", "特殊跃迁 · 类别 21", null), new("22", "特殊跃迁 · 类别 22", null)
        ]),
        BuiltInGameIds.ZenlessZoneZero => ("nap", "4", "3", "2",
        [
            new("2", "独家频段", 90, true), new("3", "音擎频段", 80),
            new("1", "常驻频段", 90), new("5", "邦布频段", 80),
            new("102", "特殊调频 · 类别 102", null), new("103", "特殊调频 · 类别 103", null)
        ]),
        _ => throw new ArgumentOutOfRangeException(nameof(gameId), "This game has no UIGF analysis definition.")
    };
    private static string Value(JsonObject item, string key) => item[key]?.ToString() ?? "";
    private static string DateLabel(string value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) : "日期未知";
    private sealed record Pull(string Id, string Banner, string Type, string Time, string Name, string ItemType, string Rank, int Index)
    {
        public string PoolId => Banner.Length > 0 ? Banner : "未标识卡池";
    }
}