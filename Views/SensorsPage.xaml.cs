using System.ComponentModel;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>Live sensor table: search, category filter chips, foldable device groups and sortable columns.
/// Reordering of device groups lives in SensorsPage.Reorder.cs.</summary>
public partial class SensorsPage : UserControl
{
    private static readonly bool IsAdmin =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static readonly string[] NumericKeys = { nameof(SensorVm.Value), nameof(SensorVm.Min), nameof(SensorVm.Max) };

    private readonly App _app = App.Current;
    private readonly ListCollectionView _view;
    private readonly HashSet<string> _collapsed;
    private string _chipSignature = "";
    private string? _hardwareFilter;
    private string? _typeFilter;

    public event Action<SensorVm>? AddToOverlayRequested;
    public event Action? InstallPawnIoRequested;

    public SensorsPage()
    {
        InitializeComponent();

        _collapsed = new HashSet<string>(_app.Settings.CollapsedGroups);
        _view = new ListCollectionView(_app.Sensors.All);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorVm.GroupName)));
        _view.Filter = Matches;
        SensorList.ItemsSource = _view;

        ApplyGroupOrder();
        ApplySort();
        BuildChips();
        _app.Sensors.Updated += OnSensorsUpdated;
    }

    /// <summary>Set by the main window; shows the "Install PawnIO" banner when true.</summary>
    public bool PawnIoMissing { get; set; }

    // ---------- filtering ----------

    /// <summary>Show only one hardware category and/or one sensor-type category (null = all).</summary>
    public void SetFilter(string? hardware, string? type)
    {
        _hardwareFilter = hardware;
        _typeFilter = type;
        SearchBox.Text = "";
        BuildChips();
        Refresh();
    }

    private bool Matches(object o)
    {
        var s = (SensorVm)o;
        if (_hardwareFilter != null && s.Category != _hardwareFilter) return false;
        if (_typeFilter != null && s.TypeCategory != _typeFilter) return false;

        string q = SearchBox?.Text.Trim() ?? "";
        return q.Length == 0
               || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || s.GroupName.Contains(q, StringComparison.OrdinalIgnoreCase)
               || s.TypeLabel.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Refresh()
    {
        _view.Refresh();
        EmptyText.Visibility = _app.Sensors.Loaded && _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_view != null) Refresh();
    }

    private void BuildChips()
    {
        ChipPanel.Children.Clear();

        if (_typeFilter != null)
        {
            var active = new Button { Style = (Style)FindResource("ActiveChip"), Content = _typeFilter, ToolTip = "Remove this filter" };
            active.Click += (_, _) => SetFilter(_hardwareFilter, null);
            ChipPanel.Children.Add(active);
        }

        var counts = _app.Sensors.All.GroupBy(s => s.Category).ToDictionary(g => g.Key, g => g.Count());
        AddChip("All", null, _app.Sensors.All.Count);
        foreach (var info in SensorCategories.Hardware)
        {
            if (counts.TryGetValue(info.Name, out int n)) AddChip(info.Name, info.Name, n);
        }
        _chipSignature = string.Join("|", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"));
    }

    private void AddChip(string label, string? category, int count)
    {
        var chip = new RadioButton
        {
            Style = (Style)FindResource("Chip"),
            GroupName = "HardwareChips",
            IsChecked = _hardwareFilter == category,
        };
        System.Windows.Automation.AutomationProperties.SetName(chip, label);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = label });
        content.Children.Add(new TextBlock { Text = count.ToString(), Margin = new Thickness(6, 0, 0, 0), Opacity = 0.6 });
        chip.Content = content;
        chip.Checked += (_, _) =>
        {
            if (_hardwareFilter == category) return;
            _hardwareFilter = category;
            Refresh();
        };
        ChipPanel.Children.Add(chip);
    }

    // ---------- live updates ----------

    /// <summary>After every poll: subtitle, chip counts (only rebuilt when they changed) and the warning banner.</summary>
    private void OnSensorsUpdated()
    {
        var sensors = _app.Sensors;
        int devices = sensors.All.Select(s => s.HardwareId).Distinct().Count();
        Subtitle.Text = $"{sensors.All.Count} sensors on {devices} devices · refreshing every {_app.Settings.RefreshMs / 1000.0:0.##} s";

        // New hardware (or first load) → refresh chip counts
        string signature = string.Join("|", sensors.All.GroupBy(s => s.Category).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"));
        if (signature != _chipSignature) BuildChips();
        if (sensors.All.Count != _rankedCount) ApplyGroupOrder(); // place newly detected devices
        EmptyText.Visibility = sensors.Loaded && _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

        string? warning = null;
        bool offerInstall = false;
        if (sensors.Error != null)
            warning = "Sensor error: " + sensors.Error;
        else if (!IsAdmin)
            warning = "Not running as administrator. CPU temperatures, voltages and fan speeds need admin rights.";
        else if (PawnIoMissing)
        {
            warning = "The PawnIO driver isn't installed, so CPU temperatures, voltages and fan speeds are unavailable.";
            offerInstall = true;
        }
        else if (!sensors.All.Any(s => s.HardwareType == HardwareType.Cpu && s.Type == SensorType.Temperature && s.Value > 0))
            warning = "No CPU temperature detected. Your CPU or motherboard may not expose one.";

        WarningBanner.Visibility = warning == null ? Visibility.Collapsed : Visibility.Visible;
        WarningText.Text = warning ?? "";
        BannerInstallButton.Visibility = offerInstall ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- sorting ----------

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        string key = (string)((Button)sender).Tag;
        bool numeric = NumericKeys.Contains(key);
        var s = _app.Settings;

        if (s.SensorSortKey != key)
        {
            // Numbers start highest-first (like Task Manager), text starts A→Z
            s.SensorSortKey = key;
            s.SensorSortDescending = numeric;
        }
        else if (s.SensorSortDescending == numeric)
        {
            s.SensorSortDescending = !numeric;
        }
        else
        {
            s.SensorSortKey = null; // third click: back to the default order
            s.SensorSortDescending = false;
        }

        ApplySort();
        _app.MarkDirty();
    }

    private void ApplySort()
    {
        string? key = _app.Settings.SensorSortKey;
        var direction = _app.Settings.SensorSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;

        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            // Devices stay in the user's order; sorting happens inside each group.
            _view.SortDescriptions.Add(new SortDescription(nameof(SensorVm.GroupRank), ListSortDirection.Ascending));
            if (key != null) _view.SortDescriptions.Add(new SortDescription(key, direction));
            _view.SortDescriptions.Add(new SortDescription(nameof(SensorVm.Order), ListSortDirection.Ascending));

            // Values change every refresh, so keep re-sorting live when sorting by a number
            _view.LiveSortingProperties.Clear();
            bool live = key != null && NumericKeys.Contains(key);
            if (live) _view.LiveSortingProperties.Add(key!);
            _view.IsLiveSorting = live;
        }
        UpdateSortHeaders();
    }

    private void UpdateSortHeaders()
    {
        string? key = _app.Settings.SensorSortKey;
        string arrow = _app.Settings.SensorSortDescending ? "  ▼" : "  ▲";
        SortName.Content = "SENSOR" + (key == nameof(SensorVm.Name) ? arrow : "");
        SortType.Content = "TYPE" + (key == nameof(SensorVm.TypeLabel) ? arrow : "");
        SortValue.Content = "CURRENT" + (key == nameof(SensorVm.Value) ? arrow : "");
        SortMin.Content = "MIN" + (key == nameof(SensorVm.Min) ? arrow : "");
        SortMax.Content = "MAX" + (key == nameof(SensorVm.Max) ? arrow : "");
    }

    // ---------- folding ----------

    private void GroupExpander_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander { DataContext: CollectionViewGroup g } exp)
            exp.IsExpanded = !_collapsed.Contains(g.Name?.ToString() ?? "");
    }

    private void GroupExpander_Toggled(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        if (sender is not Expander { DataContext: CollectionViewGroup g } exp) return;

        string name = g.Name?.ToString() ?? "";
        bool changed = exp.IsExpanded ? _collapsed.Remove(name) : _collapsed.Add(name);
        if (changed) SaveCollapsed();
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        _collapsed.Clear();
        SetAllExpanders(true);
        SaveCollapsed();
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var name in _app.Sensors.All.Select(s => s.GroupName).Distinct()) _collapsed.Add(name);
        SetAllExpanders(false);
        SaveCollapsed();
    }

    private void SetAllExpanders(bool expanded)
    {
        foreach (var exp in FindChildren<Expander>(SensorList)) exp.IsExpanded = expanded;
    }

    private void SaveCollapsed()
    {
        _app.Settings.CollapsedGroups = _collapsed.OrderBy(n => n).ToList();
        _app.MarkDirty();
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in FindChildren<T>(child)) yield return nested;
        }
    }

    // ---------- actions ----------

    private void AddSensor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SensorVm sensor) AddToOverlayRequested?.Invoke(sensor);
    }

    private void BannerInstall_Click(object sender, RoutedEventArgs e) => InstallPawnIoRequested?.Invoke();

    private async void ResetMinMax_Click(object sender, RoutedEventArgs e)
    {
        ResetMinMaxButton.IsEnabled = false;
        try
        {
            await _app.Sensors.ResetMinMaxAsync(); // the new min/max show with the next poll
        }
        finally
        {
            ResetMinMaxButton.IsEnabled = true;
        }
    }
}
