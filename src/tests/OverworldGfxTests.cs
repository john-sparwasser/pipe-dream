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
