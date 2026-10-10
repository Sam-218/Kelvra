namespace Kelvra;

/// <summary>
/// Where a card sits on the System page: <see cref="X"/> and <see cref="W"/> in twelfths of the page width,
/// <see cref="Y"/> in px from the top, and a height the user dragged it to (null: as tall as its content).
/// Saved in the settings.
/// </summary>
public sealed class CardSpot
{
    public string Title { get; set; } = "";
    /// <summary>Left edge in grid units (0–11); -1 = not placed yet, the page picks a spot.</summary>
    public int X { get; set; } = -1;
    /// <summary>Top in px; -1 = not set (layouts saved before cards could be placed freely: they stack in order).</summary>
    public double Y { get; set; } = -1;
    public int W { get; set; } = CardGrid.DefaultWidth;
    public double? H { get; set; }

    public CardSpot Copy() => new() { Title = Title, X = X, Y = Y, W = W, H = H };
}

/// <summary>A card for the layout maths: left edge and width in units, top and height in px.</summary>
internal readonly record struct GridBox(int X, int W, double Y, double H)
{
    public int Right => X + W;
    public double Bottom => Y + H;

    /// <summary>Shares columns with <paramref name="o"/>.</summary>
    public bool SameColumns(GridBox o) => X < o.Right && o.X < Right;

    /// <summary>Side by side with <paramref name="o"/> (their heights overlap).</summary>
    public bool Beside(GridBox o) => Y < o.Bottom - 0.5 && o.Y < Bottom - 0.5;

    public bool Overlaps(GridBox o) => SameColumns(o) && Beside(o);
}

/// <summary>
/// The rules of the System page's snap grid (pure maths, unit-tested). The page is <see cref="Units"/> units wide
/// with a row every <see cref="Row"/> px – an invisible grid every card snaps to. Cards stay where they're put, gaps
/// and all; a card that would cover another pushes it down, never up.
/// </summary>
internal static class CardGrid
{
    public const int Units = 12;
    public const int MinWidth = 2;
    public const int DefaultWidth = 3;
    public const double MinHeight = 120;
    /// <summary>Height of a grid row (px).</summary>
    public const double Row = 20;
    /// <summary>A card edge this close (px) to another card's edge snaps to it instead of to the grid.</summary>
    public const double SnapPx = 14;

    public static int ClampX(int x, int w) => Math.Clamp(x, 0, Units - Math.Clamp(w, MinWidth, Units));

    public static int ClampW(int w) => Math.Clamp(w, MinWidth, Units);

    /// <summary>
    /// Takes the cards as placed and moves the ones that cover another down until nothing overlaps.
    /// <paramref name="active"/> (the card being moved or resized; -1 for none) stays exactly where it is; the others
    /// keep their order from the top, so a card only ever moves down to make room.
    /// </summary>
    public static GridBox[] Resolve(IReadOnlyList<GridBox> boxes, int active = -1)
    {
        var result = new GridBox[boxes.Count];
        var placed = new List<GridBox>(boxes.Count);
        if (active >= 0)
        {
            result[active] = boxes[active];
            placed.Add(boxes[active]);
        }
        foreach (int i in Enumerable.Range(0, boxes.Count).Where(i => i != active).OrderBy(i => boxes[i].Y).ThenBy(i => boxes[i].X))
        {
            var b = boxes[i];
            bool moved = true;
            while (moved)
            {
                moved = false;
                foreach (var p in placed)
                {
                    if (!b.Overlaps(p)) continue;
                    b = b with { Y = p.Bottom };
                    moved = true;
                }
            }
            result[i] = b;
            placed.Add(b);
        }
        return result;
    }

    /// <summary>"Tidy up": every card moves up as far as it can without covering another (in the order they are from the top).</summary>
    public static GridBox[] Compact(IReadOnlyList<GridBox> boxes)
    {
        var result = new GridBox[boxes.Count];
        var placed = new List<GridBox>(boxes.Count);
        foreach (int i in Enumerable.Range(0, boxes.Count).OrderBy(i => boxes[i].Y).ThenBy(i => boxes[i].X))
        {
            var b = boxes[i];
            double top = placed.Where(p => p.SameColumns(b)).Select(p => p.Bottom).DefaultIfEmpty(0).Max();
            result[i] = b with { Y = top };
            placed.Add(result[i]);
        }
        return result;
    }

