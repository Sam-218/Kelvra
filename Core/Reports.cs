using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

public enum ReportRange { Day, Week, Month, Year }

/// <summary>A report's span of local time, [From, To).</summary>
public sealed record ReportPeriod(ReportRange Range, DateTime From, DateTime To)
{
    public static ReportPeriod For(ReportRange range, DateTime anchor)
    {
        var day = anchor.Date;
        switch (range)
        {
            case ReportRange.Week:
                var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
                var start = day.AddDays(-(((int)day.DayOfWeek - (int)first + 7) % 7));
                return new(range, start, start.AddDays(7));
            case ReportRange.Month:
                var month = new DateTime(day.Year, day.Month, 1);
                return new(range, month, month.AddMonths(1));
            case ReportRange.Year:
                var year = new DateTime(day.Year, 1, 1);
                return new(range, year, year.AddYears(1));
            default:
                return new(range, day, day.AddDays(1));
        }
    }

    public ReportPeriod Previous => For(Range, From.AddDays(-1));
    public ReportPeriod Next => For(Range, To);
    public bool IsCurrent => From <= DateTime.Now && DateTime.Now < To;

    /// <summary>"Today", "Yesterday", "Tue, 14 Oct 2026"; "Week of 13 Oct 2026"; "October 2026"; "2026".</summary>
    public string Title
    {
        get
        {
            var c = CultureInfo.CurrentCulture;
            return Range switch
            {
                ReportRange.Day when From == DateTime.Today => "Today",
                ReportRange.Day when From == DateTime.Today.AddDays(-1) => "Yesterday",
                ReportRange.Day => From.ToString("D", c),
                ReportRange.Week => $"Week of {From.ToString("d MMM yyyy", c)}",
                ReportRange.Month => From.ToString("MMMM yyyy", c),
                _ => From.Year.ToString(c),
            };
        }
    }

    /// <summary>Days in the period.</summary>
    public int Days => (int)Math.Round((To - From).TotalDays);
}

public enum InsightTone { Neutral, Good, Warn, Hot }

/// <summary>One line of the report's "worth knowing". Higher <see cref="Priority"/> first.</summary>
public sealed record Insight(string Icon, string Text, InsightTone Tone, int Priority);

/// <summary>A role's numbers over the period: the average and the highest reading, and when that was.</summary>
public sealed record RoleStat(string Role, SensorType Type, double? Avg, double? Max, DateTime? MaxAt);

/// <summary>One point of a report chart: a minute (day reports) or a day (longer ones).</summary>
public sealed record ReportPoint(DateTime Time, double Avg, double Max);

/// <summary>Everything a report shows, read in one go on a worker thread.</summary>
public sealed class Report
{
    public required ReportPeriod Period { get; init; }
    public int OnMinutes { get; init; }
    /// <summary>PC time in the period before, for "3 h more than last week".</summary>
    public int PreviousOnMinutes { get; init; }
    /// <summary>The GPU's limit (the user's alert, else the usual one), drawn on the chart.</summary>
    public double GpuLimit { get; init; } = ReportBuilder.DefaultGpuLimit;
    /// <summary>The limit is the user's own alert ("85° alert"), not the usual one ("87° limit").</summary>
    public bool GpuLimitIsAlert { get; init; }
    public Dictionary<string, RoleStat> Stats { get; init; } = new();
    /// <summary>Chart series by role (cpu-temp, gpu-temp): minutes for a day, days for longer.</summary>
    public Dictionary<string, List<ReportPoint>> Series { get; init; } = new();
    public bool Minutely { get; init; }
    public List<AppTotal> Apps { get; init; } = new();
    public List<CrashRecord> Crashes { get; init; } = new();
    public List<GameSessionSummary> Games { get; init; } = new();
    public List<ChangeRecord> Changes { get; init; } = new();
    public List<(string Fan, FanSlowdown Slowdown)> FanSlowdowns { get; init; } = new();
    /// <summary>Minutes a chip's minute average held at or over its limit, with the limit (°C).</summary>
    public Dictionary<string, (int Minutes, double Limit)> OverLimit { get; init; } = new();
    /// <summary>Which app was in front at the hottest minute of each role (while the minutes are kept).</summary>
    public Dictionary<string, string> HottestWith { get; init; } = new();
    /// <summary>Per app: average GPU temperature in the 30 days before the period, and the time it was in front then.</summary>
    public Dictionary<long, (double? GpuAvg, double FgSec)> AppsBefore { get; init; } = new();
    public List<Insight> Insights { get; set; } = new();
    public bool HasData => OnMinutes > 0 || Crashes.Count > 0 || Games.Count > 0;
}

