using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>One sensor's history as shown on a chart card.</summary>
public sealed class ChartItem : ObservableObject
{
    private string _statsText = "Collecting data…";
    private TimeSpan _window = TimeSpan.FromMinutes(5);
    private bool _isPinned;

    public ChartItem(SensorVm sensor, Color color)
    {
        Sensor = sensor;
        Color = color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Brush = brush;
    }

    public SensorVm Sensor { get; }
    public Color Color { get; }
    public Brush Brush { get; }
    public string Title => Sensor.Name;
    public string Subtitle => $"{Sensor.GroupName} · {Sensor.TypeLabel}";
    public List<(DateTime Time, float Value)> Points { get; } = new();

    public TimeSpan Window { get => _window; set => Set(ref _window, value); }
    public string StatsText { get => _statsText; private set => Set(ref _statsText, value); }
    public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }

    public float Min { get; private set; } = float.NaN;
    public float Max { get; private set; } = float.NaN;

    /// <summary>Pulls the latest samples from the history buffer and recomputes min/avg/max.</summary>
    public void Load(HistoryService history)
    {
        history.GetSeries(Sensor.Id, Window, Points);
        float min = float.MaxValue, max = float.MinValue;
        double sum = 0;
        int n = 0;
        foreach (var (_, v) in Points)
        {
            if (float.IsNaN(v)) continue;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
            sum += v;
            n++;
        }
        Min = n > 0 ? min : float.NaN;
        Max = n > 0 ? max : float.NaN;
        StatsText = n == 0
            ? "Collecting data…"
            : $"min {SensorFormat.Format(Sensor.Type, min)}  ·  avg {SensorFormat.Format(Sensor.Type, (float)(sum / n))}  ·  max {SensorFormat.Format(Sensor.Type, max)}";
    }
}

/// <summary>Line + area chart for a <see cref="ChartItem"/>. Refreshes itself once per second while on screen.</summary>
public sealed class ChartControl : FrameworkElement
{
    private static readonly Typeface Face = new("Segoe UI");
    private ScrollViewer? _scroller;
    private double? _hoverX;

    public ChartControl()
    {
        Loaded += (_, _) =>
        {
            _scroller = FindScroller(this);
            App.Current.History.Sampled += OnSampled;
            Refresh();
        };
        Unloaded += (_, _) => App.Current.History.Sampled -= OnSampled;
        DataContextChanged += (_, _) => Refresh();
    }

    private ChartItem? Item => DataContext as ChartItem;

    private void OnSampled()
    {
        if (IsOnScreen()) Refresh();
    }

    public void Refresh()
    {
        if (Item == null) return;
        Item.Load(App.Current.History);
        InvalidateVisual();
    }

