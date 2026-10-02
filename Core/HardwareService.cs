using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>Immutable snapshot of one sensor from one poll (raw units: °C, MHz, W …). Made on the polling thread.</summary>
public sealed record SensorReading(
    string Id,
    string HardwareId,
    string HardwareName,
    HardwareType HardwareType,
    string Name,
    SensorType Type,
    float? Value,
    float? Min,
    float? Max)
{
    public string ValueText => SensorFormat.Format(Type, Value);
    public string MinText => SensorFormat.Format(Type, Min);
    public string MaxText => SensorFormat.Format(Type, Max);

    /// <summary>Overlay label with a type hint so "GPU Core" temp/load/clock are distinguishable.</summary>
    public string OverlayLabel => Type switch
    {
        SensorType.Temperature => $"{Name} Temp",
        SensorType.Load => $"{Name} Load",
        SensorType.Clock => $"{Name} Clock",
        SensorType.Power => $"{Name} Power",
        SensorType.Voltage => $"{Name} Volt",
        SensorType.Fan => $"{Name} Fan",
        _ => Name,
    };
}

/// <summary>Reads every sensor the machine exposes (CPU, GPU, RAM, board, drives, network...).</summary>
public sealed class HardwareService : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
        IsStorageEnabled = true,
        IsNetworkEnabled = true,
        IsBatteryEnabled = true,
        IsPsuEnabled = true,
    };

    // Poll runs on a worker thread while Reopen can come from the UI (after a PawnIO install): never both at once
    private readonly object _lock = new();
    private bool _opened;

    public void Open()
    {
        lock (_lock)
        {
            _computer.Open();
            _opened = true;
        }
    }

    /// <summary>Re-detects hardware, e.g. after the PawnIO driver was installed.</summary>
    public void Reopen()
    {
        lock (_lock)
        {
            if (_opened) _computer.Close();
            _computer.Open();
            _opened = true;
        }
    }

    /// <summary>Updates every device and returns all current readings. Slow (driver calls): call off the UI thread.</summary>
    public List<SensorReading> Poll()
    {
        var list = new List<SensorReading>(256);
        lock (_lock)
        {
            if (!_opened) return list;
            foreach (IHardware hw in _computer.Hardware)
                Collect(hw, hw, list);
        }
        return list;
    }

    /// <summary>Adds the sensors of <paramref name="hw"/> and its sub-devices, all filed under the top-level device.</summary>
    private static void Collect(IHardware root, IHardware hw, List<SensorReading> list)
    {
        try { hw.Update(); } catch { /* some devices fail transiently */ }

        foreach (ISensor s in hw.Sensors)
        {
            list.Add(new SensorReading(
                s.Identifier.ToString(),
                root.Identifier.ToString(),
                root.Name,
                root.HardwareType,
                hw == root ? s.Name : $"{hw.Name} {s.Name}",
                s.SensorType,
                s.Value, s.Min, s.Max));
        }

        foreach (IHardware sub in hw.SubHardware)
            Collect(root, sub, list);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_opened) _computer.Close();
            _opened = false;
        }
    }
}

/// <summary>Units, display text and °C/°F conversion for sensor values. Text is culture-invariant ("1.5 W" everywhere).</summary>
public static class SensorFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Show temperatures in °F instead of °C (Settings). Values are always stored in °C.</summary>
    public static bool UseFahrenheit { get; set; }

    /// <summary>Converts a raw sensor value to the unit shown in the UI.</summary>
    public static float ToDisplay(SensorType type, float value) =>
        type == SensorType.Temperature && UseFahrenheit ? value * 9f / 5f + 32f : value;

    /// <summary>Converts a value typed in the UI back to the raw sensor unit.</summary>
    public static float FromDisplay(SensorType type, float value) =>
        type == SensorType.Temperature && UseFahrenheit ? (value - 32f) * 5f / 9f : value;

    /// <summary>Unit label for raw numbers (charts, CSV, alerts).</summary>
    public static string Unit(SensorType type) => type switch
    {
        SensorType.Temperature => UseFahrenheit ? "°F" : "°C",
        SensorType.Load or SensorType.Control or SensorType.Level => "%",
        SensorType.Clock => "MHz",
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Power => "W",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.Frequency => "Hz",
        SensorType.Energy => "mWh",
        SensorType.Noise => "dBA",
        SensorType.TimeSpan => "s",
        _ => "",
    };

    public static string Format(SensorType type, float? value)
    {
        if (value is not float v || float.IsNaN(v)) return "-";
        return type switch
        {
            SensorType.Temperature => $"{ToDisplay(type, v).ToString("0", Inv)} {Unit(type)}",
            SensorType.Load or SensorType.Control or SensorType.Level => $"{v.ToString("0", Inv)} %",
            SensorType.Clock => $"{v.ToString("0", Inv)} MHz",
            SensorType.Voltage => $"{v.ToString("0.000", Inv)} V",
            SensorType.Current => $"{v.ToString("0.00", Inv)} A",
            SensorType.Power => $"{v.ToString("0.0", Inv)} W",
            SensorType.Fan => $"{v.ToString("0", Inv)} RPM",
            SensorType.Flow => $"{v.ToString("0.0", Inv)} L/h",
            SensorType.Data => $"{v.ToString("0.0", Inv)} GB",
            SensorType.SmallData => $"{v.ToString("0", Inv)} MB",
            SensorType.Throughput => Throughput(v),
            SensorType.Frequency => $"{v.ToString("0", Inv)} Hz",
            SensorType.Energy => $"{v.ToString("0", Inv)} mWh",
            SensorType.Noise => $"{v.ToString("0", Inv)} dBA",
            SensorType.TimeSpan => TimeSpan.FromSeconds(v).ToString(@"hh\:mm\:ss"),
            _ => v.ToString("0.##", Inv),
        };
    }

    private static string Throughput(float bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024) return $"{(bytesPerSec / 1024 / 1024).ToString("0.0", Inv)} MB/s";
        if (bytesPerSec >= 1024) return $"{(bytesPerSec / 1024).ToString("0", Inv)} KB/s";
        return $"{bytesPerSec.ToString("0", Inv)} B/s";
    }

    public static string TypeLabel(SensorType type) => type switch
    {
        SensorType.Temperature => "Temperatures",
        SensorType.Load => "Load",
        SensorType.Clock => "Clocks",
        SensorType.Voltage => "Voltages",
        SensorType.Power => "Power",
        SensorType.Fan => "Fans",
        SensorType.Control => "Fan Control",
        SensorType.Data or SensorType.SmallData => "Data",
        SensorType.Throughput => "Throughput",
        _ => type.ToString(),
    };
}
