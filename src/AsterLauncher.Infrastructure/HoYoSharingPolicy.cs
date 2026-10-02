using AsterLauncher.Core;
namespace AsterLauncher.Infrastructure;

public static class HoYoSharingPolicy
{
    // Only packaged resource archives; runtime downloads under Persistent, indexes, revisions and Plugins remain independent.
    public static bool IsShareable(string gameId, string relative)
    {
        var path = relative.Replace('\\', '/');
        return gameId switch
        {
            BuiltInGameIds.GenshinImpact => path.StartsWith("YuanShen_Data/StreamingAssets/AssetBundles/blocks/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".blk", StringComparison.OrdinalIgnoreCase),
            BuiltInGameIds.HonkaiStarRail => path.StartsWith("StarRail_Data/StreamingAssets/Asb/Windows/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".block", StringComparison.OrdinalIgnoreCase),
            BuiltInGameIds.ZenlessZoneZero => path.StartsWith("ZenlessZoneZero_Data/StreamingAssets/Blocks/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".blk", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
    public static void ValidateIndependent(string first, string? second)
    {
        if (second is null) return;
        first = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar);
        second = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar);
        if (first.Equals(second, StringComparison.OrdinalIgnoreCase) || first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("官服和 B 服必须使用互不包含的独立目录。");
    }
}