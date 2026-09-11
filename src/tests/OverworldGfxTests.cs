using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Per-submap graphics — Lunar Magic's Overworld ▸ Submap GFX (reference/OVERWORLD.md §4).
/// Vanilla gives all seven submaps tileset row 0x11's four FG and four sprite files; prep v18
/// carries LM's hack, which is seven more entries on the per-level GFX record table, and the
/// point of it is that a repointed slot changes ONE submap's map and leaves the others alone.
/// </summary>
public class OverworldGfxTests(ITestOutputHelper log) : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pdowgfx-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    private static Rom? Open() => PreppedRom.Fork() is { } p ? Rom.Load(p) : null;

    /// <summary>The one word we repaint through: layer 2 tile 0x004, which lives in FG1's page.</summary>
    private const int FgWord = 0x0004;

    [Fact]
    public void prep_carries_lunar_magics_own_starting_record_for_every_submap()
    {
        if (Open() is not { } rom) { log.WriteLine("SKIP: no ROM"); return; }
        Assert.True(rom.HasOwGfxBypass, "prep left no per-submap GFX hook at $00A140");

        // LM's own bytes, read off a v17 base it installed the hack into on 2026-09-09: the
        // vanilla lists in a level record's word order, layer 3's four in the tail, and NO
        // enable bit in w0 — every submap reads its record once the hack is in.
        ushort[] lm = [0x14, 0x7F, 0x7F, 0x7F, 0x1E, 0x08, 0x1D, 0x1C,
                       0x1D, 0x1C, 0x0F, 0x10, 0x2B, 0x2A, 0x29, 0x28];
        for (int submap = 0; submap < Overworld.Submaps; submap++)
            Assert.Equal(lm, rom.OwGfxBypass(submap));
        Assert.Equal(lm, Overworld.VanillaGfxRecord(rom));
        Assert.Null(rom.OwGfxBypass(Overworld.Submaps));            // there are only seven
    }

    /// <summary>
    /// What makes Lunar Magic's own Submap GFX dialog read these records (prep v19): its layout
    /// stamp, and the record address in the two operand fields its dialog parses the stub for.
    /// The first 35 bytes ARE LM's stub — only the two addresses are ours — because those offsets
    /// are the contract; see reference/LM_PARITY.md §2 for how that was measured.
    /// </summary>
    [Fact]
    public void the_stub_is_lunar_magics_own_with_our_record_address_in_it()
    {
        if (Open() is not { } rom) { log.WriteLine("SKIP: no ROM"); return; }
        Assert.True(rom.HasLmOwGfxMarker, "no LM layout stamp at $0FF15C");

        int record = RomPrep.GfxBypassRecords + RomPrep.OwGfxRecordIndex * 0x20;
        // LM's own bytes, from the ROM it installed the hack into. The three address bytes (the
        // ADC operand at 9/10 and the LDA operand at 18) are ours and checked separately.
        var lm = Convert.FromHexString(
            "8520" + "8A" + "0A0A0A0A" + "18" + "690000" + "8F06C07F" + "E220" +
            "A900" + "8F08C07F" + "A942" + "8F09C07F" + "A900" + "8F0BC07F");
        int at = rom.FileOffset(RomPrep.OwGfxStub);
        for (int i = 0; i < lm.Length; i++)
            if (i is not (9 or 10 or 18)) Assert.Equal(lm[i], rom.Data[at + i]);
        Assert.Equal(record & 0xFFFF, rom.Data[at + 9] | rom.Data[at + 10] << 8);   // ADC #imm
        Assert.Equal(record >> 16, rom.Data[at + 18]);                              // LDA #imm
        // ...and the hook that reaches it is at LM's site.
        Assert.Equal(RomPrep.OwGfxStub, rom.ReadValue(RomPrep.OwGfxHook + 1, 3));
    }

    [Fact]
    public void the_arm_stub_hands_the_loader_the_submaps_record()
    {
        if (Open() is not { } rom) { log.WriteLine("SKIP: no ROM"); return; }
        for (int submap = 0; submap < Overworld.Submaps; submap++)
        {
            var cpu = new Cpu65816(rom);
            cpu.PresetWidths(m8: false, x8: true);      // $00A132 REP #$20; the stub is entered 16-bit
            cpu.PresetX(submap * 2);                    // $00A130 ASL : TAX — the submap, doubled
            cpu.CallLong(RomPrep.OwGfxStub, 10_000);
            // What the loader reads: index + 1, so the DEC/×0x20 lands on entry 0x200+submap.
            int fe = cpu.Ram7E[0xFE] | cpu.Ram7E[0xFF] << 8;
            Assert.Equal(RomPrep.OwGfxRecordIndex + submap + 1, fe);
            // ...and what LM's half of the stub leaves behind: its own record cache, pointing at
            // the same entry, and #$42 for "the overworld's load" rather than a level's #$41.
            int want = RomPrep.GfxBypassRecords + (RomPrep.OwGfxRecordIndex + submap) * 0x20;
            Assert.Equal(want & 0xFFFF, cpu.Ram7F[0xC006] | cpu.Ram7F[0xC007] << 8);
            Assert.Equal(want >> 16, cpu.Ram7F[0xC008]);
            Assert.Equal(0x42, cpu.Ram7F[0xC009]);
        }
    }

    [Fact]
    public void a_repointed_slot_changes_that_submaps_tiles_and_no_others()
    {
        if (Open() is not { } rom) { log.WriteLine("SKIP: no ROM"); return; }
        var before = new Overworld(rom);
        var was = Enumerable.Range(0, Overworld.Submaps).Select(s => before.TilePixels(FgWord, s)).ToArray();

        // FG1 (record word 7) of Yoshi's Island only. GFX0F is a real 4bpp sheet in the ROM and
        // nothing like the land, so the pixels cannot coincide.
        rom.GfxSlotOverrides[(RomPrep.OwGfxRecordIndex + 1, 7)] = 0x0F;
        Assert.Equal(0x0F, rom.OwGfxBypass(1)![7]);
        Assert.Equal(0x1C, rom.OwGfxBypass(2)![7]);

        var after = new Overworld(rom);
        Assert.NotEqual(was[1], after.TilePixels(FgWord, 1));
        for (int s = 0; s < Overworld.Submaps; s++)
            if (s != 1) Assert.Equal(was[s], after.TilePixels(FgWord, s));

        // ...and the drawer reads it as a bypass: the vanilla file beside the one in force.
        var slots = Overworld.GfxSlots(rom, 1);
        var fg1 = slots.Single(b => b.Name == "FG1");
        Assert.Equal((0x1C, 0x0F), (fg1.Def, fg1.File));
        Assert.Equal(0x77, fg1.BypWord);                            // 0x70 + record word
        Assert.All(slots.Where(b => b.Name != "FG1"), b => Assert.Equal(b.Def, b.File));
        Assert.All(Overworld.GfxSlots(rom, 2), b => Assert.Equal(b.Def, b.File));
    }

    [Fact]
    public void a_written_record_is_the_only_one_that_changes()
    {
        if (Open() is not { } rom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Overworld.VanillaGfxRecord(rom);
        w[7] = 0x0F;
        Assert.Null(rom.WriteOwGfxBypass(4, w));
        Assert.Equal(0x0F, rom.OwGfxBypass(4)![7]);
        for (int s = 0; s < Overworld.Submaps; s++)
            if (s != 4) Assert.Equal(0x1C, rom.OwGfxBypass(s)![7]);
        // Nothing has spilled onto the last level's record, which sits just before entry 0x200.
        Assert.Equal(0, rom.LmGfxRecord(0x1FF)![0]);
    }

    /// <summary>
    /// The IN-GAME half: the overworld load arms $FE and the GFX loader then uploads that
    /// submap's record. Run for real under Cpu65816 — the loader entry is the same one the
    /// game's `JSR UploadSpriteGFX` tail reaches ($00AA50) — and compared against the same run
    /// with the record left vanilla: FG1's VRAM page must change and no other page with it.
    /// </summary>
    [Fact]
    public void the_loader_uploads_the_submaps_own_files()
    {
        if (Open() is not { } rom) { log.WriteLine("SKIP: no ROM"); return; }
        const int submap = 1, fg1Page = 0x0800;                     // FG1 is VRAM words $0000-$07FF

        List<(int Word, int Value)> Run()
        {
            var cpu = new Cpu65816(rom) { VramWrites = [] };
            cpu.Ram7E[0x1931] = (byte)(Overworld.Tileset + submap);
            cpu.PresetWidths(m8: false, x8: true);
            cpu.PresetX(submap * 2);
            cpu.CallLong(RomPrep.OwGfxStub, 10_000);                // the overworld's own hook
            cpu.PresetWidths(m8: true, x8: true);
            cpu.CallLong(RomPrep.GfxLoaderEntry, 40_000_000);
            return cpu.VramWrites!;
        }

        var vanilla = Run();
        Assert.NotEmpty(vanilla.Where(w => w.Word < fg1Page));
        log.WriteLine($"{vanilla.Count} words uploaded, pages "
                    + string.Join(" ", vanilla.Select(w => w.Word >> 11).Distinct().Order().Select(p => $"${p * 0x800:X4}")));

        int fo = rom.FileOffset(rom.LmGfxBypassBase + (RomPrep.OwGfxRecordIndex + submap) * 0x20);
        rom.Data[fo + 7 * 2] = 0x00;                                // FG1 → GFX00
        var repointed = Run();

        Assert.NotEqual(vanilla.Where(w => w.Word < fg1Page), repointed.Where(w => w.Word < fg1Page));
        Assert.Equal(vanilla.Where(w => w.Word >= fg1Page), repointed.Where(w => w.Word >= fg1Page));
    }

    /// <summary>
    /// ...and the submap's LAYER 3 files with them, which came for free and was never checked.
    /// Prep v14's layer-3 pass derives its record from `$FE`, and v18's overworld stub arms `$FE`
    /// with the submap index, so w0 bit 14 and words 15-12 (LG1-LG4) work on a submap exactly as
    /// on a level. Lunar Magic's *Overworld ▸ Submap Layer 3 GFX/Tilemap Bypass* dialog reads and
    /// writes these same words in our table with no stamp beyond v19's (measured 2026-09-11 in
    /// both directions; reference/OVERWORLD.md §4).
    ///
    /// NOT covered, because it is not implemented: the TILEMAP half (LT3, word 1, gated by w0
    /// bit 13). `L3Map` keys on `$010B` and hangs off level-load hooks, so a submap's word 1 is
    /// never read — LM can author it into the record and the game ignores it.
    /// </summary>
    [Fact]
    public void the_loader_uploads_a_submaps_layer_3_files_too()
    {
        if (Open() is not { } rom) { log.WriteLine("SKIP: no ROM"); return; }
        const int submap = 1, l3Lo = 0x4000, l3Hi = 0x5000;         // LG1-LG4 land in words $4000-$4FFF

        List<(int Word, int Value)> Run()
        {
            var cpu = new Cpu65816(rom) { VramWrites = [] };
            cpu.Ram7E[0x1931] = (byte)(Overworld.Tileset + submap);
            cpu.PresetWidths(m8: false, x8: true);
            cpu.PresetX(submap * 2);
            cpu.CallLong(RomPrep.OwGfxStub, 10_000);
            cpu.PresetWidths(m8: true, x8: true);
            cpu.CallLong(RomPrep.GfxLoaderEntry, 40_000_000);
            return cpu.VramWrites!;
        }
        static IEnumerable<(int Word, int Value)> L3(List<(int Word, int Value)> w)
            => w.Where(x => x.Word >= l3Lo && x.Word < l3Hi);

        // The record ships with the vanilla files in words 15-12 and the bypass OFF, so the pass
        // uploads 0x28-0x2B either way: turning the bit on alone must change nothing.
        int fo = rom.FileOffset(rom.LmGfxBypassBase + (RomPrep.OwGfxRecordIndex + submap) * 0x20);
        var off = Run();
        Assert.NotEmpty(L3(off));
        rom.Data[fo + 1] |= 0x40;                                   // w0 bit 14: layer-3 bypass on
        Assert.Equal(L3(off), L3(Run()));

        // Now repoint LG1 (w15). Only the layer-3 window moves, and only its first quarter.
        rom.Data[fo + 15 * 2] = 0x2B;                               // LG1: 0x28 -> 0x2B
        var moved = Run();
        Assert.NotEqual(L3(off), L3(moved));
        Assert.Equal(off.Where(w => w.Word < l3Lo || w.Word >= l3Hi),
                     moved.Where(w => w.Word < l3Lo || w.Word >= l3Hi));
        Assert.Equal(L3(off).Where(w => w.Word >= 0x4400), L3(moved).Where(w => w.Word >= 0x4400));
    }

    [Fact]
    public void a_submaps_slot_set_in_the_session_survives_the_project_and_a_build()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);

        s.OwGfxSubmap = 3;                                  // Forest of Illusion
        log.WriteLine(s.SetOwGfxSlot(word: 7, file: 0x0F)); // its FG1
        Assert.Equal(0x0F, s.OverworldGfxBins.Single(b => b.Name == "FG1").File);
        Assert.Equal(0x1C, s.OverworldGfxBins.Single(b => b.Name == "FG1").Def);
        s.OwGfxSubmap = 1;
        Assert.Equal(0x1C, s.OverworldGfxBins.Single(b => b.Name == "FG1").File);

        s.Save();
        Assert.Equal(0x0F, s.Project!.Data.Overworld.GfxSlots["3"][7]);
        log.WriteLine(s.Build());
        string built = Path.Combine(dir, "proj", "build", s.Project.Name + ".smc");
        Assert.True(File.Exists(built), "no built ROM");
        var rom = Rom.Load(built);
        Assert.Equal(0x0F, rom.OwGfxBypass(3)![7]);
        for (int sm = 0; sm < Overworld.Submaps; sm++)
            if (sm != 3) Assert.Equal(0x1C, rom.OwGfxBypass(sm)![7]);

        // The reopened project draws the Forest in its own graphics again.
        var s2 = new Services.EditorSession();
        Assert.True(s2.OpenProject(Path.Combine(dir, "proj", "project.pdp")), s2.Status);
        s2.OwGfxSubmap = 3;
        Assert.Equal(0x0F, s2.OverworldGfxBins.Single(b => b.Name == "FG1").File);
    }
}
