using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kelvra;

/// <summary>What one game session (or benchmark run) looked like, kept in History → Game sessions.</summary>
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
    string? CsvFile)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Builds the summary from a game's frame statistics.</summary>
    public static GameSessionSummary From(string game, DateTime started, FrameStats stats, float? maxCpuTemp, float? maxGpuTemp,
                                          bool benchmark = false, string? csvFile = null)
    {
        var s = stats.Session();
        var snap = stats.Snapshot(); // only the session values are used
        return new GameSessionSummary(game, started, s.Length.TotalSeconds, s.Avg, s.Low1, s.Low01, snap.MinFps, snap.MaxFps,
                                      s.Stutters, s.GpuBoundSeconds, s.CpuBoundSeconds, s.CappedSeconds, maxCpuTemp, maxGpuTemp,
                                      benchmark, csvFile);
    }

    // ---------- display (History page) ----------

    [JsonIgnore] public string Title => Benchmark ? $"{Game} · Benchmark" : Game;

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
        w.Write("TimeMs,FrameTimeMs,FPS,CpuBusyMs,GpuBusyMs,LatencyMs,Displayed\r\n");
        double time = 0; // frame-time timeline from the first frame (independent of PresentMon's time column)
        static string O(double? v) => v is double d ? d.ToString("0.###", CultureInfo.InvariantCulture) : "";
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (i > 0) time += f.FrameTimeMs;
            w.Write($"{O(time)},{O(f.FrameTimeMs)},{O(1000 / f.FrameTimeMs)},{O(f.CpuBusyMs)},{O(f.GpuBusyMs)},{O(f.LatencyMs)},{(f.Displayed ? "1" : "0")}\r\n");
        }
    }
}

/// <summary>One frame of a benchmark, kept compact (~32 bytes instead of a whole parsed CSV line): a run can hold millions.</summary>
public readonly record struct BenchFrame(float FrameTimeMs, float? CpuBusyMs, float? GpuBusyMs, float? LatencyMs, bool Displayed)
{
    public static BenchFrame From(FrameSample s) =>
        new((float)s.FrameTimeMs, (float?)s.CpuBusyMs, (float?)s.GpuBusyMs, (float?)s.LatencyMs, s.Displayed);
}
