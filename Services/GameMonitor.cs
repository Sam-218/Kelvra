using System.Diagnostics;
using System.Windows.Threading;
using static Kelvra.NativeMethods;

namespace Kelvra;

/// <summary>A game Kelvra is measuring: from its first frames until the process exits (alt-tabbing doesn't end it).</summary>
public sealed class GameSession
{
    public GameSession(int processId, string name, FrameStats stats)
    {
        ProcessId = processId;
        Name = name;
        Stats = stats;
    }

    public int ProcessId { get; }
    /// <summary>Readable name ("Dying Light 2"), see <see cref="GameRules.DisplayName"/>.</summary>
    public string Name { get; }
    public FrameStats Stats { get; }
    public DateTime Started { get; } = DateTime.Now;

    /// <summary>Hottest CPU / GPU temperature (°C) seen while the game ran, for the session summary.</summary>
    public float? MaxCpuTemp { get; set; }
    public float? MaxGpuTemp { get; set; }
}

/// <summary>A benchmark run: every frame of the game between two presses of the benchmark shortcut.</summary>
public sealed class BenchmarkRun
{
    internal const int MaxFrames = 2_000_000; // ~2 h at 300 FPS

    public BenchmarkRun(GameSession game) => Game = game;

    public GameSession Game { get; }
    public FrameStats Stats { get; } = new();
    public List<BenchFrame> Frames { get; } = new();
    public DateTime Started { get; } = DateTime.Now;
    public float? MaxCpuTemp { get; set; }
    public float? MaxGpuTemp { get; set; }
}

/// <summary>
/// Decides which program is "the game": the foreground app, once it has drawn frames steadily for 2 s, runs
/// fullscreen (or with a hardware present mode, or is on the Always list) and isn't a known non-game. Feeds its
/// frames into a <see cref="FrameStats"/> and tells the overlays when a game starts, ends, or loses the foreground.
/// </summary>
public sealed class GameMonitor : IDisposable
{
    private const double MinFps = 10;
    private static readonly TimeSpan SteadyFor = TimeSpan.FromSeconds(2);

    private readonly AppSettings _settings;
    // Fast enough that the gaming overlay appears/hides right after alt-tab; each tick is a few cheap Win32 calls
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly object _lock = new();
    private readonly int _ownPid = Environment.ProcessId;

    private int _foregroundPid;
    private GameRules.AppInfo _foreground = GameRules.AppInfo.None;
    private IntPtr _foregroundHwnd;
    private int _candidatePid;
    private FrameStats? _candidate;
    private GameSession? _active;
    private BenchmarkRun? _benchmark;
    private IntPtr _gameHwnd;
    private IntPtr _gameProcess; // kept open while the game runs: a cheap exit check, and immune to its PID being reused
    private volatile bool _gameVisible;
    private bool _wasVisible;

    private readonly LateFrameFilter _late = new();
    private bool _activeGap; // the active game's frames were skipped since the last counted one (under _lock)
    private long _lateFrames, _loggedLateFrames;

    public GameMonitor(AppSettings settings)
    {
        _settings = settings;
        Runner.Frame += OnFrame;
        Runner.Started += () =>
        {
            lock (_lock) _late.Reset(); // a new PresentMon run starts its clock at 0
        };
        _timer.Tick += (_, _) => Tick();
    }

    public PresentMonRunner Runner { get; } = new();

    /// <summary>The game being measured (it may be in the background right now), or null.</summary>
    public GameSession? ActiveGame => _active;


    /// <summary>
    /// The game can be seen: it's in front, or another monitor's window has the focus while the game keeps running on
    /// its own screen. Not when it's minimized or covered on its screen. Gaming overlays show, and frames count, only then.
    /// </summary>
    public bool GameVisible => _gameVisible;

