using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>
/// "Thermal headroom": the thermal scale from 20 to 100 °C, dimmed past the current temperature, with a box around the
/// range of the shown period and a marker at now. Under it the scale in steps of 20°. Bind <see cref="Panel"/>.
/// </summary>
public sealed class HeatBar : FrameworkElement
{
    public static readonly DependencyProperty PanelProperty = DependencyProperty.Register(nameof(Panel), typeof(DevicePanel), typeof(HeatBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((HeatBar)d).Watch(e)));

    private const double From = 20, To = 100, BarHeight = 8, Top = 6;
    private static readonly Typeface Face = new("Segoe UI");

    public DevicePanel? Panel { get => (DevicePanel?)GetValue(PanelProperty); set => SetValue(PanelProperty, value); }

    public HeatBar()
    {
        Height = 42;
        SnapsToDevicePixels = true;
    }

    private void Watch(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DevicePanel old) old.PropertyChanged -= OnPanelChanged;
        if (e.NewValue is DevicePanel now) now.PropertyChanged += OnPanelChanged;
    }

    private void OnPanelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        if (w < 40 || Panel is not { } p) return;
        double X(double c) => Math.Clamp((c - From) / (To - From), 0, 1) * w;

        // The scale itself (the same stops as the design's CSS: cold 0 %, fine 35 %, warm 72 %, hot 88 %, critical 100 %)
        var scale = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        scale.GradientStops.Add(new GradientStop(Res("ColdBrush"), 0));
        scale.GradientStops.Add(new GradientStop(Res("GoodBrush"), 0.35));
        scale.GradientStops.Add(new GradientStop(Res("WarnBrush"), 0.72));
        scale.GradientStops.Add(new GradientStop(Res("HotBrush"), 0.88));
        scale.GradientStops.Add(new GradientStop(Res("CritBrush"), 1));
        scale.Freeze();
        var bar = new Rect(0, Top, w, BarHeight);
        dc.DrawRoundedRectangle(scale, null, bar, BarHeight / 2, BarHeight / 2);

        if (double.IsNaN(p.Temp))
        {
            // No reading: the whole scale dimmed, no marker
            var dim = new SolidColorBrush(Res("SurfaceAltBrush")) { Opacity = 0.82 };
            dim.Freeze();
            dc.DrawRoundedRectangle(dim, null, bar, BarHeight / 2, BarHeight / 2);
        }
        else
        {
            // Past the current temperature: dimmed, so the bar reads as "how far up the scale you are"
            double now = X(p.Temp);
            var mask = new SolidColorBrush(Res("SurfaceAltBrush")) { Opacity = 0.82 };
            mask.Freeze();
            dc.PushClip(new RectangleGeometry(bar, BarHeight / 2, BarHeight / 2));
            dc.DrawRectangle(mask, null, new Rect(now, Top, w - now, BarHeight));
            dc.Pop();

            if (!double.IsNaN(p.RangeMin) && !double.IsNaN(p.RangeMax))
            {
                var rangePen = new Pen(new SolidColorBrush(Res("MutedBrush")) { Opacity = 0.6 }, 1.5);
                rangePen.Freeze();
                double a = X(p.RangeMin) - 3, b = X(p.RangeMax) + 3;
                dc.DrawRoundedRectangle(null, rangePen, new Rect(a, Top - 4, Math.Max(6, b - a), BarHeight + 8), 4, 4);
            }

            var marker = new SolidColorBrush(Res("TextBrush"));
            marker.Freeze();
            var ring = new Pen(new SolidColorBrush(Res("SurfaceBrush")), 3);
            ring.Freeze();
            dc.DrawRoundedRectangle(marker, ring, new Rect(now - 2, Top - 6, 4, BarHeight + 12), 2, 2);
        }

        // 20° 40° 60° 80° 100° under the bar
        var muted = new SolidColorBrush(Res("SubtleBrush"));
        muted.Freeze();
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (double c = From; c <= To; c += 20)
        {
            var text = new FormattedText($"{SensorFormat.ToDisplay(SensorType.Temperature, (float)c):0}°", CultureInfo.CurrentCulture,
                                         FlowDirection.LeftToRight, Face, 11, muted, dpi);
            double x = X(c) - (c == From ? 0 : c == To ? text.Width : text.Width / 2);
            dc.DrawText(text, new Point(x, Top + BarHeight + 10));
        }
    }

    private Color Res(string key) => (TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Gray;
}

