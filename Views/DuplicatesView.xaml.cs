using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace Kelvra;

/// <summary>Disk tab → Duplicates: find identical files in the last scan and recycle the extra copies.</summary>
public partial class DuplicatesView : UserControl
{
    private const int MaxGroupsShown = 500;

    private readonly App _app = App.Current;
    private readonly ObservableCollection<object> _rows = new();
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private List<DuplicateGroup> _groups = new();
    private DuplicateFinder? _finder;
    private CancellationTokenSource? _cts;

    /// <summary>Set by the Disk page: the tree from the most recent scan (null = nothing scanned yet).</summary>
    public Func<DiskNode?>? GetRoot { get; set; }

    /// <summary>Raised after files were moved to the Recycle Bin so the analyzer can update.</summary>
    public event Action<List<DiskNode>>? FilesRemoved;

    public DuplicatesView()
    {
        InitializeComponent();
        ResultList.ItemsSource = _rows;
        foreach (RadioButton r in SizeButtons.Children)
            r.IsChecked = int.Parse((string)r.Tag) == _app.Settings.DuplicateMinSizeMb;
        _progressTimer.Tick += (_, _) => UpdateProgress();
    }

    private void Size_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || !IsInitialized) return;
        _app.Settings.DuplicateMinSizeMb = int.Parse((string)rb.Tag);
        _app.MarkDirty();
    }

    // ---------- searching ----------

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        var root = GetRoot?.Invoke();
        if (root == null)
        {
            EmptyText.Text = "Scan a drive or folder in Analyzer first, then press “Find duplicates”.";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var finder = _finder = new DuplicateFinder();
        long minSize = _app.Settings.DuplicateMinSizeMb * 1024L * 1024L;
        bool skipWindows = SkipWindows.IsChecked == true;

        _rows.Clear();
        EmptyText.Visibility = Visibility.Collapsed;
        ActionBar.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        FindButton.IsEnabled = false;
        _progressTimer.Start();
        var started = DateTime.Now;

        try
        {
            _groups = await Task.Run(() => finder.Find(root, minSize, skipWindows, cts.Token));
            ShowGroups();
            Summary.Text = $"{_groups.Count:N0} sets of duplicates in {root.FullPath} · " +
                           $"{ByteFormat.Format(_groups.Sum(g => g.Wasted))} could be freed · took {(DateTime.Now - started).TotalSeconds:0} s";
        }
        catch (OperationCanceledException)
        {
            Summary.Text = "Search cancelled.";
        }
        finally
        {
            _progressTimer.Stop();
            ProgressPanel.Visibility = Visibility.Collapsed;
            FindButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void UpdateProgress()
    {
        if (_finder == null) return;
        ProgressText.Text = _finder.Total > 0 ? $"{_finder.Stage}  {_finder.Done:N0} / {_finder.Total:N0}" : _finder.Stage;
        ProgressFill.Width = _finder.Total > 0 ? ProgressTrack.ActualWidth * _finder.Done / _finder.Total : 0;
    }

    private void ShowGroups()
    {
        _rows.Clear();
        foreach (var g in _groups.Take(MaxGroupsShown))
        {
            _rows.Add(g);
            foreach (var f in g.Files) _rows.Add(f);
        }
        EmptyText.Text = "No duplicates found. 🎉";
        EmptyText.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActionBar.Visibility = _groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    // ---------- selecting ----------

    private void KeepNewest_Click(object sender, RoutedEventArgs e) => SelectAllBut(g => g.Files.MaxBy(f => f.Modified)!);
    private void KeepOldest_Click(object sender, RoutedEventArgs e) => SelectAllBut(g => g.Files.MinBy(f => f.Modified)!);

    private void SelectAllBut(Func<DuplicateGroup, DuplicateFile> keep)
    {
        foreach (var g in _groups.Take(MaxGroupsShown))
        {
            var kept = keep(g);
            foreach (var f in g.Files) f.Selected = !ReferenceEquals(f, kept);
        }
        UpdateSelection();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _groups.SelectMany(g => g.Files)) f.Selected = false;
        UpdateSelection();
    }

    private void FileCheck_Click(object sender, RoutedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var selected = _groups.SelectMany(g => g.Files).Where(f => f.Selected).ToList();
        RecycleButton.IsEnabled = selected.Count > 0;
        RecycleButton.Content = selected.Count > 0
            ? $"Move {selected.Count:N0} files ({ByteFormat.Format(selected.Sum(f => f.Node.Size))}) to Recycle Bin"
            : "Move selected to Recycle Bin";
    }

    // ---------- actions ----------

    private async void Recycle_Click(object sender, RoutedEventArgs e)
    {
        var selected = _groups.SelectMany(g => g.Files).Where(f => f.Selected).ToList();
        if (selected.Count == 0) return;
        if (_groups.Any(g => g.Files.All(f => f.Selected)))
        {
            MessageBox.Show(Window.GetWindow(this), "In at least one set every copy is selected. Keep at least one copy of each file.",
                "Duplicates", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"Move {selected.Count:N0} duplicate files ({ByteFormat.Format(selected.Sum(f => f.Node.Size))}) to the Recycle Bin?\n\n" +
            "One copy of each file is kept. You can restore them from the Recycle Bin.",
            "Duplicates", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        RecycleButton.IsEnabled = false;
        var removed = new List<DuplicateFile>();
        await Task.Run(() =>
        {
            foreach (var f in selected)
            {
                try
                {
                    FileSystem.DeleteFile(f.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    lock (removed) removed.Add(f);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    // locked, no access, or user cancelled – keep it in the list
                }
            }
        });

        foreach (var g in _groups) g.Files.RemoveAll(removed.Contains);
        _groups.RemoveAll(g => g.Files.Count < 2);
        ShowGroups();
        Summary.Text = $"Moved {removed.Count:N0} files ({ByteFormat.Format(removed.Sum(f => f.Node.Size))}) to the Recycle Bin." +
                       (removed.Count < selected.Count ? $" {selected.Count - removed.Count} couldn't be moved (in use?)." : "");
        FilesRemoved?.Invoke(removed.Select(f => f.Node).ToList());
    }

    private void ShowFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DuplicateFile f)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{f.Path}\"") { UseShellExecute = true });
    }
}
