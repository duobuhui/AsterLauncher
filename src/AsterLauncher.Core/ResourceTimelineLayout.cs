namespace AsterLauncher.Core;

/// <summary>Day-aligned geometry within an inclusive calendar window. No time or unpublished duration is inferred.</summary>
public sealed record ResourceTimelineSegment
{
    public int StartOffsetDays { get; init; }
    public int SpanDays { get; init; }
    public int WindowDays { get; init; }
    public DateOnly VisibleStart { get; init; }
    public DateOnly VisibleEnd { get; init; }
    public bool ContinuesBefore { get; init; }
    public bool ContinuesAfter { get; init; }
    public bool IsPoint { get; init; }
    public bool StartTimeUnspecified { get; init; }
    public bool EndTimeUnspecified { get; init; }
}

public static class ResourceTimelineLayout
{
    public static ResourceTimelineSegment? Project(ResourceCalendarEntry entry, DateOnly from, DateOnly to)
    {
        if (to < from || !entry.Overlaps(from, to)) return null;
        var lastDay = entry.EndDate ?? entry.StartDate;
        var visibleStart = entry.StartDate < from ? from : entry.StartDate;
        var visibleEnd = lastDay > to ? to : lastDay;
        var span = visibleEnd.DayNumber - visibleStart.DayNumber + 1;
        if (span <= 0) return null;
        return new()
        {
            StartOffsetDays = visibleStart.DayNumber - from.DayNumber,
            SpanDays = span,
            WindowDays = to.DayNumber - from.DayNumber + 1,
            VisibleStart = visibleStart,
            VisibleEnd = visibleEnd,
            ContinuesBefore = entry.StartDate < from,
            ContinuesAfter = lastDay > to,
            IsPoint = entry.EndDate is null,
            StartTimeUnspecified = entry.StartsAt is null,
            EndTimeUnspecified = entry.EndsAt is null
        };
    }
}
