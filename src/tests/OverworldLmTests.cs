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
