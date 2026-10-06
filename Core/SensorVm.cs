using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>Live, bindable view of one sensor.</summary>
public sealed class SensorVm : ObservableObject
{
    private float? _value;
    private float? _min;
    private float? _max;
    private string _valueText = "-";
    private string _minText = "-";
    private string _maxText = "-";
    private int _level;

    public SensorVm(SensorReading r, string groupName)
    {
        Id = r.Id;
        HardwareId = r.HardwareId;
        HardwareName = r.HardwareName;
        HardwareType = r.HardwareType;
        GroupName = groupName;
        Name = r.Name;
        Type = r.Type;
        TypeLabel = SensorFormat.TypeLabel(r.Type);
        OverlayLabel = r.OverlayLabel;
        Category = SensorCategories.HardwareCategory(r.HardwareType);
        TypeCategory = SensorCategories.TypeCategory(r.Type);
        Controllable = r.Controllable;
        Update(r);
    }

    /// <summary>Hardware category shown on the Categories page ("CPU", "GPU", …).</summary>
    public string Category { get; }
    /// <summary>Sensor-type category ("Temperatures", "Load", …).</summary>
    public string TypeCategory { get; }
    /// <summary>Original discovery order; used as the default sort and as a tie-breaker.</summary>
    public int Order { get; init; }
    /// <summary>Order of the device this sensor belongs to; keeps groups in place when sorting.</summary>
    public int HardwareOrder { get; init; }

    private int _groupRank;
    /// <summary>Position of this sensor's device group on the Sensors page (user-arrangeable).</summary>
    public int GroupRank { get => _groupRank; set => Set(ref _groupRank, value); }

    public string Id { get; }
    public string HardwareId { get; }
    public string HardwareName { get; }
    public HardwareType HardwareType { get; }
    /// <summary>Hardware name, made unique when two devices share a name.</summary>
    public string GroupName { get; }
    public string Name { get; }
    public SensorType Type { get; }
    public string TypeLabel { get; }
    public string OverlayLabel { get; }

    public float? Value { get => _value; private set => Set(ref _value, value); }
    public float? Min { get => _min; private set => Set(ref _min, value); }
    public float? Max { get => _max; private set => Set(ref _max, value); }
    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }
    public string MinText { get => _minText; private set => Set(ref _minText, value); }
    public string MaxText { get => _maxText; private set => Set(ref _maxText, value); }

    /// <summary>0 = normal, 1 = warm, 2 = hot (colours overlay values when warning colours are on).</summary>
    public int Level { get => _level; private set => Set(ref _level, value); }

    /// <summary>A fan/pump output Kelvra can drive (Fans page).</summary>
    public bool Controllable { get; }

    public bool IsGpu => HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    public void Update(SensorReading r)
    {
        Value = r.Value;
        Min = r.Min;
        Max = r.Max;
        ValueText = r.ValueText;
        MinText = r.MinText;
        MaxText = r.MaxText;
        Level = LevelFor(Type, r.Value);
    }

    /// <summary>Fixed limits on the raw value: 70/85 °C for temperatures, 80/95 % for load. Other types never warn.</summary>
    public static int LevelFor(SensorType type, float? value)
    {
        if (value is not float v) return 0;
        return type switch
        {
            SensorType.Temperature => v >= 85 ? 2 : v >= 70 ? 1 : 0,
            SensorType.Load => v >= 95 ? 2 : v >= 80 ? 1 : 0,
            _ => 0,
        };
    }

    /// <summary>0..1 fill for overlay bars.</summary>
    public double BarFraction
    {
        get
        {
            if (Value is not float v) return 0;
            // Percentages fill to 100; temperatures use 100 °C as "full"; anything else is relative to its own max so far
            double f = Type switch
            {
                SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Temperature => v / 100.0,
                _ => Max is float m && m > 0 ? v / m : 0,
            };
            return Math.Clamp(f, 0, 1);
        }
    }
}

/// <summary>Ready-made starting points for new overlays.</summary>
public static class OverlayTemplates
{
    public static readonly string[] Names = { "Blank", "Essentials", "Temperatures", "Compact bar" };