/// <summary>Reads a report from the history and words its insights.</summary>
public static class ReportBuilder
{
    /// <summary>Default "too hot" limits (°C, held for a minute) when no alert rule says otherwise.</summary>
    public const double DefaultCpuLimit = 90, DefaultGpuLimit = 87;

    public static Report Build(HistoryDb db, ReportPeriod p, IReadOnlyList<GameSessionSummary> sessions, IEnumerable<AlertRule> alerts)
    {
        long from = HistoryDb.Unix(p.From), to = HistoryDb.Unix(p.To);
        long fromDay = HistoryDb.DayOf(p.From), toDay = HistoryDb.DayOf(p.To);
        bool minutely = p.Range == ReportRange.Day;
        var sensors = db.Sensors();
        var byRole = sensors.Where(s => s.Role.Length > 0).GroupBy(s => s.Role).ToDictionary(g => g.Key, g => g.First());
        var appNames = db.AppNames();
        var fg = minutely ? db.ForegroundMinutes(from, to) : new();

        var stats = new Dictionary<string, RoleStat>();
        var series = new Dictionary<string, List<ReportPoint>>();
        var overLimit = new Dictionary<string, (int, double)>();
        var hottestWith = new Dictionary<string, string>();
        foreach (var role in new[] { HistoryRoles.CpuTemp, HistoryRoles.GpuTemp, HistoryRoles.GpuHotSpot, HistoryRoles.CpuLoad,
                                     HistoryRoles.GpuLoad, HistoryRoles.CpuPower, HistoryRoles.GpuPower, HistoryRoles.RamLoad, HistoryRoles.Fps })
        {
            if (!byRole.TryGetValue(role, out var s)) continue;
            var days = db.Days(s.Id, fromDay, toDay);
            if (days.Count == 0) continue;
            int n = days.Sum(d => d.N);
            double? avg = n > 0 ? days.Sum(d => d.Sum) / n : null;
            var maxDay = days.MaxBy(d => d.Max)!;
            DateTime? maxAt = HistoryDb.Local(maxDay.Day);

            if (minutely)
            {
                var minutes = db.Minutes(s.Id, from, to);
                if (minutes.Count > 0)
                {
                    var hottest = minutes.MaxBy(m => m.Max);
                    maxAt = HistoryDb.Local(hottest.Ts);
                    if (fg.TryGetValue(hottest.Ts, out long app) && appNames.TryGetValue(app, out var name)) hottestWith[role] = name;
                    if (role is HistoryRoles.CpuTemp or HistoryRoles.GpuTemp)
                    {
                        series[role] = minutes.Select(m => new ReportPoint(HistoryDb.Local(m.Ts), m.Avg, m.Max)).ToList();
                        double limit = LimitFor(role, s.Key, alerts);
                        int over = minutes.Count(m => m.Avg >= limit);
                        if (over > 0) overLimit[role] = (over, limit);
                    }
                }
            }
            else
            {
                // Longer periods: the hottest day's minutes (while they're still kept) say when exactly, and what was in front
                long dayEnd = HistoryDb.DayOf(HistoryDb.Local(maxDay.Day).AddDays(1));
                var dayMinutes = db.Minutes(s.Id, maxDay.Day, dayEnd);
                if (dayMinutes.Count > 0)
                {
                    var hottest = dayMinutes.MaxBy(m => m.Max);
                    maxAt = HistoryDb.Local(hottest.Ts);
                    if (db.ForegroundMinutes(hottest.Ts, hottest.Ts + 60).TryGetValue(hottest.Ts, out long app) && appNames.TryGetValue(app, out var name))
                        hottestWith[role] = name;
                }
                if (role is HistoryRoles.CpuTemp or HistoryRoles.GpuTemp)
                {
                    // A week by the hour (from its minutes, while they're kept); a month or year by the day
                    var minutes = p.Range == ReportRange.Week ? db.Minutes(s.Id, from, to) : new();
                    series[role] = minutes.Count > 0
                        ? minutes.GroupBy(m => m.Ts / 3600).Select(h => new ReportPoint(HistoryDb.Local(h.Key * 3600 + 1800),
                              h.Average(m => m.Avg), h.Max(m => m.Max))).ToList()
                        : days.Select(d => new ReportPoint(HistoryDb.Local(d.Day), d.Avg, d.Max)).ToList();
                }
            }

            stats[role] = new RoleStat(role, (SensorType)s.Kind, avg, maxDay.Max, maxAt);
        }

        // The apps' temperatures in the 30 days before, for "ran hotter than before" (same app, so a like-for-like load)
        var before = db.AppTotals(HistoryDb.DayOf(p.From.AddDays(-30)), fromDay).ToDictionary(a => a.App, a => (a.GpuTempAvg, a.FgSec));

        var fanNames = sensors.ToDictionary(s => s.Id, s => $"{s.Name} ({s.Hardware})");
        var slowdowns = p.IsCurrent || p.To >= DateTime.Today
            ? FanDrift.Find(db.FanDuty(HistoryDb.DayOf(DateTime.Today.AddDays(-130)), HistoryDb.DayOf(DateTime.Today.AddDays(1))), DateTime.Today)
                      .Select(f => (fanNames.GetValueOrDefault(f.Fan, "A fan"), f)).ToList()
            : new();

        var report = new Report
        {
            Period = p,
            OnMinutes = db.OnDays(fromDay, toDay).Sum(d => d.Minutes),
            PreviousOnMinutes = db.OnDays(HistoryDb.DayOf(p.Previous.From), fromDay).Sum(d => d.Minutes),
            GpuLimit = byRole.TryGetValue(HistoryRoles.GpuTemp, out var gpuSensor) ? LimitFor(HistoryRoles.GpuTemp, gpuSensor.Key, alerts) : DefaultGpuLimit,
            GpuLimitIsAlert = gpuSensor != null && alerts.Any(a => a.Enabled && a.Condition == AlertCondition.Above && a.SensorId == gpuSensor.Key),
            Stats = stats,
            Series = series,
            Minutely = minutely,
            Apps = db.AppTotals(fromDay, toDay),
            Crashes = db.Crashes(from, to),
            Games = sessions.Where(s => s.Started >= p.From && s.Started < p.To).ToList(),
            Changes = db.Changes(from, to),
            FanSlowdowns = slowdowns,
            OverLimit = overLimit,
            HottestWith = hottestWith,
            AppsBefore = before,
        };
        report.Insights = Insights.For(report, sessions);
        return report;
    }

