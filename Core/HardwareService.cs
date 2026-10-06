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
    /// <summary>A fan/pump output Kelvra can set (Control sensor with a software control).</summary>
    public bool Controllable { get; init; }

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

    // Fan outputs found by the last poll, and the ones Kelvra currently drives (so they can be handed back to the BIOS)
    private readonly Dictionary<string, IControl> _controls = new();
    private readonly HashSet<string> _driven = new();

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
            ReleaseFansLocked();
            _controls.Clear();
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
                Collect(hw, hw, list, _controls);
        }
        return list;
    }

    // ---------- fan control ----------

    /// <summary>
    /// Sets a fan output to <paramref name="percent"/>, or hands it back to the BIOS/driver (null). Returns false if that
    /// output doesn't exist or refused. Writes to hardware: only <see cref="FanController"/> calls this.
    /// </summary>
    public bool SetFan(string controlId, float? percent)
    {
        lock (_lock)
        {
            if (!_opened || !_controls.TryGetValue(controlId, out var control)) return false;
            try
            {
                if (percent is float p)
                {
                    control.SetSoftware(Math.Clamp(p, control.MinSoftwareValue, control.MaxSoftwareValue));
                    _driven.Add(controlId);
                }
                else
                {
                    control.SetDefault();
                    _driven.Remove(controlId);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Setting fan {controlId} to {(percent is float v ? v + " %" : "automatic")}", ex);
                return false;
            }
        }
    }

    /// <summary>Gives every fan Kelvra drives back to the BIOS/driver (on exit, errors, or when fan control is turned off).</summary>
    public void ReleaseFans()
    {
        lock (_lock) ReleaseFansLocked();
    }

    private void ReleaseFansLocked()
    {
        foreach (var id in _driven.ToList())
        {
            try
            {
                if (_controls.TryGetValue(id, out var control)) control.SetDefault();
            }
            catch (Exception ex)
            {
                Log.Error($"Handing fan {id} back to automatic", ex);
            }
        }
        _driven.Clear();
    }

    /// <summary>Starts every sensor's min/max over from its current value (like the Reset button in HWiNFO).</summary>
    public void ResetMinMax()
    {
        lock (_lock)
        {
            if (!_opened) return;
            foreach (IHardware hw in _computer.Hardware) ResetMinMax(hw);
        }
    }

    private static void ResetMinMax(IHardware hw)
    {
        foreach (ISensor s in hw.Sensors)
        {
            s.ResetMin();
            s.ResetMax();
        }
        foreach (IHardware sub in hw.SubHardware) ResetMinMax(sub);
    }

    /// <summary>Adds the sensors of <paramref name="hw"/> and its sub-devices, all filed under the top-level device.</summary>
    private static void Collect(IHardware root, IHardware hw, List<SensorReading> list, Dictionary<string, IControl> controls)
    {
        try { hw.Update(); } catch { /* some devices fail transiently */ }

        foreach (ISensor s in hw.Sensors)
        {
            string id = s.Identifier.ToString();
            bool controllable = s.SensorType == SensorType.Control && s.Control != null;
            if (controllable) controls[id] = s.Control!;
            list.Add(new SensorReading(
                id,
                root.Identifier.ToString(),
                root.Name,
                root.HardwareType,
                hw == root ? s.Name : $"{hw.Name} {s.Name}",
                s.SensorType,
                s.Value, s.Min, s.Max) { Controllable = controllable });
        }

        foreach (IHardware sub in hw.SubHardware)
            Collect(root, sub, list, controls);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            ReleaseFansLocked(); // never leave a fan at a fixed speed when Kelvra exits
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
            SensorType.TimeSpan => Duration(v),
            _ => v.ToString("0.##", Inv),
        };
    }

    /// <summary>"h:mm:ss" with total hours, so 25 h reads "25:00:00" (the "hh" format wraps at a day). No sign, as before.</summary>
    private static string Duration(float seconds)
    {
        var t = TimeSpan.FromSeconds(seconds).Duration();
        return string.Create(Inv, $"{(long)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}");
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
