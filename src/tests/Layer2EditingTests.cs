using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PipeDream.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Layer-2 editing.
///
/// A level's layer 2 is EITHER a background image OR an object stream, never both — the layer-2
/// pointer's bank byte is the mode, so there is no separate flag (CONTRACT §10). That makes the
/// conversion the interesting part: "no layer-2 objects" and "an empty layer-2 object stream" are
/// genuinely different in the ROM, so giving a background level an object layer has to be
/// explicit and has to be recorded, not inferred from an empty list.
///
/// Layer 2 uses the same object stream format as layer 1, so the same editor drives both. What
/// differs is the render layer and which of the scene's grids the result replaces — mixing those
/// up would paint layer-2 edits onto layer 1.
/// </summary>
public class Layer2EditingTests(ITestOutputHelper log) : IDisposable
{
    private readonly ITestOutputHelper log = log;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pduil2-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static bool HaveRom => File.Exists(Vanilla);

    /// <summary>
    /// Get to a level with an editable layer 2, converting one if the ROM offers none among the
    /// levels sampled. Most vanilla levels use a background image, so relying on finding a
    /// ready-made object layer 2 would silently skip the tests that matter most here.
    /// </summary>
    private static int WithLayer2(EditorSession s, ITestOutputHelper log)
    {
        foreach (int lv in new[] { 0x105, 0x106, 0x0C5, 0x101, 0x104, 0x11B, 0x1F0, 0x024 })
        {
            s.ShowLevel(lv);
            if (s.Layer2Editable) { log.WriteLine($"level ${lv:X3} already has an object layer 2"); return lv; }
        }
        s.ShowLevel(0x105);
        s.SetLayer2ObjectMode(true);
        log.WriteLine("no sampled level has an object layer 2 — converted $105");
        return 0x105;
    }

    private static EditorSession? Project(string dir)
    {
        if (!HaveRom) return null;
        var s = new EditorSession();
        return s.NewProject(Path.Combine(dir, "proj"), Vanilla) ? s : null;
    }

