using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The overworld reader on ROMs Lunar Magic has saved an overworld into. LM moves the layer 2
/// streams and the Map16 table and leaves the vanilla bytes where they were, so a reader that
/// trusts the vanilla addresses shows the old land under the hack's level tiles. Tables.Of
/// follows the loader's own operands instead (reference/OVERWORLD.md §11). Reference hacks,
/// skipped where they are not on disk.
/// </summary>
public class OverworldLmTests(ITestOutputHelper log)
{
    private sealed class RomFactAttribute : FactAttribute
    {
        public RomFactAttribute(string project, string file)
        {
            string p = ReferenceRoms.InProject(project, file);
            if (!File.Exists(p)) Skip = "reference ROM not present: " + p;
        }
    }

    private static Rom Dogs => Rom.Load(ReferenceRoms.InProject("DogsOfWar", "dogs_of_war.smc"));
    private static Rom BigEye => Rom.Load(ReferenceRoms.InProject("BigEye", "bigeye.smc"));

    /// <summary>On a ROM with Lunar Magic's per-tile level table, the level a tile enters is the
    /// tile's own: moving the tile takes the number along, and the build writes the table back.
    /// A vanilla fork stands in for an LM save: the two LZ2 blobs behind LM's operand pattern.</summary>
    /// <summary>A vanilla fork standing in for an LM save: the two LZ2 blobs behind LM's operand
    /// pattern at the scan, with Mario's start tile entering translevel 0x2A. Null with a note
    /// when there is no ROM to fork.</summary>
    private Services.EditorSession? LevelTableRom()
    {
        if (PreppedRom.Fork() is not { } p) { log.WriteLine("SKIP: no ROM"); return null; }
        var rom = Rom.Load(p);
        var levels = new byte[0x800];
        levels[Overworld.Layer1Index(6, 7, true)] = 0x2A;                  // Mario's start tile enters translevel 0x2A
        int levelBlob = RatsWriter.Allocate(rom, Gfx.Lz2Compress(levels));
        int highBlob = RatsWriter.Allocate(rom, Gfx.Lz2Compress(new byte[0x800]));
        int at = rom.FileOffset(0x04D7F2);
        foreach (var (blob, k) in new[] { (levelBlob, 0), (highBlob, 9) })
        {
            byte[] op = [0xA2, (byte)blob, (byte)(blob >> 8), 0x86, 0x8A, 0xA9, (byte)(blob >> 16), 0x85, 0x8C];   // LDX #addr : STX $8A : LDA #bank : STA $8C
            op.CopyTo(rom.Data, at + k);
        }
        File.WriteAllBytes(p, rom.Data);

        var session = new Services.EditorSession();
        Assert.True(session.OpenRom(p));
        Assert.True(session.Overworld!.HasLevelTable);
        return session;
    }

    [Fact]
    public void a_moved_level_tile_keeps_its_level_number_on_an_lm_rom()
    {
        if (LevelTableRom() is not { } session) return;
        var ow = session.Overworld!;
        Assert.Equal(0x2A, ow.TranslevelAt(6, 7, true));

        // The editor's cell carries the number above the tile, so a plain move of the value moves both.
        var map = session.OwLayer1!;
        int cell = map.At(6, Overworld.Rows + 7);
        Assert.Equal(0x2A, cell >> Services.EditorSession.OwLevelShift);
        Assert.True(map.Stamp(8, Overworld.Rows + 7, cell));
        Assert.True(map.Stamp(6, Overworld.Rows + 7, 0));
        Assert.True(map.EndStroke());
        Assert.Equal(cell & 0xFFFF, ow.Layer1At(8, 7, true));
        Assert.Equal(0x2A, ow.TranslevelAt(8, 7, true));
        Assert.Equal(0, ow.TranslevelAt(6, 7, true));
        Assert.True(map.Undo());
        Assert.Equal(0x2A, ow.TranslevelAt(6, 7, true));
        Assert.True(map.Redo());

        // The build packs the table back into LM's blob, where the game reads it.
        Assert.Null(Overworld.WriteLevelTable(session.Rom!, ow.Translevels));
        var written = Gfx.Lz2Decompress(session.Rom!.Data, session.Rom.FileOffset(ow.At.LevelTableBlob), 0x1000);
        Assert.Equal(0x2A, written[Overworld.Layer1Index(8, 7, true)]);
        Assert.Equal(0, written[Overworld.Layer1Index(6, 7, true)]);
    }

