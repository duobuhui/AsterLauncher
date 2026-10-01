using System.Globalization;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

public static class GachaStatisticsAnalyzer
{
    public static IReadOnlyList<GachaPoolStatistics> Analyze(string gameId, UigfAccountAnalysis account,
        IReadOnlyDictionary<string, GachaUpStatus>? corrections = null, IReadOnlySet<string>? knownItems = null)
        => account.Sections.Select(section => AnalyzePool(gameId, account.Uid, section, corrections, knownItems)).ToArray();

    public static string CorrectionKey(string gameId, string uid, string type, string recordId)
        => string.Join('\u001F', gameId, uid, type, recordId);

    private static GachaPoolStatistics AnalyzePool(string game, string uid, UigfPoolSection section,
        IReadOnlyDictionary<string, GachaUpStatus>? corrections, IReadOnlySet<string>? knownItems)
    {
        var high = new List<GachaStatisticPoint>();
        var featured = new List<GachaStatisticPoint>();
        var upInterval = 0;
        var previousUpKnown = false;
        foreach (var pull in section.HighRarityPulls.Reverse())
        {
            var status = corrections is not null && corrections.TryGetValue(CorrectionKey(game, uid, section.Type, pull.RecordId), out var corrected)
                ? corrected : Classify(game, section.Type, pull, knownItems);
            var point = new GachaStatisticPoint(pull.RecordId, pull.Name, pull.RawTime, pull.LocalPullCount, pull.HasPreviousHighRarity, status);
            high.Add(point);
            // An unknown high-rarity result may itself have been UP. Break the interval.
            if (status == GachaUpStatus.Unknown)
            {
                upInterval = 0;
                previousUpKnown = false;
                continue;
            }
            upInterval += pull.LocalPullCount;
            if (status == GachaUpStatus.Featured)
            {
                featured.Add(point with { Pulls = upInterval, Complete = previousUpKnown && pull.HasPreviousHighRarity });
                upInterval = 0;
                previousUpKnown = true;
            }
        }
        var featuredMax = (game, section.Type) switch
        {
            (BuiltInGameIds.GenshinImpact, "301") or (BuiltInGameIds.HonkaiStarRail, "11")
                or (BuiltInGameIds.ZenlessZoneZero, "2") => 180,
            (BuiltInGameIds.GenshinImpact, "302") or (BuiltInGameIds.HonkaiStarRail, "12")
                or (BuiltInGameIds.ZenlessZoneZero, "3") => 160,
            _ => (int?)null
        };
        return new(section.Type, section.Title, section.RecordCount, section.HighRarityLabel,
            section.HighRarityCount, section.MiddleRarityCount, section.LowRarityCount,
            section.PityCount, section.PityMaximum, featuredMax, high, featured);
    }

    public static GachaUpStatus Classify(string game, string type, UigfHighRarityPull pull, IReadOnlySet<string>? knownItems)
    {
        var character = (game, type) is (BuiltInGameIds.GenshinImpact, "301")
            or (BuiltInGameIds.HonkaiStarRail, "11") or (BuiltInGameIds.ZenlessZoneZero, "2");
        var equipment = (game, type) is (BuiltInGameIds.GenshinImpact, "302")
            or (BuiltInGameIds.HonkaiStarRail, "12") or (BuiltInGameIds.ZenlessZoneZero, "3");
        if (!character && !equipment) return GachaUpStatus.NotApplicable;
        var name = pull.Name;
        if (!DateTime.TryParseExact(pull.RawTime, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time)) return GachaUpStatus.Unknown;
        if (game == BuiltInGameIds.HonkaiStarRail && character)
        {
            if (new[] { "姬子", "瓦尔特", "布洛妮娅", "杰帕德", "克拉拉", "彦卿", "白露" }.Contains(name))
                return GachaUpStatus.NonFeatured;
            if (new[] { "希儿", "刃", "符玄" }.Contains(name) && time.Date >= new DateTime(2025, 4, 9))
                return GachaUpStatus.Unknown;
            if (new[] { "云璃", "银枝", "银狼" }.Contains(name) && time.Date >= new DateTime(2026, 4, 22))
                return GachaUpStatus.Unknown;
        }
        if (game == BuiltInGameIds.GenshinImpact && character)
        {
            if (new[] { "琴", "迪卢克", "莫娜", "七七" }.Contains(name)) return GachaUpStatus.NonFeatured;
            if (name == "刻晴") return time.Date >= new DateTime(2021, 2, 17) && time.Date <= new DateTime(2021, 3, 2)
                ? GachaUpStatus.Unknown : GachaUpStatus.NonFeatured;
            if (name == "提纳里" && time.Date >= new DateTime(2022, 9, 28)
                || name == "迪希雅" && time.Date >= new DateTime(2023, 4, 12)
                || name == "梦见月瑞希" && time.Date >= new DateTime(2025, 3, 26))
                return GachaUpStatus.NonFeatured;
        }
        if (game == BuiltInGameIds.ZenlessZoneZero && character
            && new[] { "猫又", "妮可玛塔", "猫宫又奈", "丽娜", "亚历山德丽娜·莎芭丝缇安", "珂蕾妲", "莱卡恩", "11号", "格莉丝" }.Contains(name))
            return GachaUpStatus.NonFeatured;
        // Signal customization introduced in the current ZZZ channel rules makes the
        // user's selected non-featured roster indispensable. UIGF does not include it.
        if (game == BuiltInGameIds.ZenlessZoneZero && time.Date >= new DateTime(2026, 7, 29))
            return GachaUpStatus.Unknown;
        // Standard equipment can be promoted in some games/banners; name alone is insufficient.
        if (equipment && StandardEquipment(game).Contains(name)) return game == BuiltInGameIds.HonkaiStarRail ? GachaUpStatus.NonFeatured : GachaUpStatus.Unknown;
        return knownItems?.Contains(name) == true ? GachaUpStatus.Featured : GachaUpStatus.Unknown;
    }

    private static string[] StandardEquipment(string game) => game switch
    {
        BuiltInGameIds.GenshinImpact => ["天空之刃","天空之傲","天空之翼","天空之脊","天空之卷","风鹰剑","阿莫斯之弓","和璞鸢","四风原典","狼的末路"],
        BuiltInGameIds.HonkaiStarRail => ["银河铁道之夜","以世界之名","但战斗还未结束","无可取代的东西","制胜的瞬间","如泥酣眠","时节不居"],
        BuiltInGameIds.ZenlessZoneZero => ["钢铁肉垫","燃狱齿轮","拘缚者","硫磺石","啜泣摇篮","嵌合编译器"],
        _ => []
    };
}