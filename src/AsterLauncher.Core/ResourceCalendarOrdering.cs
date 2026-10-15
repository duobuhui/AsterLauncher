namespace AsterLauncher.Core;

/// <summary>Uses status projected at the real UTC+8 clock; browsing another date never changes expiry.</summary>
public static class ResourceCalendarOrdering
{
    public static IReadOnlyList<ResourceCalendarEntry> Order(
        IEnumerable<ResourceCalendarEntry> entries, DateOnly? focusedDate = null)
    {
        var currentFirst = entries.OrderBy(entry => entry.IsExpired);
        if (focusedDate is { } date)
            return currentFirst.ThenBy(entry => entry.StartDate == date ? 0 : 1)
                .ThenBy(entry => entry.Kind == "pool" ? 1 : 0)
                .ThenBy(entry => entry.Title, StringComparer.Ordinal)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
        return currentFirst.ThenBy(entry => entry.StartDate)
            .ThenBy(entry => entry.StartsAt)
            .ThenBy(entry => entry.Kind, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
    }
}