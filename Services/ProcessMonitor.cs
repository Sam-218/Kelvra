using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kelvra;

/// <summary>Live CPU / memory / disk numbers of one process, or of an app's processes added up.</summary>
public abstract class UsageRow : ObservableObject
{
    private double _cpu;
    private long _memory;
    private double _disk;

    public abstract int Pid { get; }
    public abstract string DisplayName { get; }
    /// <summary>The exe name as Windows reports it ("claude.exe"; kernel processes have no extension: "System").</summary>
    public abstract string ImageName { get; }
    public abstract string? Path { get; }
    public abstract ImageSource? Icon { get; }

    public double Cpu { get => _cpu; set { if (Set(ref _cpu, value)) OnPropertyChanged(nameof(CpuText)); } }
    /// <summary>Private working set in bytes, like Task Manager's Memory column (memory shared with other processes isn't counted).</summary>
    public long Memory { get => _memory; set { if (Set(ref _memory, value)) OnPropertyChanged(nameof(MemoryText)); } }
    /// <summary>Bytes read + written per second.</summary>
    public double Disk { get => _disk; set { if (Set(ref _disk, value)) OnPropertyChanged(nameof(DiskText)); } }

    public string CpuText => $"{Cpu:0.0} %";
    public string MemoryText => ByteFormat.Format(Memory);
    public string DiskText => Disk < 0.05 * 1024 * 1024 ? "0 MB/s" : $"{Disk / 1024 / 1024:0.0} MB/s";
}

/// <summary>One running process.</summary>
public sealed class ProcessRow : UsageRow
{
    private bool _isChild;
    private string _windowTitle = "";
    private string _description;

    public ProcessRow(int pid, long created, string imageName, string? path, string description, ImageSource? icon)
    {
        Pid = pid;
        Created = created;
        ImageName = imageName;
        Name = imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? imageName[..^4] : imageName;
        Path = path;
        _description = description;
        Icon = icon;
    }

    public override int Pid { get; }
    /// <summary>Start time (100 ns ticks): tells a process apart from a later one that got the same PID.</summary>
    internal long Created { get; }
    /// <summary>Exe name without ".exe" ("claude").</summary>
    public string Name { get; }
    public override string ImageName { get; }
    public override string? Path { get; }
    /// <summary>The exe's description ("Antimalware Service Executable"), or "Service Host: …" for a service host; "" if there's none worth showing.</summary>
    public string Description
    {
        get => _description;
        set
        {
            if (Set(ref _description, value)) OnPropertyChanged(nameof(DisplayName));
        }
    }
    public override ImageSource? Icon { get; }

    /// <summary>Title of the process's window: the name to show when the exe has no description ("Snipping Tool").</summary>
    public string WindowTitle
    {
        get => _windowTitle;
        set
        {
            if (Set(ref _windowTitle, value)) OnPropertyChanged(nameof(DisplayName));
        }
    }

    public override string DisplayName =>
        Description.Length > 0 ? Description : WindowTitle.Length is > 0 and <= 60 ? WindowTitle : Name;

    /// <summary>Belongs to an app with several processes, so it's listed indented under that app's row.</summary>
    public bool IsChild { get => _isChild; set => Set(ref _isChild, value); }

    internal long LastCpuTicks;
    internal long LastIoBytes;
}

/// <summary>An app and the background processes it started, added up (Task Manager's "Claude (14)").</summary>
public sealed class AppGroup : UsageRow
{
    private int _count;
    private bool _isExpanded;

    public AppGroup(ProcessRow root)
    {
        Root = root;
        root.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProcessRow.DisplayName)) OnPropertyChanged(nameof(DisplayName));
        };
    }

    /// <summary>The app's own process: its name and icon stand for the group.</summary>
    public ProcessRow Root { get; }
    public List<ProcessRow> Members { get; } = new();
    public int Count { get => _count; set => Set(ref _count, value); }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public override int Pid => Root.Pid;
    public override string DisplayName => Root.DisplayName;
    public override string ImageName => Root.ImageName;
    public override string? Path => Root.Path;
    public override ImageSource? Icon => Root.Icon;
}

