using AsterLauncher.Core;

namespace AsterLauncher.Core.Tests;

public sealed class ResourceTimelineLayoutTests
{
    private static ResourceCalendarEntry Entry(int start, int? end) => new()
    { StartDate = new(2026, 10, start), EndDate = end is { } day ? new DateOnly(2026, 10, day) : null };

    [Fact]
    public void CompleteRangeUsesInclusiveDaysAndWindowRelativeOffset()
    {
        var segment = ResourceTimelineLayout.Project(Entry(7, 15), new(2026, 10, 1), new(2026, 10, 31));
        Assert.NotNull(segment);
        Assert.Equal(6, segment.StartOffsetDays);
        Assert.Equal(9, segment.SpanDays);
        Assert.Equal(31, segment.WindowDays);
        Assert.Equal(new DateOnly(2026, 10, 7), segment.VisibleStart);
        Assert.Equal(new DateOnly(2026, 10, 15), segment.VisibleEnd);
        Assert.False(segment.ContinuesBefore);
        Assert.False(segment.ContinuesAfter);
        Assert.False(segment.IsPoint);
        Assert.True(segment.StartTimeUnspecified);
        Assert.True(segment.EndTimeUnspecified);
    }

    [Theory]
    [InlineData(1, 15, true, false, 0, 6)]
    [InlineData(15, 31, false, true, 5, 6)]
    [InlineData(1, 31, true, true, 0, 11)]
    [InlineData(10, 20, false, false, 0, 11)]
    public void ClippingReportsContinuationWithoutInventingBoundaryPrecision(int start, int end, bool before, bool after, int offset, int span)
    {
        var segment = ResourceTimelineLayout.Project(Entry(start, end), new(2026, 10, 10), new(2026, 10, 20));
        Assert.NotNull(segment);
        Assert.Equal(before, segment.ContinuesBefore);
        Assert.Equal(after, segment.ContinuesAfter);
        Assert.Equal(offset, segment.StartOffsetDays);
        Assert.Equal(span, segment.SpanDays);
        Assert.Equal(11, segment.WindowDays);
        Assert.True(segment.StartTimeUnspecified);
        Assert.True(segment.EndTimeUnspecified);
    }

    [Fact]
    public void UnpublishedEndOccupiesOnlyItsKnownStartAsPoint()
    {
        var point = Entry(7, null);
        var segment = ResourceTimelineLayout.Project(point, new(2026, 10, 1), new(2026, 10, 31));
        Assert.NotNull(segment);
        Assert.True(segment.IsPoint);
        Assert.Equal(6, segment.StartOffsetDays);
        Assert.Equal(1, segment.SpanDays);
        Assert.Equal(segment.VisibleStart, segment.VisibleEnd);
        Assert.False(segment.ContinuesAfter);
        Assert.Null(ResourceTimelineLayout.Project(point, new(2026, 10, 8), new(2026, 10, 31)));
    }

    [Fact]
    public void KnownSingleDayDurationAndUnknownEndPointRemainDistinct()
    {
        var from = new DateOnly(2026, 10, 7);
        var duration = ResourceTimelineLayout.Project(Entry(7, 7), from, from);
        var point = ResourceTimelineLayout.Project(Entry(7, null), from, from);
        Assert.NotNull(duration); Assert.NotNull(point);
        Assert.Equal(1, duration.WindowDays);
        Assert.Equal(1, duration.SpanDays);
        Assert.False(duration.IsPoint);
        Assert.True(point.IsPoint);
        Assert.Equal(0, point.StartOffsetDays);
    }

    [Fact]
    public void EmptyReversedAndNonOverlappingRangesProduceNoSegment()
    {
        Assert.Null(ResourceTimelineLayout.Project(Entry(7, 15), new(2026, 10, 20), new(2026, 10, 19)));
        Assert.Null(ResourceTimelineLayout.Project(Entry(7, 15), new(2026, 10, 1), new(2026, 10, 6)));
        Assert.Null(ResourceTimelineLayout.Project(Entry(7, 15), new(2026, 10, 16), new(2026, 10, 31)));
        Assert.Null(ResourceTimelineLayout.Project(Entry(15, 7), new(2026, 10, 1), new(2026, 10, 31)));
    }

    [Fact]
    public void ExactBoundariesKeepKnownPrecisionEvenWhenClipped()
    {
        var exact = Entry(7, 15) with
        { StartsAt = DateTimeOffset.Parse("2026-10-07T12:00:00+08:00"), EndsAt = DateTimeOffset.Parse("2026-10-15T11:59:00+08:00") };
        var segment = ResourceTimelineLayout.Project(exact, new(2026, 10, 10), new(2026, 10, 12));
        Assert.NotNull(segment);
        Assert.True(segment.ContinuesBefore);
        Assert.True(segment.ContinuesAfter);
        Assert.False(segment.StartTimeUnspecified);
        Assert.False(segment.EndTimeUnspecified);
        var mixed = ResourceTimelineLayout.Project(exact with { EndsAt = null }, new(2026, 10, 10), new(2026, 10, 12));
        Assert.NotNull(mixed);
        Assert.False(mixed.StartTimeUnspecified);
        Assert.True(mixed.EndTimeUnspecified);
    }

    [Fact]
    public void CrossMonthAndLeapDayGeometryUsesCalendarDayNumbers()
    {
        var entry = new ResourceCalendarEntry { StartDate = new(2028, 2, 28), EndDate = new(2028, 3, 2) };
        var segment = ResourceTimelineLayout.Project(entry, new(2028, 2, 27), new(2028, 3, 3));
        Assert.NotNull(segment);
        Assert.Equal(1, segment.StartOffsetDays);
        Assert.Equal(4, segment.SpanDays);
        Assert.Equal(6, segment.WindowDays);
    }

    [Fact]
    public void AlreadyProjectedMidnightEndIsReusedWithoutAddingAnotherDay()
    {
        var starts = DateTimeOffset.Parse("2026-10-07T12:00:00+08:00");
        var snapshot = ResourceScheduleProjector.Build(new ResourceCatalog
        {
            Revision = 1, PublishedAt = starts,
            Announcements = [new() { GameId = "endfield", Kind = "event", Title = "边界活动", StartsAt = starts,
                EndsAt = DateTimeOffset.Parse("2026-10-07T16:00:00Z"), Url = "https://endfield.hypergryph.com/news/3805" }]
        }, "endfield", starts);
        var entry = Assert.Single(snapshot.Entries);
        var segment = ResourceTimelineLayout.Project(entry, new(2026, 10, 1), new(2026, 10, 31));
        Assert.NotNull(segment);
        Assert.Equal(1, segment.SpanDays);
        Assert.Equal(new DateOnly(2026, 10, 7), segment.VisibleEnd);
        Assert.False(segment.IsPoint);
        Assert.Null(ResourceTimelineLayout.Project(entry, new(2026, 10, 8), new(2026, 10, 8)));
    }
}
