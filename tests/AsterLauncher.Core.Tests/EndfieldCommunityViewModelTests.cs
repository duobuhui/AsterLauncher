using System.Net;
using System.Text.Json;
using AsterLauncher.App.ViewModels;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldCommunityViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "endfield-community-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Published = DateTimeOffset.Parse("2026-10-07T12:00:00+08:00");
    private static ResourceCatalog Feed => new() { Revision = 1, PublishedAt = Published };
    private static ResourceAnnouncement Event(string key, string kind, DateOnly start, DateOnly? end = null) => new()
    { GameId = BuiltInGameIds.Endfield, Key = key, Title = key, Kind = kind, StartsOn = start, EndsOn = end, Url = "https://endfield.hypergryph.com/news/3805" };
    private static ResourceCode Code(string code) => new()
    { GameId = BuiltInGameIds.Endfield, Code = code, Reward = "公开奖励说明", Source = "https://endfield.hypergryph.com/news/3805" };

    private sealed class CalendarClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task MinuteTicksKeepOpenUiStableUntilPublishedStatusChanges()
    {
        var clock = new CalendarClock(DateTimeOffset.Parse("2026-10-07T11:58:00+08:00"));
        using var handler = new Handler { Catalog = Feed with { Announcements = [
            new ResourceAnnouncement { GameId = "endfield", Kind = "event", Title = "活动",
                StartsAt = DateTimeOffset.Parse("2026-10-07T12:00:00+08:00"),
                EndsAt = DateTimeOffset.Parse("2026-10-07T14:00:00+08:00"), Url = "https://endfield.hypergryph.com/" }] } };
        using var http = new HttpClient(handler);
        var service = new ResourceUpdateService(http, _root); await service.RefreshAsync();
        var model = new EndfieldCommunityViewModel(service, clock); model.Reload();
        var changes = 0; model.PropertyChanged += (_, _) => changes++;
        clock.Now = clock.Now.AddMinutes(1); model.Reload();
        Assert.Equal(0, changes);
        Assert.True(Assert.Single(model.Snapshot.Entries).IsUpcoming);
        clock.Now = clock.Now.AddMinutes(1); model.Reload();
        Assert.Equal(1, changes);
        Assert.True(Assert.Single(model.Snapshot.Entries).IsActive);
    }

    [Fact]
    public void MidnightRefreshUpdatesTodayWithoutChangingBrowsedMonth()
    {
        using var http = new HttpClient(new Handler());
        var clock = new CalendarClock(DateTimeOffset.Parse("2026-10-31T23:59:00+08:00"));
        var model = new EndfieldCommunityViewModel(new ResourceUpdateService(http, _root), clock);
        model.Reload(); model.Select(new(2026, 9, 12));
        clock.Now = clock.Now.AddMinutes(1); model.Reload();
        Assert.Equal(new DateOnly(2026, 11, 1), model.Snapshot.Today);
        Assert.Equal(new DateOnly(2026, 9, 12), model.SelectedDate);
        Assert.Equal(new DateOnly(2026, 9, 1), model.Month);
        model.Today();
        Assert.Equal(new DateOnly(2026, 11, 1), model.SelectedDate);
    }
    [Fact]
    public void EmptyOfflineViewInitializesTodayWithoutFabricatingCodesOrCalendar()
    {
        using var http = new HttpClient(new Handler());
        var model = new EndfieldCommunityViewModel(new ResourceUpdateService(http, _root));
        model.Reload();
        Assert.Equal(model.Snapshot.Today, model.SelectedDate);
        Assert.Equal(new DateOnly(model.SelectedDate.Year, model.SelectedDate.Month, 1), model.Month);
        Assert.Empty(model.MonthEntries);
        Assert.Empty(model.SelectedEntries);
        Assert.Empty(model.AvailableCodes);
        Assert.Empty(model.ExpiredCodes);
        Assert.Contains("尚未取得公开资料", model.UpdatedText);
    }

    [Fact]
    public void MonthNavigationClampsLeapDayAndPreservesDayAcrossYearBoundary()
    {
        using var http = new HttpClient(new Handler());
        var model = new EndfieldCommunityViewModel(new ResourceUpdateService(http, _root));
        model.Reload();
        model.Select(new(2028, 1, 31));
        model.MoveMonth(1);
        Assert.Equal(new DateOnly(2028, 2, 1), model.Month);
        Assert.Equal(new DateOnly(2028, 2, 29), model.SelectedDate);
        model.Select(new(2027, 1, 31));
        model.MoveMonth(1);
        Assert.Equal(new DateOnly(2027, 2, 28), model.SelectedDate);
        model.Select(new(2027, 12, 31));
        model.MoveMonth(1);
        Assert.Equal(new DateOnly(2028, 1, 31), model.SelectedDate);
        model.MoveMonth(-1);
        Assert.Equal(new DateOnly(2027, 12, 31), model.SelectedDate);
        model.Today();
        Assert.Equal(model.Snapshot.Today, model.SelectedDate);
    }

    [Fact]
    public async Task FilterGroupsVersionWithMaintenanceAndLeavesCodesIndependent()
    {
        using var handler = new Handler { Catalog = Feed with
        {
            Announcements = [Event("活动", "event", new(2026, 10, 7)), Event("更新", "version", new(2026, 10, 7)),
                Event("维护", "maintenance", new(2026, 10, 7)), Event("前瞻", "livestream", new(2026, 10, 7))],
            Codes = [Code("PUBLIC")]
        } };
        using var http = new HttpClient(handler);
        var service = new ResourceUpdateService(http, _root); await service.RefreshAsync();
        var model = new EndfieldCommunityViewModel(service); model.Reload(); model.Select(new(2026, 10, 7));
        Assert.Equal(4, model.MonthEntries.Count);
        model.SetFilter("updates");
        Assert.Equal(new[] { "maintenance", "version" }, model.SelectedEntries.Select(e => e.Kind).Order());
        Assert.Single(model.AvailableCodes);
        model.SetFilter("event");
        Assert.Equal("event", Assert.Single(model.SelectedEntries).Kind);
        model.SetFilter("invalid-filter");
        Assert.Equal("event", model.Filter);
        model.SetFilter("pool");
        Assert.Empty(model.MonthEntries);
        Assert.Empty(model.SelectedEntries);
        model.SetFilter("all");
        Assert.Equal(4, model.SelectedEntries.Count);
    }

    [Fact]
    public async Task CrossMonthRangeAppearsOnBothBoundariesButPointEventOccupiesOnlyPublishedDay()
    {
        using var handler = new Handler { Catalog = Feed with { Announcements =
        [Event("跨月活动", "event", new(2026, 9, 24), new(2026, 10, 15)), Event("版本更新", "version", new(2026, 10, 15))] } };
        using var http = new HttpClient(handler);
        var service = new ResourceUpdateService(http, _root); await service.RefreshAsync();
        var model = new EndfieldCommunityViewModel(service); model.Reload(); model.Select(new(2026, 10, 15));
        Assert.Equal(2, model.MonthEntries.Count);
        Assert.Equal("版本更新", model.SelectedEntries[0].Title);
        Assert.Equal(2, model.SelectedEntries.Count);
        Assert.Empty(model.ForDay(new(2026, 10, 16)));
        model.Select(new(2026, 9, 24));
        Assert.Single(model.MonthEntries);
        Assert.Single(model.SelectedEntries);
        Assert.Equal(new DateOnly(2026, 9, 1), model.Month);
    }

    [Fact]
    public async Task SuccessfulResourceRefreshPreservesSelectedMonthDateAndFilter()
    {
        using var handler = new Handler { Catalog = Feed };
        using var http = new HttpClient(handler);
        var service = new ResourceUpdateService(http, _root); await service.RefreshAsync();
        var model = new EndfieldCommunityViewModel(service); model.Reload();
        model.Select(new(2027, 2, 14)); model.SetFilter("event");
        handler.Catalog = Feed with { Revision = 2, Announcements = [Event("新活动", "event", new(2027, 2, 14))] };
        await model.RefreshAsync();
        Assert.Equal(2, model.Snapshot.Revision);
        Assert.Equal(new DateOnly(2027, 2, 1), model.Month);
        Assert.Equal(new DateOnly(2027, 2, 14), model.SelectedDate);
        Assert.Equal("event", model.Filter);
        Assert.Single(model.SelectedEntries);
        Assert.Equal("资料已更新", model.Message);
        Assert.False(model.IsRefreshing);
        await model.RefreshAsync();
        Assert.Equal("已是最新资料", model.Message);
    }

    [Fact]
    public async Task FailedRefreshRetainsLocalDataAndRestoresRefreshAction()
    {
        using var handler = new Handler { Catalog = Feed with { Codes = [Code("PUBLIC")] } };
        using var http = new HttpClient(handler);
        var service = new ResourceUpdateService(http, _root); await service.RefreshAsync();
        var model = new EndfieldCommunityViewModel(service); model.Reload();
        handler.Offline = true;
        await model.RefreshAsync();
        Assert.Equal(1, model.Snapshot.Revision);
        Assert.Equal("PUBLIC", Assert.Single(model.AvailableCodes).Code);
        Assert.False(model.IsRefreshing);
        Assert.Contains("继续显示本地资料", model.Message);
    }

    [Fact]
    public async Task ConcurrentRefreshClicksShareOneRequest()
    {
        using var handler = new Handler { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var http = new HttpClient(handler);
        var model = new EndfieldCommunityViewModel(new ResourceUpdateService(http, _root)); model.Reload();
        var first = model.RefreshAsync();
        Assert.True(model.IsRefreshing);
        await model.RefreshAsync();
        Assert.Equal(1, handler.Requests);
        handler.Release.SetResult();
        await first;
        Assert.False(model.IsRefreshing);
        Assert.Equal(1, model.Snapshot.Revision);
    }

    [Fact]
    public async Task ExpiredCodesAreSeparatedWhileUnknownExpiryNeverBecomesVerifiedValid()
    {
        using var handler = new Handler { Catalog = Feed with { Codes =
        [Code("EXPIRED") with { ExpiresOn = new(2020, 1, 1) }, Code("UNKNOWN"), Code("UPCOMING") with { StartsOn = new(2099, 1, 1) }] } };
        using var http = new HttpClient(handler);
        var service = new ResourceUpdateService(http, _root); await service.RefreshAsync();
        var model = new EndfieldCommunityViewModel(service); model.Reload();
        Assert.Equal("EXPIRED", Assert.Single(model.ExpiredCodes).Code);
        Assert.False(model.ExpiredCodes[0].CanCopy);
        Assert.Equal(2, model.AvailableCodes.Count);
        var unknown = Assert.Single(model.AvailableCodes, c => c.Code == "UNKNOWN");
        Assert.Equal(ResourceCodeStatus.ExpiryUnspecified, unknown.Status);
        Assert.Equal("有效期未公布", unknown.StatusText);
        Assert.True(unknown.CanCopy);
        Assert.True(Assert.Single(model.AvailableCodes, c => c.Code == "UPCOMING").IsUpcoming);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public ResourceCatalog Catalog { get; set; } = Feed;
        public bool Offline { get; set; }
        public int Requests { get; private set; }
        public TaskCompletionSource? Release { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Offline) throw new HttpRequestException("offline");
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(Catalog, ResourceUpdateService.JsonOptions)) };
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
