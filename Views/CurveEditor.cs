using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Kelvra;

/// <summary>
/// Draggable fan curve: temperature (°C) across, fan speed (%) up. Drag a point to move it, double-click empty space
/// to add one, right-click a point to remove it. Also shows the minimum speed, the critical temperature and where the
/// fan is right now. Every point can also be typed in on the Fans page, for keyboard users.
/// </summary>
public sealed class CurveEditor : FrameworkElement
{
    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(
        nameof(Profile), typeof(FanProfile), typeof(CurveEditor), new FrameworkPropertyMetadata(null, OnProfileChanged));

    private const double MinTemp = 20, MaxTemp = 110;
    private const double Left = 40, Bottom = 24, Top = 10, Right = 12;
    private static readonly Typeface Face = new("Segoe UI");

    private FanPoint? _drag;
    private FanPoint? _hover;

    public CurveEditor()
    {
        Focusable = false;
        Cursor = Cursors.Cross;
        ToolTip = "Drag points to shape the curve · double-click to add a point · right-click a point to remove it";
    }

    public FanProfile? Profile { get => (FanProfile?)GetValue(ProfileProperty); set => SetValue(ProfileProperty, value); }

    /// <summary>Current temperature (°C) and output (%) for the live marker; NaN = not shown.</summary>
    public double LiveTemp { get; set; } = double.NaN;
    public double LivePercent { get; set; } = double.NaN;

    /// <summary>Raised after the user changed the curve (to save settings).</summary>
    public event Action? Edited;

