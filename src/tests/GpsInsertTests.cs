using Avalonia.Controls;
using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// GpsInsert end to end through Asar, against GPS files the test writes itself: GPS's own
/// main.asm, defines.asm and routines carry no licence, so the driver here is a minimal one laid
/// out as GpsBlocks reads (hook stub, marker, tables). The real folder was checked against gps.exe
/// by hand (GPS_PORT.md, phase 3).
/// </summary>
public class GpsInsertTests
{
    private const string MainAsm = """
        incsrc "__temp_settings.asm"
        org $06F624
            autoclean dl acts_likes_1
        freedata
        acts_likes_1:
            incbin "__acts_likes_1.bin"
        org $06F690
            PHB : PHX : REP #$30 : LDA.w #$0001
            autoclean JSL block_execute
        freecode
        block_execute:
            RTL
        block_bank_byte:
            incbin "__banks.bin"
        .end
            db "GPS_VeRsIoN", 3, 0
            dl block_bank_byte, block_pointers_1
            dw block_bank_byte_end-block_bank_byte, block_pointers_1_end-block_pointers_1
        freedata cleaned
        block_pointers_1:
            incbin "__pointers_1.bin"
        .end
        """;

    private const string Above = """
        db $42
        JMP Ret : JMP Above : JMP Ret
        JMP Ret : JMP Ret : JMP Ret : JMP Ret
        JMP Ret : JMP Ret : JMP Ret
        Above:
            %sparkle()
        Ret:
            RTL
        print "above only"
        """;

    private const string Wall = """
        db $37
        JMP Ret : JMP Ret : JMP Ret
        JMP Ret : JMP Ret : JMP Ret : JMP Ret
        JMP Ret : JMP Ret : JMP Ret
        JMP Feet : JMP Ret
        Feet:
            LDA #$02 : STA $19
        Ret:
            RTL
        """;

    /// <summary>A list.txt's tiles as Insert takes them from a project: tile → file.</summary>
    private static Dictionary<int, string> Blocks(string list)
        => GpsInsert.ParseList(list).ToDictionary(kv => kv.Key, kv => kv.Value.File);

    /// <summary>The driver and routines as a project keeps them (ProjectFile.GpsFiles).</summary>
    private static Dictionary<string, string> GpsFiles() => new()
    {
        ["main.asm"] = MainAsm,
        ["routines/sparkle.asm"] = "LDA #$01 : STA $1DFC\nRTL\n",
        ["routines/unused.asm"] = "RTL\n",
    };

    /// <summary>A block library (ProjectFile.BlockLibrary).</summary>
    private static Dictionary<string, string> Library() => new() { ["above.asm"] = Above, ["wall.asm"] = Wall };

