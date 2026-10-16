using System.Diagnostics;
using static Kelvra.NativeMethods;

namespace Kelvra;

/// <summary>
/// Per-app use for the history: which app is in front (by full exe path, so two different "launcher.exe" stay apart), the
/// CPU and GPU temperatures while it is, and what every app downloads and uploads (see <see cref="NetTrace"/>).
/// Time away from the PC (no input for 5 minutes, unless a game runs) isn't counted. UI thread, about once a second.
/// </summary>
public sealed class AppUsageTracker : IDisposable
{
    private static readonly TimeSpan Away = TimeSpan.FromMinutes(5);
    // The desktop and taskbar belong to explorer.exe, but looking at them isn't using File Explorer
    private static readonly HashSet<string> ShellClasses = new() { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    private sealed class Slice
    {
        public double Fg, CpuSum, GpuSum;
        public int CpuN, GpuN;
        public double? CpuMax, GpuMax;
        public long Down, Up;
    }

    private readonly Dictionary<string, Slice> _minute = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (string Path, string Name)> _pids = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _ownPid = Environment.ProcessId;
    private NetTrace? _net;

    /// <summary>Turns the per-app network trace on or off (Settings → History).</summary>
    public void SetNetwork(bool on)
    {
        if (on && _net == null) _net = NetTrace.Start();
        else if (!on && _net != null)
        {
            _net.Dispose();
            _net = null;
        }
    }

    public bool NetworkRunning => _net != null;

    /// <summary>The app in front right now (path and name), or null for the desktop, Kelvra itself or nothing.</summary>
    public (string Path, string Name)? Foreground()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (ShellClasses.Contains(cls.ToString())) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        var app = AppOf((int)pid);
        // Store apps are drawn by ApplicationFrameHost; the app itself owns a child window
        if (app is { Path: var p } && p.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            int child = 0;
            EnumChildWindows(hwnd, (h, _) =>
            {
                GetWindowThreadProcessId(h, out uint cpid);
                if (cpid != pid) child = (int)cpid;
                return child == 0;
            }, IntPtr.Zero);
            if (child != 0) app = AppOf(child);
        }
        return app;
    }

    /// <summary>One sample: the app in front gets the time since the last one and the temperatures now.</summary>
    public void Sample(float? cpuTemp, float? gpuTemp, bool gameRunning)
    {
        double seconds = Math.Min(_clock.Elapsed.TotalSeconds, 5); // a sleeping PC doesn't count
        _clock.Restart();

        if (_net != null)
            foreach (var (pid, (down, up)) in _net.Take())
                if (AppOf(pid) is { } owner)
                {
                    var s = SliceOf(owner);
                    s.Down += down;
                    s.Up += up;
                }

        if (!gameRunning && IdleTime() >= Away) return;
        if (Foreground() is not { } app) return;
        var slice = SliceOf(app);
        slice.Fg += seconds;
        if (cpuTemp is float c)
        {
            slice.CpuSum += c;
            slice.CpuN++;
            slice.CpuMax = Math.Max(slice.CpuMax ?? c, c);
        }
        if (gpuTemp is float g)
        {
            slice.GpuSum += g;
            slice.GpuN++;
            slice.GpuMax = Math.Max(slice.GpuMax ?? g, g);
        }
    }

    /// <summary>The finished minute, with apps by database id, and the one in front the longest.</summary>
    public (List<AppSlice> Apps, long? Foreground) TakeMinute(Func<string, string, long> appId)
    {
        var list = new List<AppSlice>();
        long? top = null;
        double topSeconds = 0;
        foreach (var (path, s) in _minute)
        {
            if (s.Fg <= 0 && s.Down == 0 && s.Up == 0) continue;
            long id = appId(path, _names.GetValueOrDefault(path, Path.GetFileNameWithoutExtension(path)));
            list.Add(new AppSlice(id, s.Fg, s.CpuSum, s.CpuN, s.CpuMax, s.GpuSum, s.GpuN, s.GpuMax, s.Down, s.Up));
            if (s.Fg > topSeconds)
            {
                topSeconds = s.Fg;
                top = id;
            }
        }
        _minute.Clear();
        if (_pids.Count > 2000) _pids.Clear(); // ids get reused; looked up again as needed
        return (list, top);
    }

    private Slice SliceOf((string Path, string Name) app)
    {
        _names[app.Path] = app.Name;
        if (!_minute.TryGetValue(app.Path, out var s)) _minute[app.Path] = s = new Slice();
        return s;
    }

    private (string Path, string Name)? AppOf(int pid)
    {
        if (pid == 0 || pid == _ownPid) return null;
        if (pid == 4) return ("System", "Windows (System)");
        // ponytail: cached by process id alone; a reused id is credited to the old app until the cache clears (2,000 ids).
        // Key by (id, start time) if that ever shows in the numbers.
        if (_pids.TryGetValue(pid, out var known)) return known;
        string path = ProcessPath(pid);
        if (path.Length == 0) return null;
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            name = new[] { info.FileDescription, info.ProductName }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim() ?? name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return _pids[pid] = (path, name);
    }

    public void Dispose() => SetNetwork(false);
}
