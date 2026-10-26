using System.IO.Compression;
using System.Security.Cryptography;
using PipeDream.Services;
using Xunit;

namespace PipeDream.Ui.Tests;

/// <summary>Skips without the vanilla ROM, the extracted SA-1 Pack beside the oracles, or Asar.</summary>
public sealed class Sa1BuildFactAttribute : FactAttribute
{
    public Sa1BuildFactAttribute()
    {
        foreach (var p in new[] { ReferenceRoms.Vanilla, Path.Combine(ReferenceRoms.Sa1Pack, "sa1.asm") })
            if (!File.Exists(p)) { Skip = "not present: " + p; return; }
        if (!Asar.Available) Skip = "Asar not available (PIPEDREAM_ASAR)";
    }
}

/// <summary>
/// ASM ▸ SA-1 and the build step behind it (reference/SA1_PORT.md, phase 1): the switch is a pinned
/// reference in the project, off builds exactly what a project without it builds, on builds an SA-1
/// image, and the pack's download is refused unless it is the release the catalogue names.
/// </summary>
public class Sa1BuildTests
{
    private static EditorSession NewProject()
    {
        string dir = Directory.CreateTempSubdirectory("pipedream-sa1-").FullName;
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        s.ShowLevel(0x105);
        return s;
    }

    private static byte[] Built(EditorSession s)
    {
        string status = s.Build();
        Assert.StartsWith("built", status);
        return File.ReadAllBytes(Path.Combine(s.Project!.Folder, "build", s.Project.Name + ".smc"));
    }

    [Sa1BuildFact]
    public async Task switching_sa1_off_builds_what_a_project_that_never_had_it_builds()
    {
        Assert.True(TestConfigDir.SeedSa1());
        var s = NewProject();
        var plain = Built(s);
        Assert.StartsWith("SA-1 on", await s.SetSa1(true));
        Assert.StartsWith("SA-1 off", await s.SetSa1(false));
        Assert.Equal(plain, Built(s));
        Assert.False(s.Sa1On);
        Assert.Single(s.Project!.Data.BuiltinPatches);                 // the pin stays, switched off
    }

    [Sa1BuildFact]
    public async Task with_sa1_on_the_build_is_an_sa1_image_and_the_pin_survives_a_save()
    {
        Assert.True(TestConfigDir.SeedSa1());
        var s = NewProject();
        await s.SetSa1(true);
        var rom = Rom.FromBytes(Built(s));
        Assert.Equal(0x23, rom.Data[rom.HeaderOffset + 0x7FD5]);
        Assert.IsType<Sa1Map>(rom.Map);
        // The prep's SA-1 form is in: the overworld palette hook reads the pack's RAM.
        Assert.Equal(0x7931, rom.ReadValue(RomPrep.OwPaletteHook + 2, 2));

        s.Save();
        var data = ProjectFile.FromJson(File.ReadAllText(s.Project!.FilePath));
        var pin = Assert.Single(data.BuiltinPatches);
        Assert.Equal(("sa1", "1.40", BuiltinPatches.Sa1.Sha256, true), (pin.Id, pin.Version, pin.Sha256, pin.Enabled));
    }

    /// <summary>Asar tags the pack's freecode, so the build's allocator steps around all of it: no
    /// block the pack writes in expanded space lies outside a RATS block.</summary>
    [Sa1BuildFact]
    public void every_block_the_pack_writes_in_expanded_space_is_rats_protected()
    {
        Assert.True(TestConfigDir.SeedSa1());
        var s = NewProject();
        var rom = Rom.Load(s.Project!.BaseRomPath);
        var (errors, written) = new Sa1Target(ReferenceRoms.Sa1Pack).Install(rom, s.Project.Data.BaseRom.PrepVersion);
        Assert.Empty(errors);
        var rats = RatsWriter.EnumerateRats(rom).ToList();
        var loose = written.Select(b => (Pc: rom.Map.SnesToPc(b.Snes), b.Length))
                           .Where(b => b.Pc >= 0x80000)
                           .Where(b => !rats.Any(r => r.PcOffset <= b.Pc && b.Pc + b.Length <= r.PcOffset + 8 + r.Size + 1))
                           .Select(b => $"{b.Pc:X6}+{b.Length}").ToList();
        Assert.Empty(loose);
    }