    /// <summary>A GPS folder in a temp dir with <paramref name="list"/> as its list.txt.</summary>
    private static string Folder(string list)
    {
        var dir = Directory.CreateTempSubdirectory("pipedream-gps-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "blocks"));
        Directory.CreateDirectory(Path.Combine(dir, "routines"));
        File.WriteAllText(Path.Combine(dir, "main.asm"), MainAsm);
        File.WriteAllText(Path.Combine(dir, "blocks", "above.asm"), Above);
        File.WriteAllText(Path.Combine(dir, "blocks", "wall.asm"), Wall);
        File.WriteAllText(Path.Combine(dir, "routines", "sparkle.asm"), "LDA #$01 : STA $1DFC\nRTL\n");
        File.WriteAllText(Path.Combine(dir, "routines", "unused.asm"), "RTL\n");
        File.WriteAllText(Path.Combine(dir, "list.txt"), list);
        return dir;
    }

    /// <summary>A 1MB ROM as Lunar Magic leaves it: an acts-like table in RATS, pointed to from $06F624.</summary>
    private static Rom LmSaved()
    {
        var rom = TestRom.Create(size: 0x100000);
        var acts = new byte[0x8000];
        acts[0x2FF * 2] = 0x4C;                                   // a value the list leaves alone
        int table = RatsWriter.Allocate(rom, acts);
        rom.Data[rom.FileOffset(0x06F624)] = (byte)table;
        rom.Data[rom.FileOffset(0x06F625)] = (byte)(table >> 8);
        rom.Data[rom.FileOffset(0x06F626)] = (byte)(table >> 16);
        rom.Data[rom.FileOffset(0x06F63C)] = 0xFF;                // LM has not made pages 40+
        for (int i = 0; i < 100 * 3; i++) rom.Data[rom.FileOffset(0x0CB66F) + i] = 0xFF;   // no routines yet
        return rom;
    }

    private static int Claimed(Rom rom) => RatsWriter.EnumerateRats(rom).Sum(r => r.Size + 8);

    [AsarFact]
    public void inserts_each_tile_sharing_one_copy_per_file_and_leaves_acts_like_to_the_project()
    {
        var rom = LmSaved();
        var r = GpsInsert.Insert(rom, GpsFiles(), Library(), Blocks("200\tabove.asm\nR203-214 above.asm\n2FF wall.asm\n"));

        Assert.True(r.Ok, string.Join("\n", r.Errors));
        var gps = GpsBlocks.Read(rom)!;
        Assert.Equal([0x200, 0x203, 0x204, 0x213, 0x214, 0x2FF], gps.Blocks.Keys.Order());
        Assert.Single(gps.Blocks.Values.Where(b => b.Tile != 0x2FF).Select(b => b.Addr).Distinct());
        Assert.Equal(["Mario above"], gps.Blocks[0x200].HandledContacts);
        Assert.Equal(["wall feet"], gps.Blocks[0x2FF].HandledContacts);
        // The acts-like table came back as it was (the project's edits are the build's to write).
        int acts = rom.ReadValue(0x06F624, 3);                   // read as LM's hook does: no hook code here
        Assert.Equal([0, 0, 0x4C], new[] { 0x200, 0x203, 0x2FF }.Select(t => rom.ReadValue(acts + t * 2, 2)));
        Assert.Equal(["above only"], r.Blocks.Single(b => b.Tile == 0x200).Prints);
        Assert.Equal("tile 1FF cannot be a custom block (pages 02-7F only)",
            Assert.Single(GpsInsert.Insert(rom, GpsFiles(), Library(), new Dictionary<int, string> { [0x1FF] = "above.asm" }).Errors));
        Assert.Equal("tile 201 runs gone.asm, which is not in the block library",
            Assert.Single(GpsInsert.Insert(rom, GpsFiles(), Library(), new Dictionary<int, string> { [0x201] = "gone.asm" }).Errors));
        Assert.Equal("no block driver (main.asm) in the project",
            Assert.Single(GpsInsert.Insert(rom, new Dictionary<string, string>(), Library(), Blocks("200 above.asm\n")).Errors));

        // The routine went to freespace once, its slot names it, and only the used one is filled.
        int sparkle = rom.ReadValue(0x0CB66F, 3);
        Assert.Equal(0xA9, rom.ReadByte(sparkle));                                  // LDA #$01
        Assert.Equal(0xFFFFFF, rom.ReadValue(0x0CB66F + 3, 3));                     // unused.asm
        int above = gps.Blocks[0x200].Addr, code = rom.ReadValue(above + 5, 2) | above & 0xFF0000;   // JMP #2: Mario above
        Assert.Equal([0x22, (byte)sparkle, (byte)(sparkle >> 8), (byte)(sparkle >> 16)], rom.Data[rom.FileOffset(code)..][..4]);
    }

    /// <summary>Our own driver, with and without pages 40+: blocks go in, GpsBlocks.Read finds them
    /// behind its slots, the defines resolve, and a re-insert reclaims the same space.</summary>
    [AsarFact]
    public void our_driver_runs_blocks_on_both_page_layouts_and_reinserts_in_place()
    {
        var library = new Dictionary<string, string>
        {
            ["wall.asm"] = Wall,
            ["status.asm"] = "db $42\nJMP A : JMP A : JMP A : JMP A : JMP A : JMP A : JMP A : JMP A : JMP A : JMP A\nA:\n\tLDA !sprite_status,x : STA !14C8|!addr\n\tRTL\n",
        };
        foreach (bool hi in new[] { false, true })
        {
            var rom = LmSaved();
            if (hi) rom.Data[rom.FileOffset(0x06F63C)] = 0;                    // LM made pages 40+
            var blocks = hi ? Blocks("2FF wall.asm\n4005 status.asm\n") : Blocks("2FF wall.asm\n300 status.asm\n");
            var r = GpsInsert.Insert(rom, GpsInsert.Driver(), library, blocks);
            Assert.True(r.Ok, string.Join("\n", r.Errors));
            var gps = GpsBlocks.Read(rom)!;
            Assert.Equal(blocks.Keys.Order(), gps.Blocks.Keys);
            Assert.Equal(hi, gps.Tables.Pointers2 >= 0);
            Assert.Equal(["wall feet"], gps.Blocks[0x2FF].HandledContacts);
            Assert.Equal(0x37, gps.Blocks[0x2FF].Header);
            int once = Claimed(rom);
            Assert.True(GpsInsert.Insert(rom, GpsInsert.Driver(), library, blocks).Ok);
            Assert.Equal(once, Claimed(rom));
        }
    }

    /// <summary>Blocks gps.exe already put on a ROM count as foreign until a GPS folder is the
    /// project's driver; our own driver, or none, cannot carry them.</summary>
    [AsarFact]
    public void blocks_already_on_the_rom_are_foreign_until_their_gps_folder_is_imported()
    {
        var rom = LmSaved();
        Assert.Equal(0, GpsInsert.ForeignBlocks(rom, GpsInsert.Driver()));                // nothing there yet
        Assert.True(GpsInsert.Insert(rom, GpsFiles(), Library(), Blocks("200 above.asm\n2FF wall.asm\n")).Ok);   // as gps.exe would
        Assert.Equal(2, GpsInsert.ForeignBlocks(rom, GpsInsert.Driver()));
        Assert.Equal(2, GpsInsert.ForeignBlocks(rom, new Dictionary<string, string>()));
        Assert.Equal(0, GpsInsert.ForeignBlocks(rom, GpsFiles()));                       // their folder is in
    }

    [AsarFact]
    public void reinserting_frees_the_last_insertion_first()
    {
        var rom = LmSaved();
        var blocks = Blocks("200 above.asm\n2FF wall.asm\n");
        Assert.True(GpsInsert.Insert(rom, GpsFiles(), Library(), blocks).Ok);
        int once = Claimed(rom);

        Assert.True(GpsInsert.Insert(rom, GpsFiles(), Library(), blocks).Ok);
        Assert.Equal(once, Claimed(rom));                         // nothing leaked

        Assert.True(GpsInsert.Insert(rom, GpsFiles(), Library(), Blocks("300 wall.asm\n")).Ok);
        Assert.Equal([0x300], GpsBlocks.Read(rom)!.Blocks.Keys);
        Assert.Equal(0xFFFFFF, rom.ReadValue(0x0CB66F, 3));        // sparkle's slot emptied with its last user
    }

    [AsarFact]
    public void a_file_without_a_block_header_fails_and_leaves_the_rom_alone()
    {
        var rom = LmSaved();
        var library = Library();
        library["bad.asm"] = "db $00\nRTL\n";
        var before = rom.Data.ToArray();

        var r = GpsInsert.Insert(rom, GpsFiles(), library, Blocks("200 bad.asm\n"));

        Assert.False(r.Ok);
        Assert.Contains("db $42 or db $37", Assert.Single(r.Errors));
        Assert.Equal(before, rom.Data);
    }

    [AsarFact]
    public void a_project_build_inserts_its_imported_blocks_and_fails_on_an_asar_error()
    {
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName, rom = Path.Combine(dir, "base.sfc");
        File.WriteAllBytes(rom, LmSaved().Data);
        var p = Project.Create(Path.Combine(dir, "proj"), rom);
        Assert.Null(GpsInsert.Import(Folder("200 above.asm\n2FF wall.asm\n"), out var list, out var files, out var library));
        (p.Data.GpsFiles, p.Data.BlockLibrary) = (files, library);
        p.Data.Blocks = list.ToDictionary(kv => kv.Key.ToString("X3"), kv => kv.Value.File);
        p.Save();

        var (status, built) = RomBuilder.Build(p);
        Assert.NotNull(built);
        Assert.Equal([0x200, 0x2FF], GpsBlocks.Read(Rom.Load(built!))!.Blocks.Keys.Order());

        p.Data.BlockLibrary["wall.asm"] += "LDA.q #0\n";
        (status, built) = RomBuilder.Build(p);
        Assert.Null(built);
        Assert.StartsWith("build failed: custom blocks: ", status);
        Assert.Contains("wall.asm:", status);
    }

    /// <summary>Prep v30 carries LM's custom-block dispatch: the acts-like sites reach LM's
    /// trampolines, the trampolines branch into the slots GPS writes, and the acts-like table
    /// the editor and the game read is the one GPS repointed $06F624 to.</summary>
    [AsarFact]
    public void a_prepped_base_takes_custom_blocks_through_lunar_magics_dispatch()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var p = Project.Create(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla);
        Assert.Equal(RomPrep.Version, p.Data.BaseRom.PrepVersion);
        Assert.Null(GpsInsert.Import(Folder("200:0130 above.asm\n"), out var list, out var files, out var library));
        // What the session does with the import: the files and library in, the block into
        // Blocks, the acts-like into ActsAs.
        (p.Data.GpsFiles, p.Data.BlockLibrary) = (files, library);
        p.Data.Blocks["200"] = list[0x200].File;
        p.Data.Map16.ActsAs["200"] = list[0x200].ActsLike;
        p.Save();

        var (status, built) = RomBuilder.Build(p);

        Assert.True(built is not null, status);
        var rom = Rom.Load(built!);
        Assert.True(RomPrep.HasLmBlockDispatch(rom));
        Assert.Equal(0xF0, rom.ReadByte(0x06F665));                               // BEQ …
        Assert.Equal(0x06F690, 0x06F667 + (sbyte)rom.ReadByte(0x06F666));         // … to slot 0,
        Assert.Equal([0x8B, 0xDA, 0xC2, 0x30], rom.Data[rom.FileOffset(0x06F690)..][..4]);   // GPS's stub
        Assert.Equal(rom.ReadValue(0x06F624, 3), rom.LmActsAsBase);
        Assert.Equal(0x130, rom.ActsAs(0x200));
        Assert.Equal([0x200], GpsBlocks.Read(rom)!.Blocks.Keys);
    }

