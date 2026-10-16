using System.Globalization;

namespace Kelvra;

/// <summary>
/// One frame, as PresentMon reports it: a frame the game presented, or (with frame generation in the driver) a frame
/// generated between two of them. Times are in milliseconds; null = "NA" (not measured).
/// </summary>
/// <param name="TimeMs">PresentMon's time column (ms since it started); only used to notice frames that arrive late.</param>
/// <param name="FrameTimeMs">Present to present. A generated frame repeats the interval of the present it belongs to.</param>
/// <param name="DisplayedMs">Screen update to screen update: how long the previous frame stayed on screen. Null if not shown.</param>
/// <param name="FrameType">"Application", or who generated it ("AMD AFMF", "Intel XeSS-FG"); "" without the column.</param>
/// <param name="PcLatencyMs">Input to screen as NVIDIA Reflex measures it, in games that report it.</param>
public sealed record FrameSample(
    int ProcessId,
    ulong SwapChain,
    int SyncInterval,
    string PresentMode,
    double TimeMs,
    double FrameTimeMs,
    double? CpuBusyMs,
    double? GpuBusyMs,
    double? UntilDisplayedMs,
    double? DisplayedMs = null,
    string FrameType = "",
    double? PcLatencyMs = null)
{
    /// <summary>Shown on screen (dropped frames have no display time).</summary>
    public bool Displayed => UntilDisplayedMs is not null;

    /// <summary>
    /// Made by frame generation in the driver (AMD Fluid Motion Frames, Intel XeSS-FG), not rendered by the game.
    /// DLSS / FSR frame generation inside a game presents its frames like the game's own, so they aren't marked.
    /// </summary>
    public bool Generated => FrameType.Length > 0 && FrameType is not ("Application" or "NotSet" or "Repeated");

    /// <summary>
    /// How old the frame is when it reaches the screen. NVIDIA Reflex's PC latency (input → screen) where the game
    /// reports it; otherwise frame start → display (CPU time until Present + Present until shown). Null for frames that
    /// weren't shown, and for generated ones without Reflex numbers (they have no CPU time of their own).
    /// </summary>
    public double? LatencyMs => PcLatency ?? EstimatedLatencyMs;

    /// <summary>Reflex's PC latency of a shown frame, or null.</summary>
    public double? PcLatency => Displayed && PcLatencyMs is double pcl && pcl > 0 ? pcl : null;

    /// <summary>Frame start → display, worked out from PresentMon's CPU and display times.</summary>
    public double? EstimatedLatencyMs => !Generated && CpuBusyMs is double c && UntilDisplayedMs is double d ? c + d : null;
}

/// <summary>
/// Reads PresentMon's CSV output (2.x default metrics). Columns are found by name from the header line, so a newer
/// PresentMon that adds or reorders columns still works. Only the columns Kelvra uses are read.
/// </summary>
public sealed class PresentMonCsv
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly int _pid, _swap, _sync, _mode, _time, _frameTime, _cpuBusy, _gpuBusy, _untilDisplayed, _displayed, _frameType,
                         _pcLatency, _columns;

    private PresentMonCsv(Dictionary<string, int> col, int columns)
    {
        int Find(params string[] names)
        {
            foreach (var n in names)
                if (col.TryGetValue(n, out int i)) return i;
            return -1;
        }

        _pid = Find("ProcessID");
        _swap = Find("SwapChainAddress");
        _sync = Find("SyncInterval");
        _mode = Find("PresentMode");
        // Milliseconds since PresentMon started (2.6 writes "TimeInMs"); only used to notice frames that arrive late
        _time = Find("TimeInMs", "CPUStartTimeInMs", "CPUStartTime", "CPUStartQPCTime");
        _frameTime = Find("MsBetweenPresents", "FrameTime");
        _cpuBusy = Find("MsCPUBusy", "CPUBusy");
        _gpuBusy = Find("MsGPUBusy", "GPUBusy");
        _untilDisplayed = Find("MsUntilDisplayed");
        _displayed = Find("MsBetweenDisplayChange", "DisplayedTime");
        _frameType = Find("FrameType");
        _pcLatency = Find("MsPCLatency", "PCLatency");
        _columns = columns;
    }
    /// <summary>The parser for this header line, or null if it isn't a PresentMon header.</summary>
    public static PresentMonCsv? FromHeader(string line)
    {
        var names = line.TrimStart('﻿').Split(',');
        var col = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++) col.TryAdd(names[i].Trim(), i);
        if (!col.ContainsKey("Application") || !col.ContainsKey("ProcessID") ||
            !(col.ContainsKey("MsBetweenPresents") || col.ContainsKey("FrameTime"))) return null;
        return new PresentMonCsv(col, names.Length);
    }

    /// <summary>The line has the header's number of columns (so a null from <see cref="Parse"/> is just a zero-length present).</summary>
    public bool IsComplete(string line) => line.AsSpan().Count(',') == _columns - 1;

    // The last present mode and frame type seen: lines repeat the same few values, so they're reused instead of
    // allocating a new string per frame. Only the reader thread parses.
    private string _lastMode = "", _lastType = "";

    /// <summary>
    /// One frame, or null for lines that aren't one (a repeated header, a status message, a cut-off line).
    /// Hot path: a line arrives for every frame of every program PresentMon sees, often thousands a second. The columns
    /// are found as ranges of the line and parsed in place, instead of string.Split's array and ~30 strings per line:
    /// 1.4x faster and 92 % less garbage (measured: 1.7 KB → 144 bytes per line, the record itself).
    /// </summary>
    public FrameSample? Parse(string line)
    {
        // One slot more than the header has columns: a line with extra columns fills it and is refused, as before
        Span<Range> f = _columns < 128 ? stackalloc Range[_columns + 1] : new Range[_columns + 1];
        var t = line.AsSpan();
        if (t.Split(f, ',') != _columns) return null;

        if (!int.TryParse(t[f[_pid]], NumberStyles.Integer, Inv, out int pid)) return null;
        if (Num(t, f, _frameTime) is not double frameTime || frameTime <= 0 || frameTime > 60_000) return null;

        return new FrameSample(pid, Hex(t, f, _swap), (int)(Num(t, f, _sync) ?? 0), Str(t, f, _mode, ref _lastMode), Num(t, f, _time) ?? 0,
                               frameTime, Num(t, f, _cpuBusy), Num(t, f, _gpuBusy), Num(t, f, _untilDisplayed),
                               Num(t, f, _displayed) is double shown && shown > 0 ? shown : null, Str(t, f, _frameType, ref _lastType),
                               Num(t, f, _pcLatency));
    }

    /// <summary>Column <paramref name="i"/> trimmed, as a string: the same object as last time when it hasn't changed.</summary>
    private static string Str(ReadOnlySpan<char> line, ReadOnlySpan<Range> f, int i, ref string last)
    {
        if (i < 0) return "";
        var value = line[f[i]].Trim();
        if (!value.SequenceEqual(last)) last = value.ToString();
        return last;
    }

    private static double? Num(ReadOnlySpan<char> line, ReadOnlySpan<Range> f, int i)
    {
        if (i < 0) return null;
        return double.TryParse(line[f[i]], NumberStyles.Float, Inv, out double v) && double.IsFinite(v) ? v : null;
    }

    private static ulong Hex(ReadOnlySpan<char> line, ReadOnlySpan<Range> f, int i)
    {
        if (i < 0) return 0;
        var s = line[f[i]].Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ulong.TryParse(s, NumberStyles.HexNumber, Inv, out ulong v) ? v : 0;
    }
}