    /// <summary>On SA-1 the direct page is $3000, so UberASM's dispatch, which stores the routine's address at $00 and jumps
    /// with JML [$0000], must name $3000: through $0000 it jumped through work RAM nobody wrote and the game fell over (Mesen).
    /// The game mode it dispatches on is read from the pack's BW-RAM, and the hooks accept the pack's forms of SMW's code.</summary>
    [Sa1BuildFact]
    public async Task uberasm_on_sa1_dispatches_through_the_direct_page_and_reads_the_moved_ram()
    {
        Assert.True(TestConfigDir.SeedSa1());
        var s = NewProject();
        s.Project!.Data.Uber.Files["gamemode/gm.asm"] = "main:\n    RTL\n"u8.ToArray();
        UberImport.ParseList("gamemode:\n14 gm.asm\n", s.Project.Data.Uber, []);
        await s.SetSa1(true);
        var rom = Rom.FromBytes(Built(s));
        int frame = rom.ReadValue(0x008071, 3);                                        // Frame, from the hook's JML
        var code = rom.Data.AsSpan(rom.FileOffset(frame), 0x40).ToArray();
        Assert.Contains("AF000140", Convert.ToHexString(code));                     // LDA.l $400100: the game mode in BW-RAM
        string all = Convert.ToHexString(rom.Data);
        Assert.Contains("DC0030", all);                                              // JML [$3000]
        Assert.DoesNotContain("E230DC0000", all);                                    // and never SEP #$30 : JML [$0000]
    }

    /// <summary>Custom sprites on SA-1 go round the pack's loader rather than through it: the shooter
    /// number hooks the pack's JML at $02AB9E (which covers SMW's LDA $04), the spawn calls what the
    /// pack calls there, and the load table's eleven sites keep the pack's own hooks (its table at
    /// $418A00 is !1938). In Mesen (Sa1DriversSetup) the sprites run as on LoROM.</summary>
    [Sa1BuildFact]
    public async Task custom_sprites_on_sa1_hook_around_the_packs_loader()
    {
        Assert.True(TestConfigDir.SeedSa1());
        var s = NewProject();
        var data = s.Project!.Data;
        data.Sprites["probe"] = new ProjectFile.SpriteEntry
        {
            Config = new PixiConfig { ActLike = 0x36, AsmFile = "probe.asm" }.ToJson(),
            Source = "print \"INIT \",pc\n    RTL\nprint \"MAIN \",pc\n    RTL\n",
        };
        data.SpriteSlots["1A"] = "probe";
        await s.SetSa1(true);
        var rom = Rom.FromBytes(Built(s));
        var pack = Rom.Load(s.Project.BaseRomPath);
        var (errors, _) = new Sa1Target(ReferenceRoms.Sa1Pack).Install(pack, data.BaseRom.PrepVersion);
        Assert.Empty(errors);

        Assert.Equal(0x5C, rom.ReadByte(0x02AB9E));                                      // ours, where the pack's was
        Assert.NotEqual(pack.ReadValue(0x02AB9F, 3), rom.ReadValue(0x02AB9F, 3));
        Assert.Equal(pack.ReadValue(0x02A9CA, 3), rom.ReadValue(rom.ReadValue(0x02A9CA, 3) + 5, 3));   // Spawn: LDA $6040,x : PHA : JSL the pack's
        foreach (int at in (int[])[0x02A856, 0x02ABF2, 0x02A8BB, 0x02A936, 0x01AC9C, 0x02D088, 0x02FF15, 0x038712, 0x03B8BA])
            Assert.Equal(Convert.ToHexString(pack.Data, pack.FileOffset(at), 4), Convert.ToHexString(rom.Data, rom.FileOffset(at), 4));
    }

