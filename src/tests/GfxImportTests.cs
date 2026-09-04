using Xunit;

namespace PipeDream.Tests;

/// <summary>Skips at discovery when the LM reference ROM (an LM-saved hack with the GFX
/// bypass installed) is not on this machine.</summary>
public sealed class LmRomFactAttribute : FactAttribute
{
    public static string LmRomPath => ReferenceRoms.ShaoBase;

    public LmRomFactAttribute()
    {
        if (!File.Exists(LmRomPath))
            Skip = "LM reference ROM not present: " + LmRomPath;
    }
}

/// <summary>ExGFX import (stage 1): .bin bpp detection/normalization, the ImportedGfx
/// resolution seam in Gfx.Cached, and the LM-loader gate on the $0FF600 table.</summary>
public class GfxImportTests
{
    // --- bpp detection ------------------------------------------------------

    [Fact]
    public void detect_bpp_exact_full_files_then_divisibility()
    {
        Assert.Equal(4, Gfx.DetectBpp(new byte[0x1000]));   // full 128-tile 4bpp
        Assert.Equal(3, Gfx.DetectBpp(new byte[0xC00]));    // full 128-tile 3bpp
        Assert.Equal(4, Gfx.DetectBpp(new byte[64 * 32]));  // partial, divisible by 32 only
        Assert.Equal(3, Gfx.DetectBpp(new byte[5 * 24]));   // partial, divisible by 24 only
        Assert.Equal(0, Gfx.DetectBpp(new byte[0x601]));    // not whole tiles
        Assert.Equal(0, Gfx.DetectBpp(new byte[0]));        // empty
    }

    [Fact]
    public void detect_bpp_ambiguous_sizes_resolve_4bpp()
    {
        // 0x600 divides by both 32 (48 tiles) and 24 (64 tiles) — the %32 check runs
        // first, so ambiguity pins to 4bpp (except the exact full 3bpp file 0xC00).
        Assert.Equal(4, Gfx.DetectBpp(new byte[0x600]));
        Assert.Equal(4, Gfx.DetectBpp(new byte[96]));       // 96 = 3×32 = 4×24 → 4bpp
    }

    // --- bpp normalization --------------------------------------------------

    [Fact]
    public void normalize_3_to_4_round_trips_and_decodes_identically()
    {
        var rng = new Random(7);
        var src3 = new byte[3 * 24];                        // 3 tiles of random 3bpp data
        rng.NextBytes(src3);

        var up = Gfx.NormalizeBpp(src3, 3, 4, out bool dropped);
        Assert.False(dropped);
        Assert.Equal(3 * 32, up.Length);
        for (int t = 0; t < 3; t++)                          // pixel parity per DecodeTile
            Assert.Equal(Gfx.DecodeTile(src3, t * 24, 3), Gfx.DecodeTile(up, t * 32, 4));

        var down = Gfx.NormalizeBpp(up, 4, 3, out dropped);  // 3→4→3 identity
        Assert.False(dropped);
        Assert.Equal(src3, down);
    }

    [Fact]
    public void normalize_4_to_3_reports_discarded_plane3()
    {
        var src4 = new byte[32];
        src4[0] = 0x80; src4[16] = 0x80;                     // planes 0/2: survive
        var down = Gfx.NormalizeBpp(src4, 4, 3, out bool dropped);
        Assert.False(dropped);                               // plane 3 all zero → clean
        Assert.Equal(Gfx.DecodeTile(src4, 0, 4), Gfx.DecodeTile(down, 0, 3));

        src4[17] = 0x01;                                     // plane 3, row 0 → data loss
        Gfx.NormalizeBpp(src4, 4, 3, out dropped);
        Assert.True(dropped);
    }

    // --- the Cached resolution seam ------------------------------------------

    [Fact]
    public void cached_serves_imports_over_the_file_cache_and_invalidate_clears_negatives()
    {
        var rom = TestRom.Create();
        Assert.Null(Gfx.Cached(rom, 0x100));                 // absent → negative-cached

        var bytes = new byte[24]; bytes[0] = 0xAB;
        rom.ImportedGfx[0x100] = bytes;
        Gfx.InvalidateCache(rom);
        Assert.Same(bytes, Gfx.Cached(rom, 0x100));          // import now resolves

        var newer = new byte[24]; newer[0] = 0xCD;           // re-import same id
        rom.ImportedGfx[0x100] = newer;
        Gfx.InvalidateCache(rom);
        Assert.Same(newer, Gfx.Cached(rom, 0x100));          // no stale bytes

        // Imports are checked before the cache, so they win even without invalidation.
        rom.ImportedGfx[0x101] = bytes;
        Assert.Same(bytes, Gfx.Cached(rom, 0x101));
    }

    // --- LM GFX-loader gate ($00AA50 probe, CONTRACT §7d) --------------------

