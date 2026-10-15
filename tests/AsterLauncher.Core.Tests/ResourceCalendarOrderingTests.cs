using System.Net;
using System.Text.Json;
using AsterLauncher.App.ViewModels;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class ResourceCalendarOrderingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T10:00:00+08:00");

    [Fact]
    public void SnapshotPlacesOngoingAndUpcomingEntriesAheadOfEndedEntries()
    {
        var snapshot = Build(Now,
            Event("ended", new(2026, 10, 1), new(2026, 10, 14)),
            Event("ongoing", new(2026, 10, 1), new(2026, 10, 31)),
            Event("upcoming", new(2026, 10, 20), new(2026, 11, 1)));

        Assert.Equal(["ongoing", "upcoming", "ended"], snapshot.Entries.Select(entry => entry.Title));
        Assert.True(snapshot.Entries[^1].IsExpired);
    }

    [Fact]
    public void ExpiredPoolCannotOutrankAnOngoingActivity()
    {
        var endedPool = new ResourceCalendarEntry
        {
            Key = "pool", Title = "旧卡池", Kind = "pool", StartDate = new(2026, 9, 1),
            Status = ResourceScheduleStatus.Ended
        };
        var ongoingEvent = new ResourceCalendarEntry
        {
            Key = "event", Title = "进行中活动", Kind = "event", StartDate = new(2026, 10, 1),
            Status = ResourceScheduleStatus.Ongoing
        };

        Assert.Equal([ongoingEvent, endedPool], ResourceCalendarOrdering.Order([endedPool, ongoingEvent]));
        Assert.Equal([ongoingEvent, endedPool], ResourceCalendarOrdering.Order([endedPool, ongoingEvent], new(2026, 9, 1)));
    }

    [Theory]
    [InlineData("2026-10-15T15:59:59Z", false)]
    [InlineData("2026-10-15T16:00:00Z", true)]
    public void DateOnlyEndExpiresAfterTheWholeBeijingDay(string clock, bool expectedExpired)
    {
        var entry = Assert.Single(Build(DateTimeOffset.Parse(clock),
            Event("日期边界", new(2026, 10, 1), new(2026, 10, 15))).Entries);

        Assert.Equal(expectedExpired, entry.IsExpired);
        Assert.Null(entry.EndsAt);
        Assert.Equal(new DateOnly(2026, 10, 15), entry.EndDate);
        Assert.Equal("2026/10/01 — 2026/10/15", entry.DateText);
    }

    [Theory]
    [InlineData("2026-10-15T03:59:59Z", false)]
    [InlineData("2026-10-15T04:00:00Z", true)]
    public void ExactEndExpiresAtItsOriginalInstant(string clock, bool expectedExpired)
    {
        var end = DateTimeOffset.Parse("2026-10-15T12:00:00+08:00");
        var entry = Assert.Single(Build(DateTimeOffset.Parse(clock), new ResourceAnnouncement
        {
            GameId = BuiltInGameIds.Endfield, Key = "precise", Title = "准确时间", Kind = "event",
            StartsAt = end.AddDays(-1), EndsAt = end, Url = "https://endfield.hypergryph.com/news/2650"
        }).Entries);

        Assert.Equal(expectedExpired, entry.IsExpired);
        Assert.Equal(end, entry.EndsAt);
        Assert.Contains("12:00", entry.DateText);
    }

    [Fact]
    public void UnpublishedEndNeverBecomesExpiredBecauseItsStartIsInThePast()
    {
        var snapshot = Build(Now,
            Event("unknown", new(2026, 9, 1)),
            Event("ended", new(2026, 10, 1), new(2026, 10, 14)));

        Assert.Equal("unknown", snapshot.Entries[0].Title);
        Assert.False(snapshot.Entries[0].IsExpired);
        Assert.True(snapshot.Entries[0].HasUncertainTime);
        Assert.Null(snapshot.Entries[0].EndDate);
        Assert.Contains("结束时间未公布", snapshot.Entries[0].StatusText);
    }

    [Fact]
    public void HistoricalFocusDoesNotRecalculateExpiryAtTheSelectedDate()
    {
        var snapshot = Build(Now,
            Event("ended", new(2026, 10, 1), new(2026, 10, 14)),
            Event("ongoing", new(2026, 10, 1), new(2026, 10, 31)));

        var ordered = ResourceCalendarOrdering.Order(snapshot.Entries, new(2026, 10, 1));

        Assert.Equal(["ongoing", "ended"], ordered.Select(entry => entry.Title));
        Assert.True(ordered[^1].IsExpired);
        Assert.Equal(new DateOnly(2026, 10, 15), snapshot.Today);
    }

    [Fact]
    public void EndedEntriesRemainAvailableToTimelineGeometryAndHistoricalDays()
    {
        var entry = Assert.Single(Build(Now, Event("结束活动", new(2026, 10, 1), new(2026, 10, 14))).Entries);

        Assert.True(entry.IsExpired);
        Assert.True(entry.Overlaps(new(2026, 10, 10), new(2026, 10, 10)));
        var segment = ResourceTimelineLayout.Project(entry, new(2026, 10, 1), new(2026, 10, 31));
        Assert.NotNull(segment);
        Assert.Equal(14, segment.SpanDays);
        Assert.Equal(0, segment.StartOffsetDays);
        Assert.True(segment.StartTimeUnspecified);
        Assert.True(segment.EndTimeUnspecified);
    }

    [Fact]
    public async Task MonthAndHistoricalAgendaShareTheSameCurrentExpiryPolicy()
    {
        await WithModelAsync(Now,
            [Event("ended", new(2026, 10, 1), new(2026, 10, 14)),
             Event("ongoing", new(2026, 10, 1), new(2026, 10, 31)),
             Event("upcoming", new(2026, 10, 20), new(2026, 11, 1))],
            (model, _) =>
            {
                Assert.Equal(["ongoing", "upcoming", "ended"], model.MonthEntries.Select(entry => entry.Title));
                model.Select(new(2026, 10, 10));
                Assert.Equal(["ongoing", "ended"], model.SelectedEntries.Select(entry => entry.Title));
                Assert.Equal(["ongoing", "ended"], model.ForDay(new(2026, 10, 1)).Select(entry => entry.Title));
                Assert.Equal(new DateOnly(2026, 10, 15), model.Snapshot.Today);
                model.Select(new(2026, 11, 1));
                Assert.Equal("upcoming", Assert.Single(model.MonthEntries).Title);
                Assert.True(Assert.Single(model.SelectedEntries).IsUpcoming);
                model.Select(new(2026, 10, 10));
                Assert.True(model.SelectedEntries[^1].IsExpired);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task ClockTickReordersExpiredItemsWithoutChangingBrowsedMonthOrDate()
    {
        var end = DateTimeOffset.Parse("2026-10-15T12:00:00+08:00");
        var first = new ResourceAnnouncement
        {
            GameId = BuiltInGameIds.Endfield, Key = "a", Title = "A", Kind = "event",
            StartsAt = end.AddDays(-1), EndsAt = end, Url = "https://endfield.hypergryph.com/news/2650"
        };
        var second = first with { Key = "z", Title = "Z", EndsAt = end.AddMinutes(1) };
        await WithModelAsync(end.AddSeconds(-1), [first, second], (model, clock) =>
        {
            model.Select(new(2026, 10, 14));
            Assert.Equal(["A", "Z"], model.MonthEntries.Select(entry => entry.Title));
            var changes = 0;
            model.PropertyChanged += (_, _) => changes++;
            clock.Now = end;
            model.Reload();
            Assert.Equal(["Z", "A"], model.MonthEntries.Select(entry => entry.Title));
            Assert.Equal(["Z", "A"], model.SelectedEntries.Select(entry => entry.Title));
            Assert.Equal(new DateOnly(2026, 10, 14), model.SelectedDate);
            Assert.Equal(new DateOnly(2026, 10, 1), model.Month);
            Assert.Equal(1, changes);
            return Task.CompletedTask;
        });
    }

    private static ResourceScheduleSnapshot Build(DateTimeOffset now, params ResourceAnnouncement[] events) =>
        ResourceScheduleProjector.Build(Catalog(events), BuiltInGameIds.Endfield, now);

    private static ResourceCatalog Catalog(IEnumerable<ResourceAnnouncement> events) => new()
    {
        Revision = 1, PublishedAt = DateTimeOffset.Parse("2026-10-07T12:00:00+08:00"),
        Announcements = events.ToArray()
    };

    private static ResourceAnnouncement Event(string name, DateOnly start, DateOnly? end = null) => new()
    {
        GameId = BuiltInGameIds.Endfield, Key = name, Title = name, Kind = "event",
        StartsOn = start, EndsOn = end, Url = "https://endfield.hypergryph.com/news/2650"
    };

    private static async Task WithModelAsync(DateTimeOffset now, ResourceAnnouncement[] events,
        Func<EndfieldCommunityViewModel, Clock, Task> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "calendar-order-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new Handler(Catalog(events)));
            var service = new ResourceUpdateService(http, root);
            await service.RefreshAsync();
            var clock = new Clock(now);
            var model = new EndfieldCommunityViewModel(service, clock);
            model.Reload();
            await check(model, clock);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var parent = Path.GetDirectoryName(root)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(ResourceCatalog catalog) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(catalog, ResourceUpdateService.JsonOptions))
            });
    }
}