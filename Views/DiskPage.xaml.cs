using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace Kelvra;

public sealed record FileTypeRow(string Extension, string Name, string Detail, string SizeText, string PercentText, Brush Brush);

public sealed record LargeFileRow(DiskNode Node, string Name, string Folder, string Path, string SizeText, Brush Brush);

/// <summary>WizTree-style disk usage: folder tree, file-type breakdown, largest files and a treemap.</summary>
public partial class DiskPage : UserControl
{
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? _cts;
    private DiskScanner? _scanner;
    private DateTime _scanStarted;
    private long? _expectedBytes;
    private string? _target;
    private ScanResult? _result;
    private bool _drivesBuilt;

    public DiskPage()
    {
        InitializeComponent();
        TreeList.ItemsSource = _rows;

        Treemap.NodeClicked += Reveal;
        Treemap.NodeRightClicked += (node, _) => ShowMenu(node, Treemap);
        Treemap.ViewRootChanged += BuildBreadcrumb;
        Treemap.HoverChanged += node => HoverText.Text = node == null
            ? "Hover to see what's what · click to find it in the tree · double-click a folder to zoom in · right-click for actions"
            : $"{node.FullPath}   ·   {ByteFormat.Format(node.Size)}" + (node.IsDirectory ? $"   ·   {node.FileCount:N0} files" : "");

        _progressTimer.Tick += (_, _) => UpdateProgress();

        DuplicatesView.GetRoot = () => _result?.Root;
        DuplicatesView.FilesRemoved += async nodes =>
        {
            if (_result == null) return;
            foreach (var node in nodes.Where(n => ReferenceEquals(n.Ancestors().LastOrDefault(), _result.Root)))
                RemoveFromTree(node);
            Treemap.Redraw();
            var (types, largest) = await Task.Run(() => DiskScanner.Analyze(_result.Root));
            ShowSidePanels(types, largest, _result.Root.Size);
        };
        CleanupView.Cleaned += () =>
        {
            if (_result != null) Subtitle.Text = "Cleanup freed space – press Scan again to refresh the analyzer.";
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && !_drivesBuilt) BuildDriveChips();
        };
        BuildLegend();
    }

    private void DiskTab_Checked(object sender, RoutedEventArgs e)
    {
        if (AnalyzerContent == null || CleanupView == null || DuplicatesView == null) return; // during InitializeComponent
        bool analyzer = AnalyzerTab.IsChecked == true;
        AnalyzerContent.Visibility = analyzer ? Visibility.Visible : Visibility.Collapsed;
        AnalyzerActions.Visibility = analyzer ? Visibility.Visible : Visibility.Collapsed;
        DriveChips.Visibility = analyzer ? Visibility.Visible : Visibility.Collapsed;
        CleanupView.Visibility = CleanupTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DuplicatesView.Visibility = DuplicatesTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- target selection ----------

    private void BuildDriveChips()
    {
        _drivesBuilt = true;
        DriveChips.Children.Clear();
        string system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            long used = drive.TotalSize - drive.TotalFreeSpace;
            double fraction = drive.TotalSize > 0 ? (double)used / drive.TotalSize : 0;

            var bar = new Grid { Width = 70, Height = 4, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = (Brush)FindResource("SwitchOffBrush") });
            bar.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 70 * fraction,
                Background = (Brush)FindResource(fraction > 0.9 ? "HotBrush" : "AccentBrush"),
            });

            string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name.TrimEnd('\\') : $"{drive.Name.TrimEnd('\\')} {drive.VolumeLabel}";
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
            content.Children.Add(bar);
            content.Children.Add(new TextBlock { Text = $"{ByteFormat.Format(used)} / {ByteFormat.Format(drive.TotalSize)}", Opacity = 0.7 });

            var chip = new RadioButton
            {
                Style = (Style)FindResource("Chip"),
                GroupName = "Drives",
                Content = content,
                Tag = drive.Name,
                ToolTip = $"{drive.DriveFormat} · {ByteFormat.Format(drive.TotalFreeSpace)} free",
            };
            System.Windows.Automation.AutomationProperties.SetName(chip, drive.Name);
            chip.Checked += (_, _) => SetTarget(drive.Name);
            DriveChips.Children.Add(chip);
            if (string.Equals(drive.Name, system, StringComparison.OrdinalIgnoreCase)) chip.IsChecked = true;
        }
    }

    private void SetTarget(string path)
    {
        _target = path;
        ScanButton.Content = $"Scan {ShortName(path)}";
    }

    private static string ShortName(string path)
    {
        var trimmed = path.TrimEnd('\\');
        return trimmed.Length <= 3 ? trimmed : Path.GetFileName(trimmed);
    }

    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to scan" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var chip in DriveChips.Children.OfType<RadioButton>()) chip.IsChecked = false;
        SetTarget(dialog.FolderName);
        await ScanAsync(dialog.FolderName);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_target != null) await ScanAsync(_target);
    }

    // ---------- scanning ----------

    private async Task ScanAsync(string path)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var scanner = _scanner = new DiskScanner();

        // Let the previous tree go before building a new one
        _result = null;
        _rows.Clear();
        Treemap.Root = null;
        TypesList.ItemsSource = null;
        LargestList.ItemsSource = null;

        _expectedBytes = null;
        try
        {
            var drive = new DriveInfo(path);
            if (drive.IsReady && string.Equals(drive.Name, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                _expectedBytes = drive.TotalSize - drive.TotalFreeSpace;
        }
        catch
        {
            // not a drive root: no progress estimate
        }

        _scanStarted = DateTime.Now;
        ScanTitle.Text = $"Scanning {path}";
        ProgressFill.Width = 0;
        ScanningPanel.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        Results.Visibility = Visibility.Collapsed;
        ScanButton.IsEnabled = false;
        _progressTimer.Start();
        UpdateProgress();

        try
        {
            var result = await Task.Run(() => scanner.Scan(path, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            ShowResult(result);
        }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException { InnerException: OperationCanceledException })
        {
            if (ReferenceEquals(_cts, cts)) ShowEmpty("Scan cancelled.");
        }
        catch (Exception ex)
        {
            ShowEmpty("Scan failed: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _progressTimer.Stop();
                ScanningPanel.Visibility = Visibility.Collapsed;
                ScanButton.IsEnabled = true;
            }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void UpdateProgress()
    {
        if (_scanner == null) return;
        var elapsed = DateTime.Now - _scanStarted;
        long bytes = _scanner.BytesScanned;
        ScanStats.Text = $"{_scanner.FilesScanned:N0} files · {_scanner.FoldersScanned:N0} folders · {ByteFormat.Format(bytes)} · {elapsed:m\\:ss}";
        ScanCurrent.Text = _scanner.CurrentFolder;

        double fraction = _expectedBytes is > 0 ? Math.Min(0.99, (double)bytes / _expectedBytes.Value) : 0;
        ProgressFill.Width = ProgressTrack.ActualWidth * fraction;
        ProgressTrack.Visibility = _expectedBytes is > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowEmpty(string message)
    {
        Subtitle.Text = message;
        EmptyState.Visibility = Visibility.Visible;
        Results.Visibility = Visibility.Collapsed;
    }

    private void ShowResult(ScanResult result)
    {
        _result = result;
        var root = result.Root;

        string summary = $"{root.FullPath} · {ByteFormat.Format(root.Size)} in {root.FileCount:N0} files and {root.FolderCount:N0} folders · " +
                         $"scanned in {result.Elapsed.TotalSeconds:0.0} s";
        if (result.DriveUsedBytes is long used && used > root.Size)
            summary += $" · {ByteFormat.Format(used - root.Size)} not visible to scans (system/restore data)";
        if (result.Errors > 0) summary += $" · {result.Errors:N0} folders skipped (no access)";
        Subtitle.Text = summary;

        ShowTree(root);
        Treemap.HighlightExtension = null;
        Treemap.Root = root;
        Treemap.Selected = root;
        ShowSidePanels(result.Types, result.Largest, root.Size);

        Results.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
    }

    // ---------- treemap zoom ----------

    private void BuildBreadcrumb(DiskNode? view)
    {
        Breadcrumb.Children.Clear();
        ZoomOutButton.IsEnabled = view?.Parent != null;
        if (view == null) return;

        var chain = view.Ancestors().Reverse().Append(view).ToList();
        for (int i = 0; i < chain.Count; i++)
        {
            var node = chain[i];
            bool current = i == chain.Count - 1;
            if (i > 0)
                Breadcrumb.Children.Add(new TextBlock { Text = "›", Margin = new Thickness(2, 0, 2, 0), Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center });

            var crumb = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(6, 3, 6, 3),
                FontSize = 12,
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                Content = node.Parent == null ? node.FullPath.TrimEnd('\\') : node.Name,
                ToolTip = $"{node.FullPath} · {ByteFormat.Format(node.Size)}",
                IsEnabled = !current,
            };
            crumb.Click += (_, _) => Treemap.ViewRoot = node;
            Breadcrumb.Children.Add(crumb);
        }
        Breadcrumb.Children.Add(new TextBlock
        {
            Text = $"  {ByteFormat.Format(view.Size)} · {view.FileCount:N0} files",
            Opacity = 0.6,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (Treemap.ViewRoot?.Parent is { } parent) Treemap.ViewRoot = parent;
    }

    // ---------- file types / largest files ----------

    private void ShowSidePanels(List<FileTypeStat> types, List<DiskNode> largest, long total)
    {
        TypesList.ItemsSource = types.Take(300).Select(t => new FileTypeRow(
            t.Extension,
            t.Extension.Length == 0 ? "(no extension)" : t.Extension,
            $"{t.Kind.Name} · {t.Count:N0} files",
            ByteFormat.Format(t.Size),
            total > 0 ? $"{100.0 * t.Size / total:0.0} %" : "",
            KindBrush(t.Kind.Color))).ToList();

        LargestList.ItemsSource = largest.Select(n => new LargeFileRow(
            n, n.Name, n.Parent?.FullPath ?? "", n.FullPath, ByteFormat.Format(n.Size),
            KindBrush(FileKinds.Of(n.Extension).Color))).ToList();

        ClearHighlightButton.Visibility = Visibility.Collapsed;
    }

    private void SideTab_Checked(object sender, RoutedEventArgs e)
    {
        if (TypesList == null || LargestList == null) return;
        bool types = TypesTab.IsChecked == true;
        TypesList.Visibility = types ? Visibility.Visible : Visibility.Collapsed;
        LargestList.Visibility = types ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TypesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = TypesList.SelectedItem as FileTypeRow;
        Treemap.HighlightExtension = row?.Extension;
        ClearHighlightButton.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearHighlight_Click(object sender, RoutedEventArgs e) => TypesList.SelectedItem = null;

    private void LargestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LargestList.SelectedItem is LargeFileRow row) Reveal(row.Node);
    }

    private void LargestList_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not LargeFileRow row) return;
        ShowMenu(row.Node, LargestList);
        e.Handled = true;
    }

    private void BuildLegend()
    {
        foreach (var kind in FileKinds.All)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            item.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = KindBrush(kind.Color), Margin = new Thickness(0, 0, 6, 0) });
            item.Children.Add(new TextBlock { Text = kind.Name, FontSize = 11, Opacity = 0.75 });
            Legend.Children.Add(item);
        }
    }

    private static readonly Dictionary<Color, Brush> BrushCache = new();

    private static Brush KindBrush(Color c)
    {
        if (BrushCache.TryGetValue(c, out var b)) return b;
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return BrushCache[c] = brush;
    }

    // ---------- actions ----------

    private void ShowMenu(DiskNode node, UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        menu.Items.Add(MenuItem("Open", "\uE8A7", () => Open(node)));
        menu.Items.Add(MenuItem("Show in Explorer", "\uE838", () => ShowInExplorer(node)));
        menu.Items.Add(MenuItem("Copy path", "\uE8C8", () => Clipboard.SetText(node.FullPath)));
        var zoomTarget = node.IsDirectory ? node : node.Parent;
        if (zoomTarget != null && !ReferenceEquals(zoomTarget, Treemap.ViewRoot))
            menu.Items.Add(MenuItem($"Zoom map into “{zoomTarget.Name}”", "\uE71E", () => Treemap.ViewRoot = zoomTarget));
        menu.Items.Add(new Separator());
        var recycle = MenuItem("Move to Recycle Bin…", "\uE74D", () => RecycleAsync(node));
        recycle.IsEnabled = node.Parent != null;
        menu.Items.Add(recycle);
        menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string header, string icon, Action action)
    {
        var item = new MenuItem { Header = header, Tag = icon };
        item.Click += (_, _) => action();
        return item;
    }

    private static void Open(DiskNode node) => TryStart(new ProcessStartInfo(node.FullPath) { UseShellExecute = true });

    private static void ShowInExplorer(DiskNode node) =>
        TryStart(new ProcessStartInfo("explorer.exe", $"/select,\"{node.FullPath}\"") { UseShellExecute = true });

    private static void TryStart(ProcessStartInfo info)
    {
        try
        {
            Process.Start(info);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Kelvra", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RecycleAsync(DiskNode node)
    {
        if (node.Parent == null) return;
        string what = node.IsDirectory ? $"the folder “{node.Name}” ({node.FileCount:N0} files, {ByteFormat.Format(node.Size)})"
                                       : $"“{node.Name}” ({ByteFormat.Format(node.Size)})";
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"Move {what} to the Recycle Bin?\n\n{node.FullPath}\n\nYou can restore it from the Recycle Bin later.",
            "Move to Recycle Bin", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        string path = node.FullPath;
        try
        {
            await Task.Run(() =>
            {
                if (node.IsDirectory)
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            });
        }
        catch (OperationCanceledException)
        {
            return; // user cancelled in the Windows dialog
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), $"Couldn't move it to the Recycle Bin:\n{ex.Message}", "Kelvra",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_result == null) return;
        // If the map was zoomed into what we just removed, step back out to its parent
        if (Treemap.ViewRoot is { } view && (ReferenceEquals(view, node) || view.Ancestors().Contains(node)))
            Treemap.ViewRoot = node.Parent;
        RemoveFromTree(node);
        Treemap.Redraw();
        var (types, largest) = await Task.Run(() => DiskScanner.Analyze(_result.Root));
        ShowSidePanels(types, largest, _result.Root.Size);
    }
}
