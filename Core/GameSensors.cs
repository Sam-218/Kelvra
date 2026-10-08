using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>
/// The "Game" device in the sensor list: FPS, frame times, lows, bottleneck and latency of the game being played,
/// as ordinary sensors, so overlays, History, alerts and CSV logging work with them. Values are empty with no game.
/// </summary>
public static class GameSensors
{
    /// <summary>Kelvra's own sensor types (LibreHardwareMonitor has none for frames). Values far above its enum range.</summary>
    public const SensorType Fps = (SensorType)200;
    public const SensorType Milliseconds = (SensorType)201;
    public const SensorType Count = (SensorType)202;
    public const HardwareType Hardware = (HardwareType)200;

    public const string HardwareId = "/game";
    public const string DeviceName = "Game";

    public const string FpsId = "/game/fps";
    public const string FrameTimeId = "/game/frametime";
    public const string Low1Id = "/game/low1";
    public const string Low01Id = "/game/low01";
    public const string AvgId = "/game/avg";
    public const string StuttersId = "/game/stutters";
    public const string GpuBusyId = "/game/gpubusy";
    public const string LatencyId = "/game/latency";
    public const string SessionId = "/game/session";

    public static bool IsGameSensor(string id) => id.StartsWith(HardwareId + "/", StringComparison.Ordinal);

    /// <summary>Readings for one poll. <paramref name="s"/> is <see cref="FrameSnapshot.Empty"/> when no game runs.</summary>
    public static List<SensorReading> Readings(FrameSnapshot s, bool gameActive)
    {
        float? F(double? v) => gameActive && v is double d && double.IsFinite(d) ? (float)d : null;

        SensorReading R(string id, string name, SensorType type, float? value, float? min = null, float? max = null) =>
            new(id, HardwareId, DeviceName, Hardware, name, type, value, min, max);

        return new List<SensorReading>
        {
            R(FpsId, "FPS", Fps, F(s.Fps), F(s.MinFps), F(s.MaxFps)),
            R(FrameTimeId, "Frametime", Milliseconds, F(s.FrameTimeMs)),
            R(Low1Id, "1% low", Fps, F(s.Low1)),
            R(Low01Id, "0.1% low", Fps, F(s.Low01)),
            R(AvgId, "Average FPS", Fps, F(s.AvgFps)),
            R(StuttersId, "Stutter count", Count, gameActive ? s.Stutters : null),
            R(GpuBusyId, "GPU busy", SensorType.Level, F(s.GpuBusyPercent)),
            R(LatencyId, "Latency", Milliseconds, F(s.LatencyMs)),
            R(SessionId, "Play time", SensorType.TimeSpan, gameActive ? (float)s.SessionLength.TotalSeconds : null),
        };
    }
}
