using System.Windows;
using System.Windows.Controls;

namespace Kelvra;

/// <summary>
/// As many columns as fit (at least <see cref="MinColumnWidth"/> wide), stretched to fill the whole width.
/// Each child drops into the shortest column so tall cards don't leave gaps (unlike WrapPanel rows).
/// </summary>
public sealed class MasonryPanel : Panel
{
    public double MinColumnWidth { get; set; } = 380;

    protected override Size MeasureOverride(Size available)
    {
        double colWidth = ColumnWidth(available.Width);
        foreach (UIElement c in InternalChildren) c.Measure(new Size(colWidth, double.PositiveInfinity));
        var heights = Place(available.Width, null);
        return new Size(heights.Length * colWidth, heights.Max());
    }

    protected override Size ArrangeOverride(Size final)
    {
        double colWidth = ColumnWidth(final.Width);
        Place(final.Width, (c, col, y) => c.Arrange(new Rect(col * colWidth, y, colWidth, c.DesiredSize.Height)));
        return final;
    }

    private int Columns(double width) =>
        double.IsInfinity(width) ? Math.Max(1, InternalChildren.Count) : Math.Max(1, (int)(width / MinColumnWidth));

    private double ColumnWidth(double width) => double.IsInfinity(width) ? MinColumnWidth : width / Columns(width);

    private double[] Place(double width, Action<UIElement, int, double>? arrange)
    {
        var heights = new double[Columns(width)];
        foreach (UIElement c in InternalChildren)
        {
            int i = Array.IndexOf(heights, heights.Min());
            arrange?.Invoke(c, i, heights[i]);
            heights[i] += c.DesiredSize.Height;
        }
        return heights;
    }
}