    /// <summary>The user's own "above" alert limit for the role's sensor, else the default.</summary>
    internal static double LimitFor(string role, string sensorKey, IEnumerable<AlertRule> alerts) =>
        alerts.Where(a => a.Enabled && a.Condition == AlertCondition.Above && a.SensorId == sensorKey).Select(a => (double?)a.Threshold).Min()
        ?? (role == HistoryRoles.CpuTemp ? DefaultCpuLimit : DefaultGpuLimit);
}

/// <summary>One game in the report: all its sessions in the period together.</summary>
public sealed record GameRow(string Name, double Seconds, string Played, string Avg, string Low, string HeldBy, string Detail)
{
    public bool HasHeldBy => HeldBy.Length > 0;
}

/// <summary>The report's header in plain words: "A calm week, one hiccup." and what's behind it.</summary>
public static class ReportSummary
{
    private static string Unit(ReportRange r) => r.ToString().ToLowerInvariant();

    private static string Deg(double celsius) => SensorFormat.Format(SensorType.Temperature, (float)celsius);

    /// <summary>"A calm week." / "A calm week, one hiccup." / "A warm week." / "A hot week, 2 problems." / "A week with 3 problems."</summary>
    public static string Headline(Report r)
    {
        string unit = Unit(r.Period.Range);
        int problems = r.Crashes.Count;
        double peak = new[] { HistoryRoles.CpuTemp, HistoryRoles.GpuTemp }.Select(role => r.Stats.GetValueOrDefault(role)?.Max ?? 0).Max();
        bool hot = r.OverLimit.Count > 0 || peak >= Thermal.HotBelow, warm = peak >= Thermal.WarmBelow;
        string count = problems == 1 ? "one hiccup" : $"{problems} problems";
        return (problems, hot) switch
        {
            (0, true) => $"A hot {unit}.",
            (_, true) => $"A hot {unit}, {count}.",
            (0, _) => warm ? $"A warm {unit}." : $"A calm {unit}.",
            (1, _) => $"A {(warm ? "warm" : "calm")} {unit}, one hiccup.",
            _ => $"A {unit} with {problems} problems.",
        };
    }

