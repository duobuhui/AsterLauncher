using AsterLauncher.App.ViewModels;
using AsterLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AsterLauncher.App.Views;

public sealed class PlayActivityPage : Page
{
    private readonly LauncherViewModel _launcher;
    private readonly StackPanel _body = new() { Spacing = 14, Padding = new Thickness(24,18,24,18) };
    private readonly ComboBox _period = new() { MinWidth = 130 };
    private readonly Grid _calendar = new() { ColumnSpacing = 3, RowSpacing = 3 };
    private readonly TextBlock _summary = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _selected = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _legacy = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _title = new() { FontSize = 25, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private bool _setting;

    public PlayActivityPage(LauncherViewModel launcher)
    {
        _launcher = launcher;
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _title.Text = "游玩记录";
        heading.Children.Add(_title);
        Grid.SetColumn(_period, 1); heading.Children.Add(_period);
        AutomationProperties.SetName(_period, "游玩记录年份");
        _body.Children.Add(heading);
        _body.Children.Add(_summary);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_calendar);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        legend.Children.Add(new TextBlock { Text = "少", FontSize = 12 });
        for (var i = 0; i < 5; i++) legend.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new(3), Background = Shade(i) });
        legend.Children.Add(new TextBlock { Text = "多 · 15 分钟 / 1 小时 / 3 小时", FontSize = 12 });
        panel.Children.Add(legend);
        panel.Children.Add(_selected);
        _body.Children.Add(new Border { Child = panel, Padding = new(16), CornerRadius = new(14),
            Background = Brush("HeroGlassBrush"), BorderBrush = Brush("GlassEdgeBrush"), BorderThickness = new(1) });
        _body.Children.Add(_legacy);
        Content = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _period.SelectionChanged += (_, _) => { if (!_setting) Render(); };
        SizeChanged += (_, _) => Render();
        Loaded += (_, _) => Populate();
    }
    private void Populate()
    {
        _setting = true; _period.Items.Clear(); _period.Items.Add("过去一年");
        var now = DateTimeOffset.Now;
        var earliest = _launcher.CurrentGame?.State.PlaySessions.Select(s => s.StartedAt.Year).DefaultIfEmpty(now.Year).Min() ?? now.Year;
        for (var year = now.Year; year >= Math.Max(2000, earliest); year--) _period.Items.Add(year.ToString());
        _period.SelectedIndex = 0; _setting = false; Render();
    }
    private void Render()
    {
        var game = _launcher.CurrentGame;
        if (game is null || _period.SelectedItem is null) return;
        var now = DateTimeOffset.Now; var today = DateOnly.FromDateTime(now.DateTime);
        var year = int.TryParse(_period.SelectedItem.ToString(), out var y) ? y : (int?)null;
        var from = year is int selectedYear ? new DateOnly(selectedYear, 1, 1) : today.AddDays(-364);
        var through = year is int endYear ? new DateOnly(endYear, 12, 31) : today;
        var activity = PlayActivity.Build(game.State, from, through, TimeZoneInfo.Local, now);
        _title.Text = game.DisplayName + " · 游玩记录";
        _summary.Text = $"这段时间 {Duration(activity.Seconds)} · 游玩 {activity.ActiveDays} 天 · 最长连续 {activity.LongestStreak} 天 · 总计 {Duration(game.State.TotalPlaySeconds)}";
        _legacy.Text = activity.UnmappedSeconds > 1
            ? $"旧版累计时长中有 {Duration(activity.UnmappedSeconds)} 没有对应的逐次记录，仍计入总时长，无法分配到热力表。颜色按每天已结束会话的实际时长显示。"
            : "颜色按每天已结束会话的实际时长显示；跨午夜的会话按本地日期分摊。点击格子查看当天记录。";
        _calendar.Children.Clear(); _calendar.RowDefinitions.Clear(); _calendar.ColumnDefinitions.Clear();
        var monday = from.AddDays(-(((int)from.DayOfWeek + 6) % 7));
        var weeks = (through.DayNumber - monday.DayNumber) / 7 + 1;
        var cell = Math.Clamp((Math.Max(350, ActualWidth - 80) - 28) / weeks - 3, 5, 13);
        _calendar.ColumnDefinitions.Add(new() { Width = new GridLength(25) });
        for (var i = 0; i < weeks; i++) _calendar.ColumnDefinitions.Add(new() { Width = new GridLength(cell) });
        _calendar.RowDefinitions.Add(new() { Height = new GridLength(20) });
        for (var i = 0; i < 7; i++) _calendar.RowDefinitions.Add(new() { Height = new GridLength(cell) });
        foreach (var (row,label) in new[] {(0,"一"),(2,"三"),(4,"五")})
        {
            var text = new TextBlock { Text = label, FontSize = 11 };
            Grid.SetRow(text, row+1); _calendar.Children.Add(text);
        }
        var previousMonth = -1;
        foreach (var day in activity.Days)
        {
            var column = (day.Date.DayNumber - monday.DayNumber) / 7 + 1;
            var row = ((int)day.Date.DayOfWeek + 6) % 7 + 1;
            if (day.Date.Month != previousMonth && column <= weeks - 2)
            {
                var text = new TextBlock { Text = day.Date.Month + "月", FontSize = 11 };
                Grid.SetColumn(text, column); Grid.SetColumnSpan(text, Math.Min(3, weeks-column+1));
                _calendar.Children.Add(text); previousMonth = day.Date.Month;
            }
            var level = day.Seconds <= 0 ? 0 : day.Seconds < 900 ? 1 : day.Seconds < 3600 ? 2 : day.Seconds < 10800 ? 3 : 4;
            var button = new Button { Width = cell, Height = cell, MinWidth = 0, MinHeight = 0, Padding = new(0),
                CornerRadius = new(2), Background = Shade(level), BorderThickness = new(0), IsEnabled = day.Date <= today };
            var detail = $"{day.Date:yyyy/MM/dd} · {Duration(day.Seconds)} · {day.Sessions} 次会话";
            AutomationProperties.SetName(button, detail); ToolTipService.SetToolTip(button, detail);
            button.Click += (_, _) => _selected.Text = detail;
            Grid.SetColumn(button, column); Grid.SetRow(button, row); _calendar.Children.Add(button);
        }
        _selected.Text = $"{from:yyyy/MM/dd} — {through:yyyy/MM/dd}";
    }
    private static Brush Brush(string name) => (Brush)Application.Current.Resources[name];
    private static Brush Shade(int level) => new SolidColorBrush(level switch
    {
        0 => Color.FromArgb(90,104,112,141), 1 => Color.FromArgb(255,78,68,124),
        2 => Color.FromArgb(255,113,88,181), 3 => Color.FromArgb(255,150,120,233), _ => Color.FromArgb(255,191,169,255)
    });
    internal static string Duration(double seconds) => seconds < 60 ? "0 分钟" : seconds < 3600 ? $"{seconds/60:0} 分钟" : $"{seconds/3600:0.0} 小时";
}