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
}
