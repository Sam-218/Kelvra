using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Kelvra;

/// <summary>
/// AIDA64-style summary: Windows, CPU, GPU, RAM sticks, board/BIOS, drives (with health) and network.
/// The cards sit on an invisible snap grid and handle like windows: drag one by its header to any spot – empty ones
/// too – and the cards in its way move down; drag its right / bottom edge or corner to resize it (two cards side by
/// side share their edge, like snapped windows). Edges snap to the grid and to the cards nearby. The grid shows as
/// faint dots while a card is moved or resized. The ⋯ menu does the same without a mouse.
/// </summary>
public partial class SystemPage : UserControl
{
    /// <summary>How close to the top/bottom edge (px) the pointer has to be for the list to scroll while dragging.</summary>
    private const double EdgeZone = 56;

    public static readonly DependencyProperty CanArrangeProperty =
        DependencyProperty.Register(nameof(CanArrange), typeof(bool), typeof(SystemPage), new PropertyMetadata(true));

    /// <summary>The window is wide enough for the grid, so cards can be moved and resized.</summary>
    public bool CanArrange
    {
        get => (bool)GetValue(CanArrangeProperty);
        private set => SetValue(CanArrangeProperty, value);
    }

    internal enum Edge { Right = 1, Bottom = 2, Corner = Right | Bottom }

    private readonly ObservableCollection<InfoSection> _sections = new();
    private readonly Dictionary<InfoSection, CardSpot> _spots = new();
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _loaded;
    private bool _loading;
    private CardGridPanel? _panel;

    // Pressed on a header, not dragging yet
    private InfoSection? _pressed;
    private Point _pressAt;

    // Moving (_dragged) or resizing (_resized) a card; how everything was before, for Esc and for snapping
    private InfoSection? _dragged, _resized;
    private UIElement? _box;
    private Vector _grab;            // pointer − card's top-left corner
    private Edge _edge;
    private Point _resizeFrom;
    private Rect _resizeSlot;
    private List<GridBox> _startBoxes = new();
    private List<double> _edges = new(); // top and bottom edges of the other cards, to snap to
    private Dictionary<InfoSection, CardSpot> _startSpots = new();
    private Window? _keyWindow;      // Esc cancels, wherever the keyboard focus is
    private bool _ending;

