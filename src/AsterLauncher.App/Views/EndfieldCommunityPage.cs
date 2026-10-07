using AsterLauncher.App.Services;
using AsterLauncher.App.ViewModels;
using AsterLauncher.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;

namespace AsterLauncher.App.Views;

/// <summary>Native month calendar and redemption cards for the selected Endfield game.</summary>
public sealed class EndfieldCommunityPage : Page
{
    private readonly EndfieldCommunityViewModel _viewModel;
    private readonly IResourceCatalogProvider _resources;
    private readonly LauncherViewModel _launcher;
    private readonly Grid _layout = new() { ColumnSpacing = 16, RowSpacing = 16 };
    private readonly Grid _calendar = new() { ColumnSpacing = 4, RowSpacing = 4 };
    private readonly StackPanel _timeline = new() { Spacing = 4 };
    private readonly StackPanel _agenda = new() { Spacing = 8 };
    private readonly StackPanel _codes = new() { Spacing = 10 };
    private readonly StackPanel _coming = new() { Spacing = 8 };
    private readonly StackPanel _side = new() { Spacing = 16 };
    private readonly TextBlock _month = new() { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _selection = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _updated = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _refresh;
    private readonly List<ToggleButton> _filters = [];
    private readonly List<ToggleButton> _views = [];
    private bool _monthView;
    private readonly Microsoft.UI.Xaml.DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _attached;
    private bool _rendering;
    private bool _expiredExpanded;

    public EndfieldCommunityPage(EndfieldCommunityViewModel viewModel, IResourceCatalogProvider resources, LauncherViewModel launcher)
    {
        _viewModel = viewModel; _resources = resources; _launcher = launcher;
        var body = new StackPanel { Spacing = 16, Padding = new(24, 8, 24, 24) };
        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var context = new StackPanel { Spacing = 4 };
        context.Children.Add(_summary); context.Children.Add(_updated);
        top.Children.Add(context);
        _refresh = ActionButton("更新资料", async () => await _viewModel.RefreshAsync(), "SecondaryButtonStyle");
        AutomationProperties.SetName(_refresh, "更新终末地日历资料");
        Grid.SetColumn(_refresh, 1); top.Children.Add(_refresh);
        var statusCard = Surface(top); statusCard.Padding = new(12); body.Children.Add(statusCard);
        body.Children.Add(_message);

        var calendarBody = new StackPanel { Spacing = 12 };
        var monthBar = new Grid { ColumnSpacing = 8 };
        monthBar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        monthBar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        monthBar.Children.Add(_month);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        navigation.Children.Add(IconButton("", "日历上个月", () => _viewModel.MoveMonth(-1)));
        navigation.Children.Add(ActionButton("今天", () => { _viewModel.Today(); return Task.CompletedTask; }, "SubtleButtonStyle"));
        navigation.Children.Add(IconButton("", "日历下个月", () => _viewModel.MoveMonth(1)));
        Grid.SetColumn(navigation, 1); monthBar.Children.Add(navigation);
        calendarBody.Children.Add(monthBar);
        var viewSwitch = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (label, monthView) in new[] { ("时间轴", false), ("月历", true) })
        {
            var choice = new ToggleButton { Content = label, Tag = monthView, FontSize = 13, Padding = new(12, 4, 12, 4), MinHeight = 30 };
            AutomationProperties.SetName(choice, "日程视图：" + label);
            choice.Click += (_, _) => { if (!_rendering) { _monthView = monthView; Render(); } };
            _views.Add(choice); viewSwitch.Children.Add(choice);
        }

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (key, label) in new[] { ("all", "全部"), ("pool", "卡池"), ("event", "活动"), ("updates", "版本 / 维护"), ("livestream", "前瞻") })
        {
            var button = new ToggleButton { Content = label, Tag = key, MinWidth = 0, Padding = new(10, 4, 10, 4), FontSize = 12, MinHeight = 30 };
            AutomationProperties.SetName(button, "日历筛选：" + label);
            button.Click += (_, _) => { if (!_rendering) _viewModel.SetFilter(key); };
            _filters.Add(button); filters.Children.Add(button);
        }
        viewSwitch.Children.Add(new Border { Width = 1, Height = 20, Background = Brush("SubtleStrokeBrush"), Margin = new(4, 0, 4, 0) });
        viewSwitch.Children.Add(filters);
        calendarBody.Children.Add(new ScrollViewer { Content = viewSwitch, HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled });
        for (var i = 0; i < 7; i++) _calendar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });

        calendarBody.Children.Add(_timeline);
        calendarBody.Children.Add(_calendar);
        calendarBody.Children.Add(Caption("‹ › 表示跨月延续。仅公布日期的边界不代表确切开放或结束时刻。"));
        _selection.Style = GetStyle("SectionHeadingStyle");
        calendarBody.Children.Add(_selection);
        calendarBody.Children.Add(_agenda);
        var card = Surface(calendarBody);
        _layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        _layout.ColumnDefinitions.Add(new() { Width = new(280) });
        _layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _layout.Children.Add(card);

        var codesBody = new StackPanel { Spacing = 12 };
        codesBody.Children.Add(Heading("兑换码")); codesBody.Children.Add(Caption("国服 · 适用 PC 的公开兑换码"));
        codesBody.Children.Add(Caption("游戏内：设置 → 平台与账户 → 礼包兑换"));
        codesBody.Children.Add(_codes); _side.Children.Add(Surface(codesBody));
        var comingBody = new StackPanel { Spacing = 12 };
        comingBody.Children.Add(Heading("接下来")); comingBody.Children.Add(_coming);
        _side.Children.Add(Surface(comingBody));
        _layout.Children.Add(_side);
        body.Children.Add(_layout);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetName(this, "终末地版本日历和兑换码");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => ArrangeColumns();
        ActualThemeChanged += (_, _) => Render();
        _clock.Tick += (_, _) => _viewModel.Reload();
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_attached) return;
        _attached = true; _resources.Updated += ResourcesUpdated; _viewModel.PropertyChanged += ViewModelChanged;
        _launcher.ThemeChanged += ThemeChanged;
        _viewModel.Reload(); _clock.Start();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _attached = false; _clock.Stop(); _resources.Updated -= ResourcesUpdated;
        _viewModel.PropertyChanged -= ViewModelChanged; _launcher.ThemeChanged -= ThemeChanged;
    }
    private void ThemeChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Render);
    private void ResourcesUpdated(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (_attached) _viewModel.Reload(); });
    private void ViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Render();
    private void ArrangeColumns()
    {
        var wide = ActualWidth >= 860;
        _layout.ColumnDefinitions[1].Width = wide ? new(280) : new(0);
        Grid.SetColumn(_side, wide ? 1 : 0); Grid.SetRow(_side, wide ? 0 : 1);
    }
    private void Render()
    {
        _rendering = true;
        try
        {
            ArrangeColumns();
            _month.Text = _viewModel.MonthText; _month.Foreground = Brush("GlassTextBrush");
            _summary.Text = $"终末地 · 国服    本月 {_viewModel.MonthEntries.Count} 项安排";
            _summary.Foreground = Brush("GlassTextBrush"); _summary.FontSize = 14;
            _updated.Text = _viewModel.UpdatedText; _updated.Style = GetStyle("CaptionTextStyle");
            _message.Text = _viewModel.Message; _message.Style = GetStyle("CaptionTextStyle");
            _message.Visibility = string.IsNullOrEmpty(_message.Text) ? Visibility.Collapsed : Visibility.Visible;
            _refresh.IsEnabled = !_viewModel.IsRefreshing;
            _selection.Text = _viewModel.SelectionText;
            foreach (var filter in _filters) filter.IsChecked = (string)filter.Tag == _viewModel.Filter;
            foreach (var view in _views) view.IsChecked = (bool)view.Tag == _monthView;
            _calendar.Visibility = _selection.Visibility = _agenda.Visibility = _monthView ? Visibility.Visible : Visibility.Collapsed;
            _timeline.Visibility = _monthView ? Visibility.Collapsed : Visibility.Visible;
            if (_monthView) { DrawDays(); DrawAgenda(); } else DrawTimeline();
            DrawCodes(); DrawComing();
        }
        finally { _rendering = false; }
    }
    private void DrawTimeline()
    {
        _timeline.Children.Clear();
        var start = _viewModel.Month;
        var end = start.AddMonths(1).AddDays(-1);
        var days = end.Day;
        var today = _viewModel.Snapshot.Today;
        var header = TimelineColumns();
        var label = Caption("卡池 / 活动");
        label.VerticalAlignment = VerticalAlignment.Bottom;
        header.Children.Add(label);
        var axis = DayColumns(days);
        axis.Height = 40;
        for (var day = 1; day <= days; day += 7)
        {
            var tick = Caption($"{start.Month}/{day}");
            tick.FontSize = 12; tick.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(tick, day - 1); Grid.SetColumnSpan(tick, Math.Min(6, days - day + 1));
            axis.Children.Add(tick);
        }
        if (today >= start && today <= end)
        {
            var marker = new TextBlock { Text = "今", FontSize = 12, Foreground = Brush("PageTitleBrush"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(marker, today.Day - 1); axis.Children.Add(marker);
        }
        Grid.SetColumn(axis, 1); header.Children.Add(axis);
        _timeline.Children.Add(header);

        var entries = _viewModel.MonthEntries.OrderBy(e => e.Kind == "pool" ? 0 : 1)
            .ThenBy(e => e.IsExpired).ThenBy(e => e.StartDate).ToArray();
        if (entries.Length == 0) { _timeline.Children.Add(Caption("本月暂无已公布的安排。")); return; }
        foreach (var entry in entries)
        {
            var segment = ResourceTimelineLayout.Project(entry, start, end);
            if (segment is null) continue;
            var row = TimelineColumns(); row.Padding = new(0, 7, 0, 7);
            var title = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = TimelineTitle(entry), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush("GlassTextBrush"), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
            title.Children.Add(Caption(entry.KindText + " · " + entry.StatusText));
            ToolTipService.SetToolTip(title, entry.Title + (entry.Detail.Length > 0 ? "\n" + entry.Detail : ""));
            var details = new Button { Content = title, Padding = new(0), Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, MinHeight = 32 };
            var flyoutContent = EventCard(entry, true); flyoutContent.MaxWidth = 420;
            details.Flyout = new Flyout { Content = flyoutContent };
            AutomationProperties.SetName(details, "查看安排：" + entry.Title);
            ToolTipService.SetToolTip(details, "查看安排详情");
            row.Children.Add(details);

            var span = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            var plot = DayColumns(days); plot.Height = 24;
            var rail = new Border { Height = 1, Background = Brush("SubtleStrokeBrush"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumnSpan(rail, days); plot.Children.Add(rail);
            for (var day = 0; day < days; day += 7)
            {
                var guide = new Border { Width = 1, Background = Brush("SubtleStrokeBrush"), HorizontalAlignment = HorizontalAlignment.Left };
                Grid.SetColumn(guide, day); plot.Children.Add(guide);
            }
            if (segment.IsPoint)
            {
                var point = new Ellipse { Width = 9, Height = 9, Fill = Brush("ThemeAccentBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(point, segment.StartOffsetDays); plot.Children.Add(point);
                ToolTipService.SetToolTip(point, "仅公布开始日期；结束时间未公布");
            }
            else
            {
                var bar = new Grid { Background = Brush("ThemeAccentSoftBrush"), BorderBrush = Brush("ThemeAccentBrush"),
                    BorderThickness = new(1), CornerRadius = new(5), Height = 18, Margin = new(1, 0, 1, 0),
                    Opacity = entry.IsExpired ? 0.45 : 1 };
                if (segment.ContinuesBefore) bar.Children.Add(new TextBlock { Text = "‹", FontSize = 13,
                    Foreground = Brush("PageTitleBrush"), Margin = new(3, -2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
                if (segment.ContinuesAfter) bar.Children.Add(new TextBlock { Text = "›", FontSize = 13,
                    Foreground = Brush("PageTitleBrush"), Margin = new(0, -2, 3, 0), HorizontalAlignment = HorizontalAlignment.Right });
                Grid.SetColumn(bar, segment.StartOffsetDays); Grid.SetColumnSpan(bar, segment.SpanDays); plot.Children.Add(bar);
                ToolTipService.SetToolTip(bar, entry.Title + "\n" + entry.DateText);
            }
            if (today >= start && today <= end)
            {
                var now = new Border { Width = 2, Background = Brush("ThemeAccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
                Grid.SetColumn(now, today.Day - 1); plot.Children.Add(now);
            }
            span.Children.Add(plot);
            span.Children.Add(Caption(entry.DateText));
            Grid.SetColumn(span, 1); row.Children.Add(span);
            var source = IconButton("", "官方公告：" + entry.Title, async () => await OpenSourceAsync(entry.Source));
            source.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(source, 2); row.Children.Add(source);
            AutomationProperties.SetName(row, entry.Title + "，" + entry.DateText + "，" + entry.StatusText);
            _timeline.Children.Add(row);
            _timeline.Children.Add(new Border { Height = 1, Background = Brush("SubtleStrokeBrush"), Opacity = 0.5 });
        }
    }
    private static string TimelineTitle(ResourceCalendarEntry entry) =>
        entry.Kind == "pool" && entry.Detail.Length > 0 && !entry.Detail.StartsWith("专武", StringComparison.Ordinal)
            ? entry.Detail.Split(" · ", StringSplitOptions.None)[0] + " · " + entry.Title : entry.Title;

    private static Grid TimelineColumns()
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new() { Width = new(150) });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new(32) });
        return grid;
    }
    private static Grid DayColumns(int days)
    {
        var grid = new Grid();
        for (var i = 0; i < days; i++) grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        return grid;
    }
    private void DrawDays()
    {
        var focus = _calendar.Children.OfType<Button>().FirstOrDefault(b => b.FocusState != FocusState.Unfocused)?.FocusState;
        _calendar.Children.Clear();
        _calendar.RowDefinitions.Clear();
        var offset = ((int)_viewModel.Month.DayOfWeek + 6) % 7;
        var weeks = (offset + DateTime.DaysInMonth(_viewModel.Month.Year, _viewModel.Month.Month) + 6) / 7;
        _calendar.RowDefinitions.Add(new() { Height = new(24) });
        for (var i = 0; i < weeks; i++) _calendar.RowDefinitions.Add(new() { Height = new(42) });
        var labels = new[] { "一", "二", "三", "四", "五", "六", "日" };
        for (var i = 0; i < labels.Length; i++)
        {
            var label = Caption(labels[i]); label.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(label, i); _calendar.Children.Add(label);
        }
        var first = _viewModel.Month.AddDays(-offset);
        for (var i = 0; i < weeks * 7; i++)
        {
            var date = first.AddDays(i); var entries = _viewModel.ForDay(date);
            var today = date == _viewModel.Snapshot.Today; var selected = date == _viewModel.SelectedDate;
            var stack = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = today ? $"{date.Day} · 今" : date.Day.ToString(),
                FontSize = 13, FontWeight = today || selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = Brush(selected || today ? "PageTitleBrush" : "GlassTextBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 3, Height = 10 };
            foreach (var kind in entries.Select(item => item.Kind).Distinct().Take(4))
                dots.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = Brush("ThemeAccentBrush"),
                    Opacity = kind == "pool" ? 0.65 : 1, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(dots);
            var button = new Button { Content = stack, Padding = new(2), HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch, MinWidth = 0, MinHeight = 0, CornerRadius = new(10),
                Background = selected ? Brush("ThemeAccentSoftBrush") : new SolidColorBrush(Colors.Transparent),
                BorderBrush = Brush(selected || today ? "ThemeStrokeBrush" : "SubtleStrokeBrush"),
                BorderThickness = new(selected || today ? 1.5 : 0),
                Opacity = date.Month == _viewModel.Month.Month ? 1 : 0.45 };
            var name = $"{date:yyyy年M月d日}，{entries.Count}项安排";
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, entries.Count > 0 ? string.Join("\n", entries.Take(4).Select(item => item.Title)) : $"{date:M月d日} · 暂无安排");
            button.Click += (_, _) => _viewModel.Select(date);
            Grid.SetColumn(button, i % 7); Grid.SetRow(button, i / 7 + 1); _calendar.Children.Add(button);
            if (selected && focus is { } state) button.Focus(state);
        }
    }
    private void DrawAgenda()
    {
        _agenda.Children.Clear();
        if (_viewModel.SelectedEntries.Count == 0)
        {
            _agenda.Children.Add(Caption("这一天暂无已公布的安排。"));
            return;
        }
        foreach (var entry in _viewModel.SelectedEntries) _agenda.Children.Add(EventCard(entry));
    }
    private FrameworkElement EventCard(ResourceCalendarEntry entry, bool compact = false)
    {
        var content = new StackPanel { Spacing = 6 };
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        meta.Children.Add(Badge(entry.KindText)); meta.Children.Add(Caption(entry.StatusText));
        if (!string.IsNullOrWhiteSpace(entry.Version)) meta.Children.Add(Caption(entry.Version));
        content.Children.Add(meta);
        content.Children.Add(new TextBlock { Text = entry.Title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("GlassTextBrush"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(Caption(entry.DateText));
        if (!string.IsNullOrEmpty(entry.Detail))
            content.Children.Add(new TextBlock { Text = entry.Detail, Style = GetStyle("CaptionTextStyle"), TextWrapping = TextWrapping.Wrap });
        var source = ActionButton("官方公告", async () => await OpenSourceAsync(entry.Source), "SubtleButtonStyle");
        source.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(source, "官方公告：" + entry.Title);
        content.Children.Add(source);
        return compact ? content : new Border { Child = content, Style = GetStyle("InsetSurfaceBorderStyle") };
    }
    private void DrawComing()
    {
        _coming.Children.Clear();
        var upcoming = _viewModel.Snapshot.Entries.Where(e => e.IsUpcoming)
            .OrderBy(e => e.StartDate).ThenBy(e => e.Kind == "pool" ? 1 : 0).Take(3).ToArray();
        if (upcoming.Length == 0) _coming.Children.Add(Caption("暂无已公布的新安排。"));
        foreach (var entry in upcoming)
        {
            var item = new StackPanel { Spacing = 4 };
            item.Children.Add(Caption(entry.StartDate.ToString("M 月 d 日") + " · " + entry.KindText));
            item.Children.Add(new TextBlock { Text = TimelineTitle(entry), FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brush("GlassTextBrush"), TextWrapping = TextWrapping.Wrap });
            var locate = new Button { Content = "查看详情", Style = GetStyle("SubtleButtonStyle") };
            var content = EventCard(entry, true); content.MaxWidth = 420;
            locate.Flyout = new Flyout { Content = content };
            locate.HorizontalAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetName(locate, "查看日程：" + entry.Title);
            item.Children.Add(locate);
            _coming.Children.Add(item);
        }
    }
    private void DrawCodes()
    {
        _codes.Children.Clear();
        if (_viewModel.AvailableCodes.Count == 0)
        {
            var empty = new StackPanel { Spacing = 10, Padding = new(0, 6, 0, 6) };
            empty.Children.Add(new FontIcon { Glyph = "", FontSize = 24, Foreground = Brush("ThemeAccentBrush"), HorizontalAlignment = HorizontalAlignment.Left });
            empty.Children.Add(new TextBlock { Text = _viewModel.ExpiredCodes.Count > 0 ? "暂无未过期的公开兑换码" : "暂无已核实的兑换码", FontSize = 14, Foreground = Brush("GlassTextBrush"), TextWrapping = TextWrapping.Wrap });
            empty.Children.Add(Caption("确认官方来源后，兑换码与奖励会随资料更新出现在这里。"));
            _codes.Children.Add(empty);
        }
        foreach (var code in _viewModel.AvailableCodes) _codes.Children.Add(CodeCard(code));
        if (_viewModel.ExpiredCodes.Count > 0)
        {
            var expired = new StackPanel { Spacing = 10 };
            foreach (var code in _viewModel.ExpiredCodes) expired.Children.Add(CodeCard(code));
            var expander = new Expander { Header = $"已过期 · {_viewModel.ExpiredCodes.Count}", Content = expired,
                HorizontalAlignment = HorizontalAlignment.Stretch, IsExpanded = _expiredExpanded };
            expander.Expanding += (_, _) => _expiredExpanded = true;
            expander.Collapsed += (_, _) => _expiredExpanded = false;
            _codes.Children.Add(expander);
        }
    }
    private FrameworkElement CodeCard(ResourceCodeEntry code)
    {
        var content = new StackPanel { Spacing = 7 };
        content.Children.Add(new TextBlock { Text = code.Code, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("GlassTextBrush"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        content.Children.Add(new TextBlock { Text = code.Reward, Foreground = Brush("GlassTextBrush"), FontSize = 13, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(Caption(code.StatusText == code.DateText ? code.DateText : code.StatusText + " · " + code.DateText));
        content.Children.Add(Caption(code.Region + " · " + code.PlatformText));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var copy = ActionButton("复制", () =>
        {
            try { var data = new DataPackage(); data.SetText(code.Code); Clipboard.SetContent(data); ShowMessage("已复制兑换码：" + code.Code); }
            catch (System.Runtime.InteropServices.COMException) { ShowMessage("剪贴板暂时不可用，请手动选择并复制兑换码。"); }
            return Task.CompletedTask;
        }, "SubtleButtonStyle");
        copy.IsEnabled = code.CanCopy; AutomationProperties.SetName(copy, "复制兑换码：" + code.Code);
        actions.Children.Add(copy);
        actions.Children.Add(ActionButton("来源", async () => await OpenSourceAsync(code.Source), "SubtleButtonStyle"));
        content.Children.Add(actions);
        return new Border { Child = content, Style = GetStyle("InsetSurfaceBorderStyle") };
    }
    private async Task OpenSourceAsync(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0) return;
        if (!await Windows.System.Launcher.LaunchUriAsync(uri)) ShowMessage("无法打开浏览器，请稍后重试。");
    }
    private void ShowMessage(string text) { _message.Text = text; _message.Visibility = Visibility.Visible; }
    private static Style GetStyle(string key) => (Style)Application.Current.Resources[key];
    private static Brush Brush(string key) => AppearanceBrushes.Get(key);
    private static Border Surface(UIElement child) => new() { Child = child, Style = GetStyle("CommunitySurfaceStyle") };
    private static TextBlock Heading(string text) => new() { Text = text, Style = GetStyle("SectionHeadingStyle") };
    private static TextBlock Caption(string text) => new() { Text = text, Style = GetStyle("CaptionTextStyle"), TextWrapping = TextWrapping.Wrap };
    private static Border Badge(string text) => new() { Child = new TextBlock { Text = text, Style = GetStyle("CompactBadgeTextStyle") }, Style = GetStyle("CompactBadgeStyle") };
    private static Button ActionButton(string text, Func<Task> action, string style)
    {
        var button = new Button { Content = text, Style = GetStyle(style) };
        button.Click += async (_, _) => await action();
        return button;
    }
    private static Button IconButton(string glyph, string name, Action action)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Style = GetStyle("CompactIconButtonStyle") };
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => action(); return button;
    }
}
