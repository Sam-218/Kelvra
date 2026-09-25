using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kelvra;

/// <summary>One running process with live CPU / memory / disk usage.</summary>
public sealed class ProcessRow : ObservableObject
{
    private double _cpu;
    private long _memory;
    private double _disk;

    public ProcessRow(int pid, string name, string? path, string description, ImageSource? icon)
    {
        Pid = pid;
        Name = name;
        Path = path;
        Description = description;
        Icon = icon;
    }

    public int Pid { get; }
    public string Name { get; }
    public string? Path { get; }
    public string Description { get; }
    public ImageSource? Icon { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : Description;

    public double Cpu { get => _cpu; set { if (Set(ref _cpu, value)) OnPropertyChanged(nameof(CpuText)); } }
    public long Memory { get => _memory; set { if (Set(ref _memory, value)) OnPropertyChanged(nameof(MemoryText)); } }
    public double Disk { get => _disk; set { if (Set(ref _disk, value)) OnPropertyChanged(nameof(DiskText)); } }

    public string CpuText => $"{Cpu:0.0} %";
    public string MemoryText => ByteFormat.Format(Memory);
    public string DiskText => Disk < 1024 ? "0 MB/s" : $"{Disk / 1024 / 1024:0.0} MB/s";

    internal long LastCpuTicks;
    internal ulong LastIoBytes;
}

/// <summary>Samples all processes (like Task Manager's Details tab).</summary>
public sealed class ProcessMonitor
{
    private readonly Dictionary<int, ProcessRow> _rows = new();
    private readonly Dictionary<string, (string Description, ImageSource? Icon)> _fileInfo = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastSample = DateTime.MinValue;

    public ObservableCollection<ProcessRow> Rows { get; } = new();
    public double TotalCpu { get; private set; }
    public long TotalMemory { get; private set; }

    private sealed record Sample(int Pid, string Name, string? Path, long CpuTicks, long Memory, ulong IoBytes);

    /// <summary>Collects on a worker thread, then updates <see cref="Rows"/> on the caller's (UI) thread.</summary>
    public async Task RefreshAsync()
    {
        var samples = await Task.Run(Collect);
        var now = DateTime.UtcNow;
        double elapsedTicks = _lastSample == DateTime.MinValue ? 0 : (now - _lastSample).Ticks;
        _lastSample = now;
        int cores = Environment.ProcessorCount;

        var seen = new HashSet<int>();
        double totalCpu = 0;
        long totalMem = 0;
        foreach (var s in samples)
        {
            seen.Add(s.Pid);
            if (!_rows.TryGetValue(s.Pid, out var row))
            {
                var (desc, icon) = s.Path != null ? FileInfoFor(s.Path) : ("", null);
                row = new ProcessRow(s.Pid, s.Name, s.Path, desc, icon) { LastCpuTicks = s.CpuTicks, LastIoBytes = s.IoBytes };
                _rows[s.Pid] = row;
                Rows.Add(row);
            }
            else if (elapsedTicks > 0)
            {
                row.Cpu = Math.Clamp((s.CpuTicks - row.LastCpuTicks) / elapsedTicks / cores * 100, 0, 100);
                row.Disk = Math.Max(0, (s.IoBytes - row.LastIoBytes) / (elapsedTicks / TimeSpan.TicksPerSecond));
                row.LastCpuTicks = s.CpuTicks;
                row.LastIoBytes = s.IoBytes;
            }
            row.Memory = s.Memory;
            totalCpu += row.Cpu;
            totalMem += s.Memory;
        }

        foreach (var pid in _rows.Keys.Where(p => !seen.Contains(p)).ToList())
        {
            Rows.Remove(_rows[pid]);
            _rows.Remove(pid);
        }
        TotalCpu = Math.Min(100, totalCpu);
        TotalMemory = totalMem;
    }

    private static List<Sample> Collect()
    {
        var list = new List<Sample>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0) continue; // System Idle Process
                string? path = null;
                long cpu = 0, mem = 0;
                ulong io = 0;
                var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        if (GetProcessTimes(h, out _, out _, out long kernel, out long user)) cpu = kernel + user;
                        if (GetProcessIoCounters(h, out var counters)) io = counters.ReadTransferCount + counters.WriteTransferCount;
                        var pmc = new PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>() };
                        if (GetProcessMemoryInfo(h, ref pmc, pmc.cb)) mem = (long)pmc.WorkingSetSize;
                        var sb = new StringBuilder(1024);
                        int size = sb.Capacity;
                        if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
                    }
                    finally
                    {
                        CloseHandle(h);
                    }
                }
                list.Add(new Sample(p.Id, p.ProcessName, path, cpu, mem, io));
            }
        }
        return list;
    }

    private (string, ImageSource?) FileInfoFor(string path)
    {
        if (_fileInfo.TryGetValue(path, out var info)) return info;
        string desc = "";
        ImageSource? icon = null;
        try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription ?? ""; } catch { /* no version info */ }
        icon = IconFor(path);
        return _fileInfo[path] = (desc.Trim(), icon);
    }

    /// <summary>Small icon of an exe/file as a frozen WPF image (null if none).</summary>
    public static ImageSource? IconFor(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(20, 20));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    // ---------- Win32 ----------

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                       QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS counters);

    [DllImport("psapi.dll")]
    private static extern bool GetProcessMemoryInfo(IntPtr h, ref PROCESS_MEMORY_COUNTERS counters, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
}
