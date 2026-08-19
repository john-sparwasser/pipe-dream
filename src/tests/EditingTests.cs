using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PipeDream.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Painting and undo through the real window.
///
/// The thing worth pinning hardest: painting must produce OBJECTS. The grid is a projection
/// of the object stream, so an edit that only writes the grid renders perfectly and then
/// vanishes on save — the project stores objects, not pixels. Every test here therefore
/// checks the object list, not just what is on screen.
///
/// Undo grouping is the other one: "ctrl+Z only undid part of what I did" is what happens
/// when undo is grouped per cell instead of per stroke.
/// </summary>
public class EditingTests(ITestOutputHelper log)
{
    private static string RomPath => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static bool HaveRom => File.Exists(RomPath);

    /// <summary>
    /// A PREPPED vanilla, cached for the whole run. Painting places Direct Map16 objects, and
    /// raw vanilla has no DM16 ASM to render them — on that base a stroke would paint pixels
    /// and then reconcile back to nothing, which is exactly the behaviour
    /// <see cref="tile_placement_is_refused_on_a_base_without_dm16"/> pins.
    /// </summary>
    /// <summary>The shared prepped ROM: prep is expensive and a private copy per class raced
    /// once the tests became one assembly.</summary>
    private static string? Prepped => PreppedRom.Path;

    /// <summary>An edit model over a real level — painting needs the object engine, so there
    /// is no useful fake here.</summary>
    private static (Rom Rom, LevelScene Scene, LevelEdit Edit)? RealEdit(int level = 0x105)
    {
        if (Prepped is not { } path) return null;
        var rom = Rom.Load(path);
        var scene = LevelScene.Build(rom, level, LevelScene.SpriteDraw.Skip);
        return (rom, scene, new LevelEdit(rom, scene, scene.Level.Objects));
    }

    private static (MainWindow W, LevelView C)? Open()
    {
        if (Prepped is not { } path) return null;
        Program.RomPath = path;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (w, w.GetControl<LevelView>("Canvas"));
    }

    private static Point Cell(LevelView v, Window w, int x, int y)
        => v.TranslatePoint(new Point(x * 16 * v.Zoom + 8 - v.Origin.X,
                                      y * 16 * v.Zoom + 8 - v.Origin.Y), w)!.Value;

    // ---- the edit model ----

    /// <summary>The one that matters: a stroke has to end up in the object stream, or the
    /// edit is a rendering illusion that disappears the moment the project is saved.</summary>
    [Fact]
    public void a_stroke_becomes_objects_in_the_stream()
    {
        if (RealEdit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, scene, edit) = r;
        int before = edit.Objects.Count;

        for (int x = 4; x < 12; x++) edit.Paint(x, 6, 0x100);
        edit.EndStroke();

        Assert.True(edit.Objects.Count > before, "painting produced no objects");
        Assert.True(edit.Dirty);
        // The engine's own render agrees with what is on screen.
        Assert.Equal(0x100, scene.Grid.Get(8, 6));
    }

    /// <summary>A straight drag must merge into few wide objects, not one per cell — that is
    /// what Dm16Saver's run-merging is for, and 20 objects per drag would bloat the stream.</summary>
    [Fact]
    public void a_straight_drag_merges_into_a_run_rather_than_one_object_per_cell()
    {
        if (RealEdit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, _, edit) = r;
        int before = edit.Objects.Count;

        for (int x = 4; x < 24; x++) edit.Paint(x, 6, 0x100);   // 20 cells
        edit.EndStroke();

        int added = edit.Objects.Count - before;
        log.WriteLine($"20 cells -> {added} object(s)");
        Assert.True(added < 20, $"20 cells produced {added} objects — runs are not merging");
    }

    [Fact]
    public void one_stroke_is_one_undo_however_many_cells_it_covers()
    {
        if (RealEdit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, scene, edit) = r;
        var before = scene.Grid.Get(9, 6);
        int objsBefore = edit.Objects.Count;

        for (int x = 4; x < 12; x++) edit.Paint(x, 6, 0x100);
        edit.EndStroke();
        Assert.Equal(1, edit.UndoDepth);

        Assert.True(edit.Undo());
        Assert.Equal(objsBefore, edit.Objects.Count);       // the whole stroke came back out
        Assert.Equal(before, scene.Grid.Get(9, 6));         // ...and the pixels agree
        Assert.False(edit.CanUndo);
    }