    /// <summary>"10 Oct – 16 Oct · your PC was on for 31 hours. Graphics driver reset on Saturday."</summary>
    public static string Subline(Report r)
    {
        var c = CultureInfo.CurrentCulture;
        var p = r.Period;
        string dates = p.Range == ReportRange.Week ? WeekDates(p.From, p.To.AddDays(-1), c) : p.Title;
        string on = r.OnMinutes == 0 ? "Kelvra recorded no PC time."
            : $"your PC was on for {(r.OnMinutes >= 120 ? $"{r.OnMinutes / 60} hours" : Insights.Duration(r.OnMinutes * 60.0))}.";
        string crash = "";
        if (r.Crashes.Count > 0)
        {
            var latest = r.Crashes[0];
            var at = HistoryDb.Local(latest.Ts);
            string when = p.Range switch
            {
                ReportRange.Day => $"at {at.ToString("t", c)}",
                ReportRange.Week => $"on {at.ToString("dddd", c)}",
                _ => $"on {at.ToString("d MMM", c)}",
            };
            string title = CrashExplainer.Explain(latest).Title;
            // "One graphics driver reset on Saturday." – an app's name keeps its capitals ("Discord crashed on Friday.")
            if (r.Crashes.Count == 1 && latest.Kind is not (CrashKinds.AppCrash or CrashKinds.AppHang))
                title = "One " + char.ToLower(title[0], c) + title[1..];
            string more = r.Crashes.Count > 1 ? $", {r.Crashes.Count - 1} more on the Crashes page" : "";
            crash = $" {title} {when}{more}.";
        }
        return $"{dates} · {on}{crash}";
    }

    /// <summary>"Oct 10 – 16" (or "10 – 16 Oct", as the culture writes it); "Sep 28 – Oct 4" across a month.</summary>
    internal static string WeekDates(DateTime first, DateTime last, CultureInfo c)
    {
        string monthDay = c.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM");
        if (first.Month != last.Month) return $"{first.ToString(monthDay, c)} – {last.ToString(monthDay, c)}";
        return monthDay.TrimStart().StartsWith('M')
            ? $"{first.ToString(monthDay, c)} – {last.Day}"
            : $"{first.Day} – {last.ToString(monthDay, c)}";
    }

