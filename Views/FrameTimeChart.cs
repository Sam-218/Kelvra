using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Kelvra;

/// <summary>
/// Live frametime graph for the gaming overlay: every frame of the last 10 s as a line (one column per pixel keeps the
/// slowest frame, so no spike is averaged away), stutters drawn in the hot colour. Redraws 20× a second while a game runs.
/// </summary>
public sealed class FrameTimeChart : FrameworkElement
{
    private const double SpanMs = 10_000;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly bool _preview;
    private Pen _line = new(Brushes.Lime, 1.2);
    private Pen _spike = new(Brushes.Red, 1.6);
    private Pen _grid = new(Brushes.White, 1);
    private Brush _text = Brushes.White;

    public FrameTimeChart(bool preview)
    {
        _preview = preview;
        SnapsToDevicePixels = true;
        _timer.Tick += (_, _) =>
        {
            if (IsVisible && App.Current.Game.ActiveGame != null) InvalidateVisual();
        };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    public void SetColors(Brush line, Brush spike, Brush text)
    {
        _line = Frozen(new Pen(line, 1.2) { LineJoin = PenLineJoin.Round });
        _spike = Frozen(new Pen(spike, 1.6));
        var faint = text.Clone();
        faint.Opacity = 0.18;
        _grid = Frozen(new Pen(faint, 1) { DashStyle = DashStyles.Dash });
        _text = text;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var game = App.Current.Game.ActiveGame;
        var points = game != null ? game.Stats.Recent(SpanMs) : _preview ? DemoPoints() : Array.Empty<FramePoint>();
        if (points.Length < 2) return;

        // Scale: at least 0–20 ms, otherwise up to the slowest frame in steps of 10 ms. Its label gets its own
        // space on the right, so it never covers the line.
        double slowest = 0;
        foreach (var p in points) slowest = Math.Max(slowest, p.FrameTimeMs);
        double top = Math.Max(20, Math.Ceiling(slowest / 10) * 10);
        var label = new FormattedText($"{top.ToString("0", CultureInfo.InvariantCulture)} ms", CultureInfo.InvariantCulture,
                                      FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Clamp(h / 3.2, 8, 11), _text,
                                      VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double plotW = w - label.Width - 5;
        if (plotW < 20) plotW = w; // too narrow for a label: just the line
        else
        {
            dc.PushOpacity(0.6);
            dc.DrawText(label, new Point(w - label.Width, 0));
            dc.Pop();
        }
        double Y(double ms) => h - 1 - Math.Min(ms, top) / top * (h - 2);
        dc.DrawLine(_grid, new Point(0, Y(top / 2)), new Point(plotW, Y(top / 2)));

        // Columns: the slowest frame per pixel column, and whether that column holds a stutter
        int columns = Math.Max(2, (int)plotW);
        var max = new double[columns];
        var stutter = new bool[columns];
        double end = points[^1].TimeMs, start = end - SpanMs;
        foreach (var p in points)
        {
            int c = Math.Clamp((int)((p.TimeMs - start) / SpanMs * (columns - 1)), 0, columns - 1);
            if (p.FrameTimeMs > max[c]) max[c] = p.FrameTimeMs;
            stutter[c] |= p.Stutter;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            bool started = false;
            for (int c = 0; c < columns; c++)
            {
                if (max[c] <= 0) continue;
                var pt = new Point(c * plotW / (columns - 1), Y(max[c]));
                if (!started)
                {
                    ctx.BeginFigure(pt, false, false);
                    started = true;
                }
                else ctx.LineTo(pt, true, true);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, _line, geometry);

        for (int c = 0; c < columns; c++)
            if (stutter[c])
            {
                double x = c * plotW / (columns - 1);
                dc.DrawLine(_spike, new Point(x, h - 1), new Point(x, Y(max[c])));
            }
    }
    /// <summary>A believable 144 FPS line with a little noise and two hitches, for the editor preview.</summary>
    private static FramePoint[] DemoPoints()
    {
        var list = new List<FramePoint>(1440);
        double t = Environment.TickCount64 % 100_000, ms;
        var rnd = new Random(7);
        for (int i = 0; i < 1440; i++)
        {
            bool spike = i is 400 or 1100;
            ms = spike ? 31 : 6.9 + rnd.NextDouble() * 1.2 + Math.Sin(i / 40.0) * 0.6;
            t += ms;
            list.Add(new FramePoint(t, ms, spike));
        }
        return list.ToArray();
    }

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