    [Fact]
    public void separate_strokes_undo_separately()
    {
        if (RealEdit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, scene, edit) = r;
        edit.Paint(4, 6, 0x100); edit.EndStroke();
        edit.Paint(5, 7, 0x101); edit.EndStroke();
        Assert.Equal(2, edit.UndoDepth);

        edit.Undo();
        Assert.Equal(0x100, scene.Grid.Get(4, 6));          // the first stroke survives
        Assert.NotEqual(0x101, scene.Grid.Get(5, 7));
    }

    [Fact]
    public void redo_replays_a_stroke_and_a_new_edit_drops_the_redo_branch()
    {
        if (RealEdit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, scene, edit) = r;
        edit.Paint(4, 6, 0x100); edit.EndStroke();
        edit.Undo();
        Assert.True(edit.CanRedo);

        edit.Redo();
        Assert.Equal(0x100, scene.Grid.Get(4, 6));

        edit.Undo();
        edit.Paint(5, 6, 0x102); edit.EndStroke();
        Assert.False(edit.CanRedo);                          // the old branch is gone
    }

    /// <summary>Raw vanilla has no Direct Map16 ASM, so placed tiles would render as nothing.
    /// That has to be refused with a reason, not silently swallowed after the pixels already
    /// moved — the optimistic paint makes a silent failure look like a rendering glitch.</summary>
    [Fact]
    public void tile_placement_is_refused_on_a_base_without_dm16()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var rom = Rom.Load(RomPath);                       // raw vanilla, unprepped
        Assert.False(rom.HasDm16Hijack);
        var scene = LevelScene.Build(rom, 0x105, LevelScene.SpriteDraw.Skip);
        var edit = new LevelEdit(rom, scene, scene.Level.Objects);

        Assert.NotNull(edit.TilePlacementBlocked);
        Assert.False(edit.Paint(4, 6, 0x100));
        edit.EndStroke();
        Assert.Equal(0, edit.UndoDepth);
        Assert.False(edit.Dirty);
    }

    /// <summary>BG-space tiles (0x4000+) live on layer 2; a DM16 object cannot address them,
    /// so stamping one must be refused rather than silently doing nothing on save.</summary>
    [Fact]
    public void bg_tiles_are_refused_rather_than_silently_dropped()
    {
        if (RealEdit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, _, edit) = r;
        Assert.False(edit.Paint(4, 6, 0x4000));
        Assert.False(edit.Paint(4, 6, 0x4123));
        Assert.Equal(0, edit.UndoDepth);
    }

    /// <summary>A diagonal drag paints only the cells it crossed, not the rectangle they
    /// span — FromBrush skips untouched cells because they are left Empty.</summary>
    [Fact]
    public void a_diagonal_stroke_does_not_fill_its_bounding_box()
    {
        if (RealEdit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, scene, edit) = r;
        var corner = scene.Grid.Get(4, 9);                   // box corner, never painted

        for (int i = 0; i < 4; i++) edit.Paint(4 + i, 6 + i, 0x100);
        edit.EndStroke();

        Assert.Equal(0x100, scene.Grid.Get(4, 6));
        Assert.Equal(0x100, scene.Grid.Get(7, 9));
        Assert.Equal(corner, scene.Grid.Get(4, 9));          // untouched
    }

    // ---- through the window ----

    [AvaloniaFact]
    public void dragging_across_the_canvas_paints_every_cell_it_crosses()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, canvas) = o;
        Dispatcher.UIThread.RunJobs();

        var painted = new List<(int, int)>();
        canvas.CellPainted += (_, c) => painted.Add(c);

        // A fast drag: two pointer samples five cells apart. Every cell between them must
        // still be painted, or strokes have holes in them at speed.
        w.MouseDown(Cell(canvas, w, 2, 10), MouseButton.Right);
        w.MouseMove(Cell(canvas, w, 7, 10));
        w.MouseUp(Cell(canvas, w, 7, 10), MouseButton.Right);

        log.WriteLine("painted: " + string.Join(" ", painted));
        for (int x = 2; x <= 7; x++) Assert.Contains((x, 10), painted);
    }

    [AvaloniaFact]
    public void a_drag_through_the_window_adds_objects_and_undo_removes_them()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, canvas) = o;
        Dispatcher.UIThread.RunJobs();

        var edit = (LevelEdit)typeof(MainWindow)
            .GetField("edit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(w)!;
        int before = edit.Objects.Count;

        w.MouseDown(Cell(canvas, w, 2, 10), MouseButton.Right);
        w.MouseMove(Cell(canvas, w, 7, 10));
        w.MouseUp(Cell(canvas, w, 7, 10), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.True(edit.Objects.Count > before, "the drag added no objects");
        Assert.Equal(1, edit.UndoDepth);

        Assert.True(edit.Undo());
        Assert.Equal(before, edit.Objects.Count);
    }
}
