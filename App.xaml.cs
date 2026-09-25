using System.Collections.Specialized;
using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Kelvra;

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

        // Keep the logon task pointing at this exe, in case it was moved
        if (Settings.StartWithWindows) _ = Task.Run(() => Autostart.Enable(out _));

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
        await Sensors.RunAsync(() => Settings.RefreshMs, _cts.Token);
    }

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
