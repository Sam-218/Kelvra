using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Kelvra;

/// <summary>AIDA64-style summary: Windows, CPU, GPU, RAM sticks, board/BIOS, drives (with health) and network.
/// Cards can be rearranged by dragging their header onto another card.</summary>
public partial class SystemPage : UserControl
{
    private const string DragFormat = "Kelvra.SystemCard";

    private readonly ObservableCollection<InfoSection> _sections = new();
    private bool _loaded;
    private bool _loading;
    private Point _dragStart;
    private InfoSection? _dragCard;

    public SystemPage()
    {
        InitializeComponent();
        Sections.ItemsSource = _sections;
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && !_loaded) await LoadAsync();
        };
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        LoadingText.Visibility = Visibility.Visible;
        try
        {
            var sensors = App.Current.Sensors.All.ToList(); // snapshot for the worker thread
            var fresh = await Task.Run(() => SystemInfo.Collect(sensors));
            _sections.Clear();
            foreach (var s in Ordered(fresh)) _sections.Add(s);
            Subtitle.Text = $"A summary of your PC's hardware and Windows · collected {DateTime.Now:HH:mm}";
            _loaded = true;
        }
        finally
        {
            _loading = false;
            LoadingText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Saved order first; cards not in it (new drive, …) keep their default spot after those.</summary>
    private IEnumerable<InfoSection> Ordered(List<InfoSection> sections)
    {
        var order = App.Current.Settings.SystemCardOrder;
        ResetOrderButton.Visibility = order.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return sections.Select((s, i) => (s, rank: order.IndexOf(s.Title) is var r and >= 0 ? r : 10_000 + i))
                       .OrderBy(x => x.rank).Select(x => x.s);
    }

    private void SaveOrder()
    {
        App.Current.Settings.SystemCardOrder = _sections.Select(s => s.Title).ToList();
        App.Current.MarkDirty();
        ResetOrderButton.Visibility = Visibility.Visible;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void ResetOrder_Click(object sender, RoutedEventArgs e)
    {
        App.Current.Settings.SystemCardOrder = new List<string>();
        App.Current.MarkDirty();
        await LoadAsync();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_sections.Count == 0) return;
        Clipboard.SetText(SystemInfo.ToText(_sections));
        Subtitle.Text = "Copied to the clipboard.";
    }

    // ---------- drag the card header onto another card ----------

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragCard = (sender as FrameworkElement)?.DataContext as InfoSection;
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCard == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var card = _dragCard;
        _dragCard = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DragFormat, card), DragDropEffects.Move);
    }

    private void Card_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        bool ok = e.Data.GetData(DragFormat) is InfoSection dragged && !ReferenceEquals(dragged, (sender as FrameworkElement)?.DataContext);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok) ((Border)sender).BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
    }

    private void Card_DragLeave(object sender, DragEventArgs e) => ((Border)sender).ClearValue(Border.BorderBrushProperty);

    private void Card_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var border = (Border)sender;
        border.ClearValue(Border.BorderBrushProperty);
        if (e.Data.GetData(DragFormat) is not InfoSection dragged || border.DataContext is not InfoSection target) return;

        int from = _sections.IndexOf(dragged), to = _sections.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        _sections.Move(from, to);
        SaveOrder();
    }
}
