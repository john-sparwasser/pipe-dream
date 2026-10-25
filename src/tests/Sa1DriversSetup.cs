using PipeDream.Services;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Builds the ROM pairs for the SA-1 driver checks (reference/SA1_PORT.md, phase 3): one project per
/// driver, built LoROM and again with ASM ▸ SA-1 on, each leaving a mark in RAM a Mesen probe reads
/// back on level $105 (walk left from the start of a new game, enter, hold right).
///
///  • blocks-*.smc: tile $200 runs a block that sets the lives counter ($0DBE) to 99 on any Mario
///    contact; it acts like cement and stands as a full-height column at X $30, so walking right
///    runs into it before the level's first enemy (Mario starts at $10 and dies near $68).
///  • music-*.smc: a short local song in each of the level header's eight song numbers, so level
///    $105's is uploaded on entry: the driver's $1DFD then holds it and $1DFE is back to 0 once
///    the engine has echoed it.
///  • uber-*.smc: a resource per code point, each leaving a mark at $0F5E-$0F64 (cluster sprite
///    tables, which level $105 does not use): level $105's init ($11 at $0F5E), main ($0F5F, and
///    $0F64 through %invoke_sa1) and nmi ($0F60), game mode $14's main ($0F61), Yoshi's Island's
///    main ($0F62) and the status bar's ($0F63).
///  • sprites-*.smc: a custom sprite in global slot 1A, placed near the start of level $105 with
///    extra bytes AB CD: INIT stores $11 at $0F5E and its first extra byte at $0F60, MAIN counts
///    frames at $0F5F and leaves SubHorzPos's side ($0F61), a Random up to $10 ($0F62) and Aiming's
///    speeds toward ($30, $40) at size $20 ($0F63, $0F64; the multiplier, on both CPUs).
///  • kinds-*.smc: a custom shooter (C0, counting at $0F65) and generator (D0, $0F66), and a sprite
///    with six extra bytes 11-66, whose INIT reads the fifth ($55) through the pointer into $0F67.
///
/// Skipped unless PIPEDREAM_SA1_DRIVERS names the output folder.
/// </summary>
public class Sa1DriversSetup(ITestOutputHelper log)
{
    private static string? Dir => Environment.GetEnvironmentVariable("PIPEDREAM_SA1_DRIVERS");

    /// <summary>The project at <paramref name="name"/>, with level $105 put in as the base has it.</summary>
    private static EditorSession NewProject(string dir, string name)
    {
        string folder = Path.Combine(dir, name);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        var s = new EditorSession();
        Assert.True(s.NewProject(folder, ReferenceRoms.Vanilla), s.Status);
        var baseRom = Rom.Load(s.Project!.BaseRomPath);
        var sd = SpriteData.Parse(baseRom, 0x105);
        var st = s.Project.Data.Level(0x105);
        st.Objects = LevelParser.Parse(baseRom, 0x105).Objects.Select(ProjectFile.ObjectDto.From).ToList();
        st.SpriteMemory = sd.SpriteMemory; st.Buoyancy = sd.Buoyancy;
        st.Sprites = sd.Sprites.Select(ProjectFile.SpriteDto.From).ToList();
        return s;
    }

    /// <summary>Build <paramref name="s"/> LoROM and SA-1 into &lt;dir&gt;/&lt;name&gt;-lorom.smc and -sa1.smc.</summary>
    private async Task BuildPair(EditorSession s, string dir, string name)
    {
        Assert.True(TestConfigDir.SeedSa1(), "no SA-1 Pack beside the oracle ROMs");
        var (status, outPath) = RomBuilder.Build(s.Project!);
        log.WriteLine($"{name} LoROM: {status}");
        Assert.NotNull(outPath);
        File.Copy(outPath!, Path.Combine(dir, name + "-lorom.smc"), overwrite: true);
        log.WriteLine(await s.SetSa1(true));
        (status, outPath) = RomBuilder.Build(s.Project!);
        log.WriteLine($"{name} SA-1: {status}");
        Assert.NotNull(outPath);
        File.Copy(outPath!, Path.Combine(dir, name + "-sa1.smc"), overwrite: true);
    }

