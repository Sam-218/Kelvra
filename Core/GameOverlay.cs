using System.Globalization;

namespace Kelvra;

/// <summary>
/// The gaming overlay's building blocks. Each module is a set of overlay items (Game sensors or "extra:game:" texts and
/// the frametime graph) that the "Gaming modules" switches add and remove as a unit, always in the same order.
/// </summary>
public static class GameOverlay
{
    public const string Group = "Game";

    public const string FrameTimesId = "extra:game:frametimes";
    public const string RangeId = "extra:game:range";
    public const string StuttersId = "extra:game:stutters";
    public const string BoundId = "extra:game:bound";
    public const string NameId = "extra:game:name";

    /// <summary>"extra:game:" items (texts and the graph), with their default labels.</summary>
    public static readonly (string Id, string Name)[] Extras =
    {
        (FrameTimesId, "Frametime graph"),
        (RangeId, "Avg / min / max"),
        (StuttersId, "Stutters"),
        (BoundId, "Bottleneck"),
        (NameId, "Game name"),
    };

    public sealed record Module(string Key, string Name, string Description, string[] Ids);

    /// <summary>The switches in display order. "hardware" gets its sensors from what was detected (see <see cref="HardwareIds"/>).</summary>
    public static readonly Module[] Modules =
    {
        new("fps", "FPS", "Frames per second right now.", new[] { GameSensors.FpsId }),
        new("frametime", "Frametime", "Milliseconds per frame (lower is smoother).", new[] { GameSensors.FrameTimeId }),
        new("lows", "1% and 0.1% lows", "FPS of the slowest frames over the last minute – how bad the dips get.", new[] { GameSensors.Low1Id, GameSensors.Low01Id }),
        new("range", "Average / min / max", "FPS over this game session.", new[] { RangeId }),
        new("graph", "Frametime graph", "Every frame of the last 10 seconds; spikes are stutters.", new[] { FrameTimesId }),
        new("stutters", "Stutters", "Frames that took much longer than the ones around them.", new[] { StuttersId }),
        new("bound", "Bottleneck", "Whether the GPU or the CPU limits the frame rate, or a limiter / VSync caps it.", new[] { BoundId }),
        new("latency", "Latency", "How old a frame is when it reaches the screen (from the start of the frame until it's shown).", new[] { GameSensors.LatencyId }),
        new("name", "Game name", "Which game is being measured (shows ● REC while a benchmark records).", new[] { NameId }),
        new("session", "Play time", "How long this game has been running.", new[] { GameSensors.SessionId }),
        new("hardware", "CPU and GPU", "Load and temperature of the CPU and GPU, and VRAM.", Array.Empty<string>()),
    };

    /// <summary>Modules a new gaming overlay starts with.</summary>
    public static readonly string[] DefaultModules = { "fps", "lows", "graph", "bound", "hardware" };

    public static string? NameOf(string id) => Extras.FirstOrDefault(e => e.Id == id).Name;

    public static bool IsExtra(string id) => id.StartsWith("extra:game:", StringComparison.Ordinal);

    /// <summary>The detected CPU/GPU sensors for the "CPU and GPU" module.</summary>
    public static string[] HardwareIds(IReadOnlyList<SensorVm> sensors) => OverlayTemplates.GamingHardware(sensors);

    public static string[] IdsOf(Module m, IReadOnlyList<SensorVm> sensors) => m.Key == "hardware" ? HardwareIds(sensors) : m.Ids;

    public static bool IsOn(OverlayProfile p, Module m, IReadOnlyList<SensorVm> sensors) => IdsOf(m, sensors).Any(p.ContainsSensor);

    /// <summary>Adds or removes a module's items. New items go right after the items of the modules listed before it.</summary>
    public static void Set(OverlayProfile p, Module m, bool on, IReadOnlyList<SensorVm> sensors)
    {
        var ids = IdsOf(m, sensors);
        if (!on)
        {
            foreach (var item in p.Items.Where(i => ids.Contains(i.SensorId)).ToList()) p.Items.Remove(item);
            return;
        }

        int index = Array.IndexOf(Modules, m);
        var earlier = Modules.Take(index).SelectMany(x => IdsOf(x, sensors)).ToHashSet();
        int at = 0;
        for (int i = 0; i < p.Items.Count; i++)
            if (earlier.Contains(p.Items[i].SensorId)) at = i + 1;

        foreach (var id in ids)
        {
            if (p.ContainsSensor(id)) continue;
            p.Items.Insert(at++, new OverlayItem { SensorId = id, Label = DefaultLabel(id) });
        }
    }

    /// <summary>Short labels that fit a game overlay ("GPU" instead of "GPU Core Load").</summary>
    private static string? DefaultLabel(string id) => id switch
    {
        GameSensors.FpsId => "FPS",
        GameSensors.Low1Id => "1% low",
        GameSensors.Low01Id => "0.1% low",
        GameSensors.SessionId => "Play time",
        _ => null,
    };

    // ---------- texts ----------

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The text of an "extra:game:" item for the current frame numbers ("-" while no game runs).</summary>
    public static string Text(string id, FrameSnapshot s, string? gameName)
    {
        if (gameName == null) return "-";
        return id switch
        {
            NameId => gameName,
            RangeId => s.AvgFps is double avg
                ? $"{F(avg)} · {F(s.MinFps)} – {F(s.MaxFps)}"
                : "-",
            StuttersId => s.Stutters == 0 ? "none"
                : s.SecondsSinceStutter is double ago ? $"{s.Stutters} · last {Ago(ago)} ago" : s.Stutters.ToString(Inv),
            BoundId => s.Bound switch
            {
                Bottleneck.GpuBound => $"GPU-bound · {F(s.GpuBusyPercent)} %",
                Bottleneck.CpuBound => $"CPU-bound · GPU {F(s.GpuBusyPercent)} %",
                Bottleneck.Capped => "Capped (limiter / VSync)",
                _ => "-",
            },
            _ => "-",
        };
    }

    private static string F(double? v) => v is double d ? d.ToString("0", Inv) : "-";

    private static string Ago(double seconds) =>
        seconds < 60 ? $"{seconds.ToString("0", Inv)} s" : $"{(seconds / 60).ToString("0", Inv)} min";

    /// <summary>Plausible numbers for the editor preview while no game runs, so the layout can be judged.</summary>
    public static readonly FrameSnapshot Demo = new(
        Fps: 142, FrameTimeMs: 7.0, Low1: 98, Low01: 71, AvgFps: 138, MinFps: 64, MaxFps: 165,
        Stutters: 3, SecondsSinceStutter: 42, GpuBusyPercent: 97, Bound: Bottleneck.GpuBound,
        LatencyMs: 18.5, PresentMode: "Hardware: Independent Flip", SessionLength: TimeSpan.FromMinutes(47));
}
