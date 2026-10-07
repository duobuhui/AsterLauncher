using System.Net;
using System.Text.Json;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class ResourceScheduleTests
{
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset Now = At("2026-10-07T12:00:00+08:00");
    private static ResourceCode Code => new() { GameId = "endfield", Code = "PUBLICTEST", Reward = "公开奖励说明", Source = "https://endfield.hypergryph.com/news/3805" };
    private static ResourceAnnouncement Event => new() { GameId = "endfield", Key = "event-1", Title = "公开活动", Kind = "event", StartsOn = new(2026, 10, 5), EndsOn = new(2026, 10, 10), Url = "https://endfield.hypergryph.com/news/3805" };
    private static ResourceCatalog Feed => new() { Revision = 1, PublishedAt = Now };
    private static ResourceCodeEntry Project(ResourceCode code, DateTimeOffset? now = null) => Assert.Single(ResourceScheduleProjector.Build(Feed with { Codes = [code] }, "endfield", now ?? Now).Codes);
    private static ResourceCalendarEntry Project(ResourceAnnouncement item, DateTimeOffset? now = null) => Assert.Single(ResourceScheduleProjector.Build(Feed with { Announcements = [item] }, "endfield", now ?? Now).Entries);
    private static ResourceCatalog Parse(ResourceCatalog catalog) => ResourceUpdateService.Parse(JsonSerializer.SerializeToUtf8Bytes(catalog, ResourceUpdateService.JsonOptions));

    [Fact]
    public void CodeWithoutExpiryNeverClaimsValidityAndRemainsCopyable()
    {
        var item = Project(Code);
        Assert.Equal(ResourceCodeStatus.ExpiryUnspecified, item.Status);
        Assert.Equal("有效期未公布", item.StatusText);
        Assert.Equal("有效期未公布", item.DateText);
        Assert.Equal("平台未注明", item.PlatformText);
        Assert.True(item.CanCopy);
        Assert.False(item.IsExpired);
    }

    [Fact]
    public void DateOnlyCodeExpiresAfterBeijingDateBoundaryWithoutInventingTime()
    {
        var code = Code with { ExpiresOn = new(2026, 10, 7) };
        var beforeMidnight = Project(code, At("2026-10-07T15:59:59Z"));
        Assert.Equal(ResourceCodeStatus.TimeUnconfirmed, beforeMidnight.Status);
        Assert.Equal("今日到期 · 时间未公布", beforeMidnight.StatusText);
        Assert.Equal("截止 2026/10/07（具体时间未公布）", beforeMidnight.DateText);
        Assert.True(beforeMidnight.CanCopy);
        var midnight = Project(code, At("2026-10-07T16:00:00Z"));
        Assert.Equal(ResourceCodeStatus.Expired, midnight.Status);
        Assert.False(midnight.CanCopy);
    }

    [Fact]
    public void ExactCodeExpiryAndOpeningRespectTheirInstants()
    {
        var code = Code with { StartsAt = Now, ExpiresAt = Now.AddHours(1) };
        Assert.True(Project(code, Now.AddTicks(-1)).IsUpcoming);
        Assert.Equal(ResourceCodeStatus.WithinPublishedPeriod, Project(code, Now).Status);
        Assert.Equal("尚未到期", Project(code, Now).StatusText);
        Assert.False(Project(code, Now.AddHours(1).AddTicks(-1)).IsExpired);
        Assert.True(Project(code, Now.AddHours(1)).IsExpired);
    }

    [Fact]
    public void DayOnlyStartStaysUncertainOnOpeningDayAndDoesNotInventExpiry()
    {
        var code = Code with { StartsOn = new(2026, 10, 7) };
        Assert.Equal(ResourceCodeStatus.Upcoming, Project(code, Now.AddDays(-1)).Status);
        Assert.Equal(ResourceCodeStatus.TimeUnconfirmed, Project(code).Status);
        Assert.Equal(ResourceCodeStatus.ExpiryUnspecified, Project(code, Now.AddDays(1)).Status);
        Assert.Equal("2026/10/07 — 未公布", Project(code).DateText);
    }

    [Fact]
    public void DateOnlyCalendarBoundariesDoNotAssertActiveAtUnpublishedHours()
    {
        Assert.True(Project(Event, At("2026-10-04T12:00:00+08:00")).IsUpcoming);
        Assert.Equal("今日开始 · 时间未公布", Project(Event, At("2026-10-05T12:00:00+08:00")).StatusText);
        Assert.True(Project(Event).IsActive);
        Assert.Equal("今日结束 · 时间未公布", Project(Event, At("2026-10-10T23:59:59+08:00")).StatusText);
        Assert.True(Project(Event, At("2026-10-11T00:00:00+08:00")).IsExpired);
    }

    [Fact]
    public void ExactEventEndsAtTheExactInstant()
    {
        var item = Event with { StartsOn = null, EndsOn = null, StartsAt = Now, EndsAt = Now.AddHours(1) };
        Assert.True(Project(item, Now.AddTicks(-1)).IsUpcoming);
        Assert.True(Project(item, Now).IsActive);
        Assert.True(Project(item, Now.AddHours(1)).IsExpired);
        Assert.Equal("2026/10/07 12:00 — 2026/10/07 13:00", Project(item).DateText);
    }

    [Fact]
    public void MixedPrecisionKeepsTheWholeRangeAtDayPrecision()
    {
        var item = Event with { StartsOn = null, StartsAt = At("2026-10-04T20:00:00Z") };
        Assert.Equal("2026/10/05 — 2026/10/10", Project(item).DateText);
        Assert.Equal(new DateOnly(2026, 10, 5), Project(item).StartDate);
    }

    [Fact]
    public void CalendarOverlapIncludesCrossMonthRangesAndClipsUnpublishedEnd()
    {
        var item = Project(Event with { StartsOn = new(2026, 9, 24), EndsOn = new(2026, 10, 15) });
        Assert.True(item.Overlaps(new(2026, 10, 1), new(2026, 10, 31)));
        Assert.True(item.Overlaps(new(2026, 10, 15), new(2026, 10, 15)));
        Assert.False(item.Overlaps(new(2026, 10, 16), new(2026, 10, 16)));
        var noEnd = Project(Event with { EndsOn = null });
        Assert.True(noEnd.Overlaps(new(2026, 10, 5), new(2026, 10, 5)));
        Assert.False(noEnd.Overlaps(new(2026, 10, 6), new(2026, 10, 6)));
        Assert.Equal("结束时间未公布", noEnd.StatusText);
    }

    [Fact]
    public void PublicPoolsEnterCalendarWithoutAnyUserArchive()
    {
        var pool = new ResourcePool { GameId = "endfield", Key = "公共卡池", Name = "公共卡池", Phase = "2", Version = "版本名称", StartsOn = new(2026, 10, 5), EndsOn = new(2026, 11, 1), FeaturedOperator = "干员", Weapon = "武器", Source = "https://endfield.hypergryph.com/news/3805" };
        var snapshot = ResourceScheduleProjector.Build(Feed with { Pools = [pool, pool with { GameId = "genshin", Key = "other" }, pool with { Key = "undated", StartsOn = null, EndsOn = null }] }, "endfield", Now);
        var item = Assert.Single(snapshot.Entries);
        Assert.Equal("pool:公共卡池:2", item.Key);
        Assert.Equal("公共卡池 #2", item.Title);
        Assert.Equal("版本名称", item.Version);
        Assert.Equal("pool", item.Kind);
        Assert.Equal("卡池", item.KindText);
        Assert.Equal("公共卡池", item.ImageKey);
        Assert.Equal("干员 · 专武「武器」", item.Detail);
        Assert.Equal(new DateOnly(2026, 10, 7), snapshot.Today);
    }

    [Theory]
    [InlineData("event", "活动")]
    [InlineData("maintenance", "维护")]
    [InlineData("livestream", "前瞻")]
    [InlineData("version", "版本更新")]
    public void SupportedKindsHaveUserFacingLabels(string kind, string label)
        => Assert.Equal(label, Project(Event with { Kind = kind }).KindText);

    [Fact]
    public void RegionAndPlatformScopesDoNotLeakInternationalOrMobileOnlyCodes()
    {
        var catalog = Feed with
        {
            Codes = [Code, Code with { Code = "INTERNATIONAL", Region = "GLOBAL" }, Code with { Code = "MOBILE", Platforms = ["Android"] },
                Code with { Code = "WORLDWIDE", Region = "ALL", Platforms = ["ALL"] }, Code with { Code = "WINDOWS", Platforms = ["Windows"] },
                Code with { Code = "OTHERGAME", GameId = "genshin" }],
            Announcements = [Event, Event with { Key = "mobile", Platforms = ["iOS"] }, Event with { Key = "overseas", Region = "GLOBAL" }]
        };
        var snapshot = ResourceScheduleProjector.Build(catalog, "endfield", Now);
        Assert.Equal(new[] { "PUBLICTEST", "WINDOWS", "WORLDWIDE" }, snapshot.Codes.Select(c => c.Code));
        Assert.Single(snapshot.Entries);
    }

    [Fact]
    public void EmptyOfflineSnapshotRemainsEmptyAndUsesInjectedBeijingClock()
    {
        var snapshot = ResourceScheduleProjector.Build(null, "endfield", At("2026-10-07T17:00:00Z"));
        Assert.Equal(new DateOnly(2026, 10, 8), snapshot.Today);
        Assert.Empty(snapshot.Entries);
        Assert.Empty(snapshot.Codes);
        Assert.Null(snapshot.PublishedAt);
    }

    [Fact]
    public void NewFieldsRoundTripAndLegacyExactAnnouncementsRemainReadable()
    {
        var item = Event with { Kind = "version", Description = "官方版本说明", Version = "版本名称", Platforms = ["PC"] };
        var code = Code with { Version = "版本名称", StartsOn = new(2026, 10, 5), ExpiresOn = new(2026, 10, 10), Platforms = ["PC"] };
        var parsed = Parse(Feed with { Codes = [code], Announcements = [item] });
        Assert.Equal(code.ExpiresOn, Assert.Single(parsed.Codes).ExpiresOn);
        Assert.Equal(item.Description, Assert.Single(parsed.Announcements).Description);
        Assert.Equal(item.StartsOn, Assert.Single(parsed.Announcements).StartsOn);
        var legacy = Event with { Key = null, StartsOn = null, EndsOn = null, StartsAt = Now, EndsAt = Now.AddHours(1) };
        Assert.Equal(legacy.StartsAt, Assert.Single(Parse(Feed with { Announcements = [legacy] }).Announcements).StartsAt);
    }

    [Fact]
    public void AnnouncementValidationRejectsAmbiguousDatesMissingStartAndDuplicates()
    {
        foreach (var invalid in new[]
        {
            Event with { StartsAt = Now }, Event with { EndsAt = Now }, Event with { EndsOn = new(2026, 10, 4) },
            Event with { StartsOn = null }, Event with { StartsOn = default(DateOnly) }, Event with { Version = "" },
            Event with { Kind = "script" }, Event with { Description = new string('x', 601) }, Event with { Platforms = null! },
            Event with { Platforms = ["PC", "pc"] }, Event with { Url = "https://endfield.hypergryph.com/news/3805?token=secret" }
        }) Assert.Throws<InvalidDataException>(() => Parse(Feed with { Announcements = [invalid] }));
        Assert.Throws<InvalidDataException>(() => Parse(Feed with { Announcements = [Event, Event with { Title = "重复ID" }] }));
    }

    [Fact]
    public void CodeValidationRejectsAmbiguousReversedDatesAndDuplicates()
    {
        foreach (var invalid in new[]
        {
            Code with { ExpiresAt = Now, ExpiresOn = new(2026, 10, 7) }, Code with { StartsAt = Now, StartsOn = new(2026, 10, 7) },
            Code with { StartsOn = new(2026, 10, 8), ExpiresOn = new(2026, 10, 7) }, Code with { StartsAt = Now, ExpiresAt = Now },
            Code with { ExpiresOn = default(DateOnly) }, Code with { ExpiresAt = default(DateTimeOffset) }, Code with { Version = " " },
            Code with { Platforms = ["PC", "pc"] }, Code with { Source = "https://endfield.hypergryph.com/news/3805?token=secret" }
        }) Assert.Throws<InvalidDataException>(() => Parse(Feed with { Codes = [invalid] }));
        Assert.Throws<InvalidDataException>(() => Parse(Feed with { Codes = [Code, Code with { Code = "publictest" }] }));
    }

    [Fact]
    public async Task ResourceOnlyUpdatePersistsNewFieldsAndKeepsSameRevisionProtection()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "resource-schedule-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new FeedHandler { Catalog = Feed };
            using var http = new HttpClient(handler);
            var service = new ResourceUpdateService(http, root);
            Assert.True(await service.RefreshAsync());
            handler.Catalog = Feed with { Revision = 2, Announcements = [Event with { Version = "版本名称" }], Codes = [Code with { ExpiresOn = new(2026, 10, 10) }] };
            Assert.True(await service.RefreshAsync());
            var restarted = new ResourceUpdateService(http, root);
            await restarted.LoadAsync();
            Assert.Equal(new DateOnly(2026, 10, 10), Assert.Single(restarted.Current!.Codes).ExpiresOn);
            Assert.Equal("版本名称", Assert.Single(restarted.Current.Announcements).Version);
            handler.Catalog = handler.Catalog with { Codes = [Code with { ExpiresOn = new(2026, 10, 11) }] };
            await Assert.ThrowsAsync<InvalidDataException>(() => restarted.RefreshAsync());
            Assert.Equal(new DateOnly(2026, 10, 10), Assert.Single(restarted.Current.Codes).ExpiresOn);
            handler.Catalog = handler.Catalog with { Revision = 3 };
            Assert.True(await restarted.RefreshAsync());
            Assert.Equal(new DateOnly(2026, 10, 11), Assert.Single(restarted.Current.Codes).ExpiresOn);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OfflineFirstRunUsesBundledTextWithoutWritingDataOrRequiringImages()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "resource-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var bundle = Path.Combine(root, "bundle.json");
            var bundled = Feed with { Announcements = [Event], Codes = [Code], Images = [new ResourceImage { GameId = "endfield", Kind = "banner", Key = "image", File = "images/test.png", Size = 67, Sha256 = new string('a', 64), Source = "https://endfield.hypergryph.com/news/3805", Attribution = "发行商" }] };
            await File.WriteAllBytesAsync(bundle, JsonSerializer.SerializeToUtf8Bytes(bundled, ResourceUpdateService.JsonOptions));
            using var http = new HttpClient(new FeedHandler());
            var data = Path.Combine(root, "Data");
            var service = new ResourceUpdateService(http, data, bundle);
            await service.LoadAsync();
            Assert.Equal(1, service.Current!.Revision);
            Assert.Single(service.Current.Announcements);
            Assert.Null(service.FindImage("endfield", "banner", "image"));
            Assert.False(Directory.Exists(data));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(2, 1, 2)]
    [InlineData(1, 2, 2)]
    [InlineData(2, 2, 2)]
    public async Task OfflineLoadChoosesHighestRevisionAndSameRevisionCacheWins(long cacheRevision, long bundleRevision, long expectedRevision)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "resource-bundle-version-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = Path.Combine(root, "Data");
            Directory.CreateDirectory(Path.Combine(data, "resources"));
            var bundle = Path.Combine(root, "bundle.json");
            await File.WriteAllBytesAsync(bundle, JsonSerializer.SerializeToUtf8Bytes(Feed with { Revision = bundleRevision, Codes = [Code with { Code = "BUNDLE" }] }, ResourceUpdateService.JsonOptions));
            var cache = Path.Combine(data, "resources", "catalog.json");
            var cacheBytes = JsonSerializer.SerializeToUtf8Bytes(Feed with { Revision = cacheRevision, Codes = [Code with { Code = "CACHE" }] }, ResourceUpdateService.JsonOptions);
            await File.WriteAllBytesAsync(cache, cacheBytes);
            using var http = new HttpClient(new FeedHandler());
            var service = new ResourceUpdateService(http, data, bundle);
            await service.LoadAsync();
            Assert.Equal(expectedRevision, service.Current!.Revision);
            Assert.Equal(cacheRevision >= bundleRevision ? "CACHE" : "BUNDLE", Assert.Single(service.Current.Codes).Code);
            Assert.Equal(cacheBytes, await File.ReadAllBytesAsync(cache));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CorruptCacheFallsBackWithoutDeletingItAndIdenticalFeedPersistsForOfflineRestart()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "resource-bundle-repair-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = Path.Combine(root, "Data");
            Directory.CreateDirectory(Path.Combine(data, "resources"));
            var bundle = Path.Combine(root, "bundle.json");
            var bundled = Feed with { Codes = [Code] };
            await File.WriteAllBytesAsync(bundle, JsonSerializer.SerializeToUtf8Bytes(bundled, ResourceUpdateService.JsonOptions));
            var cache = Path.Combine(data, "resources", "catalog.json");
            await File.WriteAllTextAsync(cache, "incomplete-file");
            using var http = new HttpClient(new FeedHandler { Catalog = bundled });
            var service = new ResourceUpdateService(http, data, bundle);
            await service.LoadAsync();
            Assert.Equal("incomplete-file", await File.ReadAllTextAsync(cache));
            Assert.Equal(1, service.Current!.Revision);
            Assert.False(await service.RefreshAsync());
            var restarted = new ResourceUpdateService(http, data);
            await restarted.LoadAsync();
            Assert.Equal(Code.Code, Assert.Single(restarted.Current!.Codes).Code);
            Assert.False(await restarted.RefreshAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("2026-10-08T00:00:00+08:00", 7)]
    [InlineData("2026-10-07T16:00:00Z", 7)]
    [InlineData("2026-10-08T00:00:00.0000001+08:00", 8)]
    public void ExactMidnightEndDoesNotOccupyFollowingDayWhileLaterInstantsDo(string endsAt, int lastDay)
    {
        var item = Event with { StartsOn = null, EndsOn = null, StartsAt = Now, EndsAt = At(endsAt) };
        var projected = Project(item);
        Assert.Equal(new DateOnly(2026, 10, lastDay), projected.EndDate);
        Assert.Equal(lastDay == 8, projected.Overlaps(new(2026, 10, 8), new(2026, 10, 8)));
        Assert.True(projected.Overlaps(new(2026, 10, 7), new(2026, 10, 7)));
        Assert.Equal(item.EndsAt, projected.EndsAt);
        Assert.Contains("2026/10/08 00:00", projected.DateText);
    }

    [Fact]
    public void PoolMidnightEndUsesHalfOpenCalendarRangeAndDateOnlyEndStillIncludesPublishedDay()
    {
        var pool = new ResourcePool { GameId = "endfield", Key = "边界卡池", Name = "边界卡池", StartsAt = Now, EndsAt = At("2026-10-07T16:00:00Z"), Source = "https://endfield.hypergryph.com/news/3805" };
        var exact = Assert.Single(ResourceScheduleProjector.Build(Feed with { Pools = [pool] }, "endfield", Now).Entries);
        Assert.False(exact.Overlaps(new(2026, 10, 8), new(2026, 10, 8)));
        var day = Project(Event with { StartsOn = new(2026, 10, 7), EndsOn = new(2026, 10, 8) });
        Assert.True(day.Overlaps(new(2026, 10, 8), new(2026, 10, 8)));
        Assert.False(day.Overlaps(new(2026, 10, 9), new(2026, 10, 9)));
    }

    [Fact]
    public void VersionPointReachesPublishedInstantWithoutClaimingMaintenanceCompleted()
    {
        var point = Event with { Kind = "version", StartsOn = null, EndsOn = null, StartsAt = Now };
        Assert.True(Project(point, Now.AddTicks(-1)).IsUpcoming);
        var reached = Project(point, Now);
        Assert.Equal(ResourceScheduleStatus.Reached, reached.Status);
        Assert.Equal("已到公布时间", reached.StatusText);
        Assert.Equal("2026/10/07 12:00", reached.DateText);
        Assert.False(reached.IsExpired);
        Assert.False(reached.IsActive);
        Assert.Null(reached.EndDate);
        Assert.Equal("结束时间未公布", Project(point with { Kind = "maintenance" }).StatusText);
    }

    [Fact]
    public void DateOnlyVersionPointDoesNotInventOpeningHourOrRemainAwaitingAnEnd()
    {
        var point = Event with { Kind = "version", StartsOn = new(2026, 10, 7), EndsOn = null };
        Assert.True(Project(point, Now.AddDays(-1)).IsUpcoming);
        var onDay = Project(point);
        Assert.Equal(ResourceScheduleStatus.TimeUnconfirmed, onDay.Status);
        Assert.Equal("已到公布日期 · 具体时间未公布", onDay.StatusText);
        Assert.Equal("2026/10/07", onDay.DateText);
        Assert.False(onDay.IsActive);
        var afterDay = Project(point, Now.AddDays(1));
        Assert.Equal(ResourceScheduleStatus.Reached, afterDay.Status);
        Assert.False(afterDay.IsExpired);
        Assert.DoesNotContain("结束", afterDay.StatusText);
    }

    [Fact]
    public async Task UnsafeCacheDirectoryFallsBackToBundledTextWithoutFollowingOrDeletingJunction()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "resource-junction-fallback-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(root, "Data", "resources");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Data"));
            var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
            var keep = Path.Combine(outside, "keep.txt"); await File.WriteAllTextAsync(keep, "keep");
            var fixture = Path.Combine(AppContext.BaseDirectory, "ResourceFeed");
            var image = ResourceUpdateService.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture, "catalog.json"))).Images.First();
            var bundled = Feed with { Codes = [Code], Images = [image] };
            var bundle = Path.Combine(root, "bundle.json");
            await File.WriteAllBytesAsync(bundle, JsonSerializer.SerializeToUtf8Bytes(bundled, ResourceUpdateService.JsonOptions));
            await CreateJunctionAsync(link, outside);
            using var http = new HttpClient(new FeedHandler());
            var service = new ResourceUpdateService(http, Path.Combine(root, "Data"), bundle);
            await service.LoadAsync();
            Assert.Equal(Code.Code, Assert.Single(service.Current!.Codes).Code);
            Assert.Null(service.FindImage(image.GameId, image.Kind, image.Key));
            Assert.Equal("keep", await File.ReadAllTextAsync(keep));
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        }
        finally
        {
            if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BundledFallbackUsesOnlyVerifiedCacheImagesAndRefreshRepairsWithoutChangingRevision()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "resource-image-fallback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "ResourceFeed");
            var images = ResourceUpdateService.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture, "catalog.json"))).Images.Take(2).ToArray();
            Assert.Equal(2, images.Length);
            var bundled = Feed with { Images = images };
            var data = Path.Combine(root, "Data");
            var content = Path.Combine(data, "resources", "content"); Directory.CreateDirectory(content);
            var bundle = Path.Combine(root, "bundle.json");
            var catalogBytes = JsonSerializer.SerializeToUtf8Bytes(bundled, ResourceUpdateService.JsonOptions);
            await File.WriteAllBytesAsync(bundle, catalogBytes);
            var cache = Path.Combine(data, "resources", "catalog.json"); await File.WriteAllBytesAsync(cache, catalogBytes);
            var healthy = Path.Combine(content, images[0].Sha256 + Path.GetExtension(images[0].File));
            var corrupt = Path.Combine(content, images[1].Sha256 + Path.GetExtension(images[1].File));
            File.Copy(Path.Combine(fixture, images[0].File), healthy);
            var badBytes = new byte[checked((int)images[1].Size)]; await File.WriteAllBytesAsync(corrupt, badBytes);
            using var http = new HttpClient(new FixtureImageFeedHandler(bundled, fixture));
            var service = new ResourceUpdateService(http, data, bundle);
            await service.LoadAsync();
            Assert.NotNull(service.Current);
            Assert.Equal(healthy, service.FindImage(images[0].GameId, images[0].Kind, images[0].Key));
            Assert.Null(service.FindImage(images[1].GameId, images[1].Kind, images[1].Key));
            Assert.Equal(badBytes, await File.ReadAllBytesAsync(corrupt));
            Assert.Equal(catalogBytes, await File.ReadAllBytesAsync(cache));
            Assert.False(await service.RefreshAsync());
            Assert.Equal(corrupt, service.FindImage(images[1].GameId, images[1].Kind, images[1].Key));
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixture, images[1].File)), await File.ReadAllBytesAsync(corrupt));
            Assert.Equal(1, service.Current.Revision);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FindImageReturnsNullWhenVerifiedDirectoryBecomesJunction()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "resource-image-junction-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(root, "Data", "resources", "content");
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "ResourceFeed");
            var image = ResourceUpdateService.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture, "catalog.json"))).Images.First();
            var catalog = Feed with { Images = [image] };
            Directory.CreateDirectory(link);
            await File.WriteAllBytesAsync(Path.Combine(root, "Data", "resources", "catalog.json"), JsonSerializer.SerializeToUtf8Bytes(catalog, ResourceUpdateService.JsonOptions));
            var path = Path.Combine(link, image.Sha256 + Path.GetExtension(image.File));
            File.Copy(Path.Combine(fixture, image.File), path);
            using var http = new HttpClient(new FeedHandler());
            var service = new ResourceUpdateService(http, Path.Combine(root, "Data"));
            await service.LoadAsync();
            Assert.Equal(path, service.FindImage(image.GameId, image.Kind, image.Key));
            File.Delete(path); Directory.Delete(link);
            var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
            var keep = Path.Combine(outside, "keep.txt"); await File.WriteAllTextAsync(keep, "keep");
            await CreateJunctionAsync(link, outside);
            Assert.Null(service.FindImage(image.GameId, image.Kind, image.Key));
            Assert.Equal("keep", await File.ReadAllTextAsync(keep));
        }
        finally
        {
            if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task CreateJunctionAsync(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.Environment.Remove("PSModulePath");
        foreach (var argument in new[] { "-NoProfile", "-Command", "New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null" }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class FixtureImageFeedHandler(ResourceCatalog catalog, string fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var relative = request.RequestUri!.AbsolutePath.Split("/resources/", 2)[1];
            var bytes = relative == "catalog.json" ? JsonSerializer.SerializeToUtf8Bytes(catalog, ResourceUpdateService.JsonOptions)
                : File.ReadAllBytes(Path.Combine(fixture, relative));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        public ResourceCatalog Catalog { get; set; } = Feed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(Catalog, ResourceUpdateService.JsonOptions)) });
    }
}
