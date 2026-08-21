using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Course Bot: named handles on entry-level slots. Creating one copies a base level's whole
/// state into an auto-picked free slot; deleting reverts the slot to the base ROM. The subtle
/// part is slot picking — every save stashes the shown level into the project, so "has an
/// entry" and "is used" are different questions.
/// </summary>
public class CourseBotTests(ITestOutputHelper log) : IDisposable
{
    private readonly ITestOutputHelper log = log;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pdcbot-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static bool HaveRom => File.Exists(Vanilla);

    private EditorSession NewSession()
    {
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
        return s;
    }

    [Fact]
    public void creating_copies_the_whole_base_level_into_the_built_rom()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }

        var s = NewSession();
        int slot = s.CreateCourseBotLevel("Yoshi Hut", 0x105);
        Assert.True(slot > 0, s.Status);
        log.WriteLine(s.Status);
        s.Save();

        var reopened = Project.Open(s.Project!.FilePath);
        Assert.Equal("Yoshi Hut", reopened.Data.CourseBot[slot.ToString("X3")]);
        var st = reopened.Data.Levels[slot.ToString("X3")];
        Assert.NotNull(st.Header);
        Assert.NotNull(st.MainEntrance);
        // Layer 2 is recorded explicitly, one way or the other.
        Assert.True(st.Layer2Objects is not null || st.Layer2Background is not null);

        var (status, path) = RomBuilder.Build(reopened);
        Assert.True(path is not null, status);
        var built = Rom.Load(path!);

        var src = LevelParser.Parse(built, 0x105);
        var dst = LevelParser.Parse(built, slot);
        Assert.Equal(src.Header.ToBytes(), dst.Header.ToBytes());
        Assert.Equal(src.Objects, dst.Objects);
        Assert.Equal(built.ReadMainEntrance(0x105).ToBytes(), built.ReadMainEntrance(slot).ToBytes());
        Assert.Equal(built.Layer2IsBackground(0x105), built.Layer2IsBackground(slot));
        if (built.Layer2IsBackground(0x105))
            Assert.Equal(built.Layer2Pointer(0x105) & 0xFFFF, built.Layer2Pointer(slot) & 0xFFFF);

        var srcSp = SpriteData.Parse(built, 0x105);
        var dstSp = SpriteData.Parse(built, slot);
        Assert.Equal(srcSp.SpriteMemory, dstSp.SpriteMemory);
        Assert.Equal(srcSp.Buoyancy, dstSp.Buoyancy);
        Assert.Equal(srcSp.Sprites, dstSp.Sprites);
    }

    [Fact]
    public void an_edited_base_copies_its_edits_and_the_copy_shows_in_the_editor()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }

        var s = NewSession();
        s.ShowLevel(0x105);
        for (int x = 4; x < 10; x++) s.Edit!.Paint(x, 6, 0x100);
        s.Edit!.EndStroke();

        int slot = s.CreateCourseBotLevel("Painted", 0x105);
        Assert.True(slot > 0, s.Status);
        s.ShowLevel(slot);
        Assert.Equal(0x100, s.Scene!.Grid.Get(6, 6));    // the copy carries the edit
    }

    /// <summary>Merely LOOKING at a level writes a base-identical entry into the .pdp on the
    /// next save; those slots must still count as free, while actually edited ones must not.</summary>
    [Fact]
    public void slot_picking_skips_edited_levels_but_not_merely_visited_ones()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }

        var s = NewSession();
        s.ShowLevel(0x001);                              // edit $001: really used
        s.Edit!.Paint(6, 6, 0x100);
        s.Edit.EndStroke();
        s.ShowLevel(0x002);                              // just visit $002
        s.ShowLevel(0x105);
        s.Save();                                        // both now have .pdp entries

        Assert.Equal(0x002, s.CreateCourseBotLevel("A", 0x105));
        Assert.Equal(0x003, s.CreateCourseBotLevel("B", 0x105));
    }

    [Fact]
    public void deleting_reverts_the_slot_and_frees_it_for_reuse()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }

        var s = NewSession();
        s.ShowLevel(0x105);
        for (int x = 4; x < 10; x++) s.Edit!.Paint(x, 6, 0x100);
        s.Edit!.EndStroke();
        int slot = s.CreateCourseBotLevel("Doomed", 0x105);
        Assert.True(slot > 0, s.Status);

        // Delete WHILE the course level is on screen — the reparse must not stash the dying
        // state back in on the way out.
        s.ShowLevel(slot);
        Assert.Equal(0x100, s.Scene!.Grid.Get(6, 6));
        s.DeleteCourseBotLevel(slot);
        Assert.Empty(s.CourseBotEntries);
        Assert.Null(s.Project!.Data.LevelOrNull(slot));
        Assert.NotEqual(0x100, s.Scene!.Grid.Get(6, 6)); // the slot shows the base ROM again

        s.Save();                                        // re-stashes a base-identical entry...
        s.ShowLevel(0x105);                              // (the SHOWN level is never assigned)
        Assert.Equal(slot, s.CreateCourseBotLevel("Reborn", 0x105));  // ...which stays free
    }

    [Fact]
    public void courses_survive_a_save_and_reopen()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }

        var a = NewSession();
        a.ShowLevel(0x105);
        a.Edit!.Paint(6, 6, 0x100);
        a.Edit.EndStroke();
        int slot = a.CreateCourseBotLevel("Keeper", 0x105);
        Assert.True(slot > 0, a.Status);
        a.Save();

        var b = new EditorSession();
        Assert.True(b.OpenProject(a.Project!.FilePath), b.Status);
        Assert.Equal([(slot, "Keeper")], b.CourseBotEntries);
        b.ShowLevel(slot);
        Assert.Equal(0x100, b.Scene!.Grid.Get(6, 6));
    }

    [Fact]
    public void an_older_pdp_without_course_bot_still_loads()
    {
        var data = ProjectFile.FromJson("""{"SchemaVersion":1}""");
        Assert.NotNull(data.CourseBot);
        Assert.Empty(data.CourseBot);
    }
}