    /// <summary>
    /// A top or bottom edge at <paramref name="y"/> px snaps to the nearest card edge in <paramref name="edges"/>
    /// within <see cref="SnapPx"/> (to line up with a neighbour, or sit right under it), otherwise to the grid row.
    /// </summary>
    public static double SnapEdge(double y, IEnumerable<double> edges)
    {
        double? near = null;
        foreach (double e in edges)
            if (Math.Abs(e - y) <= SnapPx && (near == null || Math.Abs(e - y) < Math.Abs(near.Value - y))) near = e;
        return Math.Max(0, near ?? Math.Round(y / Row) * Row);
    }

    /// <summary>
    /// Columns for cards that have no spot yet, like a newspaper: equal columns as wide as fit at
    /// <paramref name="unitPx"/> (cards at least ~400 px), each card into the shortest one.
    /// </summary>
    public static int DefaultWidthFor(double unitPx)
    {
        foreach (int w in new[] { 2, 3, 4, 6, 12 })
            if (w * unitPx >= 400) return w;
        return Units;
    }

    /// <summary>The left edge (multiple of <paramref name="w"/>) where a new card of width <paramref name="w"/> lands highest.</summary>
    public static int ShortestColumn(double[] sky, int w)
    {
        int best = 0;
        double bestTop = double.MaxValue;
        for (int x = 0; x + w <= Units; x += w)
        {
            double top = 0;
            for (int u = x; u < x + w; u++) top = Math.Max(top, sky[u]);
            if (top < bestTop - 0.5)
            {
                bestTop = top;
                best = x;
            }
        }
        return best;
    }

    /// <summary>
    /// After a drop: a card that stopped one unit short of the card beside it (or of the page edge) grows to touch it,
    /// like a window snapped next to another.
    /// </summary>
    public static GridBox FillGap(IReadOnlyList<GridBox> boxes, int index)
    {
        var card = boxes[index];
        int left = 0, right = Units;
        for (int i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            if (i == index || !b.Beside(card)) continue;
            if (b.Right <= card.X) left = Math.Max(left, b.Right);
            else if (b.X >= card.Right) right = Math.Min(right, b.X);
        }
        int x = card.X - left <= 1 ? left : card.X;
        int r = right - card.Right <= 1 ? right : card.Right;
        return card with { X = x, W = r - x };
    }

    /// <summary>
    /// Moves the right edge of card <paramref name="index"/> to <paramref name="right"/> (units). Cards right beside it
    /// that touch that edge share it, like two snapped windows: they shrink or grow from the left, never below
    /// <see cref="MinWidth"/>. Returns the new boxes (the card's and its neighbours' X / W).
    /// </summary>
    public static List<GridBox> ResizeRight(IReadOnlyList<GridBox> boxes, int index, int right)
    {
        var result = boxes.ToList();
        var card = boxes[index];
        var touching = Enumerable.Range(0, boxes.Count).Where(i => i != index && boxes[i].X == card.Right && boxes[i].Beside(card)).ToList();
        int max = touching.Count > 0 ? touching.Min(i => boxes[i].Right - MinWidth) : Units;
        right = Math.Clamp(right, card.X + MinWidth, max);
        int delta = right - card.Right;
        result[index] = card with { W = right - card.X };
        foreach (int i in touching) result[i] = boxes[i] with { X = boxes[i].X + delta, W = boxes[i].W - delta };
        return result;
    }

    /// <summary>The right edge a card can grow to without covering a card beside it (for a double-click on its edge).</summary>
    public static int FreeRight(IReadOnlyList<GridBox> boxes, int index)
    {
        var card = boxes[index];
        int right = Units;
        for (int i = 0; i < boxes.Count; i++)
            if (i != index && boxes[i].X >= card.Right && boxes[i].Beside(card)) right = Math.Min(right, boxes[i].X);
        return right;
    }
}
