using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Kelvra;

/// <summary>
/// The System page's snap grid: <see cref="CardGrid.Units"/> units across the width and a row every
/// <see cref="CardGrid.Row"/> px, each card at the spot and size <see cref="SpotOf"/> gives it (see <see cref="CardGrid"/>
/// for the rules); cards that would cover another push it down. Cards without a spot fill equal columns, each into the
/// shortest. Below <see cref="NarrowBelow"/> px there's no room for a grid: plain equal columns then.
/// When the layout changes, cards glide from their old spot to the new one. A dragged card (<see cref="Floating"/>)
/// follows the pointer above the others with a placeholder where it will land; a card being resized
/// (<see cref="Resizing"/>) follows the pointer directly. Both keep their spot; the others make room.
/// While <see cref="ShowGrid"/> is on, the grid shows as faint dots and there's room below the cards to drop one there.
/// </summary>
public sealed class CardGridPanel : Panel
{
    private static readonly Duration Glide = TimeSpan.FromMilliseconds(260);
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    private readonly Dictionary<UIElement, Rect> _slots = new();
    private readonly Dictionary<UIElement, GridBox> _boxes = new();
    private double _arrangedWidth = double.NaN;
    private UIElement? _floating;
    private Point _floatingAt;
    private int _landing; // bumped by every new drag, so a landing that's still gliding doesn't end the new one

    // The placeholder glides too: drawn at _placeholderFrom → _placeholderTo over Glide
    private Rect _placeholderFrom, _placeholderTo;
    private DateTime _placeholderStart;
    private bool _placeholderTicking;

    /// <summary>Narrower than this (px), cards go into plain equal columns and can't be arranged.</summary>
    public double NarrowBelow { get; set; } = 1000;

    /// <summary>Column width (at least) in the narrow layout.</summary>
    public double NarrowColumnWidth { get; set; } = 380;

    /// <summary>Space around each card inside its slot (the card's own margin), so the placeholder matches the card.</summary>
    public Thickness PlaceholderInset { get; set; }

    public Brush? PlaceholderFill { get; set; }
    public Pen? PlaceholderPen { get; set; }

    /// <summary>A card's spot on the grid; null or X &lt; 0 = not placed yet.</summary>
    public Func<UIElement, CardSpot?>? SpotOf { get; set; }

    /// <summary>The narrow layout started or ended (arranging is only possible in the wide one).</summary>
    public event Action? NarrowChanged;

    public bool IsNarrow { get; private set; }

    /// <summary>Width of one grid unit in px (the page width ÷ <see cref="CardGrid.Units"/>).</summary>
    public double Unit => double.IsNaN(_arrangedWidth) ? 0 : _arrangedWidth / CardGrid.Units;

    /// <summary>The card being resized: it takes its new size at once instead of gliding.</summary>
    public UIElement? Resizing { get; set; }

    /// <summary>Space (px) below the lowest card while <see cref="ShowGrid"/> is on, to drop a card there.</summary>
    public double RoomBelow { get; set; } = 480;

    public Brush? GridDotBrush { get; set; }

