namespace Kelvra;

/// <summary>
/// Notices a fan that stops: it had been spinning, and now reads 0 RPM for <see cref="StoppedFor"/> while its output is
/// driven at <see cref="MinDuty"/> % or more. Only fans whose duty is known count (motherboard headers, and fans Kelvra drives):
/// graphics card fans stop on purpose at low temperatures, and an empty header never spins at all.
/// </summary>
public sealed class FanWatch
{
    public static readonly TimeSpan StoppedFor = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SpinningFor = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(30);
    public const float MinDuty = 30, SpinningRpm = 300, StoppedRpm = 50;

    private sealed class State
    {
        public DateTime? SpinningSince, StoppedSince, Warned;
        public bool HasSpun;
    }

    private readonly Dictionary<string, State> _fans = new();

    /// <summary>One reading of a fan. Returns true when it should be reported as stopped now.</summary>
    public bool Check(string fanId, float? rpm, float? duty, DateTime now)
    {
        if (rpm is not float r) return false;
        if (!_fans.TryGetValue(fanId, out var s)) _fans[fanId] = s = new State();

        if (r >= SpinningRpm)
        {
            s.SpinningSince ??= now;
            if (now - s.SpinningSince >= SpinningFor) s.HasSpun = true;
            s.StoppedSince = null;
            return false;
        }
        s.SpinningSince = null;
        if (r > StoppedRpm || !s.HasSpun || duty is not float d || d < MinDuty)
        {
            s.StoppedSince = null;
            return false;
        }
        s.StoppedSince ??= now;
        if (now - s.StoppedSince < StoppedFor) return false;
        if (s.Warned is DateTime w && now - w < RepeatAfter) return false;
        s.Warned = now;
        return true;
    }
}

/// <summary>A fan turning slower at the same duty than it used to: dust, a worn bearing, or a cable getting in the way.</summary>
public sealed record FanSlowdown(long Fan, int Duty, double RpmNow, double RpmBefore)
{
    public double Percent => RpmBefore > 0 ? (1 - RpmNow / RpmBefore) * 100 : 0;
}

/// <summary>Finds fans that turn slower than they used to at the same duty (from the history's fan_duty_day rows).</summary>
public static class FanDrift
{
    /// <summary>Samples (about one per 5 s) needed at a duty on each side: half an hour.</summary>
    public const int MinSamples = 360;
    public const double MinShare = 0.10, MinRpm = 100;

    /// <summary>
    /// Compares each fan's speed at each duty in the last <paramref name="recentDays"/> days with the same duty
    /// 30 to 120 days ago. Reported per fan at the duty with the most time on both sides, and only when clearly slower.
    /// </summary>
    public static List<FanSlowdown> Find(IEnumerable<FanDutyRow> rows, DateTime today, int recentDays = 7)
    {
        long recentFrom = HistoryDb.DayOf(today.AddDays(-recentDays + 1));
        long oldFrom = HistoryDb.DayOf(today.AddDays(-120)), oldTo = HistoryDb.DayOf(today.AddDays(-30));
        var found = new List<FanSlowdown>();
        foreach (var fan in rows.GroupBy(r => r.Fan))
        {
            var now = Sum(fan.Where(r => r.Day >= recentFrom));
            var before = Sum(fan.Where(r => r.Day >= oldFrom && r.Day < oldTo));
            var best = now.Keys.Where(d => now[d].N >= MinSamples && before.TryGetValue(d, out var b) && b.N >= MinSamples)
                          .OrderByDescending(d => Math.Min(now[d].N, before[d].N)).Select(d => (int?)d).FirstOrDefault();
            if (best is not int duty) continue;
            double rpmNow = now[duty].Sum / now[duty].N, rpmBefore = before[duty].Sum / before[duty].N;
            if (rpmBefore - rpmNow >= MinRpm && rpmNow <= rpmBefore * (1 - MinShare))
                found.Add(new FanSlowdown(fan.Key, duty, rpmNow, rpmBefore));
        }
        return found;
    }

    private static Dictionary<int, (double Sum, int N)> Sum(IEnumerable<FanDutyRow> rows) =>
        rows.GroupBy(r => r.Duty).ToDictionary(g => g.Key, g => (g.Sum(r => r.RpmSum), g.Sum(r => r.N)));

    /// <summary>A duty in 10 % steps (55 % → 60), the bins fan speeds are compared in.</summary>
    public static int Bin(float duty) => (int)Math.Round(Math.Clamp(duty, 0, 100) / 10, MidpointRounding.AwayFromZero) * 10;
}
