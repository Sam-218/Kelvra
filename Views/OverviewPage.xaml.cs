using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kelvra;

/// <summary>
/// The home page. It answers "is my PC OK?" first, in a sentence ("All quiet."), then shows each CPU and GPU with its
/// temperature in the colour of its heat, the headroom to its limit and the shown range as a chart; under that this
/// week's "worth knowing", the pinned sensors, and memory / system drive / network.
/// </summary>
public partial class OverviewPage : UserControl
{
    public static readonly DependencyProperty DeviceColumnsProperty =
        DependencyProperty.Register(nameof(DeviceColumns), typeof(int), typeof(OverviewPage), new PropertyMetadata(2));

    public static readonly DependencyProperty RangeAgoProperty =
        DependencyProperty.Register(nameof(RangeAgo), typeof(string), typeof(OverviewPage), new PropertyMetadata(""));

    private readonly App _app = App.Current;
    private readonly ObservableCollection<DevicePanel> _panels = new();
    private readonly ObservableCollection<SensorVm> _pinned = new();
    private readonly SideStat _memory = new("Memory", "\uE964");
    private readonly SideStat _drive;
    private readonly SideStat _network = new("Network", "\uE8AB");
    private readonly string _systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
    private float _networkPeak;
    private int _builtForCount = -1;
    private long _lastDriveCheck, _lastInsights;
    private bool _loading = true;

