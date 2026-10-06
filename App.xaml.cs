using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Kelvra;

/// <summary>
/// Entry point and service hub: loads settings, creates the shared services (sensors, overlays, history,
/// alerts, CSV logger, tray icon) and runs the sensor polling loop. Pages reach them via <see cref="Current"/>.
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
    /// <summary>Fan curves (Fans page). Runs after every poll.</summary>
    public FanController Fans { get; } = new();
    private readonly System.Diagnostics.Stopwatch _fanClock = System.Diagnostics.Stopwatch.StartNew();

    private void ReleaseFansQuietly()
    {
        try { Sensors?.ReleaseFans(); }
        catch (Exception ex) { Log.Error("Handing fans back to automatic", ex); }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --- Single instance ---
        _mutex = new Mutex(true, "Kelvra.SingleInstance", out bool isFirst);
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
        SessionEnding += (_, _) => ReleaseFansQuietly(); // sign-out / shutdown
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
        Overlays = new OverlayManager(Settings, Sensors);
        Overlays.Dirty += MarkDirty;

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
        };
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
        bool miniOpen = Mini != null;
        Mini?.Close();
        Settings.MiniOpen = miniOpen; // reopen it next time
        Settings.Save();
        if (Settings.KeepHistory) History.Save(HistoryService.DefaultFile);
        Log.Info("Kelvra exiting");
        Overlays.CloseAll();
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