    [Fact]
    public void exgfx_80_table_is_ignored_without_the_lm_loader()
    {
        var rom = TestRom.Create();
        Assert.False(rom.HasLmGfxLoader);
        // Plant a plausible-looking pointer where the $0FF600 table would sit — on a
        // ROM without the loader those bytes are arbitrary data and must not be read.
        int fo = rom.FileOffset(Gfx.ExGfx80Table + (0x85 - 0x80) * 3);
        rom.Data[fo] = 0x00; rom.Data[fo + 1] = 0x80; rom.Data[fo + 2] = 0x10;   // "$108000"
        Assert.Equal(-1, Gfx.SourceSnes(rom, 0x85));

        // With the loader's JSL $0FF780 stamped at $00AA50 the same entry resolves.
        int hook = rom.FileOffset(0x00AA50);
        rom.Data[hook] = 0x22; rom.Data[hook + 1] = 0x80; rom.Data[hook + 2] = 0xF7; rom.Data[hook + 3] = 0x0F;
        Assert.True(rom.HasLmGfxLoader);
        Assert.Equal(0x108000, Gfx.SourceSnes(rom, 0x85));
    }

    /// <summary>An ExGFX Lunar Magic inserted lives in the ROM's own tables, so the browser lists
    /// it on the base side; a pointer that does not decompress to whole tiles (our prep's arm stub
    /// overlaps ids 0xFA-0xFF of the 0x80 table) is not a file and stays hidden.</summary>
    [Fact]
    public void exgfx_the_rom_itself_carries_is_a_base_candidate()
    {
        var rom = TestRom.CreateWithGfx00();                             // RomBpp = 3
        int hook = rom.FileOffset(0x00AA50);
        rom.Data[hook] = 0x22; rom.Data[hook + 1] = 0x80; rom.Data[hook + 2] = 0xF7; rom.Data[hook + 3] = 0x0F;

        // 0x85 -> a 2-tile 3bpp file: two byte-fills of 24 zeros (37 00), then FF.
        const int blob = 0x0FB000;
        int bo = rom.FileOffset(blob);
        rom.Data[bo] = 0x37; rom.Data[bo + 1] = 0x00; rom.Data[bo + 2] = 0x37; rom.Data[bo + 3] = 0x00; rom.Data[bo + 4] = 0xFF;
        int fo = rom.FileOffset(Gfx.ExGfx80Table + (0x85 - 0x80) * 3);
        rom.Data[fo] = blob & 0xFF; rom.Data[fo + 1] = blob >> 8 & 0xFF; rom.Data[fo + 2] = blob >> 16;

        // 0x86 -> code-shaped bytes: a 1-byte "file", not whole tiles.
        const int junk = 0x0FB100;
        int jo = rom.FileOffset(junk);
        rom.Data[jo] = 0x00; rom.Data[jo + 1] = 0x42; rom.Data[jo + 2] = 0xFF;   // direct copy of 1 byte
        fo = rom.FileOffset(Gfx.ExGfx80Table + (0x86 - 0x80) * 3);
        rom.Data[fo] = junk & 0xFF; rom.Data[fo + 1] = junk >> 8 & 0xFF; rom.Data[fo + 2] = junk >> 16;

        var bases = Gfx.Candidates(rom, custom: false, "");
        Assert.Contains(0x85, bases);
        Assert.DoesNotContain(0x86, bases);
        Assert.DoesNotContain(0x85, Gfx.Candidates(rom, custom: true, ""));
        Assert.Equal([0x85], Gfx.Candidates(rom, custom: false, "85"));
    }

    [RealRomFact]
    public void vanilla_rom_has_no_lm_gfx_loader()
    {
        Assert.False(Rom.Load(TestRom.RealRomPath).HasLmGfxLoader);
    }

    [LmRomFact]
    public void lm_saved_rom_has_the_gfx_loader()
    {
        var rom = Rom.Load(LmRomFactAttribute.LmRomPath);
        Assert.True(rom.HasLmGfxLoader);
        Assert.Equal(4, Gfx.RomBpp(rom));                    // LM re-normalizes GFX to 4bpp
    }

    // --- end-to-end render ---------------------------------------------------

    [Fact]
    public void imported_file_renders_through_fgtiles_when_a_slot_override_points_at_it()
    {
        var rom = TestRom.CreateWithGfx00();               // GFX00 = full 3bpp file → RomBpp 3
        Assert.Equal(3, Gfx.RomBpp(rom));

        // A recognizable 3bpp planar tile 0 in an otherwise-blank full file.
        var imported = new byte[0xC00];
        imported[0] = 0x80; imported[1] = 0x40; imported[16] = 0x20;   // planes 0/1/2, row 0
        rom.ImportedGfx[0x100] = imported;
        Gfx.InvalidateCache(rom);
        rom.GfxSlotOverrides[(TestRom.TestLevel, 7)] = 0x100;          // FG1 → page 0

        var f = Gfx.FgTiles.Load(rom, tileset: 1, TestRom.TestLevel);
        var expected = Gfx.DecodeTile(imported, 0, 3);
        Assert.Contains(expected, px => px != 0);                      // pattern is real
        Assert.Equal(expected, f.Fetch(0));                            // page 0 serves the import
    }
}