    /// <summary>The description is the first print read off the ASM, the one Insert's prints start
    /// with; a print built from more than one literal reads as none rather than half of it.</summary>
    [Fact]
    public void a_blocks_description_is_its_first_print()
    {
        Assert.Equal("above only", GpsInsert.Description(Above));
        Assert.Equal("Solid for sprites", GpsInsert.Description("db $42\n  PRINT \"Solid for sprites\"  ; desc\nprint \"@pswitch=1\"\n"));
        Assert.Null(GpsInsert.Description("db $42\nprint \"tile \", hex($200)\n"));
        Assert.Null(GpsInsert.Description("db $42\n; print \"commented out\"\nRTL\n"));
    }

    [Fact]
    public void import_reads_the_assembly_only_and_a_bad_folder_hands_nothing_over()
    {
        string src = Folder("200:0130 above.asm\n");
        Directory.CreateDirectory(Path.Combine(src, "blocks", "sub"));
        File.WriteAllText(Path.Combine(src, "blocks", "sub", "x.asm"), "RTL\n");
        File.WriteAllText(Path.Combine(src, "blocks", "notes.txt"), "");
        File.WriteAllText(Path.Combine(src, "gps.exe"), "");

        Assert.Null(GpsInsert.Import(src, out var list, out var files, out var library));
        Assert.Equal(("above.asm", 0x130), (list[0x200].File, list[0x200].ActsLike));
        Assert.Equal(["main.asm", "routines/sparkle.asm", "routines/unused.asm"], files.Keys.Order());   // no list.txt, no gps.exe
        Assert.Equal(["above.asm", "sub/x.asm", "wall.asm"], library.Keys.Order());
        Assert.Equal(Above, library["above.asm"]);

        Assert.Equal("list.txt line 2: blocks/missing.asm not found",
            GpsInsert.Import(Folder("200 above.asm\n201 missing.asm\n"), out list, out files, out library));
        Assert.Empty(list); Assert.Empty(files); Assert.Empty(library);
        Assert.Equal("list.txt line 1: block id out of bounds", GpsInsert.Import(Folder("1FF above.asm\n"), out _, out _, out _));
    }

