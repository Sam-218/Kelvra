using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>A row of the Ctrl K palette: a group heading, or a page, sensor or action that runs when chosen.</summary>
public sealed class PaletteItem : ObservableObject
{
    private bool _isSelected;

    public PaletteItem(string title, string icon = "", string detail = "", Brush? iconBrush = null, Action? run = null, bool header = false)
    {
        Title = title;
        Icon = icon;
        Detail = detail;
        IconBrush = iconBrush;
        Run = run;
        IsHeader = header;
    }

    public string Title { get; }
    public string Icon { get; }
    public string Detail { get; }
    public Brush? IconBrush { get; }
    public Action? Run { get; }
    public bool IsHeader { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>
/// Ctrl K: one search box for everything. Kelvra has over 100 sensors and 14 pages, so typing beats hunting through
/// menus. A sensor can be graphed, pinned to Overview, alerted on or used for a fan curve straight from the list.
/// </summary>
public partial class MainWindow
{
    private List<PaletteItem> _paletteItems = new();
    private int _paletteIndex = -1;

    private void SetupPalette()
    {
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
            {
                OpenPalette();
                e.Handled = true;
            }
        };
    }

    private void Search_Click(object sender, RoutedEventArgs e) => OpenPalette();

    public void OpenPalette()
    {
        Palette.Visibility = Visibility.Visible;
        PaletteBox.Text = "";
        BuildPalette("");
        Dispatcher.BeginInvoke(() => PaletteBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ClosePalette() => Palette.Visibility = Visibility.Collapsed;

    private void PaletteBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => ClosePalette();

    private void PaletteCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true; // clicks inside don't close it

    private void PaletteBox_TextChanged(object sender, TextChangedEventArgs e) => BuildPalette(PaletteBox.Text.Trim());

    private void PaletteBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                ClosePalette();
                break;
            case Key.Down:
                MoveSelection(+1);
                break;
            case Key.Up:
                MoveSelection(-1);
                break;
            case Key.Enter:
                RunSelected();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void PaletteRow_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaletteItem { IsHeader: false } item) return;
        Select(_paletteItems.IndexOf(item));
        RunSelected();
    }

    private void MoveSelection(int step)
    {
        int i = _paletteIndex;
        for (int n = 0; n < _paletteItems.Count; n++)
        {
            i = (i + step + _paletteItems.Count) % _paletteItems.Count;
            if (!_paletteItems[i].IsHeader)
            {
                Select(i);
                return;
            }
        }
    }

    private void Select(int index)
    {
        if (_paletteIndex >= 0 && _paletteIndex < _paletteItems.Count) _paletteItems[_paletteIndex].IsSelected = false;
        _paletteIndex = index;
        if (index < 0 || index >= _paletteItems.Count) return;
        _paletteItems[index].IsSelected = true;
        // Keep the chosen row in view
        if (PaletteList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row) row.BringIntoView();
    }

    private void RunSelected()
    {
        if (_paletteIndex < 0 || _paletteIndex >= _paletteItems.Count || _paletteItems[_paletteIndex].Run is not { } run) return;
        ClosePalette();
        run();
    }

    /// <summary>The palette's rows for a query: sensors (with actions for the best match), pages, then other actions.</summary>
    private void BuildPalette(string query)
    {
        bool Match(string text) => query.Length == 0 || text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        var items = new List<PaletteItem>();
        var muted = (Brush)FindResource("MutedBrush");

        if (query.Length > 0 && _app.Sensors.Loaded)
        {
            var sensors = _app.Sensors.All
                .Where(s => Match($"{s.Name} {s.GroupName} {s.TypeLabel}"))
                .OrderByDescending(s => s.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
                .Take(6).ToList();
            if (sensors.Count > 0)
            {
                items.Add(new PaletteItem("SENSORS", header: true));
                foreach (var s in sensors)
                    items.Add(new PaletteItem(s.Name, "", $"{s.ValueText} · {s.GroupName}", HeatBrush(s, muted), () => GraphSensor(s)));

                var top = sensors[0];
                items.Add(new PaletteItem("ACTIONS", header: true));
                items.Add(new PaletteItem($"Graph {top.Name}", "", "Graphs → Selected", muted, () => GraphSensor(top)));
                items.Add(new PaletteItem($"Pin {top.Name} to Overview", "", "", muted, () => PinSensor(top)));
                items.Add(new PaletteItem($"Alert when {top.Name} is over…", "", "", muted, () => AlertOn(top)));
                if (top.Type == SensorType.Temperature)
                    items.Add(new PaletteItem($"Make a fan curve from {top.Name}", "", "", muted, () => FanCurveFrom(top)));
            }
        }

        var pages = new (RadioButton Nav, string Icon)[]
        {
            (NavOverview, ""), (NavSensors, ""), (NavHistory, ""), (NavFans, ""), (NavAlerts, ""),
            (NavGames, ""), (NavOverlays, ""), (NavReports, ""), (NavCrashes, ""), (NavChanges, ""),
            (NavDisk, ""), (NavCleanup, ""), (NavProcesses, ""), (NavStartup, ""), (NavSystem, ""),
            (NavSettings, ""),
        };
        var pageItems = pages.Where(p => Match((string)p.Nav.Content))
                             .Select(p => new PaletteItem((string)p.Nav.Content, p.Icon, "Page", muted, () => p.Nav.IsChecked = true)).ToList();
        if (pageItems.Count > 0)
        {
            items.Add(new PaletteItem("PAGES", header: true));
            items.AddRange(pageItems);
        }

        var actions = new List<PaletteItem>
        {
            new(_app.Overlays.Visible ? "Hide overlays" : "Show overlays", "", _app.Settings.HotkeyOverlays, muted,
                () => _app.Overlays.Visible = !_app.Overlays.Visible),
            new(_app.Overlays.AnyUnlocked ? "Lock overlays" : "Unlock overlays to move them", "", _app.Settings.HotkeyLock, muted,
                _app.Overlays.ToggleLockAll),
            new("Open mini mode", "", "", muted, _app.ShowMini),
            new("Check for updates", "", "", muted, () => CheckForUpdates_Click(this, new RoutedEventArgs())),
            new("Copy diagnostics for a bug report", "", "", muted, () => CopyDiagnostics_Click(this, new RoutedEventArgs())),
        }.Where(a => Match(a.Title)).ToList();
        if (actions.Count > 0)
        {
            items.Add(new PaletteItem(query.Length > 0 && items.Any(i => i.Title == "ACTIONS") ? "MORE ACTIONS" : "ACTIONS", header: true));
            items.AddRange(actions);
        }
        if (items.Count == 0) items.Add(new PaletteItem($"Nothing found for “{query}”", header: true));

        _paletteItems = items;
        _paletteIndex = -1;
        PaletteList.ItemsSource = items;
        PaletteScroller.ScrollToTop();
        MoveSelection(+1);
    }

    private Brush HeatBrush(SensorVm s, Brush fallback) =>
        s.Heat is Heat h ? (Brush)FindResource(Thermal.BrushKey(h)) : fallback;

    // ---------- what the palette's sensor actions do ----------

    /// <summary>Shows the sensor on the Graphs page, in your own selection.</summary>
    internal void GraphSensor(SensorVm s)
    {
        if (!_app.Settings.HistorySelected.Contains(s.Id)) _app.Settings.HistorySelected.Add(s.Id);
        _app.Settings.HistoryMode = "Selected";
        _app.MarkDirty();
        NavHistory.IsChecked = true;
        HistoryPage.Reload();
    }

    /// <summary>Pinned sensors are the same list as Graphs → Selected; Overview shows them with a trend line.</summary>
    internal void PinSensor(SensorVm s)
    {
        if (!_app.Settings.HistorySelected.Contains(s.Id)) _app.Settings.HistorySelected.Add(s.Id);
        _app.MarkDirty();
        NavOverview.IsChecked = true;
        ShowToast($"{s.Name} is pinned on Overview");
    }

    internal void AlertOn(SensorVm s)
    {
        var rule = new AlertRule { SensorId = s.Id, Threshold = AlertService.DefaultThreshold(s) };
        _app.Settings.Alerts.Add(rule);
        NavAlerts.IsChecked = true;
        ShowToast($"New alert for {s.Name} – set its limit below");
    }

    /// <summary>Opens Fans with this temperature as what the selected fan's curve follows (its mode doesn't change).</summary>
    private void FanCurveFrom(SensorVm s)
    {
        NavFans.IsChecked = true;
        ShowToast(FansPage.FollowSensor(s.Id) ? $"The fan curve follows {s.Name} once you click Apply" : "No fan Kelvra can control was found");
    }

    // ---------- "N new" crashes in the sidebar ----------

    private void SetupCrashBadge()
    {
        if (_app.Crashes is { } watcher) watcher.Added += found => Dispatcher.BeginInvoke(() => _ = UpdateCrashBadgeAsync());
        _ = UpdateCrashBadgeAsync();
    }

    private async Task UpdateCrashBadgeAsync()
    {
        if (_app.Recorder is not { } recorder || NavCrashes.IsChecked == true)
        {
            CrashBadge.Visibility = Visibility.Collapsed;
            return;
        }
        // First start: only the last day counts as new (the first read brings in 30 days of older ones)
        long since = Math.Max(_app.Settings.CrashesSeenUnix, HistoryDb.Unix(DateTime.Now.AddDays(-1)));
        int count = await recorder.ReadAsync(db => db.Crashes(since + 1, long.MaxValue).Count);
        CrashBadge.Text = $"{count} new";
        CrashBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MarkCrashesSeen()
    {
        _app.Settings.CrashesSeenUnix = HistoryDb.Unix(DateTime.Now);
        _app.MarkDirty();
        CrashBadge.Visibility = Visibility.Collapsed;
    }
}