/// <summary>One process as read from Windows, before it becomes a <see cref="ProcessRow"/>.</summary>
internal readonly record struct ProcessSample(
    int Pid, int ParentPid, long Created, string ImageName, string? Path, long CpuTicks, long PrivateBytes, long WorkingSet, long IoBytes);

/// <summary>
/// Samples all processes the way Task Manager's Processes tab shows them: memory is the private working set, and an
/// app's helper processes (browser tabs, Electron renderers, consoles it started) are grouped under the app.
/// </summary>
public sealed class ProcessMonitor
{
    private readonly Dictionary<int, ProcessRow> _rows = new();
    private readonly Dictionary<(int Pid, long Created), AppGroup> _groups = new();
    private readonly Dictionary<string, (string Description, ImageSource? Icon)> _fileInfo = new(StringComparer.OrdinalIgnoreCase);
    // Only touched by Collect (one refresh at a time): the exe path of each process, looked up once
    private readonly Dictionary<(int Pid, long Created), string?> _paths = new();
    private long _lastSample;

    /// <summary>What the list shows at the top level: single processes and app groups.</summary>
    public IReadOnlyList<UsageRow> Apps { get; private set; } = Array.Empty<UsageRow>();
    public int ProcessCount => _rows.Count;
    public double TotalCpu { get; private set; }
    /// <summary>Physical memory in use / installed (usable), in bytes: Task Manager's "Memory 34 %".</summary>
    public long MemoryUsed { get; private set; }
    public long MemoryTotal { get; private set; }

    // Only touched by Collect: what each service host runs ("Service Host: Windows Audio"), looked up when new ones appear
    private readonly Dictionary<(int Pid, long Created), string> _serviceHosts = new();
    private long _servicesAskedAt;

    /// <param name="Windows">Processes with a window you can switch to, and that window's title.</param>
    private sealed record Snapshot(List<ProcessSample> Processes, Dictionary<int, string> Windows, Dictionary<int, string> ServiceHosts,
                                   long MemoryUsed, long MemoryTotal);

    /// <summary>Collects on a worker thread, then updates the rows on the caller's (UI) thread. Don't overlap calls.</summary>
    public async Task RefreshAsync()
    {
        var snap = await Task.Run(Collect);
        long now = Stopwatch.GetTimestamp();
        // Wall time in 100 ns ticks, the unit of the CPU times
        double elapsed = _lastSample == 0 ? 0 : (now - _lastSample) * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;
        _lastSample = now;
        int cores = Environment.ProcessorCount;

        var seen = new HashSet<int>();
        foreach (var s in snap.Processes)
        {
            seen.Add(s.Pid);
            if (!_rows.TryGetValue(s.Pid, out var row) || row.Created != s.Created)
            {
                var (desc, icon) = s.Path != null ? FileInfoFor(s.Path) : ("", null);
                if (snap.ServiceHosts.TryGetValue(s.Pid, out var services)) desc = services;
                else if (LooksLikeFileName(desc)) desc = "";
                row = new ProcessRow(s.Pid, s.Created, s.ImageName, s.Path, desc, icon) { LastCpuTicks = s.CpuTicks, LastIoBytes = s.IoBytes };
                _rows[s.Pid] = row;
            }
            else if (elapsed > 0)
            {
                // Dividing by the cores gives Task Manager's "% of the whole CPU"
                row.Cpu = Math.Clamp((s.CpuTicks - row.LastCpuTicks) / elapsed / cores * 100, 0, 100);
                row.Disk = Math.Max(0, (s.IoBytes - row.LastIoBytes) / (elapsed / TimeSpan.TicksPerSecond));
                row.LastCpuTicks = s.CpuTicks;
                row.LastIoBytes = s.IoBytes;
            }
            row.Memory = s.PrivateBytes;
            row.WindowTitle = snap.Windows.GetValueOrDefault(s.Pid, "");
            if (snap.ServiceHosts.TryGetValue(s.Pid, out var hosted)) row.Description = hosted; // its services may be known only now
        }
        foreach (var pid in _rows.Keys.Where(p => !seen.Contains(p)).ToList()) _rows.Remove(pid);

        BuildApps(snap);
        TotalCpu = Math.Min(100, _rows.Values.Sum(r => r.Cpu));
        MemoryUsed = snap.MemoryUsed;
        MemoryTotal = snap.MemoryTotal;
    }

