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
    private string? _tempId, _loadId, _clockId, _powerId;
    public string? TempId { get => _tempId; private set => Set(ref _tempId, value); }
    public string? LoadId { get => _loadId; private set => Set(ref _loadId, value); }
    public string? ClockId { get => _clockId; private set => Set(ref _clockId, value); }
    public string? PowerId { get => _powerId; private set => Set(ref _powerId, value); }
    public string ChartName => $"{Kind} temperature and load";
    public bool IsGpu => Kind == "GPU";

    // ---------- Kelvra 2: heat first ----------

    private string _hero = "–", _rangeText = "", _headroomText = "", _stateWord = "Normal";
    private Heat _heat = Heat.Fine;
    private double _temp = double.NaN, _rangeMin = double.NaN, _rangeMax = double.NaN, _limit = Thermal.CpuLimit;

    /// <summary>The temperature, big ("61").</summary>
    public string Hero { get => _hero; private set => Set(ref _hero, value); }
    /// <summary>Its band on the thermal scale: the colour of the number, the line and the state pill.</summary>
    public Heat Heat { get => _heat; private set => Set(ref _heat, value); }
    /// <summary>"Normal", "Warm", "Hot" or "Critical".</summary>
    public string StateWord { get => _stateWord; private set => Set(ref _stateWord, value); }
    /// <summary>"54–68° past hour".</summary>
    public string RangeText { get => _rangeText; private set => Set(ref _rangeText, value); }
    /// <summary>"34° to the 95 °C limit".</summary>
    public string HeadroomText { get => _headroomText; private set => Set(ref _headroomText, value); }
    /// <summary>°C now, the lowest and highest of the graph's range, and the limit (the user's alert, else the chip's usual one).</summary>
    public double Temp { get => _temp; private set => Set(ref _temp, value); }
    public double RangeMin { get => _rangeMin; private set => Set(ref _rangeMin, value); }
    public double RangeMax { get => _rangeMax; private set => Set(ref _rangeMax, value); }
    public double Limit { get => _limit; private set => Set(ref _limit, value); }

    /// <summary>The heat values, from the live temperature and the history of the shown range.</summary>
    public void UpdateHeat(IReadOnlyDictionary<string, SensorVm> byId, HistoryService history, TimeSpan range, IEnumerable<AlertRule> alerts)
    {
        var temp = TempId != null ? byId.GetValueOrDefault(TempId) : null;
        Limit = Thermal.LimitFor(TempId ?? "", IsGpu, alerts);
        if (temp?.Value is not float t || float.IsNaN(t))
        {
            // No reading (CPU temperatures need admin rights and the PawnIO driver): say so instead of "Normal"
            (Hero, Temp, RangeText, HeadroomText, StateWord, Heat) = ("–", double.NaN, "No temperature reading", "", "No reading", Heat.Fine);
            return;
        }
        Temp = t;
        Heat = Thermal.Of(t, Limit);
        StateWord = Thermal.StateWord(Heat);
        Hero = SensorFormat.ToDisplay(SensorType.Temperature, t).ToString("0", Inv);

        var points = new List<(DateTime Time, float Value)>();
        history.GetSeries(TempId!, range, points);
        var values = points.Select(p => p.Value).Where(v => !float.IsNaN(v)).ToList();
        (RangeMin, RangeMax) = values.Count > 0 ? (values.Min(), values.Max()) : (t, t);
        string span = range.TotalMinutes >= 60 ? range.TotalHours >= 24 ? "past day" : range.TotalHours > 1 ? $"past {range.TotalHours:0} hours" : "past hour"
                                               : $"past {range.TotalMinutes:0} min";
        RangeText = $"{Deg(RangeMin)}–{Deg(RangeMax)}° {span}";
        double left = Limit - t;
        HeadroomText = left > 0
            ? $"{Deg(left, difference: true)}° to the {SensorFormat.Format(SensorType.Temperature, (float)Limit)} limit"
            : $"At the {SensorFormat.Format(SensorType.Temperature, (float)Limit)} limit";
    }

    /// <summary>A temperature (or a difference of two) in the unit shown, rounded.</summary>
    private static string Deg(double celsius, bool difference = false) =>
        (difference ? (SensorFormat.UseFahrenheit ? celsius * 9 / 5 : celsius) : SensorFormat.ToDisplay(SensorType.Temperature, (float)celsius)).ToString("0", Inv);

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
        PowerId = power?.Id;

        Stats[0].Unit = SensorFormat.Unit(SensorType.Temperature);
        Show(Stats[0], temp, "0.0");
        Show(Stats[1], load, "0");
        Show(Stats[3], power, "0");

        if (cpu)
        {
            // CPUs report one clock per core: show the average in GHz, and the lowest/highest any core reached
            var cores = own.Where(s => s.Type == SensorType.Clock && Overview.IsCoreClock(s.Name) && s.Value > 0).ToList();
            ClockId = cores.FirstOrDefault()?.Id;
            Stats[2].Unit = "GHz";
            if (cores.Count == 0) Show(Stats[2], null, "0");
            else
            {
                Stats[2].Value = (cores.Average(c => c.Value!.Value) / 1000).ToString("0.0", Inv);
                var mins = cores.Where(c => c.Min > 0).Select(c => c.Min!.Value).ToList();
                var maxs = cores.Where(c => c.Max > 0).Select(c => c.Max!.Value).ToList();
                Stats[2].Range = mins.Count > 0 && maxs.Count > 0 ? $"{(mins.Min() / 1000).ToString("0.0", Inv)} – {(maxs.Max() / 1000).ToString("0.0", Inv)}" : "";
            }
        }
        else
        {
            var clock = Overview.Pick(own, SensorType.Clock, "GPU Core");
            ClockId = clock?.Id;
            Show(Stats[2], clock, "0");
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
    private string? _sparkId;

    public SideStat(string label, string icon = "")
    {
        Label = label;
        Icon = icon;
    }

    public string Label { get; }
    /// <summary>Segoe Fluent icon glyph next to the label.</summary>
    public string Icon { get; }
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    /// <summary>0–100 for the bar.</summary>
    public double Percent { get => _percent; set => Set(ref _percent, Math.Clamp(value, 0, 100)); }
    /// <summary>A sensor drawn as a line instead of the bar (network), or null.</summary>
    public string? SparkId { get => _sparkId; set => Set(ref _sparkId, value); }
    public bool HasSpark => SparkId != null;

    protected override void OnPropertyChanged(string? name = null)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(SparkId)) base.OnPropertyChanged(nameof(HasSpark));
    }
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
    /// <summary>The big number's band when it's a temperature (its colour), else null.</summary>
    public Heat? Heat { get => _heat; set => Set(ref _heat, value); }
    /// <summary>The stats on one line: "34 % · 4.1 GHz · 65 W".</summary>
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    private Heat? _heat;
    private string _detail = "";

    /// <summary>CPU/GPU card from its Overview panel: temperature big, then load, clock and power.</summary>
    public void From(DevicePanel p)
    {
        Hero = p.Stats[0].Value;
        HeroUnit = p.Stats[0].Unit;
        SparkId = p.TempId ?? p.LoadId;
        for (int i = 0; i < 3; i++) Stats[i].Value = $"{p.Stats[i + 1].Value} {p.Stats[i + 1].Unit}";
        Detail = string.Join(" · ", Stats.Select(s => s.Value.Trim()).Where(v => v.Length > 0 && !v.StartsWith('–')));
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

    /// <summary>
    /// The Overview's title and the sentence under it: "All quiet." while everything is fine, else which chip is warm,
    /// hot or at its limit, and how far it is from that limit.
    /// </summary>
    public static (string Headline, string Subline) Headline(IReadOnlyCollection<DevicePanel> panels, bool sensorError)
    {
        if (sensorError) return ("Sensors aren't answering.", "Kelvra keeps trying; the numbers shown may be old.");
        var known = panels.Where(p => !double.IsNaN(p.Temp)).ToList();
        if (known.Count == 0) return ("All quiet.", "Waiting for the first temperatures.");
        var worst = known.Max(p => p.Heat);
        var who = known.Where(p => p.Heat == worst).ToList();
        string names = string.Join(" and ", who.Select(p => p.Kind));
        bool one = who.Count == 1;
        var first = who[0];
        string limit = SensorFormat.Format(SensorType.Temperature, (float)first.Limit);
        string left = $"{SensorFormat.ToDisplay(SensorType.Temperature, (float)first.Limit) - SensorFormat.ToDisplay(SensorType.Temperature, (float)first.Temp):0}°";
        return worst switch
        {
            Heat.Critical => ($"{names} {(one ? "is" : "are")} at the limit.",
                              one ? $"{first.Kind} is at {SensorFormat.Format(SensorType.Temperature, (float)first.Temp)}, its limit is {limit}. Check its cooling."
                                  : "Check the cooling, and what's running."),
            Heat.Hot => ($"{names} {(one ? "is" : "are")} running hot.", one ? $"{left} to the {limit} limit." : "Close to their limits."),
            Heat.Warm => ($"{names} {(one ? "is" : "are")} warm.", one ? $"Still {left} below the {limit} limit." : "Still below their limits."),
            _ => ("All quiet.", $"{string.Join(" and ", known.Select(p => p.Kind).Distinct())} {(known.Count == 1 ? "is" : "are")} well inside {(known.Count == 1 ? "its limit" : "their limits")}."),
        };
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
        var downloads = net.Where(s => s.Name.Contains("Download", StringComparison.OrdinalIgnoreCase)).ToList();
        float down = downloads.Sum(s => s.Value ?? 0);
        float up = net.Where(s => s.Name.Contains("Upload", StringComparison.OrdinalIgnoreCase)).Sum(s => s.Value ?? 0);
        peak = Math.Max(peak, Math.Max(down, up));
        stat.Value = SensorFormat.Format(SensorType.Throughput, down);
        stat.Detail = $"↓ {SensorFormat.Format(SensorType.Throughput, down)} · ↑ {SensorFormat.Format(SensorType.Throughput, up)}";
        stat.Percent = peak > 0 ? down / peak * 100 : 0;
        // The line follows the busiest adapter's downloads
        stat.SparkId ??= downloads.MaxBy(s => s.Max ?? 0)?.Id;
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