    /// <summary>And the dialog can set that number, since the table is the tile's own: it lands
    /// in the map the brushes share, carries the event and directions to the new level, and takes
    /// a number the overworld cannot enter no further than the report.</summary>
    [Fact]
    public void the_level_number_is_editable_where_lunar_magics_table_holds_it()
    {
        if (LevelTableRom() is not { } session) return;
        var ow = session.Overworld!;
        var tile = session.OwLevelTileAt(6, Overworld.Rows + 7)!.Value;
        Assert.True(tile.LevelEditable);
        Assert.Equal(0x106, tile.Level);                                   // translevel 0x2A

        Assert.False(session.SetOwLevelTile(tile, level: 0x1FF, baseEvent: 0, exitDirs: tile.ExitDirs));
        Assert.Contains("101-13B", session.Status);
        Assert.Equal(0x2A, ow.TranslevelAt(6, 7, true));                   // and nothing moved

        int wasAt106 = ow.BaseEventOf(0x2A);
        Assert.True(session.SetOwLevelTile(tile, level: 0x11, baseEvent: 0x0C, exitDirs: tile.ExitDirs), session.Status);
        Assert.Equal(0x11, ow.TranslevelAt(6, 7, true));
        Assert.Equal(0x11, session.OwLayer1!.At(6, Overworld.Rows + 7) >> Services.EditorSession.OwLevelShift);
        // The event followed the number to the level now in the box, not the one it left. (The
        // directions are LM's own on such a ROM, so the dialog does not offer them.)
        Assert.Equal(0x0C, ow.BaseEventOf(0x11));
        Assert.Equal(wasAt106, ow.BaseEventOf(0x2A));       // and the level it left keeps its own

        Assert.True(session.OwLayer1!.Undo());
        Assert.Equal(0x2A, ow.TranslevelAt(6, 7, true));
    }

    [Fact]
    public void vanilla_resolves_to_the_vanilla_addresses()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) { log.WriteLine("SKIP: no vanilla ROM"); return; }
        var at = Overworld.Tables.Of(Rom.Load(ReferenceRoms.Vanilla));
        Assert.Equal(new Overworld.Tables(Overworld.Layer2Low, Overworld.Layer2High, Overworld.Map16Defs, Overworld.VanillaMap16Count, 0, 0), at);
    }

    /// <summary>DogsOfWar moved layer 2 to bank $13 and kept the vanilla Map16 table.</summary>
    [RomFact("DogsOfWar", "dogs_of_war.smc")]
    public void dogs_of_war_reads_layer_2_where_lunar_magic_put_it()
    {
        var rom = Dogs;
        var at = Overworld.Tables.Of(rom);
        log.WriteLine(at.ToString());
        Assert.Equal(0x139F8E, at.Layer2Low);
        Assert.Equal(0x13B2D6, at.Layer2High);
        Assert.Equal(Overworld.Map16Defs, at.Map16Defs);
        Assert.Equal(Overworld.VanillaMap16Count, at.Map16Count);
        Assert.Equal(0x1496B0, at.LevelTableBlob);
        Assert.Equal(0x10827A, at.Layer1HighBlob);
        var ow = new Overworld(rom);
        Assert.Equal(26, ow.WarpCount);                                 // LM's hook counts them
        // LM's per-tile table numbers the tiles as the author set them, not in scan order.
        Assert.Contains(Enumerable.Range(0, 0x800), i => ow.Layer1[i] is >= 0x56 and <= 0x80 && ow.Translevels[i] > 0x40);

        // The streams decode to a full map that ends where LM's bytes end, not vanilla's.
        var words = Overworld.DecodeLayer2(rom, out int lowEnd, out int highEnd);
        Assert.Equal(4936, lowEnd - rom.FileOffset(at.Layer2Low));
        Assert.Equal(4286, highEnd - rom.FileOffset(at.Layer2High));
        if (File.Exists(ReferenceRoms.Vanilla))
        {
            var vanilla = Overworld.DecodeLayer2(Rom.Load(ReferenceRoms.Vanilla));
            int differ = words.Zip(vanilla).Count(p => p.First != p.Second);
            log.WriteLine($"{differ} of {words.Length} layer 2 words differ from vanilla");
            Assert.True(differ > 0x400, "the hack's land should not read as vanilla's");
        }
    }

    /// <summary>BigEye moved the Map16 table to bank $15 and grew it to two pages.</summary>
    [RomFact("BigEye", "bigeye.smc")]
    public void bigeye_reads_the_two_page_map16_table()
    {
        var rom = BigEye;
        var ow = new Overworld(rom);
        log.WriteLine(ow.At.ToString());
        Assert.Equal(0x15A42C, ow.At.Map16Defs);
        Assert.Equal(0x200, ow.Map16Count);
        Assert.Equal(0x12B1D3, ow.At.Layer2Low);
        Assert.Equal(0x10F08C, ow.At.Layer1HighBlob);
        Assert.Equal(0x800, ow.Layer1.Length);
        Assert.Equal(256, ow.Map16Pixels(0x1FF, 0).Length);          // page 1 is addressable
        Assert.All(ow.Layer1, t => Assert.InRange(t, 0, 0x1FF));
        Assert.Equal(0, ow.WarpCount);
        Assert.Empty(ow.Warps);
    }
}
