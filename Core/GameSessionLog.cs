using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kelvra;

/// <summary>What one game session (or benchmark run) looked like, kept on the Games page.</summary>
public sealed record GameSessionSummary(
    string Game,
    DateTime Started,
    double Seconds,
    double? AvgFps,
    double? Low1,
    double? Low01,
    double? MinFps,
    double? MaxFps,
    int Stutters,
    int GpuBoundSeconds,
    int CpuBoundSeconds,
    int CappedSeconds,
    float? MaxCpuTemp,
    float? MaxGpuTemp,
    bool Benchmark,
    string? CsvFile,
    string? Exe = null,
    bool Crashed = false)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Builds the summary from a game's frame statistics.</summary>
    public static GameSessionSummary From(string game, DateTime started, FrameStats stats, float? maxCpuTemp, float? maxGpuTemp,
                                          bool benchmark = false, string? csvFile = null, string? exe = null)
    {
        var s = stats.Session();
        var snap = stats.Snapshot(); // only the session values are used
        return new GameSessionSummary(game, started, s.Length.TotalSeconds, s.Avg, s.Low1, s.Low01, snap.MinFps, snap.MaxFps,
                                      s.Stutters, s.GpuBoundSeconds, s.CpuBoundSeconds, s.CappedSeconds, maxCpuTemp, maxGpuTemp,
                                      benchmark, csvFile, exe);
    }

    // ---------- display (Games page) ----------

    [JsonIgnore] public string Title => Benchmark ? $"{Game} · Benchmark" : Crashed ? $"{Game} · crashed" : Game;

    [JsonIgnore] public string When
    {
        get
        {
            string day = Started.Date == DateTime.Today ? "Today" : Started.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : Started.ToString("d");
            var length = TimeSpan.FromSeconds(Seconds);
            string duration = length.TotalHours >= 1 ? $"{(int)length.TotalHours} h {length.Minutes} min"
                : length.TotalMinutes >= 1 ? $"{(int)length.TotalMinutes} min" : $"{length.Seconds} s";
            return $"{day} {Started:t} · {duration}";
        }
    }

    [JsonIgnore] public string FpsLine => AvgFps is double avg
        ? $"Avg {N(avg)} FPS · 1% low {N(Low1)} · 0.1% low {N(Low01)}"
        : "No frames were measured";

    [JsonIgnore] public string DetailLine
    {
        get
        {
            var parts = new List<string>();
            if (MinFps is double min && MaxFps is double max) parts.Add($"Min {N(min)} · Max {N(max)}");
            parts.Add(Stutters == 1 ? "1 stutter" : $"{Stutters} stutters");
            int bound = GpuBoundSeconds + CpuBoundSeconds + CappedSeconds;
            if (bound > 0)
            {
                var (name, secs) = new[] { ("GPU-bound", GpuBoundSeconds), ("CPU-bound", CpuBoundSeconds), ("capped", CappedSeconds) }.MaxBy(x => x.Item2);
                parts.Add($"{name} {secs * 100 / bound} % of the time");
            }
            if (MaxCpuTemp is float cpu) parts.Add($"CPU max {SensorFormat.Format(LibreHardwareMonitor.Hardware.SensorType.Temperature, cpu)}");
            if (MaxGpuTemp is float gpu) parts.Add($"GPU max {SensorFormat.Format(LibreHardwareMonitor.Hardware.SensorType.Temperature, gpu)}");
            return string.Join(" · ", parts);
        }
    }

    [JsonIgnore] public bool HasCsv => CsvFile != null && File.Exists(CsvFile);

    private static string N(double? v) => v is double d ? d.ToString("0", Inv) : "-";
}

