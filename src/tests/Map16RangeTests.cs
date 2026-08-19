using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// Reading the Map16 def-lookup LADDER (CONTRACT §7a-rev). Every extended tile resolves
/// through the slot covering its own 0x1000-tile range, not through range 0's slot: a slot
/// is `ADC #imm : LDY #bank&lt;&lt;8` and def = bank:(imm + tile*8), which is 16-bit addressing
/// into a 32KB LoROM window, so 0x1000 defs is the hard per-slot ceiling.
///
/// The reader used to assume range 0's slot covered everything, which silently mis-resolved
/// every page past 0x0F in the hacks that have them.
/// </summary>
public class Map16RangeTests
{
    private static string DowPath => ReferenceRoms.InProject("DogsOfWar", "dogs_of_war.smc");

    private sealed class DowFactAttribute : FactAttribute
    {
        public DowFactAttribute()
        {
            if (!File.Exists(DowPath)) Skip = "DogsOfWar reference ROM not present: " + DowPath;
        }
    }

    /// <summary>The one sampled hack that uses the ladder's SECOND chain (ranges 4-7).</summary>
    private static string HighRangePath => ReferenceRoms.InProject(Path.Combine("Secret", "workspace"), "sgdq2024.smc");

    private sealed class HighRangeFactAttribute : FactAttribute
    {
        public HighRangeFactAttribute()
        {
            if (!File.Exists(HighRangePath)) Skip = "high-range reference ROM not present: " + HighRangePath;
        }
    }

    /// <summary>DogsOfWar populates range 1, so it is the oracle for the generalisation: its
    /// range-1 defs start at a completely different offset from its range-0 defs, which is
    /// exactly what a single-slot reader cannot express.</summary>
    [DowFact]
    public void dogs_of_war_resolves_page_0x10_through_range_1s_own_slot()
    {
        var rom = Rom.Load(DowPath);
        Assert.Equal((0xAEAE, 0x1D), rom.LmMap16Slot(0));
        Assert.Equal((0x4E5E, 0x1D), rom.LmMap16Slot(1));

        // def = bank:((imm + tile*8) & 0xFFFF) — the wrap is load-bearing for range 1.
        Assert.Equal(0x1DBEAE, rom.LmMap16DefAddr(0x200));
        Assert.Equal(0x1DCE5E, rom.LmMap16DefAddr(0x1000));
        // Reading page 0x10 through range 0's slot (the old behaviour) landed elsewhere.
        Assert.NotEqual(0x1DCE5E, (0x1D << 16) | ((0xAEAE + 0x1000 * 8) & 0xFFFF));

        Assert.True(rom.HasMap16Range(0) && rom.HasMap16Range(1));
        Assert.Equal(0, rom.LmMap16Slot(2).Bank);           // ranges 2+ unused in this hack
        Assert.True(rom.LmMap16DefAddr(0x2000) < 0);
    }

    /// <summary>Ranges 4-7 live in a second chain further along the ladder, and at least one
    /// real hack uses them — so the reader has to know all eight slot addresses, not just the
    /// four our own prep emits.</summary>
    [HighRangeFact]
    public void the_reader_knows_all_eight_slots()
    {
        var rom = Rom.Load(HighRangePath);
        Assert.Equal((0x7000, 0x89), rom.LmMap16Slot(0));
        Assert.Equal((0xC770, 0x89), rom.LmMap16Slot(4));    // tiles 0x4000-0x4FFF
        Assert.Equal((0x5778, 0x89), rom.LmMap16Slot(5));
        Assert.Equal(0, rom.LmMap16Slot(1).Bank);
        Assert.Equal(0x89C770, rom.LmMap16DefAddr(0x4000));  // (0xC770 + 0x4000*8) & 0xFFFF
    }

    /// <summary>DogsOfWar's range 0 stops well short of 0xFFF while range 1 is populated —
    /// a HOLE. The count is the editor's flat ceiling, so it must stop at the hole rather
    /// than run past it and alias unallocated tiles onto range 1's defs. Filling the hole
    /// (which is just ordinary allocation) is what makes the high pages appear.</summary>
    [DowFact]
    public void a_hole_below_a_populated_range_caps_the_count_until_it_is_filled()
    {
        var rom = Rom.Load(DowPath);
        int before = rom.Map16TileCount;
        Assert.InRange(before, 0x201, 0x1000);                  // capped by the hole
        Assert.True(Map16.DefFileOffset(rom, 0, before) < 0);   // and nothing past it resolves

        // Range 1 is readable the whole time — it is the COUNT that stops, not the reader.
        Assert.True(rom.LmMap16DefAddr(0x1000) > 0);
        var page10 = Map16.LmExtendedDef(rom, 0x1000).Select(w => w.Raw).ToArray();

        // Filling range 0 to ITS ceiling is enough: nothing about range 1 is rewritten, and
        // the hack's existing page 0x10 becomes visible to the editor by itself.
        Assert.Null(rom.EnsureMap16Tiles(0x1000));
        Assert.Equal(0x1DCE5E, rom.LmMap16DefAddr(0x1000));     // range 1's slot untouched
        Assert.True(rom.Map16TileCount > 0x1000, $"count is 0x{rom.Map16TileCount:X}");
        Assert.True(Map16.DefFileOffset(rom, 0, 0x1000) > 0);
        Assert.Equal(page10, Map16.LmExtendedDef(rom, 0x1000).Select(w => w.Raw).ToArray());

        // Growing PAST range 1's own end relocates range 1, but carries its defs along.
        Assert.Null(rom.EnsureMap16Tiles(rom.Map16TileCount + 0x100));
        Assert.Equal(page10, Map16.LmExtendedDef(rom, 0x1000).Select(w => w.Raw).ToArray());
    }

    /// <summary>Every tile below the reported count must resolve, and the first one at or past
    /// it must not — the count is what the editor trusts as a flat ceiling.</summary>
    [DowFact]
    public void the_count_is_exactly_the_resolvable_range()
    {
        var rom = Rom.Load(DowPath);
        int n = rom.Map16TileCount;
        for (int t = 0x200; t < n; t += 0x40) Assert.True(Map16.DefFileOffset(rom, 0, t) > 0, $"tile {t:X} unresolved");
        Assert.True(Map16.DefFileOffset(rom, 0, n - 1) > 0);
        Assert.True(Map16.DefFileOffset(rom, 0, n) < 0);
    }

    /// <summary>A vanilla ROM has no ladder at all: no slot reads as present, so nothing
    /// resolves and the count stays at the 0x200 vanilla defs.</summary>
    [RealRomFact]
    public void vanilla_has_no_ladder()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        for (int r = 0; r < LunarMagic.Map16RangeCount; r++)
        {
            Assert.False(rom.HasMap16Range(r));
            Assert.Equal(0, rom.LmMap16Slot(r).Bank);
        }
        Assert.Equal(0x200, rom.Map16TileCount);
        Assert.True(rom.LmMap16DefAddr(0x200) < 0);
    }

    /// <summary>Out-of-range indices are rejected rather than wrapping into a neighbouring
    /// range's defs: tile*8 is only unique mod 0x10000 within one range.</summary>
    [Fact]
    public void indices_outside_the_ladder_do_not_resolve()
    {
        var rom = TestRom.Create();
        Assert.True(rom.LmMap16DefAddr(0x1FF) < 0);       // vanilla defs, not extended
        Assert.True(rom.LmMap16DefAddr(0x8000) < 0);      // past the last slot
        Assert.True(rom.LmMap16DefAddr(-1) < 0);
    }
}
