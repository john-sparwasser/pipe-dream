using Xunit;

namespace PipeDream.Tests;

/// <summary>Screen exits (CONTRACT §4, handler $0DA512). The field the handler indexes its
/// destination table with is the object's Y, NOT its stream screen — get that backwards and
/// every edited exit silently governs the wrong screen.</summary>
public class ScreenExitTests
{
    [Fact]
    public void exit_fields_decode_to_what_the_handler_reads()
    {
        // $0DA512: X = $0A & $1F (the Y field) -> $19B8,X = 4th byte;
        //          $0B & 1 -> $19D8,X (water); $0B >> 1 -> $1B93 (secondary).
        var o = LevelObject.ScreenExit(screen: 0x0B, destination: 0xC5, water: true, secondary: false);
        Assert.True(o.IsScreenExit);
        Assert.Equal(0x0B, o.ExitScreen);
        Assert.Equal(0x0B, o.Y);              // the governed screen lives in Y
        Assert.Equal(0xC5, o.ExitDestination);
        Assert.True(o.ExitIsWater);
        Assert.False(o.ExitUsesSecondary);
        Assert.Equal(1, o.XNibble);           // bit0 only

        var s = LevelObject.ScreenExit(screen: 3, destination: 0x1F, water: false, secondary: true);
        Assert.True(s.ExitUsesSecondary);
        Assert.False(s.ExitIsWater);
        Assert.Equal(2, s.XNibble);           // bit1 only
    }

    [Fact]
    public void an_exit_survives_encode_then_parse_with_its_flags()
    {
        var (rom, level) = TestRom.CreateWithLevel();
        var objs = new List<LevelObject>
        {
            LevelObject.ScreenExit(0, 0xC5, water: false, secondary: false),
            LevelObject.ScreenExit(5, 0xE1, water: true, secondary: true),
        };
        var parsed = LevelParser.ParseEncoded(rom, LevelEncoder.Encode(level, objs));
        var exits = parsed.Where(o => o.IsScreenExit).ToList();
        Assert.Equal(2, exits.Count);
        Assert.Equal((0, 0xC5, false, false),
                     (exits[0].ExitScreen, exits[0].ExitDestination, exits[0].ExitIsWater, exits[0].ExitUsesSecondary));
        Assert.Equal((5, 0xE1, true, true),
                     (exits[1].ExitScreen, exits[1].ExitDestination, exits[1].ExitIsWater, exits[1].ExitUsesSecondary));
    }

    /// <summary>The decisive one: run an encoded exit through the ROM's OWN handler and see
    /// where the destination lands. The object is emitted at stream screen 0, so the value
    /// showing up at $19B8+7 can only have come from the Y field.</summary>
    [RealRomFact]
    public void the_roms_own_handler_reads_the_fields_where_the_encoder_puts_them()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var level = LevelParser.Parse(rom, 0x105);

        (int dest, int water, int secondary) Run(LevelObject exit)
        {
            byte[] enc = LevelEncoder.Encode(level, new List<LevelObject> { exit });
            ObjectEngine.RenderEmulatedStream(rom, level.Header, enc, 0);
            var cpu = ObjectEngine.LastCpu!;
            return (cpu.Ram7E[0x19B8 + exit.ExitScreen], cpu.Ram7E[0x19D8 + exit.ExitScreen], cpu.Ram7E[0x1B93]);
        }

        Assert.Equal((0xC5, 1, 0), Run(LevelObject.ScreenExit(7, 0xC5, water: true, secondary: false)));
        Assert.Equal((0xE1, 0, 1), Run(LevelObject.ScreenExit(3, 0xE1, water: false, secondary: true)));
        Assert.Equal((0xB2, 0, 0), Run(LevelObject.ScreenExit(0, 0xB2, water: false, secondary: false)));
    }

    [RealRomFact]
    public void vanilla_exits_govern_screens_independent_of_their_stream_position()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        int exits = 0, differing = 0, secondary = 0;
        for (int lvl = 0; lvl < Rom.LevelCount; lvl++)
        {
            List<LevelObject> objs;
            try { objs = LevelParser.Parse(rom, lvl).Objects.Where(o => o.IsScreenExit).ToList(); }
            catch { continue; }
            foreach (var o in objs)
            {
                exits++;
                if (o.ExitScreen != o.Screen) differing++;
                if (o.ExitUsesSecondary) secondary++;
                Assert.InRange(o.ExitDestination, 0, 0xFF);   // the 4th byte is always present
            }
        }
        Assert.True(exits > 200, $"expected the vanilla exit set, found {exits}");
        // If Y were merely a copy of the stream screen the editor could use either field —
        // vanilla proves it cannot.
        Assert.True(differing > 0, "no vanilla exit had Y != stream screen — contract assumption is wrong");
        Assert.True(secondary > 0, "no vanilla exit used the secondary flag — decode is untested");
    }
}
