using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// GpsInsert end to end through Asar, against a GPS folder the test writes itself: GPS's own
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
    public void inserts_each_listed_tile_with_its_acts_like_and_shares_one_copy_per_file()
    {
        var rom = LmSaved();
        var r = GpsInsert.Insert(rom, Folder("200:0130\tabove.asm\nR203-214:0025 above.asm\n2FF wall.asm\n"));

        Assert.True(r.Ok, string.Join("\n", r.Errors));
        var gps = GpsBlocks.Read(rom)!;
        Assert.Equal([0x200, 0x203, 0x204, 0x213, 0x214, 0x2FF], gps.Blocks.Keys.Order());
        Assert.Single(gps.Blocks.Values.Where(b => b.Tile != 0x2FF).Select(b => b.Addr).Distinct());
        Assert.Equal(["Mario above"], gps.Blocks[0x200].HandledContacts);
        Assert.Equal(["wall feet"], gps.Blocks[0x2FF].HandledContacts);
        int acts = rom.ReadValue(0x06F624, 3);                   // read as LM's hook does: no hook code here
        Assert.Equal([0x130, 0x25, 0x4C], new[] { 0x200, 0x203, 0x2FF }.Select(t => rom.ReadValue(acts + t * 2, 2)));
        Assert.Equal(["above only"], r.Blocks.Single(b => b.Tile == 0x200).Prints);

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
        string dir = Folder("200 above.asm\n2FF wall.asm\n");
        Assert.True(GpsInsert.Insert(rom, dir).Ok);
        int once = Claimed(rom);

        Assert.True(GpsInsert.Insert(rom, dir).Ok);
        Assert.Equal(once, Claimed(rom));                         // nothing leaked

        File.WriteAllText(Path.Combine(dir, "list.txt"), "300 wall.asm\n");
        Assert.True(GpsInsert.Insert(rom, dir).Ok);
        Assert.Equal([0x300], GpsBlocks.Read(rom)!.Blocks.Keys);
        Assert.Equal(0xFFFFFF, rom.ReadValue(0x0CB66F, 3));        // sparkle's slot emptied with its last user
    }

    [AsarFact]
    public void a_file_without_a_block_header_fails_and_leaves_the_rom_alone()
    {
        var rom = LmSaved();
        string dir = Folder("200 bad.asm\n");
        File.WriteAllText(Path.Combine(dir, "blocks", "bad.asm"), "db $00\nRTL\n");
        var before = rom.Data.ToArray();

        var r = GpsInsert.Insert(rom, dir);

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
        Assert.Null(GpsInsert.Import(Folder("200 above.asm\n2FF wall.asm\n"), p.GpsFolder));

        var (status, built) = RomBuilder.Build(p);
        Assert.NotNull(built);
        Assert.Equal([0x200, 0x2FF], GpsBlocks.Read(Rom.Load(built!))!.Blocks.Keys.Order());

        File.AppendAllText(Path.Combine(p.GpsFolder, "blocks", "wall.asm"), "LDA.q #0\n");
        (status, built) = RomBuilder.Build(p);
        Assert.Null(built);
        Assert.StartsWith("build failed: custom blocks: ", status);
        Assert.Contains("wall.asm:", status);
    }

    /// <summary>Prep carries LM's acts-like core but not its custom-block dispatch, so GPS's own
    /// check would pass and the blocks would never run: the build says so instead.</summary>
    [AsarFact]
    public void a_prepped_base_refuses_custom_blocks()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var p = Project.Create(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla);
        Assert.True(p.Data.BaseRom.PrepVersion > 0);
        Assert.Null(GpsInsert.Import(Folder("200 above.asm\n"), p.GpsFolder));

        var (status, built) = RomBuilder.Build(p);

        Assert.Null(built);
        Assert.Contains("Pipe Dream-prepped base", status);
    }

    [Fact]
    public void import_copies_the_inputs_only_and_a_bad_folder_keeps_the_last_import()
    {
        string src = Folder("200 above.asm\n");
        Directory.CreateDirectory(Path.Combine(src, "blocks", "sub"));
        File.WriteAllText(Path.Combine(src, "blocks", "sub", "x.asm"), "RTL\n");
        File.WriteAllText(Path.Combine(src, "gps.exe"), "");
        string dest = Path.Combine(Directory.CreateTempSubdirectory("pipedream-proj-").FullName, "gps");

        Assert.Null(GpsInsert.Import(src, dest));
        Assert.True(File.Exists(Path.Combine(dest, "list.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "blocks", "sub", "x.asm")));
        Assert.True(File.Exists(Path.Combine(dest, "routines", "sparkle.asm")));
        Assert.False(File.Exists(Path.Combine(dest, "gps.exe")));

        string bad = Folder("200 above.asm\n201 missing.asm\n");
        Assert.Equal("list.txt line 2: blocks/missing.asm not found", GpsInsert.Import(bad, dest));
        Assert.Equal("200 above.asm\n", File.ReadAllText(Path.Combine(dest, "list.txt")));
        Assert.Equal("list.txt line 1: block id out of bounds", GpsInsert.Import(Folder("1FF above.asm\n"), dest));
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
