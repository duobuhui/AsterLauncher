using AsterLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AsterLauncher.App.Views;

public sealed class GachaScatterControl : UserControl
{
    private readonly StackPanel _body = new() { Spacing = 6 };
    private readonly TextBlock _title = new() { FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Canvas _plot = new() { Height = 92 };
    private IReadOnlyList<GachaStatisticPoint> _points = [];
    private int _maximum = 90;
    private double? _average;
    private int? _selectedPosition;

    public GachaScatterControl()
    {
        _body.Children.Add(_title);
        _body.Children.Add(_plot);
        _body.Children.Add(_detail);
        Content = new Border { Child = _body, Padding = new(14), CornerRadius = new(12),
            Background = (Brush)Application.Current.Resources["HeroGlassBrush"],
            BorderBrush = (Brush)Application.Current.Resources["GlassEdgeBrush"], BorderThickness = new(1) };
        SizeChanged += (_, _) => Draw();
    }

    public void SetData(string title, IReadOnlyList<GachaStatisticPoint> points, int maximum, double? average)
    {
        _title.Text = title;
        _points = points;
        _maximum = Math.Max(2, maximum);
        _average = average;
        _selectedPosition = null;
        Draw();
    }

    private void Draw()
    {
        _plot.Children.Clear();
        var width = Math.Max(180, ActualWidth - 40);
        const double left = 9;
        const double baseline = 62;
        const double dotCenter = 48;
        double X(double value) => left + Math.Clamp((value - 1) / (_maximum - 1), 0, 1) * (width - 18);
        var muted = (Brush)Application.Current.Resources["GlassMutedTextBrush"];
        var accent = (Brush)Application.Current.Resources["ThemeAccentBrush"];
        _plot.Children.Add(new Line { X1 = left, X2 = width - 9, Y1 = baseline, Y2 = baseline,
            Stroke = muted, StrokeThickness = 1 });
        foreach (var tick in new[] { 1, Math.Max(2, _maximum / 4), _maximum / 2, _maximum * 3 / 4, _maximum }.Distinct())
        {
            var text = new TextBlock { Text = tick.ToString(), FontSize = 12, Foreground = muted };
            Canvas.SetLeft(text, Math.Clamp(X(tick) - (tick >= 100 ? 11 : tick >= 10 ? 7 : 3), 0, width - 22));
            Canvas.SetTop(text, baseline + 6);
            _plot.Children.Add(text);
        }
        if (_average is double average)
        {
            _plot.Children.Add(new Line { X1 = X(average), X2 = X(average), Y1 = 20, Y2 = baseline,
                Stroke = accent, StrokeThickness = 1, StrokeDashArray = [3, 3] });
            var label = new TextBlock { Text = $"均值 {average:0.0}", FontSize = 12, Foreground = muted };
            Canvas.SetLeft(label, Math.Clamp(X(average) - 28, 0, width - 75));
            Canvas.SetTop(label, 0);
            _plot.Children.Add(label);
        }

        // All outcomes use the same Y position. Exact duplicates (and clipped overflow)
        // share one hit target; the selected details retain every underlying record.
        var groups = _points.GroupBy(p => Math.Clamp(p.Pulls, 1, _maximum)).OrderBy(g => g.Key).ToArray();
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            var records = group.ToArray();
            var incomplete = records.Any(p => !p.Complete);
            var x = X(group.Key);
            var hitLeft = index == 0 ? x - 7 : Math.Max(x - 7, (X(groups[index - 1].Key) + x) / 2);
            var hitRight = index == groups.Length - 1 ? x + 7 : Math.Min(x + 7, (X(groups[index + 1].Key) + x) / 2);
            var glyph = new Ellipse { Width = 7, Height = 7, Fill = incomplete ? null : accent,
                Stroke = accent, StrokeThickness = incomplete ? 1.2 : 0, IsHitTestVisible = false };
            Canvas.SetLeft(glyph, x - 3.5);
            Canvas.SetTop(glyph, dotCenter - 3.5);
            _plot.Children.Add(glyph);
            // Visual size stays fixed; hit regions stop at the midpoint to each neighbor.
            var dot = new Button
            {
                Width = hitRight - hitLeft, Height = 24, MinHeight = 0, MinWidth = 0, Padding = new(0),
                CornerRadius = new(7), BorderThickness = new(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            var label = $"出货点：{group.Key} 抽 · {records.Length} 条记录";
            var info = string.Join(Environment.NewLine, records.Select(Describe));
            ToolTipService.SetToolTip(dot, info);
            AutomationProperties.SetName(dot, label);
            AutomationProperties.SetHelpText(dot, info);
            dot.Click += (_, _) => { _selectedPosition = group.Key; _detail.Text = info; };
            Canvas.SetLeft(dot, hitLeft);
            Canvas.SetTop(dot, dotCenter - 12);
            _plot.Children.Add(dot);
        }
        var selected = _selectedPosition is int position
            ? _points.Where(p => Math.Clamp(p.Pulls, 1, _maximum) == position).ToArray() : [];
        if (selected.Length > 0)
        {
            _detail.Text = string.Join(Environment.NewLine, selected.Select(Describe));
            return;
        }
        var complete = _points.Count(p => p.Complete);
        var overflow = _points.Count(p => p.Pulls > _maximum);
        _detail.Text = _points.Count == 0 ? "暂无出货记录"
            : $"{_points.Count} 次出货 · {complete} 个完整间隔。空心点含历史不完整的记录，不完整间隔不计入均值。"
                + (overflow > 0 ? $" {overflow} 个间隔超过横轴上限，集中显示在右端。" : "");
    }

    private static string Describe(GachaStatisticPoint point) =>
        $"{point.Name} · {(point.Complete ? "" : "至少 ")}{point.Pulls} 抽 · {point.Time} · "
        + (point.UpStatus switch
        {
            GachaUpStatus.Featured => "UP",
            GachaUpStatus.NonFeatured => "非 UP",
            GachaUpStatus.Unknown => "UP 未确认",
            _ => "此池不区分 UP"
        });
}