using Xunit;

namespace PipeDream.Tests;

public class Map16GridTests
{
    [Fact]
    public void new_grid_is_entirely_empty()
    {
        var g = new Map16Grid(16, 27);
        Assert.Equal(Map16Grid.Empty, g.Get(0, 0));
        Assert.Equal(Map16Grid.Empty, g.Get(15, 26));
        Assert.Equal(0, g.PlacedCount());
    }

    [Fact]
    public void set_then_get_round_trips_and_placed_count_tracks_it()
    {
        var g = new Map16Grid(8, 8);
        g.Set(3, 4, 0x130);
        g.Set(7, 0, 0x025);
        Assert.Equal(0x130, g.Get(3, 4));
        Assert.Equal(0x025, g.Get(7, 0));
        Assert.Equal(2, g.PlacedCount());
        g.Set(3, 4, Map16Grid.Empty);        // writing Empty clears the cell
        Assert.Equal(Map16Grid.Empty, g.Get(3, 4));
        Assert.Equal(1, g.PlacedCount());
    }

    [Fact]
    public void out_of_bounds_get_returns_empty_and_set_is_a_no_op()
    {
        var g = new Map16Grid(4, 4);
        Assert.Equal(Map16Grid.Empty, g.Get(-1, 0));
        Assert.Equal(Map16Grid.Empty, g.Get(0, -1));
        Assert.Equal(Map16Grid.Empty, g.Get(4, 0));
        Assert.Equal(Map16Grid.Empty, g.Get(0, 4));

        g.Set(-1, 0, 0x100);                 // none of these may throw or land anywhere
        g.Set(0, -1, 0x100);
        g.Set(4, 0, 0x100);
        g.Set(0, 4, 0x100);
        Assert.Equal(0, g.PlacedCount());
    }

    [Fact]
    public void clone_is_independent_of_the_original()
    {
        var g = new Map16Grid(4, 4);
        g.Set(1, 1, 0x42);
        var c = g.Clone();
        Assert.Equal(0x42, c.Get(1, 1));
        Assert.Equal(g.Width, c.Width);
        Assert.Equal(g.Height, c.Height);

        c.Set(1, 1, 0x99);                   // mutating the clone leaves the original alone
        c.Set(2, 2, 0x77);
        Assert.Equal(0x42, g.Get(1, 1));
        Assert.Equal(Map16Grid.Empty, g.Get(2, 2));

        g.Set(3, 3, 0x11);                   // and vice versa
        Assert.Equal(Map16Grid.Empty, c.Get(3, 3));
    }
}
