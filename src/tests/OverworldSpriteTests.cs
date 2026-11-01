using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// Lunar Magic's custom overworld sprite list ($0EF55D), read, packed and written back. The entries
/// are built here with the help file's own macro (reference/lm-help/html/info_custom_ov_sprite.htm),
/// so the reader is checked against LM's documentation rather than against our packer.
/// </summary>
public class OverworldSpriteTests
{
    /// <summary>%spr(id, x, y, z, extra) without the extra bytes.</summary>
    private static byte[] Spr(int id, int x, int y, int z)
        => [(byte)(id | (x & 1) << 7), (byte)(x >> 1 | (y & 7) << 5), (byte)(y >> 3 | z << 3)];

    private static void Pointer(Rom rom, int at, int value)
    {
        int p = rom.FileOffset(at);
        rom.Data[p] = (byte)value; rom.Data[p + 1] = (byte)(value >> 8); rom.Data[p + 2] = (byte)(value >> 16);
    }

    /// <summary>The help file's example: nothing on the main map, one sprite on Yoshi's Island.
    /// No size table, so the entry is four bytes.</summary>
    [Fact]
    public void reads_the_list_as_the_lm_help_file_lays_it_out()
    {
        var rom = TestRom.Create(0x100000);
        var block = new List<byte>(new byte[14]);
        for (int m = 0; m < 7; m++)
        {
            block[2 * m] = (byte)block.Count;
            if (m == 1) { block.AddRange(Spr(0x12, 0x2B, 0x15, 3)); block.Add(0xA5); }
            block.AddRange([0, 0]);
        }
        int at = RatsWriter.Allocate(rom, [.. block]);
        Pointer(rom, Overworld.SpriteList, at);

        var s = Assert.Single(Overworld.ReadSprites(rom));
        Assert.Equal((1, 0x12, 0x2B, 0x15, 3), (s.Submap, s.Number, s.X, s.Y, s.Z));
        Assert.Equal([0xA5], s.Extra);
        Assert.Equal([.. block], Overworld.PackSprites(rom, [s]));        // packs back byte for byte
    }

    [Fact]
    public void a_vanilla_rom_has_no_sprites_and_an_empty_list_writes_vanilla_bytes()
    {
        var rom = TestRom.Create(0x100000);
        Pointer(rom, Overworld.SpriteList, 0xFFFFFF);
        Assert.Empty(Overworld.ReadSprites(rom));
        Assert.Null(Overworld.WriteSprites(rom, []));
        Assert.Equal(0xFF, rom.Data[rom.FileOffset(Overworld.SpriteList) + 2]);
    }

    /// <summary>With the $0DE18C table (LM 3.60+: indexed by number - 1) each number takes its own
    /// length, and a short Extra is padded to it.</summary>
    [Fact]
    public void the_size_table_sets_each_numbers_extra_bytes()
    {
        var rom = TestRom.Create(0x100000);
        var sizes = Enumerable.Repeat((byte)3, 0x7F).ToArray();
        sizes[5 - 1] = 6;                                                   // sprite 05: three extra bytes
        Pointer(rom, Overworld.SpriteSizes, RatsWriter.Allocate(rom, sizes));
        rom.Data[rom.FileOffset(Overworld.SpriteSizes) + 3] = 0x42;
        Assert.Equal(3, Overworld.SpriteExtraCount(rom, 5));
        Assert.Equal(0, Overworld.SpriteExtraCount(rom, 6));

        List<Overworld.OwSprite> list = [new(0, 5, 1, 2, 0, [0x11]), new(0, 6, 3, 4, 0, []), new(6, 5, 63, 63, 31, [1, 2, 3])];
        Assert.Null(Overworld.WriteSprites(rom, list));
        var back = Overworld.ReadSprites(rom);
        Assert.Equal(3, back.Count);
        Assert.Equal([0x11, 0, 0], back[0].Extra);
        Assert.Equal((6, 63, 63, 31), (back[2].Submap, back[2].X, back[2].Y, back[2].Z));
        Assert.Equal([1, 2, 3], back[2].Extra);
    }

    /// <summary>A rewrite releases the old block, so building twice does not leak freespace.</summary>
    [Fact]
    public void rewriting_the_list_reuses_its_freespace()
    {
        var rom = TestRom.Create(0x100000);
        Pointer(rom, Overworld.SpriteList, 0xFFFFFF);
        List<Overworld.OwSprite> list = [new(0, 1, 10, 10, 0, [0])];
        Assert.Null(Overworld.WriteSprites(rom, list));
        int first = rom.Data[rom.FileOffset(Overworld.SpriteList)] | rom.Data[rom.FileOffset(Overworld.SpriteList) + 1] << 8;
        Assert.Null(Overworld.WriteSprites(rom, list));
        int second = rom.Data[rom.FileOffset(Overworld.SpriteList)] | rom.Data[rom.FileOffset(Overworld.SpriteList) + 1] << 8;
        Assert.Equal(first, second);
    }

    /// <summary>A sprite's X and Y are its own map's, and the submap map sits shifted and wrapped
    /// on the canvas: every position comes back through the canvas's own cell lookup.</summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 63, 63)]
    [InlineData(3, 0, 0)]
    [InlineData(5, 63, 63)]
    public void a_sprites_canvas_cell_maps_back_to_its_position(int submap, int x, int y)
    {
        var (col, row) = Services.EditorSession.OwSpriteCell(new(submap, 1, x, y, 0, []));
        Assert.True(Services.EditorSession.OwMapCell(col, row, out int cx, out int cy, out bool sub));
        Assert.Equal((x, y, submap != 0), (cx, cy, sub));
    }
}
