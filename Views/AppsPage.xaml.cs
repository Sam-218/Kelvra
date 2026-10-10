using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace Kelvra;

/// <summary>
/// Processes grouped by app like Task Manager (live CPU / memory / disk, end task) and startup apps (enable / disable).
/// </summary>
public partial class AppsPage : UserControl
{
    private static readonly string[] NumericKeys = { nameof(UsageRow.Cpu), nameof(UsageRow.Memory), nameof(UsageRow.Disk) };

    private readonly ProcessMonitor _monitor = new();
    // What the list shows, in order: kept in step with Move/Insert/Remove so selection and scrolling survive refreshes
    private readonly ObservableCollection<UsageRow> _items = new();
    // Once a second, like Task Manager; only while the Processes tab is on screen
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ListCollectionView? _startupView;
    private string _sortKey = nameof(UsageRow.Cpu);
    private bool _sortDescending = true;
    private bool _refreshing;

    public AppsPage()
    {
        InitializeComponent();
        ProcessList.ItemsSource = _items;
        ApplySort();
        StartupItem.ChangeFailed += (item, error) =>
            MessageBox.Show(Window.GetWindow(this), $"Couldn't change “{item.Name}”:\n{error}", "Kelvra", MessageBoxButton.OK, MessageBoxImage.Warning);

        _timer.Tick += async (_, _) => await RefreshProcessesAsync();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && ProcessesTab.IsChecked == true)
            {
                _timer.Start();
                await RefreshProcessesAsync();
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (ProcessesView == null) return;
        bool processes = ProcessesTab.IsChecked == true;
        ProcessesView.Visibility = processes ? Visibility.Visible : Visibility.Collapsed;
        StartupView.Visibility = processes ? Visibility.Collapsed : Visibility.Visible;
        if (processes) _timer.Start();
        else
        {
            _timer.Stop();
            LoadStartup();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Rebuild();
        _startupView?.Refresh();
    }

    private string Query => SearchBox.Text.Trim();

    // ---------- processes ----------

    private async Task RefreshProcessesAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            await _monitor.RefreshAsync();
            Rebuild();
            string memory = _monitor.MemoryTotal > 0
                ? $"  ·  Memory {ByteFormat.Format(_monitor.MemoryUsed)} of {ByteFormat.Format(_monitor.MemoryTotal)} " +
                  $"({100.0 * _monitor.MemoryUsed / _monitor.MemoryTotal:0} %)"
                : "";
            Subtitle.Text = $"{_monitor.Apps.Count} apps · {_monitor.ProcessCount} processes  ·  CPU {_monitor.TotalCpu:0} %{memory}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private bool Matches(UsageRow r)
    {
        string q = Query;
        return q.Length == 0
               || r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
               || r.ImageName.Contains(q, StringComparison.OrdinalIgnoreCase)
               || (r is ProcessRow p && p.Pid.ToString() == q);
    }

    /// <summary>
    /// Sorts and filters the apps, puts the processes of expanded apps right under them, and brings the list in line.
    /// While searching, an app whose own name doesn't match shows just the processes that do.
    /// </summary>
    private void Rebuild()
    {
        bool searching = Query.Length > 0;
        var wanted = new List<UsageRow>(_items.Count + 16);
        foreach (var app in _monitor.Apps.OrderBy(a => a, Comparer<UsageRow>.Create(Compare)))
        {
            if (app is not AppGroup group)
            {
                if (Matches(app)) wanted.Add(app);
                continue;
            }
            bool self = Matches(group);
            var members = group.Members.Where(m => !searching || self || Matches(m)).ToList();
            if (!self && members.Count == 0) continue;
            wanted.Add(group);
            if (group.IsExpanded || (searching && !self))
            {
                members.Sort(Compare);
                wanted.AddRange(members);
            }
        }
        Sync(_items, wanted);
    }

    /// <summary>Moves, inserts and removes rows so <paramref name="items"/> equals <paramref name="wanted"/>.</summary>
    internal static void Sync<T>(ObservableCollection<T> items, IReadOnlyList<T> wanted) where T : class
    {
        var keep = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance);
        for (int i = items.Count - 1; i >= 0; i--)
            if (!keep.Contains(items[i])) items.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < items.Count && ReferenceEquals(items[i], wanted[i])) continue;
            int at = -1;
            for (int j = i + 1; j < items.Count; j++)
            {
                if (!ReferenceEquals(items[j], wanted[i])) continue;
                at = j;
                break;
            }
            if (at >= 0) items.Move(at, i);
            else items.Insert(i, wanted[i]);
        }
    }

    /// <summary>The chosen column; equal values (as shown, so 0.0 % and 0.04 % tie) keep a fixed order instead of swapping every second.</summary>
    private int Compare(UsageRow a, UsageRow b)
    {
        int c = _sortKey switch
        {
            nameof(UsageRow.Cpu) => Math.Round(a.Cpu, 1).CompareTo(Math.Round(b.Cpu, 1)),
            nameof(UsageRow.Memory) => a.Memory.CompareTo(b.Memory),
            nameof(UsageRow.Disk) => Math.Round(a.Disk / 104_857.6).CompareTo(Math.Round(b.Disk / 104_857.6)), // 0.1 MB/s steps
            nameof(UsageRow.Pid) => a.Pid.CompareTo(b.Pid),
            _ => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase),
        };
        if (_sortDescending) c = -c;
        if (c != 0) return c;
        c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        return c != 0 ? c : a.Pid.CompareTo(b.Pid);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        string key = (string)((Button)sender).Tag;
        if (_sortKey == key) _sortDescending = !_sortDescending;
        else
        {
            _sortKey = key;
            _sortDescending = NumericKeys.Contains(key);
        }
        ApplySort();
    }

    private void ApplySort()
    {
        Rebuild();
        string Arrow(string key) => _sortKey == key ? (_sortDescending ? "  ▼" : "  ▲") : "";
        SortName.Content = "NAME" + Arrow(nameof(UsageRow.DisplayName));
        SortPid.Content = "PID" + Arrow(nameof(UsageRow.Pid));
        SortCpu.Content = "CPU" + Arrow(nameof(UsageRow.Cpu));
        SortMemory.Content = "MEMORY" + Arrow(nameof(UsageRow.Memory));
        SortDisk.Content = "DISK" + Arrow(nameof(UsageRow.Disk));
    }

    private void SetExpanded(AppGroup group, bool expanded)
    {
        if (group.IsExpanded == expanded) return;
        group.IsExpanded = expanded;
        Rebuild();
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppGroup group) return;
        SetExpanded(group, !group.IsExpanded);
        ProcessList.SelectedItem = group;
    }

    private void ProcessList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is AppGroup group) SetExpanded(group, !group.IsExpanded);
    }

    private void ProcessList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not UsageRow row) return;
        ProcessList.SelectedItem = row;
        var menu = new ContextMenu { PlacementTarget = ProcessList, Placement = PlacementMode.MousePoint };
        if (row is AppGroup group)
        {
            menu.Items.Add(group.IsExpanded
                ? Item("Hide its processes", "", () => SetExpanded(group, false))
                : Item($"Show its {group.Count} processes", "", () => SetExpanded(group, true)));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item(row is AppGroup ? "End task (all its processes)" : "End task", "", () => EndTask(row)));
        var open = Item("Open file location", "", () => ShowInExplorer(row.Path));
        open.IsEnabled = row.Path != null;
        menu.Items.Add(open);
        menu.Items.Add(Item("Copy path", "", () => Clipboard.SetText(row.Path ?? row.ImageName)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Search online", "", () => Start(new ProcessStartInfo(
            $"https://www.bing.com/search?q={Uri.EscapeDataString(row.ImageName)}") { UseShellExecute = true })));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void ProcessList_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key, ProcessList.SelectedItem)
        {
            case (Key.Delete, UsageRow row):
                EndTask(row);
                break;
            case (Key.Right, AppGroup group):
                SetExpanded(group, true);
                break;
            case (Key.Left, AppGroup group):
                SetExpanded(group, false);
                break;
            case (Key.Left, ProcessRow { IsChild: true } child):
                // Back up to the app the process belongs to
                ProcessList.SelectedItem = _items.OfType<AppGroup>().FirstOrDefault(g => g.Members.Contains(child));
                break;
            case (Key.Enter, AppGroup group):
                SetExpanded(group, !group.IsExpanded);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Ends one process, or every process of an app (only the app's own ones, not other apps it started).</summary>
    private void EndTask(UsageRow row)
    {
        var targets = row is AppGroup g ? g.Members.ToList() : new List<ProcessRow> { (ProcessRow)row };
        var critical = targets.FirstOrDefault(p => ProcessGuard.IsCritical(p.Pid, p.Name, p.Path));
        if (critical != null)
        {
            MessageBox.Show(Window.GetWindow(this),
                $"“{critical.DisplayName}” ({critical.ImageName}, PID {critical.Pid}) is a critical Windows process. Ending it would crash " +
                "Windows (blue screen) or sign everyone out, so Kelvra won't do it.",
                "End task", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string what = row is AppGroup group
            ? $"“{group.DisplayName}” and all {group.Count} of its processes"
            : $"“{row.DisplayName}” ({row.ImageName}, PID {row.Pid})";
        var answer = MessageBox.Show(Window.GetWindow(this), $"End {what}?\n\nUnsaved work in that app will be lost.",
            "End task", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var failed = new List<string>();
        // The app's own process first, so it can't restart the helpers that are ended after it
        foreach (var p in targets.OrderBy(p => row is AppGroup a && ReferenceEquals(p, a.Root) ? 0 : 1))
        {
            try
            {
                using var process = Process.GetProcessById(p.Pid);
                // That PID belongs to a newer process now
                if (Math.Abs(process.StartTime.ToFileTimeUtc() - p.Created) > TimeSpan.TicksPerSecond) continue;
                process.Kill();
            }
            catch (ArgumentException)
            {
                // already gone
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                failed.Add($"{p.ImageName} (PID {p.Pid}): {ex.Message}");
            }
        }
        if (failed.Count > 0)
            MessageBox.Show(Window.GetWindow(this), "Couldn't end:\n" + string.Join("\n", failed.Take(8)), "Kelvra",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ---------- startup apps ----------

    private async void LoadStartup()
    {
        Subtitle.Text = "Reading startup apps…";
        var items = await Task.Run(StartupManager.Load);
        _startupView = new ListCollectionView(items)
        {
            Filter = o =>
            {
                var s = (StartupItem)o;
                string q = Query;
                return q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || s.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || s.Command.Contains(q, StringComparison.OrdinalIgnoreCase);
            },
        };
        StartupList.ItemsSource = _startupView;
        int on = items.Count(i => i.Enabled);
        Subtitle.Text = $"{items.Count} startup apps · {on} enabled, {items.Count - on} disabled";
    }

    private void StartupList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not StartupItem item) return;
        var menu = new ContextMenu { PlacementTarget = StartupList, Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item(item.Enabled ? "Disable" : "Enable", "", () => item.Enabled = !item.Enabled));
        var open = Item("Open file location", "", () => ShowInExplorer(item.ImagePath));
        open.IsEnabled = item.ImagePath != null && File.Exists(item.ImagePath);
        menu.Items.Add(open);
        menu.Items.Add(Item("Copy command", "", () => Clipboard.SetText(item.Command)));
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---------- helpers ----------

    private static MenuItem Item(string header, string icon, Action action)
    {
        var item = new MenuItem { Header = header, Tag = icon };
        item.Click += (_, _) => action();
        return item;
    }

    private static void ShowInExplorer(string? path)
    {
        if (path != null) Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private static void Start(ProcessStartInfo info)
    {
        try { Process.Start(info); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Kelvra", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
