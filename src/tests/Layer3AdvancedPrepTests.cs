using PipeDream;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Prep v16 — the ADVANCED layer-3 bypass on the console side (CONTRACT §12b). Up to v15 the
/// scroll/blend/position nibbles were bytes nothing read: LM's reader lives at `$0FFD9F` and our
/// prep installed neither it nor the engine behind it, so every one of those settings was
/// editor-only. These run the real stamped 65816 on the emulator.
///
/// The three properties worth pinning are the ones that would each fail silently:
///   * the READER picks the right nibble out of the right word — an off-by-two in Y reads the
///     neighbouring GFX slot's id as a scroll code and the layer still moves, just wrongly;
///   * the initial X index is an INDEX, not a value, and index 3 is special-cased to $100;
///   * the Y offset is stored times 8 in a 14-bit SIGNED field and reaches the game times 16, so
///     a missed sign extension puts a negative offset 0x4000 pixels away instead of above.
///
/// What is NOT covered here: the "Fast" rate (it uses the SNES hardware divider, which this
/// emulator does not model) and the disabled path's re-entry into vanilla (it JMLs into bank 05,
/// which needs a whole level's state). Both are console checks.
/// </summary>
public class Layer3AdvancedPrepTests(ITestOutputHelper log)
{
    private const int Level = 5;

