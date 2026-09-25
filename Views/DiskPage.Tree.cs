using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Kelvra;

/// <summary>One row of the disk tree (a flattened, expandable view of <see cref="DiskNode"/>s).</summary>
public sealed class DiskRow : ObservableObject
{
    private static readonly Brush FolderBrush = Frozen(Color.FromRgb(0xE9, 0xC2, 0x4A));
    private static readonly Dictionary<Color, Brush> KindBrushes = new();
    private bool _isExpanded;

    public DiskRow(DiskNode node, int depth)
    {
        Node = node;
        Depth = depth;
    }

    /// <summary>"… N more files" placeholder for very large folders.</summary>
    public DiskRow(DiskNode parent, int depth, int hiddenCount, long hiddenBytes) : this(parent, depth)
    {
        IsMore = true;
        HiddenCount = hiddenCount;
        HiddenBytes = hiddenBytes;
    }

    public DiskNode Node { get; }
    public int Depth { get; }
    public bool IsMore { get; }
    public int HiddenCount { get; }
    public long HiddenBytes { get; }

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool CanExpand => !IsMore && Node.IsDirectory && Node.Children is { Count: > 0 };

    public string Name => IsMore
        ? $"… {HiddenCount:N0} more items"
        : Node.Parent == null ? Node.FullPath : Node.Name;

    public string Icon => IsMore ? "" : Node.IsDirectory ? "" : "";

    public Brush IconBrush
    {
        get
        {
            if (IsMore) return Brushes.Gray;
            if (Node.IsDirectory) return FolderBrush;
            var color = FileKinds.Of(Node.Extension).Color;
            lock (KindBrushes)
            {
                if (!KindBrushes.TryGetValue(color, out var b)) KindBrushes[color] = b = Frozen(color);
                return b;
            }
        }
    }

    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    private long Size => IsMore ? HiddenBytes : Node.Size;
    private long ParentSize => IsMore ? Node.Size : Node.Parent?.Size ?? Node.Size;
    private double Fraction => ParentSize > 0 ? Math.Clamp((double)Size / ParentSize, 0, 1) : 0;

    public string SizeText => ByteFormat.Format(Size);
    public double BarWidth => Fraction * 60;
    public string PercentText => $"{Fraction * 100:0.0} %";
    public string FilesText => IsMore || !Node.IsDirectory ? "" : Node.FileCount.ToString("N0");

    /// <summary>Re-reads sizes after the tree changed (e.g. a deletion).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(BarWidth));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(FilesText));
        OnPropertyChanged(nameof(CanExpand));
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public partial class DiskPage
{
    /// <summary>Folders with more entries than this show the rest as a single "… more" row.</summary>
    private const int MaxChildRows = 2000;

    private readonly ObservableCollection<DiskRow> _rows = new();

    private void ShowTree(DiskNode root)
    {
        _rows.Clear();
        var rootRow = new DiskRow(root, 0);
        _rows.Add(rootRow);
        Expand(rootRow);
        TreeList.SelectedIndex = 0;
    }

    private void Expand(DiskRow row)
    {
        if (!row.CanExpand || row.IsExpanded) return;
        int index = _rows.IndexOf(row);
        var children = row.Node.Children!;
        int shown = Math.Min(children.Count, MaxChildRows);

        for (int i = 0; i < shown; i++)
            _rows.Insert(index + 1 + i, new DiskRow(children[i], row.Depth + 1));

        if (children.Count > shown)
        {
            long hidden = children.Skip(shown).Sum(c => c.Size);
            _rows.Insert(index + 1 + shown, new DiskRow(row.Node, row.Depth + 1, children.Count - shown, hidden));
        }
        row.IsExpanded = true;
    }

    private void Collapse(DiskRow row)
    {
        int index = _rows.IndexOf(row);
        while (index + 1 < _rows.Count && _rows[index + 1].Depth > row.Depth) _rows.RemoveAt(index + 1);
        row.IsExpanded = false;
    }

    private void Toggle(DiskRow row)
    {
        if (row.IsExpanded) Collapse(row);
        else Expand(row);
    }

    /// <summary>Expands the tree down to <paramref name="node"/> and selects it.</summary>
    private void Reveal(DiskNode node)
    {
        DiskRow? last = null;
        foreach (var ancestor in node.Ancestors().Reverse())
        {
            var row = _rows.FirstOrDefault(r => !r.IsMore && ReferenceEquals(r.Node, ancestor));
            if (row == null) break;
            Expand(row);
            last = row;
        }

        var target = _rows.FirstOrDefault(r => !r.IsMore && ReferenceEquals(r.Node, node)) ?? last;
        if (target == null) return;
        TreeList.SelectedItem = target;
        TreeList.ScrollIntoView(target);
    }

    /// <summary>Takes a deleted item out of the tree and updates every folder above it.</summary>
    private void RemoveFromTree(DiskNode node)
    {
        if (node.Parent is not { } parent) return;
        parent.Children?.Remove(node);

        int files = node.IsDirectory ? node.FileCount : 1;
        int folders = node.IsDirectory ? node.FolderCount + 1 : 0;
        foreach (var a in node.Ancestors())
        {
            a.Size -= node.Size;
            a.FileCount -= files;
            a.FolderCount -= folders;
            a.Children?.Sort((x, y) => y.Size.CompareTo(x.Size));
        }

        var row = _rows.FirstOrDefault(r => !r.IsMore && ReferenceEquals(r.Node, node));
        if (row != null)
        {
            if (row.IsExpanded) Collapse(row);
            _rows.Remove(row);
        }
        foreach (var r in _rows) r.Refresh();
    }

    // ---------- tree events ----------

    private void Chevron_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DiskRow row) Toggle(row);
    }

    private void TreeList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TreeList.SelectedItem is DiskRow row && row.CanExpand) Toggle(row);
    }

    private void TreeList_KeyDown(object sender, KeyEventArgs e)
    {
        if (TreeList.SelectedItem is not DiskRow row) return;
        switch (e.Key)
        {
            case Key.Right: Expand(row); e.Handled = true; break;
            case Key.Left when row.IsExpanded: Collapse(row); e.Handled = true; break;
            case Key.Left when row.Node.Parent != null: Reveal(row.Node.Parent); e.Handled = true; break;
            case Key.Enter: Toggle(row); e.Handled = true; break;
            case Key.Delete when !row.IsMore: RecycleAsync(row.Node); e.Handled = true; break;
        }
    }

    private void TreeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TreeList.SelectedItem is DiskRow { IsMore: false } row) Treemap.Selected = row.Node;
    }

    private void TreeList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not DiskRow { IsMore: false } row) return;
        TreeList.SelectedItem = row;
        ShowMenu(row.Node, TreeList);
        e.Handled = true;
    }
}