    /// <summary>Shows the grid (while a card is moved or resized).</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set
        {
            if (_showGrid == value) return;
            _showGrid = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private bool _showGrid;

    /// <summary>The card being dragged: not animated by the layout, drawn on top, its slot shown as a placeholder.</summary>
    public UIElement? Floating
    {
        get => _floating;
        set
        {
            if (ReferenceEquals(_floating, value)) return;
            if (_floating != null) SetZIndex(_floating, 0);
            _floating = value;
            _landing++;
            if (value != null)
            {
                SetZIndex(value, 1);
                if (_slots.TryGetValue(value, out var slot)) _placeholderFrom = _placeholderTo = Inset(slot);
            }
            InvalidateVisual();
        }
    }

    /// <summary>Where a child was last placed (without any glide or drag offset).</summary>
    public Rect SlotOf(UIElement child) => _slots.TryGetValue(child, out var r) ? r : Rect.Empty;

    /// <summary>Where a child sits on the grid now (also for cards that were placed automatically or pushed down).</summary>
    internal GridBox BoxOf(UIElement child) => _boxes.TryGetValue(child, out var b) ? b : new GridBox(0, CardGrid.DefaultWidth, 0, 0);

    /// <summary>Where the dragged card's top-left corner should be, in this panel's coordinates.</summary>
    public void MoveFloating(Point topLeft)
    {
        _floatingAt = topLeft;
        if (_floating != null && _slots.TryGetValue(_floating, out var slot))
        {
            var t = Translate(_floating);
            t.BeginAnimation(TranslateTransform.XProperty, null);
            t.BeginAnimation(TranslateTransform.YProperty, null);
            t.X = topLeft.X - slot.X;
            t.Y = topLeft.Y - slot.Y;
        }
    }

    /// <summary>Lets the dragged card glide from where it was dropped into its slot, then lands it.</summary>
    /// <param name="done">Runs when it has landed (not if another drag picked a card up in the meantime).</param>
    public void Land(Action? done = null)
    {
        var card = _floating;
        if (card == null)
        {
            done?.Invoke();
            return;
        }
        int landing = _landing;
        var t = Translate(card);
        var x = new DoubleAnimation(t.X, 0, Glide) { EasingFunction = Ease };
        x.Completed += (_, _) =>
        {
            if (landing != _landing) return;
            Floating = null;
            done?.Invoke();
        };
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(t.Y, 0, Glide) { EasingFunction = Ease });
        t.BeginAnimation(TranslateTransform.XProperty, x);
    }

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 1200 : available.Width;
        var rects = Place(width, measure: true);
        double height = rects.Count == 0 ? 0 : rects.Values.Max(r => r.Bottom);
        return new Size(width, height + (ShowGrid && !IsNarrow ? RoomBelow : 0));
    }

    protected override Size ArrangeOverride(Size final)
    {
        // Only a new layout glides; a new window width just re-flows (gliding every card on each resize step would lag)
        bool glide = !double.IsNaN(_arrangedWidth) && Math.Abs(_arrangedWidth - final.Width) < 0.5;
        bool narrow = final.Width < NarrowBelow;
        _arrangedWidth = final.Width;
        var rects = Place(final.Width, measure: false);

        foreach (UIElement c in InternalChildren)
        {
            var slot = rects[c];
            c.Arrange(slot);
            var had = _slots.TryGetValue(c, out var old);
            _slots[c] = slot;
            var t = Translate(c);

            if (ReferenceEquals(c, _floating))
            {
                // Keep it under the pointer, wherever its slot went
                t.X = _floatingAt.X - slot.X;
                t.Y = _floatingAt.Y - slot.Y;
                if (Inset(slot) != _placeholderTo) StartPlaceholderGlide(Inset(slot));
                continue;
            }
            if (!glide || !had || ReferenceEquals(c, Resizing))
            {
                t.BeginAnimation(TranslateTransform.XProperty, null);
                t.BeginAnimation(TranslateTransform.YProperty, null);
                t.X = t.Y = 0;
                continue;
            }
            if (old.TopLeft == slot.TopLeft) continue;
            // From where it's drawn right now (it may still be gliding) to its new slot
            double fromX = old.X + t.X - slot.X, fromY = old.Y + t.Y - slot.Y;
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, Glide) { EasingFunction = Ease });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, Glide) { EasingFunction = Ease });
        }
        foreach (var gone in _slots.Keys.Where(k => !rects.ContainsKey(k)).ToList())
        {
            _slots.Remove(gone);
            _boxes.Remove(gone);
        }

        if (narrow != IsNarrow)
        {
            IsNarrow = narrow;
            NarrowChanged?.Invoke();
        }
        return final;
    }

    /// <summary>Every child's slot for this width (measuring them first in the measure pass).</summary>
    private Dictionary<UIElement, Rect> Place(double width, bool measure)
    {
        var rects = new Dictionary<UIElement, Rect>();
        if (width < NarrowBelow)
        {
            int cols = Math.Max(1, (int)(width / NarrowColumnWidth));
            double colWidth = width / cols;
            var heights = new double[cols];
            foreach (UIElement c in InternalChildren)
            {
                if (measure) c.Measure(new Size(colWidth, double.PositiveInfinity));
                int i = Array.IndexOf(heights, heights.Min());
                rects[c] = new Rect(i * colWidth, heights[i], colWidth, c.DesiredSize.Height);
                heights[i] += c.DesiredSize.Height;
            }
            return rects;
        }

        double unit = width / CardGrid.Units;
        int autoWidth = CardGrid.DefaultWidthFor(unit);
        var kids = InternalChildren.Cast<UIElement>().ToList();
        var spots = kids.Select(c => SpotOf?.Invoke(c)).ToList();
        var boxes = new GridBox[kids.Count];
        var sky = new double[CardGrid.Units];

        // Size first: a card's height depends on its width
        for (int i = 0; i < kids.Count; i++)
        {
            var spot = spots[i];
            bool placed = spot is { X: >= 0 };
            int w = placed ? CardGrid.ClampW(spot!.W) : autoWidth;
            double? fixedHeight = placed && spot!.H is double h0 ? Math.Max(CardGrid.MinHeight, h0) : null;
            if (measure) kids[i].Measure(new Size(w * unit, fixedHeight ?? double.PositiveInfinity));
            double h = fixedHeight ?? kids[i].DesiredSize.Height;
            int x = placed ? CardGrid.ClampX(spot!.X, w) : 0;
            boxes[i] = new GridBox(x, w, placed && spot!.Y >= 0 ? spot.Y : -1, h);
            if (boxes[i].Y < 0) continue;
            for (int u = boxes[i].X; u < boxes[i].Right; u++) sky[u] = Math.Max(sky[u], boxes[i].Bottom);
        }
        // Then the cards without a top (not placed yet, or from an older layout): under the others, in order
        for (int i = 0; i < kids.Count; i++)
        {
            if (boxes[i].Y >= 0) continue;
            var b = boxes[i];
            int x = spots[i] is { X: >= 0 } ? b.X : CardGrid.ShortestColumn(sky, b.W);
            double top = 0;
            for (int u = x; u < x + b.W; u++) top = Math.Max(top, sky[u]);
            boxes[i] = b with { X = x, Y = top };
            for (int u = x; u < x + b.W; u++) sky[u] = top + b.H;
        }

        // Nothing overlaps: the card being moved or resized keeps its spot, the ones in its way move down
        var active = _floating ?? Resizing;
        var resolved = CardGrid.Resolve(boxes, active != null ? kids.IndexOf(active) : -1);
        for (int i = 0; i < kids.Count; i++)
        {
            var b = resolved[i];
            rects[kids[i]] = new Rect(b.X * unit, b.Y, b.W * unit, b.H);
            _boxes[kids[i]] = b;
        }
        return rects;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ShowGrid && !IsNarrow && GridDotBrush != null && !double.IsNaN(_arrangedWidth))
        {
            // A dot where each column starts on every second row: the spots a card's top-left corner snaps to
            double unit = Unit;
            for (double y = 0; y <= ActualHeight; y += CardGrid.Row * 2)
                for (int x = 0; x < CardGrid.Units; x++)
                    dc.DrawRectangle(GridDotBrush, null, new Rect(x * unit - 1, y - 1, 2.5, 2.5));
        }
        if (_floating == null || PlaceholderPen == null) return;
        dc.DrawRoundedRectangle(PlaceholderFill, PlaceholderPen, PlaceholderNow(), 6, 6);
    }

    private Rect PlaceholderNow()
    {
        double p = _placeholderTicking ? Ease.Ease(Math.Clamp((DateTime.UtcNow - _placeholderStart).TotalMilliseconds / Glide.TimeSpan.TotalMilliseconds, 0, 1)) : 1;
        return new Rect(
            _placeholderFrom.X + (_placeholderTo.X - _placeholderFrom.X) * p,
            _placeholderFrom.Y + (_placeholderTo.Y - _placeholderFrom.Y) * p,
            _placeholderFrom.Width + (_placeholderTo.Width - _placeholderFrom.Width) * p,
            _placeholderFrom.Height + (_placeholderTo.Height - _placeholderFrom.Height) * p);
    }

    private void StartPlaceholderGlide(Rect to)
    {
        _placeholderFrom = PlaceholderNow(); // from wherever it's drawn now
        _placeholderTo = to;
        _placeholderStart = DateTime.UtcNow;
        if (_placeholderTicking) return;
        _placeholderTicking = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        InvalidateVisual();
        if ((DateTime.UtcNow - _placeholderStart) < Glide.TimeSpan && _floating != null) return;
        _placeholderTicking = false;
        CompositionTarget.Rendering -= OnFrame;
        InvalidateVisual();
    }

    private Rect Inset(Rect slot)
    {
        var i = PlaceholderInset;
        return new Rect(slot.X + i.Left, slot.Y + i.Top,
                        Math.Max(0, slot.Width - i.Left - i.Right), Math.Max(0, slot.Height - i.Top - i.Bottom));
    }

    /// <summary>The child's own move transform (scale for the "lifted" look, then the glide/drag offset).</summary>
    internal static TranslateTransform Translate(UIElement c) => (TranslateTransform)Transforms(c).Children[1];

    internal static ScaleTransform Scale(UIElement c) => (ScaleTransform)Transforms(c).Children[0];

    private static TransformGroup Transforms(UIElement c)
    {
        if (c.RenderTransform is TransformGroup { Children.Count: 2 } g && g.Children[0] is ScaleTransform && g.Children[1] is TranslateTransform)
            return g;
        var group = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } };
        c.RenderTransform = group;
        c.RenderTransformOrigin = new Point(0.5, 0.5);
        return group;
    }
}
