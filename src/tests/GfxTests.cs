using PipeDream;
using Xunit;

namespace PipeDream.Tests;

// Pure SNES planar tile decode + LZ2 command handling, synthetic input, no ROM.
public class GfxTests
{
    [Fact]
    public void TileBytes_PerBpp()
    {
        Assert.Equal(16, Gfx.TileBytes(2));
        Assert.Equal(24, Gfx.TileBytes(3));
        Assert.Equal(32, Gfx.TileBytes(4));
    }

    [Fact]
    public void DecodeTile_2bpp_PlaneBits()
    {
        // Row 0: plane0 = 0x80 (col0 bit set), plane1 = 0x40 (col1 bit set).
        var src = new byte[16];
        src[0] = 0x80; src[1] = 0x40;
        var px = Gfx.DecodeTile(src, 0, 2);
        Assert.Equal(1, px[0]);   // col0: only plane0 -> index 1
        Assert.Equal(2, px[1]);   // col1: only plane1 -> index 2
        Assert.Equal(0, px[2]);   // rest blank
    }

    [Fact]
    public void DecodeTile_4bpp_AllPlanes()
    {
        // 4bpp: planes 0/1 interleaved in first 16 bytes, planes 2/3 in next 16.
        var src = new byte[32];
        src[0] = 0x80;    // plane0 col0
        src[1] = 0x80;    // plane1 col0
        src[16] = 0x80;   // plane2 col0
        src[17] = 0x80;   // plane3 col0
        var px = Gfx.DecodeTile(src, 0, 4);
        Assert.Equal(0b1111, px[0]);   // all four planes set -> index 15
    }

    [Fact]
    public void DecodeTile_IndicesInRange()
    {
        var rng = new System.Random(1);
        var src = new byte[24];
        rng.NextBytes(src);
        var px = Gfx.DecodeTile(src, 0, 3);
        Assert.Equal(64, px.Length);
        Assert.All(px, p => Assert.InRange(p, 0, 7));   // 3bpp -> 0..7
    }

    [Fact]
    public void Lz2_DirectCopyThenEnd()
    {
        // Command header: CCCLLLLL. Direct copy (cmd 0), length L -> L+1 literal bytes.
        // 0x02 = cmd 0, len field 2 -> 3 bytes copied; then 0xFF terminates.
        var data = new byte[] { 0x02, 0xAA, 0xBB, 0xCC, 0xFF };
        var outp = Gfx.Lz2Decompress(data, 0);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, outp);
    }

    [Fact]
    public void Lz2_ByteFill()
    {
        // Command 1 (byte fill): 0x20|len. 0x24 = cmd 1, len 4 -> 5 bytes of the next byte.
        var data = new byte[] { 0x24, 0x77, 0xFF };
        var outp = Gfx.Lz2Decompress(data, 0);
        Assert.Equal(new byte[] { 0x77, 0x77, 0x77, 0x77, 0x77 }, outp);
    }
}