    private bool IsOnScreen()
    {
        if (!IsVisible) return false;
        if (_scroller == null) return true;
        try
        {
            var bounds = TransformToAncestor(_scroller).TransformBounds(new Rect(RenderSize));
            return bounds.Bottom >= 0 && bounds.Top <= _scroller.ViewportHeight;
        }
        catch (InvalidOperationException)
        {
            return false; // not connected to the scroller (being recycled)
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (Item is not { } item || w < 10 || h < 10) return;

        var line = TryFindResource("LineBrush") as Brush ?? Brushes.Gray;
        var muted = TryFindResource("MutedBrush") as Brush ?? Brushes.Gray;
        var gridPen = new Pen(line, 1) { DashStyle = DashStyles.Dash };
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Y range (in display units)
        var type = item.Sensor.Type;
        bool percent = type is SensorType.Load or SensorType.Level or SensorType.Control;
        double lo, hi;
        if (percent) { lo = 0; hi = 100; }
        else if (float.IsNaN(item.Min)) { lo = 0; hi = 1; }
        else
        {
            lo = SensorFormat.ToDisplay(type, item.Min);
            hi = SensorFormat.ToDisplay(type, item.Max);
            if (lo >= 0 && type != SensorType.Temperature && type != SensorType.Voltage) lo = 0;
            double pad = Math.Max((hi - lo) * 0.15, Math.Abs(hi) * 0.02 + 0.5);
            hi += pad;
            if (lo > 0 || type == SensorType.Temperature) lo -= pad;
        }
        if (hi - lo < 1e-6) hi = lo + 1;

        for (int i = 0; i <= 2; i++)
        {
            double y = Math.Round(h * i / 2) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }
        DrawLabel(dc, FormatAxis(type, hi), muted, 2, 1, dip);
        DrawLabel(dc, FormatAxis(type, lo), muted, 2, h - 14, dip);

        var now = DateTime.Now;
        double windowSec = item.Window.TotalSeconds;
        double X(DateTime t) => w - (now - t).TotalSeconds / windowSec * w;
        double Y(float raw) => h - (SensorFormat.ToDisplay(type, raw) - lo) / (hi - lo) * h;

        // Average points into buckets of ~2 px so an hour of data stays cheap to draw
        var pts = Bucket(item.Points, Math.Max(10, (int)(w / 2)));
        if (pts.Count < 2) return;

        var stroke = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var s = stroke.Open())
        using (var f = fill.Open())
        {
            bool open = false;
            double lastX = 0;
            foreach (var (t, v) in pts)
            {
                if (float.IsNaN(v))
                {
                    if (open) f.LineTo(new Point(lastX, h), false, false);
                    open = false;
                    continue;
                }
                var p = new Point(X(t), Math.Clamp(Y(v), 0, h));
                if (!open)
                {
                    s.BeginFigure(p, false, false);
                    f.BeginFigure(new Point(p.X, h), true, true);
                    f.LineTo(p, false, false);
                    open = true;
                }
                else
                {
                    s.LineTo(p, true, true);
                    f.LineTo(p, false, false);
                }
                lastX = p.X;
            }
            if (open) f.LineTo(new Point(lastX, h), false, false);
        }
        stroke.Freeze();
        fill.Freeze();

        var areaBrush = new LinearGradientBrush(Color.FromArgb(0x60, item.Color.R, item.Color.G, item.Color.B),
                                                Color.FromArgb(0x05, item.Color.R, item.Color.G, item.Color.B), 90);
        dc.DrawGeometry(areaBrush, null, fill);
        dc.DrawGeometry(null, new Pen(item.Brush, 1.8) { LineJoin = PenLineJoin.Round }, stroke);

        if (_hoverX is double hx) DrawHover(dc, item, pts, hx, X, Y, muted, dip);
    }

    private void DrawHover(DrawingContext dc, ChartItem item, List<(DateTime Time, float Value)> pts, double hx,
                           Func<DateTime, double> x, Func<float, double> y, Brush muted, double dip)
    {
        (DateTime Time, float Value)? best = null;
        double bestDist = double.MaxValue;
        foreach (var p in pts)
        {
            if (float.IsNaN(p.Value)) continue;
            double d = Math.Abs(x(p.Time) - hx);
            if (d < bestDist) { bestDist = d; best = p; }
        }
        if (best is not { } b) return;

        double px = x(b.Time), py = Math.Clamp(y(b.Value), 0, ActualHeight);
        dc.DrawLine(new Pen(muted, 1), new Point(px, 0), new Point(px, ActualHeight));
        dc.DrawEllipse(item.Brush, new Pen(Brushes.White, 1.5), new Point(px, py), 4, 4);

        var text = new FormattedText($"{SensorFormat.Format(item.Sensor.Type, b.Value)}  ·  {b.Time:HH:mm:ss}",
            CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 11,
            TryFindResource("TextBrush") as Brush ?? Brushes.White, dip);
        double tx = Math.Clamp(px - text.Width / 2, 2, ActualWidth - text.Width - 2);
        var box = new Rect(tx - 5, 2, text.Width + 10, text.Height + 4);
        dc.DrawRoundedRectangle(TryFindResource("SurfaceAltBrush") as Brush ?? Brushes.Black,
                                new Pen(TryFindResource("LineBrush") as Brush ?? Brushes.Gray, 1), box, 4, 4);
        dc.DrawText(text, new Point(tx, 4));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverX = null;
        InvalidateVisual();
    }

    private static List<(DateTime, float)> Bucket(List<(DateTime Time, float Value)> points, int buckets)
    {
        if (points.Count <= buckets) return points;
        var result = new List<(DateTime, float)>(buckets + 1);
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
            result.Add((points[(from + to - 1) / 2].Time, n > 0 ? (float)(sum / n) : float.NaN));
        }
        return result;
    }

    private static string FormatAxis(SensorType type, double displayValue) =>
        SensorFormat.Format(type, SensorFormat.FromDisplay(type, (float)displayValue));

    private void DrawLabel(DrawingContext dc, string text, Brush brush, double x, double y, double dip) =>
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 10, brush, dip),
                    new Point(x, y));

    private static ScrollViewer? FindScroller(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer sv) return sv;
        return null;
    }
}
