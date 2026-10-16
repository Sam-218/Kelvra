using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>
/// Which sensors the long-term history keeps, and the role reports know each by. One sensor per single role (the CPU's
/// package temperature, the main GPU's core temperature …), every fan, fan output and drive, plus whatever the user
/// watches: alert rules, pinned graphs, fan curve sources, overlay items.
/// </summary>
public static class HistoryRoles
{
    public const string CpuTemp = "cpu-temp", CpuLoad = "cpu-load", CpuPower = "cpu-power";
    public const string GpuTemp = "gpu-temp", GpuHotSpot = "gpu-hotspot", GpuLoad = "gpu-load", GpuPower = "gpu-power";
    public const string RamLoad = "ram-load", Fps = "fps", Fan = "fan", FanControl = "fan-control", DriveTemp = "drive-temp";

    /// <summary>Sensor id → role ("" for sensors kept only because the user watches them).</summary>
    public static Dictionary<string, string> Pick(IReadOnlyList<SensorVm> sensors, IEnumerable<string> watched)
    {
        var picked = new Dictionary<string, string>();
        void One(string role, params Func<SensorVm, bool>[] matchers)
        {
            foreach (var match in matchers)
                if (sensors.FirstOrDefault(s => match(s) && !picked.ContainsKey(s.Id)) is { } hit)
                {
                    picked[hit.Id] = role;
                    return;
                }
        }

        static bool Cpu(SensorVm s) => s.HardwareType == HardwareType.Cpu;
        // The graphics card that matters: a discrete one before an integrated one
        var gpu = sensors.Where(s => s.IsGpu).OrderBy(s => s.HardwareType == HardwareType.GpuIntel ? 1 : 0).ThenBy(s => s.HardwareOrder)
                         .Select(s => s.HardwareId).FirstOrDefault();
        bool Gpu(SensorVm s) => s.HardwareId == gpu;

        One(CpuTemp, s => Cpu(s) && s.Type == SensorType.Temperature && (s.Name.Contains("Package") || s.Name.Contains("Tctl") || s.Name.Contains("Tdie")),
                     s => Cpu(s) && s.Type == SensorType.Temperature);
        One(CpuLoad, s => Cpu(s) && s.Type == SensorType.Load && s.Name.Contains("Total"));
        One(CpuPower, s => Cpu(s) && s.Type == SensorType.Power && s.Name.Contains("Package"));
        One(GpuTemp, s => Gpu(s) && s.Type == SensorType.Temperature && s.Name.Contains("Core"),
                     s => Gpu(s) && s.Type == SensorType.Temperature);
        One(GpuHotSpot, s => Gpu(s) && s.Type == SensorType.Temperature && s.Name.Contains("Hot"));
        One(GpuLoad, s => Gpu(s) && s.Type == SensorType.Load && s.Name.Contains("Core"));
        One(GpuPower, s => Gpu(s) && s.Type == SensorType.Power && s.Name.Contains("Package"),
                      s => Gpu(s) && s.Type == SensorType.Power);
        One(RamLoad, s => s.HardwareType == HardwareType.Memory && s.Type == SensorType.Load && !s.HardwareName.Contains("Virtual"));
        One(Fps, s => s.Id == GameSensors.FpsId);

        foreach (var s in sensors)
        {
            if (picked.ContainsKey(s.Id)) continue;
            if (s.Type == SensorType.Fan) picked[s.Id] = Fan;
            else if (s.Type == SensorType.Control) picked[s.Id] = FanControl;
        }
        foreach (var drive in sensors.Where(s => s.HardwareType == HardwareType.Storage && s.Type == SensorType.Temperature)
                                     .GroupBy(s => s.HardwareId).Select(g => g.First()))
            picked.TryAdd(drive.Id, DriveTemp);

        var known = sensors.Select(s => s.Id).ToHashSet();
        foreach (var id in watched)
            if (known.Contains(id)) picked.TryAdd(id, "");
        return picked;
    }

    /// <summary>The fan output that drives a fan, by LibreHardwareMonitor's naming ("/lpc/nct6798d/0/fan/2" → ".../control/2").</summary>
    public static string ControlOf(string fanId) => fanId.Replace("/fan/", "/control/", StringComparison.Ordinal);
}
