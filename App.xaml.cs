using System.Collections.Specialized;
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
    private bool _dirty;

    public static new App Current => (App)Application.Current;

    public AppSettings Settings { get; private set; } = null!;
    public SensorStore Sensors { get; private set; } = null!;
    public OverlayManager Overlays { get; private set; } = null!;
    public TrayIcon Tray { get; private set; } = null!;
    public MainWindow MainView { get; private set; } = null!;
    public HistoryService History { get; } = new();
    public AlertService Alerts { get; } = new();
    public CsvLogger Logger { get; } = new();

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

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Kelvra error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // --- Settings and services (order matters: the windows below read these in their constructors) ---
        Settings = AppSettings.Load();
        SensorFormat.UseFahrenheit = Settings.Fahrenheit;
        ThemeManager.Initialize(Settings.Theme);
        WatchAlertRules();

        Sensors = new SensorStore();
        Overlays = new OverlayManager(Settings, Sensors);
        Overlays.Dirty += MarkDirty;

        MainView = new MainWindow();
        new WindowInteropHelper(MainView).EnsureHandle(); // hotkeys need a handle even when starting in the tray
        Tray = new TrayIcon();

        bool startHidden = Settings.StartMinimized || e.Args.Contains(Autostart.MinimizedArg);
        if (!startHidden) MainView.Show();

        // Keep the logon task pointing at this exe, in case it was moved. From a folder other programs can change
        // the task is left alone (never re-pointed there) and the user is told to move Kelvra.exe.
        if (Settings.StartWithWindows)
        {
            if (Autostart.IsExeLocationSafe()) _ = Task.Run(() => Autostart.Enable(out _));
            else Tray.Notify("Start with Windows needs attention",
                "Kelvra.exe is in a folder other programs can change. Move it to C:\\Program Files\\Kelvra (see Settings).");
        }

        // Debounced save: changes only set a flag, so dragging a slider or overlay doesn't rewrite the file each frame
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _saveTimer.Tick += (_, _) =>
        {
            if (!_dirty) return;
            _dirty = false;
            Settings.Save();
        };
        _saveTimer.Start();

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
        };
        History.Sampled += Logger.OnSample;
        Alerts.Fired += e =>
        {
            Tray.Notify(e.Title, e.Message);
            if (Settings.AlertSound) SystemSounds.Exclamation.Play();
        };
        Overlays.Sync();
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
        Logger.Stop();
        Settings.Save();
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