    /// <summary>"3 h more than last week", "about the same as yesterday", "" when nothing was recorded before.</summary>
    public static string PcTimeChange(Report r)
    {
        if (r.PreviousOnMinutes == 0) return "";
        string before = r.Period.Range == ReportRange.Day ? "the day before" : $"last {Unit(r.Period.Range)}";
        int diff = r.OnMinutes - r.PreviousOnMinutes;
        if (Math.Abs(diff) < 60) return $"about the same as {before}";
        return $"{Math.Abs(diff) / 60} h {(diff > 0 ? "more" : "less")} than {before}";
    }

    /// <summary>"Peak Sat 21:14 · Cyberpunk 2077": when a role peaked, and what was in front then.</summary>
    public static string Peak(Report r, string role)
    {
        if (r.Stats.GetValueOrDefault(role)?.MaxAt is not DateTime at) return "";
        var c = CultureInfo.CurrentCulture;
        string when = r.Period.Range switch
        {
            ReportRange.Day => at.ToString("t", c),
            ReportRange.Week => $"{at.ToString("ddd", c)} {at.ToString("t", c)}",
            _ => at.TimeOfDay == TimeSpan.Zero ? at.ToString("d MMM", c) : $"{at.ToString("d MMM", c)}, {at.ToString("t", c)}",
        };
        return r.HottestWith.TryGetValue(role, out var app) ? $"Peak {when} · {app}" : $"Peak {when}";
    }

    /// <summary>The period's games, one row per game (benchmarks left out), most played first.</summary>
    public static List<GameRow> Games(IEnumerable<GameSessionSummary> sessions) =>
        sessions.Where(s => !s.Benchmark).GroupBy(s => s.Game, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            double seconds = g.Sum(s => s.Seconds);
            // ponytail: time-weighted mean of the sessions' figures, not a pooled 1 % low (the frames aren't kept)
            double? Weighted(Func<GameSessionSummary, double?> pick)
            {
                var with = g.Where(s => pick(s) is double && s.Seconds > 0).ToList();
                double w = with.Sum(s => s.Seconds);
                return w > 0 ? with.Sum(s => pick(s)!.Value * s.Seconds) / w : null;
            }
            int gpu = g.Sum(s => s.GpuBoundSeconds), cpu = g.Sum(s => s.CpuBoundSeconds), capped = g.Sum(s => s.CappedSeconds);
            string held = gpu + cpu + capped == 0 ? "" : gpu >= cpu && gpu >= capped ? "GPU" : cpu >= capped ? "CPU" : "Cap";
            int crashed = g.Count(s => s.Crashed);
            string detail = $"{g.Count()} session{(g.Count() == 1 ? "" : "s")}" + (crashed > 0 ? $", {crashed} ended in a crash" : "");
            return new GameRow(g.First().Game, seconds, Insights.Short(seconds), Weighted(s => s.AvgFps) is double a ? a.ToString("0") : "–",
                               Weighted(s => s.Low1) is double l ? l.ToString("0") : "–", held, detail);
        }).OrderByDescending(r => r.Seconds).ToList();

    /// <summary>The whole report as plain text, for "Copy summary".</summary>
    public static string Text(Report r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Kelvra report · {r.Period.Title}");
        sb.AppendLine(Headline(r));
        sb.AppendLine(Subline(r));
        foreach (var (label, role) in new[] { ("CPU", HistoryRoles.CpuTemp), ("GPU", HistoryRoles.GpuTemp) })
            if (r.Stats.GetValueOrDefault(role) is { Avg: double avg, Max: double max })
                sb.AppendLine($"{label}: {Deg(avg)} avg, {Deg(max)} peak. {Peak(r, role)}".TrimEnd());
        foreach (var i in r.Insights) sb.AppendLine("- " + i.Text);
        return sb.ToString();
    }
}

