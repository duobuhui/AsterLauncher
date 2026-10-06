using AsterLauncher.Core;
namespace AsterLauncher.Infrastructure;

// All dates are UTC+8. Version boundaries use the announced maintenance window;
// unknown phases never inherit phase-1 dates and unpublished ends are not inferred.
internal static class EndfieldBannerDetails
{
    internal sealed record Detail(string Dates, string WeaponPool, string Weapon, string Source);
    private static readonly Dictionary<string, Detail> Details = new(StringComparer.Ordinal)
    {
        ["熔火灼痕"] = new("2026/01/22 11:00 — 2026/02/07 11:59", "熔铸申领", "熔铸火焰", "https://www.taptap.cn/moment/762996134376376239"),
        ["轻飘飘的信使"] = new("2026/02/07 12:00 — 2026/02/24 11:59", "迅行申领", "使命必达", "https://www.taptap.cn/moment/768565832493041953"),
        ["热烈色彩"] = new("2026/02/24 12:00 — 2026/03/12 06:00", "绘涂申领", "艺术暴君", "https://www.taptap.cn/moment/774777281615759534"),
        ["河流的女儿"] = new("2026/03/12 12:00 — 2026/03/29 11:59", "新芽申领", "落草", "https://www.taptap.cn/moment/780516140374819093"),
        ["狼珀"] = new("2026/03/29 12:00 — 2026/04/17 06:00", "绯珀申领", "狼之绯", "https://endfield.hypergryph.com/news/6003"),
        ["春雷动，万物生"] = new("2026/04/17 12:00 — 2026/05/22 11:59", "行舟申领", "孤舟", "https://endfield.hypergryph.com/news/9343"),
        ["拳出无悔"] = new("2026/06/05 12:00 — 2026/06/26 11:59", "绛结申领", "赤缨", "https://www.taptap.cn/moment/810964540903653859"),
        ["逐罪者"] = new("2026/06/26 12:00 — 2026/07/16 06:00", "染赤申领", "镀红祝福", "https://www.taptap.cn/moment/818960388082109841"),
        ["临渊望北"] = new("2026/07/16 12:00 — 2026/08/09 11:59", "军列申领", "四二式·肃阵", "https://www.taptap.cn/moment/826214313340440174"),
        ["晨星于此闪耀"] = new("2026/08/09 12:00 — 2026/09/02 06:00", "明曜申领", "曜夜的首演", "https://endfield.hypergryph.com/news/1165"),
        ["冬猎"] = new("2026/09/02 12:00 — 2026/09/30 11:59", "幽寒申领", "寒夜幽影", "https://endfield.hypergryph.com/news/2653")
    };
    public static Detail? Resolve(string name, string? phase, EndfieldPoolCategory category, ResourceCatalog? resources = null)
    {
        var item = resources?.Pools.FirstOrDefault(p => p.GameId == BuiltInGameIds.Endfield && p.Key == name &&
            (category == EndfieldPoolCategory.Refactor ? p.Phase == EndfieldPoolCatalog.RefactorPhaseNumber(name, phase).ToString() : p.Phase is null));
        if (item is not null) return new(item.DateText, item.WeaponPool ?? "", item.Weapon ?? "", item.Source);
        if (category == EndfieldPoolCategory.Refactor && EndfieldPoolCatalog.RefactorSeriesName(name) == "绚丽异彩")
        {
            var phaseNumber = EndfieldPoolCatalog.RefactorPhaseNumber(name, phase);
            return new(phaseNumber == 1 ? "2026/09/24 12:00 — 未公布" : "", "点绘申领", "艺术暴君", "https://endfield.hypergryph.com/news/2653");
        }
        return Details.GetValueOrDefault(name);
    }
}