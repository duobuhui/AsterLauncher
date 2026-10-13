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
    private int _referenceMaximum = 90;
    private double? _average;
    private int? _selectedPosition;

    public GachaScatterControl()
    {
        _body.Children.Add(_title);
        _body.Children.Add(_plot);
        _body.Children.Add(_detail);
        Content = new Border { Child = _body, Padding = new(14), CornerRadius = new(12),
            Background = AsterLauncher.App.Services.AppearanceBrushes.Get("HeroGlassBrush"),
            BorderBrush = AsterLauncher.App.Services.AppearanceBrushes.Get("GlassEdgeBrush"), BorderThickness = new(1) };
        SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
    }

    public void SetData(string title, IReadOnlyList<GachaStatisticPoint> points, int maximum, double? average)
    {
        _title.Text = title;
        _points = points;
        _referenceMaximum = Math.Max(2, maximum);
        _maximum = GachaChartVisuals.ScatterAxisMaximum(_referenceMaximum, points.Select(point => point.Pulls));
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
        var muted = AsterLauncher.App.Services.AppearanceBrushes.Get("GlassMutedTextBrush");
        var accent = AsterLauncher.App.Services.AppearanceBrushes.Get("ThemeAccentBrush");
        _plot.Children.Add(new Line { X1 = left, X2 = width - 9, Y1 = baseline, Y2 = baseline,
            Stroke = muted, StrokeThickness = 1 });
        foreach (var tick in new[] { 1, Math.Max(2, _maximum / 4), _maximum / 2, (int)(_maximum * 0.75), _maximum }.Distinct())
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

        // All outcomes use the same Y position. Exact duplicates
        // share one hit target; the selected details retain every underlying record.
        var groups = _points.GroupBy(p => Math.Clamp(p.Pulls, 1, _maximum)).OrderBy(g => g.Key).ToArray();
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            var records = group.ToArray();
            var visual = GachaChartVisuals.Scatter(group.Key, _maximum, records.Length, records.Any(p => !p.Complete));
            var x = X(group.Key);
            var hitRadius = Math.Max(10, visual.Diameter / 2 + 3);
            var hitLeft = index == 0 ? x - hitRadius : Math.Max(x - hitRadius, (X(groups[index - 1].Key) + x) / 2);
            var hitRight = index == groups.Length - 1 ? x + hitRadius : Math.Min(x + hitRadius, (X(groups[index + 1].Key) + x) / 2);
            if (_selectedPosition == group.Key)
            {
                var selection = new Ellipse
                {
                    Width = visual.Diameter + 6, Height = visual.Diameter + 6, Stroke = accent,
                    StrokeThickness = 1, Opacity = 0.7, IsHitTestVisible = false
                };
                Canvas.SetLeft(selection, x - selection.Width / 2);
                Canvas.SetTop(selection, dotCenter - selection.Height / 2);
                _plot.Children.Add(selection);
            }
            var glyph = new Ellipse
            {
                Width = visual.Diameter, Height = visual.Diameter, Fill = visual.IsHollow ? null : accent,
                Stroke = accent, StrokeThickness = visual.StrokeThickness, Opacity = visual.Opacity, IsHitTestVisible = false
            };
            Canvas.SetLeft(glyph, x - visual.Diameter / 2);
            Canvas.SetTop(glyph, dotCenter - visual.Diameter / 2);
            _plot.Children.Add(glyph);
            // Recorded positions and aggregation stay exact; stronger points never add samples.
            // Hit regions still stop at the midpoint to each neighbor as visual sizes grow.
            var dot = new Button
            {
                Width = hitRight - hitLeft, Height = 24, MinHeight = 0, MinWidth = 0, Padding = new(0),
                CornerRadius = new(7), BorderThickness = new(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            var label = $"出货点：{group.Key} 抽 · {records.Length} 条记录";
            var info = $"{records.Length} 条记录 · {records.Count(record => record.Complete)} 个完整间隔\n" + string.Join(Environment.NewLine, records.Select(Describe));
            ToolTipService.SetToolTip(dot, info);
            AutomationProperties.SetName(dot, label);
            AutomationProperties.SetHelpText(dot, info);
            dot.Click += (_, _) => { _selectedPosition = group.Key; Draw(); };
            Canvas.SetLeft(dot, hitLeft);
            Canvas.SetTop(dot, dotCenter - 12);
            _plot.Children.Add(dot);
        }
        var selected = _selectedPosition is int position
            ? _points.Where(p => Math.Clamp(p.Pulls, 1, _maximum) == position).ToArray() : [];
        if (selected.Length > 0)
        {
            _detail.Text = $"{selected.Length} 条记录 · {selected.Count(record => record.Complete)} 个完整间隔\n" + string.Join(Environment.NewLine, selected.Select(Describe));
            return;
        }
        var complete = _points.Count(p => p.Complete);

        _detail.Text = _points.Count == 0 ? "暂无出货记录"
            : $"{_points.Count} 次出货 · {complete} 个完整间隔。抽数越高越醒目；同位置记录越多，点越大。空心点含历史不完整记录，不完整间隔不计入均值。"
                + (_maximum > _referenceMaximum ? $" 横轴已扩展至 {_maximum} 抽以显示全部记录。" : "");
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