    /// <summary>Exe names that drew frames in the foreground this run (offered in Settings → Gaming → Always/Never).</summary>
    public SortedSet<string> SeenApps { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A game started or ended, or came to / left the foreground.</summary>
    public event Action? Changed;

    /// <summary>A program was recognised as a game and is now measured.</summary>
    public event Action<GameSession>? GameStarted;

    /// <summary>The game's process exited (or another game took over): the session is finished.</summary>
    public event Action<GameSession>? GameEnded;

    /// <summary>Starts or stops measuring, following Settings → Gaming → Measure FPS.</summary>
    public void ApplyEnabled()
    {
        if (_settings.GameFpsEnabled)
        {
            Runner.Start();
            _timer.Start();
            return;
        }
        _timer.Stop();
        Task.Run(Runner.Stop); // ending the trace session can take a few seconds: not on the UI thread
        lock (_lock)
        {
            _candidate = null;
            _candidatePid = 0;
        }
        if (_active != null) End();
    }

    /// <summary>Current numbers of the active game (empty when there's none).</summary>
    public FrameSnapshot Current() => _active?.Stats.Snapshot() ?? FrameSnapshot.Empty;

    /// <summary>The benchmark being recorded, or null.</summary>
    public BenchmarkRun? Benchmark => _benchmark;

    /// <summary>Starts recording every frame of the active game. False if no game runs.</summary>
    public bool StartBenchmark()
    {
        lock (_lock)
        {
            if (_active == null) return false;
            _benchmark = new BenchmarkRun(_active);
            return true;
        }
    }

    /// <summary>Stops recording and returns the run (null if none was recording).</summary>
    public BenchmarkRun? StopBenchmark()
    {
        lock (_lock)
        {
            var run = _benchmark;
            _benchmark = null;
            return run;
        }
    }

    /// <summary>A benchmark ended on its own because its game closed (the run is passed along to be saved).</summary>
    public event Action<BenchmarkRun>? BenchmarkEnded;

    // Reader thread: only the foreground app's and the visible game's frames are kept, and only ones that arrive on time
    private void OnFrame(FrameSample s)
    {
        FrameStats? active, candidate;
        BenchmarkRun? bench;
        bool gap;
        lock (_lock)
        {
            active = _active?.ProcessId == s.ProcessId ? _active.Stats : null;
            candidate = _candidatePid == s.ProcessId ? _candidate : null;
            if (active == null && candidate == null) return;

            if (_late.IsLate(s.TimeMs, Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency))
            {
                _lateFrames++; // PresentMon catching up on a backlog: these numbers would be stale
                if (active != null) _activeGap = true;
                return;
            }

            // A game in the background (minimized, covered) runs throttled or not at all: not what you're playing
            if (!_gameVisible && !ReferenceEquals(candidate, active))
            {
                if (active != null) _activeGap = true;
                active = null;
            }
            gap = active != null && _activeGap;
            if (active != null) _activeGap = false;
            bench = active != null && _benchmark?.Game.ProcessId == s.ProcessId ? _benchmark : null;
            if (bench != null && bench.Frames.Count < BenchmarkRun.MaxFrames) bench.Frames.Add(BenchFrame.From(s));
        }
        if (gap)
        {
            active!.MarkGap();
            bench?.Stats.MarkGap();
        }
        active?.Add(s);
        if (candidate != null && !ReferenceEquals(candidate, active)) candidate.Add(s);
        bench?.Stats.Add(s);
    }

    private void Tick()
    {
        WatchForeground();

        if (_active != null && (!GameRunning() || (_active.ProcessId == _foregroundPid && GameRules.IsNever(_foreground, _settings))))
            End();

        FrameStats? candidate;
        lock (_lock) candidate = _candidate;
        if (candidate != null && (_active == null || _active.ProcessId != _candidatePid))
        {
            var snap = candidate.Snapshot();
            if (snap.Fps is double fps && fps >= MinFps && _foreground.Exe.Length > 0) SeenApps.Add(_foreground.Exe);
            if (snap.Fps >= MinFps && snap.SessionLength >= SteadyFor &&
                GameRules.LooksLikeGame(_foreground, _settings, IsFullscreen(_foregroundHwnd), snap.PresentMode))
                Begin(new GameSession(_candidatePid, GameRules.DisplayName(_foreground), candidate));
        }

        if (_active != null && _foregroundPid == _active.ProcessId) _gameHwnd = _foregroundHwnd; // follows a re-created game window
        bool visible = IsGameShowing();
        _gameVisible = visible;
        if (visible != _wasVisible)
        {
            _wasVisible = visible;
            Changed?.Invoke();
        }

        if (_lateFrames - _loggedLateFrames >= 100)
        {
            Log.Info($"Skipped {_lateFrames - _loggedLateFrames:N0} late frames while PresentMon caught up");
            _loggedLateFrames = _lateFrames;
        }
    }

    /// <summary>See <see cref="GameVisible"/>. Another window on the game's own monitor covers it; one on another monitor doesn't.</summary>
    private bool IsGameShowing()
    {
        if (_active == null || _gameHwnd == IntPtr.Zero || !IsWindow(_gameHwnd) || IsIconic(_gameHwnd)) return false;
        if (_foregroundPid == _active.ProcessId) return true;
        if (_foregroundHwnd == IntPtr.Zero) return _gameVisible; // mid-switch: keep the last answer
        return MonitorFromWindow(_gameHwnd, MONITOR_DEFAULTTONEAREST) != MonitorFromWindow(_foregroundHwnd, MONITOR_DEFAULTTONEAREST);
    }

    /// <summary>Follows the foreground window. The Always/Never lists are checked every tick, so edits apply at once.</summary>
    private void WatchForeground()
    {
        IntPtr hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out uint pid);
        _foregroundHwnd = hwnd;
        if ((int)pid != _foregroundPid)
        {
            _foregroundPid = (int)pid;
            _foreground = pid == 0 || pid == _ownPid ? GameRules.AppInfo.None : ReadAppInfo((int)pid, hwnd);
        }

        bool candidate = _foregroundPid != 0 && GameRules.IsCandidate(_foreground, _settings);
        lock (_lock)
        {
            if (!candidate)
            {
                _candidatePid = 0;
                _candidate = null;
            }
            else if (_candidatePid != _foregroundPid)
            {
                _candidatePid = _foregroundPid;
                // Back to the game: keep adding to its session instead of starting over
                _candidate = _active?.ProcessId == _foregroundPid ? _active.Stats : new FrameStats();
            }
        }
    }

