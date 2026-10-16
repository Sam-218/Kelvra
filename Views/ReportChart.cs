using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>
/// The Reports page's temperature chart: CPU (text colour) and GPU (faint) over the report's period, by the minute for a
/// day, by the hour for a week and by the day for longer. The time the PC was on is shaded; a reading over
/// <see cref="HotDot"/> gets a dot in its heat colour; the GPU limit is a dashed line; crashes and driver changes are
/// flagged where they happened. A gap in the data is a gap in the lines. Hover for the values at that time.
/// </summary>
public sealed class ReportChart : FrameworkElement
{
    /// <summary>Readings at or over this (°C) get a dot: "over 80°".</summary>
    public const double HotDot = 80;

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private const double Right = 64, Bottom = 26, Top = 50; // Right: the °/alert labels; Top: two rows of flags

    private ReportPeriod? _period;
    private readonly List<(string Label, string BrushKey, List<ReportPoint> Points)> _series = new();
    private readonly List<(DateTime At, string Label, bool Problem)> _markers = new();
    private double _limit = ReportBuilder.DefaultGpuLimit;
    private bool _limitIsAlert;
    private double? _hoverX;

    public ReportChart()
    {
        SnapsToDevicePixels = true;
        MouseMove += (_, e) =>
        {
            _hoverX = e.GetPosition(this).X;
            InvalidateVisual();
        };
        MouseLeave += (_, _) =>
        {
            _hoverX = null;
            InvalidateVisual();
        };
    }

    public void SetData(Report r)
    {
        _period = r.Period;
        (_limit, _limitIsAlert) = (r.GpuLimit, r.GpuLimitIsAlert);
        _series.Clear();
        if (r.Series.TryGetValue(HistoryRoles.GpuTemp, out var gpu)) _series.Add(("GPU", "SubtleBrush", gpu)); // under the CPU line
        if (r.Series.TryGetValue(HistoryRoles.CpuTemp, out var cpu)) _series.Add(("CPU", "TextBrush", cpu));
        _markers.Clear();
        foreach (var c in r.Crashes) _markers.Add((HistoryDb.Local(c.Ts), CrashExplainer.Explain(c).Title, true));
        foreach (var c in r.Changes.Where(c => c.Kind == ChangeKinds.Driver && c.After != null))
            _markers.Add((HistoryDb.Local(c.Ts), $"{c.Subject} {c.After}", false));
        InvalidateVisual();
    }

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    /// <summary>How long one point stands for: a minute, an hour (a week from minutes) or a day.</summary>
    private TimeSpan Step(ReportPeriod p) =>
        p.Range == ReportRange.Day ? TimeSpan.FromMinutes(1)
        : _series.Any(s => s.Points.Count > p.Days) ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable for the hover
        if (_period is not { } p || w < Right + 60 || h < Bottom + Top + 30) return;

        Brush muted = Res("MutedBrush"), subtle = Res("SubtleBrush"), line = Res("LineBrush"), crit = Res("CritBrush"), hot = Res("HotBrush");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double plotW = w - Right, plotH = h - Bottom;

        var values = _series.SelectMany(s => s.Points).SelectMany(pt => new[] { pt.Avg, pt.Max }).Where(double.IsFinite).ToList();
        if (values.Count == 0)
        {
            Text(dc, "No temperatures recorded for this period yet.", muted, 12, new Point(0, (Top + plotH) / 2 - 8), dpi);
            return;
        }
        // The limit stays in view when it's near, so "how close did it get" reads at a glance
        double top = values.Max() >= _limit - 15 ? Math.Max(values.Max(), _limit) : values.Max();
        double lo = Math.Floor((values.Min() - 5) / 10) * 10, hi = Math.Ceiling((top + 5) / 10) * 10;
        if (hi - lo < 30) hi = lo + 30;

        double span = (p.To - p.From).TotalSeconds;
        double X(DateTime t) => (t - p.From).TotalSeconds / span * plotW;
        double Y(double v) => Math.Clamp(Top + (plotH - Top) * (1 - (v - lo) / (hi - lo)), Top, plotH);

