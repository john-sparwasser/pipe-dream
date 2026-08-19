using PipeDream;
using Xunit;

namespace PipeDream.Tests;

// Pure BGR555 <-> RGBA math, no ROM.
public class PaletteTests
{
    [Fact]
    public void ToRgba_IsOpaque()
    {
        Assert.Equal(0xFFu, Palette.ToRgba(0) >> 24);
        Assert.Equal(0xFFu, Palette.ToRgba(0x7FFF) >> 24);
    }

    [Fact]
    public void ToRgba_WhiteAndBlack()
    {
        Assert.Equal(0xFF000000u, Palette.ToRgba(0x0000));           // black, opaque
        Assert.Equal(0xFFFFFFFFu, Palette.ToRgba(0x7FFF));           // 5-bit max -> 0xFF per channel
    }

    [Theory]
    [InlineData(0x001F, 0xFF)]   // red max  -> R channel 0xFF
    [InlineData(0x03E0, 0xFF)]   // green max
    [InlineData(0x7C00, 0xFF)]   // blue max
    public void ToRgba_ChannelExtremes(ushort bgr, uint expectedByte)
    {
        uint rgba = Palette.ToRgba(bgr);
        uint r = rgba & 0xFF, g = (rgba >> 8) & 0xFF, b = (rgba >> 16) & 0xFF;
        Assert.Equal(expectedByte, System.Math.Max(r, System.Math.Max(g, b)));
    }

    [Fact]
    public void IsTransparent_EveryRowColorZero()
    {
        for (int i = 0; i < 256; i++)
            Assert.Equal(i % 16 == 0, Palette.IsTransparent(i));
    }
}
