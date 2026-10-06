using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>One cell of a device panel on the Overview page: "TEMP 48.2 °C, 46 – 55".</summary>
public sealed class StatCell : ObservableObject
{
    private string _value = "–";
    private string _range = "";
    private string _unit;

    public StatCell(string label, string unit, bool last)
    {
        Label = label;
        _unit = unit;
        // 1 px gap to the next cell; the panel's line colour shows through it
        Margin = last ? new Thickness(0) : new Thickness(0, 0, 1, 0);
    }

    public string Label { get; }
    public string Unit { get => _unit; set => Set(ref _unit, value); }
    public Thickness Margin { get; }
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Range { get => _range; set => Set(ref _range, value); }
}

/// <summary>A CPU or GPU on the Overview page: temperature, load, clock and power, plus which sensors the graph shows.</summary>
public sealed class DevicePanel : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public DevicePanel(string hardwareId, string kind, string device)
    {
        HardwareId = hardwareId;
        Kind = kind;
        Device = device;
        Stats = new ObservableCollection<StatCell>
        {
            new("TEMP", "°C", false), new("LOAD", "%", false), new("CLOCK", "MHz", false), new("POWER", "W", true),
        };
    }

    public string HardwareId { get; }
    /// <summary>"CPU" or "GPU" (upper case: shown as the panel's label).</summary>
    public string Kind { get; }
    public string Device { get; }
    public ObservableCollection<StatCell> Stats { get; }
    private string? _tempId, _loadId;
    public string? TempId { get => _tempId; private set => Set(ref _tempId, value); }
    public string? LoadId { get => _loadId; private set => Set(ref _loadId, value); }
    public string ChartName => $"{Kind} temperature and load";

    /// <summary>Re-reads the four values from the current sensors.</summary>
    public void Update(IReadOnlyList<SensorVm> sensors)
    {
        var own = sensors.Where(s => s.HardwareId == HardwareId).ToList();
        bool cpu = Kind == "CPU";

        var temp = cpu ? Overview.CpuTemperature(own) : Overview.Pick(own, SensorType.Temperature, "GPU Core");
        var load = cpu ? Overview.Pick(own, SensorType.Load, "CPU Total") : Overview.Pick(own, SensorType.Load, "GPU Core");
        var power = Overview.Pick(own, SensorType.Power, "Package", cpu ? "CPU" : "GPU Power", "GPU");
        TempId = temp?.Id;
        LoadId = load?.Id;

        Stats[0].Unit = SensorFormat.Unit(SensorType.Temperature);
        Show(Stats[0], temp, "0.0");
        Show(Stats[1], load, "0");
        Show(Stats[3], power, "0.0");

        if (cpu)
        {
            // CPUs report one clock per core: show the average, and the lowest/highest any core reached
            var cores = own.Where(s => s.Type == SensorType.Clock && Overview.IsCoreClock(s.Name) && s.Value > 0).ToList();
            if (cores.Count == 0) Show(Stats[2], null, "0");
            else
            {
                Stats[2].Value = cores.Average(c => c.Value!.Value).ToString("0", Inv);
                var mins = cores.Where(c => c.Min > 0).Select(c => c.Min!.Value).ToList();
                var maxs = cores.Where(c => c.Max > 0).Select(c => c.Max!.Value).ToList();
                Stats[2].Range = mins.Count > 0 && maxs.Count > 0 ? $"{mins.Min().ToString("0", Inv)} – {maxs.Max().ToString("0", Inv)}" : "";
            }
        }
        else
        {
            Show(Stats[2], Overview.Pick(own, SensorType.Clock, "GPU Core"), "0");
        }
    }

    private static void Show(StatCell cell, SensorVm? s, string format)
    {
        if (s?.Value is not float v || float.IsNaN(v))
        {
            cell.Value = "–";
            cell.Range = "";
            return;
        }
        string F(float x) => SensorFormat.ToDisplay(s.Type, x).ToString(format, Inv);
        cell.Value = F(v);
        cell.Range = s.Min is float lo && s.Max is float hi ? $"{F(lo)} – {F(hi)}" : "";
    }
}

/// <summary>A row of the Overview side panel (memory, drive, network) with a fill bar.</summary>
public sealed class SideStat : ObservableObject
{
    private string _value = "–";
    private string _detail = "";
    private double _percent;

    public SideStat(string label) => Label = label;

    public string Label { get; }
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    /// <summary>0–100 for the bar.</summary>
    public double Percent { get => _percent; set => Set(ref _percent, Math.Clamp(value, 0, 100)); }
}

/// <summary>One card of the mini window: a big value, a sparkline and three small values.</summary>
public sealed class MiniCard : ObservableObject
{
    private string _hero = "–";
    private string _heroUnit = "";
    private string? _sparkId;

    public MiniCard(string title, string device, params string[] statLabels)
    {
        Title = title;
        Device = device;
        Stats = new ObservableCollection<StatCell>(statLabels.Select((l, i) => new StatCell(l, "", i == statLabels.Length - 1)));
    }

    public string Title { get; }
    public string Device { get; }
    public ObservableCollection<StatCell> Stats { get; }
    public string Hero { get => _hero; set => Set(ref _hero, value); }
    public string HeroUnit { get => _heroUnit; set => Set(ref _heroUnit, value); }
    /// <summary>Sensor drawn as the sparkline (null = none).</summary>
    public string? SparkId { get => _sparkId; set => Set(ref _sparkId, value); }

