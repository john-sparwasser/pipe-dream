using Xunit;

namespace PipeDream.Tests;

/// <summary>Naming and finding imported ExGFX. The names are pure metadata — nothing in the
/// ROM read/write path reads them — so what matters is that they survive a project round trip
/// and that the browser's filter finds files by either name or id.</summary>
public class GfxNamingTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pdgn-" + Guid.NewGuid().ToString("N")[..8]);

    public GfxNamingTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    private static Rom WithImports(params (int id, string name)[] files)
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        foreach (var (id, name) in files)
        {
            rom.ImportedGfx[id] = new byte[0xC00];
            if (name.Length > 0) rom.ImportedGfxNames[id] = name;
        }
        return rom;
    }

    [RealRomFact]
    public void an_unnamed_file_reports_an_empty_name_rather_than_throwing()
    {
        var rom = WithImports((0x100, ""));
        Assert.Equal("", rom.GfxName(0x100));
        Assert.Equal("", rom.GfxName(0x777));      // never imported at all
    }

    [RealRomFact]
    public void the_filter_matches_on_name_and_on_hex_id()
    {
        var rom = WithImports((0x100, "grass-tiles"), (0x101, "cave"), (0x102, ""));

        // by name, anywhere in it, case-insensitively
        Assert.True(Gfx.Matches(rom, 0x100, "GRASS"));
        Assert.True(Gfx.Matches(rom, 0x100, "tiles"));
        Assert.False(Gfx.Matches(rom, 0x101, "grass"));
        // by id, so someone who knows the number can still type it
        Assert.True(Gfx.Matches(rom, 0x101, "101"));
        Assert.True(Gfx.Matches(rom, 0x102, "10"));
        // an unnamed file is still reachable by its id
        Assert.True(Gfx.Matches(rom, 0x102, "102"));
        Assert.False(Gfx.Matches(rom, 0x102, "grass"));
    }

    [RealRomFact]
    public void ids_match_by_prefix_so_a_single_letter_does_not_drag_in_coincidences()
    {
        var rom = WithImports((0x100, "grass"));
        // "10" reaches both the $10x range and the file whose id simply IS $10 — both are
        // what someone typing "10" could mean, so both are kept.
        Assert.True(Gfx.Matches(rom, 0x100, "10"));
        Assert.True(Gfx.Matches(rom, 0x010, "10"));
        // What it must NOT do is match an id that merely CONTAINS the text.
        Assert.False(Gfx.Matches(rom, 0x210, "10"));
        Assert.False(Gfx.Matches(rom, 0x310, "10"));
        // A bare hex letter finds the low file with that id, not every id containing it.
        Assert.True(Gfx.Matches(rom, 0x00A, "a"));
        Assert.False(Gfx.Matches(rom, 0x01A, "a"));
        Assert.False(Gfx.Matches(rom, 0x02A, "a"));
    }

    [RealRomFact]
    public void candidates_are_one_side_or_the_other_in_id_order()
    {
        var rom = WithImports((0x102, "c"), (0x100, "a"), (0x101, "b"));

        var custom = Gfx.Candidates(rom, custom: true, "");
        Assert.Equal([0x100, 0x101, 0x102], custom);

        var bases = Gfx.Candidates(rom, custom: false, "");
        Assert.Contains(0x00, bases);
        Assert.Contains(0x33, bases);
        Assert.DoesNotContain(0x34, bases);        // base range is 0x00-0x33
        Assert.DoesNotContain(0x100, bases);       // ...and never the custom side
        Assert.Equal(bases.OrderBy(i => i), bases);

        // Filtering applies within a side: "a" finds the file NAMED "a" among the custom ones and
        // the base file whose id is $00A, and neither list leaks into the other.
        Assert.Equal([0x100], Gfx.Candidates(rom, custom: true, "a"));
        Assert.Equal([0x00A], Gfx.Candidates(rom, custom: false, "a"));
    }

    [RealRomFact]
    public void a_fork_of_a_base_file_lists_as_base_not_custom()
    {
        // Imports can only land at 0x100+, but a project carries forks of base files under their
        // own ids. A fork is still the ROM's file: listing it as custom would promise an ExGFX id
        // it does not have, and listing it twice would offer the same file two ways.
        var rom = WithImports((0x02, "forked-gfx02"));
        Assert.Single(Gfx.Candidates(rom, custom: false, ""), i => i == 0x02);
        Assert.DoesNotContain(0x02, Gfx.Candidates(rom, custom: true, ""));
    }

    [RealRomFact]
    public void names_survive_a_project_save_and_reopen()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        p.Data.Gfx["100"] = Convert.ToBase64String(new byte[0xC00]);
        p.Data.GfxNames["100"] = "grass-tiles";
        p.Save();

        var re = Project.Open(p.FilePath);
        Assert.Equal("grass-tiles", re.Data.GfxNames["100"]);
    }

    [RealRomFact]
    public void a_pdp_without_names_still_loads()
    {
        // Names went in as a separate map precisely so older projects keep working.
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        p.Data.Gfx["100"] = Convert.ToBase64String(new byte[0xC00]);
        p.Save();
        string json = File.ReadAllText(p.FilePath).Replace("\"GfxNames\": {},", "");
        File.WriteAllText(p.FilePath, json);

        var re = Project.Open(p.FilePath);
        Assert.Empty(re.Data.GfxNames);
        Assert.Single(re.Data.Gfx);
    }
}
