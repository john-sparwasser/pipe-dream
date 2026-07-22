using PipeDream;
using Xunit;

namespace PipeDream.Tests;

// Data-table + bit-decode logic that needs no ROM.
public class SpecTests
{
    [Fact]
    public void Map16Word_DecodesTilemapBits()
    {
        // YXPCCCTT TTTTTTTT: tile 0x182, pal 2, no prio/flip.
        var w = new Map16.Word((ushort)(0x182 | (2 << 10)));
        Assert.Equal(0x182, w.Tile);
        Assert.Equal(2, w.Palette);
        Assert.False(w.Priority);
        Assert.False(w.FlipX);
        Assert.False(w.FlipY);

        var f = new Map16.Word(0xC000 | 0x055);   // flipX+flipY
        Assert.True(f.FlipX);
        Assert.True(f.FlipY);
        Assert.Equal(0x055, f.Tile);
    }

    [Fact]
    public void ObjectNames_KnownEntries()
    {
        Assert.Equal("Coins", ObjectNames.Standard(0x05));
        Assert.Equal("Ground ledge", ObjectNames.Standard(0x14));
        Assert.Equal("Screen exit", ObjectNames.Extended(0x00));
        Assert.Equal("Yoshi Coin", ObjectNames.Extended(0x41));
    }

    [Fact]
    public void ObjectNames_UnknownFallsBackToHex()
    {
        Assert.Equal("Object FE", ObjectNames.Standard(0xFE));
        Assert.Equal("Ext FE", ObjectNames.Extended(0xFE));
    }

    [Fact]
    public void SpriteDisplay_ParsesTilesAndReqs()
    {
        const string json = """
        { "sprites": {
            "05": {
              "name": "Red Koopa",
              "tiles": [ { "x": -1, "y": -15, "tile": "0x082", "pal": 3, "size": 16, "xflip": true } ],
              "hitbox": { "x": 2, "y": 3, "w": 12, "h": 10 },
              "gfx": { "1": ["0x01"] }
            } } }
        """;
        var table = SpriteDisplay.Parse(json);
        Assert.True(table.ContainsKey(0x05));
        var e = table[0x05];
        Assert.Equal("Red Koopa", e.Name);
        var o = Assert.Single(e.Oam);
        Assert.Equal(-1, o.X);
        Assert.Equal(0x082, o.Tile);
        Assert.True(o.Big);                          // size 16
        Assert.Equal(3, (o.Attr >> 1) & 7);          // palette 3 in YXPPCCCT
        Assert.True((o.Attr & 0x40) != 0);           // xflip bit
        Assert.Equal(new[] { 0x01 }, e.Req[1]);      // slot 1 accepts file 0x01
        Assert.Empty(e.Req[0]);
        Assert.Equal((2, 3, 12, 10), e.Hitbox);
    }
}