/// <summary>
/// The report's "worth knowing" lines. Each rule is a small function that returns its lines or nothing; only things with
/// enough data behind them, and comparisons only like for like (the same app, the same fan duty).
/// </summary>
public static class Insights
{
    public const double MinAppSeconds = 30 * 60, MinAppSecondsBefore = 60 * 60, AppTempDiff = 5;

    // Segoe Fluent icons
    private const string IconWarn = "", IconTemp = "", IconFan = "", IconCompare = "",
        IconClock = "", IconGame = "", IconGood = "", IconDriver = "";

    private static readonly Func<Report, IReadOnlyList<GameSessionSummary>, IEnumerable<Insight>>[] Rules =
    {
        Crashes, FanSlowdowns, OverLimit, AppTemperatures, Drivers, Usage, Games, AllGood,
    };

    public static List<Insight> For(Report r, IReadOnlyList<GameSessionSummary> allSessions) =>
        Rules.SelectMany(rule => rule(r, allSessions)).OrderByDescending(i => i.Priority).ToList();

    private static IEnumerable<Insight> Crashes(Report r, IReadOnlyList<GameSessionSummary> _)
    {
        if (r.Crashes.Count == 0) yield break;
        var latest = r.Crashes[0];
        var ex = CrashExplainer.Explain(latest);
        string more = r.Crashes.Count > 1 ? $" {r.Crashes.Count} problems in all (Crashes page)." : "";
        string culprit = latest.Kind == CrashKinds.AppHang ? "" : $": {ex.Culprit}"; // "stopped responding: Not responding" says it twice
        yield return new(IconWarn, $"{ex.Title} {When(r, HistoryDb.Local(latest.Ts))}{culprit}.{more}", InsightTone.Warn, 100);
    }

    private static IEnumerable<Insight> FanSlowdowns(Report r, IReadOnlyList<GameSessionSummary> _)
    {
        foreach (var (fan, f) in r.FanSlowdowns)
            yield return new(IconFan, $"{fan} turns {f.Percent:0} % slower at {f.Duty} % than 1–4 months ago ({f.RpmNow:0} vs {f.RpmBefore:0} RPM): " +
                                      "dust, a worn bearing or a cable in the way.", InsightTone.Warn, 90);
    }

    private static IEnumerable<Insight> OverLimit(Report r, IReadOnlyList<GameSessionSummary> _)
    {
        foreach (var (role, (minutes, limit)) in r.OverLimit)
        {
            string chip = role == HistoryRoles.CpuTemp ? "CPU" : "GPU";
            var stat = r.Stats.GetValueOrDefault(role);
            string peak = stat?.Max is double max ? $" (hottest {Temp(max)}{At(r, stat.MaxAt)}{With(r, role)})" : "";
            yield return new(IconTemp, $"The {chip} held {Temp(limit)} or more for {Minutes(minutes)}{peak}.", InsightTone.Hot, 85);
        }
    }

    /// <summary>The same app ran clearly hotter or cooler than in the 30 days before: a like-for-like load.</summary>
    private static IEnumerable<Insight> AppTemperatures(Report r, IReadOnlyList<GameSessionSummary> _)
    {
        foreach (var app in r.Apps.Where(a => a.FgSec >= MinAppSeconds && a.GpuTempAvg is double).Take(10))
        {
            if (!r.AppsBefore.TryGetValue(app.App, out var was) || was.FgSec < MinAppSecondsBefore || was.GpuAvg is not double before) continue;
            double diff = app.GpuTempAvg!.Value - before;
            if (Math.Abs(diff) < AppTempDiff) continue;
            string word = diff > 0 ? "hotter" : "cooler";
            yield return new(IconCompare, $"In {app.Name} the GPU ran {Math.Abs(diff):0}° {word} than in the 30 days before " +
                                          $"({Temp(app.GpuTempAvg.Value)} vs {Temp(before)} on average).",
                             diff > 0 ? InsightTone.Hot : InsightTone.Good, 70);
        }
    }

