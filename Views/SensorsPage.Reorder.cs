using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Kelvra;

/// <summary>Letting the user arrange device groups: drag the ≡ handle, or use the ▲ ▼ buttons on a group header.</summary>
public partial class SensorsPage
{
    private const string DragFormat = "Kelvra.DeviceGroup";

    private int _rankedCount = -1;
    private Point _dragStart;
    private string? _dragGroupId;
    private InsertionAdorner? _adorner;

    /// <summary>Applies the saved device order to every sensor and re-sorts the list.</summary>
    private void ApplyGroupOrder()
    {
        var order = _app.Settings.GroupOrder;
        foreach (var s in _app.Sensors.All)
        {
            int i = order.IndexOf(s.HardwareId);
            s.GroupRank = i >= 0 ? i : 10_000 + s.HardwareOrder; // unlisted devices keep detection order, after listed ones
        }
        _rankedCount = _app.Sensors.All.Count;
        _view.Refresh();
        ResetOrderButton.Visibility = order.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>All devices (hardware IDs) in their current display order.</summary>
    private List<string> CurrentOrder() =>
        _app.Sensors.All.GroupBy(s => s.HardwareId).Select(g => g.First())
            .OrderBy(s => s.GroupRank).Select(s => s.HardwareId).ToList();

    /// <summary>Devices currently shown (respects the active filter).</summary>
    private List<string> VisibleOrder() =>
        _view.Groups?.OfType<CollectionViewGroup>().Select(GroupIdOf).OfType<string>().ToList() ?? new();

    private static string? GroupIdOf(object? context) =>
        context is CollectionViewGroup { ItemCount: > 0 } g && g.Items[0] is SensorVm s ? s.HardwareId : null;

    private void MoveGroup(string id, string targetId, bool after)
    {
        if (id == targetId) return;
        var order = CurrentOrder();
        order.Remove(id);
        int index = order.IndexOf(targetId);
        if (index < 0) return;
        order.Insert(after ? index + 1 : index, id);

        _app.Settings.GroupOrder = order;
        _app.MarkDirty();
        ApplyGroupOrder();
    }

    // ---------- buttons ----------

    private void GroupUp_Click(object sender, RoutedEventArgs e) => Nudge(sender, -1);
    private void GroupDown_Click(object sender, RoutedEventArgs e) => Nudge(sender, +1);

    private void Nudge(object sender, int delta)
    {
        if (GroupIdOf((sender as FrameworkElement)?.DataContext) is not string id) return;
        var visible = VisibleOrder();
        int i = visible.IndexOf(id);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= visible.Count) return;
        MoveGroup(id, visible[j], after: delta > 0);
    }

    private void ResetOrder_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.GroupOrder = new List<string>();
        _app.MarkDirty();
        ApplyGroupOrder();
    }

    // ---------- drag source (the ≡ handle) ----------

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragGroupId = GroupIdOf((sender as FrameworkElement)?.DataContext);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true; // don't fold/unfold the group
    }

    private void Grip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragGroupId = null;
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragGroupId == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance &&
            Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance) return;

        string id = _dragGroupId;
        _dragGroupId = null;
        ((UIElement)sender).ReleaseMouseCapture();
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DragFormat, id), DragDropEffects.Move);
        }
        finally
        {
            RemoveAdorner();
        }
    }

    // ---------- drop target (the list) ----------

    private void SensorList_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DragFormat))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        AutoScroll(e);

        var target = FindAncestor<GroupItem>(e.OriginalSource as DependencyObject);
        if (target == null)
        {
            RemoveAdorner();
            return;
        }
        ShowAdorner(target, DropsAfter(target, e));
    }

    private void SensorList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        RemoveAdorner();
        if (e.Data.GetData(DragFormat) is not string id) return;

        var target = FindAncestor<GroupItem>(e.OriginalSource as DependencyObject);
        if (target != null && GroupIdOf(target.DataContext) is string targetId)
        {
            MoveGroup(id, targetId, DropsAfter(target, e));
        }
        else if (VisibleOrder() is { Count: > 0 } visible)
        {
            MoveGroup(id, visible[^1], after: true); // dropped below the last group
        }
    }

    private void SensorList_DragLeave(object sender, DragEventArgs e) => RemoveAdorner();

    /// <summary>Upper half of a group's header = insert before it; anything lower = insert after it.</summary>
    private static bool DropsAfter(GroupItem target, DragEventArgs e)
    {
        var header = FindChildren<ToggleButton>(target).FirstOrDefault();
        double headerMid = header != null
            ? header.TranslatePoint(new Point(0, header.ActualHeight / 2), target).Y
            : 20;
        return e.GetPosition(target).Y > headerMid;
    }

    private void AutoScroll(DragEventArgs e)
    {
        var scroller = FindChildren<ScrollViewer>(SensorList).FirstOrDefault();
        if (scroller == null) return;
        double y = e.GetPosition(SensorList).Y;
        if (y < 40) scroller.ScrollToVerticalOffset(scroller.VerticalOffset - 12);
        else if (y > SensorList.ActualHeight - 40) scroller.ScrollToVerticalOffset(scroller.VerticalOffset + 12);
    }

    private void ShowAdorner(GroupItem target, bool after)
    {
        if (_adorner != null && ReferenceEquals(_adorner.AdornedElement, target) && _adorner.After == after) return;
        RemoveAdorner();
        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer == null) return;
        _adorner = new InsertionAdorner(target, after, (Brush)FindResource("AccentBrush"));
        layer.Add(_adorner);
    }

    private void RemoveAdorner()
    {
        if (_adorner == null) return;
        AdornerLayer.GetAdornerLayer(_adorner.AdornedElement)?.Remove(_adorner);
        _adorner = null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null && node is not T)
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }

    /// <summary>Accent line showing where a dragged group will land.</summary>
    private sealed class InsertionAdorner : Adorner
    {
        private readonly Pen _pen;
        private readonly Brush _brush;

        public InsertionAdorner(UIElement adorned, bool after, Brush brush) : base(adorned)
        {
            After = after;
            _brush = brush;
            _pen = new Pen(brush, 2.5);
            IsHitTestVisible = false;
        }

        public bool After { get; }

        protected override void OnRender(DrawingContext dc)
        {
            var size = AdornedElement.RenderSize;
            double y = After ? size.Height - 1 : 5;
            dc.DrawLine(_pen, new Point(8, y), new Point(size.Width - 8, y));
            dc.DrawEllipse(_brush, null, new Point(8, y), 4, 4);
        }
    }
}