    [Fact]
    public async Task build_the_block_pair()
    {
        if (Dir is not { } dir) { log.WriteLine("SKIP: set PIPEDREAM_SA1_DRIVERS to an output folder"); return; }
        Directory.CreateDirectory(dir);
        var s = NewProject(dir, "blocks");
        s.NewBlock("lives", out var made);
        Assert.NotNull(made);
        s.SetBlockSource(made!, """
            db $42
            JMP Mario : JMP Mario : JMP Mario
            JMP Return : JMP Return : JMP Return : JMP Return
            JMP Mario : JMP Mario : JMP Mario

            Mario:
                LDA #$63
                STA $0DBE|!addr
            Return:
                RTL
            """);
        log.WriteLine(s.AssignBlock(0x200, made));
        var m16 = s.Project!.Data.Map16;
        m16.TileCount = Math.Max(m16.TileCount, 0x300);
        m16.Ext["200"] = "3009300930093009";                       // 8x8 tile $130 in all four quadrants
        m16.ActsAs["200"] = 0x130;                                  // cement
        var objects = s.Project.Data.Level(0x105).Objects;
        for (int row = 0; row < 0x1B; row++)
            objects.Add(ProjectFile.ObjectDto.From(LevelObject.MakeDm16(0x200, screen: 0, xNib: 3, y: row)));
        await BuildPair(s, dir, "blocks");
    }

    [Fact]
    public async Task build_the_music_pair()
    {
        if (Dir is not { } dir) { log.WriteLine("SKIP: set PIPEDREAM_SA1_DRIVERS to an output folder"); return; }
        Directory.CreateDirectory(dir);
        var s = NewProject(dir, "music");
        var m = s.Project!.Data.Music;
        foreach (int n in (int[])[0x0A, 0x0B, 0x0C, 0x0E, 0x0F, 0x10, 0x11, 0x12])         // the level header's eight songs
        {
            m.Songs[$"s{n:X2}.mml"] = $"#amk 2\n#0 t60 @0 o4 c4 d4 e4 f{n % 8 + 1}";
            m.SongList.Add(new MusicState.SongSlot { Number = n, Path = $"s{n:X2}.mml" });
        }
        await BuildPair(s, dir, "music");
    }

    [Fact]
    public async Task build_the_uberasm_pair()
    {
        if (Dir is not { } dir) { log.WriteLine("SKIP: set PIPEDREAM_SA1_DRIVERS to an output folder"); return; }
        Directory.CreateDirectory(dir);
        var s = NewProject(dir, "uber");
        var u = s.Project!.Data.Uber;
        void File(string path, string text) => u.Files[path] = System.Text.Encoding.UTF8.GetBytes(text);
        File("level/probe.asm", """
            init:
                LDA #$11
                STA $0F5E|!addr
                RTL
            main:
                INC $0F5F|!addr
                %invoke_sa1(on_sa1)
                RTL
            nmi:
                INC $0F60|!addr
                RTL
            on_sa1:
                PHB : PHK : PLB
                INC $0F64|!addr
                PLB
                RTL
            """);
        File("gamemode/gm14.asm", "main:\n    INC $0F61|!addr\n    RTL\n");
        File("overworld/yi.asm", "main:\n    INC $0F62|!addr\n    RTL\n");
        File("other/status_code.asm", "main:\n    INC $0F63|!addr\n    RTS\n");
        UberImport.ParseList("level:\n105 probe.asm\ngamemode:\n14 gm14.asm\noverworld:\n1 yi.asm\n", u, []);
        await BuildPair(s, dir, "uber");
    }