    /// <summary>Exe name and path, window title and product name: what the Always/Never lists and the game-library check look at.</summary>
    private static GameRules.AppInfo ReadAppInfo(int pid, IntPtr hwnd)
    {
        string path = ProcessPath(pid);
        string exe = path.Length > 0 ? Path.GetFileNameWithoutExtension(path) : ProcessName(pid);
        string product = "";
        if (path.Length > 0)
        {
            try { product = FileVersionInfo.GetVersionInfo(path).ProductName?.Trim() ?? ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return new GameRules.AppInfo(exe, path, WindowTitle(hwnd), product);
    }
    private void Begin(GameSession game)
    {
        if (_active != null) End();
        lock (_lock)
        {
            _active = game;
            _activeGap = false;
        }
        _gameHwnd = _foregroundHwnd;
        _gameProcess = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)game.ProcessId);
        _gameVisible = _wasVisible = true;
        Log.Info($"Game detected: {game.Name}");
        GameStarted?.Invoke(game);
        Changed?.Invoke();
    }

    private void End()
    {
        GameSession? ended;
        lock (_lock)
        {
            ended = _active;
            _active = null;
        }
        _gameHwnd = IntPtr.Zero;
        if (_gameProcess != IntPtr.Zero) CloseHandle(_gameProcess);
        _gameProcess = IntPtr.Zero;
        _gameVisible = _wasVisible = false;
        if (ended == null) return;
        Log.Info($"Game ended: {ended.Name}");
        if (_benchmark?.Game == ended && StopBenchmark() is { } run) BenchmarkEnded?.Invoke(run);
        GameEnded?.Invoke(ended);
        Changed?.Invoke();
    }

    /// <summary>The active game's process hasn't exited (its handle isn't signalled).</summary>
    private bool GameRunning() =>
        _gameProcess != IntPtr.Zero ? WaitForSingleObject(_gameProcess, 0) != WAIT_OBJECT_0 : IsAlive(_active!.ProcessId);