    /// <summary>Puts every process under its app (see <see cref="ProcessTree"/>) and adds up each app with more than one.</summary>
    private void BuildApps(Snapshot snap)
    {
        var roots = ProcessTree.Roots(snap.Processes, snap.Windows.Keys.ToHashSet());
        var byRoot = new Dictionary<int, List<ProcessRow>>();
        foreach (var (pid, row) in _rows)
        {
            int root = roots.TryGetValue(pid, out int r) && _rows.ContainsKey(r) ? r : pid;
            if (!byRoot.TryGetValue(root, out var list)) byRoot[root] = list = new List<ProcessRow>();
            list.Add(row);
        }

        var apps = new List<UsageRow>(byRoot.Count);
        var live = new HashSet<(int, long)>();
        foreach (var (rootPid, members) in byRoot)
        {
            var root = _rows[rootPid];
            if (members.Count == 1)
            {
                root.IsChild = false;
                apps.Add(root);
                continue;
            }
            var key = (rootPid, root.Created);
            if (!_groups.TryGetValue(key, out var group)) _groups[key] = group = new AppGroup(root);
            live.Add(key);
            group.Members.Clear();
            group.Members.AddRange(members);
            group.Count = members.Count;
            group.Cpu = Math.Min(100, members.Sum(m => m.Cpu));
            group.Memory = members.Sum(m => m.Memory);
            group.Disk = members.Sum(m => m.Disk);
            foreach (var m in members) m.IsChild = true;
            apps.Add(group);
        }
        foreach (var key in _groups.Keys.Where(k => !live.Contains(k)).ToList()) _groups.Remove(key);
        Apps = apps;
    }

