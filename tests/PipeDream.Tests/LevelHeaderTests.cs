using PipeDream;
using Xunit;

namespace PipeDream.Tests;

// LevelHeader decodes 5 raw bytes into fields (bank 05 CODE_0584E3). Pure, no ROM.
public class LevelHeaderTests
{
    [Fact]
    public void DecodesKnownFields()
    {
        // b0=0x14 -> Screens (0x14&0x1F)+1=0x15, BgPalette 0x14>>5=0
        // b1=0x41 -> LevelMode 1, BackAreaColor 2
        // b2=0x35 -> SpriteSet 5, Music 3, Layer3Priority 0
        // b3=0xC5 -> Time 0xC5>>6=3, SpritePalette (0xC5>>3)&7=0, FgPalette 5
        // b4=0x27 -> Tileset 7, ScrollSetting (0x27>>4)&3=2, ItemMemory 0x27>>6=0
        var h = new LevelHeader(new byte[] { 0x14, 0x41, 0x35, 0xC5, 0x27 });
        Assert.Equal(0x15, h.Screens);
        Assert.Equal(0, h.BgPalette);
        Assert.Equal(1, h.LevelMode);
        Assert.Equal(2, h.BackAreaColor);
        Assert.Equal(5, h.SpriteSet);
        Assert.Equal(3, h.Music);
        Assert.Equal(0, h.Layer3Priority);
        Assert.Equal(3, h.Time);
        Assert.Equal(0, h.SpritePalette);
        Assert.Equal(5, h.FgPalette);
        Assert.Equal(7, h.Tileset);
        Assert.Equal(2, h.ScrollSetting);
        Assert.Equal(0, h.ItemMemory);
    }

    [Fact]
    public void ScreensIsOneBased()
    {
        Assert.Equal(1, new LevelHeader(new byte[] { 0x00, 0, 0, 0, 0 }).Screens);
        Assert.Equal(32, new LevelHeader(new byte[] { 0x1F, 0, 0, 0, 0 }).Screens);
    }
}
