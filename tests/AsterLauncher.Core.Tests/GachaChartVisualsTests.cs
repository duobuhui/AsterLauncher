using AsterLauncher.Core;

namespace AsterLauncher.Core.Tests;

public sealed class GachaChartVisualsTests
{
    [Theory]
    [InlineData(80, 90)]
    [InlineData(76, 90)]
    [InlineData(70, 80)]
    [InlineData(90, 90)]
    public void LateCompleteIntervalsRemainRedInsteadOfDependingOnRowLoad(int count, int maximum)
    {
        var visual = GachaChartVisuals.CountBar(count, maximum, true);
        Assert.True(visual.HasKnownMaximum);
        Assert.False(visual.IsLowerBound);
        Assert.InRange(visual.Fraction, 0.84, 1);
        Assert.True(visual.Start.Red > visual.Start.Green * 1.5);
        Assert.True(visual.End.Red > visual.End.Green * 1.5);
        Assert.True(visual.Start.Green < 85 && visual.Start.Blue < 85);
        Assert.True(visual.End.Green < 85 && visual.End.Blue < 85);
    }

    [Fact]
    public void IncreasingCompleteIntervalsBecomeProgressivelyWarmer()
    {
        var counts = new[] { 1, 20, 45, 65, 76, 90 };
        var previous = int.MinValue;
        foreach (var count in counts)
        {
            var color = GachaChartVisuals.CountBar(count, 90, true).Start;
            var warmth = color.Red - color.Green;
            Assert.True(warmth >= previous);
            previous = warmth;
        }
    }

    [Fact]
    public void FirstObservedResultKeepsItsMeasuredLengthAndLowerBoundMeaning()
    {
        var partial = GachaChartVisuals.CountBar(80, 90, false);
        var complete = GachaChartVisuals.CountBar(80, 90, true);
        Assert.True(partial.IsLowerBound);
        Assert.True(partial.HasKnownMaximum);
        Assert.Equal(complete.Fraction, partial.Fraction);
        Assert.NotEqual(complete.Start, partial.Start);
        Assert.True(partial.Start.Red > partial.Start.Blue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnknownMaximumDoesNotFabricateAPityFractionOrLateRed(int? maximum)
    {
        var visual = GachaChartVisuals.CountBar(200, maximum, true);
        Assert.False(visual.HasKnownMaximum);
        Assert.Equal(0, visual.Fraction);
        Assert.True(visual.Start.Red < visual.Start.Blue);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(45, 0.5)]
    [InlineData(90, 1)]
    [InlineData(int.MaxValue, 1)]
    public void BarLengthStaysWithinTheConfirmedReference(int count, double expected)
        => Assert.Equal(expected, GachaChartVisuals.CountBar(count, 90, true).Fraction);

    [Fact]
    public void RecomputingLateIntervalAfterAnotherRowProducesTheSameColor()
    {
        var late = GachaChartVisuals.CountBar(82, 90, true);
        _ = GachaChartVisuals.CountBar(5, 90, true);
        _ = GachaChartVisuals.CountBar(80, null, false);
        Assert.Equal(late, GachaChartVisuals.CountBar(82, 90, true));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(80)]
    [InlineData(180)]
    public void LargerPullCountsAreMoreVisibleWithoutAddingRecordedSamples(int maximum)
    {
        var early = GachaChartVisuals.Scatter(1, maximum, 1, false);
        var middle = GachaChartVisuals.Scatter(maximum / 2, maximum, 1, false);
        var late = GachaChartVisuals.Scatter(maximum, maximum, 1, false);
        Assert.True(early.Diameter < middle.Diameter && middle.Diameter < late.Diameter);
        Assert.True(early.Opacity < middle.Opacity && middle.Opacity < late.Opacity);
        Assert.False(late.IsHollow);
        Assert.Equal(0, late.StrokeThickness);
    }

    [Fact]
    public void RepeatedRecordedPositionsBecomeDenserWhileRetainingIncompleteHistory()
    {
        var single = GachaChartVisuals.Scatter(60, 90, 1, true);
        var repeated = GachaChartVisuals.Scatter(60, 90, 4, true);
        Assert.True(repeated.Diameter > single.Diameter);
        Assert.True(repeated.Opacity > single.Opacity);
        Assert.True(repeated.StrokeThickness > single.StrokeThickness);
        Assert.True(single.IsHollow && repeated.IsHollow);
    }

    [Theory]
    [InlineData(1, 90, 1)]
    [InlineData(-10, 0, -1)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public void ScatterVisualsStayFiniteAndBoundedForExtremeInput(int pulls, int maximum, int records)
    {
        var visual = GachaChartVisuals.Scatter(pulls, maximum, records, true);
        Assert.InRange(visual.Diameter, 6.5, 17);
        Assert.InRange(visual.Opacity, 0.52, 1);
        Assert.InRange(visual.StrokeThickness, 1.25, 2.8);
        Assert.True(visual.IsHollow);
    }

    [Fact]
    public void AxisExpandsForActualIntervalsRatherThanClippingDifferentRecordsTogether()
    {
        var pulls = new[] { 10, 90, 110, 140, 110 };
        Assert.Equal(140, GachaChartVisuals.ScatterAxisMaximum(90, pulls));
        Assert.Equal(new[] { 10, 90, 110, 140, 110 }, pulls);
        Assert.Equal(180, GachaChartVisuals.ScatterAxisMaximum(180, pulls));
    }

    [Theory]
    [InlineData(90, 90)]
    [InlineData(0, 2)]
    [InlineData(-10, 2)]
    public void EmptyScatterRetainsAUsableReferenceAxis(int reference, int expected)
        => Assert.Equal(expected, GachaChartVisuals.ScatterAxisMaximum(reference, []));
}