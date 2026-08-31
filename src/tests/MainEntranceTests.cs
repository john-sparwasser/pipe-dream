using Xunit;

namespace PipeDream.Tests;

/// <summary>Main entrance / entry settings — the per-level bank-05 tables decoded at
/// $05D90D-$05D99F. A project replays these bytes into the built ROM, so the encode has to
/// be an exact inverse of the decode.</summary>
public class MainEntranceTests
{
    [Fact]
    public void every_possible_record_survives_decode_then_encode()
    {
        foreach (byte fill in new byte[] { 0x00, 0xFF, 0x5A })
            for (int b = 0; b < 12; b++)
                for (int v = 0; v < 256; v++)
                {
                    byte[] bytes = [fill, fill, fill, fill, fill, fill, fill, fill, fill, fill, fill, fill];
                    bytes[b] = (byte)v;
                    Assert.Equal(bytes, new MainEntrance(bytes).ToBytes());
                }
    }

    [Fact]
    public void fields_land_on_the_bits_the_decode_reads()
    {
        // $05F000: bits0-3 Mario Y, bits4-7 layer-2 scroll.
        // $05F200: bits0-2 Mario X, bits3-5 action, bits6-7 layer-2 BG setting.
        // $05F400: bits0-1 vertical scroll, bits2-3 screen boundary Y.
        // $05F600: bits5-6 vertical level, bit7 skip entrance walk.
        var e = new MainEntrance([0, 0, 0, 0]) with
        {
            MarioY = 0x0B, Layer2Scroll = 5, MarioX = 3, EntranceAction = 6, Layer3Option = 2,
            VerticalScroll = 1, ScreenBoundaryY = 2, VerticalLevel = 1, SkipEntranceWalk = 1,
        };
        Assert.Equal([0x5B, 0xB3, 0x09, 0xA0, 0, 0, 0, 0, 0, 0, 0, 0], e.ToBytes());
        Assert.Equal(e, new MainEntrance(e.ToBytes()));
    }

    /// <summary>The two records drive the same two RAM addresses from DIFFERENT bit
    /// positions — $1C/$20 sit at bits 4-5/6-7 of one byte in a secondary record and bits
    /// 2-3/0-1 of another byte here. Sharing packing code would silently swap them.</summary>
    [Fact]
    public void screen_boundary_and_scroll_do_not_share_bits_with_the_secondary_record()
    {
        var main = new MainEntrance([0, 0, 0, 0]) with { ScreenBoundaryY = 3, VerticalScroll = 0 };
        Assert.Equal(0x0C, main.ToBytes()[2]);
        var sec = new SecondaryEntrance([0, 0, 0, 0]) with { ScreenBoundaryY = 3, VerticalScroll = 0 };
        Assert.Equal(0x30, sec.ToBytes()[1]);
    }

    [Fact]
    public void bits_the_decode_ignores_are_carried_through()
    {
        var e = new MainEntrance([0, 0, 0xF0, 0x1F]);
        Assert.Equal(0x0F, e.ReservedBoundary);
        Assert.Equal(0x1F, e.ReservedMode);
        Assert.Equal([0, 0, 0xF0, 0x1F, 0, 0, 0, 0, 0, 0, 0, 0], (e with { ScreenBoundaryY = 0 }).ToBytes());
    }

    [RealRomFact]
    public void rom_read_write_round_trips_through_the_four_tables()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var original = rom.ReadMainEntrance(0x105);
        var edited = original with { MarioX = 5, EntranceAction = 3, VerticalLevel = 1 };

        rom.WriteMainEntrance(0x105, edited);
        Assert.Equal(edited, rom.ReadMainEntrance(0x105));
        Assert.Equal(original, rom.ReadMainEntrance(0x105) with
        { MarioX = original.MarioX, EntranceAction = original.EntranceAction, VerticalLevel = original.VerticalLevel });

        rom.WriteMainEntrance(0x105, original);
        Assert.Equal(original, rom.ReadMainEntrance(0x105));
    }

    /// <summary>Cross-check against a completely independent source of the same fact: the
    /// entry table's vertical bit ($05F600 bit 5 → $5B) against the level header's LevelMode.
    /// Both mark exactly 11 levels vertical and agree on all but two, in opposite directions
    /// — vanilla's own data disagrees with itself on $012 and $108, which nothing notices
    /// because at runtime only $5B decides. A wrong bit position would not produce a
    /// two-level discrepancy; it would produce a wholesale one.</summary>
    [RealRomFact]
    public void the_vertical_bit_agrees_with_the_header_level_mode()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        int fromEntryCount = 0, fromHeaderCount = 0;
        var disagree = new List<int>();
        for (int lvl = 0; lvl < Rom.LevelCount; lvl++)
        {
            bool fromEntry = (rom.ReadMainEntrance(lvl).VerticalLevel & 1) != 0;
            bool fromHeader = rom.IsVerticalMode(LevelParser.Parse(rom, lvl).Header.LevelMode);
            if (fromEntry) fromEntryCount++;
            if (fromHeader) fromHeaderCount++;
            if (fromEntry != fromHeader) disagree.Add(lvl);
        }
        Assert.Equal(11, fromEntryCount);
        Assert.Equal(11, fromHeaderCount);
        Assert.Equal([0x012, 0x108], disagree);
    }

    [RealRomFact]
    public void every_vanilla_record_round_trips()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        for (int lvl = 0; lvl < Rom.LevelCount; lvl++)
        {
            var e = rom.ReadMainEntrance(lvl);
            rom.WriteMainEntrance(lvl, e);
            Assert.Equal(e, rom.ReadMainEntrance(lvl));
        }
    }
}