    private Snapshot Collect()
    {
        var processes = ReadProcesses();
        var alive = new HashSet<(int, long)>();
        for (int i = 0; i < processes.Count; i++)
        {
            var p = processes[i];
            var key = (p.Pid, p.Created);
            alive.Add(key);
            if (!_paths.TryGetValue(key, out var path)) _paths[key] = path = ImagePath(p.Pid);
            processes[i] = p with { Path = path };
        }
        foreach (var key in _paths.Keys.Where(k => !alive.Contains(k)).ToList()) _paths.Remove(key);

        // Service hosts are named after the services they run. Windows is asked again when a new host starts, or (a host
        // whose service wasn't registered yet) every 10 s while one has no name
        var hosts = processes.Where(p => p.ImageName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)).ToList();
        bool unnamed = hosts.Any(p => _serviceHosts.GetValueOrDefault((p.Pid, p.Created)) == "");
        if (hosts.Any(p => !_serviceHosts.ContainsKey((p.Pid, p.Created))) || (unnamed && Environment.TickCount64 - _servicesAskedAt > 10_000))
        {
            _servicesAskedAt = Environment.TickCount64;
            var services = ServicesByProcess();
            foreach (var p in hosts) _serviceHosts[(p.Pid, p.Created)] = ServiceHostName(services.GetValueOrDefault(p.Pid));
        }
        foreach (var key in _serviceHosts.Keys.Where(k => !alive.Contains(k)).ToList()) _serviceHosts.Remove(key);
        var serviceHosts = hosts.Select(p => (p.Pid, Name: _serviceHosts[(p.Pid, p.Created)]))
                                .Where(h => h.Name.Length > 0).ToDictionary(h => h.Pid, h => h.Name);

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        bool memOk = GlobalMemoryStatusEx(ref mem);
        return new Snapshot(processes, AppWindows(), serviceHosts,
                            memOk ? (long)(mem.ullTotalPhys - mem.ullAvailPhys) : 0, memOk ? (long)mem.ullTotalPhys : 0);
    }

    /// <summary>"Service Host: Windows Audio", or with several services "Service Host: Windows Audio (+3)"; "" for none.</summary>
    internal static string ServiceHostName(IReadOnlyList<string>? services) => services switch
    {
        null or { Count: 0 } => "",
        { Count: 1 } => $"Service Host: {services[0]}",
        _ => $"Service Host: {services.Order(StringComparer.CurrentCultureIgnoreCase).First()} (+{services.Count - 1})",
    };

    /// <summary>A "description" that's just the file name again ("SnippingTool.exe") says nothing: the window title is better.</summary>
    internal static bool LooksLikeFileName(string description) =>
        description.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || description.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every process (but the Idle pseudo-process) in one call. Unlike opening each process this also works for
    /// protected ones (antivirus, anti-cheat) and gives the private working set and the parent.
    /// </summary>
    internal static List<ProcessSample> ReadProcesses()
    {
        int size = 512 * 1024;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out int needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(size * 2, needed + 64 * 1024); // processes may start in between: leave room
                    continue;
                }
                if (status < 0) throw new System.ComponentModel.Win32Exception(RtlNtStatusToDosError(status));
                return Parse(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static List<ProcessSample> Parse(IntPtr buffer)
    {
        var list = new List<ProcessSample>(400);
        long offset = 0;
        while (true)
        {
            var e = Marshal.PtrToStructure<SYSTEM_PROCESS_INFORMATION>(buffer + (nint)offset);
            int pid = (int)e.UniqueProcessId;
            if (pid != 0)
            {
                string name = e.ImageNameBuffer != IntPtr.Zero && e.ImageNameLength > 0
                    ? Marshal.PtrToStringUni(e.ImageNameBuffer, e.ImageNameLength / 2)
                    : "System";
                list.Add(new ProcessSample(pid, (int)e.InheritedFromUniqueProcessId, e.CreateTime, name, null,
                                           e.KernelTime + e.UserTime, e.WorkingSetPrivateSize, (long)(ulong)e.WorkingSetSize,
                                           e.ReadTransferCount + e.WriteTransferCount));
            }
            if (e.NextEntryOffset == 0) return list;
            offset += e.NextEntryOffset;
        }
    }

    /// <summary>Full exe path (most elevated and protected processes allow this much too); null if Windows won't say.</summary>
    private static string? ImagePath(int pid)
    {
        IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    /// <summary>
    /// Processes with a window you can switch to, with the first titled one's title. They're apps of their own, never
    /// part of the program that started them.
    /// </summary>
    private static Dictionary<int, string> AppWindows()
    {
        var windows = new Dictionary<int, string>();
        EnumWindowsProc callback = (hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero &&
                (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64() & NativeMethods.WS_EX_TOOLWINDOW) == 0)
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (windows.GetValueOrDefault((int)pid, "").Length == 0)
                {
                    int length = NativeMethods.GetWindowTextLength(hwnd);
                    var text = new StringBuilder(Math.Max(1, length + 1));
                    if (length > 0) NativeMethods.GetWindowText(hwnd, text, text.Capacity);
                    windows[(int)pid] = text.ToString().Trim();
                }
            }
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return windows;
    }

    /// <summary>Display names of the running services, by the process they run in.</summary>
    private static Dictionary<int, List<string>> ServicesByProcess()
    {
        var result = new Dictionary<int, List<string>>();
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return result;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int size = 128 * 1024, resume = 0;
            buffer = Marshal.AllocHGlobal(size);
            while (true)
            {
                bool ok = EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, buffer, size,
                                               out int needed, out int count, ref resume, null);
                int error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != ERROR_MORE_DATA) return result;
                // ENUM_SERVICE_STATUS_PROCESSW: name, display name, then SERVICE_STATUS_PROCESS (9 DWORDs, the 8th is the PID)
                int entry = IntPtr.Size * 2 + 9 * 4;
                entry = (entry + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
                for (int i = 0; i < count; i++)
                {
                    IntPtr at = buffer + i * entry;
                    int pid = Marshal.ReadInt32(at, IntPtr.Size * 2 + 7 * 4);
                    string? display = Marshal.PtrToStringUni(Marshal.ReadIntPtr(at, IntPtr.Size));
                    if (pid == 0 || string.IsNullOrWhiteSpace(display)) continue;
                    if (!result.TryGetValue(pid, out var list)) result[pid] = list = new List<string>();
                    list.Add(display.Trim());
                }
                if (ok) return result;
                if (count == 0)
                {
                    // Not even one entry fit: a bigger buffer, same position
                    if (needed <= size) return result;
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                    size = needed;
                    buffer = Marshal.AllocHGlobal(size);
                }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseServiceHandle(scm);
        }
    }

    /// <summary>Description and icon of an exe, cached per path (many processes share one exe, e.g. browsers).</summary>
    private (string, ImageSource?) FileInfoFor(string path)
    {
        if (_fileInfo.TryGetValue(path, out var info)) return info;
        string desc = "";
        try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription ?? ""; } catch { /* no version info */ }
        var icon = IconFor(path);
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

    private const int SystemProcessInformation = 5;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const uint GW_OWNER = 4;

    /// <summary>SYSTEM_PROCESS_INFORMATION (x64 layout, 256 bytes); the thread entries that follow it aren't read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public ushort ImageNameLength;
        public ushort ImageNameMaximumLength;
        public IntPtr ImageNameBuffer;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public UIntPtr UniqueProcessKey;
        public UIntPtr PeakVirtualSize;
        public UIntPtr VirtualSize;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivatePageCount;
        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const int SC_ENUM_PROCESS_INFO = 0;
    private const uint SERVICE_WIN32 = 0x30, SERVICE_ACTIVE = 0x1;
    private const int ERROR_MORE_DATA = 234;

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumServicesStatusEx(IntPtr scm, int infoLevel, uint serviceType, uint serviceState, IntPtr services,
                                                    int bufSize, out int bytesNeeded, out int servicesReturned, ref int resumeHandle, string? groupName);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
}

