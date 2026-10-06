using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Kelvra;

/// <summary>
/// Squarified treemap of a <see cref="DiskNode"/> tree (WizTree style): every file is a block sized by
/// its bytes and coloured by file type, nested inside its folders. Folders get a title bar with their
/// name and size, big files are labelled, and double-clicking zooms into a folder.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private const double MinArea = 2.0;   // px² – smaller blocks aren't drawn
    private const double HeaderGap = 1.0; // px between a folder's border and its contents
    private const double TitleHeight = 17; // folder title bar
    private const double LabelFont = 11;

    private readonly List<(Rect Rect, DiskNode Node)> _hits = new();
    private readonly Dictionary<DiskNode, Rect> _rects = new(ReferenceEqualityComparer.Instance);
    private readonly MapLayer _layer = new();        // the map, kept as a cached bitmap (see MapLayer)
    private readonly DrawingVisual _overlay = new(); // hover + selection outlines, cheap to redraw
    private DrawingVisual Map => _layer.Drawing;
    private readonly Dictionary<Color, Brush> _cushions = new();
    private readonly Dictionary<Color, Brush> _dimmed = new();
    private static readonly Pen Outline = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 0.6));
    private static readonly Brush FolderFill = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x38)));
    private static readonly Pen HoverPen = Freeze(new Pen(Brushes.White, 1.5));
    private static readonly Brush TitleFill = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x41, 0x50)));
    private static readonly Pen FolderEdge = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x4A, 0x51, 0x63)), 1));
    private static readonly Brush TitleText = Freeze(new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF5)));
    private static readonly Brush TitleSizeText = Freeze(new SolidColorBrush(Color.FromRgb(0xA4, 0xAC, 0xBB)));
    private static readonly Brush DarkText = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x12, 0x16)));
    private static readonly Brush LightText = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)));
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private double _pixelsPerDip = 1;
    private DiskNode? _viewRoot;

    private DiskNode? _root;
    private DiskNode? _selected;
    private DiskNode? _hover;
    private string? _highlightExtension;

    public event Action<DiskNode>? NodeClicked;
    public event Action<DiskNode, Point>? NodeRightClicked;
    public event Action<DiskNode?>? HoverChanged;
    /// <summary>Raised when the map zooms in or out (argument = folder now filling the map).</summary>
    public event Action<DiskNode?>? ViewRootChanged;

    public TreemapControl()
    {
        ClipToBounds = true;
        Focusable = false;
        Cursor = Cursors.Hand;
        AddVisualChild(_layer);
        AddVisualChild(_overlay);
    }

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? _layer : _overlay;

    protected override Size MeasureOverride(Size availableSize)
    {
        _layer.Measure(availableSize);
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _layer.Arrange(new Rect(finalSize));
        return finalSize;
    }

    /// <summary>
    /// Holds the map drawing with a <see cref="BitmapCache"/>. A big drive draws 100,000+ blocks, and without the cache
    /// WPF replays all of them (about half a second at full-screen size) every time the hover outline moves — that was the
    /// laggy, block-skipping hover. Cached, a hover change only redraws the outline; the map is re-rasterized only when
    /// it actually changes (scan, zoom, highlight, resize). Not hit-testable: the treemap control handles the mouse.
    /// </summary>
    private sealed class MapLayer : FrameworkElement
    {
        public MapLayer()
        {
            IsHitTestVisible = false;
            AddVisualChild(Drawing);
            CacheMode = new BitmapCache { SnapsToDevicePixels = true };
        }

        public DrawingVisual Drawing { get; } = new();

        /// <summary>Renders the cache at the screen's real pixel density, so labels stay sharp at 125–200 % scaling.</summary>
        public void MatchDpi(double scale)
        {
            if (CacheMode is BitmapCache cache && Math.Abs(cache.RenderAtScale - scale) > 0.01)
                CacheMode = new BitmapCache { SnapsToDevicePixels = true, RenderAtScale = scale };
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => Drawing;
    }

    public DiskNode? Root
    {
        get => _root;
        set
        {
            _root = value;
            _viewRoot = null;
            _hover = null;
            RenderMap();
            ViewRootChanged?.Invoke(value);
        }
    }

    /// <summary>The folder currently filling the map (zoom level). Defaults to <see cref="Root"/>.</summary>
    public DiskNode? ViewRoot
    {
        get => _viewRoot ?? _root;
        set
        {
            if (value == null || !value.IsDirectory) return;
            _viewRoot = ReferenceEquals(value, _root) ? null : value;
            _hover = null;
            RenderMap();
            ViewRootChanged?.Invoke(ViewRoot);
        }
    }

    public DiskNode? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            RenderOverlay();
        }
    }

    /// <summary>When set, only files with this extension are drawn in full colour.</summary>
    public string? HighlightExtension
    {
        get => _highlightExtension;
        set
        {
            _highlightExtension = value;
            RenderMap();
        }
    }

    // ---------- rendering ----------

    /// <summary>Redraws after the tree changed in place (e.g. something was deleted).</summary>
    public void Redraw() => RenderMap();

    protected override void OnRender(DrawingContext dc) =>
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit-testable surface

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RenderMap();
    }

    /// <summary>Moved to a monitor with other scaling: redraw so the cached map is sharp there.</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RenderMap();
    }

    private void RenderMap()
    {
        _hits.Clear();
        _rects.Clear();
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _layer.MatchDpi(_pixelsPerDip);
        using (var dc = Map.RenderOpen())
        {
            var bounds = new Rect(RenderSize);
            dc.DrawRectangle(FolderFill, null, bounds);
            if (ViewRoot is { Size: > 0 } view) Draw(dc, view, bounds, 0);
        }
        RenderOverlay();
    }

    private void RenderOverlay()
    {
        using var dc = _overlay.RenderOpen();
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DeepSkyBlue;
        if (_selected != null && FindRect(_selected) is Rect sel)
            dc.DrawRectangle(null, new Pen(accent, 2.5), Inset(sel, 1));
        if (_hover != null && FindRect(_hover) is Rect hov)
            dc.DrawRectangle(null, HoverPen, Inset(hov, 0.75));
    }

    private void Draw(DrawingContext dc, DiskNode node, Rect rect, int depth)
    {
        if (rect.Width < 0.5 || rect.Height < 0.5) return;
        _hits.Add((rect, node));
        _rects[node] = rect;

        if (!node.IsDirectory)
        {
            var fill = BrushFor(node);
            dc.DrawRectangle(fill, rect.Width > 5 && rect.Height > 5 ? Outline : null, rect);
            DrawFileLabel(dc, node, rect);
            return;
        }

        dc.DrawRectangle(FolderFill, depth > 0 && rect.Width > 12 && rect.Height > 12 ? FolderEdge : null, rect);
        if (node.Children is not { Count: > 0 } || node.Size <= 0) return;

        Rect inner;
        if (depth > 0 && rect.Width >= 48 && rect.Height >= TitleHeight + 20)
        {
            // Title bar: folder name on the left, size on the right
            var title = new Rect(rect.X, rect.Y, rect.Width, TitleHeight);
            dc.DrawRectangle(TitleFill, null, title);
            var size = Text(ByteFormat.Format(node.Size), TitleSizeText, Face, rect.Width);
            bool roomForSize = rect.Width > size.Width + 60;
            if (roomForSize) dc.DrawText(size, new Point(rect.Right - size.Width - 5, rect.Y + 1.5));
            var name = Text(node.Name, TitleText, BoldFace, rect.Width - 10 - (roomForSize ? size.Width + 8 : 0));
            dc.DrawText(name, new Point(rect.X + 5, rect.Y + 1.5));
            inner = new Rect(rect.X + 2, rect.Y + TitleHeight, rect.Width - 4, rect.Height - TitleHeight - 2);
        }
        else
        {
            inner = depth > 0 && rect.Width > 8 && rect.Height > 8 ? Inset(rect, HeaderGap) : rect;
        }
        double scale = inner.Width * inner.Height / node.Size;

        // Children are sorted largest-first, so everything after the first too-small one is too small as well
        int count = 0;
        while (count < node.Children.Count && node.Children[count].Size * scale >= MinArea) count++;
        if (count == 0) return;

        foreach (var (child, childRect) in Squarify(node.Children, count, inner, scale))
            Draw(dc, child, childRect, depth + 1);
    }

    /// <summary>Squarified layout (Bruls, Huizing &amp; van Wijk) of the first <paramref name="count"/> items.</summary>
    private static List<(DiskNode, Rect)> Squarify(List<DiskNode> items, int count, Rect rect, double scale)
    {
        var result = new List<(DiskNode, Rect)>(count);
        int i = 0;
        var r = rect;
        while (i < count && r.Width > 0 && r.Height > 0)
        {
            double side = Math.Min(r.Width, r.Height);
            double rowSum = 0, worst = double.MaxValue;
            int j = i;
            while (j < count)
            {
                double area = items[j].Size * scale;
                double sum = rowSum + area;
                double max = items[i].Size * scale; // sorted, so the first is the biggest
                double ratio = Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * area));
                if (j > i && ratio > worst) break;
                rowSum = sum;
                worst = ratio;
                j++;
            }

            if (r.Width >= r.Height)
            {
                double w = rowSum / r.Height, y = r.Y;
                for (int k = i; k < j; k++)
                {
                    double h = items[k].Size * scale / w;
                    result.Add((items[k], new Rect(r.X, y, w, h)));
                    y += h;
                }
                r = new Rect(r.X + w, r.Y, Math.Max(0, r.Width - w), r.Height);
            }
            else
            {
                double h = rowSum / r.Width, x = r.X;
                for (int k = i; k < j; k++)
                {
                    double w = items[k].Size * scale / h;
                    result.Add((items[k], new Rect(x, r.Y, w, h)));
                    x += w;
                }
                r = new Rect(r.X, r.Y + h, r.Width, Math.Max(0, r.Height - h));
            }
            i = j;
        }
        return result;
    }

    private void DrawFileLabel(DrawingContext dc, DiskNode file, Rect rect)
    {
        if (rect.Width < 56 || rect.Height < 22) return;
        bool dim = _highlightExtension != null && file.Extension != _highlightExtension;
        var color = FileKinds.Of(file.Extension).Color;
        double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
        var brush = dim ? TitleSizeText : luminance > 0.62 ? DarkText : LightText;

        var name = Text(file.Name, brush, BoldFace, rect.Width - 8);
        dc.DrawText(name, new Point(rect.X + 4, rect.Y + 3));
        if (rect.Height >= 38)
        {
            var size = Text(ByteFormat.Format(file.Size), brush, Face, rect.Width - 8);
            dc.DrawText(size, new Point(rect.X + 4, rect.Y + 3 + name.Height));
        }
    }

    private FormattedText Text(string text, Brush brush, Typeface face, double maxWidth) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, LabelFont, brush, _pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    private Brush BrushFor(DiskNode file)
    {
        var color = FileKinds.Of(file.Extension).Color;
        bool dim = _highlightExtension != null && file.Extension != _highlightExtension;
        var cache = dim ? _dimmed : _cushions;
        if (cache.TryGetValue(color, out var brush)) return brush;

        // "Cushion" look: lighter top-left, darker bottom-right
        var baseColor = dim ? Blend(color, Color.FromRgb(0x2A, 0x2E, 0x38), 0.8) : color;
        brush = Freeze(new LinearGradientBrush(Blend(baseColor, Colors.White, 0.25), Blend(baseColor, Colors.Black, 0.3), 45));
        cache[color] = brush;
        return brush;
    }

    // ---------- mouse ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var node = HitTest(e.GetPosition(this));
        if (node == _hover) return;
        _hover = node;
        HoverChanged?.Invoke(node);
        RenderOverlay();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        HoverChanged?.Invoke(null);
        RenderOverlay();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount != 2 || HitTest(e.GetPosition(this)) is not { } node) return;
        // Double-click zooms into the folder (or the folder containing a file)
        var folder = node.IsDirectory ? node : node.Parent;
        if (folder != null && !ReferenceEquals(folder, ViewRoot)) ViewRoot = folder;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (HitTest(e.GetPosition(this)) is { } node) NodeClicked?.Invoke(node);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (HitTest(e.GetPosition(this)) is { } node)
        {
            NodeRightClicked?.Invoke(node, e.GetPosition(this));
            e.Handled = true;
        }
    }

    /// <summary>Deepest block under the point (children are recorded after their parents).</summary>
    private DiskNode? HitTest(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Rect.Contains(p)) return _hits[i].Node;
        return null;
    }

    /// <summary>Where a node was drawn; falls back to its nearest drawn ancestor.</summary>
    private Rect? FindRect(DiskNode node)
    {
        for (var n = node; n != null; n = n.Parent)
            if (_rects.TryGetValue(n, out var rect)) return rect;
        return null;
    }

    // ---------- helpers ----------

    private static Rect Inset(Rect r, double by) =>
        r.Width > by * 2 && r.Height > by * 2 ? new Rect(r.X + by, r.Y + by, r.Width - by * 2, r.Height - by * 2) : r;

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