/// <summary>
/// A CPU's or GPU's temperature over the Overview's range, as a line in its heat colour with a soft fill, the load as
/// grey bars along the bottom, and the limit as a dashed red line. Redraws with every history sample while visible.
/// </summary>
public sealed class HeatChart : FrameworkElement
{
    public static readonly DependencyProperty PanelProperty = DependencyProperty.Register(nameof(Panel), typeof(DevicePanel), typeof(HeatChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface Face = new("Segoe UI");
    private const double AxisWidth = 34;
    private readonly List<(DateTime Time, float Value)> _temps = new(), _loads = new();

    public DevicePanel? Panel { get => (DevicePanel?)GetValue(PanelProperty); set => SetValue(PanelProperty, value); }

    public HeatChart()
    {
        SnapsToDevicePixels = true;
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
    }

    private void OnSampled()
    {
        if (IsVisible) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth - AxisWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, h));
        if (w < 40 || h < 30 || Panel is not { } p) return;

        var window = TimeSpan.FromMinutes(App.Current.Settings.HistoryMinutes);
        var history = App.Current.History;
        if (p.TempId != null) history.GetSeries(p.TempId, window, _temps); else _temps.Clear();
        if (p.LoadId != null) history.GetSeries(p.LoadId, window, _loads); else _loads.Clear();

        // Temperatures from 30° up to a little over the limit (or the hottest reading, if higher)
        double hottest = _temps.Where(t => !float.IsNaN(t.Value)).Select(t => (double)t.Value).DefaultIfEmpty(p.Limit).Max();
        double lo = Math.Min(30, _temps.Where(t => !float.IsNaN(t.Value)).Select(t => (double)t.Value).DefaultIfEmpty(30).Min() - 5);
        double hi = Math.Max(p.Limit, hottest) + 6;
        var now = DateTime.UtcNow;
        double X(DateTime t) => w - (now - t).TotalSeconds / window.TotalSeconds * w;
        double Y(double c) => h - (c - lo) / (hi - lo) * h;

        var line = Brush("LineBrush");
        var subtle = Brush("SubtleBrush");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var grid = new Pen(line, 1);
        grid.Freeze();
        foreach (double c in new[] { 40.0, 60.0, 80.0 })
        {
            if (c <= lo || c >= hi) continue;
            double y = Math.Round(Y(c)) + 0.5;
            dc.DrawLine(grid, new Point(0, y), new Point(w, y));
            Label(dc, $"{SensorFormat.ToDisplay(SensorType.Temperature, (float)c):0}°", subtle, w + 6, y - 8, dpi);
        }

        // The limit
        var crit = Brush("CritBrush");
        var limitPen = new Pen(crit, 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) };
        limitPen.Freeze();
        double ly = Math.Round(Y(p.Limit)) + 0.5;
        dc.DrawLine(limitPen, new Point(0, ly), new Point(w, ly));
        Label(dc, $"{SensorFormat.ToDisplay(SensorType.Temperature, (float)p.Limit):0}°", crit, w + 6, ly - 8, dpi);

        // Load: grey bars along the bottom third (one per ~1/60 of the range)
        const int bars = 60;
        var barBrush = new SolidColorBrush(((SolidColorBrush)Brush("ChartSecondaryBrush")).Color) { Opacity = 0.45 };
        barBrush.Freeze();
        double slot = w / bars;
        for (int i = 0; i < bars; i++)
        {
            var from = now - window + TimeSpan.FromTicks(window.Ticks * i / bars);
            var to = now - window + TimeSpan.FromTicks(window.Ticks * (i + 1) / bars);
            var values = _loads.Where(l => l.Time >= from && l.Time < to && !float.IsNaN(l.Value)).Select(l => l.Value).ToList();
            if (values.Count == 0) continue;
            double bh = Math.Max(1.5, values.Average() / 100 * h * 0.32);
            dc.DrawRectangle(barBrush, null, new Rect(i * slot + 1, h - bh, Math.Max(1, slot - 2), bh));
        }

        // Temperature: the current heat colour, with a soft fill under it
        var heat = Brush(Thermal.BrushKey(p.Heat));
        var c0 = ((SolidColorBrush)heat).Color;
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, c0.R, c0.G, c0.B), Color.FromArgb(0x00, c0.R, c0.G, c0.B), 90);
        fill.Freeze();
        var stroke = new StreamGeometry();
        var area = new StreamGeometry();
        using (var s = stroke.Open())
        using (var a = area.Open())
        {
            bool open = false;
            Point last = default;
            foreach (var (t, v) in Thin(_temps, (int)(w / 2)))
            {
                if (float.IsNaN(v))
                {
                    if (open) a.LineTo(new Point(last.X, h), false, false);
                    open = false;
                    continue;
                }
                var pt = new Point(X(t), Math.Clamp(Y(v), 0, h));
                if (!open)
                {
                    s.BeginFigure(pt, false, false);
                    a.BeginFigure(new Point(pt.X, h), true, true);
                    a.LineTo(pt, false, false);
                    open = true;
                }
                else
                {
                    s.LineTo(pt, true, true);
                    a.LineTo(pt, false, false);
                }
                last = pt;
            }
            if (open) a.LineTo(new Point(last.X, h), false, false);
        }
        stroke.Freeze();
        area.Freeze();
        dc.DrawGeometry(fill, null, area);
        var pen = new Pen(heat, 1.8) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, stroke);
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private static void Label(DrawingContext dc, string text, Brush brush, double x, double y, double dpi) =>
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, brush, dpi), new Point(x, y));

    /// <summary>About one point per 2 px: runs of samples averaged (a gap stays a gap).</summary>
    private static IEnumerable<(DateTime Time, float Value)> Thin(List<(DateTime Time, float Value)> points, int buckets)
    {
        if (points.Count <= buckets || buckets < 2)
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