    private static IEnumerable<Insight> Drivers(Report r, IReadOnlyList<GameSessionSummary> sessions)
    {
        foreach (var c in r.Changes.Where(c => c.Kind is ChangeKinds.Driver && c.Before != null && c.After != null))
        {
            var at = HistoryDb.Local(c.Ts);
            var effects = ChangeEffects.Of(at, null, sessions, top: 2);
            string after = effects.Count > 0 ? " Since then: " + string.Join("; ", effects.Select(ChangeEffects.Describe)) + "." : "";
            yield return new(IconDriver, $"{c.Subject} driver {c.Before} → {c.After} {When(r, at)}.{after}", InsightTone.Neutral, 60);
        }
    }

    private static IEnumerable<Insight> Usage(Report r, IReadOnlyList<GameSessionSummary> _)
    {
        if (r.OnMinutes < 15) yield break;
        var top = r.Apps.FirstOrDefault(a => a.FgSec >= 60);
        string app = top != null ? $"; {top.Name} was in front the longest ({Duration(top.FgSec)})" : "";
        yield return new(IconClock, $"Kelvra recorded {Duration(r.OnMinutes * 60.0)} of PC time{app}.", InsightTone.Neutral, 50);
    }

    private static IEnumerable<Insight> Games(Report r, IReadOnlyList<GameSessionSummary> _)
    {
        var played = r.Games.Where(g => !g.Benchmark).ToList();
        if (played.Count == 0) yield break;
        var best = played.Where(g => g.AvgFps is double).MaxBy(g => g.Seconds);
        string longest = best != null ? $" Longest: {best.Game}, {Duration(best.Seconds)} at {best.AvgFps:0} FPS average." : "";
        int crashed = played.Count(g => g.Crashed);
        string crashes = crashed > 0 ? $" {crashed} ended in a crash." : "";
        yield return new(IconGame, $"{played.Count} game session{(played.Count == 1 ? "" : "s")}.{longest}{crashes}",
                         crashed > 0 ? InsightTone.Warn : InsightTone.Neutral, 40);
    }

    private static IEnumerable<Insight> AllGood(Report r, IReadOnlyList<GameSessionSummary> _)
    {
        if (r.OnMinutes >= 60 && r.Crashes.Count == 0 && r.OverLimit.Count == 0 && r.FanSlowdowns.Count == 0)
            yield return new(IconGood, "No crashes, no overheating, fans as usual.", InsightTone.Good, 10);
    }

    // ---------- words ----------

    private static string Temp(double c) => SensorFormat.Format(SensorType.Temperature, (float)c);

    private static string When(Report r, DateTime t) =>
        // The time on its own: inside a custom format "t" is the AM/PM letter ("on Mon 12 Oct, P"), not the short time
        r.Period.Range == ReportRange.Day ? $"at {t.ToString("t", CultureInfo.CurrentCulture)}"
            : $"on {t.ToString("ddd d MMM", CultureInfo.CurrentCulture)}, {t.ToString("t", CultureInfo.CurrentCulture)}";

    private static string At(Report r, DateTime? t) => t is DateTime time
        ? r.Period.Range == ReportRange.Day ? $" at {time.ToString("t", CultureInfo.CurrentCulture)}" : $" on {time.ToString("ddd d MMM", CultureInfo.CurrentCulture)}"
        : "";

    private static string With(Report r, string role) => r.HottestWith.TryGetValue(role, out var app) ? $", {app} in front" : "";

    private static string Minutes(int minutes) => minutes >= 60 ? Duration(minutes * 60.0) : $"{minutes} min";

    /// <summary>"2 h 05 min", "45 min".</summary>
    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";
    }

    /// <summary>"7 h 40 m", "45 m": the tables' short form.</summary>
    public static string Short(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} m" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} m";
    }
}
