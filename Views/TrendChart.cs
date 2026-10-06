using System.Windows;
using System.Windows.Media;

namespace Kelvra;

/// <summary>
/// Compact line graph for the Overview page: one or two sensors over the History range (Settings → HistoryMinutes).
/// Two-series mode (device panels) shares a fixed 0–100 scale, which suits °C and % alike; single-series mode
/// (sparklines) scales to its own data. Redraws once per history sample while visible.
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty PrimaryIdProperty = DependencyProperty.Register(
        nameof(PrimaryId), typeof(string), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryIdProperty = DependencyProperty.Register(
        nameof(SecondaryId), typeof(string), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(
        nameof(AutoScale), typeof(bool), typeof(TrendChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(TrendChart), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fixed range in seconds (0 = the History range from Settings).</summary>
    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(double), typeof(TrendChart), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Line colour of the main series (null = the accent colour).</summary>
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fill under the main series (null = none).</summary>
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    private readonly List<(DateTime Time, float Value)> _primary = new();
    private readonly List<(DateTime Time, float Value)> _secondary = new();

    public TrendChart()
    {
        Loaded += (_, _) =>
        {
            App.Current.History.Sampled += OnSampled;
            ThemeManager.AccentChanged += InvalidateVisual;
        };
        Unloaded += (_, _) =>
        {
            App.Current.History.Sampled -= OnSampled;
            ThemeManager.AccentChanged -= InvalidateVisual;
        };
        SnapsToDevicePixels = true;
    }

    public string? PrimaryId { get => (string?)GetValue(PrimaryIdProperty); set => SetValue(PrimaryIdProperty, value); }
    public string? SecondaryId { get => (string?)GetValue(SecondaryIdProperty); set => SetValue(SecondaryIdProperty, value); }
    public bool AutoScale { get => (bool)GetValue(AutoScaleProperty); set => SetValue(AutoScaleProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    private void OnSampled()
    {
        if (IsVisible) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w < 10 || h < 6) return;

        var line = TryFindResource("LineBrush") as Brush ?? Brushes.Gray;
        if (ShowGrid)
        {
            var gridPen = new Pen(line, 1);
            gridPen.Freeze();
            for (int i = 1; i <= 3; i++)
            {
                double y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }
        }

        var history = App.Current.History;
        var window = WindowSeconds > 0 ? TimeSpan.FromSeconds(WindowSeconds) : TimeSpan.FromMinutes(App.Current.Settings.HistoryMinutes);
        if (PrimaryId is { } p) history.GetSeries(p, window, _primary); else _primary.Clear();
        if (SecondaryId is { } s) history.GetSeries(s, window, _secondary); else _secondary.Clear();

        double lo = 0, hi = 100;
        if (AutoScale)
        {
            var values = _primary.Select(x => x.Value).Where(v => !float.IsNaN(v)).ToList();
            if (values.Count == 0) return;
            lo = values.Min();
            hi = values.Max();
            double pad = Math.Max((hi - lo) * 0.2, Math.Abs(hi) * 0.02 + 0.5);
            lo -= pad;
            hi += pad;
        }

        var now = DateTime.UtcNow;
        double seconds = window.TotalSeconds;
        Point At((DateTime Time, float Value) pt) =>
            new(w - (now - pt.Time).TotalSeconds / seconds * w, Math.Clamp(h - (pt.Value - lo) / (hi - lo) * h, 1, h - 1));

        Draw(dc, _secondary, TryFindResource("ChartSecondaryBrush") as Brush ?? Brushes.Gray, null, 1.3, At, (int)(w / 2), h);
        Draw(dc, _primary, Stroke ?? TryFindResource("AccentBrush") as Brush ?? Brushes.Orange, Fill, 1.7, At, (int)(w / 2), h);
    }

    private static void Draw(DrawingContext dc, List<(DateTime Time, float Value)> points, Brush brush, Brush? fill, double thickness,
                             Func<(DateTime, float), Point> at, int buckets, double height)
    {
        if (points.Count < 2) return;
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var g = line.Open())
        using (var a = area.Open())
        {
            bool open = false;
            Point last = default;
            foreach (var pt in Thin(points, Math.Max(10, buckets)))
            {
                if (float.IsNaN(pt.Value))
                {
                    if (open) a.LineTo(new Point(last.X, height), false, false);
                    open = false; // a gap in the data is a gap in the line
                    continue;
                }
                var xy = at(pt);
                if (!open)
                {
                    g.BeginFigure(xy, false, false);
                    a.BeginFigure(new Point(xy.X, height), true, true);
                    a.LineTo(xy, false, false);
                }
                else
                {
                    g.LineTo(xy, true, true);
                    a.LineTo(xy, false, false);
                }
                last = xy;
                open = true;
            }
            if (open) a.LineTo(new Point(last.X, height), false, false);
        }
        line.Freeze();
        area.Freeze();
        if (fill != null) dc.DrawGeometry(fill, null, area);
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    /// <summary>Averages runs of points so a 1 h series (3,600 samples) draws about one point per 2 px.</summary>
    private static IEnumerable<(DateTime Time, float Value)> Thin(List<(DateTime Time, float Value)> points, int buckets)
    {
        if (points.Count <= buckets)
        {
            foreach (var p in points) yield return p;
            yield break;
        }
        double per = (double)points.Count / buckets;
        for (int b = 0; b < buckets; b++)
        {
            int from = (int)(b * per), to = Math.Min(points.Count, (int)((b + 1) * per));
            double sum = 0;
            int n = 0;
            for (int i = from; i < to; i++)
            {
                if (float.IsNaN(points[i].Value)) continue;
                sum += points[i].Value;
                n++;
            }
            yield return (points[(from + to - 1) / 2].Time, n > 0 ? (float)(sum / n) : float.NaN);
        }
    }
}
