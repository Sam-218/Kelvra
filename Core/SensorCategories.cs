using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>How sensors are bucketed on the Categories page and in the Sensors filter chips.</summary>
public static class SensorCategories
{
    public sealed record Info(string Name, string Icon);

    /// <summary>Hardware categories in display order.</summary>
    public static readonly Info[] Hardware =
    {
        new("CPU", ""),
        new("GPU", ""),
        new("Memory", ""),
        new("Motherboard", ""),
        new("Storage", ""),
        new("Network", ""),
        new("Cooling", ""),
        new("Battery", ""),
        new("Power supply", ""),
        new("Other", ""),
    };

    /// <summary>Sensor-type categories in display order.</summary>
    public static readonly Info[] Types =
    {
        new("Temperatures", ""),
        new("Load", ""),
        new("Clocks", ""),
        new("Power", ""),
        new("Voltages", ""),
        new("Fans", ""),
        new("Data", ""),
        new("Throughput", ""),
        new("Other", ""),
    };

    public static string HardwareCategory(HardwareType type) => type switch
    {
        HardwareType.Cpu => "CPU",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "GPU",
        HardwareType.Memory => "Memory",
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => "Motherboard",
        HardwareType.Storage => "Storage",
        HardwareType.Network => "Network",
        HardwareType.Cooler => "Cooling",
        HardwareType.Battery => "Battery",
        HardwareType.Psu => "Power supply",
        _ => "Other",
    };

    public static string TypeCategory(SensorType type) => type switch
    {
        SensorType.Temperature => "Temperatures",
        SensorType.Load => "Load",
        SensorType.Clock or SensorType.Frequency => "Clocks",
        SensorType.Power or SensorType.Energy => "Power",
        SensorType.Voltage or SensorType.Current => "Voltages",
        SensorType.Fan or SensorType.Control or SensorType.Flow => "Fans",
        SensorType.Data or SensorType.SmallData => "Data",
        SensorType.Throughput => "Throughput",
        _ => "Other",
    };

}
