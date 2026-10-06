using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace Kelvra;

/// <summary>
/// The home page ("instrument panel"): a panel per CPU and GPU with temperature, load, clock and power plus a graph,
/// the sensors pinned on the History page with a trend line each, and memory / system drive / network at a glance.
/// </summary>
public partial class OverviewPage : UserControl
{
    public static readonly DependencyProperty DeviceColumnsProperty =
        DependencyProperty.Register(nameof(DeviceColumns), typeof(int), typeof(OverviewPage), new PropertyMetadata(2));

    public static readonly DependencyProperty RangeTextProperty =
        DependencyProperty.Register(nameof(RangeText), typeof(string), typeof(OverviewPage), new PropertyMetadata(""));

    private readonly App _app = App.Current;
    private readonly ObservableCollection<DevicePanel> _panels = new();
    private readonly ObservableCollection<SensorVm> _pinned = new();
    private readonly SideStat _memory = new("MEMORY");
    private readonly SideStat _drive;
    private readonly SideStat _network = new("NETWORK");
    private readonly string _systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
    private float _networkPeak;
    private int _builtForCount = -1;
    private long _lastDriveCheck;
    private bool _loading = true;

    public OverviewPage()
    {
        InitializeComponent();
        _drive = new SideStat($"DRIVE {_systemDrive.TrimEnd('\\')}");
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
        };
        SizeChanged += (_, _) => UpdateColumns();
    }

    /// <summary>Two device panels side by side when there's room.</summary>
    public int DeviceColumns { get => (int)GetValue(DeviceColumnsProperty); set => SetValue(DeviceColumnsProperty, value); }

    /// <summary>"−5:00 → now" under each graph.</summary>
    public string RangeText { get => (string)GetValue(RangeTextProperty); set => SetValue(RangeTextProperty, value); }

    private void OnUpdated()
    {
        if (!IsVisible) return;
        var sensors = _app.Sensors;
        LiveText.Text = sensors.Error != null ? "SENSOR ERROR" : $"LIVE · {_app.Settings.RefreshMs / 1000.0:0.0#} s";
        LiveDot.Fill = (System.Windows.Media.Brush)FindResource(sensors.Error != null ? "HotBrush" : "GoodBrush");
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
        foreach (var p in _panels) p.Update(sensors.All);

        Overview.UpdateMemory(_memory, sensors.All);
        Overview.UpdateNetwork(_network, sensors.All, ref _networkPeak);
        if (Environment.TickCount64 - _lastDriveCheck > 10_000)
        {
            _lastDriveCheck = Environment.TickCount64;
            Overview.UpdateDrive(_drive, _systemDrive);
        }
    }

    private void UpdateColumns() => DeviceColumns = _panels.Count >= 2 && ActualWidth > 760 ? 2 : 1;

    private void RebuildPinned()
    {
        _pinned.Clear();
        foreach (var id in _app.Settings.HistorySelected)
            if (_app.Sensors.ById.TryGetValue(id, out var s)) _pinned.Add(s);
        PinnedEmpty.Visibility = _pinned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- range ----------

    private void SyncRange()
    {
        _loading = true;
        foreach (RadioButton r in RangeButtons.Children)
            r.IsChecked = int.Parse((string)r.Tag) == _app.Settings.HistoryMinutes;
        _loading = false;
        int minutes = _app.Settings.HistoryMinutes;
        RangeText = minutes >= 60 ? $"−{minutes / 60} h → now" : $"−{minutes}:00 → now";
        TrendHeader.Text = minutes >= 60 ? $"{minutes / 60} H TREND" : $"{minutes} MIN TREND";
    }

    private void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton rb) return;
        _app.Settings.HistoryMinutes = int.Parse((string)rb.Tag);
        _app.MarkDirty();
        SyncRange();
        foreach (var chart in FindCharts(this)) chart.InvalidateVisual();
    }

    private static IEnumerable<TrendChart> FindCharts(DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TrendChart chart) yield return chart;
            foreach (var nested in FindCharts(child)) yield return nested;
        }
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

        // Keep the existing order, append new ones (same list as History → Selected)
        var chosen = new HashSet<string>(picker.SelectedIds);
        var list = _app.Settings.HistorySelected.Where(chosen.Contains).ToList();
        list.AddRange(picker.SelectedIds.Where(id => !list.Contains(id)));
        _app.Settings.HistorySelected = list;
        _app.MarkDirty();
        RebuildPinned();
    }
}
