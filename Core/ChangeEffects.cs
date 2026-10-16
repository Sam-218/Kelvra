using System.Globalization;

namespace Kelvra;

/// <summary>
/// How games ran after a driver (or BIOS / Windows) change against before it, from the game sessions: average FPS and the
/// hottest GPU, game by game, so a lighter game played afterwards isn't mistaken for a better driver. Sessions up to 30 days
/// on each side, and only until the next change of the same kind (the days after that belong to it).
/// </summary>
public static class ChangeEffects
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);
    public const double MinSeconds = 600;       // play time needed on each side
    public const double SameFpsShare = 0.03;    // within 3 %: about the same
    public const float MinTempDiff = 3;

    public sealed record GameEffect(string Game, double FpsBefore, double FpsAfter, float? GpuBefore, float? GpuAfter)
    {
        public double FpsChange => FpsBefore > 0 ? FpsAfter / FpsBefore - 1 : 0;
    }

    /// <param name="at">When the change was noticed.</param>
    /// <param name="next">The next change of the same kind, if any.</param>
    public static List<GameEffect> Of(DateTime at, DateTime? next, IEnumerable<GameSessionSummary> sessions, int top = 3)
    {
        var end = next is DateTime n && n < at + Window ? n : at + Window;
        var effects = new List<GameEffect>();
        foreach (var game in sessions.Where(s => s.AvgFps is > 0).GroupBy(s => s.Game, StringComparer.OrdinalIgnoreCase))
        {
            var before = game.Where(s => s.Started >= at - Window && s.Started < at).ToList();
            var after = game.Where(s => s.Started >= at && s.Started < end).ToList();
            double secBefore = before.Sum(s => s.Seconds), secAfter = after.Sum(s => s.Seconds);
            if (secBefore < MinSeconds || secAfter < MinSeconds) continue;
            effects.Add(new GameEffect(game.Key, Weighted(before), Weighted(after), Hottest(before), Hottest(after)));
        }
        // The games with the most play on both sides first
        return effects.OrderByDescending(e => Math.Min(Seconds(sessions, e.Game, at - Window, at), Seconds(sessions, e.Game, at, end))).Take(top).ToList();
    }

    /// <summary>"Cyberpunk 2077: 92 → 101 FPS (+10 %), GPU 78 → 75 °C".</summary>
    public static string Describe(GameEffect e)
    {
        var inv = CultureInfo.InvariantCulture;
        string fps = Math.Abs(e.FpsChange) < SameFpsShare
            ? $"about the same FPS ({e.FpsAfter.ToString("0", inv)})"
            : $"{e.FpsBefore.ToString("0", inv)} → {e.FpsAfter.ToString("0", inv)} FPS ({(e.FpsChange * 100).ToString("+0;-0", inv)} %)";
        string gpu = e.GpuBefore is float b && e.GpuAfter is float a && Math.Abs(a - b) >= MinTempDiff
            ? $", hottest GPU {Temp(b)} → {Temp(a)}" : "";
        return $"{e.Game}: {fps}{gpu}";
    }

    private static string Temp(float c) => SensorFormat.Format(LibreHardwareMonitor.Hardware.SensorType.Temperature, c);

    private static double Weighted(List<GameSessionSummary> s) => s.Sum(x => x.AvgFps!.Value * x.Seconds) / s.Sum(x => x.Seconds);

    /// <summary>The typical hottest GPU of the sessions (their median), so one long hot session doesn't decide alone.</summary>
    private static float? Hottest(List<GameSessionSummary> s)
    {
        var temps = s.Where(x => x.MaxGpuTemp is float).Select(x => x.MaxGpuTemp!.Value).Order().ToList();
        return temps.Count == 0 ? null : temps[temps.Count / 2];
    }

    private static double Seconds(IEnumerable<GameSessionSummary> sessions, string game, DateTime from, DateTime to) =>
        sessions.Where(s => string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase) && s.Started >= from && s.Started < to).Sum(s => s.Seconds);
}
