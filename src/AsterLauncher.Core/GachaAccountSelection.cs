namespace AsterLauncher.Core;

public static class GachaCapabilities
{
    public static bool SupportsUigf(string? gameId) => gameId is BuiltInGameIds.GenshinImpact
        or BuiltInGameIds.HonkaiStarRail or BuiltInGameIds.ZenlessZoneZero;
    public static bool SupportsRecords(string? gameId) => gameId == BuiltInGameIds.Endfield || SupportsUigf(gameId);
}

/// <summary>Page-session choices only; account IDs and authorization data are never written by this class.</summary>
public sealed class GachaAccountSelection
{
    private readonly Dictionary<string, string> _selectedUids = new(StringComparer.Ordinal);

    public string? Resolve(UigfGachaAnalysis analysis)
    {
        if (!GachaCapabilities.SupportsUigf(analysis.GameId)) return null;
        if (_selectedUids.TryGetValue(analysis.GameId, out var selected)
            && analysis.Accounts.Any(account => account.Uid == selected))
            return selected;
        var first = analysis.Accounts.FirstOrDefault(account => !string.IsNullOrWhiteSpace(account.Uid));
        if (first is null) return null;
        _selectedUids[analysis.GameId] = first.Uid;
        return first.Uid;
    }

    public bool Select(UigfGachaAnalysis analysis, string? uid)
    {
        if (!GachaCapabilities.SupportsUigf(analysis.GameId) || string.IsNullOrWhiteSpace(uid)
            || !analysis.Accounts.Any(account => account.Uid == uid)) return false;
        _selectedUids[analysis.GameId] = uid;
        return true;
    }
}