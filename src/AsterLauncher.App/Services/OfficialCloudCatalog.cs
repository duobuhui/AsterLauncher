using AsterLauncher.Core;

namespace AsterLauncher.App.Services;

public sealed record OfficialCloudEntry(Uri Uri, string Description);

public static class OfficialCloudCatalog
{
    public static OfficialCloudEntry? Get(string gameId) => gameId switch
    {
        BuiltInGameIds.Endfield => new(new("https://endfield.hypergryph.com/cloud"),
            DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Date < new DateTime(2026, 10, 15)
                ? "打开官方云游戏入口，启动器无需下载游戏。官方预计 10 月 15 日开放网页版，实际开放情况以官网为准。"
                : "在默认浏览器中打开官方云游戏入口，启动器无需下载游戏。支持的平台与开放情况以官网为准。"),
        BuiltInGameIds.GenshinImpact => new(new("https://ys.mihoyo.com/cloud/"),
            "在默认浏览器中打开云·原神，无需在启动器中下载游戏。登录后由官方提供云游戏服务。"),
        BuiltInGameIds.HonkaiStarRail => new(new("https://sr.mihoyo.com/cloud/"),
            "在默认浏览器中打开云·星穹铁道，无需在启动器中下载游戏。登录后由官方提供云游戏服务。"),
        BuiltInGameIds.ZenlessZoneZero => new(new("https://zzz.mihoyo.com/cloud-feat/"),
            "打开云·绝区零官方网站，启动器无需下载游戏。目前官网提供轻量云客户端，尚未确认支持浏览器直接游玩。"),
        _ => null
    };
}
