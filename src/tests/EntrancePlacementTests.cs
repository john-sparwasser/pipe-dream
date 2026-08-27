using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Tests;

/// <summary>Where an entrance puts Mario. The record stores a screen and two indices into the
/// bank-05 tables, never a position, so this is the decode ($05D7D9 / $05D909-$05DA05) read
/// back out.</summary>
public class EntrancePlacementTests(ITestOutputHelper log)
{
    [RealRomFact]
    public void the_tables_are_the_eight_by_sixteen_grid_the_decode_indexes()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var xs = Enumerable.Range(0, EntrancePlacement.XCount).Select(i => EntrancePlacement.X(rom, 0, i)).ToList();
        var ys = Enumerable.Range(0, EntrancePlacement.YCount).Select(i => EntrancePlacement.Y(rom, i)).ToList();
        log.WriteLine($"X: {string.Join(" ", xs.Select(v => $"{v:X3}"))}");
        log.WriteLine($"Y: {string.Join(" ", ys.Select(v => $"{v:X3}"))}");

        Assert.All(xs, x => Assert.InRange(x, 0, 0xFF));       // X low byte; the screen is separate
        Assert.All(ys, y => Assert.InRange(y, 0, 0x1FF));      // Y is a full 16-bit level position

        // Only FIVE of the eight X offsets are distinct on a horizontal level: indices 4-7 differ
        // from 0-3 only in the table's HIGH byte ($05D758), and the tail overwrites that with
        // the screen. The pairs collapse — a property of the ROM, not a bug here.
        Assert.Equal(5, xs.Distinct().Count());

        // The screen is a whole 256px step on top of the X offset.
        Assert.Equal(EntrancePlacement.X(rom, 0, 3) + 0x300, EntrancePlacement.X(rom, 3, 3));
    }

    /// <summary>A drag lands on the nearest spot the ROM can express, which is the whole reason
    /// the snap exists: there are 8 x 16 of them per screen and nothing in between.</summary>
    [RealRomFact]
    public void a_dropped_position_snaps_to_the_nearest_expressible_one()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        foreach (int screen in new[] { 0, 1, 7, 0x1F })
            foreach (int xi in new[] { 0, 3, 7 })
            {
                int exact = EntrancePlacement.X(rom, screen, xi);
                var (s, i) = EntrancePlacement.NearestX(rom, exact);
                Assert.Equal(exact, EntrancePlacement.X(rom, s, i));    // an exact spot is kept
                var (s2, i2) = EntrancePlacement.NearestX(rom, exact + 3);
                Assert.Equal(exact, EntrancePlacement.X(rom, s2, i2));  // ...and 3px off returns to it
            }
        foreach (int yi in new[] { 0, 5, 15 })
        {
            int exact = EntrancePlacement.Y(rom, yi);
            Assert.Equal(exact, EntrancePlacement.Y(rom, EntrancePlacement.NearestY(rom, exact)));
        }
    }

    /// <summary>
    /// V10 hooks two sites and Lunar Magic takes one of them: every LM hack NOPs `$05D9E9`, the
    /// midway branch's `JMP $05DA17`, while `$05D9FE` is untouched in all of them. So the two
    /// halves are detected separately — a base that has been through LM keeps its freely placed
    /// main entrance and loses the midway one, and the editor has to know which.
    /// </summary>
    [LmRefRomFact]
    public void the_midway_hook_is_a_site_lunar_magic_takes_and_the_main_one_is_not()
    {
        var ours = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(ours, 10);
        Assert.True(ours.HasFreeEntrancePositions);
        Assert.True(ours.HasFreeMidwayPosition);

        var shao = Rom.Load(ReferenceRoms.ShaoBase);
        Assert.Equal(0xEA, shao.ReadByte(RomPrep.MidwayJmpSite));      // LM NOPped it
        Assert.Equal(0x4C, shao.ReadByte(RomPrep.MainJmpSite));        // ...and left this one
        Assert.False(shao.HasFreeEntrancePositions);                   // neither half is ours

        // Our stamp with LM's NOPs over the midway site: main survives, midway does not.
        var mixed = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(mixed, 10);
        for (int i = 0; i < 3; i++) mixed.Data[mixed.FileOffset(RomPrep.MidwayJmpSite) + i] = 0xEA;
        Assert.True(mixed.HasFreeEntrancePositions);
        Assert.False(mixed.HasFreeMidwayPosition);
    }

    /// <summary>
    /// V10's stub, run as code. It has to do two things and no more: put the table's position
    /// into $94/$96 when the record is active, and leave vanilla's answer completely alone when
    /// it is not — a stamp that runs on every level entry has no business changing an untouched
    /// level.
    /// </summary>
    [RealRomFact]
    public void v10_places_mario_from_the_table_and_otherwise_keeps_out_of_the_way()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom, 10);
        Assert.True(rom.HasFreeEntrancePositions);
        Assert.True(FreeEntrance.Supported(rom));

        // The stub ends by jumping where vanilla went; for the test it returns instead, so what
        // is being measured is the stub and not the whole level load.
        foreach (int site in new[] { 0x05DCB3, 0x05DCD4 })
            rom.Data[rom.FileOffset(site)] = 0x60;                  // JMP -> RTS

        (int X, int Y) Run(int entry, int level, int secondary)
        {
            var cpu = new Cpu65816(rom);
            cpu.PresetWidths(m8: true, x8: false);                  // 8-bit A, 16-bit index
            cpu.Ram7E[0x0E] = (byte)level; cpu.Ram7E[0x0F] = (byte)(level >> 8);
            cpu.Ram7E[0x1B93] = (byte)secondary;
            cpu.Ram7E[0x94] = 0x11; cpu.Ram7E[0x95] = 0x22;         // vanilla's answer, to spot
            cpu.Ram7E[0x96] = 0x33; cpu.Ram7E[0x97] = 0x44;
            cpu.CallNear(entry);
            return (cpu.Ram7E[0x94] | (cpu.Ram7E[0x95] << 8), cpu.Ram7E[0x96] | (cpu.Ram7E[0x97] << 8));
        }

        // Nothing placed: vanilla's position survives untouched.
        Assert.Equal((0x2211, 0x4433), Run(0x05DC90, 0x105, 0));
        Assert.Equal((0x2211, 0x4433), Run(0x05DCB6, 0x105, 0));

        Assert.True(FreeEntrance.Write(rom, 0x105, midway: false, 0x0345, 0x0123));
        Assert.True(FreeEntrance.Write(rom, 0x105, midway: true, 0x1EE0, 0x0210));
        Assert.Equal((0x0345, 0x0123), Run(0x05DC90, 0x105, 0));    // main
        Assert.Equal((0x1EE0, 0x0210), Run(0x05DCB6, 0x105, 0));    // midway, independently

        // A secondary entry keeps its own record's position: the main stub stands down.
        Assert.Equal((0x2211, 0x4433), Run(0x05DC90, 0x105, 1));
        // ...and another level is unaffected by this one being placed.
        Assert.Equal((0x2211, 0x4433), Run(0x05DC90, 0x106, 0));

        Assert.Equal((0x0345, 0x0123), FreeEntrance.Read(rom, 0x105, midway: false));
        Assert.True(FreeEntrance.Clear(rom, 0x105, midway: false));
        Assert.Null(FreeEntrance.Read(rom, 0x105, midway: false));
        Assert.Equal((0x2211, 0x4433), Run(0x05DC90, 0x105, 0));    // back to vanilla's
    }
}
