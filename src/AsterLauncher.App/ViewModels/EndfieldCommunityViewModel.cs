using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using System.Globalization;

namespace AsterLauncher.App.ViewModels;

/// <summary>Month selection and public feed state; no game/account operations.</summary>
public sealed class EndfieldCommunityViewModel(ResourceUpdateService resources, TimeProvider? clock = null) : ObservableObject
{
    public ResourceScheduleSnapshot Snapshot { get; private set; } = new();
    public DateOnly Month { get; private set; } = new(2026, 1, 1);
    public DateOnly SelectedDate { get; private set; }
    public string Filter { get; private set; } = "all";
    public bool IsRefreshing { get; private set; }
    public string Message { get; private set; } = "";
    private bool _initialized;

    public string MonthText => Month.ToString("yyyy 年 M 月", CultureInfo.InvariantCulture);
    public string SelectionText => SelectedDate.ToString("M 月 d 日", CultureInfo.InvariantCulture)
        + " · " + new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)SelectedDate.DayOfWeek];
    public string UpdatedText => Snapshot.PublishedAt is { } date
        ? $"资料更新 {date.ToOffset(TimeSpan.FromHours(8)):yyyy/MM/dd} · 北京时间"
        : "尚未取得公开资料 · 北京时间";
    public IReadOnlyList<ResourceCalendarEntry> MonthEntries => Filtered()
        .Where(e => e.Overlaps(Month, Month.AddMonths(1).AddDays(-1))).ToArray();
    public IReadOnlyList<ResourceCalendarEntry> SelectedEntries => ForDay(SelectedDate);
    public IReadOnlyList<ResourceCodeEntry> AvailableCodes => Snapshot.Codes.Where(c => !c.IsExpired).ToArray();
    public IReadOnlyList<ResourceCodeEntry> ExpiredCodes => Snapshot.Codes.Where(c => c.IsExpired).ToArray();

    public void Reload()
    {
        var next = ResourceScheduleProjector.Build(resources.Current, BuiltInGameIds.Endfield, (clock ?? TimeProvider.System).GetUtcNow());
        if (_initialized && Snapshot.Today == next.Today && Snapshot.Revision == next.Revision
            && Snapshot.PublishedAt == next.PublishedAt && Snapshot.Entries.SequenceEqual(next.Entries) && Snapshot.Codes.SequenceEqual(next.Codes)) return;
        Snapshot = next;
        if (!_initialized)
        {
            SelectedDate = Snapshot.Today;
            Month = new(SelectedDate.Year, SelectedDate.Month, 1);
            _initialized = true;
        }
        OnPropertyChanged(nameof(Snapshot));
    }
    public void MoveMonth(int offset)
    {
        if (Month.Year <= 2000 && offset < 0 || Month.Year >= 2100 && offset > 0) return;
        Month = Month.AddMonths(offset);
        SelectedDate = new(Month.Year, Month.Month, Math.Min(SelectedDate.Day, DateTime.DaysInMonth(Month.Year, Month.Month)));
        OnPropertyChanged(nameof(Month));
    }
    public void Today()
    {
        Reload();
        SelectedDate = Snapshot.Today; Month = new(SelectedDate.Year, SelectedDate.Month, 1);
        OnPropertyChanged(nameof(Month));
    }
    public void Select(DateOnly date)
    {
        SelectedDate = date;
        if (date.Month != Month.Month || date.Year != Month.Year) Month = new(date.Year, date.Month, 1);
        OnPropertyChanged(nameof(SelectedDate));
    }
    public void SetFilter(string filter)
    {
        if (filter is not ("all" or "pool" or "event" or "updates" or "livestream")) return;
        Filter = filter; OnPropertyChanged(nameof(Filter));
    }
    public IReadOnlyList<ResourceCalendarEntry> ForDay(DateOnly date) => Filtered().Where(e => e.Overlaps(date, date))
        .OrderBy(e => e.StartDate == date ? 0 : 1).ThenBy(e => e.Kind == "pool" ? 1 : 0).ThenBy(e => e.Title, StringComparer.Ordinal).ToArray();
    private IEnumerable<ResourceCalendarEntry> Filtered() => Snapshot.Entries.Where(e => Filter == "all"
        || Filter == "updates" && e.Kind is "version" or "maintenance" || e.Kind == Filter);

    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true; Message = "正在更新公开资料…"; OnPropertyChanged(nameof(IsRefreshing));
        try
        {
            var changed = await resources.RefreshAsync();
            Reload(); Message = changed ? "资料已更新" : "已是最新资料";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException or UnauthorizedAccessException)
        { Message = "暂时无法更新，继续显示本地资料。"; }
        finally { IsRefreshing = false; OnPropertyChanged(nameof(IsRefreshing)); }
    }
}
