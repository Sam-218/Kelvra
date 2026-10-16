using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Kelvra;

/// <summary>
/// Draggable fan curve: temperature (°C) across, fan speed (%) up. Click a point to select it (the Fans page edits it as
/// numbers), drag it to move it, double-click empty space to add one, right-click a point to remove it. Also shows the
/// minimum speed, the full-speed area above the critical temperature (hatched), the thermal scale along the bottom and
/// where the fan is right now, in the colour of its temperature.
/// </summary>
public sealed class CurveEditor : FrameworkElement
{
    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(
        nameof(Profile), typeof(FanProfile), typeof(CurveEditor), new FrameworkPropertyMetadata(null, OnProfileChanged));

    public static readonly DependencyProperty SelectedPointProperty = DependencyProperty.Register(
        nameof(SelectedPoint), typeof(FanPoint), typeof(CurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    private const double MinTemp = 20, MaxTemp = 100;
    private const double Left = 44, Bottom = 46, Top = 12, Right = 16;
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private FanPoint? _drag;
    private FanPoint? _hover;

    public CurveEditor()
    {
        Focusable = false;
        Cursor = Cursors.Cross;
        ToolTip = "Drag points to shape the curve · double-click to add a point · right-click a point to remove it";
    }

    public FanProfile? Profile { get => (FanProfile?)GetValue(ProfileProperty); set => SetValue(ProfileProperty, value); }

    /// <summary>The point picked for editing (ringed, its values shown); the page binds its boxes to it.</summary>
    public FanPoint? SelectedPoint { get => (FanPoint?)GetValue(SelectedPointProperty); set => SetValue(SelectedPointProperty, value); }

    /// <summary>Current temperature (°C) and output (%) for the live marker; NaN = not shown.</summary>
    public double LiveTemp { get; set; } = double.NaN;
    public double LivePercent { get; set; } = double.NaN;

    /// <summary>Raised after the user changed the curve.</summary>
    public event Action? Edited;

    private static void OnProfileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CurveEditor)d;
        if (e.OldValue is FanProfile old) editor.Unhook(old);
        if (e.NewValue is FanProfile p) editor.Hook(p);
        editor.SelectMiddle();
        editor.InvalidateVisual();
    }

