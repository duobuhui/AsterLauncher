using AsterLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AsterLauncher.App.Views;

public sealed class GachaScatterControl : UserControl
{
    private readonly StackPanel _body = new() { Spacing = 6 };
    private readonly TextBlock _title = new() { FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Canvas _plot = new() { Height = 155 };
    private IReadOnlyList<GachaStatisticPoint> _points = [];
    private int _maximum = 90;
    private double? _average;
    public GachaScatterControl()
    {
        _body.Children.Add(_title); _body.Children.Add(_plot); _body.Children.Add(_detail);
        Content = new Border { Child = _body, Padding = new(14), CornerRadius = new(12),
            Background = (Brush)Application.Current.Resources["HeroGlassBrush"],
            BorderBrush = (Brush)Application.Current.Resources["GlassEdgeBrush"], BorderThickness = new(1) };
        SizeChanged += (_, _) => Draw();
    }
    public void SetData(string title, IReadOnlyList<GachaStatisticPoint> points, int maximum, double? average)
    {
        _title.Text = title; _points = points; _maximum = Math.Max(2, maximum); _average = average; Draw();
    }
    private void Draw()
    {
        _plot.Children.Clear();
        var width = Math.Max(180, ActualWidth - 54);
        const double left = 9;
        const double baseline = 126;
        double X(double value) => left + Math.Clamp((value-1)/(_maximum-1),0,1) * (width-18);
        var muted = (Brush)Application.Current.Resources["GlassMutedTextBrush"];
        _plot.Children.Add(new Line { X1 = left, X2 = width-9, Y1 = baseline, Y2 = baseline, Stroke = muted, StrokeThickness = 1 });
        foreach (var tick in new[] {1, Math.Max(2,_maximum/4),_maximum/2,_maximum*3/4,_maximum}.Distinct())
        {
            var text = new TextBlock { Text = tick.ToString(), FontSize = 12, Foreground = muted };
            Canvas.SetLeft(text, X(tick)-6); Canvas.SetTop(text, baseline+6); _plot.Children.Add(text);
        }
        var occupied = new List<(double X, int Row)>();
        foreach (var point in _points)
        {
            var x = X(point.Pulls);
            var row = 0;
            while (row < 5 && occupied.Any(p => p.Row == row && Math.Abs(p.X-x)<13)) row++;
            occupied.Add((x,row));
            var fill = new SolidColorBrush(point.UpStatus == GachaUpStatus.Featured ? Color.FromArgb(255,200,173,255)
                : point.UpStatus == GachaUpStatus.NonFeatured ? Color.FromArgb(255,243,185,99) : Color.FromArgb(255,143,202,240));
            var dot = new Button { Width = 12, Height = 12, MinHeight = 0, MinWidth = 0, Padding = new(0),
                CornerRadius = new(6), BorderThickness = new(point.Complete ? 0 : 2), BorderBrush = fill,
                Background = point.Complete ? fill : new SolidColorBrush(Color.FromArgb(255,35,40,63)) };
            var info = $"{point.Name} · {(point.Complete ? "" : "本地 ≥")}{point.Pulls} 抽 · {point.Time} · {point.UpStatus switch {GachaUpStatus.Featured=>"UP",GachaUpStatus.NonFeatured=>"非 UP",GachaUpStatus.Unknown=>"UP 待校对",_=>"此池不区分 UP"}}";
            ToolTipService.SetToolTip(dot, info); AutomationProperties.SetName(dot,info);
            dot.Click += (_,_) => _detail.Text = info;
            Canvas.SetLeft(dot,x-6); Canvas.SetTop(dot,baseline-22-row*17); _plot.Children.Add(dot);
        }
        if (_average is double average)
        {
            _plot.Children.Add(new Line { X1=X(average),X2=X(average),Y1=17,Y2=baseline,Stroke=new SolidColorBrush(Color.FromArgb(255,232,227,255)),StrokeThickness=1.5,StrokeDashArray=[3,3] });
            var label = new TextBlock { Text=$"均值 {average:0.0}",FontSize=12, Foreground=muted };
            Canvas.SetLeft(label,Math.Clamp(X(average)-28,0,width-75)); Canvas.SetTop(label,0); _plot.Children.Add(label);
        }
        var complete = _points.Count(p => p.Complete);
        var overflow = _points.Count(p => p.Pulls>_maximum);
        _detail.Text = _points.Count == 0 ? "暂无可绘制的出货记录。"
            : $"{_points.Count} 个结果 · {complete} 个完整间隔参与均值。空心点为不完整历史，不计入均值。点击圆点查看物品和日期。"
                + (overflow>0 ? $" {overflow} 个间隔超过坐标参考上限，显示在右端，请校对记录。" : "");
    }
}