    /// <summary>A project from before the library kept its import in gps/ beside the .pdp; opening
    /// it reads that into the .pdp, which from then on carries the blocks alone.</summary>
    [Fact]
    public void an_old_projects_gps_folder_moves_into_the_pdp_and_the_pdp_alone_carries_it()
    {
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName, rom = Path.Combine(dir, "base.sfc");
        File.WriteAllBytes(rom, LmSaved().Data);
        var p = Project.Create(Path.Combine(dir, "proj"), rom);
        p.Data.Blocks["200"] = "above.asm";
        p.Save();
        string old = Folder("");
        foreach (var f in new[] { "main.asm", "blocks/above.asm", "blocks/wall.asm", "routines/sparkle.asm" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(p.GpsFolder, f))!);
            File.Copy(Path.Combine(old, f), Path.Combine(p.GpsFolder, f));
        }

        var opened = Project.Open(p.FilePath);
        Assert.Equal(["main.asm", "routines/sparkle.asm"], opened.Data.GpsFiles.Keys.Order());
        Assert.Equal(["above.asm", "wall.asm"], opened.Data.BlockLibrary.Keys.Order());
        opened.Save();

        Directory.Delete(p.GpsFolder, true);
        var shared = Project.Open(p.FilePath);
        Assert.Equal(Above, shared.Data.BlockLibrary["above.asm"]);
        Assert.Equal(MainAsm, shared.Data.GpsFiles["main.asm"]);
        Assert.Equal("above.asm", shared.Data.Blocks["200"]);
    }

    [Fact]
    public void a_projects_blocks_are_keyed_by_tile_hex()
    {
        var data = new ProjectFile { Blocks = { ["200"] = "a.asm", ["2FF"] = "b.asm" } };
        Assert.Equal(new Dictionary<int, string> { [0x200] = "a.asm", [0x2FF] = "b.asm" }, GpsInsert.BlocksOf(data));
        Assert.Throws<FormatException>(() => GpsInsert.BlocksOf(new ProjectFile { Blocks = { ["zz"] = "a.asm" } }));
    }

    /// <summary>The block list as the Blocks window sees it: import fills it, Assign and New block
    /// edit it, the Map16 editor supplies the acts-like, and the contacts column reads the last
    /// build — so it is empty before one, and tells a block that handles nothing from one that
    /// was never built.</summary>
    [AsarFact]
    public void the_block_list_assigns_creates_and_reads_the_last_build()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new PipeDream.Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        s.ShowLevel(0x105);
        Assert.False(s.HasGpsDriver);

        s.ImportGps(Folder("200:0130 above.asm\n"));
        Assert.StartsWith("imported GPS folder: 2 block(s), 1 block tile(s), 1 acts-like", s.Status);
        Assert.True(s.HasGpsDriver);
        var e = Assert.Single(s.BlockEntries);
        Assert.Equal((0x200, "above.asm", true, 0x130, false), (e.Tile, e.File, e.InLibrary, e.ActsLike, e.Built));
        Assert.Equal(["above.asm", "wall.asm"], s.BlockNames);

        Assert.Equal("tile 2FF: wall.asm", s.AssignBlock(0x2FF, "wall.asm"));
        Assert.StartsWith("tile 1FF cannot be a custom block", s.AssignBlock(0x1FF, "wall.asm"));
        Assert.Equal("nope.asm is not in the library", s.AssignBlock(0x201, "nope.asm"));
        Assert.Equal("added spin.asm to the library", s.NewBlock("spin", out var made));
        Assert.Equal("spin.asm", made);
        Assert.Equal("spin.asm is already in the library", s.NewBlock("spin.asm", out _));
        Assert.Equal("a block name is letters, digits, _ and -", s.NewBlock("../x", out _));
        Assert.Equal("tile 201: spin.asm", s.AssignBlock(0x201, "spin.asm"));
        Assert.Equal("tile 202: spin.asm", s.AssignBlock(0x202, "spin.asm"));          // many tiles, one block
        Assert.Equal("spin.asm", s.BlockFileOf(0x201));
        string file = Path.Combine(dir, "lib.asm");
        File.WriteAllText(file, "db $42\nRTL\n");
        Assert.Equal("added lib.asm to the library", s.ImportBlockFile(file, out _));
        Assert.Equal("added lib.asm to the library", s.Status);

        s.Build();
        Assert.StartsWith("built ", s.Status);
        var rows = s.BlockEntries;
        Assert.Equal([0x200, 0x201, 0x202, 0x2FF], rows.Select(r => r.Tile));
        Assert.All(rows, r => Assert.True(r.Built));
        Assert.Equal(["Mario above"], rows[0].Handled);
        Assert.Empty(rows[1].Handled);                                  // the template: RTL on every contact
        Assert.Equal(["wall feet"], rows[3].Handled);
        var built = GpsBlocks.Read(Rom.Load(Path.Combine(dir, "proj", "build", "proj.smc")))!;
        Assert.Equal(built.Blocks[0x201].Addr, built.Blocks[0x202].Addr);           // one copy of spin
        Assert.Equal(3, built.Blocks.Values.Select(b => b.Addr).Distinct().Count()); // lib.asm, run nowhere, not inserted

        s.SetBlockSource("spin.asm", s.BlockSource("spin.asm")!.Replace("    RTL", "    LDA #$01 : STA $19\n    RTL"));
        s.Build();
        Assert.Equal(GpsBlocks.Contacts[..10], Assert.Single(s.BlockEntries, r => r.Tile == 0x201).Handled);   // the edit was built
        Assert.Equal(0x130, Rom.Load(Path.Combine(dir, "proj", "build", "proj.smc")).ActsAs(0x200));

        Assert.Equal("spin.asm runs on 2 tile(s) — take it off them first", s.DeleteBlock("spin.asm"));
        Assert.Equal("tile 201: block removed", s.AssignBlock(0x201, null));
        Assert.Equal("tile 201 has no block", s.AssignBlock(0x201, ""));
        Assert.Equal([0x200, 0x202, 0x2FF], s.BlockEntries.Select(r => r.Tile));
        Assert.Contains("spin.asm", s.BlockNames);                                 // the block stays
        Assert.Equal("deleted lib.asm", s.DeleteBlock("lib.asm"));
        Assert.False(Directory.Exists(Path.Combine(dir, "proj", "gps")));          // nothing beside the .pdp
    }

    /// <summary>A fresh project needs no GPS folder: the first block brings our driver, and it builds.</summary>
    [AsarFact]
    public void the_first_block_brings_our_driver_and_the_project_builds()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new PipeDream.Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        Assert.False(s.HasGpsDriver);
        s.NewBlock("spin", out _);
        Assert.True(s.HasGpsDriver);
        s.AssignBlock(0x201, "spin.asm");
        s.Build();
        var e = Assert.Single(s.BlockEntries);
        Assert.True(e.Built, s.Status);
    }

    /// <summary>A block opened in the external editor is a temp copy; a save there comes back into
    /// the library on the session's tick. The "editor" is a script that edits its argument.</summary>
    [Fact]
    public void a_block_saved_in_the_external_editor_comes_back_into_the_project()
    {
        if (!File.Exists(ReferenceRoms.Vanilla) || OperatingSystem.IsWindows()) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new PipeDream.Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        s.NewBlock("spin", out _);
        string editor = Path.Combine(dir, "ed");
        File.WriteAllText(editor, "#!/bin/sh\nprintf '; edited\\n' > \"$1\"\n");
        File.SetUnixFileMode(editor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        s.SetTextEditor(editor);

        Assert.StartsWith("opened spin.asm in ed;", s.OpenBlockExternally("spin.asm"));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (s.BlockSource("spin.asm") != "; edited\n" && DateTime.UtcNow < until) { Thread.Sleep(20); s.Tick(); }
        Assert.Equal("; edited\n", s.BlockSource("spin.asm"));
        Assert.Equal("nope.asm is not in the library", s.OpenBlockExternally("nope.asm"));
    }

    /// <summary>A base gps.exe already put blocks on: every block action is refused with the way
    /// out, the build will not clean them off, and importing their GPS folder lifts it.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void a_base_with_gps_blocks_is_locked_until_its_gps_folder_is_imported()
    {
        if (!File.Exists(ReferenceRoms.Vanilla) || !Asar.Available) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var p = Project.Create(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla);
        var baseRom = Rom.Load(p.BaseRomPath);
        Assert.True(GpsInsert.Insert(baseRom, GpsFiles(), Library(), Blocks("200 above.asm\n2FF wall.asm\n")).Ok);
        RatsWriter.SaveAs(baseRom, p.BaseRomPath);
        p.Data.BaseRom.Sha256 = RomHash.HeaderlessSha256File(p.BaseRomPath);         // a hack with GPS blocks as its base
        p.Data.BaseRom.PrepVersion = 0;
        p.Save();

        var s = new PipeDream.Services.EditorSession();
        Assert.True(s.OpenProject(p.FilePath), s.Status);
        string locked = s.BlocksLockedReason!;
        Assert.StartsWith("This base ROM already has 2 custom blocks inserted by GPS", locked);
        Assert.Contains("Import GPS folder", locked);
        Assert.Equal(locked, s.NewBlock("spin", out var made));
        Assert.Null(made);
        Assert.Equal(locked, s.AssignBlock(0x201, "above.asm"));
        Assert.Empty(s.BlockNames);
        var w = new PipeDream.Ui.BlocksWindow(s);
        Assert.True(w.GetControl<Border>("LockNotice").IsVisible);
        Assert.Equal(locked, w.GetControl<TextBlock>("LockText").Text);
        Assert.False(w.GetControl<Button>("NewButton").IsEnabled);
        Assert.False(w.GetControl<Button>("ImportButton").IsEnabled);

        // A project that got blocks before this guard existed still cannot build over them.
        var stale = Project.Open(p.FilePath);
        stale.Data.GpsFiles = GpsInsert.Driver();
        stale.Data.BlockLibrary["x.asm"] = Wall;
        stale.Data.Blocks["300"] = "x.asm";
        Assert.StartsWith("build failed: the base ROM already has 2 custom blocks from GPS", RomBuilder.Build(stale).status);
        Assert.Equal(2, GpsBlocks.Read(Rom.Load(p.BaseRomPath))!.Blocks.Count);         // and they are still there

        s.ImportGps(Folder("200 above.asm\n2FF wall.asm\n"));
        Assert.Null(s.BlocksLockedReason);
        w = new PipeDream.Ui.BlocksWindow(s);
        Assert.False(w.GetControl<Border>("LockNotice").IsVisible);
        Assert.True(w.GetControl<Button>("NewButton").IsEnabled);
        Assert.Equal("added spin.asm to the library", s.NewBlock("spin", out _));
    }

    /// <summary>The picker leads with "none", marks the tile's current block, and names the tiles.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_block_picker_lists_none_first_and_marks_the_current_block()
    {
        var w = new PipeDream.Ui.BlockPickerWindow(new PipeDream.Services.EditorSession(), null);
        var rows = ((System.Collections.IEnumerable)w.GetControl<ListBox>("List").ItemsSource!).Cast<PipeDream.Ui.BlockPickerWindow.Row>().ToList();
        Assert.Equal("(none)", Assert.Single(rows).Name);
        Assert.Null(rows[0].File);
        Assert.Equal(PipeDream.Ui.UiColors.Selection, rows[0].Highlight);                     // none IS the current block
        Assert.Null(w.Picked);                                                     // cancelled until Use
    }

    /// <summary>Selecting a library block arms Remove and Edit; the editor's Save writes the ASM
    /// back and Cancel drops it.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_blocks_window_lists_the_library_and_edits_the_selected_block()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new PipeDream.Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        s.NewBlock("spin", out _);
        s.AssignBlock(0x201, "spin.asm");
        s.NewBlock("lib", out _);

        var w = new PipeDream.Ui.BlocksWindow(s);
        var library = w.GetControl<ListBox>("Library");
        var rows = ((System.Collections.IEnumerable)library.ItemsSource!).Cast<PipeDream.Ui.BlocksWindow.LibraryRow>().ToList();
        Assert.Equal([new("lib.asm", "unused"), new("spin.asm", "on 1 tile")], rows);
        Assert.False(w.GetControl<Button>("EditButton").IsEnabled);
        library.SelectedItem = rows[1];
        Assert.True(w.GetControl<Button>("EditButton").IsEnabled);
        Assert.True(w.GetControl<Button>("RemoveButton").IsEnabled);

        var edit = new PipeDream.Ui.BlockEditWindow(s, "spin.asm");
        Assert.Equal("spin.asm", edit.Title);
        var editor = edit.GetControl<AvaloniaEdit.TextEditor>("Editor");
        Assert.True(editor.ShowLineNumbers);
        Assert.Equal("Asar", editor.SyntaxHighlighting?.Name);
        Assert.Equal("JetBrains Mono", editor.FontFamily.FamilyNames[0]);
        edit.GetControl<AvaloniaEdit.TextEditor>("Editor").Text = "; cancelled";
        edit.Close();                                                               // closing is a cancel
        Assert.NotEqual("; cancelled", s.BlockSource("spin.asm"));
        edit = new PipeDream.Ui.BlockEditWindow(s, "spin.asm");
        edit.GetControl<AvaloniaEdit.TextEditor>("Editor").Text = "; saved";
        edit.GetControl<Button>("Save").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("; saved", s.BlockSource("spin.asm"));
    }

    /// <summary>New block: the icons are vanilla tiles drawn in the level's graphics; Okay adds the
    /// template under the name with the picked icon, a bad name stays open with the reason, and
    /// Cancel adds nothing.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_new_block_modal_names_the_block_and_picks_its_icon()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new PipeDream.Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        s.ShowLevel(0x105);
        var okay = new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent);

        var w = new PipeDream.Ui.NewBlockWindow(s);
        var icons = w.GetControl<ListBox>("Icons");
        var rows = ((System.Collections.IEnumerable)icons.ItemsSource!).Cast<PipeDream.Ui.NewBlockWindow.IconRow>().ToList();
        Assert.Equal(PipeDream.Services.EditorSession.BlockIconTiles, rows.Select(r => r.Tile));
        Assert.All(rows, r => Assert.NotNull(r.Image));
        w.GetControl<Button>("Okay").RaiseEvent(okay);
        Assert.Equal("a block name is letters, digits, _ and -", w.GetControl<TextBlock>("Hint").Text);
        Assert.Null(w.Made);
        w.GetControl<TextBox>("BlockName").Text = "spin";
        icons.SelectedItem = rows[2];
        w.GetControl<Button>("Okay").RaiseEvent(okay);
        Assert.Equal("spin.asm", w.Made);
        Assert.Equal(0x130, s.BlockIcon("spin.asm"));
        Assert.Contains("db $42", s.BlockSource("spin.asm"));                     // the boilerplate Edit opens

        var cancelled = new PipeDream.Ui.NewBlockWindow(s);
        cancelled.GetControl<TextBox>("BlockName").Text = "other";
        cancelled.Close();
        Assert.Equal(["spin.asm"], s.BlockNames);
        Assert.Equal("deleted spin.asm", s.DeleteBlock("spin.asm"));
        Assert.Null(s.BlockIcon("spin.asm"));
    }

    /// <summary>The block editor's colours pick out each kind of token in a block.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void asar_highlighting_colours_comments_labels_defines_macros_opcodes_and_numbers()
    {
        var doc = new AvaloniaEdit.Document.TextDocument("Above: LDA !sprite_status,x : %glitter() ; hi\n.sub db $42, %0101, 3\n");
        var h = new AvaloniaEdit.Highlighting.DocumentHighlighter(doc, PipeDream.Ui.BlockEditWindow.Highlighting);
        string? ColourAt(int line, string token)
        {
            var l = doc.GetLineByNumber(line);
            int at = l.Offset + doc.GetText(l).IndexOf(token);
            return h.HighlightLine(line).Sections.LastOrDefault(x => x.Offset <= at && at < x.Offset + x.Length)?.Color.Name;
        }
        Assert.Equal("Label", ColourAt(1, "Above:"));
        Assert.Equal("Opcode", ColourAt(1, "LDA"));
        Assert.Equal("Define", ColourAt(1, "!sprite_status"));
        Assert.Equal("Macro", ColourAt(1, "%glitter"));
        Assert.Equal("Comment", ColourAt(1, "; hi"));
        Assert.Equal("Label", ColourAt(2, ".sub"));
        Assert.Equal("Directive", ColourAt(2, "db"));
        Assert.Equal("Number", ColourAt(2, "$42"));
        Assert.Equal("Number", ColourAt(2, "%0101"));
    }

    /// <summary>The linter is quiet on good code — the template, our driver, the test blocks — and
    /// names each kind of typo where it is.</summary>
    [Fact]
    public void the_linter_passes_good_asm_and_points_at_typos()
    {
        foreach (string good in GpsInsert.Driver().Values.Concat([MainAsm, Above, Wall, PipeDream.Services.EditorSession.BlockTemplate("x")]))
            Assert.Empty(AsmLint.Check(good));
        Assert.Empty(AsmLint.Check("?loop LDA $00 : BNE ?loop\n?+ RTL\nprint \"costs $ : 5\"\n!x #= 5%2\nmacro m(a)\n\tLDA <a>\nendmacro\n"));

        string bad = "LDQ #1\nRTL #2\nLDA\nLDA.q #0\nLDA $G0 : ORA %102\nJML [$0000\nprint \"oops\n";
        string At(AsmIssue i) => bad.Substring(i.Offset, i.Length) + " — " + i.Message;
        Assert.Equal(
        [
            "LDQ — unknown instruction LDQ",
            "#2 — RTL takes no operand",
            "LDA — LDA needs an operand",
            ".q — .q is not a size: .b, .w or .l",
            "$G0 — not a hex number",
            "%102 — not a binary number",
            "[$0000 — this [ is never closed",
            "\"oops — this quote is never closed",
        ], AsmLint.Check(bad).Select(At));
    }

    /// <summary>The editor squiggles what the linter finds and counts it, re-checked as you type.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_block_editor_counts_problems_as_you_type()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new PipeDream.Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        s.NewBlock("spin", out _);
        var w = new PipeDream.Ui.BlockEditWindow(s, "spin.asm");
        var problems = w.GetControl<TextBlock>("Problems");
        Assert.Equal("", problems.Text);                                          // the template is clean
        w.GetControl<AvaloniaEdit.TextEditor>("Editor").Text = "LDQ #1\nRTS #2\n";
        Assert.Equal("2 problems", problems.Text);
        Assert.Equal("line 1: unknown instruction LDQ\nline 2: RTS takes no operand", ToolTip.GetTip(problems));
        Assert.Equal(2, w.Lint.Issues.Count);
    }

    /// <summary>Preferences shows each program, or what happens while it is unset.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void preferences_show_the_editor_and_emulator_or_their_defaults()
    {
        var s = new PipeDream.Services.EditorSession();
        s.SetTextEditor("/usr/bin/vim");
        s.SetEmulator(null);
        var w = new PipeDream.Ui.PreferencesWindow(s);
        Assert.Equal("/usr/bin/vim", w.GetControl<TextBox>("EditorPath").Text);
        Assert.Equal("Mesen, found automatically", w.GetControl<TextBox>("EmulatorPath").Text);
        s.SetTextEditor(null);
    }

    /// <summary>The window loads and reads an empty session without a project.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_blocks_window_opens_on_a_session_without_a_project()
    {
        var w = new PipeDream.Ui.BlocksWindow(new PipeDream.Services.EditorSession());
        Assert.Empty((System.Collections.IEnumerable)w.GetControl<ListBox>("Library").ItemsSource!);
        Assert.Contains("no blocks yet", w.GetControl<TextBlock>("Hint").Text);
        Assert.False(w.GetControl<Button>("RemoveButton").IsEnabled);              // nothing selected
        Assert.False(w.GetControl<Button>("EditButton").IsEnabled);
    }

    [Fact]
    public void the_list_reads_singles_ranges_rectangles_and_acts_like()
    {
        var list = GpsInsert.ParseList("""
            ; comment
            200:0130	a.asm
            210-212 b.asm
            R220-231:0025 c.asm
            210 d.asm
            200 e.asm
            @dsc
            300	0	not a block
            """);

        Assert.Equal([0x200, 0x210, 0x211, 0x212, 0x220, 0x221, 0x230, 0x231], list.Keys.Order());
        Assert.Equal(new GpsInsert.Entry("e.asm", 0x130, 6), list[0x200]);       // a later line keeps the acts-like
        Assert.Equal(("d.asm", -1), (list[0x210].File, list[0x210].ActsLike));
        Assert.Equal(("c.asm", 0x25), (list[0x231].File, list[0x231].ActsLike));
        Assert.Throws<FormatException>(() => GpsInsert.ParseList("1FF a.asm"));
        Assert.Throws<FormatException>(() => GpsInsert.ParseList("200a.asm"));
    }
}
