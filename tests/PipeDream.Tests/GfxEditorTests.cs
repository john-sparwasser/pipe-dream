using Xunit;

namespace PipeDream.Tests;

/// <summary>GFX tile editor (canvas mode 3): SetTilePixel ↔ DecodeTile round-trips, the
/// stroke-capturing pixel writer, the per-tile flood fill, the copy-on-write fork of stock
/// files, and the re-look-up stroke replay closures.</summary>
public class GfxEditorTests
{
    // --- SetTilePixel ↔ DecodeTile -------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void set_tile_pixel_round_trips_decode_tile(int bpp)
    {
        int tb = Gfx.TileBytes(bpp), colors = 1 << bpp;
        var gfx = new byte[tb];
        new Random(bpp).NextBytes(gfx);                    // clearing bits must work too
        for (int y = 0; y < 8; y++)                        // all 64 pixels, indices cycling
            for (int x = 0; x < 8; x++)
                Gfx.SetTilePixel(gfx, 0, bpp, x, y, (y * 8 + x) % colors);
        var px = Gfx.DecodeTile(gfx, 0, bpp);
        for (int i = 0; i < 64; i++) Assert.Equal(i % colors, px[i]);

        for (int c = 0; c < colors; c++)                   // one pixel through every index
        {
            Gfx.SetTilePixel(gfx, 0, bpp, 3, 5, c);
            Assert.Equal(c, Gfx.DecodeTile(gfx, 0, bpp)[5 * 8 + 3]);
        }
    }

    // --- the stroke-capturing writer ------------------------------------------

    [Fact]
    public void same_color_write_records_nothing_and_leaves_bytes_identical()
    {
        var gfx = new byte[24];
        Gfx.SetTilePixel(gfx, 0, 3, 2, 2, 5);
        var snap = (byte[])gfx.Clone();
        var stroke = new List<(int off, byte before, byte after)>();
        GfxEditor.WritePixel(gfx, 0, 3, 2, 2, 5, stroke);
        Assert.Empty(stroke);
        Assert.Equal(snap, gfx);

        GfxEditor.WritePixel(gfx, 24, 3, 0, 0, 1, stroke);   // past the file: ignored
        Assert.Empty(stroke);
        Assert.Equal(snap, gfx);
    }

    [Fact]
    public void write_pixel_captures_only_the_plane_bytes_that_changed()
    {
        var gfx = new byte[24];
        Gfx.SetTilePixel(gfx, 0, 3, 0, 0, 5);              // 101: planes 0+2
        var stroke = new List<(int off, byte before, byte after)>();
        GfxEditor.WritePixel(gfx, 0, 3, 0, 0, 7, stroke);  // 111: only plane 1 flips
        var e = Assert.Single(stroke);
        Assert.Equal((1, (byte)0x00, (byte)0x80), e);      // plane 1 row 0 = offset 1
        Assert.Equal(7, Gfx.DecodeTile(gfx, 0, 3)[0]);
    }

    // --- flood fill ------------------------------------------------------------

    [Fact]
    public void fill_floods_4_connected_within_one_tile_only()
    {
        // Two 3bpp tiles; tile 0 split by a vertical wall of color 1 at x=4.
        var gfx = new byte[2 * 24];
        for (int y = 0; y < 8; y++) Gfx.SetTilePixel(gfx, 0, 3, 4, y, 1);
        var stroke = new List<(int off, byte before, byte after)>();
        GfxEditor.FillTile(gfx, 3, 0, 0, 5, stroke);       // seed left of the wall
        Assert.NotEmpty(stroke);
        var t0 = Gfx.DecodeTile(gfx, 0, 3);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(x < 4 ? 5 : x == 4 ? 1 : 0, t0[y * 8 + x]);
        Assert.All(Gfx.DecodeTile(gfx, 24, 3), p => Assert.Equal(0, p));   // neighbor tile untouched
    }

    [Fact]
    public void fill_is_a_noop_when_target_equals_the_color()
    {
        var gfx = new byte[24];
        var stroke = new List<(int off, byte before, byte after)>();
        GfxEditor.FillTile(gfx, 3, 3, 3, 0, stroke);       // color 0 over color 0
        Assert.Empty(stroke);
        Assert.All(gfx, b => Assert.Equal(0, b));
    }

    // --- copy-on-write fork ------------------------------------------------------

    [Fact]
    public void first_touch_forks_stock_bytes_under_the_same_id()
    {
        var rom = TestRom.CreateWithGfx00();
        var stock = Gfx.Cached(rom, 0)!;
        var g = GfxEditor.EditableBytes(rom, 0, out bool forked)!;
        Assert.True(forked);
        Assert.NotSame(stock, g);                          // a clone, not the cached stock array
        Assert.Equal(stock, g);
        Assert.Same(g, rom.ImportedGfx[0]);                // same-id keying shadows the stock file
        Assert.Same(g, Gfx.Cached(rom, 0));                // cache invalidated → fork served

        Assert.Same(g, GfxEditor.EditableBytes(rom, 0, out forked));   // second touch: no re-fork
        Assert.False(forked);
    }

    [Fact]
    public void unresolvable_id_returns_null_without_forking()
    {
        var rom = TestRom.Create();
        Assert.Null(GfxEditor.EditableBytes(rom, 0x2A5, out bool forked));
        Assert.False(forked);
        Assert.False(rom.ImportedGfx.ContainsKey(0x2A5));
    }

    // --- stroke replay (undo/redo closures) ---------------------------------------

    [Fact]
    public void stroke_replay_re_looks_up_the_array_and_bounds_checks()
    {
        var rom = TestRom.Create();
        var g = new byte[24];
        rom.ImportedGfx[0x100] = g;
        var stroke = new List<(int off, byte before, byte after)>();
        GfxEditor.WritePixel(g, 0, 3, 0, 0, 7, stroke);    // planes 0/1/2 → offsets 0, 1, 16
        Assert.Equal(3, stroke.Count);
        var edits = stroke.ToArray();

        GfxEditor.ApplyStroke(rom, 0x100, edits, redo: false);
        Assert.All(g, b => Assert.Equal(0, b));            // undo restored the zeros
        GfxEditor.ApplyStroke(rom, 0x100, edits, redo: true);
        Assert.Equal(7, Gfx.DecodeTile(g, 0, 3)[0]);       // redo reapplied

        var shorter = new byte[8];                          // re-import replaced the array
        rom.ImportedGfx[0x100] = shorter;
        GfxEditor.ApplyStroke(rom, 0x100, edits, redo: false);  // offset 16 skipped, no throw
        Assert.Equal(0, shorter[0]);                        // in-range offsets land in the NEW array
        Assert.Equal(0x80, g[16]);                          // the replaced array is never touched

        rom.ImportedGfx.Remove(0x100);
        GfxEditor.ApplyStroke(rom, 0x100, edits, redo: false);  // missing id: silent no-op
    }
}
