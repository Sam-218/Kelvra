using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace Kelvra;

public sealed record InfoLine(string Label, string Value);

public sealed record InfoSection(string Title, string Icon, List<InfoLine> Lines);

/// <summary>AIDA64-style system summary gathered from WMI, the registry and the live sensors.</summary>
public static class SystemInfo
{
    public static List<InfoSection> Collect(IReadOnlyList<SensorVm> sensors)
    {
        var sections = new List<InfoSection>
        {
            Computer(),
            Processor(),
            Graphics(),
            Memory(),
            Motherboard(),
        };
        sections.AddRange(Storage(sensors));
        sections.Add(Network());
        return sections.Where(s => s.Lines.Count > 0).ToList();
    }

    public static string ToText(IEnumerable<InfoSection> sections)
    {
        var sb = new StringBuilder();
        foreach (var s in sections)
        {
            sb.AppendLine($"== {s.Title} ==");
            foreach (var l in s.Lines) sb.AppendLine($"{l.Label}: {l.Value}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ---------- sections ----------

    private static InfoSection Computer()
    {
        var lines = new List<InfoLine>();
        using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string product = cv?.GetValue("ProductName") as string ?? "Windows";
        string build = cv?.GetValue("CurrentBuild") as string ?? "";
        if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        string version = cv?.GetValue("DisplayVersion") as string ?? "";
        object? ubr = cv?.GetValue("UBR");

        lines.Add(new("Device name", Environment.MachineName));
        lines.Add(new("User", Environment.UserName));
        lines.Add(new("Windows", $"{product} {version}".Trim()));
        lines.Add(new("Build", ubr != null ? $"{build}.{ubr}" : build));
        if (cv?.GetValue("InstallDate") is int installed)
            lines.Add(new("Installed", DateTimeOffset.FromUnixTimeSeconds(installed).LocalDateTime.ToString("d MMM yyyy")));
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        lines.Add(new("Uptime", up.TotalDays >= 1 ? $"{(int)up.TotalDays} d {up.Hours} h {up.Minutes} min" : $"{up.Hours} h {up.Minutes} min"));
        lines.Add(new("Architecture", System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString()));
        return new("Computer", "", lines);
    }

    private static InfoSection Processor()
    {
        var lines = new List<InfoLine>();
        foreach (var cpu in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, VirtualizationFirmwareEnabled FROM Win32_Processor"))
        {
            lines.Add(new("Model", Str(cpu, "Name")));
            lines.Add(new("Cores / threads", $"{Str(cpu, "NumberOfCores")} cores · {Str(cpu, "NumberOfLogicalProcessors")} threads"));
            lines.Add(new("Base clock", $"{Str(cpu, "MaxClockSpeed")} MHz"));
            if (cpu["L2CacheSize"] is uint l2 && l2 > 0) lines.Add(new("L2 cache", ByteFormat.Format(l2 * 1024L)));
            if (cpu["L3CacheSize"] is uint l3 && l3 > 0) lines.Add(new("L3 cache", ByteFormat.Format(l3 * 1024L)));
            lines.Add(new("Socket", Str(cpu, "SocketDesignation")));
            if (cpu["VirtualizationFirmwareEnabled"] is bool virt) lines.Add(new("Virtualization", virt ? "Enabled" : "Disabled"));
        }
        return new("Processor", "", lines);
    }

    private static InfoSection Graphics()
    {
        var lines = new List<InfoLine>();
        foreach (var gpu in Query("SELECT Name, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
        {
            string name = Str(gpu, "Name");
            lines.Add(new("Adapter", name));
            if (VideoMemory(name) is long vram) lines.Add(new("  Video memory", ByteFormat.Format(vram)));
            lines.Add(new("  Driver", $"{Str(gpu, "DriverVersion")}  ({Date(gpu["DriverDate"])})"));
            if (gpu["CurrentHorizontalResolution"] is uint w && gpu["CurrentVerticalResolution"] is uint h)
                lines.Add(new("  Display", $"{w} × {h} @ {Str(gpu, "CurrentRefreshRate")} Hz"));
        }
        return new("Graphics", "", lines);
    }

    private static InfoSection Memory()
    {
        var lines = new List<InfoLine>();
        foreach (var cs in Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
            if (cs["TotalPhysicalMemory"] is ulong total) lines.Add(new("Installed", ByteFormat.Format((long)total)));

        int slots = Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => a["MemoryDevices"] is ushort n ? n : 0);
        var sticks = Query("SELECT DeviceLocator, Capacity, Manufacturer, PartNumber, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
        if (slots > 0) lines.Add(new("Slots used", $"{sticks.Count} of {slots}"));
        foreach (var m in sticks)
        {
            long cap = m["Capacity"] is ulong c ? (long)c : 0;
            string speed = m["ConfiguredClockSpeed"] is uint cfg && cfg > 0 ? $"{cfg} MT/s" : $"{Str(m, "Speed")} MT/s";
            lines.Add(new(Str(m, "DeviceLocator"), $"{ByteFormat.Format(cap)} · {speed} · {Str(m, "Manufacturer")} {Str(m, "PartNumber")}".Trim()));
        }
        return new("Memory", "", lines);
    }

    private static InfoSection Motherboard()
    {
        var lines = new List<InfoLine>();
        foreach (var bb in Query("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard"))
            lines.Add(new("Board", $"{Str(bb, "Manufacturer")} {Str(bb, "Product")}".Trim()));
        foreach (var bios in Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
        {
            lines.Add(new("BIOS", $"{Str(bios, "SMBIOSBIOSVersion")} ({Str(bios, "Manufacturer")})"));
            lines.Add(new("BIOS date", Date(bios["ReleaseDate"])));
        }
        using var sb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        lines.Add(new("Boot mode", sb != null ? "UEFI" : "Legacy BIOS"));
        if (sb?.GetValue("UEFISecureBootEnabled") is int secure) lines.Add(new("Secure Boot", secure == 1 ? "On" : "Off"));
        return new("Motherboard", "", lines);
    }

    /// <summary>One section per physical drive.</summary>
    private static IEnumerable<InfoSection> Storage(IReadOnlyList<SensorVm> sensors)
    {
        foreach (var d in Query("SELECT Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive"))
        {
            var lines = new List<InfoLine>();
            string model = Str(d, "Model");
            long size = d["Size"] is ulong s ? (long)s : 0;
            lines.Add(new("Capacity", ByteFormat.Format(size)));

            // Health values come from the live sensors (SMART), matched by model name
            var health = sensors.Where(x => x.HardwareType == HardwareType.Storage &&
                                            (x.HardwareName.Contains(model, StringComparison.OrdinalIgnoreCase) ||
                                             model.Contains(x.HardwareName, StringComparison.OrdinalIgnoreCase)))
                                .Where(x => x.Type is SensorType.Temperature or SensorType.Level or SensorType.Data)
                                .Where(x => !x.Name.Contains("Threshold"))
                                .GroupBy(x => x.Name).Select(g => g.First());
            foreach (var h in health) lines.Add(new(h.Name, h.ValueText));
            yield return new(model, "", lines);
        }
    }

    private static InfoSection Network()
    {
        var lines = new List<InfoLine>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            string ip = nic.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "–";
            string speed = nic.Speed >= 1_000_000_000 ? $"{nic.Speed / 1_000_000_000.0:0.#} Gbit/s" : $"{nic.Speed / 1_000_000} Mbit/s";
            lines.Add(new(nic.Name, nic.Description));
            lines.Add(new("  Address", $"{ip} · {speed}"));
            lines.Add(new("  MAC", string.Join("-", nic.GetPhysicalAddress().GetAddressBytes().Select(x => x.ToString("X2")))));
        }
        return new("Network", "", lines);
    }

    // ---------- helpers ----------

    private static List<ManagementBaseObject> Query(string wql)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch
        {
            return new List<ManagementBaseObject>();
        }
    }

    private static string Str(ManagementBaseObject o, string prop) => o[prop]?.ToString()?.Trim() ?? "";

    private static string Date(object? wmiDate)
    {
        try
        {
            return wmiDate is string s ? ManagementDateTimeConverter.ToDateTime(s).ToString("d MMM yyyy") : "–";
        }
        catch
        {
            return "–";
        }
    }

    /// <summary>Real VRAM from the driver's registry key (WMI's AdapterRAM caps at 4 GB).</summary>
    private static long? VideoMemory(string adapterName)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return null;
            foreach (var sub in cls.GetSubKeyNames())
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") as string != adapterName) continue;
                return k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes, 0),
                    _ => null,
                };
            }
        }
        catch
        {
            // no access
        }
        return null;
    }
}
