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
        Assert.True(GfxBrowser.Matches(rom, 0x100, "GRASS"));
        Assert.True(GfxBrowser.Matches(rom, 0x100, "tiles"));
        Assert.False(GfxBrowser.Matches(rom, 0x101, "grass"));
        // by id, so someone who knows the number can still type it
        Assert.True(GfxBrowser.Matches(rom, 0x101, "101"));
        Assert.True(GfxBrowser.Matches(rom, 0x102, "10"));
        // an unnamed file is still reachable by its id
        Assert.True(GfxBrowser.Matches(rom, 0x102, "102"));
        Assert.False(GfxBrowser.Matches(rom, 0x102, "grass"));
    }

    [RealRomFact]
    public void ids_match_by_prefix_so_a_single_letter_does_not_drag_in_coincidences()
    {
        var rom = WithImports((0x100, "grass"));
        // "10" reaches both the $10x range and the file whose id simply IS $10 — both are
        // what someone typing "10" could mean, so both are kept.
        Assert.True(GfxBrowser.Matches(rom, 0x100, "10"));
        Assert.True(GfxBrowser.Matches(rom, 0x010, "10"));
        // What it must NOT do is match an id that merely CONTAINS the text.
        Assert.False(GfxBrowser.Matches(rom, 0x210, "10"));
        Assert.False(GfxBrowser.Matches(rom, 0x310, "10"));
        // A bare hex letter finds the low file with that id, not every id containing it.
        Assert.True(GfxBrowser.Matches(rom, 0x00A, "a"));
        Assert.False(GfxBrowser.Matches(rom, 0x01A, "a"));
        Assert.False(GfxBrowser.Matches(rom, 0x02A, "a"));
    }

    [RealRomFact]
    public void candidates_list_imports_in_id_order_and_only_adds_stock_when_asked()
    {
        var rom = WithImports((0x102, "c"), (0x100, "a"), (0x101, "b"));

        var custom = GfxBrowser.Candidates(rom, includeStock: false, "");
        Assert.Equal([0x100, 0x101, 0x102], custom);

        var all = GfxBrowser.Candidates(rom, includeStock: true, "");
        Assert.Contains(0x00, all);
        Assert.Contains(0x33, all);
        Assert.DoesNotContain(0x34, all);          // stock range is 0x00-0x33
        Assert.True(all.Count > custom.Count);
        Assert.Equal(all.OrderBy(i => i), all);    // still id-ordered

        // Filtering applies to the combined list. "a" matches the file NAMED "a" and the
        // stock file whose id is $00A — both intentional, nothing else.
        Assert.Equal([0x00A, 0x100], GfxBrowser.Candidates(rom, includeStock: true, "a"));
    }

    [RealRomFact]
    public void an_import_shadowing_a_stock_id_is_listed_once()
    {
        // Imports can only land at 0x100+, but a project could carry a fork of a stock file;
        // the stock pass must not then list the same id twice.
        var rom = WithImports((0x02, "forked-gfx02"));
        var all = GfxBrowser.Candidates(rom, includeStock: true, "");
        Assert.Single(all.Where(i => i == 0x02));
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
