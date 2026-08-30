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
    /// Prep v10 IS Lunar Magic's entrance format: the hooks and routines it stamps are the
    /// bytes every LM save installs, so after.smc (a plain LM save) and ShaoBase (a real hack)
    /// both read as having free positions, and our stamp is byte-identical to theirs.
    /// </summary>
    [LmRefRomFact]
    public void v10_stamps_exactly_the_routines_lunar_magic_installs()
    {
        var ours = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(ours, 10);
        var lm = Rom.Load(ReferenceRoms.LmAfter);
        var shao = Rom.Load(ReferenceRoms.ShaoBase);

        foreach (var rom in new[] { ours, lm, shao })
        {
            Assert.True(rom.HasFreeEntrancePositions);
            Assert.True(rom.HasFreeSecondaryPositions);
        }
        Assert.False(Rom.Load(TestRom.RealRomPath).HasFreeEntrancePositions);

        byte[] Bytes(Rom r, int snes, int n) => r.Data.AsSpan(r.FileOffset(snes), n).ToArray();
        foreach (var (snes, n) in new[] { (RomPrep.LmMainEntranceHook, 4), (RomPrep.LmMainEntranceRoutine, 0x46),
                                          (RomPrep.LmSecondaryHook, 8), (RomPrep.LmSecondaryRoutine, 0xB6),
                                          (RomPrep.LmSecondaryReaders, 5), (RomPrep.LmMidwayStore, 5), (0x05D9C3, 1) })
        {
            Assert.Equal(Bytes(lm, snes, n), Bytes(ours, snes, n));
            Assert.Equal(Bytes(lm, snes, n), Bytes(shao, snes, n));
        }
        // The two secondary tables are the one per-ROM thing, and each ROM names its own.
        Assert.Equal(RomPrep.SecondaryYHighSnes, ours.LmSecondaryYHighTable);
        Assert.Equal(0x1086C9, lm.LmSecondaryYHighTable);
        Assert.Equal(0x10F0C5, shao.LmSecondaryYHighTable);
        // ...the migrated ninth bit on every submap record, byte for byte...
        for (int i = 0x100; i < 0x200; i++)
            Assert.Equal(lm.ReadByte(0x05FE00 + i), ours.ReadByte(0x05FE00 + i));
        // ...and LM's initial values for the three per-level tables.
        for (int i = 0; i < 0x200; i++)
        {
            Assert.Equal(lm.ReadByte(Rom.LmEntranceFlags + i), ours.ReadByte(Rom.LmEntranceFlags + i));
            Assert.Equal(lm.ReadByte(Rom.LmEntranceYHigh + i), ours.ReadByte(Rom.LmEntranceYHigh + i));
            Assert.Equal(lm.ReadByte(Rom.LmEntranceFgBg + i), ours.ReadByte(Rom.LmEntranceFgBg + i));
        }
    }

    /// <summary>
    /// LM's two routines, run as code over records this editor wrote, land Mario where
    /// <see cref="EntrancePlacement.Method2X"/>/<see cref="EntrancePlacement.Method2Y"/> say —
    /// so the decode in the editor and the code in the game agree, on the prepped base.
    /// </summary>
    [RealRomFact]
    public void lunar_magics_routines_put_mario_where_the_editor_says()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom, 10);

        // Main: $05DD30 is entered with A = $F200 byte (the action bits already shifted) and
        // Y = level; it writes $94-$97 and returns. The shared tail then puts the SCREEN into
        // $95 for a horizontal level, which is what Method2X does too.
        var main = rom.ReadMainEntrance(0x105) with { Method2 = 1, ReservedMode = 3, MarioX = 5, XHigh = 1, MarioY = 0xA, YHigh = 1 };
        rom.WriteMainEntrance(0x105, main);
        var cpu = new Cpu65816(rom);
        cpu.PresetWidths(m8: true, x8: false);
        cpu.PresetDbr(0x05);                                    // bank-05 code runs with DBR = $05
        cpu.PresetRegs(a: 0, x: 0, y: 0x105);
        cpu.CallLong(RomPrep.LmMainEntranceRoutine);
        int y = cpu.Ram7E[0x96] | (cpu.Ram7E[0x97] << 8);
        Assert.Equal(EntrancePlacement.Method2Y(0xA, 1), y);
        Assert.Equal(EntrancePlacement.Method2X(3, 5, 1) & 0xFF, cpu.Ram7E[0x94]);       // X low: half + step

        // Secondary: $03BCE0 is entered with A = the $FE00 byte, X = Y = record, $00 = $FA00
        // byte and $01 = $FC00 byte (the decode's stashes).
        var sec = rom.ReadSecondaryEntrance(0x0D4) with { Method2 = 1, ReservedX = 7, MarioX = 2, XHigh = 0, MarioY = 3, YHigh = 1 };
        rom.WriteSecondaryEntrance(0x0D4, sec);
        Assert.Equal(sec, rom.ReadSecondaryEntrance(0x0D4));                             // fifth byte round-trips
        var b = sec.ToBytes();
        cpu = new Cpu65816(rom);
        cpu.PresetWidths(m8: true, x8: false);
        cpu.Ram7E[0x00] = b[1]; cpu.Ram7E[0x01] = b[2]; cpu.Ram7E[0x0E] = 0xD4;
        cpu.PresetRegs(a: b[3], x: 0x0D4, y: 0x0D4);
        cpu.CallLong(RomPrep.LmSecondaryRoutine);
        y = cpu.Ram7E[0x96] | (cpu.Ram7E[0x97] << 8);
        Assert.Equal(EntrancePlacement.Method2Y(3, 1), y);
        Assert.Equal(EntrancePlacement.Method2X(7, 2, 0) & 0xFF, cpu.Ram7E[0x94]);
        Assert.Equal(0, cpu.Ram7E[0x0F]);                                                // destination high bit
    }
    /// <summary>
    /// The separate-midway routine is the same story one level up: LM installs it on demand
    /// (juz and ShaoBase have it, a plain save does not), byte-identical apart from four table
    /// operands and its own address — so ours is theirs with the operands repointed.
    /// </summary>
    [LmRefRomFact]
    public void v10_stamps_lunar_magics_separate_midway_routine()
    {
        var ours = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(ours, 10);
        var shao = Rom.Load(ReferenceRoms.ShaoBase);
        var juz = Rom.Load(ReferenceRoms.InProject("juz", "SMW.smc"));

        Assert.True(ours.HasFreeMidwayPosition);
        Assert.True(shao.HasFreeMidwayPosition);
        Assert.True(juz.HasFreeMidwayPosition);
        Assert.False(Rom.Load(ReferenceRoms.LmAfter).HasFreeMidwayPosition);       // a plain save
        Assert.Equal(RomPrep.MidwayTablesSnes, ours.LmMidwayTable);
        Assert.Equal(0x128008, shao.LmMidwayTable);
        Assert.Equal(0x138008, juz.LmMidwayTable);

        // Byte-identical outside the five operand triples.
        int[] operands = [0x0A, 0x27, 0x48, 0x57, 0xAF];
        foreach (var other in new[] { shao, juz })
        {
            int theirs = other.ReadValue(RomPrep.LmMidwayHook + 1, 3);
            for (int i = 0; i < 0xC4; i++)
            {
                if (operands.Any(o => i >= o && i < o + 3)) continue;
                Assert.True(ours.ReadByte(RomPrep.MidwayRoutineSnes + i) == other.ReadByte(theirs + i), $"+{i:X2}");
            }
            // ...and the exit-arrival hook at $05D979 points 0xA0 into the same blob.
            Assert.Equal(theirs + 0xA0, other.ReadValue(RomPrep.LmExitArrivalHook + 1, 3));
        }
        Assert.Equal(RomPrep.MidwayRoutineSnes + 0xA0, ours.ReadValue(RomPrep.LmExitArrivalHook + 1, 3));
    }

    /// <summary>The midway routine, run as code: without the separate flag it hands back the
    /// screen (plus the fifth bit) and touches nothing; with it, Mario's position is the
    /// record's, where <see cref="EntrancePlacement"/> says.</summary>
    [RealRomFact]
    public void the_midway_routine_places_mario_from_its_own_record()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom, 10);
        var main = rom.ReadMainEntrance(0x105);

        int Run(out (int X, int Y) at)
        {
            var cpu = new Cpu65816(rom);
            cpu.PresetWidths(m8: true, x8: false);
            cpu.PresetDbr(0x05);
            cpu.Ram7E[0x0E] = 0x05; cpu.Ram7E[0x0F] = 0x01;
            cpu.Ram7E[0x94] = 0x11; cpu.Ram7E[0x96] = 0x33; cpu.Ram7E[0x97] = 0x44;
            cpu.PresetRegs(a: rom.ReadMainEntrance(0x105).ToBytes()[2], x: 0, y: 0x105);   // $F400 byte
            cpu.CallLong(RomPrep.MidwayRoutineSnes);
            at = (cpu.Ram7E[0x94], cpu.Ram7E[0x96] | (cpu.Ram7E[0x97] << 8));
            return cpu.Acc & 0xFF;
        }

        rom.WriteMainEntrance(0x105, main with { ReservedBoundary = 3, MidwayScreenHigh = 1 });
        Assert.Equal(0x13, Run(out var untouched));                     // screen, five bits
        Assert.Equal((0x11, 0x4433), untouched);                          // vanilla's answer kept

        rom.WriteMainEntrance(0x105, main with { ReservedBoundary = 3, MidwaySeparate = 1, MidwayX = 0xF, MidwayY = 8, MidwayYHigh = 0x41 });
        Assert.Equal(0x03, Run(out var placed));
        Assert.Equal(0xF0, placed.X);                                     // the whole nibble is X bits 4-7
        Assert.Equal(EntrancePlacement.Method2Y(8, 1), placed.Y);
    }
    /// <summary>
    /// LM's level-entry engine is transplanted whole: both blocks byte-identical to after.smc's
    /// apart from the four bank bytes, the fourteen hooks the same bytes with the bank changed,
    /// and $06FA00 at LM's initial value. Detection follows the $05DA17 hook, so after.smc and
    /// ShaoBase read as having it too.
    /// </summary>
    [LmRefRomFact]
    public void v10_transplants_lunar_magics_level_entry_engine()
    {
        var ours = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(ours, 10);
        var lm = Rom.Load(ReferenceRoms.LmAfter);
        Assert.True(ours.HasLmFgBgRelative);
        Assert.True(lm.HasLmFgBgRelative);
        Assert.True(Rom.Load(ReferenceRoms.ShaoBase).HasLmFgBgRelative);
        Assert.False(Rom.Load(TestRom.RealRomPath).HasLmFgBgRelative);

        foreach (var (oursAt, lmAt, size, bankBytes) in new[]
        {
            (LmLevelEntry.BlockASnes, 0x108141, 0x510, new[] { 0x035, 0x049 }),
            (LmLevelEntry.BlockBSnes, 0x108AD5, 0x3C0, new[] { 0x325, 0x333 }),
        })
            for (int i = 0; i < size; i++)
            {
                int want = bankBytes.Contains(i) ? LmLevelEntry.Bank : lm.ReadByte(lmAt + i);
                Assert.True(want == ours.ReadByte(oursAt + i), $"{oursAt:X6}+{i:X3}");
            }
        foreach (var (site, bytes) in LmLevelEntry.Hooks())
            for (int i = 0; i < bytes.Length; i++)
            {
                Assert.Equal(bytes[i], ours.ReadByte(site + i));
                Assert.Equal(i == 3 ? 0x10 : bytes[i], lm.ReadByte(site + i));
            }
        for (int i = 0; i < 0x200; i++)
            Assert.Equal(lm.ReadByte(Rom.LmEntranceLayer2 + i), ours.ReadByte(Rom.LmEntranceLayer2 + i));
        // ...and the level-height half: block C with its five bank bytes, the three small blocks
        // unchanged, every hook with its JSL/JML banks rewritten, and the in-place edits verbatim.
        foreach (var (oursAt, lmAt, size, bankBytes) in new[]
        {
            (LmLevelEntry.BlockCSnes, 0x108EED, 0x370, new[] { 0x086, 0x0AE, 0x0B8, 0x1CF, 0x1DA }),
            (LmLevelEntry.BlockDSnes, 0x108E9D, 0x20, Array.Empty<int>()),
            (LmLevelEntry.BlockESnes, 0x108EC5, 0x20, Array.Empty<int>()),
            (LmLevelEntry.BlockFSnes, 0x1092AD, 0x110, Array.Empty<int>()),
        })
            for (int i = 0; i < size; i++)
            {
                int want = bankBytes.Contains(i) ? LmLevelEntry.Bank : lm.ReadByte(lmAt + i);
                Assert.True(want == ours.ReadByte(oursAt + i), $"{oursAt:X6}+{i:X3}");
            }
        foreach (var (site, bytes) in LmLevelEntry.HeightHooks())
            for (int i = 0; i < bytes.Length; i++)
            {
                Assert.Equal(bytes[i], ours.ReadByte(site + i));
                Assert.Equal(bytes[i] == LmLevelEntry.Bank ? 0x10 : bytes[i], lm.ReadByte(site + i));
            }
        foreach (var (site, bytes) in LmLevelEntry.InPlacePatches())
            for (int i = 0; i < bytes.Length; i++)
            {
                Assert.Equal(bytes[i], ours.ReadByte(site + i));
                Assert.Equal(bytes[i], lm.ReadByte(site + i));
            }
        Assert.True(ours.HasLmLevelHeight);
        Assert.True(lm.HasLmLevelHeight);
        Assert.Equal(0x108AD5, lm.LmLevelHeightTable);
        Assert.Equal(LmLevelEntry.BlockBSnes, ours.LmLevelHeightTable);
        Assert.Equal(0x1B0, ours.LevelHeightPx(0x105));
        Assert.Equal(0x3800, Rom.Load(ReferenceRoms.InProject("DogsOfWar", "dogs_of_war.smc")).LevelHeightPx(0x109));
        // ...and LM's render engine (LmLevelRender): the bank-$1F block at LM's own address,
        // every fixed block and in-place edit byte-for-byte after.smc's.
        for (int i = 0; i < LmLevelRender.Bank1FSize; i++)
            Assert.True(lm.ReadByte(LmLevelRender.Bank1FSnes + i) == ours.ReadByte(LmLevelRender.Bank1FSnes + i), $"1F+{i:X4}");
        foreach (var (site, bytes) in LmLevelRender.Blocks().Concat(LmLevelRender.InPlace()))
            for (int i = 0; i < bytes.Length; i++)
            {
                Assert.Equal(bytes[i], ours.ReadByte(site + i));
                Assert.Equal(bytes[i], lm.ReadByte(site + i));
            }
        Assert.True(ours.HasLmVramPatch);
    }

    /// <summary>
    /// The $05DA17 tail, run as code on the prepped base. Without the relative bit it leaves the
    /// FG/BG position vanilla set; with it, the FG position becomes Mario's Y plus the entrance's
    /// offset nibble x16 — signed by $06FC00 bit 6 — which is what LM's help describes.
    /// </summary>
    [RealRomFact]
    public void the_tail_hook_sets_the_camera_relative_to_mario_when_asked()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom, 10);

        (int Fg, int Bg) Run(int fgbgByte, int f400, int fc00)
        {
            var cpu = new Cpu65816(rom);
            cpu.PresetWidths(m8: true, x8: true);
            cpu.PresetDbr(0x05);
            cpu.Ram7E[0x0E] = 0x05; cpu.Ram7E[0x0F] = 0x01;                 // level $105, horizontal
            cpu.Ram7E[0x96] = 0x50; cpu.Ram7E[0x97] = 0x01;                 // Mario Y = $0150
            cpu.Ram7E[0x1C] = 0xC0; cpu.Ram7E[0x20] = 0xC0;                 // vanilla's answer, to spot
            cpu.Ram7E[0x13CD] = (byte)fgbgByte;                             // what $05DD30 left there
            cpu.Ram7E[0x02] = (byte)f400; cpu.Ram7E[0x04] = (byte)fc00;     // ...and its scratch
            cpu.Ram7E[0x13D7] = 0xB0; cpu.Ram7E[0x13D8] = 0x01;             // level height, from the $05D9A1 hook
            cpu.CallLong(LmLevelEntry.BlockASnes);
            return (cpu.Ram7E[0x1C] | (cpu.Ram7E[0x1D] << 8), cpu.Ram7E[0x20] | (cpu.Ram7E[0x21] << 8));
        }

        Assert.Equal(0x00C0, Run(0x1A, 0x04, 0x00).Fg);                     // LM's default byte: untouched
        Assert.Equal(0x0150 + 0x40, Run(0x9A, 0x04, 0x00).Fg);              // relative, +4 x 16
        Assert.Equal(0x0150 - 0x40, Run(0x9A, 0x0C, 0x40).Fg);              // nibble $C with the sign bit: -4 x 16
    }
}
