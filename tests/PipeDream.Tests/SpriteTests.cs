using Xunit;

namespace PipeDream.Tests;

public class SpriteTests
{
    [Fact]
    public void absolute_x_combines_screen_and_nibble()
    {
        var s = new Sprite(Screen: 2, XNibble: 5, Y: 11, Extra: 0, Number: 0x0F);
        Assert.Equal(37, s.AbsoluteX);
    }

    [Fact]
    public void cell_maps_horizontal_levels_as_x_y_and_vertical_levels_swapped()
    {
        var s = new Sprite(Screen: 2, XNibble: 5, Y: 11, Extra: 0, Number: 0x0F);
        Assert.Equal((37, 11), s.Cell(vertical: false));
        // Vertical levels decode "with X and Y swapped": Y becomes the column,
        // the screen walk runs down the level.
        Assert.Equal((11, 37), s.Cell(vertical: true));
    }

    [Fact]
    public void horizontal_and_vertical_cells_are_transposes_of_each_other()
    {
        foreach (var s in new[]
        {
            new Sprite(0, 0, 0, 0, 1),
            new Sprite(5, 15, 0x1F, 3, 0x20),
            new Sprite(31, 1, 7, 0, 0x80),
        })
        {
            var (hx, hy) = s.Cell(false);
            Assert.Equal((hy, hx), s.Cell(true));
        }
    }

    [Fact]
    public void numbers_from_e7_up_are_scroll_commands_not_sprites()
    {
        Assert.False(new Sprite(0, 0, 0, 0, 0xE6).IsScrollCommand);
        Assert.True(new Sprite(0, 0, 0, 0, 0xE7).IsScrollCommand);
        Assert.True(new Sprite(0, 0, 0, 0, 0xFF).IsScrollCommand);
    }
}