    /// <summary>Every build carries the report: with SA-1 on, what breaks there, named and lined; off,
    /// what needs SA-1. The Patches window badges the same.</summary>
    [Sa1BuildFact]
    public async Task each_build_reports_what_breaks_on_sa1_or_needs_it()
    {
        Assert.True(TestConfigDir.SeedSa1());
        var s = NewProject();
        var data = s.Project!.Data;
        data.Patches.Add(new() { Name = "lives.asm", Source = "org $00F5F8\n    INC $0DBE\n" });
        data.Patches.Add(new() { Name = "fast.asm", Source = "if read1($00FFD5) == $23\n    sa1rom\nendif\norg $108000\n    STA $3100\n" });
        string off = s.Build();
        Assert.Contains("patch fast.asm needs SA-1 (fast.asm:5: STA $3100", off);
        Assert.DoesNotContain("lives.asm needs", off);
        Assert.DoesNotContain("SA-1: patch", off);
        Assert.Equal("needs SA-1", s.Patches.Single(p => p.Name == "fast.asm").Sa1);
        Assert.Null(s.Patches.Single(p => p.Name == "lives.asm").Sa1);

        await s.SetSa1(true);
        string on = s.Build();
        Assert.Contains("SA-1: patch lives.asm breaks (lives.asm:2: INC $0DBE — SMW's RAM at its LoROM address; SA-1 keeps it at $6DBE", on);
        Assert.Equal("breaks on SA-1", s.Patches.Single(p => p.Name == "lives.asm").Sa1);
        Assert.Contains("lives.asm", s.Sa1Summary(on: true));
    }

    /// <summary>The badge reaches the screen: in the Patches window, with SA-1 on, beside the patch, the
    /// lines behind it on hover.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task the_patches_window_badges_a_patch_that_breaks_on_sa1()
    {
        if (!File.Exists(ReferenceRoms.Vanilla) || !Asar.Available || !TestConfigDir.SeedSa1()) return;
        var s = NewProject();
        s.Project!.Data.Patches.Add(new() { Name = "lives.asm", Source = "org $00F5F8\n    INC $0DBE\n" });
        await s.SetSa1(true);
        var w = new PatchesWindow(s);
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var badge = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<Avalonia.Controls.TextBlock>()
            .Single(t => t.Text == "breaks on SA-1");
        Assert.StartsWith("lives.asm:2: INC $0DBE — SMW's RAM", Avalonia.Controls.ToolTip.GetTip(badge) as string);
        w.Close();
    }

