using Avalonia.Headless.XUnit;
using PipeDream.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The whole cycle without a window: new project → edit → save → reopen → build.
///
/// This is the test the ImGui editor could never have. Its save path was welded to the view,
/// so "does an edit actually reach the built ROM" was only answerable by a human clicking
/// through the GUI and then inspecting a ROM — which is how an edit that renders perfectly
/// and saves nothing survives review.
/// </summary>
public class SessionTests(ITestOutputHelper log) : IDisposable
{
    private readonly ITestOutputHelper log = log;
    // Project.Create makes the folder (and its parents), so nothing needs creating here.
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pduisess-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static bool HaveRom => File.Exists(Vanilla);

    [Fact]
    public void a_painted_stroke_reaches_the_project_and_then_the_built_rom()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }

        var session = new EditorSession();
        Assert.True(session.NewProject(Path.Combine(dir, "proj"), Vanilla), session.Status);
        Assert.NotNull(session.Project);
        Assert.NotNull(session.Edit);

        // A vanilla base is prepped on create, so Direct Map16 placement works.
        Assert.Null(session.Edit!.TilePlacementBlocked);
        int before = session.Edit.Objects.Count;

        session.ShowLevel(0x105);
        for (int x = 4; x < 14; x++) session.Edit!.Paint(x, 6, 0x100);
        session.Edit!.EndStroke();
        Assert.True(session.Edit.Objects.Count > before);

        session.Save();
        log.WriteLine(session.Status);

        // The .pdp on disk carries the objects — not just the in-memory project.
        var reopened = Project.Open(session.Project!.FilePath);
        var lvl = reopened.Data.Level(0x105);
        Assert.NotEmpty(lvl.Objects);
        Assert.Contains(lvl.Objects, o => o.ToLevelObject().IsDm16);

        // ...and the build turns them into a real ROM.
        var (status, outPath) = RomBuilder.Build(reopened);
        Assert.NotNull(outPath);
        Assert.True(File.Exists(outPath), status);
        log.WriteLine($"built {outPath}");

        // The built ROM's level really contains the placed tile.
        var built = Rom.Load(outPath!);
        var parsed = LevelParser.Parse(built, 0x105);
        Assert.Contains(parsed.Objects, o => o.IsDm16 && o.Dm16Tile == 0x100);
    }

    [Fact]
    public void reopening_a_saved_project_restores_the_edits()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }

        var a = new EditorSession();
        Assert.True(a.NewProject(Path.Combine(dir, "proj"), Vanilla), a.Status);
        string pdp = a.Project!.FilePath;
        a.ShowLevel(0x105);
        for (int x = 4; x < 10; x++) a.Edit!.Paint(x, 6, 0x100);
        a.Edit!.EndStroke();
        int objects = a.Edit.Objects.Count;
        a.Save();

        var b = new EditorSession();
        Assert.True(b.OpenProject(pdp), b.Status);
        b.ShowLevel(0x105);

        Assert.Equal(objects, b.Edit!.Objects.Count);
        Assert.Equal(0x100, b.Scene!.Grid.Get(6, 6));
    }

    /// <summary>Switching level must not lose the level you are leaving — the session stashes
    /// on the way out, which is the difference between "autosave" and "autolose".</summary>
    [Fact]
    public void leaving_a_level_commits_its_edits()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }

        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
        s.ShowLevel(0x105);
        for (int x = 4; x < 10; x++) s.Edit!.Paint(x, 6, 0x100);
        s.Edit!.EndStroke();

        s.ShowLevel(0x106);                       // walk away without saving explicitly
        Assert.NotEmpty(s.Project!.Data.Level(0x105).Objects);

        s.ShowLevel(0x105);                       // and coming back finds them
        Assert.Equal(0x100, s.Scene!.Grid.Get(6, 6));
    }

    /// <summary>
    /// Sprite edits have to survive the round trip too, and they used not to. The sprite list
    /// hung off whichever LevelEdit was current, and an object re-render replaces that — so the
    /// save read the ROM's PARSE of the level instead of the edited list, and reopening a
    /// project showed the base ROM's sprites over your own level. The session owns the list now.
    /// </summary>
    [Fact]
    public void sprite_edits_survive_a_save_and_reopen()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }

        var a = new EditorSession();
        Assert.True(a.NewProject(Path.Combine(dir, "proj"), Vanilla), a.Status);
        string pdp = a.Project!.FilePath;
        a.ShowLevel(0x105);
        Assert.NotNull(a.Sprites);

        int before = a.Sprites!.Sprites.Sprites.Count;
        Assert.True(a.Sprites.Place(number: 0x0B, cx: 20, cy: 10));
        a.RefreshSprites();                      // recompose, as a canvas edit does
        // The recompose must not have quietly reverted to the ROM's list.
        Assert.Equal(before + 1, a.Sprites!.Sprites.Sprites.Count);
        a.Save();

        var b = new EditorSession();
        Assert.True(b.OpenProject(pdp), b.Status);
        b.ShowLevel(0x105);
        Assert.Equal(before + 1, b.Sprites!.Sprites.Sprites.Count);
        Assert.Contains(b.Sprites.Sprites.Sprites, s => s.Number == 0x0B && s.Cell(false) == (20, 10));
    }

    /// <summary>
    /// A .pdp is shareable on its own and the base ROM copy beside it deliberately is not, so
    /// opening a project whose base is missing is the NORMAL path for someone else's work, not an
    /// error. It holds the project and asks for a ROM, and the located one is verified against the
    /// hash the project pinned — a base that only looked right would corrupt every offset the
    /// project recorded against it.
    /// </summary>
    [Fact]
    public void a_project_whose_base_rom_is_missing_can_be_recovered()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }

        var a = new EditorSession();
        Assert.True(a.NewProject(Path.Combine(dir, "proj"), Vanilla), a.Status);
        string pdp = a.Project!.FilePath;
        string basePath = a.Project.BaseRomPath;
        a.ShowLevel(0x105);
        a.Edit!.Paint(6, 6, 0x100);
        a.Edit.EndStroke();
        a.Save();

        File.Delete(basePath);                     // as if only the .pdp had been shared

        var b = new EditorSession();
        Assert.False(b.OpenProject(pdp));          // held, not opened
        Assert.NotNull(b.PendingBaseProblem);
        log.WriteLine($"{b.PendingProjectName}: {b.PendingBaseProblem}");
        Assert.NotEmpty(b.PendingBaseDescription);

        // A wrong ROM is refused rather than adopted.
        string decoy = Path.Combine(dir, "decoy.smc");
        File.WriteAllBytes(decoy, new byte[0x8000]);
        Assert.NotNull(b.AdoptPendingBase(decoy));
        Assert.NotNull(b.PendingBaseProblem);       // still waiting

        // The real one is prepped to the project's pinned prep version and adopted.
        Assert.Null(b.AdoptPendingBase(Vanilla));
        Assert.Null(b.PendingBaseProblem);
        b.ShowLevel(0x105);
        Assert.Equal(0x100, b.Scene!.Grid.Get(6, 6));   // and the edits are there
    }

    [Fact]
    public void a_rom_opened_without_a_project_says_so_rather_than_pretending_to_save()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(Vanilla));
        Assert.Null(s.Project);
        Assert.Contains("no project", s.Save());
        Assert.Contains("no project", s.Build());
    }
}
