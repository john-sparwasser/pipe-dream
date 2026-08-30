using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Tests;

/// <summary>
/// Lunar Magic's variable level height, end to end: the height byte in the entrance record
/// (prep v10's transplanted engine reads it), 32-row bands in the object stream (ext 01's X
/// nibble / ext 03's Y), and the editor's grid sized to the level rather than to 27 rows.
/// </summary>
public class LevelHeightTests(ITestOutputHelper log)
{
    private static string DogsOfWar => ReferenceRoms.InProject("DogsOfWar", "dogs_of_war.smc");

    /// <summary>Bands are stream plumbing like screens: the encoder emits a jump whenever one
    /// changes, ext 01 with the band in X below 16, ext 03 above, and the parser reads both
    /// back into <see cref="LevelObject.Band"/>.</summary>
    [RealRomFact]
    public void bands_round_trip_through_the_stream_as_jumps()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom, 10);
        var level = LevelParser.Parse(rom, 0x105);
        var objs = new List<LevelObject>
        {
            LevelObject.MakeDm16(0x130, 0, 3, 5),                       // row 5
            LevelObject.MakeDm16(0x130, 0, 4, 8, band: 1),              // row 40
            LevelObject.MakeDm16(0x130, 0, 5, 2, band: 20),             // row 642 (one-column territory)
        };
        var norm = LevelEncoder.NormalizeStream(objs);
        Assert.Equal(5, norm.Count);                                     // two jumps inserted
        Assert.True(norm[1].IsScreenJump && norm[1].Byte3 == 0x01 && norm[1].XNibble == 1);
        Assert.True(norm[3].IsScreenJump && norm[3].Byte3 == 0x03 && norm[3].Y == 20);

        var back = LevelParser.ParseEncoded(rom, LevelEncoder.Encode(level, norm));
        Assert.Equal([5, 40, 642], back.Where(o => o.IsDm16).Select(o => o.AbsoluteY));
        Assert.All(back.Where(o => o.IsDm16), o => Assert.Equal(0, o.Screen));
    }

    /// <summary>A level given LM's height 0x17 (0x950 px, 149 rows): the engine renders a grid of
    /// that height, and an object placed in band 1 lands on row 40 of it — run through the game's
    /// own object loader on the prepped base, so this is what the game will show.</summary>
    [RealRomFact]
    public void a_taller_level_renders_to_its_height_and_places_past_row_31()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom, 10);
        var e = rom.ReadMainEntrance(0x105);
        rom.WriteMainEntrance(0x105, e with { HeightIndex = 0x17 });
        Assert.Equal(149, rom.LevelHeightRows(0x105));

        var level = LevelParser.Parse(rom, 0x105);
        var grid = ObjectEngine.Render(rom, level);
        Assert.Equal(149, grid.Height);
        Assert.Equal(6 * 16, grid.Width);                                // 0x3800 / 0x950 columns

        var objs = new List<LevelObject> { LevelObject.MakeDm16(0x130, 0, 3, 8, band: 1) };
        var offs = new List<int>();
        byte[] enc = LevelEncoder.Encode(level, LevelEncoder.NormalizeStream(objs), offs);
        var so = new ushort[enc.Length];
        for (int b = offs[^1]; b < enc.Length - 1; b++) so[b] = 1;   // the DM16 is the last record
        ObjectEngine.RenderEmulatedStream(rom, level.Header, enc, 0, so, out var owners, out _, 30_000_000, 149);
        Assert.Equal(1, owners!.Get(3, 40));
        Assert.NotEqual(1, owners.Get(3, 8));
    }

    /// <summary>DogsOfWar is the only reference ROM with tall levels; its $109 is one column of
    /// 896 rows and $10F three columns of 149. Parsed with bands and rendered by its own engine,
    /// the tiles reach the bottom — the same numbers the --tallprobe oracle produced.</summary>
    [Fact]
    public void dogsofwar_tall_levels_render_to_their_full_height()
    {
        if (!File.Exists(DogsOfWar)) { log.WriteLine("SKIP: DogsOfWar not present"); return; }
        var rom = Rom.Load(DogsOfWar);
        Assert.True(rom.HasLmLevelHeight);
        foreach (var (lvl, rows, cols, deepest) in new[] { (0x109, 896, 1, 895), (0x10F, 149, 6, 148) })
        {
            Assert.Equal(rows, rom.LevelHeightRows(lvl));
            var level = LevelParser.Parse(rom, lvl);
            Assert.Contains(level.Objects, o => o.Band > 0);
            var grid = ObjectEngine.Render(rom, level);
            Assert.Equal((cols * 16, rows), (grid.Width, grid.Height));
            int last = -1;
            for (int y = 0; y < grid.Height; y++)
                for (int x = 0; x < grid.Width; x++)
                    if (grid.Get(x, y) != Map16Grid.Empty && grid.Get(x, y) != 0x25) last = y;
            Assert.Equal(deepest, last);
        }
    }

    /// <summary>LM's extended sprite list (header bit 5): `FF nn` sets a 32-row band for the sprites
    /// that follow, `FF FE` ends it. DogsOfWar's tall levels are the ground truth — parse them and
    /// encode back byte for byte, and read the bands the game places them in.</summary>
    [Fact]
    public void extended_sprite_lists_round_trip_and_carry_bands()
    {
        if (!File.Exists(DogsOfWar)) { log.WriteLine("SKIP: DogsOfWar not present"); return; }
        var rom = Rom.Load(DogsOfWar);
        foreach (var (lvl, count, firstBand) in new[] { (0x109, 15, 0x18), (0x10F, 8, 0), (0x11F, 1, 5) })
        {
            var sd = SpriteData.Parse(rom, lvl);
            Assert.True(sd.ExtendedList);
            Assert.Equal(count, sd.Sprites.Count);
            Assert.Equal(firstBand, sd.Sprites[0].Band);
            byte[] enc = sd.Encode();
            int p = rom.FileOffset(rom.SpritePointer(lvl));
            Assert.Equal(rom.Data.AsSpan(p, enc.Length).ToArray(), enc);        // exact inverse, LM's bytes
            Assert.Equal(0xFE, enc[^1]);
        }
        var s = SpriteData.Parse(rom, 0x109).Sprites[0];
        Assert.Equal(0x18 * 32 + s.Y, s.AbsoluteY);
        Assert.Equal((s.AbsoluteX, 0x18 * 32 + s.Y), s.Cell(vertical: false));
    }

    /// <summary>A vanilla list stays vanilla (no bit 5, plain FF end); a sprite placed in a band
    /// turns the list extended and comes back at its row.</summary>
    [RealRomFact]
    public void placing_a_sprite_below_row_31_makes_the_list_extended()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var sd = SpriteData.Parse(rom, 0x105);
        Assert.False(sd.ExtendedList);
        byte[] vanilla = sd.Encode();
        Assert.Equal(0xFF, vanilla[^1]);
        Assert.NotEqual(0xFE, vanilla[^2]);

        sd.Sprites.Add(Services.SpriteEdit.At(0x0F, 0, cx: 20, cy: 40, vert: false));
        Assert.Equal(1, sd.Sprites[^1].Band);
        Assert.Equal(8, sd.Sprites[^1].Y);
        byte[] enc = sd.Encode();
        Assert.Equal(0x20, enc[0] & 0x20);
        Assert.Equal([0xFF, 0x01], enc[^7..^5]);                               // the band marker before it
        Assert.Equal([0xFF, 0xFE], enc[^2..]);
    }

    /// <summary>The OAM capture runs the game's own sprite loader on a synthetic one-record list;
    /// for a row past 31 that list has to be LM's extended form with a band marker, and $0A the band
    /// LM's loader keeps for the Y high byte — or the sprite spawns 512px too high.</summary>
    [RealRomFact]
    public void a_sprite_captured_in_a_band_draws_at_its_row()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom, 10);
        var oam = SpriteRender.Capture(rom, new Sprite(1, 4, 8, 0, 0x0F, Band: 1), cellX: 20, cellY: 40, vertical: false, heightRows: 149);
        Assert.NotNull(oam);
        Assert.All(oam!, o => Assert.InRange(o.Y, 40 * 16 - 32, 40 * 16 + 32));
        var top = SpriteRender.Capture(rom, new Sprite(1, 4, 8, 0, 0x0F), cellX: 20, cellY: 8, vertical: false, heightRows: 149);
        Assert.NotNull(top);
        Assert.All(top!, o => Assert.InRange(o.Y, 8 * 16 - 32, 8 * 16 + 32));
    }
}
