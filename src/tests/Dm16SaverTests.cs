using Xunit;

namespace PipeDream.Tests;

public class Dm16SaverTests
{
    /// <summary>Paint the DM16 objects of a parsed stream into (x, y) → tile cells.</summary>
    private static Dictionary<(int X, int Y), int> Paint(IEnumerable<LevelObject> objs)
    {
        var cells = new Dictionary<(int, int), int>();
        foreach (var o in objs.Where(o => o.IsDm16))
        {
            var (w, h) = o.Dm16Size();
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                    cells[(o.AbsoluteX + i, o.Y + j)] = o.Dm16Tile;
        }
        return cells;
    }

    [Fact]
    public void brush_layout_survives_decompose_encode_and_parse()
    {
        // 3x2 brush at (20, 9): a 2x2 block of tile 0x045, one 0x1A3, one empty cell.
        ushort[] brush =
        {
            0x045, 0x045, Map16Grid.Empty,
            0x045, 0x045, 0x1A3,
        };
        var objs = Dm16Saver.FromBrush(brush, 3, 2, cx: 20, cy: 9, vert: false);

        var (rom, level) = TestRom.CreateWithLevel(dm16: true);
        var parsed = LevelParser.ParseEncoded(
            rom, LevelEncoder.Encode(level, LevelEncoder.NormalizeStream(objs)));
        var cells = Paint(parsed);

        Assert.Equal(5, cells.Count);                       // empty cell produced nothing
        Assert.Equal(0x045, cells[(20, 9)]);
        Assert.Equal(0x045, cells[(21, 9)]);
        Assert.Equal(0x045, cells[(20, 10)]);
        Assert.Equal(0x045, cells[(21, 10)]);
        Assert.Equal(0x1A3, cells[(22, 10)]);
        Assert.False(cells.ContainsKey((22, 9)));           // stamping never erases
    }

    [Fact]
    public void uniform_brush_becomes_one_maximal_rectangle_even_past_the_nibble_limit()
    {
        // 20x3 uniform brush → a single extended Form B object, layout intact.
        var brush = new ushort[20 * 3];
        Array.Fill(brush, (ushort)0x2B0);
        var objs = Dm16Saver.FromBrush(brush, 20, 3, cx: 2, cy: 4, vert: false);

        var o = Assert.Single(objs);
        Assert.Equal((20, 3), o.Dm16Size());

        var (rom, level) = TestRom.CreateWithLevel(dm16: true);
        var parsed = LevelParser.ParseEncoded(
            rom, LevelEncoder.Encode(level, LevelEncoder.NormalizeStream(objs)));
        var cells = Paint(parsed);
        Assert.Equal(20 * 3, cells.Count);
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 20; i++)
                Assert.Equal(0x2B0, cells[(2 + i, 4 + j)]);
    }

    [Fact]
    public void marker_and_bg_space_tiles_are_skipped()
    {
        ushort[] brush = { (ushort)(0x8000 | 0x25), 0x4001, 0x130 };   // marker, BG-space, real
        var objs = Dm16Saver.FromBrush(brush, 3, 1, cx: 0, cy: 0, vert: false);
        var o = Assert.Single(objs);
        Assert.Equal(0x130, o.Dm16Tile);
        Assert.Equal(2, o.AbsoluteX);
    }

    [Fact]
    public void vertical_levels_split_runs_at_the_half_screen_seam_and_use_band_screens()
    {
        // 4x1 uniform run crossing column 16 in a vertical level must split into two
        // objects (the right half is encoded via Y bit 4, not X), at row 18 → band 1.
        var brush = new ushort[4];
        Array.Fill(brush, (ushort)0x045);
        var objs = Dm16Saver.FromBrush(brush, 4, 1, cx: 14, cy: 18, vert: true);

        Assert.Equal(2, objs.Count);
        var left = objs.Single(o => o.XNibble == 14);
        var right = objs.Single(o => o.XNibble == 0);
        Assert.Equal((2, 1), left.Dm16Size());
        Assert.Equal((2, 1), right.Dm16Size());
        Assert.Equal(1, left.Screen);                       // screen = 16-row band (18 >> 4)
        Assert.Equal(1, right.Screen);
        Assert.Equal(18 & 15, left.Y);                      // left half: plain Y
        Assert.Equal((18 & 15) | 0x10, right.Y);            // right half: Y bit 4 set
    }
}
