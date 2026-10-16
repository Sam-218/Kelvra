using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Kelvra;

/// <summary>
/// Entry point and service hub: loads settings, creates the shared services (sensors, overlays, live graphs, alerts,
/// CSV logger, tray icon, and the long-term history with its crash and change watchers) and runs the sensor polling
/// loop. Pages reach them via <see cref="Current"/>.
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;
    private readonly CancellationTokenSource _cts = new();
    private DispatcherTimer? _saveTimer;
    private DispatcherTimer? _historyTimer;
    private bool _dirty;
    private string? _reportedSaveError;

    public static new App Current => (App)Application.Current;

    public AppSettings Settings { get; private set; } = null!;
    public SensorStore Sensors { get; private set; } = null!;
    public OverlayManager Overlays { get; private set; } = null!;
    public TrayIcon Tray { get; private set; } = null!;
    public MainWindow MainView { get; private set; } = null!;
    public HistoryService History { get; } = new();
    public AlertService Alerts { get; } = new();
    public CsvLogger Logger { get; } = new();
    /// <summary>Finds the game being played and measures its frames (PresentMon). Feeds the "Game" sensors and game overlays.</summary>
    public GameMonitor Game { get; private set; } = null!;
    /// <summary>Fan curves (Fans page). Runs after every poll.</summary>
    public FanController Fans { get; } = new();
    private readonly System.Diagnostics.Stopwatch _fanClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly PollScheduler _scheduler = new();

    /// <summary>Long-term history (Reports, Crashes, Changes); null if history.db couldn't be opened.</summary>
    public HistoryRecorder? Recorder { get; private set; }
    /// <summary>Reads crashes from the Windows event logs into the history.</summary>
    public CrashWatcher? Crashes { get; private set; }
    /// <summary>Notices driver, Windows, app and startup changes.</summary>
    public ChangeWatcher? Changes { get; private set; }

    private void ReleaseFansQuietly()
    {
        try { Sensors?.ReleaseFans(); }
        catch (Exception ex) { Log.Error("Handing fans back to automatic", ex); }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --- Single instance ---
        bool afterUpdate = e.Args.Contains(Updater.AfterUpdateArg);
        // A development copy with its own data folder may run next to the real Kelvra
        _mutex = new Mutex(true, AppSettings.CustomDir ? "Kelvra.SingleInstance.Dev" : "Kelvra.SingleInstance", out bool isFirst);
        if (!isFirst && afterUpdate) isFirst = WaitForPreviousInstance(_mutex);
        if (!isFirst)
        {
            MessageBox.Show("Kelvra is already running (check the system tray).", "Kelvra",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Crash log: everything that escapes is written to %APPDATA%\Kelvra\logs (Settings → Copy diagnostics)
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI error", args.Exception);
            MessageBox.Show($"{args.Exception.Message}\n\nThe details were saved to Kelvra's log. Settings → About → Copy diagnostics " +
                            "puts them on the clipboard for a bug report.", "Kelvra error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Error(args.IsTerminating ? "Fatal error" : "Unhandled error", ex);
            if (args.IsTerminating) ReleaseFansQuietly(); // never leave a fan at a fixed speed behind
        };
        SessionEnding += (_, _) => // sign-out / shutdown: Windows ends Kelvra without Quit()
        {
            ReleaseFansQuietly();
            Recorder?.Stop();
            if (Game == null) return;
            Settings.Save(); // changes still waiting for the debounced save
            if (Settings.KeepHistory) History.Save(HistoryService.DefaultFile); // otherwise up to 10 min are lost
            try
            {
                SaveRunningGame();
                Task.WaitAll(_benchmarkWrites.ToArray(), TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                Log.Error("Saving the game session at sign-out", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved background task error", args.Exception);
            args.SetObserved();
        };
        Log.Info($"Kelvra {typeof(App).Assembly.GetName().Version?.ToString(3)} starting");

        // --- Settings and services (order matters: the windows below read these in their constructors) ---
        Settings = AppSettings.Load();
        SensorFormat.UseFahrenheit = Settings.Fahrenheit;
        ThemeManager.Initialize(Settings.Theme, Settings.Accent);
        WatchAlertRules();

        Sensors = new SensorStore();
        Sensors.PlanPoll = PlanPoll;
        OpenHistory();
        Game = new GameMonitor(Settings);
        bool gameSensorsShown = false;
        Sensors.ExtraReadings = () =>
        {
            // Once shown, keep the Game device (empty values) even if measuring is switched off
            if (!Settings.GameFpsEnabled && !gameSensorsShown) return null;
            gameSensorsShown = true;
            return GameSensors.Readings(Game.Current(), Game.ActiveGame != null);
        };
        Overlays = new OverlayManager(Settings, Sensors)
        {
            GameVisible = () => Game.GameVisible,
            GameActive = () => Game.ActiveGame != null,
        };
        Overlays.Dirty += MarkDirty;
        Game.Changed += Overlays.Sync;

        MainView = new MainWindow();
        new WindowInteropHelper(MainView).EnsureHandle(); // hotkeys need a handle even when starting in the tray
        Tray = new TrayIcon();

        bool startHidden = Settings.StartMinimized || e.Args.Contains(Autostart.MinimizedArg);
        if (!startHidden) MainView.Show();

        if (Settings.RecoveredFrom is string broken)
        {
            string message = $"Your settings file couldn't be read, so Kelvra started with default settings.\nThe old file was kept as:\n{broken}";
            if (startHidden) Tray.Notify("Kelvra settings were reset", message);
            else MessageBox.Show(MainView, message, "Kelvra", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Keep the logon task pointing at this exe, in case it was moved
        if (Settings.StartWithWindows) _ = RefreshAutostartAsync();

        // Debounced save: changes only set a flag, so dragging a slider or overlay doesn't rewrite the file each frame
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _saveTimer.Tick += (_, _) =>
        {
            if (!_dirty) return;
            _dirty = false;
            SaveSettings();
        };
        _saveTimer.Start();

        if (Settings.KeepHistory) History.Load(HistoryService.DefaultFile);
        _historyTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _historyTimer.Tick += (_, _) =>
        {
            if (Settings.KeepHistory) History.Save(HistoryService.DefaultFile);
        };
        _historyTimer.Start();

        // Ask before the hardware is opened, so an install takes effect without reconnecting.
        PawnIoPromptWindow.ShowIfNeeded(Settings, MainView);
        MarkDirty();

        if (afterUpdate) Tray.Notify("Kelvra was updated", $"You're now on version {Updater.CurrentVersion.ToString(3)}.");
        StartUpdateChecks();
        Game.ApplyEnabled();

        // --- Data flow: each poll updates overlays, history and alerts; each history sample feeds the CSV logger ---
        Sensors.FirstLoad += OnFirstLoad;
        Sensors.Updated += Overlays.UpdateValues;
        Sensors.Updated += () =>
        {
            History.Record(Sensors);
            Alerts.Evaluate(Settings, Sensors);
            double seconds = _fanClock.Elapsed.TotalSeconds;
            _fanClock.Restart();
            Fans.Tick(Settings.Fans, Sensors, seconds, Sensors.RequestFan);
            Recorder?.Sample(Sensors, Game.ActiveGame != null, DateTime.UtcNow);
        };
        Sensors.Updated += TrackGameTemperatures;
        Game.GameEnded += game => _ = SaveEndedGameAsync(game);
        Game.GameStarted += game =>
        {
            // Only for people who use the gaming overlay; everyone else isn't interrupted in their games
            if (!Settings.Overlays.Any(p => p.Enabled && p.ShowOnlyInGames) || !Settings.GameOverlaysVisible) return;
            string key = Settings.HotkeyBenchmark.Length > 0 ? $" · {Settings.HotkeyBenchmark} records a benchmark" : "";
            OsdToast.Notify($"Kelvra: {game.Name} detected", "Measuring FPS" + key, seconds: 3);
        };
        Game.BenchmarkEnded += SaveBenchmark;
        History.Sampled += Logger.OnSample;
        Alerts.Fired += e =>
        {
            Tray.Notify(e.Title, e.Message);
            if (Settings.AlertSound) SystemSounds.Exclamation.Play();
        };
        Overlays.Sync();
        if (Settings.MiniOpen) ShowMini();
        // Runs until Quit(); the delegate re-reads the refresh rate so Settings changes apply on the next poll
        await Sensors.RunAsync(() => Settings.RefreshMs, _cts.Token);
    }

    /// <summary>
    /// Which devices the next poll reads (see <see cref="PollScheduler"/>): everything while the window, mini mode or a
    /// CSV log is open; otherwise what overlays, alerts and fan curves use, plus what the history records.
    /// </summary>
    private Func<string, LibreHardwareMonitor.Hardware.HardwareType, bool> PlanPoll()
    {
        bool everything = MainWindowShown || Mini != null || Logger.IsRecording;
        var live = PollScheduler.DevicesOf(
            Overlays.ShownSensorIds
                .Concat(Settings.Alerts.Where(a => a.Enabled).Select(a => a.SensorId))
                .Concat(Settings.Fans.Where(f => f.Mode != FanMode.Auto).SelectMany(f => new[] { f.ControlId, f.SourceId })),
            Sensors.ById);
        return _scheduler.Plan(everything, live, Recorder?.Devices ?? new HashSet<string>(), DateTime.UtcNow);
    }

    /// <summary>
    /// Opens history.db and starts the crash and change watchers. A file that can't be opened (damaged) is set aside and
    /// a new one started; if that fails too, Kelvra runs without long-term history.
    /// </summary>
    private void OpenHistory()
    {
        HistoryDb? db = null;
        for (int attempt = 0; attempt < 2 && db == null; attempt++)
        {
            try
            {
                db = HistoryDb.Open(HistoryDb.DefaultFile);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                Log.Error("Opening history.db", ex);
                if (attempt > 0 || !File.Exists(HistoryDb.DefaultFile)) break;
                try { File.Move(HistoryDb.DefaultFile, $"{HistoryDb.DefaultFile}.bad-{DateTime.Now:yyyyMMdd-HHmmss}"); }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { break; }
            }
        }
        if (db == null) return;

        Recorder = new HistoryRecorder(db, Settings) { AppliedDuty = Fans.Applied };
        Recorder.FanStopped += (title, message) =>
        {
            Tray.Notify(title, message);
            if (Settings.AlertSound) SystemSounds.Exclamation.Play();
        };
        ApplyHistorySettings();
        Crashes = new CrashWatcher(db);
        Crashes.Added += found => Dispatcher.BeginInvoke(() => NotifyCrashes(found));
        Changes = new ChangeWatcher(db);
        Crashes.Start();
        Changes.Start();
    }

    /// <summary>
    /// Tells about a blue screen, sudden shutdown or graphics driver reset of the last day (app crashes only show on the
    /// Crashes page: too many to notify about). At most two notifications at a time.
    /// </summary>
    private void NotifyCrashes(IReadOnlyList<CrashRecord> found)
    {
        long since = HistoryDb.Unix(DateTime.Now.AddDays(-1));
        foreach (var c in found.Where(c => c.Ts >= since && c.Kind is CrashKinds.BlueScreen or CrashKinds.Shutdown or CrashKinds.DriverReset)
                               .OrderByDescending(c => c.Ts).Take(2))
        {
            var ex = CrashExplainer.Explain(c);
            Tray?.Notify(ex.Title, $"{ex.Reason} Kelvra → Crashes has the details.");
        }
    }

    /// <summary>Applies Settings → History (recording on/off, the per-app network trace).</summary>
    public void ApplyHistorySettings() => Recorder?.Apps.SetNetwork(Settings.LongHistory && Settings.TrackAppNetwork);

    /// <summary>Once the sensor list exists: names overlay items and gives first-time users the "Essentials" overlay.</summary>
    private void OnFirstLoad()
    {
        Overlays.ResolveNames();
        if (!Settings.DefaultsApplied && Settings.Overlays.Count == 0)
        {
            var essentials = OverlayTemplates.Create("Essentials", Sensors.All, "Essentials");
            Settings.Overlays.Add(essentials);
        }
        Settings.DefaultsApplied = true;
        Overlays.RebuildAll();
        MarkDirty();
    }

    /// <summary>Schedules a settings save (picked up by the 1.5 s save timer).</summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>Saves now. A failure is retried on the next change and reported once per new reason (not on every retry).</summary>
    private void SaveSettings()
    {
        if (Settings.Save())
        {
            _reportedSaveError = null;
            return;
        }
        _dirty = true; // try again on the next tick
        if (Settings.LastSaveError == _reportedSaveError) return;
        _reportedSaveError = Settings.LastSaveError;
        Tray.Notify("Kelvra couldn't save your settings", $"{Settings.LastSaveError}\nKelvra keeps trying; your changes are kept until it works.");
    }

    private async Task RefreshAutostartAsync()
    {
        string error = "";
        bool ok = await Task.Run(() =>
        {
            try { return Autostart.Enable(out error); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                error = ex.Message;
                return false;
            }
        });
        if (ok) return;
        Log.Warn("Updating the Start with Windows task failed: " + error);
        Tray.Notify("Start with Windows needs attention", $"Kelvra couldn't update its sign-in task, so it may not start with Windows.\n{error}");
    }

    // ---------- game sessions and benchmarks ----------

    /// <summary>Hottest CPU and GPU temperatures while a game (and a benchmark) runs, for their summaries.</summary>
    private void TrackGameTemperatures()
    {
        if (Game.ActiveGame is not { } game) return;
        float? cpu = Sensors.All.Where(s => s.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu &&
                                            s.Type == LibreHardwareMonitor.Hardware.SensorType.Temperature &&
                                            (s.Name.Contains("Package") || s.Name.Contains("Tctl")))
                                .Select(s => s.Value).FirstOrDefault(v => v is not null);
        cpu ??= Sensors.All.Where(s => s.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu &&
                                       s.Type == LibreHardwareMonitor.Hardware.SensorType.Temperature).Max(s => s.Value);
        float? gpu = Sensors.All.Where(s => s.IsGpu && s.Type == LibreHardwareMonitor.Hardware.SensorType.Temperature && s.Name.Contains("Core"))
                                .Max(s => s.Value);

        static float? Hotter(float? a, float? b) => a is float x && b is float y ? Math.Max(x, y) : a ?? b;
        game.MaxCpuTemp = Hotter(game.MaxCpuTemp, cpu);
        game.MaxGpuTemp = Hotter(game.MaxGpuTemp, gpu);
        if (Game.Benchmark is { } run)
        {
            run.MaxCpuTemp = Hotter(run.MaxCpuTemp, cpu);
            run.MaxGpuTemp = Hotter(run.MaxGpuTemp, gpu);
        }
    }

    /// <summary>On exit or Windows sign-out/shutdown: keep the running benchmark and game session, and ones still being checked.</summary>
    private void SaveRunningGame()
    {
        if (Game.StopBenchmark() is { } run) SaveBenchmark(run);
        if (Game.ActiveGame is { } game) SaveGameSession(game);
        foreach (var (_, summary) in _endingGames) SaveSummary(summary);
        _endingGames.Clear();
    }

    private static GameSessionSummary? Summary(GameSession game)
    {
        var summary = GameSessionSummary.From(game.Name, game.Started, game.Stats, game.MaxCpuTemp, game.MaxGpuTemp, exe: game.Exe);
        return TimeSpan.FromSeconds(summary.Seconds) < GameSessionLog.MinLength ? null : summary;
    }

    private void SaveGameSession(GameSession game)
    {
        if (Summary(game) is { } summary) SaveSummary(summary);
    }

    /// <summary>Game sessions waiting for the crash check (saved as they are if Kelvra exits first).</summary>
    private readonly Dictionary<GameSession, GameSessionSummary> _endingGames = new();

    /// <summary>
    /// A game closed: Windows logs a crash a moment after the process ends, so wait a few seconds, read the event log, and
    /// save the session marked as crashed if the game's exe crashed or froze while it ran.
    /// </summary>
    private async Task SaveEndedGameAsync(GameSession game)
    {
        if (Summary(game) is not { } summary) return;
        if (Crashes == null || Recorder == null || game.Exe.Length == 0)
        {
            SaveSummary(summary);
            return;
        }
        _endingGames[game] = summary;
        await Task.Delay(TimeSpan.FromSeconds(8));
        await Crashes.ScanAsync();
        long from = HistoryDb.Unix(game.Started), to = HistoryDb.Unix(DateTime.Now) + 1;
        bool crashed = await Recorder.ReadAsync(db => db.Crashes(from, to).Any(c =>
            c.Kind is CrashKinds.AppCrash or CrashKinds.AppHang &&
            Path.GetFileNameWithoutExtension(c.App).Equals(game.Exe, StringComparison.OrdinalIgnoreCase)));
        if (!_endingGames.Remove(game)) return; // saved already, while quitting
        SaveSummary(summary with { Crashed = crashed });
        if (crashed) Tray.Notify($"{game.Name} crashed", "Kelvra → Crashes shows what Windows recorded about it.");
    }

    /// <summary>The benchmark shortcut: starts recording the running game, or stops and saves the run.</summary>
    public void ToggleBenchmark()
    {
        string key = Settings.HotkeyBenchmark.Length > 0 ? Settings.HotkeyBenchmark : "the benchmark shortcut";
        if (Game.StopBenchmark() is { } run)
        {
            SaveBenchmark(run);
            return;
        }
        if (!Settings.GameFpsEnabled)
        {
            OsdToast.Notify("Benchmark not started", "FPS measuring is off. Turn it on in Kelvra → Settings → Gaming.", OsdToast.Red, 5);
            return;
        }
        if (!Game.StartBenchmark())
        {
            OsdToast.Notify("Benchmark not started", $"Kelvra hasn't detected a game yet. Play for a few seconds, then press {key} again.", OsdToast.Red, 5);
            return;
        }
        OsdToast.Notify("● Benchmark recording", $"{Game.ActiveGame?.Name} · press {key} again to stop", OsdToast.Red);
        Tray.Notify("Benchmark started", $"Recording {Game.ActiveGame?.Name}. Press {key} again to stop.");
    }

    /// <summary>
    /// Writes the run's frames to a CSV on a worker thread (millions of lines mustn't freeze the window), then logs its
    /// summary. While quitting it waits for the file instead, so nothing is lost.
    /// </summary>
    private void SaveBenchmark(BenchmarkRun run)
    {
        string safe = string.Concat(run.Game.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string csv = Path.Combine(CsvLogger.DefaultFolder, $"Benchmark {safe} {run.Started:yyyy-MM-dd HH-mm-ss}.csv");
        var summary = GameSessionSummary.From(run.Game.Name, run.Started, run.Stats, run.MaxCpuTemp, run.MaxGpuTemp, benchmark: true, csvFile: csv);
        string length = summary.When.Split(" · ").Last();
        if (!IsQuitting) OsdToast.Notify("■ Benchmark stopped and saved", $"{summary.FpsLine} · {length}", OsdToast.Green, 6);

        // The summary first (the Games page hides the CSV button until the file exists), then the frames in the background.
        // Complete: no frames are added once the run is stopped.
        SaveSummary(summary);
        Tray.Notify("Benchmark saved", $"{summary.FpsLine} · {length}\nDetails: Kelvra → Games.");
        var write = Task.Run(() =>
        {
            try
            {
                GameSessionLog.WriteFrames(csv, run.Frames);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Saving benchmark frames", ex);
                try { File.Delete(csv); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
        });
        _benchmarkWrites.Add(write);
        _benchmarkWrites.RemoveAll(t => t.IsCompleted);
        if (IsQuitting) write.Wait();
    }

    /// <summary>Benchmark CSVs still being written; Quit waits for them so no file is cut off.</summary>
    private readonly List<Task> _benchmarkWrites = new();

    private void SaveSummary(GameSessionSummary summary)
    {
        try
        {
            GameSessionLog.Append(summary, GameSessionLog.DefaultFile);
            GameSessionsChanged?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving the game session", ex);
        }
    }

    /// <summary>A session or benchmark was added to the Games page.</summary>
    public event Action? GameSessionsChanged;

    /// <summary>After an update the old Kelvra is still closing: wait for it to let go of the single-instance lock.</summary>
    private static bool WaitForPreviousInstance(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(15)); }
        catch (AbandonedMutexException) { return true; } // it exited without releasing the lock: it's ours now
    }

    // ---------- updates ----------

    private DispatcherTimer? _updateTimer;
    private UpdateInfo? _latestUpdate; // what the last successful check found (null = up to date)
    private DateTime _lastUpdateCheckUtc = DateTime.MinValue;
    private UpdateInfo? _pendingUpdate; // found while the window was hidden: offered when it opens
    private bool _updatePromptOpen;

    /// <summary>
    /// First check 15 s after startup, then an hourly tick: GitHub is asked once a day, and an update the user
    /// declined is offered again as soon as its 24 h pause is over.
    /// </summary>
    private void StartUpdateChecks()
    {
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(1);
            Updater.CleanupOldVersion(); // the previous exe, once the update's restart is done with it
            if (!Settings.CheckForUpdates) return;
            if (DateTime.UtcNow - _lastUpdateCheckUtc >= TimeSpan.FromHours(24))
                await CheckForUpdatesAsync(manual: false);
            else if (_latestUpdate != null && Updater.ShouldPrompt(_latestUpdate, Settings, DateTime.UtcNow))
                OfferUpdate(_latestUpdate);
        };
        _updateTimer.Start();
        MainView.IsVisibleChanged += (_, _) => OfferPendingUpdate();
        MainView.StateChanged += (_, _) => OfferPendingUpdate();
        Game.Changed += OfferPendingUpdate; // the game was closed or left
    }

    /// <summary>
    /// Asks GitHub for a newer release and offers it. Returns it, or null when Kelvra is up to date.
    /// A manual check (Settings → About) ignores the "Not now" pause and throws network errors; an automatic one only logs them.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync(bool manual)
    {
        UpdateInfo? update;
        try
        {
            update = await Updater.CheckAsync();
        }
        catch (Exception ex) when (!manual && Updater.IsCheckError(ex))
        {
            Log.Warn("Update check failed: " + ex.Message);
            return null;
        }
        _lastUpdateCheckUtc = DateTime.UtcNow;
        _latestUpdate = update;
        if (update != null && (manual || Updater.ShouldPrompt(update, Settings, DateTime.UtcNow))) OfferUpdate(update, manual);
        return update;
    }

    private bool MainWindowShown => MainView.IsVisible && MainView.WindowState != WindowState.Minimized;

    /// <summary>
    /// Shows the update prompt, or a notification and the prompt later: once the window opens (Kelvra in the tray) or
    /// once you stop playing – a dialog popping up in the middle of a game would be unwelcome. A manual check asks at once.
    /// </summary>
    private void OfferUpdate(UpdateInfo update, bool manual = false)
    {
        if (_updatePromptOpen || IsQuitting) return;
        if (!MainWindowShown || (!manual && Game.GameVisible))
        {
            if (_pendingUpdate?.Version != update.Version)
                Tray.Notify($"Kelvra {update.Version.ToString(3)} is available",
                            Game.GameVisible ? "Kelvra will ask after your game." : "Open Kelvra to install it.");
            _pendingUpdate = update;
            return;
        }

        _pendingUpdate = null;
        _updatePromptOpen = true;
        var choice = UpdatePromptWindow.Ask(update, MainView);
        _updatePromptOpen = false;
        if (choice == UpdateChoice.Installed)
        {
            RestartAfterUpdate();
            return;
        }
        Settings.UpdateDeclinedVersion = update.Version.ToString(3);
        Settings.UpdateDeclinedAtUtc = DateTime.UtcNow;
        MarkDirty();
    }

    private void OfferPendingUpdate()
    {
        if (_pendingUpdate is not UpdateInfo update || !MainWindowShown || Game.GameVisible) return;
        // Once the window has finished appearing, not in the middle of showing it
        Dispatcher.InvokeAsync(() =>
        {
            if (_pendingUpdate == update) OfferUpdate(update);
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Starts the newly installed exe (it waits for this one to close) and exits.</summary>
    private void RestartAfterUpdate()
    {
        string exe = Environment.ProcessPath!;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, Updater.AfterUpdateArg)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            });
        }
        catch (Win32Exception ex)
        {
            Log.Error("Restarting after the update", ex);
            MessageBox.Show(MainView, $"Kelvra was updated but couldn't restart itself:\n{ex.Message}\n\nThe new version starts the next time you open Kelvra.",
                "Kelvra", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Quit();
    }

    /// <summary>The mini window, while it's open.</summary>
    public MiniWindow? Mini { get; private set; }

    /// <summary>Opens mini mode, or brings it to the front if it's already open.</summary>
    public void ShowMini()
    {
        if (Mini == null)
        {
            Mini = new MiniWindow();
            Mini.Closed += (_, _) => Mini = null;
        }
        Settings.MiniOpen = true;
        MarkDirty();
        Mini.Show();
        Mini.Activate();
    }

    /// <summary>Called when Settings → Keep history is switched: saves the history now, or deletes the saved copy.</summary>
    public void ApplyKeepHistory()
    {
        if (Settings.KeepHistory) History.Save(HistoryService.DefaultFile);
        else
        {
            try { File.Delete(HistoryService.DefaultFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Deleting saved history", ex); }
        }
    }

    /// <summary>Saves settings when an alert rule is added, removed or edited (not on live status updates).</summary>
    private void WatchAlertRules()
    {
        string[] persisted =
        {
            nameof(AlertRule.SensorId), nameof(AlertRule.Condition), nameof(AlertRule.Threshold),
            nameof(AlertRule.DurationSeconds), nameof(AlertRule.CooldownMinutes), nameof(AlertRule.Enabled),
        };
        void OnRuleChanged(object? s, PropertyChangedEventArgs e)
        {
            if (persisted.Contains(e.PropertyName)) MarkDirty();
        }

        foreach (var r in Settings.Alerts) r.PropertyChanged += OnRuleChanged;
        Settings.Alerts.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (AlertRule r in e.NewItems) r.PropertyChanged += OnRuleChanged;
            if (e.OldItems != null) foreach (AlertRule r in e.OldItems) r.PropertyChanged -= OnRuleChanged;
            MarkDirty();
        };
    }

    public bool IsQuitting { get; private set; }

    /// <summary>The only real exit (ShutdownMode is explicit): stops polling, saves, and removes overlays and the tray icon.</summary>
    public void Quit()
    {
        if (IsQuitting) return;
        IsQuitting = true;
        _cts.Cancel();
        ReleaseFansQuietly();
        Logger.Stop();
        Crashes?.Dispose();
        Changes?.Dispose();
        Recorder?.Stop();
        Recorder?.Dispose();
        bool miniOpen = Mini != null;
        Mini?.Close();
        Settings.MiniOpen = miniOpen; // reopen it next time
        Settings.Save();
        if (Settings.KeepHistory) History.Save(HistoryService.DefaultFile);
        Log.Info("Kelvra exiting");
        Overlays.CloseAll();
        SaveRunningGame();
        Task.WaitAll(_benchmarkWrites.ToArray(), TimeSpan.FromSeconds(30));
        Game.Dispose(); // stops PresentMon and its trace session
        Tray.Dispose();
        Sensors.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
