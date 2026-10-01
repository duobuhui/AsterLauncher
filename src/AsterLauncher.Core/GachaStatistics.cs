namespace AsterLauncher.Core;

public enum GachaUpStatus { Unknown, Featured, NonFeatured, NotApplicable }
public sealed record GachaStatisticPoint(string RecordId, string Name, string Time, int Pulls, bool Complete, GachaUpStatus UpStatus);
public sealed record GachaPoolStatistics(string Type, string Title, int Draws, string HighLabel, int HighCount, int MiddleCount,
    int LowCount, int CurrentPity, int? PityMaximum, int? FeaturedMaximum,
    IReadOnlyList<GachaStatisticPoint> HighPoints, IReadOnlyList<GachaStatisticPoint> FeaturedPoints)
{
    public double? Average => Mean(HighPoints);
    public double? FeaturedAverage => Mean(FeaturedPoints);
    public int UnknownCount => HighPoints.Count(p => p.UpStatus == GachaUpStatus.Unknown);
    public int FeaturedCount => HighPoints.Count(p => p.UpStatus == GachaUpStatus.Featured);
    public int NonFeaturedCount => HighPoints.Count(p => p.UpStatus == GachaUpStatus.NonFeatured);
    public int? Minimum => HighPoints.Where(p => p.Complete).Select(p => (int?)p.Pulls).Min();
    public int? Maximum => HighPoints.Where(p => p.Complete).Select(p => (int?)p.Pulls).Max();
    private static double? Mean(IReadOnlyList<GachaStatisticPoint> points)
    {
        var complete = points.Where(p => p.Complete).ToArray();
        return complete.Length == 0 ? null : complete.Average(p => p.Pulls);
    }
}
public sealed record EndfieldWeaponPoolStatistics(string PoolId, string Title, int Draws, int SixStars, int FiveStars, int OtherRarity);
