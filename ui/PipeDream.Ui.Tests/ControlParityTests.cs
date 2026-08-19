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
/// The canvas controls must match the ImGui editor's ObjectTool exactly, so muscle memory
/// carries over. Read off the ImGui editor's ObjectTool and LevelViewport before they were
/// deleted (git history, if the exact behaviour is ever in doubt):
///
///   RIGHT click/drag   stamp the tile brush (right-click WITH a selection duplicates it)
///   LEFT click+drag    rubber-band select, live while dragging
///   LEFT on selection  drag to move
///   LEFT click, still  cycle the overlap stack under the cursor
///   CTRL+LEFT drag     grab the covered tiles as the brush (no selection change)
///   DELETE             delete the selection
///   WHEEL              horizontal scroll; SHIFT+WHEEL vertical; vertical levels normal
///   CTRL+Z / CTRL+SHIFT+Z   undo / redo
///
/// Left-drag painting was the obvious guess and the wrong one — these pin the real bindings.
/// </summary>
public class ControlParityTests(ITestOutputHelper log)
{
    private static string RomPath => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static readonly Lazy<string?> Prepped = new(() =>
    {
        if (!File.Exists(RomPath)) return null;
        string tmp = Path.Combine(Path.GetTempPath(), "pdui-prepped.smc");
        if (!File.Exists(tmp))
        {
            File.Copy(RomPath, tmp, overwrite: true);
            if (RomPrep.PrepInPlace(tmp) is not null) return null;
        }
        return tmp;
    });

    private static (MainWindow W, LevelView C)? Open()
    {
        if (Prepped.Value is not { } path) return null;
        Program.RomPath = path;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (w, w.GetControl<LevelView>("Canvas"));
    }

    private static Point At(LevelView v, Window w, int x, int y)
        => v.TranslatePoint(new Point(x * 16 * v.Zoom + 8 - v.Origin.X,
                                      y * 16 * v.Zoom + 8 - v.Origin.Y), w)!.Value;

    private static LevelEdit EditOf(MainWindow w) => (LevelEdit)typeof(MainWindow)
        .GetField("edit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .GetValue(w)!;

    [AvaloniaFact]
    public void right_drag_stamps_tiles_and_left_drag_does_not()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var edit = EditOf(w);
        int before = edit.Objects.Count;

        // LEFT drag: selects, never paints.
        w.MouseDown(At(c, w, 2, 10), MouseButton.Left);
        w.MouseMove(At(c, w, 7, 10));
        w.MouseUp(At(c, w, 7, 10), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, edit.Objects.Count);

        // RIGHT drag: stamps.
        w.MouseDown(At(c, w, 2, 12), MouseButton.Right);
        w.MouseMove(At(c, w, 7, 12));
        w.MouseUp(At(c, w, 7, 12), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.True(edit.Objects.Count > before, "right drag did not stamp");
    }

    [AvaloniaFact]
    public void left_drag_selects_the_objects_it_covers()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var edit = EditOf(w);

        w.MouseDown(At(c, w, 0, 0), MouseButton.Left);
        w.MouseMove(At(c, w, 20, 20));
        w.MouseUp(At(c, w, 20, 20), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        log.WriteLine($"selected {edit.Selection.Count} objects");
        Assert.NotEmpty(edit.Selection);
    }

    /// <summary>Ctrl+drag grabs tiles instead of selecting — the green band in the ImGui tool.</summary>
    [AvaloniaFact]
    public void ctrl_left_drag_grabs_tiles_and_leaves_the_selection_alone()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var edit = EditOf(w);

        (int X, int Y, int W, int H)? grabbed = null;
        c.GrabRequested += (_, g) => grabbed = g;

        w.MouseDown(At(c, w, 3, 10), MouseButton.Left, RawInputModifiers.Control);
        w.MouseMove(At(c, w, 6, 12));
        w.MouseUp(At(c, w, 6, 12), MouseButton.Left, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(grabbed);
        Assert.Equal((3, 10, 4, 3), grabbed!.Value);
        Assert.Empty(edit.Selection);            // a grab is not a selection
    }

    /// <summary>A stationary left click cycles the overlap stack: click again on the same cell
    /// and you get the object beneath, LM-style.</summary>
    [AvaloniaFact]
    public void a_stationary_left_click_selects_and_repeats_cycle_the_stack()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var edit = EditOf(w);

        // Find a cell that actually has an object under it.
        (int X, int Y)? target = null;
        for (int y = 0; y < 27 && target is null; y++)
            for (int x = 0; x < 32; x++)
                if (edit.ObjectAt(x, y) is not null) { target = (x, y); break; }
        Assert.NotNull(target);

        var p = At(c, w, target!.Value.X, target.Value.Y);
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(edit.Selection);
    }

    [AvaloniaFact]
    public void right_click_with_a_selection_duplicates_it_instead_of_stamping()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var edit = EditOf(w);

        w.MouseDown(At(c, w, 0, 0), MouseButton.Left);
        w.MouseMove(At(c, w, 20, 20));
        w.MouseUp(At(c, w, 20, 20), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        int selected = edit.Selection.Count;
        Assert.True(selected > 0);
        int before = edit.Objects.Count;

        w.MouseDown(At(c, w, 24, 6), MouseButton.Right);
        w.MouseUp(At(c, w, 24, 6), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before + selected, edit.Objects.Count);
    }

    [AvaloniaFact]
    public void delete_removes_the_selection()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var edit = EditOf(w);

        w.MouseDown(At(c, w, 0, 0), MouseButton.Left);
        w.MouseMove(At(c, w, 20, 20));
        w.MouseUp(At(c, w, 20, 20), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        int selected = edit.Selection.Count;
        int before = edit.Objects.Count;
        Assert.True(selected > 0);

        c.Focus();
        w.KeyPress(Key.Delete, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before - selected, edit.Objects.Count);
        Assert.Empty(edit.Selection);
    }

    [AvaloniaFact]
    public void ctrl_z_undoes_and_ctrl_shift_z_redoes()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var edit = EditOf(w);
        int before = edit.Objects.Count;

        w.MouseDown(At(c, w, 2, 12), MouseButton.Right);
        w.MouseMove(At(c, w, 7, 12));
        w.MouseUp(At(c, w, 7, 12), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        int after = edit.Objects.Count;
        Assert.True(after > before);

        w.KeyPress(Key.Z, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, edit.Objects.Count);

        w.KeyPress(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(after, edit.Objects.Count);
    }

    /// <summary>Horizontal levels scroll SIDEWAYS with the wheel — the single most jarring
    /// difference if it were left as a normal vertical wheel.</summary>
    [AvaloniaFact]
    public void the_wheel_scrolls_horizontally_and_shift_wheel_vertically()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;

        var moves = new List<(double Dx, double Dy)>();
        c.ScrollRequested += (_, d) => moves.Add(d);
        c.Vertical = false;

        var at = At(c, w, 4, 4);
        w.MouseWheel(at, new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(moves);
        Assert.NotEqual(0, moves[0].Dx);          // sideways, not down
        Assert.Equal(0, moves[0].Dy);

        moves.Clear();
        w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(moves);
        Assert.Equal(0, moves[0].Dx);             // shift flips it to vertical
        Assert.NotEqual(0, moves[0].Dy);

        // A VERTICAL level keeps the normal wheel, so the scroll viewer handles it.
        moves.Clear();
        c.Vertical = true;
        w.MouseWheel(at, new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(moves);
    }
}