    /// <summary>Builds a new profile from a template, picking matching sensors from what was actually detected.</summary>
    public static OverlayProfile Create(string template, IReadOnlyList<SensorVm> sensors, string name)
    {
        var p = new OverlayProfile { Name = name };

        // Adds the first sensor that matches; later matchers are fallbacks (e.g. "Package" temp, else any CPU temp)
        void Add(string? label, bool bar, params Func<SensorVm, bool>[] matches)
        {
            foreach (var match in matches)
            {
                var hit = sensors.FirstOrDefault(s => match(s) && !p.ContainsSensor(s.Id));
                if (hit == null) continue;
                p.Items.Add(new OverlayItem
                {
                    SensorId = hit.Id, Label = label, ShowBar = bar,
                    SensorName = hit.OverlayLabel, HardwareName = hit.GroupName,
                });
                return;
            }
        }

        static bool Cpu(SensorVm s) => s.HardwareType == HardwareType.Cpu;
        static bool Ram(SensorVm s) => s.HardwareType == HardwareType.Memory && !s.HardwareName.Contains("Virtual");

        Func<SensorVm, bool> cpuTemp = s => Cpu(s) && s.Type == SensorType.Temperature &&
                                            (s.Name.Contains("Package") || s.Name.Contains("Tctl"));
        Func<SensorVm, bool> cpuTempAny = s => Cpu(s) && s.Type == SensorType.Temperature;
        Func<SensorVm, bool> cpuLoad = s => Cpu(s) && s.Type == SensorType.Load && s.Name.Contains("Total");
        Func<SensorVm, bool> gpuTemp = s => s.IsGpu && s.Type == SensorType.Temperature && s.Name.Contains("Core");
        Func<SensorVm, bool> gpuLoad = s => s.IsGpu && s.Type == SensorType.Load && s.Name.Contains("Core");
        Func<SensorVm, bool> vram = s => s.IsGpu && s.Type == SensorType.SmallData && s.Name == "GPU Memory Used";
        Func<SensorVm, bool> vramAny = s => s.IsGpu && s.Type == SensorType.SmallData && s.Name.Contains("Memory Used");
        Func<SensorVm, bool> ramUsed = s => Ram(s) && s.Type == SensorType.Data && s.Name.Contains("Used");

        switch (template)
        {
            case "Essentials":
                Add(null, false, cpuLoad);
                Add(null, false, cpuTemp, cpuTempAny);
                Add(null, false, s => Cpu(s) && s.Type == SensorType.Power && s.Name.Contains("Package"));
                Add(null, false, gpuLoad);
                Add(null, false, gpuTemp);
                Add(null, false, s => s.IsGpu && s.Type == SensorType.Clock && s.Name.Contains("Core"));
                Add(null, false, vram, vramAny);
                Add(null, false, s => Ram(s) && s.Type == SensorType.Load);
                Add(null, false, ramUsed);
                break;

            case "Temperatures":
                p.AccentColor = "#FF6B6B";
                Add(null, true, cpuTemp, cpuTempAny);
                Add(null, true, gpuTemp);
                Add(null, true, s => s.IsGpu && s.Type == SensorType.Temperature && s.Name.Contains("Hot"));
                Add(null, true, s => s.IsGpu && s.Type == SensorType.Temperature && s.Name.Contains("Memory"));
                foreach (var drive in sensors.Where(s => s.HardwareType == HardwareType.Storage && s.Type == SensorType.Temperature)
                                             .GroupBy(s => s.HardwareId).Select(g => g.First()))
                    Add(null, true, s => s.Id == drive.Id);
                break;

            case "Compact bar":
                p.Layout = OverlayLayout.Horizontal;
                p.ShowHeaders = false;
                p.FontSize = 12;
                p.CornerRadius = 6;
                Add("CPU", false, cpuLoad);
                Add("CPU", false, cpuTemp, cpuTempAny);
                Add("GPU", false, gpuLoad);
                Add("GPU", false, gpuTemp);
                Add("VRAM", false, vram, vramAny);
                Add("RAM", false, ramUsed);
                break;
        }
        return p;
    }
}