        // shaded = PC on: every point stands for its minute / hour / day
        var step = Step(p);
        var shade = new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF));
        if (Res("TextBrush") is SolidColorBrush text) shade = new SolidColorBrush(Color.FromArgb(0x0A, text.Color.R, text.Color.G, text.Color.B));
        shade.Freeze();
        var on = _series.SelectMany(s => s.Points).Select(pt => pt.Time).Distinct().OrderBy(t => t).ToList();
        for (int i = 0; i < on.Count;)
        {
            // one rectangle per run of points next to each other
            DateTime start = on[i] - step / 2, end = on[i] + step / 2;
            while (++i < on.Count && on[i] - step / 2 <= end + TimeSpan.FromSeconds(1)) end = on[i] + step / 2;
            dc.DrawRectangle(shade, null, new Rect(X(start), Top, Math.Max(1, X(end) - X(start)), plotH - Top));
        }

        // grid: every 20° on the right, and the days (a week) or the ticks below
        var gridPen = Frozen(new Pen(line, 1));
        for (double v = Math.Ceiling(lo / 20) * 20; v < hi; v += 20)
        {
            double y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(plotW, y));
            Text(dc, $"{SensorFormat.ToDisplay(SensorType.Temperature, (float)v):0}°", subtle, 11, new Point(plotW + 10, y - 8), dpi);
        }
        foreach (var (label, at, edge) in Ticks(p))
        {
            if (edge is DateTime e)
            {
                double x = Math.Round(X(e)) + 0.5;
                dc.DrawLine(gridPen, new Point(x, Top), new Point(x, plotH));
            }
            var ft = Format(label, muted, 12, dpi);
            dc.DrawText(ft, new Point(Math.Clamp(X(at) - ft.Width / 2, 0, plotW - ft.Width), plotH + 6));
        }

        // the limit: the user's alert, else the chip's usual limit
        if (_limit < hi)
        {
            double ly = Math.Round(Y(_limit)) + 0.5;
            var dash = new Pen(hot, 1) { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) };
            dc.PushOpacity(0.7);
            dc.DrawLine(Frozen(dash), new Point(0, ly), new Point(plotW, ly));
            dc.Pop();
            string word = _limitIsAlert ? "alert" : "limit";
            Text(dc, $"{SensorFormat.ToDisplay(SensorType.Temperature, (float)_limit):0}° {word}", hot, 11, new Point(plotW + 10, ly - 8), dpi);
        }

        // flags: labels go on the first of two rows with room; one without room keeps its line (the Crashes page has the rest)
        var rowEnds = new[] { double.MinValue, double.MinValue };
        foreach (var (at, label, problem) in _markers.OrderBy(m => m.At))
        {
            double mx = Math.Round(X(at)) + 0.5;
            if (mx < 0 || mx > plotW) continue;
            var brush = problem ? crit : muted;
            var ft = Format(label, brush, 11, dpi, bold: true);
            double lx = Math.Min(mx + 6, plotW - ft.Width - 14);
            int row = Array.FindIndex(rowEnds, end => lx > end + 6);
            double ly = row < 0 ? Top : row * (Top / 2);
            var dash = new Pen(brush, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            dc.DrawLine(Frozen(dash), new Point(mx, row < 0 ? Top : ly + 10), new Point(mx, plotH));
            if (row < 0) continue;
            dc.DrawRoundedRectangle(Res("SurfaceAltBrush"), Frozen(new Pen(brush, 1)), new Rect(lx, ly, ft.Width + 14, 20), 5, 5);
            dc.DrawText(ft, new Point(lx + 7, ly + (20 - ft.Height) / 2));
            rowEnds[row] = lx + ft.Width + 14;
        }

        // the lines (averages), then a dot in its heat colour where a minute / hour / day went over 80°
        var gap = step * 1.6;
        foreach (var (label, key, points) in _series)
        {
            var thinned = Thin(points, (int)(plotW / 2));
            DrawLine(dc, thinned, pt => new Point(X(pt.Time), Y(pt.Avg)),
                     new Pen(Res(key), label == "CPU" ? 1.8 : 1.6) { LineJoin = PenLineJoin.Round }, gap);
            foreach (var pt in thinned.Where(pt => pt.Max >= HotDot && double.IsFinite(pt.Avg)))
                dc.DrawEllipse(Thermal.BrushFor(pt.Max), null, new Point(X(pt.Time), Y(pt.Avg)), 3.5, 3.5);
        }

        if (_hoverX is double hx && hx >= 0 && hx <= plotW) DrawHover(dc, p, hx, X, plotH, dpi, line, muted);
    }

    private void DrawHover(DrawingContext dc, ReportPeriod p, double hx, Func<DateTime, double> x, double plotH, double dpi, Brush line, Brush muted)
    {
        var lines = new List<(string Text, Brush Brush)>();
        DateTime? at = null;
        foreach (var (label, key, points) in _series.AsEnumerable().Reverse()) // CPU first
        {
            var near = points.Where(pt => double.IsFinite(pt.Avg)).MinBy(pt => Math.Abs(x(pt.Time) - hx));
            if (near == null || Math.Abs(x(near.Time) - hx) > 12) continue;
            at ??= near.Time;
            lines.Add(($"{label} {SensorFormat.Format(SensorType.Temperature, (float)near.Avg)} · max {SensorFormat.Format(SensorType.Temperature, (float)near.Max)}",
                       key == "SubtleBrush" ? muted : Res(key)));
        }
        if (at is not DateTime time) return;
        var pen = Frozen(new Pen(line, 1));
        dc.DrawLine(pen, new Point(Math.Round(hx) + 0.5, Top), new Point(Math.Round(hx) + 0.5, plotH));

        var c = CultureInfo.CurrentCulture;
        string head = p.Range == ReportRange.Day ? time.ToString("t", c)
            : p.Range == ReportRange.Week ? $"{time.ToString("ddd d MMM", c)}, {time.ToString("t", c)}" : time.ToString("ddd d MMM", c);
        lines.Insert(0, (head, muted));
        var texts = lines.Select(l => Format(l.Text, l.Brush, 11, dpi)).ToList();
        double boxW = texts.Max(t => t.Width) + 16, boxH = texts.Sum(t => t.Height) + 10;
        double bx = hx + 10 + boxW > ActualWidth - Right ? hx - 10 - boxW : hx + 10;
        dc.DrawRoundedRectangle(Res("SurfaceAltBrush"), pen, new Rect(bx, Top + 4, boxW, boxH), 6, 6);
        double ty = Top + 9;
        foreach (var t in texts)
        {
            dc.DrawText(t, new Point(bx + 8, ty));
            ty += t.Height;
        }
    }

    private static void DrawLine(DrawingContext dc, List<ReportPoint> points, Func<ReportPoint, Point> at, Pen pen, TimeSpan gap)
    {
        pen.Freeze();
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            ReportPoint? last = null;
            foreach (var pt in points)
            {
                if (!double.IsFinite(pt.Avg)) { last = null; continue; }
                if (last == null || pt.Time - last.Time > gap) c.BeginFigure(at(pt), false, false);
                else c.LineTo(at(pt), true, true);
                last = pt;
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    /// <summary>About one point per 2 px: a day's 1,440 minutes averaged in groups (the highest kept as the highest).</summary>
    private static List<ReportPoint> Thin(List<ReportPoint> points, int buckets)
    {
        if (points.Count <= buckets || buckets < 2) return points;
        var list = new List<ReportPoint>(buckets);
        double per = (double)points.Count / buckets;
        for (int b = 0; b < buckets; b++)
        {
            int from = (int)(b * per), to = Math.Min(points.Count, (int)((b + 1) * per));
            if (to <= from) continue;
            var run = points.GetRange(from, to - from);
            list.Add(new ReportPoint(run[run.Count / 2].Time, run.Average(x => x.Avg), run.Max(x => x.Max)));
        }
        return list;
    }

    /// <summary>The labels under the chart (centred on <c>At</c>) and, for a week, the line between the days (<c>Edge</c>).</summary>
    private static IEnumerable<(string Label, DateTime At, DateTime? Edge)> Ticks(ReportPeriod p)
    {
        var c = CultureInfo.CurrentCulture;
        switch (p.Range)
        {
            case ReportRange.Day:
                for (int hour = 0; hour < 24; hour += 3) yield return (p.From.AddHours(hour).ToString("t", c), p.From.AddHours(hour), null);
                break;
            case ReportRange.Week:
                for (var d = p.From; d < p.To; d = d.AddDays(1)) yield return (d.ToString("ddd d", c), d.AddHours(12), d);
                break;
            case ReportRange.Month:
                for (var d = p.From; d < p.To; d = d.AddDays(5)) yield return (d.ToString("d MMM", c), d.AddHours(12), null);
                break;
            default:
                for (var d = p.From; d < p.To; d = d.AddMonths(1)) yield return (d.ToString("MMM", c), d.AddDays(15), d);
                break;
        }
    }

    private static FormattedText Format(string text, Brush brush, double size, double dpi, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, brush, dpi);

    private static void Text(DrawingContext dc, string text, Brush brush, double size, Point at, double dpi) =>
        dc.DrawText(Format(text, brush, size, dpi), at);
}