    /// <summary>A prepped ROM whose level-5 record carries the given advanced settings.</summary>
    private static (Rom Rom, int RecOffset) Prepped()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom);
        int fo = rom.FileOffset(RomPrep.GfxBypassRecords + Level * 0x20);
        for (int w = 0; w < 16; w++) { rom.Data[fo + w * 2] = 0x7F; rom.Data[fo + w * 2 + 1] = 0; }
        return (rom, fo);
    }

    /// <summary>Write the nine advanced nibbles the way <see cref="Layer3.WriteAdvanced"/> does,
    /// so the ASM is tested against the same encoding the editor writes.</summary>
    private static void PutAdvanced(Rom rom, int fo, Layer3.Advanced adv)
    {
        var w = new ushort[16];
        for (int i = 0; i < 16; i++) w[i] = (ushort)(rom.Data[fo + i * 2] | (rom.Data[fo + i * 2 + 1] << 8));
        Layer3.WriteAdvanced(w, adv);
        for (int i = 0; i < 16; i++) { rom.Data[fo + i * 2] = (byte)w[i]; rom.Data[fo + i * 2 + 1] = (byte)(w[i] >> 8); }
    }

    private static Cpu65816 RunOpt(Rom rom)
    {
        var cpu = new Cpu65816(rom);
        cpu.Ram7E[0xFE] = Level + 1;
        cpu.PresetWidths(m8: true, x8: true);
        cpu.CallLong(RomPrep.L3Opt, 2_000_000);
        return cpu;
    }

    private static int Word(Cpu65816 cpu, int addr) => cpu.Ram7E[addr] | (cpu.Ram7E[addr + 1] << 8);

    [RealRomFact]
    public void the_reader_gathers_the_nine_nibbles_into_lms_four_variables()
    {
        var (rom, fo) = Prepped();
        Assert.True(rom.HasLmLayer3Advanced, "v16 must answer the advanced-reader probe");

        // Distinct nibbles in every one of the nine words, so a wrong Y shows up as a wrong value.
        var w = new ushort[16];
        for (int i = 0; i < 16; i++) w[i] = 0x007F;
        int[] words = [2, 3, 9, 10, 11, 12, 13, 14, 15];
        foreach (int i in words) w[i] = (ushort)(0x007F | (i << 12));      // nib(wN) = N & 0xF
        for (int i = 0; i < 16; i++) { rom.Data[fo + i * 2] = (byte)w[i]; rom.Data[fo + i * 2 + 1] = (byte)(w[i] >> 8); }

        var cpu = RunOpt(rom);
        Assert.Equal(11, cpu.Ram7F[0xC01A]);                              // nib(w11)
        Assert.Equal(3 << 4 | 2, cpu.Ram7F[0xC01B]);                      // nib(w3)<<4 | nib(w2)
        Assert.Equal(10 << 4 | 9, cpu.Ram7F[0xC01C]);                     // nib(w10)<<4 | nib(w9)
        // $145E = nib(w15)<<12 | nib(w14)<<8 | nib(w13)<<4 | nib(w12)
        Assert.Equal((15 << 12) | (14 << 8) | (13 << 4) | 12, Word(cpu, 0x145E));
        log.WriteLine($"$7FC01A={cpu.Ram7F[0xC01A]:X2} $7FC01B={cpu.Ram7F[0xC01B]:X2} "
                    + $"$7FC01C={cpu.Ram7F[0xC01C]:X2} $145E={Word(cpu, 0x145E):X4}");
    }

    [RealRomFact]
    public void the_initial_x_index_is_an_index_and_index_three_is_special_cased()
    {
        // 00 04 08 10 tiles = 0x00 0x40 0x80 0x100 pixels — the list skips 0C precisely because
        // the game special-cases the last one.
        for (int idx = 0; idx < 4; idx++)
        {
            var (rom, fo) = Prepped();
            PutAdvanced(rom, fo, new Layer3.Advanced(false, false, false, 0, 0, idx, 0));
            int got = Word(RunOpt(rom), 0x146A);
            int want = idx == 3 ? 0x100 : idx * 0x40;
            log.WriteLine($"X index {idx} (= {Layer3.XPositions[idx]:X2} tiles) -> $146A = {got:X4}");
            Assert.Equal(want, got);
        }
    }

    [RealRomFact]
    public void the_y_offset_reaches_the_game_times_sixteen_with_its_sign()
    {
        foreach (int y in (int[])[0, 1, 0x123, -1, -0x120, Layer3.MaxY, Layer3.MinY])
        {
            var (rom, fo) = Prepped();
            PutAdvanced(rom, fo, new Layer3.Advanced(false, false, false, 0, 0, 0, y));
            int got = (short)Word(RunOpt(rom), 0x146C);
            log.WriteLine($"Y {y} -> $146C = {got} (want {y * 16})");
            Assert.Equal(y * 16, got);
        }
    }

    [RealRomFact]
    public void colour_math_and_the_subscreen_move_are_applied_from_their_bits()
    {
        var (rom, fo) = Prepped();
        PutAdvanced(rom, fo, new Layer3.Advanced(CgAdSub: true, Subscreen: true,
                                                 FixScrollSync: false, 0, 0, 0, 0));
        var cpu = new Cpu65816(rom);
        cpu.Ram7E[0xFE] = Level + 1;
        cpu.Ram7E[0x40] = 0x00;
        cpu.Ram7E[0x0D9D] = 0x00; cpu.Ram7E[0x0D9E] = 0x00;
        cpu.PresetWidths(m8: true, x8: true);
        cpu.CallLong(RomPrep.L3Opt, 2_000_000);
        Assert.Equal(0x04, cpu.Ram7E[0x40] & 0x04);                       // CGADSUB on
        Assert.Equal(0x0400, Word(cpu, 0x0D9D) & 0x0400);                 // layer 3 to subscreen

        // ...and cleared again when the flags are off, so a level that turns them off recovers.
        var (rom2, fo2) = Prepped();
        PutAdvanced(rom2, fo2, new Layer3.Advanced(false, false, false, 0, 0, 0, 0));
        var cpu2 = new Cpu65816(rom2);
        cpu2.Ram7E[0xFE] = Level + 1;
        cpu2.Ram7E[0x40] = 0xFF;
        cpu2.PresetWidths(m8: true, x8: true);
        cpu2.CallLong(RomPrep.L3Opt, 2_000_000);
        Assert.Equal(0, cpu2.Ram7E[0x40] & 0x04);
    }

    /// <summary>
    /// The per-frame half: layer 3's position is the initial offset plus a fraction of layer 1's
    /// camera, and the fraction is what the dropdown picks. The ladder is the whole point — if the
    /// code→shift mapping is off by one, every level's parallax is subtly wrong and nothing
    /// crashes.
    /// </summary>
    [RealRomFact]
    public void each_scroll_rate_moves_layer_3_by_its_own_fraction_of_layer_1()
    {
        const int cam = 0x800, baseOff = 0x40;
        // dropdown index → the divisor its handler applies to the camera.
        var expect = new (int Index, string Name, int Shift)[]
        {
            (0, "None",     -1),           // -1 = does not follow the camera at all
            (1, "Constant",  0),
            (2, "Medium",    1),
            (3, "Medium 2",  2),
            (4, "Medium 3",  3),
            (5, "Medium 4",  4),
            (6, "Slow",      5),
            (7, "Slow 2",    6),
        };
        foreach (var (index, name, shift) in expect)
        {
            var (rom, fo) = Prepped();
            PutAdvanced(rom, fo, new Layer3.Advanced(false, false, false,
                                                     VScroll: index, HScroll: index, XPos: 1, Y: 0));
            var cpu = RunOpt(rom);                                        // resolves + stashes codes
            Assert.Equal(Layer3.ScrollCodes[index], cpu.Ram7E[RomPrep.L3CodeH]);
            Assert.Equal(Layer3.ScrollCodes[index], cpu.Ram7E[RomPrep.L3CodeV]);

            cpu.Ram7E[0x1931] = 0;
            cpu.Ram7E[0x1A] = cam & 0xFF; cpu.Ram7E[0x1B] = cam >> 8;     // layer 1 camera X
            cpu.Ram7E[0x1C] = cam & 0xFF; cpu.Ram7E[0x1D] = cam >> 8;     // ...and Y
            cpu.PresetWidths(m8: true, x8: true);
            cpu.PresetWidths(m8: true, x8: true);
        cpu.CallLong(RomPrep.L3Scroll, 2_000_000);

            int moved = shift < 0 ? 0 : cam >> shift;
            int wantX = baseOff + moved;         // XPos index 1 = 0x40 pixels of initial offset
            int wantY = moved;                   // Y offset 0, so the axis is the camera fraction
            log.WriteLine($"{name,-9} code {Layer3.ScrollCodes[index]:X2}: "
                        + $"$22={Word(cpu, 0x22):X4} (want {wantX:X4})  "
                        + $"$24={Word(cpu, 0x24):X4} (want {wantY:X4})");
            Assert.Equal(wantX, Word(cpu, RomPrep.L3ScrollX));
            Assert.Equal(wantY, Word(cpu, RomPrep.L3ScrollY));
        }
    }

    [RealRomFact]
    public void the_scroll_sync_fix_mirrors_the_result_where_the_hdma_reads_it()
    {
        var (rom, fo) = Prepped();
        PutAdvanced(rom, fo, new Layer3.Advanced(false, false, FixScrollSync: true,
                                                 VScroll: 1, HScroll: 1, XPos: 0, Y: 0));
        var cpu = RunOpt(rom);
        cpu.Ram7E[0x1931] = 0;
        cpu.Ram7E[0x1A] = 0x34; cpu.Ram7E[0x1B] = 0x12;
        cpu.Ram7E[0x1C] = 0x78; cpu.Ram7E[0x1D] = 0x56;
        cpu.PresetWidths(m8: true, x8: true);
        cpu.CallLong(RomPrep.L3Scroll, 2_000_000);
        Assert.Equal(0x1234, Word(cpu, 0x22));
        Assert.Equal(0x5678, Word(cpu, 0x24));
        Assert.Equal(0x1234, Word(cpu, 0x1B78));
        Assert.Equal(0x5678, Word(cpu, 0x1B7A));
    }

    /// <summary>
    /// The twelve auto-scroll rates: a speed in 8.8 fixed point accumulated per frame, not a
    /// fraction of the camera. The pixel is only carried out when the fraction rolls over, so a
    /// test that ran one frame would see nothing move at the slow speeds and conclude the whole
    /// thing was dead — which is exactly how the "hold position" stub used to pass.
    ///
    /// The seeded frame is deliberately still: `$0BE6`/`$0BE7` bit 7 makes the level open on the
    /// position the X/Y fields name (for auto-scroll LM's help calls those actual positions, not
    /// offsets) rather than one step past it.
    /// </summary>
    [RealRomFact]
    public void an_auto_scroll_rate_carries_a_whole_pixel_only_when_its_fraction_rolls_over()
    {
        // dropdown index → (speed per frame in 8.8, name). Up/Left are positive, Down/Right negative.
        var expect = new (int Index, int Speed)[]
        {
            (9, 0x0040), (10, 0x0080), (11, 0x0100), (12, 0x0200), (13, 0x0300), (14, 0x0400),
            (15, -0x0040), (16, -0x0080), (17, -0x0100), (18, -0x0200), (19, -0x0300), (20, -0x0400),
        };
        foreach (var (index, speed) in expect)
        {
            var (rom, fo) = Prepped();
            PutAdvanced(rom, fo, new Layer3.Advanced(false, false, false,
                                                     VScroll: 0, HScroll: index, XPos: 1, Y: 0));
            var cpu = RunOpt(rom);
            Assert.Equal(Layer3.ScrollCodes[index], cpu.Ram7E[RomPrep.L3CodeH]);
            Assert.Equal(0x40, Word(cpu, RomPrep.L3ScrollX));             // seeded at XPos index 1

            cpu.Ram7E[0x1931] = 0;
            const int frames = 8;
            for (int f = 0; f < frames; f++)
            {
                cpu.PresetWidths(m8: true, x8: true);
                cpu.CallLong(RomPrep.L3Scroll, 2_000_000);
            }
            // The same accumulator, in C#. Not speed*frames/256: the fraction seed for a NEGATIVE
            // speed is the speed's own low byte (LM's `BMI` past the `LDA #$0000`), which delays
            // its first whole pixel — at the slowest rate by three frames. Modelling it here
            // rather than asserting the idealised figure is what makes this test able to fail
            // for a real reason.
            int frac = speed < 0 ? speed & 0xFF : 0, want = 0x40;
            for (int f = 1; f < frames; f++)
            {
                int sum = (frac + speed) & 0xFFFF;
                frac = sum & 0xFF;
                want = (want + ((short)(sum & 0xFF00) >> 8)) & 0xFFFF;
            }
            log.WriteLine($"index {index,2} speed {speed,6}: $22={Word(cpu, 0x22):X4} (want {want:X4})");
            Assert.Equal(want, Word(cpu, RomPrep.L3ScrollX));
            Assert.Equal(0, Word(cpu, RomPrep.L3ScrollY));                // vertical is None
        }
    }

    /// <summary>
    /// `$00` must survive the seat. `$009FC0` leaves the level's mode*3 there and `$00A026` —
    /// two instructions after this routine returns — adds it to the option to index the layer-3
    /// tilemap pointer table. Both the reader's nibble-pair helper and the engine's code
    /// resolution want a scratch byte, and taking `$00` sent the stripe uploader off a pointer
    /// that is not a script: the level came up dark and wrong whenever the group was on, which
    /// read as "the advanced settings do nothing" rather than as a crash.
    /// </summary>
    [RealRomFact]
    public void the_seat_gives_back_the_scratch_byte_the_caller_is_still_using()
    {
        foreach (bool on in (bool[])[true, false])
        {
            var (rom, fo) = Prepped();
            if (on) PutAdvanced(rom, fo, new Layer3.Advanced(true, true, true, 8, 8, 3, 0x100));
            var cpu = new Cpu65816(rom);
            cpu.Ram7E[0xFE] = Level + 1;
            cpu.Ram7E[0x00] = 0x2A;                                       // mode*3, as $009FC0 left it
            cpu.PresetWidths(m8: true, x8: true);
            cpu.CallLong(RomPrep.L3Opt, 2_000_000);
            Assert.Equal(0x2A, cpu.Ram7E[0x00]);
        }
    }

    /// <summary>
    /// The group must clear `$13D5`, or none of the scrolling above ever runs on a real level.
    /// `$00A012` sets that flag for every (mode, option) whose entry in `$009F88` is negative —
    /// which includes **mode 0, option 3**, i.e. an ordinary horizontal level set to "Tileset
    /// Specific", the exact case the advanced group exists for. Its only reader gates the JSR
    /// that reaches the per-frame scroll routine our dispatcher hooks. MEASURED in Mesen: 0
    /// dispatcher hits over a whole level with the flag up, 250 with it forced down. LM writes
    /// the same byte at `$109A65` (`STX $13D5`, X = 0 on the path every horizontal level takes).
    /// </summary>
    [RealRomFact]
    public void the_group_lets_layer_3_scroll_on_a_level_whose_tileset_froze_it()
    {
        var (rom, fo) = Prepped();
        PutAdvanced(rom, fo, new Layer3.Advanced(false, false, false, VScroll: 0, HScroll: 2, 0, 0));
        var cpu = new Cpu65816(rom);
        cpu.Ram7E[0xFE] = Level + 1;
        cpu.Ram7E[0x13D5] = 1;                                            // what $00A012 leaves
        cpu.PresetWidths(m8: true, x8: true);
        cpu.CallLong(RomPrep.L3Opt, 2_000_000);
        Assert.Equal(0, cpu.Ram7E[0x13D5]);

        // A level that does NOT use the group keeps vanilla's answer — the flag is the tileset's
        // to set, and overriding it everywhere would move layer 3 on levels that never asked.
        var (rom2, _) = Prepped();
        var cpu2 = new Cpu65816(rom2);
        cpu2.Ram7E[0xFE] = Level + 1;
        cpu2.Ram7E[0x13D5] = 1;
        cpu2.PresetWidths(m8: true, x8: true);
        cpu2.CallLong(RomPrep.L3Opt, 2_000_000);
        Assert.Equal(1, cpu2.Ram7E[0x13D5]);
    }

    /// <summary>A level that does not use the group must not have its layer-3 option answer
    /// change — that byte gates the tilemap upload, so a wrong 0 here blanks layer 3 outright.</summary>
    [RealRomFact]
    public void an_unbypassed_level_still_gets_the_vanilla_option_answer()
    {
        var (rom, _) = Prepped();                                         // no advanced nibbles
        foreach (int option in (int[])[0, 1, 2, 3])
        {
            var cpu = new Cpu65816(rom);
            cpu.Ram7E[0xFE] = Level + 1;
            cpu.Ram7E[0x1BE3] = (byte)option;
            cpu.PresetWidths(m8: true, x8: true);
            cpu.CallLong(RomPrep.L3Opt, 2_000_000);
            // A = option - 1, which is what the displaced BEQ at $00A023 reads (X = option and
            // the Z it branches on both come from the TAX/INX this cannot observe).
            Assert.Equal((option - 1) & 0xFF, cpu.Acc & 0xFF);
        }
    }
}
