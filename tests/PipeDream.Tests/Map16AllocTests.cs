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

    /// <summary>The editor used to label EVERY empty page "click to allocate", across all three
    /// banks and all 0x20 pages each — but only bank 0 below page 0x10 can grow, because
    /// EnsureMap16Tiles patches the single lookup slot covering tiles 0x200-0xFFF. So roughly
    /// 7 of every 8 pages advertised something that could not happen.</summary>
    [Fact]
    public void only_bank_0_below_page_10_can_be_created()
    {
        Assert.True(Map16Editor.CanAllocate(0x200));      // first extended tile
        Assert.True(Map16Editor.CanAllocate(0xFFF));      // last supported
        Assert.False(Map16Editor.CanAllocate(0x1FF));     // vanilla defs already exist
        Assert.False(Map16Editor.CanAllocate(0x1000));    // past the lookup slot
        Assert.False(Map16Editor.CanAllocate(0x2000));    // bank 1
        Assert.False(Map16Editor.CanAllocate(0x4000));    // bank 2 = the fixed BG table
    }

    [Fact]
    public void empty_pages_describe_themselves_without_promising_allocation()
    {
        // Bank 0 under page 0x10: the only place editing creates anything.
        Assert.Contains("paint", Map16Editor.UnusedPageNote(0, 0x05));
        // Bank 0 at or past page 0x10, and bank 1: unreachable, and must not say "paint".
        Assert.DoesNotContain("paint", Map16Editor.UnusedPageNote(0, 0x10));
        Assert.DoesNotContain("paint", Map16Editor.UnusedPageNote(1, 0x20));
        // Bank 2 is the BG table — fixed size, so it is a different explanation entirely.
        Assert.Contains("BG", Map16Editor.UnusedPageNote(2, 0x42));
        Assert.DoesNotContain("paint", Map16Editor.UnusedPageNote(2, 0x42));
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
        Assert.Contains("0xFFF", rom.EnsureMap16Tiles(0x1001) ?? "");
        Assert.Null(rom.EnsureMap16Tiles(0x1000));             // the ceiling itself is fine
    }
}
