using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

/// <summary>Validates untrusted, user-supplied record links without fetching or retaining them.</summary>
internal static class GachaHistoryUrlValidator
{
    internal const string InvalidLinkMessage = "链接不属于当前游戏的受支持官方记录页面，或缺少必要授权参数。请从游戏内记录页重新复制完整链接。";

    internal static bool TryValidate(string gameId, string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32768) return false;
        var input = value.Trim();
        if (input.Any(char.IsControl) || input.Contains('\\')
            || !Uri.TryCreate(input, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps || !candidate.IsDefaultPort
            || candidate.UserInfo.Length != 0 || candidate.Host.EndsWith('.')
            || !TryParseQuery(candidate.Query, out var query)) return false;

        // Accept complete known paths, with no encoded separators.
        var pathStart = input.IndexOf('/', input.IndexOf("://", StringComparison.Ordinal) + 3);
        var rawPath = pathStart < 0 ? string.Empty : input[pathStart..].Split('?', '#')[0];
        if (rawPath.Contains('%') || rawPath.Contains("/../", StringComparison.Ordinal)
            || rawPath.Contains("/./", StringComparison.Ordinal)) return false;
        var path = candidate.AbsolutePath;
        if (path.Contains('%') || path.Contains("//", StringComparison.Ordinal)) return false;
        if (gameId == BuiltInGameIds.Endfield)
        {
            if (candidate.Host is not ("ef-webview.hypergryph.com" or "ef-webview.gryphline.com")
                || path is not ("/api/record/char" or "/api/record/weapon")
                || !HasCredential(query, "token") || !HasValue(query, "server_id")
                || !query["server_id"].All(char.IsAsciiDigit)
                || query["server_id"].Length > 16
                || query.ContainsKey("authkey") || query.ContainsKey("game_biz")) return false;
            var poolKey = path.EndsWith("/char", StringComparison.Ordinal) ? "pool_type" : "pool_id";
            if (!HasValue(query, poolKey) || query[poolKey].Length > 128
                || !query[poolKey].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return false;
            query.Remove("seq_id");
        }
        else
        {
            var definition = gameId switch
            {
                BuiltInGameIds.GenshinImpact => (Key: "hk4e", Web: "/hk4e/event/e20190909gacha", GlobalWeb: "/genshin/event/e20190909gacha", Api: "/gacha_info/api/getGachaLog"),
                BuiltInGameIds.HonkaiStarRail => (Key: "hkrpg", Web: "/hkrpg/event/e20211215gacha", GlobalWeb: "/hkrpg/event/e20211215gacha", Api: "/common/hkrpg_gacha_record/api/getGachaLog"),
                BuiltInGameIds.ZenlessZoneZero => (Key: "nap", Web: "/nap/event/e20230424gacha", GlobalWeb: "/nap/event/e20230424gacha", Api: "/common/gacha_record/api/getGachaLog"),
                _ => default
            };
            if (definition.Key is null || !HasCredential(query, "authkey") || query.ContainsKey("token")) return false;
            var global = candidate.Host == "gs.hoyoverse.com"
                || candidate.Host == $"public-operation-{definition.Key}-sg.hoyoverse.com";
            var webPath = global ? definition.GlobalWeb : definition.Web;
            var validWeb = candidate.Host == (global ? "gs.hoyoverse.com" : "webstatic.mihoyo.com")
                && (path == webPath || path == webPath + "/" || path == webPath + "/index.html");
            var validApi = candidate.Host == (global
                    ? $"public-operation-{definition.Key}-sg.hoyoverse.com" : $"public-operation-{definition.Key}.mihoyo.com")
                && (path == definition.Api || definition.Key == "hkrpg"
                    && path == "/common/hkrpg_gacha_record/api/getLdGachaLog");
            if (!validWeb && !validApi) return false;
            if (query.TryGetValue("game_biz", out var business)
                && business != definition.Key + (global ? "_global" : "_cn")) return false;
            if (query.TryGetValue("region", out var region)
                && !IsGameRegion(definition.Key, global, region)) return false;
            foreach (var key in new[] { "gacha_type", "real_gacha_type", "page", "size", "end_id" }) query.Remove(key);
        }

        try { uri = WithQuery(candidate, query); return true; }
        catch (UriFormatException) { return false; }
    }

    private static bool IsGameRegion(string game, bool global, string region) => (game, global) switch
    {
        ("hk4e", false) => region is "cn_gf01" or "cn_qd01",
        ("hk4e", true) => region is "os_usa" or "os_euro" or "os_asia" or "os_cht",
        ("hkrpg", false) => region is "prod_gf_cn" or "prod_qd_cn",
        ("hkrpg", true) => region is "prod_official_usa" or "prod_official_eur" or "prod_official_asia" or "prod_official_cht",
        ("nap", false) => region is "prod_gf_cn" or "prod_qd_cn",
        ("nap", true) => region is "prod_gf_us" or "prod_gf_eu" or "prod_gf_jp" or "prod_gf_sg",
        _ => false
    };

    private static bool HasCredential(IReadOnlyDictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) && value.Length >= 16 && !value.Any(char.IsWhiteSpace);

    private static bool HasValue(IReadOnlyDictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);

    internal static bool TryParseQuery(string text, out Dictionary<string, string> query)
    {
        query = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var part in text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                for (var i = 0; i < part.Length; i++)
                    if (part[i] == '%' && (i + 2 >= part.Length
                        || !Uri.IsHexDigit(part[i + 1]) || !Uri.IsHexDigit(part[i + 2]))) return false;
                var pair = part.Split('=', 2);
                var key = Uri.UnescapeDataString(pair[0]);
                var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
                if (key.Length == 0 || key.Any(char.IsControl) || value.Any(char.IsControl)
                    || !query.TryAdd(key, value)) return false;
            }
            return true;
        }
        catch (UriFormatException) { return false; }
    }

    internal static Uri WithQuery(Uri uri, IReadOnlyDictionary<string, string> query) => new UriBuilder(uri)
    {
        Fragment = string.Empty,
        Query = string.Join('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
    }.Uri;
}

