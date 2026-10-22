using System.Text;
using Xunit;

namespace PipeDream.Tests;

/// <summary>UberASM's build step (reference/UBERASM_PORT.md, phase 2): our driver inserted onto SMW, its dispatch run in the
/// 65816 interpreter. Each resource logs an id at $7FB120 + $7FB11F, so the order calls ran in can be read back.</summary>
public class UberInsertTests
{
    /// <summary>Append <paramref name="id"/> to the log ($7FB11F counts).</summary>
    private static string Log(int id) => $"    LDA $7FB11F\n    TAX\n    LDA #${id:X2}\n    STA $7FB120,x\n    INX\n    TXA\n    STA $7FB11F\n";

    private static ProjectFile Project(Action<UberState>? change = null)
    {
        var p = new ProjectFile();
        var u = p.Uber;
        void File(string path, string text) => u.Files[path] = Encoding.UTF8.GetBytes(text);
        File("level/a.asm", ";>bytes 2\ninit:\n" + Log(0xA1) + "    RTL\nmain:\n" + Log(0xA2) +
            "    LDY #$00\n    LDA ($00),y\n    STA $7FB111\n    INY\n    LDA ($00),y\n    STA $7FB112\n" +
            "    PHB\n    PLA\n    STA $7FB113\n    PHK\n    PLA\n    STA $7FB118\n" +
            "    JSL math_double\n    %UberRoutine(Mark)\n    RTL\n");
        File("level/star.asm", "main:\n" + Log(0x5A) + "    RTL\n");
        File("gamemode/gm.asm", "init:\n" + Log(0x61) + "    RTL\nmain:\n" + Log(0x62) + "    RTL\nend:\n" + Log(0x63) + "    RTL\n");
        File("library/math.asm", "double:\n    LDA #$D0\n    STA $7FB114\n    RTL\n");
        File("routines/Mark.asm", "    LDA #$E0\n    STA $7FB115\n    RTL\n");
        File("other/global_code.asm", "init:\n    LDA #$C1\n    STA $7FB116\n    RTS\n");
        File("other/status_code.asm", "main:\n    LDA #$C2\n    STA $7FB117\n    RTS\n");
        UberImport.ParseList("level:\n* star.asm\n105 a.asm : 12 34\n106 -star.asm\ngamemode:\n14 gm.asm\n", u, []);
        change?.Invoke(u);
        return p;
    }

    private static (Rom Rom, UberInsert.Result R) Insert(ProjectFile p)
    {
        var rom = Rom.Load(ReferenceRoms.Vanilla);
        return (rom, UberInsert.Insert(rom, p));
    }

    private static byte[] Run(Rom rom, UberInsert.Result r, string entry, int level = 0x105, int mode = 0x14)
    {
        var cpu = new Cpu65816(rom);
        cpu.Ram7E[0x10B] = (byte)level; cpu.Ram7E[0x10C] = (byte)(level >> 8);
        cpu.Ram7E[0x100] = (byte)mode;
        cpu.CallLong(r.Labels![entry]);
        return cpu.Ram7F;
    }

    private static string Logged(byte[] ram7F) => Convert.ToHexString(ram7F.AsSpan(0xB120, ram7F[0xB11F]));

    [AsarFact]
    public void a_levels_resources_run_star_first_with_their_extra_bytes_bank_library_and_routines()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var (rom, r) = Insert(Project());
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        var ram = Run(rom, r, "LevelMainDispatch");
        Assert.Equal("5AA2", Logged(ram));                                       // * first, then the level's own
        Assert.Equal((0x12, 0x34), (ram[0xB111], ram[0xB112]));                  // its extra bytes, through $00
        Assert.Equal(ram[0xB118], ram[0xB113]);                                  // the DBR is its own bank
        Assert.Equal((0xD0, 0xE0), (ram[0xB114], ram[0xB115]));                  // the library label and the shared routine ran
        Assert.Equal("", Logged(Run(rom, r, "LevelMainDispatch", level: 0x106))); // -star.asm: skipped
        Assert.Equal("5A", Logged(Run(rom, r, "LevelMainDispatch", level: 0x107))); // no entry of its own: *
        Assert.Equal("A1", Logged(Run(rom, r, "LevelInitDispatch")));            // star.asm has no init:
        Assert.Equal("", Logged(Run(rom, r, "LevelEndDispatch")));
    }

    [AsarFact]
    public void game_modes_global_init_and_the_hooks_are_in_place()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var (rom, r) = Insert(Project());
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Equal("61", Logged(Run(rom, r, "GameModeInitDispatch")));
        Assert.Equal("62", Logged(Run(rom, r, "GameModeMainDispatch")));
        Assert.Equal("63", Logged(Run(rom, r, "GameModeEndDispatch")));
        Assert.Equal("", Logged(Run(rom, r, "GameModeMainDispatch", mode: 0x13)));

        var ram = Run(rom, r, "GlobalInit");
        Assert.Equal(0xC1, ram[0xB116]);                                          // the global file's init:
        Assert.Equal((0xFF, 0xFF), (ram[0xAC80], ram[0xAC81]));                  // freeram: no game mode seen yet

        int Jml(int at) => rom.ReadByte(at) == 0x5C ? rom.ReadByte(at + 1) | rom.ReadByte(at + 2) << 8 | rom.ReadByte(at + 3) << 16 : -1;
        Assert.Equal(r.Labels!["Frame"], Jml(0x008070));
        Assert.Equal(r.Labels["LevelMain"], Jml(0x00A242));
        Assert.Equal(r.Labels["Nmi"], Jml(0x008174));
        Assert.Equal(r.Labels["StatusBar"], Jml(0x008E1A));
    }

    [AsarRomFact]
    public void a_project_builds_with_uberasm_beside_custom_sprites_and_the_other_drivers()
    {
        // The real build on a prepped base: prep's hooks, the sprite driver and ours together, with no overlap warnings.
        var (status, built) = SpriteInsertTests.Build(Directory.CreateTempSubdirectory("pd-uber-build-").FullName,
            more: (data, _, _) => data.Uber = Project().Uber);
        Assert.True(built is not null, status);
        Assert.DoesNotContain("UberASM", status);
        Assert.Equal(0x5C, built.ReadByte(0x00A242));                             // ours: JML LevelMain
        Assert.Equal(0x5C, built.ReadByte(0x02A846));                             // the sprite driver's: JML StepRecord
    }

    [AsarFact]
    public void mistakes_are_named()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        Assert.Equal("level/a.asm takes 2 extra bytes (;>bytes), the list gives 1",
            Assert.Single(Insert(Project(u => u.Levels.Find(e => e.Number == 0x105)!.Calls[0].Bytes = [1])).R.Errors));
        Assert.Equal("%UberRoutine(Mark): routines/Mark.asm is not in the project",
            Assert.Single(Insert(Project(u => u.Files.Remove("routines/Mark.asm"))).R.Errors));
        Assert.StartsWith("level/star.asm: ", Assert.Single(Insert(Project(u => u.Files["level/star.asm"] = "main:\n    LDA #\n"u8.ToArray())).R.Errors));

        var patched = Rom.Load(ReferenceRoms.Vanilla);
        patched.Data[patched.FileOffset(0x00A242)] = 0x5C;                        // someone's JML where level main hooks
        Assert.StartsWith("$00A242 is not SMW's code", Assert.Single(UberInsert.Insert(patched, Project()).Errors));
    }
}
