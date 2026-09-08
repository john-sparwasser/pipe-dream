using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// ExAnimation end to end (reference/EXANIMATION.md): a slot set in the session lands in the
/// project file, survives save, and comes out of the built ROM as the same record — and a source
/// file 60 imported in the session is in the built ROM at the address the record's slots read.
/// </summary>
public class ExAnimationFlowTests(ITestOutputHelper log) : IDisposable
{
    private readonly ITestOutputHelper log = log;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pdexan-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    [Fact]
    public void a_slot_set_in_the_session_is_in_the_project_and_the_built_rom()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
        s.ShowLevel(0x105);

        var gfx = Enumerable.Range(0, 0x800).Select(i => (byte)(i * 7)).ToArray();
        Assert.True(s.SetExAnimSource(0, gfx), s.Status);
        var slot = new ExAnimation.Slot(2, 4, ExAnimation.TriggerPow, 2, 0x8A00 | 0, [0x0000, 0x0080], 0);
        Assert.True(s.SetExAnim(global: false, [slot], altFileIndex: 0), s.Status);
        var g = new ExAnimation.Slot(0, 1, ExAnimation.TriggerNone, 3, 0x0B00, [0x7D20, 0x87A0, 0x9240], 0);
        Assert.True(s.SetExAnim(global: true, [g], altFileIndex: 0), s.Status);

        Assert.True(s.Project!.Data.ExAnimation.Levels.ContainsKey("105"), "level record not in the project");
        Assert.NotNull(s.Project.Data.ExAnimation.Global);
        s.Save();
        Assert.True(s.Project.Data.Gfx.ContainsKey("060"), "file 60 not in the project");
        log.WriteLine(s.Build());
        string built = Path.Combine(dir, "proj", "build", s.Project.Name + ".smc");
        Assert.True(File.Exists(built), "no built ROM");
        var rom = Rom.Load(built);

        var lvl = Assert.Single(ExAnimation.ReadLevel(rom, 0x105));
        Assert.Equal((2, 4, ExAnimation.TriggerPow, 2, true), (lvl.Index, lvl.Type, lvl.Trigger, lvl.FrameCount, lvl.AltFile));
        Assert.Equal(4, lvl.Frames.Length);                       // POW doubles: LM pads the triggered half
        var glob = Assert.Single(ExAnimation.ReadGlobal(rom));
        Assert.Equal(0xB0, glob.DestTile);
        int file = rom.LmAltExGfx(0);
        Assert.True(file > 0, "file 60 not installed in the built ROM");
        Assert.Equal(gfx, rom.Data.AsSpan(rom.FileOffset(file), gfx.Length).ToArray());