    /// <summary>A sensible point to start with: the middle one.</summary>
    private void SelectMiddle() =>
        SelectedPoint = Profile is { Points.Count: > 0 } p ? p.Points.OrderBy(x => x.Temp).ElementAt(p.Points.Count / 2) : null;

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
            // A preset or a revert replaced the whole list
            Unhook(p);
            Hook(p);
            SelectMiddle();
        }
        if (e.PropertyName != nameof(FanProfile.Status)) InvalidateVisual();
    }

    private void OnPointsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (FanPoint pt in e.OldItems) pt.PropertyChanged -= OnPointChanged;
        if (e.NewItems != null) foreach (FanPoint pt in e.NewItems) pt.PropertyChanged += OnPointChanged;
        if (SelectedPoint != null && Profile?.Points.Contains(SelectedPoint) != true) SelectMiddle();
        InvalidateVisual();
    }

    private void OnPointChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    // ---------- coordinates ----------

    private double PlotW => Math.Max(10, ActualWidth - Left - Right);
    private double PlotH => Math.Max(10, ActualHeight - Top - Bottom);
    private double X(double temp) => Left + (Math.Clamp(temp, MinTemp, MaxTemp) - MinTemp) / (MaxTemp - MinTemp) * PlotW;
    private double Y(double percent) => Top + (1 - percent / 100) * PlotH;
    private double TempAt(double x) => MinTemp + (x - Left) / PlotW * (MaxTemp - MinTemp);
    private double PercentAt(double y) => (1 - (y - Top) / PlotH) * 100;

    // ---------- drawing ----------

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (Profile is not { } p || w < 120 || h < 100) return;

        var line = Res("LineBrush", Brushes.Gray);
        var subtle = Res("SubtleBrush", Brushes.Gray);
        var text = Res("TextBrush", Brushes.White);
        var crit = Res("CritBrush", Brushes.Red);
        var surface = Res("SurfaceBrush", Brushes.Black);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double plotBottom = Top + PlotH, plotRight = Left + PlotW;

        // full speed above the critical temperature: hatched
        double cx = X(p.CriticalTemp);
        if (cx < plotRight)
        {
            var hatch = new DrawingBrush(new GeometryDrawing(null, new Pen(crit, 2), new LineGeometry(new Point(0, 0), new Point(0, 7))))
            {
                TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 7, 7), ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(-1, 0, 7, 7), ViewboxUnits = BrushMappingMode.Absolute, Transform = new RotateTransform(45), Opacity = 0.35,
            };
            hatch.Freeze();
            dc.DrawRectangle(hatch, null, new Rect(cx, Top, plotRight - cx, PlotH));
            var full = Format("100 %", crit, 11, dip);
            dc.DrawText(full, new Point((cx + plotRight) / 2 - full.Width / 2, Top + 4));
        }

        // grid and labels
        var grid = new Pen(line, 1);
        grid.Freeze();
        for (int pct = 0; pct <= 100; pct += 25)
        {
            double y = Math.Round(Y(pct)) + 0.5;
            dc.DrawLine(grid, new Point(Left, y), new Point(plotRight, y));
            var label = Format($"{pct}%", subtle, 11, dip);
            dc.DrawText(label, new Point(Left - 10 - label.Width, y - label.Height / 2));
        }
        for (int t = 20; t <= 100; t += 20)
        {
            double x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(grid, new Point(x, Top), new Point(x, plotBottom));
            var label = Format($"{t} °C", subtle, 11, dip);
            dc.DrawText(label, new Point(x - label.Width / 2, plotBottom + 22));
        }

        // the thermal scale along the bottom: the colour each temperature has everywhere in Kelvra
        var scale = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), Opacity = 0.85 };
        foreach (var (at, key) in new[] { (0.0, "ColdBrush"), (0.3, "GoodBrush"), (0.62, "WarnBrush"), (0.8, "HotBrush"), (1.0, "CritBrush") })
            scale.GradientStops.Add(new GradientStop((Res(key, Brushes.Gray) as SolidColorBrush)?.Color ?? Colors.Gray, at));
        scale.Freeze();
        dc.DrawRoundedRectangle(scale, null, new Rect(Left, plotBottom + 6, PlotW, 6), 3, 3);

        // minimum speed
        double my = Math.Round(Y(p.MinPercent)) + 0.5;
        var minPen = new Pen(subtle, 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) };
        minPen.Freeze();
        dc.DrawLine(minPen, new Point(Left, my), new Point(plotRight, my));
        dc.DrawText(Format($"minimum {p.MinPercent:0} %", subtle, 11, dip), new Point(Left + 6, my + 4));

        // the curve: flat before the first point and after the last, capped by the minimum and the critical temperature
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
        if (text is SolidColorBrush ts)
        {
            // a soft fill under the curve
            var fill = new LinearGradientBrush(Color.FromArgb(0x1A, ts.Color.R, ts.Color.G, ts.Color.B), Color.FromArgb(0, ts.Color.R, ts.Color.G, ts.Color.B), 90);
            fill.Freeze();
            var area = geo.Clone();
            var under = new PathGeometry();
            under.AddGeometry(area);
            under.Figures[0].Segments.Add(new LineSegment(new Point(X(MaxTemp), plotBottom), false));
            under.Figures[0].Segments.Add(new LineSegment(new Point(X(MinTemp), plotBottom), false));
            under.Figures[0].IsClosed = true;
            under.Figures[0].IsFilled = true;
            dc.DrawGeometry(fill, null, under);
        }
        var curvePen = new Pen(text, 2.2) { LineJoin = PenLineJoin.Round };
        curvePen.Freeze();
        dc.DrawGeometry(null, curvePen, geo);

        // live position, in the colour of the temperature
        if (!double.IsNaN(LiveTemp))
        {
            var heat = Thermal.BrushFor(LiveTemp, p.CriticalTemp);
            double lx = X(LiveTemp);
            double ly = double.IsNaN(LivePercent) ? plotBottom : Y(Math.Clamp(LivePercent, 0, 100));
            var livePen = new Pen(heat, 1.5) { DashStyle = new DashStyle(new double[] { 1.3, 2 }, 0) };
            livePen.Freeze();
            dc.DrawLine(livePen, new Point(lx, ly), new Point(lx, plotBottom + 4));
            if (!double.IsNaN(LivePercent))
            {
                dc.PushOpacity(0.18);
                dc.DrawEllipse(heat, null, new Point(lx, ly), 11, 11);
                dc.Pop();
                dc.DrawEllipse(heat, null, new Point(lx, ly), 5, 5);
            }
            string now = double.IsNaN(LivePercent) ? $"Now {LiveTemp:0} °C" : $"Now {LiveTemp:0} °C · {LivePercent:0} %";
            var nowText = Format(now, heat, 12, dip, bold: true);
            double nx = lx - nowText.Width - 12 < Left ? lx + 12 : lx - nowText.Width - 12;
            dc.DrawText(nowText, new Point(nx, Math.Min(ly + 14, plotBottom - nowText.Height - 2)));
        }

        // the points; the selected one ringed, with its values
        var selected = SelectedPoint;
        foreach (var pt in pts)
        {
            bool isSelected = ReferenceEquals(pt, selected), active = isSelected || ReferenceEquals(pt, _hover) || ReferenceEquals(pt, _drag);
            var at = new Point(X(pt.Temp), Y(pt.Percent));
            if (isSelected)
            {
                var halo = new Pen(text, 5);
                halo.Freeze();
                dc.PushOpacity(0.2);
                dc.DrawEllipse(null, halo, at, 14, 14);
                dc.Pop();
            }
            dc.DrawEllipse(surface, new Pen(text, 2.2), at, active ? 8 : 6, active ? 8 : 6);
        }
        if ((_drag ?? _hover ?? selected) is { } shown && pts.Contains(shown))
        {
            var label = Format($"{shown.Temp:0} °C → {shown.Percent:0} %", text, 12, dip, bold: true);
            var at = new Point(X(shown.Temp), Y(shown.Percent));
            double bw = label.Width + 28, bh = 30;
            double bx = at.X + 18 + bw > plotRight ? at.X - 18 - bw : at.X + 18;
            double by = Math.Max(Top, at.Y - 46);
            dc.DrawRoundedRectangle(Res("SurfaceAltBrush", surface), new Pen(Res("Line2Brush", line), 1), new Rect(bx, by, bw, bh), 6, 6);
            dc.DrawText(label, new Point(bx + 14, by + (bh - label.Height) / 2));
        }
    }

    private static FormattedText Format(string s, Brush brush, double size, double dip, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, brush, dip);

    // ---------- editing ----------

    private FanPoint? Hit(Point at) =>
        Profile?.Points.Where(pt => (new Point(X(pt.Temp), Y(pt.Percent)) - at).Length < 12)
                       .OrderBy(pt => (new Point(X(pt.Temp), Y(pt.Percent)) - at).Length).FirstOrDefault();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Profile is not { } p) return;
        var at = e.GetPosition(this);
        if (e.ClickCount == 2 && Hit(at) == null && p.Points.Count < FanCurve.MaxPoints)
        {
            var added = new FanPoint
            {
                Temp = (float)Math.Round(Math.Clamp(TempAt(at.X), MinTemp, MaxTemp)),
                Percent = (float)Math.Round(Math.Clamp(PercentAt(at.Y), 0, 100)),
            };
            int index = p.Points.TakeWhile(x => x.Temp < added.Temp).Count();
            p.Points.Insert(index, added);
            SelectedPoint = added;
            Edited?.Invoke();
            return;
        }
        _drag = Hit(at);
        if (_drag == null) return;
        SelectedPoint = _drag;
        CaptureMouse();
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
