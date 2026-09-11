using Xunit;

namespace PipeDream.Tests;

/// <summary>Map16 page allocation. The editor grows the extended def region on demand, so
/// this has to work on a PREPPED VANILLA base and not just on an LM-saved ROM (the self-check
/// only ever exercised the latter).</summary>
public class Map16AllocTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pdm16-" + Guid.NewGuid().ToString("N")[..8]);

    public Map16AllocTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    [RealRomFact]
    public void allocation_works_on_a_prepped_vanilla_base()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var rom = Rom.Load(p.BaseRomPath);
        Assert.NotEqual(0, rom.LmMap16Defs.Bank);              // prep provides the hijack
        // Prep ships a 0x800-byte def block = 0x100 extended tiles, so pages 00-02 exist.
        Assert.Equal(0x300, rom.Map16TileCount);
        Assert.True(Map16.DefFileOffset(rom, 0, 0x400) < 0);   // page 04 not yet there

        Assert.Null(rom.EnsureMap16Tiles(0x500));
        Assert.Equal(0x500, rom.Map16TileCount);

        // A tile on a newly allocated page must resolve to a writable def, and read back.
        int fo = Map16.DefFileOffset(rom, 0, 0x400);
        Assert.True(fo > 0, "newly allocated tile 0x400 has no def offset");
        rom.Data[fo] = 0x34; rom.Data[fo + 1] = 0x12;
        Assert.Equal(0x1234, rom.Data[fo] | (rom.Data[fo + 1] << 8));

        // Tiles the editor still cannot reach must stay unresolved rather than aliasing.
        Assert.True(Map16.DefFileOffset(rom, 0, 0x500) < 0);
    }

    /// <summary>Asking for a count the ROM already has is a no-op, not a failure — the
    /// auto-allocate path leans on that when a page is already there.</summary>
    [RealRomFact]
    public void allocating_up_to_the_existing_count_is_a_silent_no_op()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var rom = Rom.Load(p.BaseRomPath);
        int before = rom.Map16TileCount, sizeBefore = rom.ActualRomSize;
        Assert.Null(rom.EnsureMap16Tiles(before));
        Assert.Null(rom.EnsureMap16Tiles(0x100));
        Assert.Equal(before, rom.Map16TileCount);
        Assert.Equal(sizeBefore, rom.ActualRomSize);           // no bank wasted
    }

    /// <summary>Both FG banks (tiles 0x200-0x3FFF) can be created — four lookup-ladder ranges,
    /// which is also exactly what LM's Direct-Map16 objects can address (6-bit page byte).
    /// Bank 2 is the fixed BG table and can never grow.</summary>
    [Fact]
    public void both_fg_banks_can_be_created_but_the_bg_table_cannot()
    {
        Assert.True(Map16Layout.CanAllocate(0x200));      // first extended tile
        Assert.True(Map16Layout.CanAllocate(0xFFF));      // last of range 0
        Assert.True(Map16Layout.CanAllocate(0x1000));     // range 1 — the page 0x10 wall is gone
        Assert.True(Map16Layout.CanAllocate(0x2000));     // bank 1 / range 2
        Assert.True(Map16Layout.CanAllocate(0x3FFF));     // last level-placeable tile
        Assert.False(Map16Layout.CanAllocate(0x1FF));     // vanilla defs already exist
        Assert.False(Map16Layout.CanAllocate(0x4000));    // bank 2 = the fixed BG table
    }

    [Fact]
    public void empty_pages_describe_themselves_without_promising_allocation()
    {
        // FG banks: an empty page is just empty tiles, filled by painting — never an
        // "unlock" the user has to perform first.
        Assert.Contains("paint", Map16Layout.UnusedPageNote(0, 0x05));
        Assert.Contains("paint", Map16Layout.UnusedPageNote(0, 0x10));
        Assert.Contains("paint", Map16Layout.UnusedPageNote(1, 0x20));
        Assert.All([Map16Layout.UnusedPageNote(0, 0x05), Map16Layout.UnusedPageNote(1, 0x20)],
                   s => Assert.DoesNotContain("creat", s));   // nothing to create, nothing to click
        // Bank 2 is the BG table — fixed size, so it is a different explanation entirely.
        Assert.Contains("BG", Map16Layout.UnusedPageNote(2, 0x42));
        Assert.DoesNotContain("paint", Map16Layout.UnusedPageNote(2, 0x42));
    }

    [RealRomFact]
    public void an_unprepped_vanilla_rom_reports_why_allocation_is_impossible()
    {
        var rom = Rom.Load(TestRom.RealRomPath);               // raw vanilla: no LM hijack
        Assert.Equal(0, rom.LmMap16Defs.Bank);
        Assert.Contains("Lunar Magic", rom.EnsureMap16Tiles(0x300));
    }

    [RealRomFact]
    public void the_supported_ceiling_is_reported_rather_than_silently_clamped()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var rom = Rom.Load(p.BaseRomPath);
        Assert.Contains("0x7FFF", rom.EnsureMap16Tiles(0x8001) ?? "");
        Assert.Null(rom.EnsureMap16Tiles(0x1000));             // range 0's ceiling is unremarkable now
    }

    /// <summary>The point of prep v3: a page past 0xF needs its OWN lookup slot and its own
    /// bank, because def = bank:(imm + tile*8) is 16-bit addressing into a 32KB window — one
    /// slot can never cover more than 0x1000 tiles. Growing across the boundary must therefore
    /// leave BOTH ranges readable and independently writable.</summary>
    [RealRomFact]
    public void pages_past_0x0F_allocate_into_their_own_range()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var rom = Rom.Load(p.BaseRomPath);
        Assert.True(Map16.DefFileOffset(rom, 0, 0x1000) < 0);       // page 0x10 absent to begin with

        Assert.Null(rom.EnsureMap16Tiles(0x1100));
        // 0x1100 rather than 0x1000 proves range 0 was filled to ITS ceiling on the way past:
        // the count stops at the first hole, so a gap anywhere below would cap it lower.
        Assert.Equal(0x1100, rom.Map16TileCount);

        // Different ranges land in different banks, and neither aliases the other.
        int lo = Map16.DefFileOffset(rom, 0, 0xFFF), hi = Map16.DefFileOffset(rom, 0, 0x1000);
        Assert.True(lo > 0 && hi > 0);
        Assert.NotEqual(rom.LmMap16Slot(0).Bank, rom.LmMap16Slot(1).Bank);
        rom.Data[lo] = 0xAA; rom.Data[hi] = 0x55;
        Assert.Equal(0xAA, rom.Data[lo]);
        Assert.Equal(0x55, rom.Data[hi]);
        Assert.Equal(0x55, rom.Data[Map16.DefFileOffset(rom, 0, 0x1000)]);

        // Tile 0x1100 is still past the end — growth is page-granular, not bank-granular.
        Assert.True(Map16.DefFileOffset(rom, 0, 0x1100) < 0);
    }

    /// <summary>Prep v3 emits ranges 0-3 and stops; that is what makes the write path refuse
    /// a range the base's in-game lookup would never reach, instead of writing defs that
    /// render blank on hardware.</summary>
    [RealRomFact]
    public void the_base_declares_which_ranges_its_lookup_reaches()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var rom = Rom.Load(p.BaseRomPath);
        for (int r = 0; r < 4; r++) Assert.True(rom.HasMap16Range(r), $"range {r} missing");
        Assert.False(rom.HasMap16Range(4));
        Assert.False(rom.HasMap16Range(7));
        Assert.Contains("0x3FFF", rom.EnsureMap16Tiles(0x4001) ?? "");
    }

    /// <summary>A project stores extended defs keyed by TILE NUMBER, because the region moves
    /// on every allocation — so replaying one into a fresh base has to allocate first and
    /// resolve offsets after. Four-hex-digit keys (tile 0x1000+) must survive that round trip
    /// exactly like three-digit ones.</summary>
    [RealRomFact]
    public void a_high_page_edit_replays_into_a_freshly_prepped_build()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        p.Data.Map16.TileCount = 0x1100;
        p.Data.Map16.Ext[0x1005.ToString("X3")] = "AABBCCDDEEFF0011";
        p.Data.Map16.Ext[0x0205.ToString("X3")] = "1122334455667788";

        var rom = Rom.Load(p.BaseRomPath);
        Assert.Null(RomBuilder.ReplayMap16(rom, p.Data));
        Assert.True(rom.Map16TileCount >= 0x1100);

        Assert.Equal("AABBCCDDEEFF0011",
                     Convert.ToHexString(rom.Data.AsSpan(Map16.DefFileOffset(rom, 0, 0x1005), 8)));
        Assert.Equal("1122334455667788",
                     Convert.ToHexString(rom.Data.AsSpan(Map16.DefFileOffset(rom, 0, 0x0205), 8)));
    }

    /// <summary>A prep-v2 base only has range 0, so asking for a high page must produce the
    /// upgrade hint rather than a silent no-op or a corrupt patch of a slot that isn't there.</summary>
    [RealRomFact]
    public void a_prep_v2_base_is_told_to_upgrade_rather_than_half_allocating()
    {
        var tmp = Path.Combine(dir, "v2.smc");
        File.Copy(TestRom.RealRomPath, tmp);
        Assert.Null(RomPrep.PrepInPlace(tmp, 2));
        var rom = Rom.Load(tmp);
        Assert.True(rom.HasMap16Range(0));
        Assert.False(rom.HasMap16Range(1));
        Assert.Null(rom.EnsureMap16Tiles(0x1000));              // range 0 still grows normally
        Assert.Contains("upgrade", rom.EnsureMap16Tiles(0x1100) ?? "");
        Assert.Equal(0x1000, rom.Map16TileCount);               // and nothing was half-written
    }

    /// <summary>
    /// The ladder stops at the BG boundary. LM numbers its BG pages 0x8000+ and this editor
    /// renumbers them 0x4000+, while LM's own 0x4000-0x7FFF are more FG tiles reached through the
    /// ladder's SECOND chain (ranges 4-7 at `$06F593`…). A base with ranges 0-3 full and range 4
    /// populated must therefore still resolve our BG tiles to the fixed `$0D9100` table, and must
    /// not let the count run past 0x4000 — otherwise DefFileOffset hands the BG numbers to the
    /// ladder. Latent, not observed: no sampled LM ROM populates ranges 4-7 (the one that does,
    /// sgdq2024, is SA-1, whose Map16 hijack does not even point at `$06F5D0`).
    /// </summary>
    [RealRomFact]
    public void the_ladder_stops_at_the_bg_boundary_even_with_a_populated_range_4()
    {
        var tmp = Path.Combine(dir, "highrange.smc");
        File.Copy(TestRom.RealRomPath, tmp);
        Assert.Null(RomPrep.PrepInPlace(tmp));
        var rom = Rom.Load(tmp);

        // Fill ranges 0-3 and populate range 4 the way LM's second chain would. Slot addresses
        // are CONTRACT §7a-rev's; the defs behind them need not exist for the addressing to run.
        void Slot(int at, int imm, int bank)
        {
            int fo = rom.FileOffset(at);
            rom.Data[fo] = 0x69; rom.Data[fo + 1] = (byte)imm; rom.Data[fo + 2] = (byte)(imm >> 8);
            rom.Data[fo + 3] = 0xA0; rom.Data[fo + 4] = 0x00; rom.Data[fo + 5] = (byte)bank;
        }
        foreach (var (at, bank) in new[] { (0x06F552, 0x12), (0x06F55B, 0x13),
                                           (0x06F566, 0x14), (0x06F56F, 0x15), (0x06F593, 0x16) })
            Slot(at, 0x0008, bank);

        Assert.Equal(0x16, rom.LmMap16Slot(4).Bank);            // the slot is read...
        Assert.False(rom.HasMap16Range(4));                     // ...but it is not a range of ours
        Assert.True(rom.LmMap16DefAddr(Map16.BgTileBase) > 0);  // ...and still addressable on purpose
        Assert.True(rom.Map16TileCount <= Map16.BgTileBase,     // what stops is the COUNT
                    $"count is 0x{rom.Map16TileCount:X}");
        // So a BG tile still resolves to the fixed table, which is what the game reads.
        Assert.Equal(rom.FileOffset(0x0D9100), Map16.DefFileOffset(rom, 0, Map16.BgTileBase));
        Assert.Equal(rom.FileOffset(0x0D9100 + 8), Map16.DefFileOffset(rom, 0, Map16.BgTileBase + 1));
    }
}