/// <summary>Which app each process belongs to (pure rules, unit-tested).</summary>
internal static class ProcessTree
{
    /// <summary>
    /// Parents whose children are separate apps: the shell starts everything you open, and Windows' own start-up and
    /// service hosts start services and system tasks.
    /// </summary>
    internal static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "svchost.exe", "services.exe", "wininit.exe", "winlogon.exe", "smss.exe", "csrss.exe", "lsass.exe",
        "userinit.exe", "sihost.exe", "taskhostw.exe", "WmiPrvSE.exe", "System", "Registry",
    };

    /// <summary>
    /// Like Task Manager: a process belongs to the app that started it, up the chain of parents, unless it has a window
    /// of its own (a game a launcher started is its own app), its parent is gone (or the PID was reused by a newer
    /// process), or the parent is one of the <see cref="Hosts"/>. Returns the app's root PID for every process.
    /// </summary>
    public static Dictionary<int, int> Roots(IReadOnlyList<ProcessSample> processes, IReadOnlySet<int> withWindow)
    {
        var byPid = new Dictionary<int, ProcessSample>(processes.Count);
        foreach (var p in processes) byPid[p.Pid] = p;

        var roots = new Dictionary<int, int>(processes.Count);
        var chain = new List<int>();
        foreach (var p in processes)
        {
            chain.Clear();
            var current = p;
            int root;
            while (true)
            {
                if (roots.TryGetValue(current.Pid, out root)) break;
                chain.Add(current.Pid);
                if (withWindow.Contains(current.Pid) || chain.Count > 64 ||
                    !byPid.TryGetValue(current.ParentPid, out var parent) || parent.Pid == current.Pid || parent.Pid <= 4 ||
                    parent.Created > current.Created || Hosts.Contains(parent.ImageName))
                {
                    root = current.Pid;
                    break;
                }
                current = parent;
            }
            foreach (int pid in chain) roots[pid] = root;
        }
        return roots;
    }
}
