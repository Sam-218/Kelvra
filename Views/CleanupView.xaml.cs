using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Kelvra;

/// <summary>Disk tab → Cleanup: measure and empty temp folders, caches and the Recycle Bin.</summary>
public partial class CleanupView : UserControl
{
    private readonly App _app = App.Current;
    private List<CleanupTarget> _targets = new();
    private bool _measured;
    private bool _busy;

    /// <summary>Raised after cleaning so the disk analyzer knows its numbers are stale.</summary>
    public event Action? Cleaned;

    public CleanupView()
    {
        InitializeComponent();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && !_measured) await MeasureAsync();
        };
    }

    private async Task MeasureAsync()
    {
        if (_busy) return;
        _busy = true;
        CleanButton.IsEnabled = false;
        var unchecked_ = new HashSet<string>(_app.Settings.CleanupUnchecked);
        _targets = await Task.Run(CleanupService.Targets);
        foreach (var t in _targets) t.Selected = !unchecked_.Contains(t.Id);
        TargetList.ItemsSource = _targets;

        await Task.WhenAll(_targets.Select(t => Task.Run(() => CleanupService.Measure(t))));
        _measured = true;
        _busy = false;
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        long selected = _targets.Where(t => t.Selected && t.Size > 0).Sum(t => t.Size);
        long all = _targets.Where(t => t.Size > 0).Sum(t => t.Size);
        Summary.Text = $"{ByteFormat.Format(all)} found in {_targets.Count(t => t.Size > 0)} places. Files in use are always skipped.";
        CleanButton.Content = selected > 0 ? $"Clean {ByteFormat.Format(selected)}" : "Clean selected";
        CleanButton.IsEnabled = !_busy && selected > 0;
    }

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.CleanupUnchecked = _targets.Where(t => !t.Selected).Select(t => t.Id).ToList();
        _app.MarkDirty();
        UpdateTotals();
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        ResultBar.Visibility = Visibility.Collapsed;
        await MeasureAsync();
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _targets.Where(t => t.Selected && t.Size > 0).ToList();
        if (chosen.Count == 0) return;

        string list = string.Join("\n", chosen.Select(t => $"  • {t.Name} – {t.SizeText}"));
        string warning = chosen.Any(t => t.IsRecycleBin) ? "\n\nEmptying the Recycle Bin can't be undone." : "";
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"Permanently delete these files?\n\n{list}{warning}\n\nFiles that are in use will be skipped.",
            "Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _busy = true;
        CleanButton.IsEnabled = false;
        CleanButton.Content = "Cleaning…";
        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (var t in chosen)
        {
            t.Status = "Cleaning…";
            var (f, d, s) = await Task.Run(() => CleanupService.Clean(t));
            freed += f;
            deleted += d;
            skipped += s;
            await Task.Run(() => CleanupService.Measure(t));
        }
        _busy = false;

        ResultText.Text = $"Freed {ByteFormat.Format(freed)} ({deleted:N0} files deleted)." +
                          (skipped > 0 ? $" {skipped:N0} files were in use or protected and were left alone." : "");
        ResultBar.Visibility = Visibility.Visible;
        UpdateTotals();
        Cleaned?.Invoke();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CleanupTarget t) return;
        string? target = t.IsRecycleBin ? "shell:RecycleBinFolder" : t.FirstFolder;
        if (target == null)
        {
            MessageBox.Show(Window.GetWindow(this), "This folder doesn't exist on your PC.", "Cleanup");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
    }
}
