using System.Text.Json.Nodes;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

// Compatibility facade for existing callers; all games use the same analysis engine.
internal static class StarRailGachaAnalyzer
{
    public static StarRailGachaAnalysis Analyze(JsonObject root)
    {
        var analysis = UigfGachaAnalyzer.Analyze(root, BuiltInGameIds.HonkaiStarRail);
        return new StarRailGachaAnalysis(analysis.Accounts.Select(account =>
            new StarRailAccountAnalysis(account.Uid, account.RecordCount, account.Sections.Select(section =>
                new StarRailWarpSection(section.Type, section.Title, section.RecordCount,
                    section.HighRarityCount, section.MiddleRarityCount, section.LowRarityCount,
                    section.PityCount, section.PityMaximum, section.DateRange,
                    section.HighRarityPulls.Select(ConvertPull).ToArray(),
                    section.Banners.Select(banner => new StarRailWarpBanner(banner.PoolId,
                        banner.Title, banner.DateText, banner.RecordCount,
                        banner.HighRarityPulls.Select(ConvertPull).ToArray())).ToArray())).ToArray())).ToArray());
    }

    private static StarRailFiveStarPull ConvertPull(UigfHighRarityPull pull) =>
        new(pull.Name, pull.LocalPullCount, pull.HasPreviousHighRarity, pull.PityMaximum, pull.TimeText);
}