        // The persisted project reopens to the same state.
        var s2 = new EditorSession();
        Assert.True(s2.OpenProject(Path.Combine(dir, "proj", "project.pdp")), s2.Status);
        s2.ShowLevel(0x105);
        Assert.Single(s2.ExAnimSlots(global: false));
        Assert.Single(s2.ExAnimSlots(global: true));
    }

    /// <summary>Reassigning moves the whole record to the new number; a taken number refuses.</summary>
    [Fact]
    public void reassigning_a_slot_moves_it_and_refuses_a_taken_number()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
        s.ShowLevel(0x105);
        var a = s.AddExAnimSlot(global: false);
        var b = s.AddExAnimSlot(global: false);
        Assert.Equal((0, 1), (a!.Value.Index, b!.Value.Index));

        Assert.True(s.ReassignExAnimSlot(global: false, from: 0, to: 5), s.Status);
        var back = s.ExAnimSlots(global: false).OrderBy(x => x.Index).ToList();
        Assert.Equal([1, 5], back.Select(x => x.Index));
        Assert.Equal(a.Value.Frames, back[1].Frames);

        Assert.False(s.ReassignExAnimSlot(global: false, from: 5, to: 1));   // 1 is taken
        Assert.False(s.ReassignExAnimSlot(global: false, from: 5, to: 0x20)); // out of range
    }

    /// <summary>
    /// The Animations mode's Overworld tab writes a submap's list into Lunar Magic's OWN
    /// overworld table — the separate hack prep v17 carries — at the submap's own index, and it
    /// survives the project and a build. Each submap is its own entry, and none of it disturbs
    /// the level lists, which live in the other table entirely (reference/EXANIMATION.md §10).
    /// </summary>
    [Fact]
    public void a_submap_list_writes_to_the_overworlds_own_table_and_survives_a_build()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
        s.ShowLevel(0x105);
        Assert.True(s.Rom!.LmOwExAnimBase > 0, "prep did not carry the overworld hack");

        // The level's list first, so the two tables can be told apart afterwards.
        var lvl = new ExAnimation.Slot(0, 1, ExAnimation.TriggerNone, 1, 0x0000, [0x7D00], 0);
        Assert.True(s.SetExAnim(global: false, [lvl], 0), s.Status);

        // Yoshi's Island (1) and Bowser's Valley (4): the two submaps the probe pinned the index
        // rule with. The source is the overworld's own animated-tile buffer, $7EAD00.
        var yi = new ExAnimation.Slot(0, 1, ExAnimation.TriggerNone, 1, 0x1000, [0xAD00], 0);
        var vb = new ExAnimation.Slot(3, 2, ExAnimation.TriggerNone, 2, 0x1200, [0xAD00, 0xAD20], 0);
        s.ExAnimSubmap = 1;
        Assert.Equal(1, s.ExAnimListIndex);
        Assert.True(s.ExAnimReady);
        Assert.Null(s.ExAnimNotReadyWhy);
        Assert.True(s.SetExAnim(global: false, [yi], 0), s.Status);
        s.ExAnimSubmap = 4;
        Assert.True(s.SetExAnim(global: false, [vb], 0), s.Status);

        // Read straight back out of the session ROM, at LM's own table.
        Assert.Equal(0x600, Assert.Single(ExAnimation.ReadSubmap(s.Rom, 1)).OwSrcTile(0));   // as LM's dialog shows it
        Assert.Equal(2, Assert.Single(ExAnimation.ReadSubmap(s.Rom, 4)).FrameCount);
        foreach (int empty in new[] { 0, 2, 3, 5, 6 }) Assert.Empty(ExAnimation.ReadSubmap(s.Rom, empty));

        Assert.Equal(["1", "4"], s.Project!.Data.ExAnimation.Submaps.Keys.OrderBy(k => k));
        Assert.True(s.Project.Data.ExAnimation.Levels.ContainsKey("105"));
        s.Save();
        log.WriteLine(s.Build());
        string built = Path.Combine(dir, "proj", "build", s.Project.Name + ".smc");
        Assert.True(File.Exists(built), "no built ROM");
        var rom = Rom.Load(built);

        var one = Assert.Single(ExAnimation.ReadSubmap(rom, 1));
        Assert.Equal((0, 1, 1, 0x100), (one.Index, one.Type, one.FrameCount, one.DestTile));
        var four = Assert.Single(ExAnimation.ReadSubmap(rom, 4));
        Assert.Equal((3, 2, 2, 0x120), (four.Index, four.Type, four.FrameCount, four.DestTile));
        Assert.Empty(ExAnimation.ReadSubmap(rom, 0));
        Assert.Single(ExAnimation.ReadLevel(rom, 0x105));       // the level table is a different table

        // The persisted project reopens to the same lists.
        var s2 = new EditorSession();
        Assert.True(s2.OpenProject(Path.Combine(dir, "proj", "project.pdp")), s2.Status);
        s2.ExAnimSubmap = 4;
        Assert.Equal(3, Assert.Single(s2.ExAnimSlots(global: false)).Index);

        // Emptying a submap's list takes its entry out of both the ROM and the project.
        s2.ExAnimSubmap = 1;
        Assert.True(s2.SetExAnim(global: false, [], 0), s2.Status);
        Assert.Empty(ExAnimation.ReadSubmap(s2.Rom!, 1));
        Assert.DoesNotContain("1", s2.Project!.Data.ExAnimation.Submaps.Keys);
    }

    /// <summary>A base without the overworld hack says so and refuses, rather than landing bytes
    /// at a table it does not have — the level lists still write.</summary>
    [Fact]
    public void a_submap_list_is_refused_on_a_base_without_the_overworld_hack()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, "v16.smc");
        File.Copy(Vanilla, p, overwrite: true);
        Assert.Null(RomPrep.PrepInPlace(p, version: 16));       // the version before the hack
        var s = new EditorSession();
        Assert.True(s.OpenRom(p));
        Assert.True(s.Rom!.LmExAnimBase > 0);
        Assert.Equal(-1, s.Rom.LmOwExAnimBase);
        var slot = new ExAnimation.Slot(0, 1, ExAnimation.TriggerNone, 1, 0x0000, [0x7D00], 0);

        s.ExAnimSubmap = 2;
        Assert.False(s.ExAnimReady);
        Assert.Contains("overworld ExAnimation hack", s.ExAnimNotReadyWhy);
        Assert.Empty(s.ExAnimSlots(global: false));
        Assert.False(s.SetExAnim(global: false, [slot], 0));

        s.ExAnimSubmap = -1;                                    // the level: writes as ever
        Assert.True(s.SetExAnim(global: false, [slot], 0), s.Status);
    }
}
