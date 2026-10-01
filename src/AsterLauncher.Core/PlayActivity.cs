namespace AsterLauncher.Core;

public sealed record PlayActivityDay(DateOnly Date, double Seconds, int Sessions);
public sealed record PlayActivity(IReadOnlyList<PlayActivityDay> Days, double UnmappedSeconds)
{
    public double Seconds => Days.Sum(d => d.Seconds);
    public int ActiveDays => Days.Count(d => d.Seconds > 0);
    public int LongestStreak
    {
        get { var longest = 0; var current = 0; foreach (var day in Days) { current = day.Seconds > 0 ? current + 1 : 0; longest = Math.Max(longest, current); } return longest; }
    }
    public static PlayActivity Build(GameUserState state, DateOnly from, DateOnly through, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (through < from || through.DayNumber - from.DayNumber > 370) throw new ArgumentOutOfRangeException(nameof(through));
        var seconds = new double[through.DayNumber - from.DayNumber + 1];
        var sessions = new int[seconds.Length];
        double recorded = 0;
        foreach (var session in state.PlaySessions)
        {
            if (!double.IsFinite(session.DurationSeconds) || session.DurationSeconds <= 0 || session.StartedAt > now) continue;
            var duration = Math.Min(session.DurationSeconds, (now - session.StartedAt).TotalSeconds);
            recorded += duration;
            var end = session.StartedAt.AddSeconds(duration);
            var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(session.StartedAt, zone).DateTime);
            var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end, zone).DateTime);
            for (var date = first < from ? from : first; date <= last && date <= through; date = date.AddDays(1))
            {
                var startOfDay = Boundary(date);
                var nextDay = Boundary(date.AddDays(1));
                var start = session.StartedAt > startOfDay ? session.StartedAt : startOfDay;
                var finish = end < nextDay ? end : nextDay;
                if (finish <= start) continue;
                var i = date.DayNumber - from.DayNumber;
                seconds[i] += (finish - start).TotalSeconds;
                sessions[i]++;
            }
        }
        var days = Enumerable.Range(0, seconds.Length).Select(i => new PlayActivityDay(from.AddDays(i), seconds[i], sessions[i])).ToArray();
        return new PlayActivity(days, double.IsFinite(state.TotalPlaySeconds) ? Math.Max(0, state.TotalPlaySeconds - recorded) : 0);

        DateTimeOffset Boundary(DateOnly date)
        {
            var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
        }
    }
}