/// <summary>One game on the Games page: all its sessions and benchmark runs (newest first) and their numbers together.</summary>
public sealed record GameGroup(string Name, List<GameSessionSummary> Sessions, GameRow? Stats)
{
    /// <summary>One card per game, the most recently played first. Names match regardless of case.</summary>
    public static List<GameGroup> From(IEnumerable<GameSessionSummary> sessions) =>
        sessions.GroupBy(s => s.Game, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var list = g.OrderByDescending(s => s.Started).ToList();
                    return new GameGroup(list[0].Game, list, ReportSummary.Games(list).FirstOrDefault()); // stats leave benchmarks out
                })
                .OrderByDescending(g => g.Sessions[0].Started).ToList();

    public DateTime LastPlayed => Sessions[0].Started;
    public int Crashes => Sessions.Count(s => s.Crashed);
    public bool HasCrashes => Crashes > 0;
    public bool HasHeldBy => Stats?.HasHeldBy == true;
    public string Avg => Stats?.Avg ?? "–";
    public string Low => Stats?.Low ?? "–";
    public string HeldBy => Stats?.HeldBy ?? "";

    /// <summary>"12 sessions · 6 h 40 min played · last Yesterday 8:42 PM".</summary>
    public string Summary
    {
        get
        {
            int runs = Sessions.Count(s => s.Benchmark), played = Sessions.Count - runs;
            var parts = new List<string>();
            if (played > 0) parts.Add(played == 1 ? "1 session" : $"{played} sessions");
            if (runs > 0) parts.Add(runs == 1 ? "1 benchmark" : $"{runs} benchmarks");
            if (Stats != null) parts.Add($"{Stats.Played} played");
            string day = LastPlayed.Date == DateTime.Today ? "Today" : LastPlayed.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : LastPlayed.ToString("d");
            parts.Add($"last {day} {LastPlayed:t}");
            return string.Join(" · ", parts);
        }
    }

    public string CrashText => Crashes == 1 ? "1 crash" : $"{Crashes} crashes";
}

/// <summary>
/// The last <see cref="Keep"/> game sessions as JSON lines in %APPDATA%\Kelvra\game-sessions.jsonl, newest last.
/// A bad line (hand-edited, cut off by a crash) is skipped rather than losing the whole list.
/// </summary>
public static class GameSessionLog
{
    public static readonly string DefaultFile = Path.Combine(AppSettings.Dir, "game-sessions.jsonl");
    internal const int Keep = 200;

    /// <summary>Sessions shorter than this aren't logged (a launcher or an app briefly mistaken for a game).</summary>
    public static readonly TimeSpan MinLength = TimeSpan.FromSeconds(30);

    public static void Append(GameSessionSummary summary, string path)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Length > 0).ToList() : new List<string>();
        lines.Add(JsonSerializer.Serialize(summary));
        if (lines.Count > Keep) lines.RemoveRange(0, lines.Count - Keep);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Newest first.</summary>
    public static List<GameSessionSummary> Load(string path)
    {
        var list = new List<GameSessionSummary>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                if (line.Length > 0 && JsonSerializer.Deserialize<GameSessionSummary>(line) is { Game: not null } s) list.Add(s);
            }
            catch (JsonException)
            {
                // skip it
            }
        }
        list.Reverse();
        return list;
    }

    /// <summary>
    /// Every frame of a benchmark run as CSV (times in ms from the first frame), for Excel or CapFrameX-style tools.
    /// Streamed line by line: a long run has millions of frames.
    /// </summary>
    public static void WriteFrames(string path, IReadOnlyList<BenchFrame> frames)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new StreamWriter(path, append: false, new UTF8Encoding(false), bufferSize: 1 << 16);
        // DisplayedMs: screen update to screen update (the frame pacing you see; use it with frame generation).
        // Generated: made by the driver's frame generation; it repeats the FrameTimeMs of the present it belongs to.
        w.Write("TimeMs,FrameTimeMs,FPS,CpuBusyMs,GpuBusyMs,LatencyMs,Displayed,DisplayedMs,Generated\r\n");
        double time = 0; // frame-time timeline from the first frame (independent of PresentMon's time column)
        static string O(double? v) => v is double d ? d.ToString("0.###", CultureInfo.InvariantCulture) : "";
        bool first = true;
        foreach (var f in frames)
        {
            // A generated frame shares its present: the time moves on with the game's next own frame
            if (!first && !f.Generated) time += f.FrameTimeMs;
            first = false;
            w.Write($"{O(time)},{O(f.FrameTimeMs)},{O(1000 / f.FrameTimeMs)},{O(f.CpuBusyMs)},{O(f.GpuBusyMs)},{O(f.LatencyMs)}," +
                    $"{(f.Displayed ? "1" : "0")},{O(f.DisplayedMs)},{(f.Generated ? "1" : "0")}\r\n");
        }
    }
}

/// <summary>One frame of a benchmark, kept compact (~40 bytes instead of a whole parsed CSV line): a run can hold millions.</summary>
public readonly record struct BenchFrame(float FrameTimeMs, float? CpuBusyMs, float? GpuBusyMs, float? LatencyMs, bool Displayed,
                                         float? DisplayedMs = null, bool Generated = false)
{
    public static BenchFrame From(FrameSample s) =>
        new((float)s.FrameTimeMs, (float?)s.CpuBusyMs, (float?)s.GpuBusyMs, (float?)s.LatencyMs, s.Displayed, (float?)s.DisplayedMs, s.Generated);
}