    /// <summary>The overworld's tables stay where they are under the pack (it moves RAM, not data), so
    /// every overworld edit changes the same bytes to the same values on SA-1 as on LoROM. The build
    /// writes them into the tables the loader names, after the pack: a pack that rewrote a loader
    /// operand would send a writer elsewhere, and this would see it.</summary>
    [Sa1BuildFact]
    public async Task overworld_edits_land_on_sa1_where_they_land_on_lorom()
    {
        Assert.True(TestConfigDir.SeedSa1());
        var s = NewProject();
        var plainLo = Built(s);
        await s.SetSa1(true);
        var plainSa1 = Built(s);

        var ow = s.Overworld!;
        var o = s.Project!.Data.Overworld;
        static string Words(ushort[] w, int i, ushort v) { var c = (ushort[])w.Clone(); c[i] = v; return Convert.ToBase64String(ProjectSession.BytesOf(c)); }
        static string Bytes(byte[] b, int i) { var c = (byte[])b.Clone(); c[i] ^= 0x01; return Convert.ToBase64String(c); }
        o.Layer2 = Words(ow.Layer2, 0x101, ow.Layer2[0x100]);
        o.Layer1 = Words(ow.Layer1, 0x101, ow.Layer1[0x100]);
        o.EventPieces = Words(ow.EventPieces, 3, (ushort)(ow.EventPieces[3] ^ 0x0001));
        o.Layer1Defs = Words(ow.Layer1Defs, 5, (ushort)(ow.Layer1Defs[5] ^ 0x0001));
        o.BaseEvents = Bytes(ow.BaseEventTable, 2);
        o.ExitDirs = Bytes(ow.ExitDirTable, 2);
        o.Warps = Bytes(ow.WarpTable, 0);
        o.Exits = Bytes(ow.ExitTable, 0);
        int colour = Palette.OverworldSource(s.Rom!, 0, 0x22)!.Value;                 // the prep's palette table, bank $13
        o.Palette[colour] = 0x1234;

        var editedSa1 = Built(s);
        await s.SetSa1(false);
        var editedLo = Built(s);

        // In place, below the expanded area: freespace moves with the pack's code, and is the allocator's.
        static List<(int, byte)> Diff(byte[] a, byte[] b) => [.. Enumerable.Range(0, 0x80000).Where(i => a[i] != b[i]).Select(i => (i, b[i]))];
        var lo = Diff(plainLo, editedLo);
        Assert.Contains(lo, d => Rom.PcToSnes(d.Item1) >> 16 == 0x05);              // base events, layer 1 defs
        Assert.Contains(lo, d => Rom.PcToSnes(d.Item1) >> 16 == 0x0C);              // event pieces
        Assert.Equal(lo, Diff(plainSa1, editedSa1));
        foreach (var built in new[] { editedLo, editedSa1 })
            Assert.Equal(0x1234, Rom.FromBytes(built).ReadValue(colour, 2));
    }

    [Sa1BuildFact]
    public async Task sa1_cannot_be_switched_on_over_a_base_at_another_prep_version()
    {
        var s = NewProject();
        s.Project!.Data.BaseRom.PrepVersion = LmSa1Forms.PrepVersion - 1;
        Assert.Contains($"prep v{LmSa1Forms.PrepVersion}", await s.SetSa1(true));
        Assert.False(s.Sa1On);
        Assert.Empty(s.Project.Data.BuiltinPatches);
    }

    [Fact]
    public void a_project_without_the_field_loads_with_sa1_off()
    {
        var data = ProjectFile.FromJson("""{ "SchemaVersion": 1 }""");
        Assert.Empty(data.BuiltinPatches);
        Assert.Null(data.Sa1);
        Assert.DoesNotContain("\"Sa1\"", data.ToJson());
    }

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var w = new StreamWriter(z.CreateEntry(name).Open())) w.Write(text);
        return ms.ToArray();
    }

    private static BuiltinPatch Entry(byte[] zip, string id) =>
        new(id, "Test Pack", "1.0", "https://example.invalid/x.zip",
            Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant(), "main.asm", "test");

    [Fact]
    public void a_release_with_the_catalogue_hash_is_extracted_into_the_cache()
    {
        var zip = Zip(("main.asm", "; hi"), ("sub/x.asm", "; x"));
        var p = Entry(zip, "t-" + Guid.NewGuid().ToString("N"));
        Assert.Null(BuiltinPatches.Install(p, zip));
        Assert.True(BuiltinPatches.Installed(p));
        Assert.Equal("; x", File.ReadAllText(Path.Combine(BuiltinPatches.Folder(p), "sub", "x.asm")));
    }

    [Fact]
    public void a_download_with_any_other_hash_is_refused_and_nothing_is_installed()
    {
        var zip = Zip(("main.asm", "; hi"));
        var p = Entry(zip, "t-" + Guid.NewGuid().ToString("N")) with { Sha256 = new string('0', 64) };
        Assert.Contains("not the release", BuiltinPatches.Install(p, zip));
        Assert.False(BuiltinPatches.Installed(p));
    }
}
