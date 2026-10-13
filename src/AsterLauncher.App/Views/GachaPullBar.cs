using AsterLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AsterLauncher.App.Views;

/// <summary>Recomputes the complete fill whenever recycled row data changes.</summary>
public sealed class GachaPullBar : UserControl
{
    public static readonly DependencyProperty PullProperty = DependencyProperty.Register(
        nameof(Pull), typeof(object), typeof(GachaPullBar), new PropertyMetadata(null, OnPullChanged));
    private readonly Border _fill = new() { CornerRadius = new(4), RenderTransformOrigin = new(0, 0.5) };
    private readonly ScaleTransform _scale = new() { ScaleX = 0 };
    private readonly TextBlock _name = new()
    {
        FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new(7, 0, 7, 0),
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
    };
    private Storyboard? _animation;

    public GachaPullBar()
    {
        _fill.RenderTransform = _scale;
        var body = new Grid { Height = 24 };
        body.Children.Add(new Border { Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(242, 43, 56, 72)), CornerRadius = new(4) });
        body.Children.Add(_fill);
        body.Children.Add(_name);
        Content = body;
        Loaded += (_, _) => Refresh(animate: true);
        ActualThemeChanged += (_, _) => Refresh(animate: false);
        Unloaded += (_, _) => { _animation?.Stop(); _animation = null; };
    }

    public object? Pull
    {
        get => GetValue(PullProperty);
        set => SetValue(PullProperty, value);
    }

    private static void OnPullChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((GachaPullBar)sender).Refresh(animate: ((GachaPullBar)sender).IsLoaded);

    private void Refresh(bool animate)
    {
        _animation?.Stop();
        _animation = null;
        var pull = Pull as UigfHighRarityPull;
        _name.Text = pull?.Name ?? "";
        if (pull is null)
        {
            _scale.ScaleX = 0;
            _fill.Background = null;
            ToolTipService.SetToolTip(this, null);
            AutomationProperties.SetName(this, "");
            return;
        }
        var visual = GachaChartVisuals.CountBar(pull.LocalPullCount, pull.PityMaximum, pull.HasPreviousHighRarity);
        _fill.Background = GachaCountBrushConverter.Create(visual);
        _scale.ScaleX = visual.Fraction;
        var explanation = !visual.HasKnownMaximum ? "此类卡池没有已确认的保底上限，未绘制比例。"
            : visual.IsLowerBound ? "缺少此前记录，抽数是本地记录下界，不作为完整出货间隔。"
            : "颜色随已记录的出货间隔由冷到暖，不表示 UP 判定。";
        ToolTipService.SetToolTip(this, $"{pull.Name} · {pull.PullCountText}\n{explanation}");
        AutomationProperties.SetName(this, $"{pull.Name} · {pull.PullCountText}");
        if (!animate || visual.Fraction <= 0 || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var animation = new DoubleAnimation
        {
            From = 0, To = visual.Fraction, Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, _scale);
        Storyboard.SetTargetProperty(animation, nameof(ScaleTransform.ScaleX));
        _animation = new Storyboard();
        _animation.Children.Add(animation);
        _animation.Begin();
    }
}

public sealed class GachaCountBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visual = value switch
        {
            UigfPityIndicator indicator => GachaChartVisuals.CountBar(indicator.Count ?? 0, indicator.Maximum, indicator.HasKnownHighRarity),
            UigfHighRarityPull pull => GachaChartVisuals.CountBar(pull.LocalPullCount, pull.PityMaximum, pull.HasPreviousHighRarity),
            _ => GachaChartVisuals.CountBar(0, null, false)
        };
        return Create(visual);
    }

    internal static LinearGradientBrush Create(GachaCountBarVisual visual) => new()
    {
        StartPoint = new(0, 0.5), EndPoint = new(1, 0.5),
        GradientStops =
        {
            new GradientStop { Offset = 0, Color = Color(visual.Start) },
            new GradientStop { Offset = 1, Color = Color(visual.End) }
        }
    };
    private static Windows.UI.Color Color(GachaChartColor color)
        => Microsoft.UI.ColorHelper.FromArgb(255, color.Red, color.Green, color.Blue);
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class GachaHighCountBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true
        ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 143, 29, 43))
        : AsterLauncher.App.Services.AppearanceBrushes.Get("PageTitleBrush");
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}