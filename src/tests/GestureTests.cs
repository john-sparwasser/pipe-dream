using Avalonia;
using PipeDream.Ui;
using Xunit;

namespace PipeDream.Tests;

/// <summary>The rectangle arithmetic the canvases share. Each canvas used to carry its own copy;
/// these pin the one copy so a canvas that leans on it can trust the edges.</summary>
public class GestureTests
{
    [Fact]
    public void a_span_is_inclusive_and_does_not_care_which_way_it_was_dragged()
    {
        Assert.Equal((2, 3, 4, 2), Lasso.Span((2, 3), (5, 4)));
        Assert.Equal((2, 3, 4, 2), Lasso.Span((5, 4), (2, 3)));
        Assert.Equal((7, 7, 1, 1), Lasso.Span((7, 7), (7, 7)));          // a click is a one-cell lasso
    }

    [Fact]
    public void contains_is_half_open_and_false_for_no_rectangle()
    {
        Assert.True(Lasso.Contains((2, 3, 4, 2), (5, 4)));
        Assert.False(Lasso.Contains((2, 3, 4, 2), (6, 4)));
        Assert.False(Lasso.Contains(null, (2, 3)));
    }

    [Fact]
    public void a_moved_block_parks_against_the_edge_it_was_dragged_past()
    {
        // 4x1 block at x=4, grabbed at its second cell, dragged far right in a 16-wide grid.
        Assert.Equal((12, 0, 4, 1), Lasso.Moved((4, 0, 4, 1), (5, 0), (40, 0), 16, 8));
        Assert.Equal((0, 0, 4, 1), Lasso.Moved((4, 0, 4, 1), (5, 0), (-9, 0), 16, 8));
        Assert.Equal((5, 0, 4, 1), Lasso.Moved((4, 0, 4, 1), (5, 0), (6, 0), 16, 8));
        // A block wider than the grid still has a home: the origin.
        Assert.Equal((0, 0, 20, 1), Lasso.Moved((0, 0, 20, 1), (1, 0), (9, 0), 16, 8));
    }

    [Fact]
    public void a_point_past_the_grid_clamps_to_the_border_but_a_hit_test_says_off()
    {
        Assert.Equal((15, 7), Lasso.Clamped(new Point(900, 900), 32, 16, 8));
        Assert.Equal((0, 0), Lasso.Clamped(new Point(-5, -5), 32, 16, 8));
        Assert.Null(Lasso.Clamped(new Point(1, 1), 32, 0, 8));
        Assert.Null(Lasso.CellAt(new Point(900, 1), 32, 16, 8));
        Assert.Null(Lasso.CellAt(new Point(-1, 1), 32, 16, 8));
        Assert.Equal((3, 1), Lasso.CellAt(new Point(100, 40), 32, 16, 8));
    }

    [Fact]
    public void a_grip_is_on_the_border_only_and_shrinks_on_a_small_selection()
    {
        var r = new Rect(100, 100, 200, 100);
        Assert.Equal((-1, 0), Grips.EdgeAt(new Point(103, 150), r, 9));
        Assert.Equal((1, 1), Grips.EdgeAt(new Point(297, 197), r, 9));
        Assert.Equal((0, -1), Grips.EdgeAt(new Point(200, 95), r, 9));
        Assert.Equal((0, 0), Grips.EdgeAt(new Point(200, 150), r, 9));           // the middle moves
        Assert.Equal((0, 0), Grips.EdgeAt(new Point(103, 30), r, 9));            // level with an edge, off the rect
        Assert.Equal((0, 0), Grips.EdgeAt(new Point(103, 150), r, 9, wOk: false)); // axis that cannot resize
        // A 12px-wide selection keeps a 4px grip, so its middle is still grabbable.
        var small = new Rect(0, 0, 12, 60);
        Assert.Equal((-1, 0), Grips.EdgeAt(new Point(3, 30), small, 9));
        Assert.Equal((0, 0), Grips.EdgeAt(new Point(6, 30), small, 9));
    }

    [Fact]
    public void resizing_moves_the_grabbed_edge_and_pins_the_opposite_one()
    {
        Assert.Equal((2, 2, 6, 2), Grips.Resized((4, 2, 4, 2), (-1, 0), (2, 9)));
        Assert.Equal((4, 2, 4, 5), Grips.Resized((4, 2, 4, 2), (0, 1), (0, 6)));
        Assert.Equal((7, 2, 1, 2), Grips.Resized((4, 2, 4, 2), (-1, 0), (12, 2)));  // dragged through: one cell
        Assert.Equal(1 | 8, Grips.Mask((-1, 1)));
        Assert.Equal(2 | 4, Grips.Mask((1, -1)));
        Assert.Null(Grips.CursorFor((0, 0)));
        Assert.Same(Grips.CursorFor((-1, -1)), Grips.CursorFor((1, 1)));
        Assert.Same(Grips.CursorFor((1, -1)), Grips.CursorFor((-1, 1)));
    }

    [Fact]
    public void a_stroke_between_two_samples_has_no_holes_and_skips_the_start()
    {
        var steps = Lasso.Between((0, 0), (5, 2)).ToList();
        Assert.Equal(5, steps.Count);
        Assert.Equal((5, 2), steps[^1]);
        Assert.DoesNotContain((0, 0), steps);
        for (int i = 1; i < steps.Count; i++)
            Assert.True(Math.Abs(steps[i].X - steps[i - 1].X) <= 1 && Math.Abs(steps[i].Y - steps[i - 1].Y) <= 1,
                        $"gap between {steps[i - 1]} and {steps[i]}");
        Assert.Equal([(3, 3)], Lasso.Between((3, 3), (3, 3)));
    }
}