    public SystemPage()
    {
        InitializeComponent();
        Sections.ItemsSource = _sections;
        _autoScroll.Tick += (_, _) => AutoScroll();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && !_loaded) await LoadAsync();
        };
    }

    private void Grid_Loaded(object sender, RoutedEventArgs e)
    {
        _panel = (CardGridPanel)sender;
        _panel.SpotOf = box => Sections.ItemContainerGenerator.ItemFromContainer(box) is InfoSection s ? _spots.GetValueOrDefault(s) : null;
        _panel.NarrowChanged += () =>
        {
            CanArrange = !_panel.IsNarrow;
            ArrangeHint.Visibility = _panel.IsNarrow ? Visibility.Visible : Visibility.Collapsed;
            UpdateLayoutButtons();
        };
        _panel.InvalidateMeasure();
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
            // Saved spots by title; two identical drives share a title, so each title can have several, used in turn
            var saved = App.Current.Settings.SystemCardLayout.GroupBy(s => s.Title)
                           .ToDictionary(g => g.Key, g => new Queue<CardSpot>(g.Select(s => s.Copy())));
            _sections.Clear();
            _spots.Clear();
            if (_panel != null) _panel.MinHeight = 0;
            foreach (var s in Ordered(fresh))
            {
                _spots[s] = saved.TryGetValue(s.Title, out var q) && q.Count > 0 ? q.Dequeue() : new CardSpot { Title = s.Title };
                _sections.Add(s);
            }
            Subtitle.Text = $"A summary of your PC's hardware and Windows · collected {DateTime.Now:HH:mm}";
            _loaded = true;
        }
        finally
        {
            _loading = false;
            LoadingText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Saved layout first (or the card order of older versions); cards not in it (new drive, …) follow in their default order.</summary>
    private IEnumerable<InfoSection> Ordered(List<InfoSection> sections)
    {
        var settings = App.Current.Settings;
        var order = settings.SystemCardLayout.Count > 0 ? settings.SystemCardLayout.Select(s => s.Title).ToList() : settings.SystemCardOrder;
        UpdateLayoutButtons(custom: order.Count > 0);
        return sections.Select((s, i) => (s, rank: order.IndexOf(s.Title) is var r and >= 0 ? r : 10_000 + i))
                       .OrderBy(x => x.rank).Select(x => x.s);
    }

    private bool _custom;

    /// <summary>"Reset layout" once the cards were arranged; "Tidy up" too while they can be.</summary>
    private void UpdateLayoutButtons(bool? custom = null)
    {
        _custom = custom ?? _custom;
        ResetOrderButton.Visibility = _custom ? Visibility.Visible : Visibility.Collapsed;
        TidyButton.Visibility = _custom && CanArrange ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveLayout()
    {
        var settings = App.Current.Settings;
        settings.SystemCardLayout = _sections.Select(s =>
        {
            var spot = _spots[s].Copy();
            spot.Title = s.Title;
            return spot;
        }).ToList();
        settings.SystemCardOrder = _sections.Select(s => s.Title).ToList();
        App.Current.MarkDirty();
        UpdateLayoutButtons(custom: true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void ResetOrder_Click(object sender, RoutedEventArgs e)
    {
        App.Current.Settings.SystemCardOrder = new List<string>();
        App.Current.Settings.SystemCardLayout = new List<CardSpot>();
        App.Current.MarkDirty();
        await LoadAsync();
    }

    /// <summary>Closes the gaps: every card moves up as far as it can, keeping its column and width.</summary>
    private void Tidy_Click(object sender, RoutedEventArgs e)
    {
        if (_panel == null || !CanArrange) return;
        Commit();
        Snapshot();
        var tidy = CardGrid.Compact(Boxes());
        for (int i = 0; i < _sections.Count; i++) _spots[_sections[i]].Y = tidy[i].Y;
        Relayout();
        if (Changed()) SaveLayout();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_sections.Count == 0) return;
        Clipboard.SetText(SystemInfo.ToText(_sections));
        Subtitle.Text = "Copied to the clipboard.";
    }

    // ---------- the grid ----------

    private UIElement? BoxOf(InfoSection card) => Sections.ItemContainerGenerator.ContainerFromItem(card) as UIElement;

    /// <summary>
    /// Every card's spot becomes where it's shown: cards placed automatically or pushed down by another stay there now,
    /// so the next change only moves what it has to.
    /// </summary>
    private void Commit()
    {
        if (_panel == null) return;
        foreach (var s in _sections)
        {
            if (BoxOf(s) is not UIElement box) continue;
            var b = _panel.BoxOf(box);
            var spot = _spots[s];
            (spot.X, spot.W, spot.Y) = (b.X, b.W, b.Y);
        }
    }

    /// <summary>A card's spot with its height right now (as it's shown, or as the user set it).</summary>
    private GridBox Box(InfoSection card)
    {
        var spot = _spots[card];
        double h = _panel != null && BoxOf(card) is UIElement box ? _panel.BoxOf(box).H : 0;
        return new GridBox(spot.X, spot.W, spot.Y, h);
    }

    private List<GridBox> Boxes() => _sections.Select(Box).ToList();

    private void Relayout()
    {
        _panel?.InvalidateMeasure();
        _panel?.UpdateLayout();
    }

    private void Snapshot() => _startSpots = _spots.ToDictionary(kv => kv.Key, kv => kv.Value.Copy());

    private bool Changed() =>
        _sections.Any(s => _startSpots.TryGetValue(s, out var a) &&
                           (a.X != _spots[s].X || a.Y != _spots[s].Y || a.W != _spots[s].W || a.H != _spots[s].H));

    private void Restore()
    {
        foreach (var (card, spot) in _startSpots)
        {
            var now = _spots[card];
            (now.X, now.Y, now.W, now.H) = (spot.X, spot.Y, spot.W, spot.H);
        }
        Relayout();
    }

    /// <summary>Top and bottom edges of every card but <paramref name="except"/>, as shown now: what edges snap to.</summary>
    private List<double> EdgesExcept(InfoSection except)
    {
        var edges = new List<double>();
        if (_panel == null) return edges;
        foreach (var s in _sections)
        {
            if (ReferenceEquals(s, except) || BoxOf(s) is not UIElement box) continue;
            var r = _panel.SlotOf(box);
            edges.Add(r.Top);
            edges.Add(r.Bottom);
        }
        return edges;
    }

    // ---------- the ⋯ menu: size and move without dragging (also works by keyboard) ----------

    private void CardMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InfoSection card || _panel == null) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        if (CanArrange)
        {
            Commit();
            var spot = _spots[card];
            foreach (var (name, w) in new[] { ("Quarter width", 3), ("Third", 4), ("Half", 6), ("Full width", 12) })
                menu.Items.Add(MenuItem(name, spot.W == w ? "" : "", true, () => Change(card, () =>
                {
                    spot.W = w;
                    spot.X = CardGrid.ClampX(spot.X, w);
                })));
            int free = CardGrid.FreeRight(Boxes(), _sections.IndexOf(card));
            menu.Items.Add(MenuItem("Fill up to the next card", "", free > spot.X + spot.W, () => Change(card, () => spot.W = free - spot.X)));
            menu.Items.Add(MenuItem("Fit height to the content", "", spot.H != null, () => Change(card, () => spot.H = null)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Move to the top", "", spot.Y > 0, () => Change(card, () => spot.Y = 0)));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(MenuItem("Copy this card", "", true, () =>
        {
            Clipboard.SetText(SystemInfo.ToText(new[] { card }));
            Subtitle.Text = $"Copied “{card.Title}” to the clipboard.";
        }));
        menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string header, string icon, bool enabled, Action action)
    {
        var item = new MenuItem { Header = header, Tag = icon, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>One change from the menu: the card keeps priority, the others make room, and the result is kept.</summary>
    private void Change(InfoSection card, Action change)
    {
        if (_panel == null || BoxOf(card) is not UIElement box) return;
        Snapshot();
        change();
        _panel.Resizing = box; // takes its spot first; cards in the way move down
        Relayout();
        _panel.Resizing = null;
        Commit();
        Relayout();
        if (Changed()) SaveLayout();
    }

    // ---------- moving a card by its header ----------

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanArrange || IsInside<ButtonBase>(e.OriginalSource as DependencyObject)) return; // the ⋯ button
        _pressed = (sender as FrameworkElement)?.DataContext as InfoSection;
        _pressAt = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse(); // a quick flick that leaves the header still starts a drag
    }

    private void Header_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _pressed = null;
        if (((UIElement)sender).IsMouseCaptured) ((UIElement)sender).ReleaseMouseCapture();
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null || _dragged != null || _resized != null || _panel == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressed = null;
            return;
        }
        var delta = e.GetPosition(this) - _pressAt;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        BeginDrag(_pressed, e.GetPosition(_panel));
    }

    /// <param name="pointer">Where the pointer is, in the panel's coordinates.</param>
    internal void BeginDrag(InfoSection card, Point pointer)
    {
        _pressed = null;
        if (_panel == null || _panel.IsNarrow || BoxOf(card) is not UIElement box) return;
        var slot = _panel.SlotOf(box);
        if (slot.IsEmpty) return;

        Commit();
        Snapshot();
        _edges = EdgesExcept(card);
        _dragged = card;
        _box = box;
        _grab = pointer - slot.TopLeft;

        _panel.PlaceholderFill = (Brush)FindResource("AccentSoftBrush");
        _panel.PlaceholderPen = new Pen((Brush)FindResource("AccentBrush"), 1.5) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
        _panel.Floating = box;
        _panel.MoveFloating(slot.TopLeft);
        Lift(box, true);
        StartTracking(Cursors.SizeAll);
    }

    /// <summary>
    /// The card follows the pointer; its spot snaps to the grid – the column under its left edge, the row (or a
    /// neighbour's edge) at its top. Cards in its way move down, and back up once it has passed.
    /// </summary>
    internal void Follow(Point p)
    {
        if (_panel == null || _dragged == null) return;
        _panel.MoveFloating(p - _grab);

        var spot = _spots[_dragged];
        int x = CardGrid.ClampX((int)Math.Round((p.X - _grab.X) / _panel.Unit), spot.W);
        double y = CardGrid.SnapEdge(p.Y - _grab.Y, _edges);
        if (x == spot.X && y == spot.Y) return;

        (spot.X, spot.Y) = (x, y);
        Relayout();
        _panel.MoveFloating(p - _grab); // its slot moved: keep it under the pointer
    }

    // ---------- resizing a card by its edges ----------

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanArrange || _panel == null || (sender as FrameworkElement)?.DataContext is not InfoSection card) return;
        var edge = Enum.Parse<Edge>((string)((FrameworkElement)sender).Tag);
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            Fit(card, edge);
            return;
        }
        BeginResize(card, edge, e.GetPosition(_panel));
    }

    /// <summary>Double-click on an edge: the right edge fills up to the next card (or the page edge), the bottom one fits the content.</summary>
    private void Fit(InfoSection card, Edge edge)
    {
        Commit();
        int free = CardGrid.FreeRight(Boxes(), _sections.IndexOf(card));
        Change(card, () =>
        {
            var spot = _spots[card];
            if (edge.HasFlag(Edge.Right) && free > spot.X + spot.W) spot.W = free - spot.X;
            if (edge.HasFlag(Edge.Bottom)) spot.H = null;
        });
    }

    internal void BeginResize(InfoSection card, Edge edge, Point pointer)
    {
        if (_panel == null || _panel.IsNarrow || BoxOf(card) is not UIElement box) return;
        Commit();
        Snapshot();
        _resized = card;
        _box = box;
        _edge = edge;
        _resizeFrom = pointer;
        _resizeSlot = _panel.SlotOf(box);
        _startBoxes = Boxes();
        _edges = EdgesExcept(card);
        _panel.Resizing = box;
        StartTracking(edge switch { Edge.Right => Cursors.SizeWE, Edge.Bottom => Cursors.SizeNS, _ => Cursors.SizeNWSE });
    }

    /// <summary>
    /// The right edge snaps to whole grid units and takes a card touching it along (they share the edge, like snapped
    /// windows); the bottom edge snaps to the grid rows and to the cards nearby. Cards in the way move down.
    /// </summary>
    internal void ResizeTo(Point p)
    {
        if (_panel == null || _resized == null) return;
        int index = _sections.IndexOf(_resized);
        var spot = _spots[_resized];
        bool changed = false;

        if (_edge.HasFlag(Edge.Right))
        {
            int right = (int)Math.Round((_resizeSlot.Right + p.X - _resizeFrom.X) / _panel.Unit);
            var boxes = CardGrid.ResizeRight(_startBoxes, index, right);
            for (int i = 0; i < boxes.Count; i++)
            {
                var s = _spots[_sections[i]];
                if (s.X == boxes[i].X && s.W == boxes[i].W) continue;
                (s.X, s.W) = (boxes[i].X, boxes[i].W);
                changed = true;
            }
        }
        if (_edge.HasFlag(Edge.Bottom))
        {
            double bottom = CardGrid.SnapEdge(_resizeSlot.Bottom + p.Y - _resizeFrom.Y, _edges);
            double h = Math.Max(CardGrid.MinHeight, bottom - _resizeSlot.Top);
            if (spot.H != h)
            {
                spot.H = h;
                changed = true;
            }
        }
        if (changed) Relayout();
    }

    // ---------- pointer tracking (moving and resizing) ----------

    private void StartTracking(Cursor cursor)
    {
        if (_panel != null)
        {
            var dot = ((Brush)FindResource("MutedBrush")).Clone();
            dot.Opacity = 0.35;
            _panel.GridDotBrush = dot;
            _panel.ShowGrid = true;
        }
        Mouse.OverrideCursor = cursor;
        Scroller.CaptureMouse(); // on the scroller: the cards themselves get moved around underneath
        _keyWindow = Window.GetWindow(this);
        if (_keyWindow != null) _keyWindow.PreviewKeyDown += Window_PreviewKeyDown;
        _autoScroll.Start();
    }

    private void Scroller_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panel == null) return;
        if (_dragged != null) Follow(e.GetPosition(_panel));
        else if (_resized != null) ResizeTo(e.GetPosition(_panel));
    }

    private void Scroller_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragged == null && _resized == null) return;
        e.Handled = true;
        End(cancel: false);
    }

    // Alt-Tab, a dialog, … took the mouse away: keep what's there
    private void Scroller_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // Only when the scroller itself loses it. This event bubbles: the header that held the mouse until the drag
        // started raises it too, the moment the scroller takes over – that once ended every drag right away.
        if (!ReferenceEquals(e.OriginalSource, Scroller)) return;
        if ((_dragged != null || _resized != null) && !_ending) End(cancel: false);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || (_dragged == null && _resized == null)) return;
        e.Handled = true;
        End(cancel: true);
    }

    /// <summary>Scrolls while the pointer is held near the top or bottom edge; faster closer to the edge.</summary>
    private void AutoScroll()
    {
        if ((_dragged == null && _resized == null) || _panel == null) return;
        double y = Mouse.GetPosition(Scroller).Y, height = Scroller.ActualHeight;
        double step = y < EdgeZone ? -(EdgeZone - y) : y > height - EdgeZone ? y - (height - EdgeZone) : 0;
        if (step == 0) return;
        step = Math.Clamp(step / 2.5, -24, 24);
        double before = Scroller.VerticalOffset;
        Scroller.ScrollToVerticalOffset(before + step);
        Scroller.UpdateLayout();
        if (Scroller.VerticalOffset == before) return;
        if (_dragged != null) Follow(Mouse.GetPosition(_panel));
        else ResizeTo(Mouse.GetPosition(_panel));
    }

    internal void End(bool cancel)
    {
        if (_panel == null || (_dragged == null && _resized == null)) return;
        _ending = true;
        try
        {
            _autoScroll.Stop();
            Mouse.OverrideCursor = null;
            if (_keyWindow != null) _keyWindow.PreviewKeyDown -= Window_PreviewKeyDown;
            _keyWindow = null;
            if (Scroller.IsMouseCaptured) Scroller.ReleaseMouseCapture();

            if (cancel) Restore();
            else
            {
                // Cards that made room stay where they went
                Commit();
                if (_dragged != null)
                {
                    // Dropped one unit short of the card beside it or the page edge: snap to it, like a window
                    int index = _sections.IndexOf(_dragged);
                    var filled = CardGrid.FillGap(Boxes(), index);
                    var spot = _spots[_dragged];
                    if (filled.X != spot.X || filled.W != spot.W)
                    {
                        (spot.X, spot.W) = (filled.X, filled.W);
                        Relayout();
                        Commit();
                    }
                }
            }

            var box = _box!;
            if (_dragged != null)
            {
                Lift(box, false);
                _panel.Land(() => box.ClearValue(EffectProperty));
            }
            _panel.Resizing = null;
            // The room below the cards goes away with the grid; keep what's on screen, or the page would jump up
            _panel.MinHeight = Scroller.VerticalOffset + Scroller.ViewportHeight;
            _panel.ShowGrid = false;
            if (!cancel && Changed()) SaveLayout();
        }
        finally
        {
            _dragged = _resized = null;
            _box = null;
            _ending = false;
        }
    }

    /// <summary>A dragged card is "picked up": a little larger, with a shadow under it.</summary>
    private static void Lift(UIElement box, bool up)
    {
        var scale = CardGridPanel.Scale(box);
        var anim = new DoubleAnimation(up ? 1.02 : 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        if (up) box.Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.35, Color = Colors.Black };
    }

    /// <summary>
    /// The wheel over a card scrolls the card if it was made shorter than its content and has room to scroll that way;
    /// otherwise the page (a scroll area inside the page would swallow it).
    /// </summary>
    private void CardBody_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var inner = (ScrollViewer)sender;
        e.Handled = true;
        bool canScroll = e.Delta < 0 ? inner.VerticalOffset < inner.ScrollableHeight - 0.5 : inner.VerticalOffset > 0.5;
        if (canScroll)
        {
            inner.ScrollToVerticalOffset(inner.VerticalOffset - e.Delta * 0.4);
            return;
        }
        Scroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }

    private static bool IsInside<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