    /// <summary>Fallback when the game's process couldn't be opened (rare: some anti-cheat protections).</summary>
    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no process with that id any more
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true; // protected (anti-cheat) processes can't always be asked; it's there
        }
    }

    /// <summary>Full exe path. Works for most anti-cheat protected games too (limited query rights), "" if not.</summary>
    private static string ProcessPath(int pid)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return "";
        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : "";
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var buffer = new System.Text.StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string ProcessName(int pid)
    {
        if (pid == 0) return "";
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    /// <summary>The window is exclusive or borderless fullscreen (see <see cref="GameRules.IsFullscreen"/>).</summary>
    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;
        var screen = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
        bool caption = (GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_CAPTION) == WS_CAPTION;
        return GameRules.IsFullscreen((r.Left, r.Top, r.Right, r.Bottom), (screen.Left, screen.Top, screen.Right, screen.Bottom), caption);
    }

    public void Dispose()
    {
        _timer.Stop();
        Runner.Dispose();
        if (_gameProcess != IntPtr.Zero) CloseHandle(_gameProcess);
        _gameProcess = IntPtr.Zero;
    }
}

/// <summary>Which programs can count as games (pure rules, unit-tested).</summary>
public static class GameRules
{
    /// <summary>What's known about the foreground program. <see cref="Exe"/> is without ".exe".</summary>
    public sealed record AppInfo(string Exe, string Path, string Title, string Product)
    {
        public static readonly AppInfo None = new("", "", "", "");
    }

