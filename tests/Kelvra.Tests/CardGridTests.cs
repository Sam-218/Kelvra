namespace Kelvra.Tests;

/// <summary>The System page's snap grid: free placement, making room, snapping and window-like resizing.</summary>
public class CardGridTests
{
    private static GridBox B(int x, int w, double y, double h) => new(x, w, y, h);

    [Fact]
    public void Cards_stay_where_they_are_put_gaps_and_all()
    {
        var boxes = new[] { B(0, 3, 0, 100), B(6, 3, 400, 100), B(3, 3, 120, 50) };
        Assert.Equal(boxes, CardGrid.Resolve(boxes)); // nothing overlaps: nothing moves (no pulling up into gaps)
    }

    [Fact]
    public void A_card_put_onto_others_pushes_them_down_and_keeps_its_spot()
    {
        // The active card (index 0) dropped over the top of a column of two cards
        var boxes = new[] { B(0, 6, 50, 200), B(0, 3, 0, 100), B(0, 3, 100, 100), B(6, 3, 0, 100) };
        var r = CardGrid.Resolve(boxes, active: 0);
        Assert.Equal(B(0, 6, 50, 200), r[0]);
        Assert.Equal(B(0, 3, 250, 100), r[1]); // pushed below the active card
        Assert.Equal(B(0, 3, 350, 100), r[2]); // and the one under it moves along
        Assert.Equal(B(6, 3, 0, 100), r[3]);   // other columns aren't touched
    }

    [Fact]
    public void Tidy_up_closes_the_gaps_in_each_column()
    {
        var tidy = CardGrid.Compact(new[] { B(0, 3, 300, 100), B(0, 3, 600, 50), B(3, 6, 90, 40) });
        Assert.Equal(new double[] { 0, 100, 0 }, tidy.Select(b => b.Y));
    }

    [Theory]
    [InlineData(47, 40)]   // on the grid (rows of 20 px)
    [InlineData(55, 60)]
    [InlineData(208, 212)] // within reach of a neighbour's edge: lines up with it
    [InlineData(-30, 0)]   // never above the page
    public void Edges_snap_to_neighbours_first_then_to_the_grid(double y, double snapped) =>
        Assert.Equal(snapped, CardGrid.SnapEdge(y, new double[] { 212, 500 }));

    [Fact]
    public void Dropped_one_unit_short_of_a_neighbour_it_snaps_to_it()
    {
        // A card at 0–3, the dropped card at 4–7 (one unit gap left), one at 9–12 (two units gap right)
        var boxes = new[] { B(0, 3, 0, 200), B(4, 3, 50, 200), B(9, 3, 0, 200) };
        Assert.Equal(B(3, 4, 50, 200), CardGrid.FillGap(boxes, 1)); // fills the gap on the left only

        Assert.Equal(B(8, 4, 0, 100), CardGrid.FillGap(new[] { B(8, 3, 0, 100) }, 0)); // one unit from the page edge
        Assert.Equal(B(4, 3, 0, 100), CardGrid.FillGap(new[] { B(4, 3, 0, 100) }, 0)); // far from everything
        // A card above or below doesn't count as a neighbour
        Assert.Equal(B(4, 8, 300, 100), CardGrid.FillGap(new[] { B(0, 3, 0, 100), B(4, 7, 300, 100) }, 1));
    }

    [Fact]
    public void Two_cards_side_by_side_share_their_edge_like_snapped_windows()
    {
        var boxes = new[] { B(0, 6, 0, 200), B(6, 6, 20, 200), B(0, 12, 260, 100) };
        var wider = CardGrid.ResizeRight(boxes, 0, right: 8);
        Assert.Equal(B(0, 8, 0, 200), wider[0]);
        Assert.Equal(B(8, 4, 20, 200), wider[1]);   // the neighbour gave up the room
        Assert.Equal(B(0, 12, 260, 100), wider[2]); // the card below isn't beside it: untouched

        var max = CardGrid.ResizeRight(boxes, 0, right: 12);
        Assert.Equal(B(10, CardGrid.MinWidth, 20, 200), max[1]); // never narrower than the minimum

        var narrower = CardGrid.ResizeRight(boxes, 0, right: 1);
        Assert.Equal(B(0, CardGrid.MinWidth, 0, 200), narrower[0]);
        Assert.Equal(B(2, 10, 20, 200), narrower[1]);
    }

    [Fact]
    public void Free_room_to_the_right_ends_at_the_next_card_beside_it()
    {
        var boxes = new[] { B(0, 3, 0, 200), B(7, 3, 100, 200), B(3, 9, 400, 100) };
        Assert.Equal(7, CardGrid.FreeRight(boxes, 0));
        Assert.Equal(CardGrid.Units, CardGrid.FreeRight(new[] { B(0, 3, 0, 100) }, 0));
    }

    [Theory]
    [InlineData(158, 3)] // 1900 px page: quarters
    [InlineData(100, 4)] // 1200 px: thirds
    [InlineData(84, 6)]  // 1000 px: halves
    public void Cards_without_a_spot_get_columns_that_fit(double unitPx, int width) =>
        Assert.Equal(width, CardGrid.DefaultWidthFor(unitPx));

    [Fact]
    public void New_cards_go_to_the_shortest_column()
    {
        var sky = new double[CardGrid.Units];
        for (int u = 0; u < 3; u++) sky[u] = 300;
        for (int u = 3; u < 6; u++) sky[u] = 100;
        for (int u = 6; u < 12; u++) sky[u] = 200;
        Assert.Equal(3, CardGrid.ShortestColumn(sky, 3));
    }
}