    /// <summary>CPU/GPU card from its Overview panel: temperature big, then load, clock and power.</summary>
    public void From(DevicePanel p)
    {
        Hero = p.Stats[0].Value;
        HeroUnit = p.Stats[0].Unit;
        SparkId = p.TempId ?? p.LoadId;
        for (int i = 0; i < 3; i++) Stats[i].Value = $"{p.Stats[i + 1].Value} {p.Stats[i + 1].Unit}";
    }
}

/// <summary>Which sensors the Overview page shows. Pure functions over the sensor list, so they're easy to test.</summary>
public static class Overview
{
    public static bool IsCpu(SensorVm s) => s.HardwareType == HardwareType.Cpu;

    /// <summary>
    /// "Core #3" but not "Core #3 (Effective)": Ryzen also reports effective clocks, which sit far lower at idle and
    /// pulled the average down below the real cores' minimum (2246 MHz shown with a 4076–4100 range).
    /// </summary>
    public static bool IsCoreClock(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, @"^Core #\d+$");

    /// <summary>The first sensor of <paramref name="type"/> whose name contains one of the hints (in order), else the first of that type.</summary>
    public static SensorVm? Pick(IReadOnlyList<SensorVm> sensors, SensorType type, params string[] nameHints)
    {
        var ofType = sensors.Where(s => s.Type == type).ToList();
        foreach (var hint in nameHints)
        {
            var hit = ofType.FirstOrDefault(s => s.Name.Contains(hint, StringComparison.OrdinalIgnoreCase) && s.Value is not null);
            if (hit != null) return hit;
        }
        return ofType.FirstOrDefault(s => s.Value is not null);
    }

    /// <summary>The CPU temperature people expect: Tctl/Tdie (AMD) or Package (Intel), else the hottest core.</summary>
    public static SensorVm? CpuTemperature(IReadOnlyList<SensorVm> sensors)
    {
        var temps = sensors.Where(s => s.Type == SensorType.Temperature && s.Value > 0).ToList();
        foreach (var hint in new[] { "Tctl", "Package", "Core Max", "Core Average" })
        {
            var hit = temps.FirstOrDefault(s => s.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return temps.MaxBy(s => s.Value);
    }

    /// <summary>One panel per CPU and GPU, in detection order.</summary>
    public static List<DevicePanel> Panels(IReadOnlyList<SensorVm> sensors) =>
        sensors.Where(s => IsCpu(s) || s.IsGpu)
               .GroupBy(s => s.HardwareId)
               .Select(g => new DevicePanel(g.Key, IsCpu(g.First()) ? "CPU" : "GPU", g.First().GroupName))
               .ToList();

    /// <summary>Physical memory load and "12.4 of 32.0 GB in use" (the virtual memory device is skipped).</summary>
    public static void UpdateMemory(SideStat stat, IReadOnlyList<SensorVm> sensors)
    {
        var mem = sensors.Where(s => s.HardwareType == HardwareType.Memory && !s.GroupName.Contains("Virtual", StringComparison.OrdinalIgnoreCase)).ToList();
        var load = mem.FirstOrDefault(s => s.Type == SensorType.Load);
        var used = mem.FirstOrDefault(s => s.Type == SensorType.Data && s.Name.Contains("Used", StringComparison.OrdinalIgnoreCase));
        var free = mem.FirstOrDefault(s => s.Type == SensorType.Data && s.Name.Contains("Available", StringComparison.OrdinalIgnoreCase));
        stat.Value = load?.Value is float l ? $"{l.ToString("0", CultureInfo.InvariantCulture)} %" : "–";
        stat.Percent = load?.Value ?? 0;
        stat.Detail = used?.Value is float u && free?.Value is float f
            ? $"{u.ToString("0.0", CultureInfo.InvariantCulture)} of {(u + f).ToString("0.0", CultureInfo.InvariantCulture)} GB in use"
            : "";
    }

    /// <summary>Total download/upload over all network adapters; the bar is relative to the fastest seen so far.</summary>
    public static void UpdateNetwork(SideStat stat, IReadOnlyList<SensorVm> sensors, ref float peak)
    {
        var net = sensors.Where(s => s.HardwareType == HardwareType.Network && s.Type == SensorType.Throughput).ToList();
        float down = net.Where(s => s.Name.Contains("Download", StringComparison.OrdinalIgnoreCase)).Sum(s => s.Value ?? 0);
        float up = net.Where(s => s.Name.Contains("Upload", StringComparison.OrdinalIgnoreCase)).Sum(s => s.Value ?? 0);
        peak = Math.Max(peak, Math.Max(down, up));
        stat.Value = $"↓ {SensorFormat.Format(SensorType.Throughput, down)}";
        stat.Detail = $"↑ {SensorFormat.Format(SensorType.Throughput, up)}";
        stat.Percent = peak > 0 ? down / peak * 100 : 0;
    }

    public static void UpdateDrive(SideStat stat, string root)
    {
        try
        {
            var d = new DriveInfo(root);
            if (!d.IsReady) return;
            long used = d.TotalSize - d.TotalFreeSpace;
            stat.Value = $"{100.0 * used / d.TotalSize:0} %";
            stat.Percent = 100.0 * used / d.TotalSize;
            stat.Detail = $"{ByteFormat.Format(d.TotalFreeSpace)} free of {ByteFormat.Format(d.TotalSize)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            stat.Detail = "Not available";
        }
    }
}