    public OverviewPage()
    {
        InitializeComponent();
        _drive = new SideStat($"Drive {_systemDrive.TrimEnd('\\')}", "\uEDA2");
        DevicePanels.ItemsSource = _panels;
        PinnedList.ItemsSource = _pinned;
        SideList.ItemsSource = new[] { _memory, _drive, _network };

        SyncRange();
        _loading = false;

        _app.Sensors.Updated += OnUpdated;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            SyncRange();
            RebuildPinned();
            OnUpdated();
            _ = LoadInsightsAsync();
        };
        SizeChanged += (_, _) => UpdateColumns();
    }

    /// <summary>Two device panels side by side when there's room.</summary>
    public int DeviceColumns { get => (int)GetValue(DeviceColumnsProperty); set => SetValue(DeviceColumnsProperty, value); }

    /// <summary>"60 min ago" under each chart.</summary>
    public string RangeAgo { get => (string)GetValue(RangeAgoProperty); set => SetValue(RangeAgoProperty, value); }

    private TimeSpan Range => TimeSpan.FromMinutes(_app.Settings.HistoryMinutes);

    private void OnUpdated()
    {
        if (!IsVisible) return;
        var sensors = _app.Sensors;
        LiveText.Text = sensors.Error != null ? "SENSOR ERROR" : $"LIVE · {_app.Settings.RefreshMs / 1000.0:0.#} s";
        LiveDot.Fill = (Brush)FindResource(sensors.Error != null ? "DangerBrush" : "GoodBrush");
        if (!sensors.Loaded) return;
        LoadingText.Visibility = Visibility.Collapsed;

        if (sensors.All.Count != _builtForCount)
        {
            _builtForCount = sensors.All.Count;
            _panels.Clear();
            foreach (var p in Overview.Panels(sensors.All)) _panels.Add(p);
            UpdateColumns();
            RebuildPinned();
        }
        foreach (var p in _panels)
        {
            p.Update(sensors.All);
            p.UpdateHeat(sensors.ById, _app.History, Range, _app.Settings.Alerts);
        }
        ShowHeadline();

        Overview.UpdateMemory(_memory, sensors.All);
        Overview.UpdateNetwork(_network, sensors.All, ref _networkPeak);
        if (Environment.TickCount64 - _lastDriveCheck > 10_000)
        {
            _lastDriveCheck = Environment.TickCount64;
            Overview.UpdateDrive(_drive, _systemDrive);
        }
        if (Environment.TickCount64 - _lastInsights > 10 * 60_000) _ = LoadInsightsAsync();
    }

    /// <summary>"All quiet." – or which chip is warm, hot or at its limit, and when the last alert was.</summary>
    private void ShowHeadline()
    {
        var (headline, subline) = Overview.Headline(_panels, _app.Sensors.Error != null);
        Headline.Text = headline;
        if (_app.Alerts.Recent.FirstOrDefault() is { } last)
            subline += $" Last alert {Ago(last.Time)}.";
        Subline.Text = subline;
    }

    private static string Ago(DateTime t)
    {
        var span = DateTime.Now - t;
        return span.TotalMinutes < 2 ? "just now" : span.TotalHours < 1 ? $"{span.TotalMinutes:0} min ago"
             : t.Date == DateTime.Today ? $"at {t.ToString("t", CultureInfo.CurrentCulture)}" : $"on {t.ToString("ddd d MMM", CultureInfo.CurrentCulture)}";
    }

    private void UpdateColumns() => DeviceColumns = _panels.Count >= 2 && ActualWidth > 900 ? 2 : 1;

    private void RebuildPinned()
    {
        _pinned.Clear();
        foreach (var id in _app.Settings.HistorySelected)
            if (_app.Sensors.ById.TryGetValue(id, out var s)) _pinned.Add(s);
        PinnedEmpty.Visibility = _pinned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- worth knowing ----------

    /// <summary>The top lines of this week's report (see <see cref="Insights"/>), read on a worker thread.</summary>
    private async Task LoadInsightsAsync()
    {
        _lastInsights = Environment.TickCount64;
        if (_app.Recorder is not { } recorder || !_app.Settings.LongHistory)
        {
            InsightList.ItemsSource = null;
            InsightEmpty.Visibility = Visibility.Visible;
            return;
        }
        var alerts = _app.Settings.Alerts.ToList();
        List<Insight> insights;
        try
        {
            insights = await recorder.ReadAsync(db =>
            {
                List<GameSessionSummary> sessions;
                try { sessions = GameSessionLog.Load(GameSessionLog.DefaultFile); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { sessions = new(); }
                return ReportBuilder.Build(db, ReportPeriod.For(ReportRange.Week, DateTime.Today), sessions, alerts).Insights;
            });
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            Log.Error("Reading this week's insights", ex);
            return;
        }
        var rows = insights.Take(3).Select(i => new InsightRow(i.Icon, i.Text, (Brush)FindResource(i.Tone switch
        {
            InsightTone.Good => "GoodBrush",
            InsightTone.Warn => "WarnBrush",
            InsightTone.Hot => "HotBrush",
            _ => "MutedBrush",
        }))).ToList();
        InsightList.ItemsSource = rows;
        InsightEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- range ----------

    private void SyncRange()
    {
        _loading = true;
        int minutes = _app.Settings.HistoryMinutes;
        // The Overview offers 5 min, 1 h and 24 h; other ranges (from Graphs) show as the nearest one here
        int shown = minutes <= 15 ? 5 : minutes <= 360 ? 60 : 1440;
        foreach (RadioButton r in RangeButtons.Children)
            r.IsChecked = int.Parse((string)r.Tag) == shown;
        _loading = false;
        RangeAgo = minutes >= 60 ? $"{minutes / 60} h ago" : $"{minutes} min ago";
        if (minutes == 60) RangeAgo = "60 min ago";
    }

    private void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton rb) return;
        _app.Settings.HistoryMinutes = int.Parse((string)rb.Tag);
        _app.MarkDirty();
        SyncRange();
        OnUpdated();
        foreach (var chart in FindVisuals<FrameworkElement>(this).Where(v => v is HeatChart or HeatBar)) chart.InvalidateVisual();
    }

    private static IEnumerable<T> FindVisuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisuals<T>(child)) yield return nested;
        }
    }

    private void Mini_Click(object sender, RoutedEventArgs e) => _app.ShowMini();

    // ---------- a device's ⋯ menu ----------

    private void DeviceMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DevicePanel panel } button || panel.TempId is not { } id ||
            !_app.Sensors.ById.TryGetValue(id, out var temp)) return;
        var window = (MainWindow)Window.GetWindow(this);
        var menu = new ContextMenu { PlacementTarget = button };
        menu.Items.Add(new MenuItem { Header = $"Graph the {panel.Kind} temperature", Tag = "\uE9D2", Command = new Relay(() => window.GraphSensor(temp)) });
        menu.Items.Add(new MenuItem { Header = $"Pin the {panel.Kind} temperature", Tag = "\uE718", Command = new Relay(() => window.PinSensor(temp)) });
        menu.Items.Add(new MenuItem { Header = $"Alert when the {panel.Kind} is too hot…", Tag = "\uEA8F", Command = new Relay(() => window.AlertOn(temp)) });
        menu.IsOpen = true;
    }

    /// <summary>A menu item's action.</summary>
    private sealed class Relay(Action run) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }

    // ---------- pinned ----------

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var selected = new HashSet<string>(_app.Settings.HistorySelected);
        var picker = new SensorPickerWindow(_app.Sensors.All, "Choose sensors to pin", selected.Contains, editable: true)
        {
            Owner = Window.GetWindow(this),
        };
        if (picker.ShowDialog() != true) return;

        // Keep the existing order, append new ones (same list as Graphs → Selected)
        var chosen = new HashSet<string>(picker.SelectedIds);
        var list = _app.Settings.HistorySelected.Where(chosen.Contains).ToList();
        list.AddRange(picker.SelectedIds.Where(id => !list.Contains(id)));
        _app.Settings.HistorySelected = list;
        _app.MarkDirty();
        RebuildPinned();
    }
}
