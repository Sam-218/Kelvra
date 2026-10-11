using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace Kelvra;

public enum InfoKind
{
    /// <summary>Label and value.</summary>
    Line,
    /// <summary>A sub-heading inside a card (one graphics adapter, one network adapter); the value is shown beside it, muted.</summary>
    Heading,
    /// <summary>Label and value with a bar under the value (<see cref="InfoLine.Fraction"/> filled), e.g. how full a drive is.</summary>
    Meter,
}

public sealed record InfoLine(string Label, string Value, InfoKind Kind = InfoKind.Line, double Fraction = 0)
{
    public bool IsHeading => Kind == InfoKind.Heading;
    public bool IsMeter => Kind == InfoKind.Meter;
    /// <summary>A meter over 90 % (a nearly full drive) is drawn in the warning colour.</summary>
    public bool IsHigh => Kind == InfoKind.Meter && Fraction >= 0.9;
}

/// <param name="Subtitle">One line under the title ("NVMe SSD · 932 GB").</param>
public sealed record InfoSection(string Title, string Icon, List<InfoLine> Lines, string Subtitle = "");

/// <summary>AIDA64-style system summary gathered from WMI, the registry and the live sensors.</summary>
public static class SystemInfo
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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
            sb.AppendLine(s.Subtitle.Length > 0 ? $"== {s.Title} ({s.Subtitle}) ==" : $"== {s.Title} ==");
            foreach (var l in s.Lines)
            {
                if (l.IsHeading) sb.AppendLine(l.Value.Length > 0 ? $"[{l.Label} · {l.Value}]" : $"[{l.Label}]");
                else sb.AppendLine(l.Label.EndsWith(':') ? $"{l.Label} {l.Value}" : $"{l.Label}: {l.Value}"); // "C: 824 GB free…"
            }
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
        string model = "";
        foreach (var cpu in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, VirtualizationFirmwareEnabled FROM Win32_Processor"))
        {
            model = Str(cpu, "Name");
            lines.Add(new("Model", model));
            lines.Add(new("Cores / threads", $"{Str(cpu, "NumberOfCores")} cores · {Str(cpu, "NumberOfLogicalProcessors")} threads"));
            if (cpu["MaxClockSpeed"] is uint mhz && mhz > 0) lines.Add(new("Rated clock", $"{(mhz / 1000.0).ToString("0.00", Inv)} GHz"));
            string cache = string.Join(" · ", new[] { ("L2", cpu["L2CacheSize"]), ("L3", cpu["L3CacheSize"]) }
                .Where(c => c.Item2 is uint kb && kb > 0).Select(c => $"{c.Item1} {ByteFormat.Format((uint)c.Item2! * 1024L)}"));
            if (cache.Length > 0) lines.Add(new("Cache", cache));
            lines.Add(new("Socket", Str(cpu, "SocketDesignation")));
            if (cpu["VirtualizationFirmwareEnabled"] is bool virt) lines.Add(new("Virtualization", virt ? "Enabled" : "Disabled in the BIOS"));
        }
        return new("Processor", "", lines);
    }

    private static InfoSection Graphics()
    {
        var lines = new List<InfoLine>();
        var gpus = Query("SELECT Name, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController");
        foreach (var gpu in gpus)
        {
            string name = Str(gpu, "Name");
            // Several adapters (iGPU + GPU): one heading each
            if (gpus.Count > 1) lines.Add(new(name, "", InfoKind.Heading));
            else lines.Add(new("Model", name));
            if (VideoMemory(name) is long vram) lines.Add(new("Video memory", ByteFormat.Format(vram)));
            string driver = Str(gpu, "DriverVersion");
            string date = Date(gpu["DriverDate"]);
            lines.Add(new("Driver", NvidiaVersion(name, driver) is string nv ? $"{nv} · {date}" : $"{driver} · {date}"));
            if (gpu["CurrentHorizontalResolution"] is uint w && gpu["CurrentVerticalResolution"] is uint h)
                lines.Add(new("Display", $"{w} × {h} @ {Str(gpu, "CurrentRefreshRate")} Hz"));
        }
        return new("Graphics", "", lines);
    }

    /// <summary>
    /// An AMD or Intel graphics adapter is installed: the only ones whose driver frame generation (Fluid Motion Frames,
    /// XeSS-FG) PresentMon tells apart from the game's own frames. Goes by the PCI vendor, not the name.
    /// </summary>
    public static bool HasAmdOrIntelGpu() =>
        Query("SELECT PNPDeviceID FROM Win32_VideoController").Any(g => IsAmdOrIntel(Str(g, "PNPDeviceID")));

    /// <param name="pnpDeviceId">"PCI\VEN_1002&amp;DEV_744C&amp;…" (1002 = AMD, 8086 = Intel, 10DE = NVIDIA).</param>
    internal static bool IsAmdOrIntel(string pnpDeviceId) =>
        pnpDeviceId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ||
        pnpDeviceId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase);

    /// <summary>NVIDIA's own version ("617.14") from the Windows driver version (32.0.16.1714); null for other vendors.</summary>
    internal static string? NvidiaVersion(string adapter, string windowsVersion)
    {
        if (!adapter.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = windowsVersion.Split('.');
        if (parts.Length != 4) return null;
        string digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5 || !digits.All(char.IsAsciiDigit)) return null;
        digits = digits[^5..];
        return $"{int.Parse(digits[..3], Inv)}.{digits[3..]}";
    }

    private static InfoSection Memory()
    {
        var lines = new List<InfoLine>();
        var sticks = Query("SELECT DeviceLocator, Capacity, Manufacturer, PartNumber, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
        long installed = sticks.Sum(m => m["Capacity"] is ulong c ? (long)c : 0);
        long usable = Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem").Select(cs => cs["TotalPhysicalMemory"] is ulong t ? (long)t : 0).FirstOrDefault();
        if (installed > 0) lines.Add(new("Installed", usable > 0 ? $"{ByteFormat.Format(installed)} · {ByteFormat.Format(usable)} usable" : ByteFormat.Format(installed)));
        else if (usable > 0) lines.Add(new("Installed", ByteFormat.Format(usable)));

        int configured = sticks.Select(m => m["ConfiguredClockSpeed"] is uint s && s > 0 ? (int)s : m["Speed"] is uint s2 ? (int)s2 : 0).DefaultIfEmpty(0).Max();
        string type = sticks.Select(m => m["SMBIOSMemoryType"] is uint t ? MemoryType(t) : "").FirstOrDefault(t => t.Length > 0) ?? "";
        if (configured > 0) lines.Add(new("Speed", type.Length > 0 ? $"{type}-{configured} ({configured} MT/s)" : $"{configured} MT/s"));
        int rated = sticks.Select(m => RatedSpeed(Str(m, "PartNumber"))).DefaultIfEmpty(0).Max();
        if (configured > 0 && rated > configured + 100)
            lines.Add(new("Rated speed", $"{rated} MT/s · XMP / EXPO seems to be off"));

        int slots = Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => a["MemoryDevices"] is ushort n ? n : 0);
        if (slots > 0) lines.Add(new("Slots", $"{sticks.Count} of {slots} used"));
        foreach (var m in sticks)
        {
            long cap = m["Capacity"] is ulong c ? (long)c : 0;
            string make = $"{Str(m, "Manufacturer")} {Str(m, "PartNumber")}".Trim();
            lines.Add(new(Str(m, "DeviceLocator"), make.Length > 0 ? $"{ByteFormat.Format(cap)} · {make}" : ByteFormat.Format(cap)));
        }
        return new("Memory", "", lines);
    }

    private static string MemoryType(uint smbios) => smbios switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "",
    };

    /// <summary>The speed in a module's part number ("F4-3200C16-16GTZRX" → 3200, "CMK32GX4M2E3200C16" → 3200); 0 if there's none.</summary>
    internal static int RatedSpeed(string partNumber)
    {
        var m = Regex.Match(partNumber, @"(?<!\d)(2133|2400|2666|2800|2933|3000|3200|3333|3466|3600|3733|3800|3866|4000|4133|4266|4400|4600|4800|5200|5600|6000|6200|6400|6600|6800|7200|7600|8000|8400)(?!\d)");
        return m.Success ? int.Parse(m.Value, Inv) : 0;
    }

    private static InfoSection Motherboard()
    {
        var lines = new List<InfoLine>();
        foreach (var bb in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
        {
            lines.Add(new("Model", Str(bb, "Product")));
            lines.Add(new("Maker", Str(bb, "Manufacturer")));
        }
        foreach (var bios in Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
            lines.Add(new("BIOS", $"{Str(bios, "SMBIOSBIOSVersion")} · {Date(bios["ReleaseDate"])}"));
        using var sb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        string boot = sb != null ? "UEFI" : "Legacy BIOS";
        if (sb?.GetValue("UEFISecureBootEnabled") is int secure) boot += secure == 1 ? " · Secure Boot on" : " · Secure Boot off";
        lines.Add(new("Boot", boot));
        return new("Motherboard", "", lines);
    }

    /// <summary>One section per physical drive: type, how full its volumes are, health, temperature, and the data it has moved.</summary>
    private static IEnumerable<InfoSection> Storage(IReadOnlyList<SensorVm> sensors)
    {
        // Type, bus, firmware and Windows' health verdict per disk number
        var physical = Query(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, MediaType, BusType, FirmwareVersion, HealthStatus FROM MSFT_PhysicalDisk")
            .Where(p => int.TryParse(Str(p, "DeviceId"), out _))
            .GroupBy(p => int.Parse(Str(p, "DeviceId"), Inv)).ToDictionary(g => g.Key, g => g.First());
        var letters = Query(@"root\Microsoft\Windows\Storage", "SELECT DiskNumber, DriveLetter FROM MSFT_Partition")
            .Where(p => p["DriveLetter"] is char c && c != '\0')
            .Select(p => (Disk: Convert.ToInt32(p["DiskNumber"], Inv), Letter: (char)p["DriveLetter"]))
            .ToLookup(p => p.Disk, p => p.Letter);

        // The Windows drive first, then by drive letter; drives without one last
        char system = char.ToUpperInvariant(Environment.GetFolderPath(Environment.SpecialFolder.Windows).FirstOrDefault('C'));
        var disks = Query("SELECT Index, Model, Size FROM Win32_DiskDrive")
            .Select(d => (Disk: d, Index: Convert.ToInt32(d["Index"], Inv)))
            .OrderBy(d => letters[d.Index].Contains(system) ? 0 : 1)
            .ThenBy(d => letters[d.Index].Select(char.ToUpperInvariant).DefaultIfEmpty('~').Min())
            .ThenBy(d => d.Index)
            .Select(d => d.Disk);
        foreach (var d in disks)
        {
            var lines = new List<InfoLine>();
            int index = Convert.ToInt32(d["Index"], Inv);
            string model = Str(d, "Model");
            long size = d["Size"] is ulong s ? (long)s : 0;
            physical.TryGetValue(index, out var pd);

            foreach (char letter in letters[index].OrderBy(c => c))
            {
                try
                {
                    var drive = new DriveInfo(letter.ToString());
                    if (!drive.IsReady || drive.TotalSize <= 0) continue;
                    double used = 1 - (double)drive.TotalFreeSpace / drive.TotalSize;
                    string name = drive.VolumeLabel.Trim();
                    string label = name.Length > 0 ? $"{letter}: {name}" : $"{letter}:";
                    lines.Add(new(label, $"{ByteFormat.Format(drive.TotalFreeSpace)} free of {ByteFormat.Format(drive.TotalSize)}", InfoKind.Meter, used));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // not a readable volume (BitLocker locked, removed)
                }
            }

            // Health values come from the live sensors (SMART), matched by model name
            var smart = sensors.Where(x => x.HardwareType == HardwareType.Storage &&
                                           (x.HardwareName.Contains(model, StringComparison.OrdinalIgnoreCase) ||
                                            model.Contains(x.HardwareName, StringComparison.OrdinalIgnoreCase)))
                               .ToList();
            SensorVm? Find(SensorType type, params string[] names) =>
                names.Select(n => smart.FirstOrDefault(x => x.Type == type && x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                     .FirstOrDefault(x => x?.Value != null);

            string verdict = (pd?["HealthStatus"] as ushort?) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "" };
            if (Find(SensorType.Level, "Life", "Remaining Life") is { Value: float life })
                lines.Add(new("Health", verdict.Length > 0 ? $"{life:0} % life left · {verdict}" : $"{life:0} % life left"));
            else if (verdict.Length > 0) lines.Add(new("Health", verdict));

            var temp = Find(SensorType.Temperature, "Temperature", "Composite Temperature", "Temperature #1")
                       ?? smart.FirstOrDefault(x => x.Type == SensorType.Temperature && x.Value != null &&
                                                    !x.Name.Contains("Warning") && !x.Name.Contains("Critical") && !x.Name.Contains("Threshold"));
            if (temp != null) lines.Add(new("Temperature", temp.ValueText));

            string read = Find(SensorType.Data, "Data Read") is { Value: float r } ? Terabytes(r) : "";
            string written = Find(SensorType.Data, "Data Written") is { Value: float w } ? Terabytes(w) : "";
            if (read.Length > 0 && written.Length > 0) lines.Add(new("Read / written", $"{read} / {written}"));
            else if (written.Length > 0) lines.Add(new("Written", written));

            if (pd != null && Str(pd, "FirmwareVersion") is { Length: > 0 } fw) lines.Add(new("Firmware", fw));

            string kind = DriveKind(pd);
            string subtitle = string.Join(" · ", new[] { kind, size > 0 ? ByteFormat.Format(size) : "" }.Where(x => x.Length > 0));
            if (lines.Count == 0) lines.Add(new("Volumes", "None with a drive letter"));
            yield return new(model, "", lines, subtitle);
        }
    }

    /// <summary>"NVMe SSD", "SATA SSD", "SATA HDD", "USB drive" … from Windows' storage info.</summary>
    private static string DriveKind(ManagementBaseObject? pd)
    {
        if (pd == null) return "";
        ushort media = pd["MediaType"] is ushort m ? m : (ushort)0;
        ushort bus = pd["BusType"] is ushort b ? b : (ushort)0;
        string busName = bus switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 12 => "SD", 13 => "MMC", 3 => "ATA", _ => "" };
        string mediaName = media switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
        if (bus == 7) return mediaName.Length > 0 ? $"USB {mediaName}" : "USB drive";
        return $"{busName} {mediaName}".Trim();
    }

    /// <summary>LibreHardwareMonitor counts drive traffic in GB (10⁹ bytes); big totals read better in TB.</summary>
    internal static string Terabytes(float gb) =>
        gb >= 1000 ? $"{(gb / 1000).ToString("0.0", Inv)} TB" : $"{gb.ToString("0", Inv)} GB";

    private static InfoSection Network()
    {
        var lines = new List<InfoLine>();
        var macs = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            // Windows also lists the filter drivers stacked on an adapter ("…-WFP Native MAC Layer LightWeight Filter-0000"):
            // same MAC, no address of their own
            if (IsFilterLayer(nic.Name) || IsFilterLayer(nic.Description)) continue;
            var ip = nic.GetIPProperties();
            if (ip.UnicastAddresses.Count == 0) continue;
            string mac = string.Join("-", nic.GetPhysicalAddress().GetAddressBytes().Select(x => x.ToString("X2")));
            if (mac.Length > 0 && !macs.Add(mac)) continue;

            string speed = nic.Speed <= 0 ? "" : nic.Speed >= 1_000_000_000 ? $"{nic.Speed / 1_000_000_000.0:0.#} Gbit/s" : $"{nic.Speed / 1_000_000} Mbit/s";
            lines.Add(new(nic.Name, speed, InfoKind.Heading));
            lines.Add(new("Adapter", nic.Description));
            var v4 = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (v4 != null) lines.Add(new("IPv4", v4.PrefixLength > 0 ? $"{v4.Address} / {v4.PrefixLength}" : v4.Address.ToString()));
            var v6 = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal);
            if (v6 != null) lines.Add(new("IPv6", v6.Address.ToString()));
            var gateway = ip.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork) ?? ip.GatewayAddresses.FirstOrDefault();
            if (gateway != null) lines.Add(new("Gateway", gateway.Address.ToString()));
            var dns = ip.DnsAddresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).Take(2).ToList();
            if (dns.Count > 0) lines.Add(new("DNS", string.Join(", ", dns)));
            if (mac.Length > 0) lines.Add(new("MAC", mac));
        }
        return new("Network", "", lines);
    }

    internal static bool IsFilterLayer(string name) =>
        Regex.IsMatch(name, @"-\d{4}$") || name.Contains("-WFP ", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("-QoS Packet Scheduler", StringComparison.OrdinalIgnoreCase);

    // ---------- helpers ----------

    private static List<ManagementBaseObject> Query(string wql) => Query(null, wql);

    private static List<ManagementBaseObject> Query(string? scope, string wql)
    {
        try
        {
            using var searcher = scope == null ? new ManagementObjectSearcher(wql) : new ManagementObjectSearcher(scope, wql);
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