    [Fact]
    public async Task build_the_sprite_pair()
    {
        if (Dir is not { } dir) { log.WriteLine("SKIP: set PIPEDREAM_SA1_DRIVERS to an output folder"); return; }
        Directory.CreateDirectory(dir);
        var s = NewProject(dir, "sprites");
        var data = s.Project!.Data;
        var config = new PixiConfig { ActLike = 0x36, AsmFile = "probe.asm", ExtraBytes = 2, ExtraBytesSet = 0 };
        config.Tweak[4] = 0x01;                                            // $1686: inedible
        data.Sprites["probe"] = new ProjectFile.SpriteEntry { Config = config.ToJson(), Source = """
            print "INIT ",pc
                LDA #$11
                STA $0F5E|!addr
                LDA !extra_byte_1,x
                STA $0F60|!addr
                RTL
            print "MAIN ",pc
                PHB : PHK : PLB
                INC $0F5F|!addr
                %SubHorzPos()
                TYA
                STA $0F61|!addr
                LDA #$10
                %Random()
                STA $0F62|!addr
                REP #$20
                LDA #$0030                  ; aim at a point $30 right and $40 below
                STA $00
                LDA #$0040
                STA $02
                SEP #$20
                LDA #$20
                %Aiming()
                LDA $00
                STA $0F63|!addr
                LDA $02
                STA $0F64|!addr
                PLB
                RTL
            """ };
        data.SpriteSlots["1A"] = "probe";
        var baseRom = Rom.Load(s.Project.BaseRomPath);
        var sd = SpriteData.Parse(baseRom, 0x105);
        var edit = new SpriteEdit(sd, null, false);
        Assert.True(edit.Place(0x1A, 12, 22));
        Assert.True(edit.SetData(sd.Sprites.Count - 1, 0x1A, 2, [0xAB, 0xCD]));
        data.Level(0x105).Sprites = sd.Sprites.Select(ProjectFile.SpriteDto.From).ToList();
        await BuildPair(s, dir, "sprites");
    }

    [Fact]
    public async Task build_the_shooter_generator_and_long_extra_bytes_pair()
    {
        if (Dir is not { } dir) { log.WriteLine("SKIP: set PIPEDREAM_SA1_DRIVERS to an output folder"); return; }
        Directory.CreateDirectory(dir);
        var s = NewProject(dir, "kinds");
        var data = s.Project!.Data;
        void Add(string name, string kind, string slot, int extraBytes, string source)
        {
            var config = new PixiConfig { Type = kind == "sprite" ? 1 : 2, ActLike = 0x36, AsmFile = name + ".asm", ExtraBytes = extraBytes };
            data.Sprites[name] = new ProjectFile.SpriteEntry { Kind = kind, Config = config.ToJson(), Source = source };
            data.SpriteSlots[slot] = name;
        }
        Add("shooter", "shooter", "C0", 0, "print \"MAIN \",pc\n    INC $0F65|!addr\n    RTL\n");
        Add("generator", "generator", "D0", 0, "print \"MAIN \",pc\n    INC $0F66|!addr\n    RTL\n");
        Add("long", "sprite", "1B", 6, """
            print "INIT ",pc
                LDA !extra_byte_1,x         ; more than four: a pointer to them
                STA $00
                LDA !extra_byte_2,x
                STA $01
                LDA !extra_byte_3,x
                STA $02
                LDY #$04
                LDA [$00],y                 ; the fifth
                STA $0F67|!addr
                RTL
            print "MAIN ",pc
                RTL
            """);
        var baseRom = Rom.Load(s.Project.BaseRomPath);
        var sd = SpriteData.Parse(baseRom, 0x105);
        var edit = new SpriteEdit(sd, null, false);
        Assert.True(edit.Place(0xC0, 10, 18));
        Assert.True(edit.SetData(sd.Sprites.Count - 1, 0xC0, 2, null));
        Assert.True(edit.Place(0xD0, 11, 18));
        Assert.True(edit.SetData(sd.Sprites.Count - 1, 0xD0, 2, null));
        Assert.True(edit.Place(0x1B, 13, 22));
        Assert.True(edit.SetData(sd.Sprites.Count - 1, 0x1B, 2, [0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        data.Level(0x105).Sprites = sd.Sprites.Select(ProjectFile.SpriteDto.From).ToList();
        await BuildPair(s, dir, "kinds");
    }
}
