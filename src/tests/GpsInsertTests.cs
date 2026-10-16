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
        Assert.Equal("no GPS driver: import a GPS folder for its main.asm",
            Assert.Single(GpsInsert.Insert(rom, new Dictionary<string, string>(), Library(), Blocks("200 above.asm\n")).Errors));

        // The routine went to freespace once, its slot names it, and only the used one is filled.
        int sparkle = rom.ReadValue(0x0CB66F, 3);
        Assert.Equal(0xA9, rom.ReadByte(sparkle));                                  // LDA #$01
        Assert.Equal(0xFFFFFF, rom.ReadValue(0x0CB66F + 3, 3));                     // unused.asm
        int above = gps.Blocks[0x200].Addr, code = rom.ReadValue(above + 5, 2) | above & 0xFF0000;   // JMP #2: Mario above
        Assert.Equal([0x22, (byte)sparkle, (byte)(sparkle >> 8), (byte)(sparkle >> 16)], rom.Data[rom.FileOffset(code)..][..4]);
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

    /// <summary>The picker leads with "none", marks the tile's current block, and names the tiles.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_block_picker_lists_none_first_and_marks_the_current_block()
    {
        var w = new PipeDream.Ui.BlockPickerWindow(new PipeDream.Services.EditorSession(), [0x200, 0x201], null);
        var rows = ((System.Collections.IEnumerable)w.GetControl<ListBox>("List").ItemsSource!).Cast<PipeDream.Ui.BlockPickerWindow.Row>().ToList();
        Assert.Equal("(none)", Assert.Single(rows).Name);
        Assert.Null(rows[0].File);
        Assert.Equal(PipeDream.Ui.UiColors.Selection, rows[0].Highlight);                     // none IS the current block
        Assert.Contains("2 tiles selected", w.GetControl<TextBlock>("Subject").Text);
        Assert.Null(w.Picked);                                                     // cancelled until Use
    }

    /// <summary>The window loads and reads an empty session without a project.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_blocks_window_opens_on_a_session_without_a_project()
    {
        var w = new PipeDream.Ui.BlocksWindow(new PipeDream.Services.EditorSession());
        Assert.Empty((System.Collections.IEnumerable)w.GetControl<ListBox>("Blocks").ItemsSource!);
        Assert.Empty((System.Collections.IEnumerable)w.GetControl<ListBox>("Library").ItemsSource!);
        Assert.Contains("no blocks yet", w.GetControl<TextBlock>("Hint").Text);
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
