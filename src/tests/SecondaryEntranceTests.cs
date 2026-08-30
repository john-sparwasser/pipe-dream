using Xunit;

namespace PipeDream.Tests;

/// <summary>Secondary entrance records — the destination side of a secondary exit, decoded
/// by $05D7D9-$05D838 out of four parallel tables. A project replays these bytes into the
/// built ROM, so an inexact round trip would corrupt entrances nobody edited.</summary>
public class SecondaryEntranceTests
{
    [Fact]
    public void every_possible_record_survives_decode_then_encode()
    {
        foreach (byte fill in new byte[] { 0x00, 0xFF, 0x5A })
            for (int b = 0; b < 6; b++)
                for (int v = 0; v < 256; v++)
                {
                    byte[] bytes = [fill, fill, fill, fill, fill, fill];
                    bytes[b] = (byte)v;
                    Assert.Equal(bytes, new SecondaryEntrance(bytes).ToBytes());
                }
    }

    [Fact]
    public void fields_land_on_the_bits_the_decode_reads()
    {
        // $05FA00: bits0-3 Mario Y, bits4-5 screen boundary, bits6-7 vertical scroll.
        // $05FC00: bits5-7 Mario X.  $05FE00: bits0-2 entrance action.
        var e = new SecondaryEntrance([0xC5, 0x00, 0x00, 0x00]) with
        {
            MarioY = 0x0B, ScreenBoundaryY = 2, VerticalScroll = 3, MarioX = 5, EntranceAction = 6,
        };
        Assert.Equal([0xC5, 0xEB, 0xA0, 0x06, 0, 0], e.ToBytes());
        Assert.Equal(e, new SecondaryEntrance(e.ToBytes()));
    }

    [Fact]
    public void bits_the_vanilla_decode_ignores_are_carried_through()
    {
        // $05FC00 bits0-4 (the screen) and $05FE00 bits3-7 (Lunar Magic's) are unread by
        // vanilla, so editing an unrelated field must not clear them.
        var e = new SecondaryEntrance([0, 0, 0x1F, 0xF8]);
        Assert.Equal(0x1F, e.ReservedX);
        Assert.Equal((1, 3, 1, 1), (e.DestinationHigh, e.XHigh, e.Method2, e.ActionHigh));
        Assert.Equal([0, 0, 0x1F, 0xF8, 0, 0], (e with { MarioX = 0 }).ToBytes());
        Assert.Equal([0, 0, 0x3F, 0xF9, 0, 0], (e with { MarioX = 1, EntranceAction = 1 }).ToBytes());
    }

    [RealRomFact]
    public void rom_read_write_round_trips_through_the_four_tables()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var original = rom.ReadSecondaryEntrance(0xD4);
        var edited = original with { DestinationLevel = 0xC5, MarioX = 3, EntranceAction = 2 };

        rom.WriteSecondaryEntrance(0xD4, edited);
        Assert.Equal(edited, rom.ReadSecondaryEntrance(0xD4));
        Assert.NotEqual(edited, rom.ReadSecondaryEntrance(0xD5));   // neighbours untouched

        rom.WriteSecondaryEntrance(0xD4, original);
        Assert.Equal(original, rom.ReadSecondaryEntrance(0xD4));
    }

    /// <summary>Ties the two halves together: if either the exit decode (which byte is the
    /// destination) or the entrance indexing were wrong, vanilla exits would point at empty
    /// records. Vanilla populates 42 entrances — 24 below $100 and 18 above — because the
    /// index is 9 bits: the exit supplies the low byte and $05D7CB supplies bit 8 from the
    /// submap flag ($1F11). So one exit byte names a PAIR of records, and this checks that
    /// at least one of the pair exists.</summary>
    [RealRomFact]
    public void every_vanilla_secondary_exit_points_at_a_populated_entrance()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        int seen = 0;
        bool Populated(int i) => rom.ReadSecondaryEntrance(i).ToBytes().Any(b => b != 0);
        for (int lvl = 0; lvl < Rom.LevelCount; lvl++)
        {
            List<LevelObject> objs;
            try { objs = LevelParser.Parse(rom, lvl).Objects.Where(o => o.IsScreenExit).ToList(); }
            catch { continue; }
            foreach (var o in objs.Where(o => o.ExitUsesSecondary))
            {
                Assert.True(Populated(o.ExitDestination) || Populated(o.ExitDestination + 0x100),
                            $"level {lvl:X3} secondary exit points at empty entrance " +
                            $"{o.ExitDestination:X2}/{o.ExitDestination + 0x100:X3}");
                seen++;
            }
        }
        Assert.True(seen > 10, $"expected vanilla's secondary exits, found {seen}");
    }

    [RealRomFact]
    public void every_vanilla_entrance_round_trips()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        for (int i = 0; i < Rom.SecondaryEntranceCount; i++)
        {
            var e = rom.ReadSecondaryEntrance(i);
            rom.WriteSecondaryEntrance(i, e);
            Assert.Equal(e, rom.ReadSecondaryEntrance(i));
        }
    }
}