    [Fact]
    public void editing_layer_2_writes_layer_2s_grid_and_leaves_layer_1_alone()
    {
        if (Project(dir) is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        WithLayer2(s, log);

        s.SetEditLayer(1);
        Assert.Equal(1, s.EditLayer);

        var layer1Before = s.Scene!.Grid.Clone();
        int before = s.Edit!.Objects.Count;
        for (int x = 4; x < 9; x++) s.Edit.Paint(x, 8, 0x100);
        s.Edit.EndStroke();
        Assert.True(s.Edit.Objects.Count > before, "the layer-2 stroke produced no objects");

        // Layer 1's grid must be untouched: the two layers are separate streams rendered into
        // separate grids, and the composer draws layer 2 BEHIND layer 1.
        for (int y = 0; y < 27; y++)
            for (int x = 0; x < s.Scene.Grid.Width; x++)
                Assert.Equal(layer1Before.Get(x, y), s.Scene.Grid.Get(x, y));
        Assert.Equal(0x100, s.Scene.Layer2!.Get(6, 8));
    }

    /// <summary>Each layer keeps its own undo history, so switching layers mid-session does not
    /// throw work away — the ImGui editor had to clear the history here because its undo closures
    /// captured whichever list was current.</summary>
    [Fact]
    public void switching_layers_keeps_both_histories()
    {
        if (Project(dir) is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        WithLayer2(s, log);

        s.Edit!.Paint(4, 4, 0x100);
        s.Edit.EndStroke();
        int l1Depth = s.Edit.UndoDepth;
        Assert.True(l1Depth > 0);

        s.SetEditLayer(1);
        s.Edit!.Paint(4, 6, 0x100);
        s.Edit.EndStroke();
        Assert.True(s.Edit.UndoDepth > 0);

        s.SetEditLayer(0);
        Assert.Equal(l1Depth, s.Edit!.UndoDepth);      // layer 1's history survived
    }

    [Fact]
    public void a_background_level_has_no_layer_2_to_edit_until_it_is_converted()
    {
        if (Project(dir) is not { } s) { log.WriteLine("SKIP: no ROM"); return; }

        // Find a background-image level — most levels are one.
        int? bg = null;
        foreach (int lv in new[] { 0x105, 0x106, 0x101, 0x0C5, 0x104 })
        {
            s.ShowLevel(lv);
            if (!s.Layer2Editable) { bg = lv; break; }
        }
        if (bg is null) { log.WriteLine("SKIP: no background-image level found"); return; }
        log.WriteLine($"level ${bg:X3} uses a background image");

        Assert.Contains("background image", s.SetEditLayer(1));
        Assert.Equal(0, s.EditLayer);                  // refused, not silently accepted

        string note = s.SetLayer2ObjectMode(true);
        log.WriteLine(note);
        Assert.True(s.Layer2Editable);
        Assert.True(s.Layer2FromProject);              // it is the project's, not the base ROM's
        s.SetEditLayer(1);
        Assert.Equal(1, s.EditLayer);
        Assert.NotNull(s.Scene!.Layer2);

        // And back again: dropping it restores the base ROM's background.
        s.SetLayer2ObjectMode(false);
        Assert.False(s.Layer2Editable);
        Assert.Equal(0, s.EditLayer);
    }

    /// <summary>The conversion has to be RECORDED, not inferred: an empty object stream and no
    /// object stream are different things in the ROM, so a reopened project must still show the
    /// converted level as converted.</summary>
    [Fact]
    public void a_conversion_survives_save_and_reopen()
    {
        if (Project(dir) is not { } a) { log.WriteLine("SKIP: no ROM"); return; }
        a.ShowLevel(0x105);
        if (a.Layer2Editable) { log.WriteLine("SKIP: $105 already has an object layer 2"); return; }
        string pdp = a.Project!.FilePath;

        a.SetLayer2ObjectMode(true);
        a.SetEditLayer(1);
        for (int x = 3; x < 7; x++) a.Edit!.Paint(x, 10, 0x100);
        a.Edit!.EndStroke();
        int objects = a.Edit.Objects.Count;
        Assert.True(objects > 0);
        a.Save();

        var b = new EditorSession();
        Assert.True(b.OpenProject(pdp), b.Status);
        b.ShowLevel(0x105);
        Assert.True(b.Layer2Editable, "the conversion was lost on reopen");
        Assert.True(b.Layer2FromProject);
        b.SetEditLayer(1);
        Assert.Equal(objects, b.Edit!.Objects.Count);
    }

    [Fact]
    public void pointing_layer_2_at_a_background_drops_the_object_stream()
    {
        if (Project(dir) is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        s.ShowLevel(0x105);
        s.SetLayer2ObjectMode(true);
        Assert.True(s.Layer2Editable);

        var choices = s.Backgrounds();
        Assert.NotEmpty(choices);
        int pick = choices.First(c => c.Lo16 != s.CurrentBackground).Lo16;
        string note = s.SetLayer2Background(pick);
        log.WriteLine(note);

        Assert.False(s.Layer2Editable);       // the two modes are exclusive
        Assert.Equal(pick, s.CurrentBackground);
        Assert.Equal(0, s.EditLayer);
    }

    /// <summary>Building an object layer a level's MODE never reads is the quietest way to waste
    /// an afternoon, so the session reports it.</summary>
    [Fact]
    public void converting_warns_when_the_level_mode_ignores_layer_2()
    {
        if (Project(dir) is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        s.ShowLevel(0x105);
        if (s.Layer2Editable) { log.WriteLine("SKIP: $105 already has an object layer 2"); return; }

        string note = s.SetLayer2ObjectMode(true);
        log.WriteLine($"mode reads layer 2: {s.LevelModeReadsLayer2}; note: {note}");
        if (!s.LevelModeReadsLayer2) Assert.Contains("never loads", note);
    }

    // ---- through the window ----

    [AvaloniaFact]
    public void the_layer_buttons_show_what_is_available()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var l1 = w.GetControl<ToggleButton>("LayerOne");
        var l2 = w.GetControl<ToggleButton>("LayerTwo");
        var add = w.GetControl<Button>("AddLayer2");
        var drop = w.GetControl<Button>("DropLayer2");

        Assert.True(l1.IsChecked);
        // Exactly one of "switch to layer 2" and "create layer 2" makes sense at a time.
        Assert.Equal(l2.IsEnabled, !add.IsVisible);
        // Dropping is only offered for a stream this project created, never the base ROM's.
        Assert.False(drop.IsVisible);
    }
}