    /// <summary>Programs that draw lots of frames but aren't games: browsers, launchers, chat, recording, Windows itself.</summary>
    internal static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "applicationframehost", "textinputhost",
        "lockapp", "systemsettings", "taskmgr", "msedgewebview2", "widgets", "snippingtool", "screenclippinghost",
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "arc", "iexplore",
        "discord", "slack", "teams", "ms-teams", "zoom", "skype", "telegram", "whatsapp", "spotify",
        "obs64", "obs32", "streamlabs obs", "xsplit.core", "nvcontainer", "nvidia overlay", "nvidia app", "radeonsoftware", "amdrsserv",
        "steam", "steamwebhelper", "epicgameslauncher", "battle.net", "eadesktop", "origin", "ubisoftconnect", "upc", "galaxyclient",
        "riotclientux", "riotclientservices", "xboxapp", "gamebar", "gamebarftserver", "playnite.desktopapp", "playnite.fullscreenapp",
        "vlc", "mpc-hc64", "mpc-be64", "wmplayer", "video.ui", "microsoft.media.player", "potplayermini64",
        "code", "devenv", "rider64", "idea64", "windowsterminal", "wt", "cmd", "conhost", "powershell", "pwsh",
        "wallpaper32", "wallpaper64", "lively", "kelvra",
        "mstsc", "msrdc", "mpv", "potplayer", "potplayer64", "kodi", "plex", "plexamp", "netflix", "smplayer", "mpc-qt",
        "vmware", "vmware-vmx", "vmplayer", "virtualboxvm", "vmconnect", "parsecd", "moonlight", "sunshine",
    };

    /// <summary>Folders game stores install games into. A program from one of them is a game, even windowed.</summary>
    private static readonly string[] GameLibraries =
    {
        @"\steamapps\common\", @"\Epic Games\", @"\GOG Galaxy\Games\", @"\GOG Games\", @"\XboxGames\", @"\Riot Games\",
        @"\Ubisoft Game Launcher\games\", @"\EA Games\", @"\Origin Games\", @"\Rockstar Games\", @"\Battle.net\Games\",
        @"\Amazon Games\Library\", @"\itch\apps\",
    };

    internal static string Normalize(string exe)
    {
        string name = exe.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>"War Thunder", "warthunder" and "War-Thunder" all become "warthunder".</summary>
    internal static string Key(string text) =>
        new(Normalize(text).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// A list entry matches the exe name, the window title, the product name or one of the folders the exe is in, so
    /// "Dying Light 2" finds DyingLightGame_x64_rwdi.exe (in "…\common\Dying Light 2\…") and "Warthunder" finds aces.exe.
    /// </summary>
    public static bool Matches(string entry, AppInfo app)
    {
        string key = Key(entry);
        if (key.Length == 0) return false;
        if (Key(app.Exe) == key || Key(app.Title) == key || Key(app.Product) == key) return true;
        return app.Path.Split('\\', '/').SkipLast(1).Any(folder => Key(folder) == key);
    }

    public static bool IsAlways(AppInfo app, AppSettings s) => s.GameAlways.Any(e => Matches(e, app));

    public static bool IsNever(AppInfo app, AppSettings s) => s.GameNever.Any(e => Matches(e, app));


    /// <summary>Worth watching for frames: on the Always list, or not on the Never list and not a known non-game.</summary>
    public static bool IsCandidate(AppInfo app, AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(app.Exe)) return false;
        if (IsAlways(app, s)) return true;
        return !IsNever(app, s) && !NotGames.Contains(Normalize(app.Exe));
    }


    public static bool IsInGameLibrary(string path) => LibraryFolder(path) != null;

    /// <summary>The game's folder inside a store library ("…\steamapps\common\Dying Light 2\…" → "Dying Light 2"), or null.</summary>
    internal static string? LibraryFolder(string path)
    {
        foreach (var dir in GameLibraries)
        {
            int at = path.IndexOf(dir, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            string rest = path[(at + dir.Length)..];
            int slash = rest.IndexOf('\\');
            if (slash > 0) return rest[..slash]; // the exe must be inside the game's folder, not the library itself
        }
        return null;
    }

    /// <summary>
    /// A readable name for the overlay and the session list: the game's store folder, else the window title (without
    /// " - …" / " (…)" additions), else the product name, else the exe name.
    /// </summary>
    public static string DisplayName(AppInfo app)
    {
        if (LibraryFolder(app.Path) is string folder) return folder;
        string title = app.Title;
        foreach (var cut in new[] { " - ", " – ", " | ", " (" })
        {
            int i = title.IndexOf(cut, StringComparison.Ordinal);
            if (i > 0) title = title[..i];
        }
        title = title.Trim();
        if (title.Length is > 1 and <= 60) return title;
        if (app.Product.Length is > 1 and <= 60) return app.Product;
        return app.Exe;
    }

    /// <summary>
    /// A candidate drawing frames steadily counts as a game when it's on the Always list, installed by a game store,
    /// covers its monitor, or presents like a fullscreen game (a hardware present mode).
    /// </summary>
    public static bool LooksLikeGame(AppInfo app, AppSettings s, bool fullscreen, string presentMode) =>
        IsAlways(app, s) || IsInGameLibrary(app.Path) || fullscreen || IsHardwarePresentMode(presentMode);

    /// <summary>
    /// Exactly the monitor's size, without a title bar. A maximized window doesn't count: its rect sticks out ~8 px past
    /// the screen on every side and it has a caption – that once made Blender or a video player on a second monitor
    /// (no taskbar there) look like a game.
    /// </summary>
    public static bool IsFullscreen((int Left, int Top, int Right, int Bottom) window, (int Left, int Top, int Right, int Bottom) monitor, bool hasCaption) =>
        !hasCaption && window == monitor;

    /// <summary>The game owns the screen or a hardware plane (fullscreen-style presentation, not a composed window).</summary>
    public static bool IsHardwarePresentMode(string mode) => mode.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase);
}
/// <summary>
/// Spots frames PresentMon delivers late (a backlog after alt-tab). PresentMon's time column counts from its own start;
/// the smallest gap seen between that and the arrival time is "on time", and a frame arriving more than
/// <see cref="LateMs"/> later than that is late. Frames without a time are never late. If every frame has been late for
/// <see cref="GiveUpMs"/> – longer than any backlog lasts – the clocks have drifted apart, and "on time" starts over.
/// </summary>
internal sealed class LateFrameFilter
{
    internal const double LateMs = 1500;
    internal const double GiveUpMs = 10_000;
    private double _onTime = double.MaxValue;
    private double? _lateSince;

    public void Reset()
    {
        _onTime = double.MaxValue;
        _lateSince = null;
    }

    /// <param name="arrivalMs">When the frame arrived, in ms on a steady clock (QPC, like PresentMon's).</param>
    public bool IsLate(double frameTimeMs, double arrivalMs)
    {
        if (frameTimeMs <= 0) return false;
        double offset = arrivalMs - frameTimeMs;
        if (offset < _onTime) _onTime = offset;
        if (offset - _onTime <= LateMs)
        {
            _lateSince = null;
            return false;
        }
        _lateSince ??= arrivalMs;
        if (arrivalMs - _lateSince.Value < GiveUpMs) return true;
        _onTime = offset;
        _lateSince = null;
        return false;
    }
}