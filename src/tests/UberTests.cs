using Xunit;

namespace PipeDream.Tests;

/// <summary>UberASM (reference/UBERASM_PORT.md, phase 1): list.txt as UberASM Tool 2.1 and 1.x read it, a folder import, and the
/// session's operations.</summary>
public class UberTests
{
    private static UberState Parse(string text, List<string>? problems = null)
    {
        var u = new UberState();
        UberImport.ParseList(text, u, problems ?? []);
        return u;
    }

    private static string Show(UberEntry e) => string.Join(", ", e.Calls.Select(c =>
        (c.Skip ? "-" : "") + c.File + (c.Bytes.Length > 0 ? ":" + Convert.ToHexString(c.Bytes) : "")));

    [Fact]
    public void the_readmes_example_list_reads_as_it_says()
    {
        // 2.1's readme, "The List File", word for word but for the other commands it leaves out.
        var problems = new List<string>();
        var u = Parse("""
            ; Example list file...this line is a comment.
            # so is this
            level:
               105 incio.asm
               106 incio.asm,                 ; comment here too
                   waves.asm, gradient.asm
            overworld:
            * music.asm, yoshi.asm : 00
            0 -music.asm
            1 -yoshi.asm, yoshi.asm:FF
            level:
            107 special.asm:@100, ../overworld/music.asm
            """, problems);
        Assert.Empty(problems);
        Assert.Equal(["105: level/incio.asm", "106: level/incio.asm, level/waves.asm, level/gradient.asm",
                      "107: level/special.asm:64, overworld/music.asm"],
                     u.Levels.Select(e => $"{e.Number:X}: {Show(e)}"));
        Assert.Equal(["*: overworld/music.asm, overworld/yoshi.asm:00", "0: -overworld/music.asm", "1: -overworld/yoshi.asm, overworld/yoshi.asm:FF"],
                     u.Overworlds.Select(e => $"{(e.Number == UberEntry.All ? "*" : e.Number.ToString("X"))}: {Show(e)}"));
    }

    [Fact]
    public void extra_bytes_take_every_form_the_readme_gives()
    {
        // "-10, F0, $f0, -@16, @240, and %11110000 all refer to the same value"; (@300) is 2C 01; [12AB00] little-endian.
        var u = Parse("level:\n105 a.asm : -10 F0 $f0 -@16 @240 %11110000 0xF0 (@300) [12AB00]");
        Assert.Equal("F0F0F0F0F0F0F02C0100AB12", Convert.ToHexString(u.Levels[0].Calls[0].Bytes));
    }

    [Fact]
    public void a_1x_list_reads_too()
    {
        // 1.5's own list.txt: # comments, sprite: for the RAM, a file under a subfolder with spaces in its path.
        var problems = new List<string>();
        var u = Parse("""
            verbose: on
            level:
                    105 example.asm
                    115 world 3/castle.asm
            overworld:
            gamemode:
            global:         other/global_code.asm
            statusbar:      other/status_code.asm
            macrolib:       other/macro_library.asm
            sprite:         $7FAC80
            sprite-sa1:     $41AC80
            rom:            YourROM.smc
            """, problems);
        Assert.Empty(problems);
        Assert.Equal(0x7FAC80, u.FreeRam);
        Assert.Equal(["level/example.asm", "level/world 3/castle.asm"], u.Levels.Select(e => e.Calls[0].File));
    }

    [Fact]
    public void mistakes_in_the_list_are_named_by_line()
    {
        var problems = new List<string>();
        Parse("105 early.asm\nlevel:\n200 a.asm\n105 a.asm : zz\nwhat: ever\noverworld:\n7 b.asm", problems);
        Assert.Equal([
            "list.txt line 1: a resource before level:, gamemode: or overworld:",
            "list.txt line 3: \"200\" is not a level number",
            "list.txt line 4: \"zz\" is not an extra byte",
            "list.txt line 5: unknown command \"what:\"",
            "list.txt line 7: \"7\" is not a overworld number",
        ], problems);
    }

    [Fact]
    public void a_folder_imports_through_the_session_and_its_files_run_where_the_list_says()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string dir = Directory.CreateTempSubdirectory("pd-uber-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "level"));
        Directory.CreateDirectory(Path.Combine(dir, "library"));
        Directory.CreateDirectory(Path.Combine(dir, "asm", "base"));
        File.WriteAllText(Path.Combine(dir, "list.txt"), "level:\n105 waves.asm\n* waves.asm\nfreeram: $7FB000\n");
        File.WriteAllText(Path.Combine(dir, "level", "waves.asm"), "main:\n    RTL\n");
        File.WriteAllBytes(Path.Combine(dir, "library", "table.bin"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(dir, "asm", "base", "main.asm"), "; the tool's own, left behind");

        var s = new Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(Directory.CreateTempSubdirectory("pd-proj-").FullName, "p"), ReferenceRoms.Vanilla), s.Status);
        Assert.Equal("imported 2 file(s) and 2 list entries", s.ImportUberFolder(dir));
        Assert.Equal([("level/waves.asm", "level *, level 105"), ("library/table.bin", "")], s.UberFiles.Select(f => (f.Path, f.RunsIn)));
        Assert.Equal("main:\n    RTL\n", s.UberText("level/waves.asm"));

        Assert.Equal("level 106 runs level/waves.asm", s.SetUberCalls("level", 0x106, [new UberCall { File = "level/waves.asm" }]));
        Assert.Equal("level/gone.asm is not in the project", s.SetUberCalls("level", 0x107, [new UberCall { File = "level/gone.asm" }]));
        Assert.Equal("deleted level/waves.asm", s.DeleteUberFile("level/waves.asm"));
        Assert.Empty(s.UberCalls("level", 0x105));                                       // gone from every entry that ran it
        Assert.Equal("that folder has no list.txt — pick UberASM Tool's own folder", s.ImportUberFolder(Path.Combine(dir, "level")));
    }

    [Fact]
    public void the_list_writes_back_and_reads_the_same()
    {
        var u = Parse("level:\n106 a.asm, waves.asm : 01 (@300)\n* -b.asm\noverworld:\n1 ../level/a.asm\ngamemode:\n14 gm.asm\nfreeram: $7FB000");
        var again = Parse(UberImport.FormatList(u));
        Assert.Equal(u.Levels.Select(Show), again.Levels.Select(Show));
        Assert.Equal(u.Overworlds.Select(Show), again.Overworlds.Select(Show));
        Assert.Equal(u.GameModes.Select(Show), again.GameModes.Select(Show));
        Assert.Equal(0x7FB000, again.FreeRam);
    }
}
