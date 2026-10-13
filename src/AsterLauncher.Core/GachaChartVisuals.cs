namespace AsterLauncher.Core;

public readonly record struct GachaChartColor(byte Red, byte Green, byte Blue);
public sealed record GachaCountBarVisual(
    double Fraction, GachaChartColor Start, GachaChartColor End, bool IsLowerBound, bool HasKnownMaximum);
public sealed record GachaScatterVisual(double Diameter, double Opacity, double StrokeThickness, bool IsHollow);

/// <summary>Visual strength describes recorded counts, never UP classification or an inferred pity rule.</summary>
public static class GachaChartVisuals
{
    private static readonly (double Position, GachaChartColor Color)[] Palette =
    [
        (0, new(66, 111, 131)),
        (0.5, new(123, 105, 62)),
        (0.72, new(164, 93, 57)),
        (0.85, new(165, 53, 61)),
        (1, new(143, 29, 43))
    ];

    public static GachaCountBarVisual CountBar(int count, int? maximum, bool complete)
    {
        var known = maximum is > 0;
        var fraction = known ? Math.Clamp(count / (double)maximum!.Value, 0, 1) : 0;
        // A first observed result is only a lower bound. Keep its measured length
        // and a restrained warm tone instead of presenting an exact late interval.
        var color = !known ? new GachaChartColor(102, 115, 129) : ColorAt(complete ? fraction : Math.Min(fraction, 0.68));
        return new(fraction, Mix(color, new(255, 255, 255), 0.06), Mix(color, new(0, 0, 0), 0.04), !complete, known);
    }

    public static GachaScatterVisual Scatter(int pulls, int axisMaximum, int recordCount, bool containsIncomplete)
    {
        var position = Math.Clamp((pulls - 1d) / Math.Max(1, axisMaximum - 1d), 0, 1);
        var repeats = Math.Log2(Math.Max(1, recordCount));
        return new(
            Math.Min(17, 6.5 + 4.5 * position + 1.8 * repeats),
            Math.Clamp(0.52 + 0.38 * Math.Sqrt(position) + 0.055 * repeats, 0.52, 1),
            containsIncomplete ? Math.Min(2.8, 1.25 + 0.65 * position + 0.25 * repeats) : 0,
            containsIncomplete);
    }

    // The reference may be a confirmed pity maximum, but imported history can
    // contain larger measured intervals. Preserve each recorded X coordinate.
    public static int ScatterAxisMaximum(int referenceMaximum, IEnumerable<int> pulls)
        => Math.Max(2, Math.Max(referenceMaximum, pulls.DefaultIfEmpty(1).Max()));

    private static GachaChartColor ColorAt(double position)
    {
        for (var index = 1; index < Palette.Length; index++)
        {
            if (position > Palette[index].Position) continue;
            var previous = Palette[index - 1];
            var current = Palette[index];
            return Mix(previous.Color, current.Color, (position - previous.Position) / (current.Position - previous.Position));
        }
        return Palette[^1].Color;
    }

    private static GachaChartColor Mix(GachaChartColor first, GachaChartColor second, double amount) => new(
        (byte)Math.Round(first.Red + (second.Red - first.Red) * amount),
        (byte)Math.Round(first.Green + (second.Green - first.Green) * amount),
        (byte)Math.Round(first.Blue + (second.Blue - first.Blue) * amount));
}