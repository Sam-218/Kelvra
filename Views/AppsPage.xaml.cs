using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace Kelvra;

/// <summary>Processes (live CPU / memory / disk, end task) and startup apps (enable / disable).</summary>
public partial class AppsPage : UserControl
{
    private static readonly string[] NumericKeys = { nameof(ProcessRow.Cpu), nameof(ProcessRow.Memory), nameof(ProcessRow.Disk), nameof(ProcessRow.Pid) };

    private readonly ProcessMonitor _monitor = new();
    private readonly ListCollectionView _processView;
    // Only runs while the Processes tab is on screen: sampling every process isn't free
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ListCollectionView? _startupView;
    private string _sortKey = nameof(ProcessRow.Cpu);
    private bool _sortDescending = true;
    private bool _refreshing;

    public AppsPage()
    {
        InitializeComponent();
        _processView = new ListCollectionView(_monitor.Rows) { Filter = MatchesProcess };
        ProcessList.ItemsSource = _processView;
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
        _processView?.Refresh();
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
            Subtitle.Text = $"{_monitor.Rows.Count} processes  ·  CPU {_monitor.TotalCpu:0} %  ·  " +
                            $"{ByteFormat.Format(_monitor.TotalMemory)} memory in use by processes";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private bool MatchesProcess(object o)
    {
        var p = (ProcessRow)o;
        string q = Query;
        return q.Length == 0
               || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || p.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
               || p.Pid.ToString() == q;
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        string key = (string)((Button)sender).Tag;
        bool numeric = NumericKeys.Contains(key) && key != nameof(ProcessRow.Pid);
        if (_sortKey == key) _sortDescending = !_sortDescending;
        else
        {
            _sortKey = key;
            _sortDescending = numeric;
        }
        ApplySort();
    }

    private void ApplySort()
    {
        using (_processView.DeferRefresh())
        {
            _processView.SortDescriptions.Clear();
            _processView.SortDescriptions.Add(new SortDescription(_sortKey,
                _sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending));
            _processView.LiveSortingProperties.Clear();
            _processView.LiveSortingProperties.Add(_sortKey);
            _processView.IsLiveSorting = true;
        }

        string Arrow(string key) => _sortKey == key ? (_sortDescending ? "  ▼" : "  ▲") : "";
        SortName.Content = "NAME" + Arrow(nameof(ProcessRow.DisplayName));
        SortPid.Content = "PID" + Arrow(nameof(ProcessRow.Pid));
        SortCpu.Content = "CPU" + Arrow(nameof(ProcessRow.Cpu));
        SortMemory.Content = "MEMORY" + Arrow(nameof(ProcessRow.Memory));
        SortDisk.Content = "DISK" + Arrow(nameof(ProcessRow.Disk));
    }

    private void ProcessList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ProcessRow row) return;
        ProcessList.SelectedItem = row;
        var menu = new ContextMenu { PlacementTarget = ProcessList, Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item("End task", "", () => EndTask(row)));
        var open = Item("Open file location", "", () => ShowInExplorer(row.Path));
        open.IsEnabled = row.Path != null;
        menu.Items.Add(open);
        menu.Items.Add(Item("Copy path", "", () => Clipboard.SetText(row.Path ?? row.Name)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Search online", "", () => Start(new ProcessStartInfo(
            $"https://www.bing.com/search?q={Uri.EscapeDataString(row.Name + ".exe")}") { UseShellExecute = true })));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void ProcessList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && ProcessList.SelectedItem is ProcessRow row) EndTask(row);
    }

    private void EndTask(ProcessRow row)
    {
        if (ProcessGuard.IsCritical(row.Pid, row.Name, row.Path))
        {
            MessageBox.Show(Window.GetWindow(this),
                $"“{row.DisplayName}” ({row.Name}, PID {row.Pid}) is a critical Windows process. Ending it would crash Windows " +
                "(blue screen) or sign everyone out, so Kelvra won't do it.",
                "End task", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"End “{row.DisplayName}” ({row.Name}.exe, PID {row.Pid})?\n\nUnsaved work in that app will be lost.",
            "End task", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            using var p = Process.GetProcessById(row.Pid);
            p.Kill();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), $"Couldn't end {row.Name}:\n{ex.Message}", "Kelvra",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
