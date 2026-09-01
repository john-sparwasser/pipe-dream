using PipeDream;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Where the layer-3 uploads LAND, not just what they contain. The SNES VRAM map SMW sets up in
/// `SetUpScreen` puts layer 3's character data and tilemap immediately below layer 1's tilemap:
///
///   words $0000-$3FFF  layer 1/2 character data (the 8x8 tiles the level is drawn from)
///   words $4000-$4FFF  layer 3 character data   (BG34NBA $04, 512 tiles of 2bpp)
///   words $5000-$5FFF  layer 3 tilemap          (BG3SC $53, 64x64)
///   words $6000-$7FFF  layer 1 / layer 2 tilemaps
///
/// So a layer-3 upload that overruns its window by one word repaints layer 1 — either its
/// graphics (below $4000) or its tilemap (at $6000), and the second shows up as one tile
/// repeating in a grid. Every earlier layer-3 test asserted the BYTES via VramLog, which is blind
/// to this: an upload that fits and one that overruns produce identical bytes.
/// </summary>
public class Layer3VramBoundsTests(ITestOutputHelper log)
{
    private const int Level = 5;
    private const int L3CharLo = 0x4000, L3MapLo = 0x5000, L1MapLo = 0x6000;

    private static (Rom Rom, int Rec) Prepped()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom);
        int fo = rom.FileOffset(RomPrep.GfxBypassRecords + Level * 0x20);
        for (int w = 0; w < 16; w++) { rom.Data[fo + w * 2] = 0x7F; rom.Data[fo + w * 2 + 1] = 0; }
        return (rom, fo);
    }

    private void Report(string what, List<(int Word, int Value)> writes)
    {
        if (writes.Count == 0) { log.WriteLine($"{what}: no VRAM writes"); return; }
        int lo = writes.Min(w => w.Word), hi = writes.Max(w => w.Word);
        log.WriteLine($"{what}: {writes.Count} words, ${lo:X4}-${hi:X4}");
        foreach (var g in writes.GroupBy(w => w.Word >> 12).OrderBy(g => g.Key))
            log.WriteLine($"    page ${g.Key:X}000: {g.Count()} words "
                        + $"(${g.Min(w => w.Word):X4}-${g.Max(w => w.Word):X4})");
    }

    /// <summary>The GFX stage (which ends in v14's layer-3 pass) must touch layer 3's character
    /// window and nothing else — in particular nothing below $4000, which is layer 1's graphics.</summary>
    [RealRomFact]
    public void the_layer3_gfx_pass_stays_inside_layer_3s_character_window()
    {
        var (rom, fo) = Prepped();
        rom.Data[fo + 1] = 0x40;                                    // w0 bit 14: layer-3 bypass on
        rom.Data[fo + 12 * 2] = 0x00;                               // LG4 → GFX 00, a real file

        var cpu = new Cpu65816(rom) { VramWrites = [] };
        cpu.Ram7E[0xFE] = Level + 1;
        cpu.Ram7E[0x1931] = 0;
        cpu.CallLong(RomPrep.GfxLoaderEntry, 40_000_000);

        var l3 = cpu.VramWrites!.Where(w => w.Word >= L3CharLo).ToList();
        Report("layer-3 GFX pass", l3);
        Assert.NotEmpty(l3);
        Assert.All(l3, w => Assert.InRange(w.Word, L3CharLo, L3MapLo - 1));
        // ...and nothing at all in layer 1's tilemap.
        Assert.Empty(cpu.VramWrites!.Where(w => w.Word >= L1MapLo));
    }

    /// <summary>
    /// Where the tilemap DECOMPRESSION reaches in RAM, which is a different question from where
    /// the copy reaches in VRAM and was the actual bug: a tilemap file is 0x2000 bytes, twice any
    /// GFX file, and the shared $7E:AD00 buffer only has room to $7EBCFF (a 4bpp file fills it
    /// exactly — §V13). Decompressing 0x2000 there ran to $7ECCFF, straight through the layer-2
    /// map at $7E:B900, its page plane at $7E:BD00 and the LAYER-1 Map16 map at $7E:C800, so the
    /// level came up with one Map16 tile repeating in a grid.
    ///
    /// Nothing in VRAM was wrong, which is why every other layer-3 test passed while the level
    /// was visibly broken. From v16 the tilemap has its own buffer at $7F:A000.
    /// </summary>
    [RealRomFact]
    public void the_tilemap_decompression_does_not_reach_the_levels_map16_maps()
    {
        var (rom, fo) = Prepped();
        var raw = new byte[0x2000];
        for (int i = 0; i < raw.Length; i += 2) { raw[i] = 0x80; raw[i + 1] = 0x2D; }
        int snes = RatsWriter.Allocate(rom, Gfx.Lz2Compress(raw), avoidBankCross: false);
        int pf = rom.FileOffset(Gfx.ExGfx80Table);
        rom.Data[pf] = (byte)snes; rom.Data[pf + 1] = (byte)(snes >> 8); rom.Data[pf + 2] = (byte)(snes >> 16);

        rom.Data[fo + 1] = 0x20;                                    // w0 bit 13 = tilemap bypass
        int w1 = 0x080 | Layer3.BuiltTilemapDestination << 14;      // ExGFX 0x80, size 0x2000
        rom.Data[fo + 2] = (byte)w1; rom.Data[fo + 3] = (byte)(w1 >> 8);

        var cpu = new Cpu65816(rom) { VramWrites = [] };
        cpu.Ram7E[0xFE] = Level + 1;
        cpu.Ram7E[0x1931] = 0;
        cpu.Ram7E[0x1BE3] = 3;
        cpu.Ram7E[0x010B] = Level; cpu.Ram7E[0x010C] = 0;
        cpu.PresetWidths(m8: true, x8: true);
        cpu.CallLong(RomPrep.L3Map, 60_000_000);

        Assert.NotEmpty(cpu.VramWrites!);                           // it really did run

        // The three RAM regions the old shared buffer ran through.
        foreach (var (name, at) in new[] { ("layer-2 map $7E:B900", 0xB900),
                                          ("layer-2 page $7E:BD00", 0xBD00),
                                          ("layer-1 map $7E:C800", 0xC800) })
        {
            int hits = 0;
            for (int a = at; a < at + 0x400; a++) if (cpu.Ram7E[a] != 0) hits++;
            log.WriteLine($"  {name}: {hits} bytes written");
            Assert.Equal(0, hits);
        }
        // ...and it landed in its own buffer instead.
        int used = 0;
        for (int a = 0xA000; a <= 0xBFFF; a++) if (cpu.Ram7F[a] != 0) used++;
        log.WriteLine($"  own buffer $7F:A000: {used} bytes written");
        Assert.True(used > 0x1000, $"the tilemap should decompress into its own buffer, saw {used}");
    }

    /// <summary>
    /// A tilemap the EDITOR built must not land on the status bar. The bar is the first 32x5
    /// words of the same window ($5000-$509F), so "Start of Layer 3" at the full 0x2000 copies
    /// the map's own top five rows over the score, coins, time and lives — and a custom layer 3
    /// usually sets the priority bit, so it covers them rather than blending. That reached a
    /// real project as "the HUD is showing all kinds of characters"; the build uses "Under
    /// Status Bar" instead, whose offset applies to the source as well, so nothing shifts.
    /// </summary>
    [RealRomFact]
    public void a_built_tilemap_leaves_the_status_bars_own_words_alone()
    {
        var (rom, fo) = Prepped();
        var raw = new byte[0x2000];
        for (int i = 0; i < raw.Length; i += 2) { raw[i] = 0x80; raw[i + 1] = 0x2D; }   // priority set
        int snes = RatsWriter.Allocate(rom, Gfx.Lz2Compress(raw), avoidBankCross: false);
        int pf = rom.FileOffset(Gfx.ExGfx80Table);
        rom.Data[pf] = (byte)snes; rom.Data[pf + 1] = (byte)(snes >> 8); rom.Data[pf + 2] = (byte)(snes >> 16);

        rom.Data[fo + 1] = 0x20;                                    // w0 bit 13: tilemap bypass on
        int w1 = 0x080 | Layer3.BuiltTilemapDestination << 14;      // ExGFX 0x80, size 0x2000
        rom.Data[fo + 2] = (byte)w1; rom.Data[fo + 3] = (byte)(w1 >> 8);

        var cpu = new Cpu65816(rom) { VramWrites = [] };
        cpu.Ram7E[0xFE] = Level + 1;
        cpu.Ram7E[0x1931] = 0;
        cpu.Ram7E[0x010B] = Level; cpu.Ram7E[0x010C] = 0;
        cpu.Ram7E[0x1BE3] = 3;
        cpu.PresetWidths(m8: true, x8: true);
        cpu.CallLong(RomPrep.L3Map, 60_000_000);

        var writes = cpu.VramWrites!;
        Report("built tilemap", writes);
        Assert.NotEmpty(writes);
        Assert.DoesNotContain(writes, w => w.Word < 0x50A0);
        // ...and the row AFTER the bar is still the file's own row 5, not shifted up into it.
        int row5 = Layer3.CellIndex(0, 5);
        Assert.Contains(writes, w => w.Word == L3MapLo + row5);
    }

    /// <summary>
    /// The tilemap copy, at every one of LM's four destinations and every size. This is the one
    /// with real overrun risk: the length is computed in BYTES and the loop counts WORDS, and the
    /// full 0x2000 size at "Start of Layer 3" is designed to end EXACTLY at $6000 — one word past
    /// is layer 1's tilemap.
    /// </summary>
    [RealRomFact]
    public void the_tilemap_copy_never_reaches_layer_1s_tilemap()
    {
        for (int size = 0; size < 3; size++)                        // 0x2000 / 0x1000 / 0x800
            for (int dest = 0; dest < 4; dest++)
            {
                var (rom, fo) = Prepped();
                int bytes = Layer3.TilemapSizes[size];
                var raw = new byte[bytes];
                for (int i = 0; i < bytes; i++) raw[i] = (byte)(i * 5 + 1);
                int snes = RatsWriter.Allocate(rom, Gfx.Lz2Compress(raw), avoidBankCross: false);
                int pf = rom.FileOffset(Gfx.ExGfx80Table);
                rom.Data[pf] = (byte)snes; rom.Data[pf + 1] = (byte)(snes >> 8); rom.Data[pf + 2] = (byte)(snes >> 16);

                rom.Data[fo + 1] = 0x20;                            // w0 bit 13: tilemap bypass on
                int w1 = 0x080 | (size & 3) << 12 | dest << 14;      // ExGFX 0x80
                rom.Data[fo + 2] = (byte)w1; rom.Data[fo + 3] = (byte)(w1 >> 8);

                var cpu = new Cpu65816(rom) { VramWrites = [] };
                cpu.Ram7E[0xFE] = Level + 1;
                cpu.Ram7E[0x1931] = 0;
                cpu.Ram7E[0x010B] = Level; cpu.Ram7E[0x010C] = 0;
                cpu.Ram7E[0x1BE3] = 3;                              // a layer-3 option that loads
                cpu.PresetWidths(m8: true, x8: true);
                cpu.CallLong(RomPrep.L3Map, 40_000_000);

                var writes = cpu.VramWrites!;
                Report($"size 0x{bytes:X} dest {dest} ({Layer3.TilemapDestinations[dest]})", writes);
                if (writes.Count == 0) continue;
                Assert.All(writes, w => Assert.InRange(w.Word, L3MapLo, L1MapLo - 1));
            }
    }
}