    private static void OnProfileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CurveEditor)d;
        if (e.OldValue is FanProfile old) editor.Unhook(old);
        if (e.NewValue is FanProfile p) editor.Hook(p);
        editor.InvalidateVisual();
    }

    private void Hook(FanProfile p)
    {
        p.PropertyChanged += OnProfilePropertyChanged;
        p.Points.CollectionChanged += OnPointsChanged;
        foreach (var pt in p.Points) pt.PropertyChanged += OnPointChanged;
    }

    private void Unhook(FanProfile p)
    {
        p.PropertyChanged -= OnProfilePropertyChanged;
        p.Points.CollectionChanged -= OnPointsChanged;
        foreach (var pt in p.Points) pt.PropertyChanged -= OnPointChanged;
    }

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FanProfile.Points) && sender is FanProfile p)
        {
            // A preset replaced the whole list
            Unhook(p);
            Hook(p);
        }
        if (e.PropertyName != nameof(FanProfile.Status)) InvalidateVisual();
    }

    private void OnPointsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (FanPoint pt in e.OldItems) pt.PropertyChanged -= OnPointChanged;
        if (e.NewItems != null) foreach (FanPoint pt in e.NewItems) pt.PropertyChanged += OnPointChanged;
        InvalidateVisual();
    }

    private void OnPointChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    // ---------- coordinates ----------

    private double PlotW => Math.Max(10, ActualWidth - Left - Right);
    private double PlotH => Math.Max(10, ActualHeight - Top - Bottom);
    private double X(double temp) => Left + (temp - MinTemp) / (MaxTemp - MinTemp) * PlotW;
    private double Y(double percent) => Top + (1 - percent / 100) * PlotH;
    private double TempAt(double x) => MinTemp + (x - Left) / PlotW * (MaxTemp - MinTemp);
    private double PercentAt(double y) => (1 - (y - Top) / PlotH) * 100;

    // ---------- drawing ----------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (Profile is not { } p || w < 80 || h < 60) return;

        Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
        var line = Res("LineBrush", Brushes.Gray);
        var muted = Res("MutedBrush", Brushes.Gray);
        var accent = Res("AccentBrush", Brushes.Orange);
        var hot = Res("HotBrush", Brushes.Red);
        var text = Res("TextBrush", Brushes.White);
        var surface = Res("SurfaceBrush", Brushes.Black);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // grid + labels
        var grid = new Pen(line, 1);
        grid.Freeze();
        for (int pct = 0; pct <= 100; pct += 25)
        {
            double y = Math.Round(Y(pct)) + 0.5;
            dc.DrawLine(grid, new Point(Left, y), new Point(Left + PlotW, y));
            Label(dc, $"{pct} %", muted, 2, y - 7, dip);
        }
        for (int t = 20; t <= 110; t += 10)
        {
            double x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(grid, new Point(x, Top), new Point(x, Top + PlotH));
            Label(dc, $"{t}°", muted, x - 8, Top + PlotH + 4, dip);
        }

        // minimum speed band
        var soft = Res("HoverBrush", Brushes.Transparent);
        dc.DrawRectangle(soft, null, new Rect(Left, Y(p.MinPercent), PlotW, Math.Max(0, Y(0) - Y(p.MinPercent))));
        Label(dc, $"minimum {p.MinPercent:0} %", muted, Left + 6, Y(p.MinPercent) - 15, dip);

        // critical temperature
        var critPen = new Pen(hot, 1.2) { DashStyle = DashStyles.Dash };
        critPen.Freeze();
        double cx = X(p.CriticalTemp);
        dc.DrawLine(critPen, new Point(cx, Top), new Point(cx, Top + PlotH));
        Label(dc, $"100 % from {p.CriticalTemp:0}°", hot, Math.Min(cx + 4, w - 110), Top + 2, dip);

        // curve: flat before the first point and after the last, then capped by min and critical
        var pts = p.Points.OrderBy(x => x.Temp).ToList();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(X(MinTemp), Y(Math.Max(pts[0].Percent, p.MinPercent))), false, false);
            for (double t = MinTemp; t <= MaxTemp; t += 0.5)
            {
                double pct = t >= p.CriticalTemp ? 100 : Math.Max(FanCurve.Evaluate(pts, (float)t), p.MinPercent);
                g.LineTo(new Point(X(t), Y(pct)), true, true);
            }
        }
        geo.Freeze();
        var curvePen = new Pen(accent, 2.2) { LineJoin = PenLineJoin.Round };
        curvePen.Freeze();
        dc.DrawGeometry(null, curvePen, geo);

        // the user's points
        foreach (var pt in pts)
        {
            double r = ReferenceEquals(pt, _hover) || ReferenceEquals(pt, _drag) ? 7 : 5.5;
            dc.DrawEllipse(accent, new Pen(surface, 2), new Point(X(pt.Temp), Y(pt.Percent)), r, r);
            if (ReferenceEquals(pt, _hover) || ReferenceEquals(pt, _drag))
                Label(dc, $"{pt.Temp:0}° → {pt.Percent:0} %", text, Math.Min(X(pt.Temp) + 10, w - 90), Y(pt.Percent) - 22, dip);
        }

        // live position
        if (!double.IsNaN(LiveTemp))
        {
            var livePen = new Pen(text, 1) { DashStyle = DashStyles.Dot };
            livePen.Freeze();
            double lx = X(Math.Clamp(LiveTemp, MinTemp, MaxTemp));
            dc.DrawLine(livePen, new Point(lx, Top), new Point(lx, Top + PlotH));
            if (!double.IsNaN(LivePercent)) dc.DrawEllipse(text, null, new Point(lx, Y(Math.Clamp(LivePercent, 0, 100))), 4, 4);
            Label(dc, $"now {LiveTemp:0}°", text, Math.Min(lx + 4, w - 60), Top + PlotH - 16, dip);
        }
    }

    private static void Label(DrawingContext dc, string s, Brush brush, double x, double y, double dip) =>
        dc.DrawText(new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 10.5, brush, dip), new Point(x, y));

    // ---------- editing ----------

    private FanPoint? Hit(Point at) =>
        Profile?.Points.Where(pt => (new Point(X(pt.Temp), Y(pt.Percent)) - at).Length < 10)
                       .OrderBy(pt => (new Point(X(pt.Temp), Y(pt.Percent)) - at).Length).FirstOrDefault();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Profile is not { } p) return;
        var at = e.GetPosition(this);
        if (e.ClickCount == 2 && Hit(at) == null && p.Points.Count < FanCurve.MaxPoints)
        {
            var added = new FanPoint { Temp = (float)Math.Round(TempAt(at.X)), Percent = (float)Math.Round(PercentAt(at.Y)) };
            int index = p.Points.TakeWhile(x => x.Temp < added.Temp).Count();
            p.Points.Insert(index, added);
            Edited?.Invoke();
            return;
        }
        _drag = Hit(at);
        if (_drag != null) CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = e.GetPosition(this);
        if (_drag != null && Profile is { } p)
        {
            // Keep the points in temperature order: a point can't pass its neighbours
            var sorted = p.Points.OrderBy(x => x.Temp).ToList();
            int i = sorted.IndexOf(_drag);
            double lo = i > 0 ? sorted[i - 1].Temp + 1 : MinTemp;
            double hi = i < sorted.Count - 1 ? sorted[i + 1].Temp - 1 : MaxTemp;
            _drag.Temp = (float)Math.Round(Math.Clamp(TempAt(at.X), lo, hi));
            _drag.Percent = (float)Math.Round(Math.Clamp(PercentAt(at.Y), 0, 100));
            return;
        }
        var hover = Hit(at);
        if (!ReferenceEquals(hover, _hover))
        {
            _hover = hover;
            Cursor = hover != null ? Cursors.SizeAll : Cursors.Cross;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == null) return;
        _drag = null;
        ReleaseMouseCapture();
        Edited?.Invoke();
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (Profile is not { } p || Hit(e.GetPosition(this)) is not { } pt || p.Points.Count <= 2) return;
        p.Points.Remove(pt);
        _hover = null;
        Edited?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
    }
}
