using System.Globalization;

namespace AsterLauncher.Core;

public enum ResourceScheduleStatus { Upcoming, Ongoing, Ended, TimeUnconfirmed, Reached }
public enum ResourceCodeStatus { Upcoming, WithinPublishedPeriod, ExpiryUnspecified, Expired, TimeUnconfirmed }

public sealed record ResourceScheduleSnapshot
{
    public DateOnly Today { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public IReadOnlyList<ResourceCalendarEntry> Entries { get; init; } = [];
    public IReadOnlyList<ResourceCodeEntry> Codes { get; init; } = [];
}

public sealed record ResourceCalendarEntry
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Kind { get; init; } = "";
    public string KindText { get; init; } = "";
    public string Detail { get; init; } = "";
    public string DateText { get; init; } = "";
    public string StatusText { get; init; } = "";
    public string Source { get; init; } = "";
    public string? Version { get; init; }
    public string? ImageKey { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? EndsAt { get; init; }
    public ResourceScheduleStatus Status { get; init; }
    public bool IsExpired => Status == ResourceScheduleStatus.Ended;
    public bool IsUpcoming => Status == ResourceScheduleStatus.Upcoming;
    public bool IsActive => Status == ResourceScheduleStatus.Ongoing;
    public bool HasUncertainTime => Status == ResourceScheduleStatus.TimeUnconfirmed;
    // An unpublished end is not an infinite calendar bar. Its known start remains discoverable.
    public bool Overlaps(DateOnly from, DateOnly to) => StartDate <= to && (EndDate ?? StartDate) >= from;
}

public sealed record ResourceCodeEntry
{
    public string Code { get; init; } = "";
    public string Reward { get; init; } = "";
    public string Region { get; init; } = "";
    public string PlatformText { get; init; } = "";
    public string DateText { get; init; } = "";
    public string StatusText { get; init; } = "";
    public string Source { get; init; } = "";
    public string? Version { get; init; }
    public ResourceCodeStatus Status { get; init; }
    public bool IsExpired => Status == ResourceCodeStatus.Expired;
    public bool IsUpcoming => Status == ResourceCodeStatus.Upcoming;
    public bool CanCopy => !IsExpired;
}

/// <summary>Pure public-resource projection. The caller supplies the clock; display dates always use UTC+8.</summary>
public static class ResourceScheduleProjector
{
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    public static ResourceScheduleSnapshot Build(ResourceCatalog? catalog, string gameId, DateTimeOffset now,
        string region = "CN", string platform = "PC")
    {
        var today = LocalDate(now);
        if (catalog is null) return new() { Today = today };
        var entries = new List<ResourceCalendarEntry>();
        foreach (var pool in catalog.Pools.Where(p => p.GameId == gameId))
        {
            var start = Date(pool.StartsOn, pool.StartsAt);
            if (start is null) continue;
            var end = LastCalendarDate(pool.EndsOn, pool.EndsAt);
            var (status, statusText) = ScheduleStatus(pool.StartsAt, pool.EndsAt, pool.StartsOn, pool.EndsOn, now);
            entries.Add(new()
            {
                Key = "pool:" + pool.Key + ":" + pool.Phase,
                Title = pool.Name + (string.IsNullOrEmpty(pool.Phase) ? "" : " #" + pool.Phase),
                Kind = "pool", KindText = "卡池", Version = pool.Version,
                Detail = string.Join(" · ", new[] { pool.FeaturedOperator, pool.Weapon is { Length: > 0 } weapon ? "专武「" + weapon + "」" : null }.Where(s => !string.IsNullOrEmpty(s))),
                DateText = pool.DateText, Status = status, StatusText = statusText, Source = pool.Source,
                ImageKey = pool.Key, StartDate = start.Value, EndDate = end, StartsAt = pool.StartsAt, EndsAt = pool.EndsAt
            });
        }
        foreach (var item in catalog.Announcements.Where(a => a.GameId == gameId && RegionMatches(a.Region, region) && PlatformMatches(a.Platforms, platform)))
        {
            var start = Date(item.StartsOn, item.StartsAt);
            if (start is null) continue;
            var (status, statusText) = ScheduleStatus(item.StartsAt, item.EndsAt, item.StartsOn, item.EndsOn, now, isVersionPoint: item.Kind == "version");
            entries.Add(new()
            {
                Key = "announcement:" + (item.Key ?? item.Kind + ":" + item.Title + ":" + start.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Title = item.Title, Kind = item.Kind, KindText = KindText(item.Kind), Version = item.Version,
                Detail = item.Description ?? "",
                DateText = item.Kind == "version" && item.EndsAt is null && item.EndsOn is null
                    ? (item.StartsAt is { } pointTime ? Format(pointTime) : Format(item.StartsOn))
                    : RangeText(item.StartsAt, item.EndsAt, item.StartsOn, item.EndsOn),
                Status = status, StatusText = statusText, Source = item.Url,
                StartDate = start.Value, EndDate = LastCalendarDate(item.EndsOn, item.EndsAt), StartsAt = item.StartsAt, EndsAt = item.EndsAt
            });
        }
        return new()
        {
            Today = today, Revision = catalog.Revision, PublishedAt = catalog.PublishedAt,
            Entries = entries.OrderBy(e => e.StartDate).ThenBy(e => e.StartsAt).ThenBy(e => e.Kind).ThenBy(e => e.Key, StringComparer.Ordinal).ToArray(),
            Codes = catalog.Codes.Where(c => c.GameId == gameId && RegionMatches(c.Region, region) && PlatformMatches(c.Platforms, platform))
                .Select(c => ProjectCode(c, now)).OrderBy(c => c.IsExpired).ThenBy(c => c.IsUpcoming).ThenBy(c => c.Code, StringComparer.Ordinal).ToArray()
        };
    }

    private static ResourceCodeEntry ProjectCode(ResourceCode code, DateTimeOffset now)
    {
        var today = LocalDate(now);
        var status = ResourceCodeStatus.ExpiryUnspecified;
        var label = "有效期未公布";
        if (code.ExpiresAt is { } end && now >= end || code.ExpiresOn is { } endDay && today > endDay)
        { status = ResourceCodeStatus.Expired; label = "已过期"; }
        else if (code.StartsAt is { } start && now < start || code.StartsOn is { } startDay && today < startDay)
        { status = ResourceCodeStatus.Upcoming; label = "尚未开始"; }
        else if (code.StartsOn == today)
        { status = ResourceCodeStatus.TimeUnconfirmed; label = "今日开放 · 时间未公布"; }
        else if (code.ExpiresOn == today)
        { status = ResourceCodeStatus.TimeUnconfirmed; label = "今日到期 · 时间未公布"; }
        else if (code.ExpiresAt is not null || code.ExpiresOn is not null)
        { status = ResourceCodeStatus.WithinPublishedPeriod; label = "尚未到期"; }
        return new()
        {
            Code = code.Code, Reward = code.Reward, Version = code.Version, Region = code.Region == "CN" ? "国服" : code.Region,
            PlatformText = code.Platforms.Count == 0 ? "平台未注明" : string.Join(" / ", code.Platforms.Select(p => p.Equals("all", StringComparison.OrdinalIgnoreCase) ? "全平台" : p)),
            DateText = CodeDateText(code), Status = status, StatusText = label, Source = code.Source
        };
    }

    private static (ResourceScheduleStatus, string) ScheduleStatus(DateTimeOffset? start, DateTimeOffset? end,
        DateOnly? startDay, DateOnly? endDay, DateTimeOffset now, bool isVersionPoint = false)
    {
        var today = LocalDate(now);
        if (end is { } e && now >= e || endDay is { } d && today > d) return (ResourceScheduleStatus.Ended, "已结束");
        if (start is { } s && now < s || startDay is { } a && today < a) return (ResourceScheduleStatus.Upcoming, "即将开始");
        if (isVersionPoint && end is null && endDay is null)
            return start is not null
                ? (ResourceScheduleStatus.Reached, "已到公布时间")
                : (startDay == today ? ResourceScheduleStatus.TimeUnconfirmed : ResourceScheduleStatus.Reached, "已到公布日期 · 具体时间未公布");
        if (startDay == today && endDay == today) return (ResourceScheduleStatus.TimeUnconfirmed, "今日活动 · 时间未公布");
        if (startDay == today) return (ResourceScheduleStatus.TimeUnconfirmed, "今日开始 · 时间未公布");
        if (endDay == today) return (ResourceScheduleStatus.TimeUnconfirmed, "今日结束 · 时间未公布");
        if (end is null && endDay is null) return (ResourceScheduleStatus.TimeUnconfirmed, "结束时间未公布");
        return (ResourceScheduleStatus.Ongoing, "进行中");
    }

    private static string CodeDateText(ResourceCode code)
    {
        if (code.StartsAt is null && code.StartsOn is null)
            return code.ExpiresOn is { } date ? "截止 " + Format(date) + "（具体时间未公布）"
                : code.ExpiresAt is { } end ? "截止 " + Format(end) : "有效期未公布";
        return RangeText(code.StartsAt, code.ExpiresAt, code.StartsOn, code.ExpiresOn);
    }
    private static string RangeText(DateTimeOffset? start, DateTimeOffset? end, DateOnly? startDay, DateOnly? endDay)
    {
        if (startDay is not null || endDay is not null)
            return Format(Date(startDay, start)) + " — " + Format(Date(endDay, end));
        return (start is { } a ? Format(a) : "未公布") + " — " + (end is { } b ? Format(b) : "未公布");
    }
    private static string KindText(string kind) => kind switch
    { "event" => "活动", "maintenance" => "维护", "livestream" => "前瞻", "version" => "版本更新", _ => "日程" };
    private static DateOnly LocalDate(DateTimeOffset value) => DateOnly.FromDateTime(value.ToOffset(BeijingOffset).DateTime);
    private static DateOnly? Date(DateOnly? day, DateTimeOffset? time) => day ?? (time is { } value ? LocalDate(value) : null);
    private static DateOnly? LastCalendarDate(DateOnly? day, DateTimeOffset? time)
    {
        if (day is not null || time is null) return day;
        var local = time.Value.ToOffset(BeijingOffset);
        var date = DateOnly.FromDateTime(local.DateTime);
        // Exact intervals are [start, end); midnight belongs only to the following day.
        return local.TimeOfDay == TimeSpan.Zero ? date.AddDays(-1) : date;
    }
    private static string Format(DateOnly? value) => value?.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) ?? "未公布";
    private static string Format(DateTimeOffset value) => value.ToOffset(BeijingOffset).ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
    private static bool RegionMatches(string value, string region) => value.Equals(region, StringComparison.OrdinalIgnoreCase) || value.Equals("all", StringComparison.OrdinalIgnoreCase);
    private static bool PlatformMatches(IReadOnlyList<string> platforms, string platform) => platforms.Count == 0 || platforms.Any(p => p.Equals(platform, StringComparison.OrdinalIgnoreCase) || p.Equals("all", StringComparison.OrdinalIgnoreCase) || platform.Equals("PC", StringComparison.OrdinalIgnoreCase) && p.Equals("Windows", StringComparison.OrdinalIgnoreCase));
}
