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
/// Painting and undo, driven through the real window. Undo grouping is the specific thing
/// worth pinning: "ctrl+Z only undid part of what I did" was a real bug in the ImGui editor,
/// and it comes from grouping undo by cell instead of by stroke.
/// </summary>
public class EditingTests(ITestOutputHelper log)
{
    private static string RomPath => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static (MainWindow W, LevelView C)? Open()
    {
        if (!File.Exists(RomPath)) return null;
        Program.RomPath = RomPath;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (w, w.GetControl<LevelView>("Canvas"));
    }

    // Canvas-local cell centre, translated to WINDOW space: the mouse helpers take window
    // coordinates, and the canvas sits to the right of the drawer.
    private static Point Cell(LevelView v, Window w, int x, int y)
        => v.TranslatePoint(new Point(x * 16 * v.Zoom + 8 - v.Origin.X,
                                      y * 16 * v.Zoom + 8 - v.Origin.Y), w)!.Value;

    // ---- the edit model on its own: no window, no ROM ----

    private static LevelEdit FakeEdit(out Map16Grid grid)
    {
        var g = new Map16Grid(32, 27);
        grid = g;
        var caches = new uint[4][][];
        for (int p = 0; p < 4; p++)
        {
            caches[p] = new uint[0x200][];
            for (int t = 0; t < 0x200; t++) caches[p][t] = new uint[256];
        }
        var phases = new uint[4][];
        for (int p = 0; p < 4; p++) phases[p] = new uint[32 * 16 * 27 * 16];
        var scene = new LevelScene(phases, 32 * 16, 27 * 16, g, null!, caches);
        return new LevelEdit(scene);
    }

    [Fact]
    public void one_stroke_is_one_undo_however_many_cells_it_covers()
    {
        var edit = FakeEdit(out var grid);
        for (int x = 0; x < 10; x++) edit.Paint(x, 5, 0x100);
        edit.EndStroke();

        Assert.Equal(1, edit.UndoDepth);
        Assert.Equal(0x100, grid.Get(9, 5));

        Assert.True(edit.Undo());
        // ALL ten cells come back, not just the last one.
        for (int x = 0; x < 10; x++)
            Assert.NotEqual(0x100, grid.Get(x, 5));
        Assert.False(edit.CanUndo);
    }

    [Fact]
    public void separate_strokes_undo_separately()
    {
        var edit = FakeEdit(out var grid);
        edit.Paint(1, 1, 0x100); edit.EndStroke();
        edit.Paint(2, 2, 0x101); edit.EndStroke();
        Assert.Equal(2, edit.UndoDepth);

        edit.Undo();
        Assert.Equal(0x100, grid.Get(1, 1));      // the first stroke survives
        Assert.NotEqual(0x101, grid.Get(2, 2));
    }

    /// <summary>Dragging back over a cell within one stroke must not bury its ORIGINAL value,
    /// or undo restores an intermediate state instead of what was there before.</summary>
    [Fact]
    public void repainting_a_cell_within_a_stroke_still_undoes_to_the_original()
    {
        var edit = FakeEdit(out var grid);
        int original = grid.Get(3, 3);
        edit.Paint(3, 3, 0x100);
        edit.Paint(3, 3, 0x111);
        edit.EndStroke();

        edit.Undo();
        Assert.Equal(original, grid.Get(3, 3));
    }

    [Fact]
    public void redo_replays_a_stroke_and_a_new_edit_drops_the_redo_branch()
    {
        var edit = FakeEdit(out var grid);
        edit.Paint(4, 4, 0x100); edit.EndStroke();
        edit.Undo();
        Assert.True(edit.CanRedo);

        edit.Redo();
        Assert.Equal(0x100, grid.Get(4, 4));

        edit.Undo();
        edit.Paint(5, 5, 0x102); edit.EndStroke();
        Assert.False(edit.CanRedo);                // the old branch is gone
    }

    [Fact]
    public void painting_the_same_value_is_not_an_edit()
    {
        var edit = FakeEdit(out var grid);
        edit.Paint(6, 6, 0x100); edit.EndStroke();
        Assert.False(edit.Paint(6, 6, 0x100));     // no change, no new undo entry
        edit.EndStroke();
        Assert.Equal(1, edit.UndoDepth);
    }

    // ---- the same thing through the window ----

    [AvaloniaFact]
    public void dragging_across_the_canvas_paints_every_cell_it_crosses()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, canvas) = o;
        var palette = w.GetControl<Map16PaletteView>("Palette");
        Dispatcher.UIThread.RunJobs();

        var painted = new List<(int, int)>();
        canvas.CellPainted += (_, c) => painted.Add(c);

        // A fast drag: the pointer reports two samples five cells apart, and every cell
        // between them must still be painted or the stroke has holes in it.
        w.MouseDown(Cell(canvas, w, 2, 10), MouseButton.Left);
        w.MouseMove(Cell(canvas, w, 7, 10));
        w.MouseUp(Cell(canvas, w, 7, 10), MouseButton.Left);

        log.WriteLine("painted: " + string.Join(" ", painted));
        for (int x = 2; x <= 7; x++)
            Assert.Contains((x, 10), painted);
    }

    [AvaloniaFact]
    public void a_drag_then_undo_restores_the_level()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, canvas) = o;
        Dispatcher.UIThread.RunJobs();

        // Snapshot the row we are about to paint over.
        var scene = typeof(MainWindow).GetField("scene", System.Reflection.BindingFlags.NonPublic
                                                       | System.Reflection.BindingFlags.Instance)!
                                      .GetValue(w) as LevelScene;
        Assert.NotNull(scene);
        var before = Enumerable.Range(2, 6).Select(x => scene!.Grid.Get(x, 10)).ToArray();

        w.MouseDown(Cell(canvas, w, 2, 10), MouseButton.Left);
        w.MouseMove(Cell(canvas, w, 7, 10));
        w.MouseUp(Cell(canvas, w, 7, 10), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        bool changed = Enumerable.Range(2, 6).Select(x => scene!.Grid.Get(x, 10)).SequenceEqual(before) == false;
        Assert.True(changed, "the drag painted nothing");

        // Undo through the same model the Ctrl+Z menu item drives.
        var edit = typeof(MainWindow).GetField("edit", System.Reflection.BindingFlags.NonPublic
                                                     | System.Reflection.BindingFlags.Instance)!
                                     .GetValue(w) as LevelEdit;
        Assert.True(edit!.Undo());

        Assert.Equal(before, Enumerable.Range(2, 6).Select(x => scene!.Grid.Get(x, 10)).ToArray());
    }
}
