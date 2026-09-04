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
///   RIGHT click/drag   stamp the tile brush — a selection does NOT change that
///   RIGHT click        with a selection, duplicate it at the cursor (the level outranks the drawer)
///   LEFT click+drag    rubber-band select, live while dragging
///   LEFT on selection  drag to move, live under the cursor
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

    /// <summary>The shared prepped ROM (see PreppedRom): prep is expensive and a private copy
    /// per class raced once the tests became one assembly.</summary>
    private static string? Prepped => PreppedRom.Path;

    private static (MainWindow W, LevelView C)? Open()
    {
        if (Prepped is not { } path) return null;
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

    private static EditorSession SessionOf(MainWindow w) => (EditorSession)typeof(MainWindow)
        .GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .GetValue(w)!;

    private static void Invoke(MainWindow w, string method) => typeof(MainWindow)
        .GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .Invoke(w, null);

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
    public void ctrl_right_click_with_a_selection_duplicates_it()
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

        w.MouseDown(At(c, w, 24, 6), MouseButton.Right, RawInputModifiers.Control);
        w.MouseUp(At(c, w, 24, 6), MouseButton.Right, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before + selected, edit.Objects.Count);
    }

    /// <summary>The level's selection outranks the drawer: with tiles selected in the level, a
    /// plain right-click duplicates them and stamps nothing from the drawer. Drop the selection
    /// and the same right-click places the drawer's tile again.</summary>
    [AvaloniaFact]
    public void a_level_selection_takes_right_click_until_it_is_dropped()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var edit = EditOf(w);

        w.MouseDown(At(c, w, 0, 0), MouseButton.Left);
        w.MouseMove(At(c, w, 20, 20));
        w.MouseUp(At(c, w, 20, 20), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(edit.Selection);
        int before = edit.Objects.Count, selected = edit.Selection.Count;

        int tile = w.GetControl<Map16PaletteView>("Palette").Selected;
        w.MouseDown(At(c, w, 40, 6), MouseButton.Right);
        w.MouseUp(At(c, w, 40, 6), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before + selected, edit.Objects.Count);        // duplicated, not stamped

        edit.Selection.Clear();
        w.MouseDown(At(c, w, 24, 6), MouseButton.Right);
        w.MouseUp(At(c, w, 24, 6), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(tile, edit.Scene.Grid.Get(24, 6));            // the drawer places again
    }

    /// <summary>Dragging a selection moves it WHILE the mouse is down, not on release, and the
    /// whole drag is one undo entry.</summary>
    [AvaloniaFact]
    public void dragging_a_selection_moves_it_live_as_one_undo_entry()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var edit = EditOf(w);

        // Select one object by clicking it, then drag from a cell it owns.
        (int X, int Y)? target = null;
        for (int y = 0; y < 20 && target is null; y++)
            for (int x = 0; x < 24; x++)
                if (edit.ObjectAt(x, y) is not null) { target = (x, y); break; }
        Assert.NotNull(target);
        var (tx, ty) = target!.Value;

        w.MouseDown(At(c, w, tx, ty), MouseButton.Left);
        w.MouseUp(At(c, w, tx, ty), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        int sel = edit.Selection.Single();
        int x0 = edit.Objects[sel].AbsoluteX;
        int depth = edit.UndoDepth;

        w.MouseDown(At(c, w, tx, ty), MouseButton.Left);
        w.MouseMove(At(c, w, tx + 1, ty));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(x0 + 1, edit.Objects[sel].AbsoluteX);   // moved BEFORE the release

        w.MouseMove(At(c, w, tx + 3, ty));
        w.MouseUp(At(c, w, tx + 3, ty), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(x0 + 3, edit.Objects[sel].AbsoluteX);
        Assert.Equal(depth + 1, edit.UndoDepth);             // one drag, one undo
    }

    /// <summary>A drag is measured from where it started, not from the last step: dragged past the
    /// level's top (where the move is clamped) and back down, the object comes back to the cursor
    /// instead of staying short by the clamped rows; and a drag past the left edge stops at column
    /// 0 rather than wrapping into screen 31. A burst of pointer moves before the UI gets a turn is
    /// folded into ONE re-render, landing at the last position.</summary>
    [AvaloniaFact]
    public void a_drag_is_anchored_clamped_and_folds_a_burst_into_one_render()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var edit = EditOf(w);
        (int X, int Y)? target = null;
        for (int y = 2; y < 20 && target is null; y++)
            for (int x = 4; x < 24; x++)
                if (edit.ObjectAt(x, y) is not null) { target = (x, y); break; }
        var (tx, ty) = target!.Value;
        w.MouseDown(At(c, w, tx, ty), MouseButton.Left);
        w.MouseUp(At(c, w, tx, ty), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        int sel = edit.Selection.Single();
        int x0 = edit.Objects[sel].AbsoluteX, y0 = edit.Objects[sel].AbsoluteY;

        // Up past the top by far more rows than exist, then back to two rows above the start.
        w.MouseDown(At(c, w, tx, ty), MouseButton.Left);
        w.MouseMove(At(c, w, tx, ty - 60)); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, edit.Objects[sel].AbsoluteY);                 // clamped at the top
        w.MouseMove(At(c, w, tx, ty - 2)); Dispatcher.UIThread.RunJobs();
        Assert.Equal(y0 - 2, edit.Objects[sel].AbsoluteY);            // back under the cursor, not short by 58
        // Off the left edge: column 0, never screen 31.
        w.MouseMove(At(c, w, tx - 40, ty - 2)); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, edit.Objects[sel].AbsoluteX);
        w.MouseUp(At(c, w, tx - 40, ty - 2), MouseButton.Left); Dispatcher.UIThread.RunJobs();

        // A burst: eight pointer moves, one dispatcher turn, one render, final position. Raised as
        // raw routed events — the headless MouseMove helper pumps the dispatcher after each one,
        // which is exactly the gap a real burst does not leave.
        var obj = edit.Objects[sel];
        int renders = edit.Reconciles;
        w.MouseDown(At(c, w, obj.AbsoluteX, obj.AbsoluteY), MouseButton.Left);
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        for (int i = 1; i <= 8; i++)
            c.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, c, pointer, w,
                                              At(c, w, obj.AbsoluteX + i, obj.AbsoluteY), 0,
                                              new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
                                              KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(obj.AbsoluteX + 8, edit.Objects[sel].AbsoluteX);
        Assert.Equal(renders + 1, edit.Reconciles);
        w.MouseUp(At(c, w, obj.AbsoluteX + 8, obj.AbsoluteY), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(obj.AbsoluteX + 8, edit.Objects[sel].AbsoluteX);
    }

    /// <summary>Backspace is Delete: a Mac keyboard has no Delete key, and either should remove
    /// what is selected — level objects here, sprites and Map16 tiles through the same check.</summary>
    [AvaloniaTheory]
    [InlineData(PhysicalKey.Delete)]
    [InlineData(PhysicalKey.Backspace)]
    public void delete_removes_the_selection(PhysicalKey key)
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
        w.KeyPressQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before - selected, edit.Objects.Count);
        Assert.Empty(edit.Selection);
    }

    /// <summary>A middle-button drag pans the scroll viewer under the pointer, by exactly the drag,
    /// on every scrollable surface — here the level canvas, but the handler is the window's.</summary>
    [AvaloniaFact]
    public void middle_drag_pans_the_view()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var sv = w.GetControl<ScrollViewer>("CanvasScroll");
        Dispatcher.UIThread.RunJobs();
        sv.Offset = new Vector(200, 0);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(200, sv.Offset.X, 1);          // the level is wider than the viewport

        // A point in the viewport's middle: a cell address would have scrolled off under the drawer.
        var from = sv.TranslatePoint(new Point(sv.Bounds.Width / 2, sv.Bounds.Height / 2), w)!.Value;
        w.MouseDown(from, MouseButton.Middle);
        w.MouseMove(from + new Vector(-50, 0));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(250, sv.Offset.X, 1);          // dragging left shows what is to the right
        w.MouseUp(from + new Vector(-50, 0), MouseButton.Middle);
        w.MouseMove(from + new Vector(-90, 0));     // released: moving the mouse pans nothing
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(250, sv.Offset.X, 1);
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

        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, edit.Objects.Count);

        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
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

        // A VERTICAL level goes the other way round: the wheel is up/down, Shift sideways. The
        // canvas scrolls it itself (not the scroll viewer) so both orientations get the same rate.
        moves.Clear();
        c.Vertical = true;
        w.MouseWheel(at, new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(moves);
        Assert.Equal(0, moves[0].Dx);
        Assert.NotEqual(0, moves[0].Dy);
        moves.Clear();
        w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(0, moves[0].Dx);
        Assert.Equal(0, moves[0].Dy);
    }

    /// <summary>Ctrl+wheel reorders instead of scrolling: up takes the selected object one slot
    /// later in the stream — on top of what it passed — and down brings it back. One notch is one
    /// undo, and the selection follows the object to its new index. Ctrl only: Cmd+wheel is the
    /// zoom, on a Mac too.</summary>
    [AvaloniaFact]
    public void ctrl_wheel_steps_the_selection_through_the_stream()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var edit = EditOf(w);
        Assert.True(edit.Objects.Count >= 2);
        var first = edit.Objects[0];
        edit.Selection.Clear(); edit.Selection.Add(0);
        int depth = edit.UndoDepth;

        var moves = new List<(double Dx, double Dy)>();
        c.ScrollRequested += (_, d) => moves.Add(d);
        var at = At(c, w, 4, 4);

        w.MouseWheel(at, new Vector(0, 1), RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(moves);                                  // reordered, not scrolled
        Assert.Equal(first, edit.Objects[1]);
        Assert.Equal([1], edit.Selection);
        Assert.Equal(depth + 1, edit.UndoDepth);

        w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(first, edit.Objects[0]);
        Assert.Equal([0], edit.Selection);

        w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Control);   // already first: not an edit
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(depth + 2, edit.UndoDepth);

        Assert.True(edit.Undo());
        Assert.Equal(first, edit.Objects[1]);
    }

    /// <summary>Alt+wheel and Cmd+wheel over the level or the Map16 sheet zoom a slider step, about
    /// the cursor, and neither scroll nor reorder; the plain wheel over the level still scrolls.</summary>
    [AvaloniaFact]
    public void alt_or_cmd_wheel_zooms_the_level_and_map16_views()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var edit = EditOf(w);
        var slider = w.GetControl<Slider>("ZoomSlider");
        var moves = new List<(double Dx, double Dy)>();
        c.ScrollRequested += (_, d) => moves.Add(d);
        edit.Selection.Clear(); edit.Selection.Add(0);
        var first = edit.Objects[0];
        var at = At(c, w, 4, 4);

        double pct = slider.Value;
        w.MouseWheel(at, new Vector(0, 1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(pct + slider.TickFrequency, slider.Value, 1);
        Assert.Equal(slider.Value / 100, c.Zoom, 3);
        w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Meta);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(pct, slider.Value, 1);
        Assert.Empty(moves);                                  // zoomed, not scrolled...
        Assert.Equal(first, edit.Objects[0]);                 // ...and not reordered
        w.MouseWheel(at, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Single(moves);                                 // the plain wheel is still a scroll
        Assert.Equal(pct, slider.Value, 1);

        w.GetControl<Avalonia.Controls.Primitives.ToggleButton>("ModeMap16").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var m16 = w.GetControl<Map16CanvasView>("Map16Canvas");
        double m16Pct = slider.Value;
        var m16At = m16.TranslatePoint(new Point(20, 20), w)!.Value;
        w.MouseWheel(m16At, new Vector(0, 1), RawInputModifiers.Meta);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(m16Pct + slider.TickFrequency, slider.Value, 1);
        Assert.Equal(slider.Value / 100, m16.Zoom, 3);
    }

    /// <summary>The desk — the checkerboard beside a canvas — takes the wheel the way the canvas
    /// does. The level's wheel is a scroll, so its desk scrolls and zooms nothing; the GFX
    /// sheet's wheel is a zoom, so its desk zooms a slider step and keeps the point under the
    /// cursor where it was.</summary>
    [AvaloniaFact]
    public void the_desk_follows_its_canvas_scrolling_beside_the_level_and_zooming_beside_a_sheet()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var slider = w.GetControl<Slider>("ZoomSlider");
        var sv = w.GetControl<ScrollViewer>("CanvasScroll");
        double pct = slider.Value;

        // The level is centred in a taller viewport, so the strip along the bottom is desk.
        var desk = sv.TranslatePoint(new Point(sv.Bounds.Width / 2, sv.Bounds.Height - 4), w)!.Value;
        Assert.Null(c.CellAt(w.TranslatePoint(desk, c)!.Value));
        w.MouseWheel(desk, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(pct, slider.Value);
        Assert.Equal(pct / 100, c.Zoom);

        // The GFX sheet: at 15x it outgrows the viewport, so there is room for the anchor to
        // scroll into, and the desk under the sheet's left edge is where the wheel lands.
        w.GetControl<Avalonia.Controls.Primitives.ToggleButton>("ModeGfx").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        slider.Value = 1500;
        Dispatcher.UIThread.RunJobs();
        var sheet = w.GetControl<GfxCanvasView>("GfxCanvas");
        var gsv = w.GetControl<ScrollViewer>("GfxSheetScroll");
        Assert.Equal(15, sheet.Zoom);
        var local = new Point(-4, 30 * sheet.Zoom + 1);          // just left of the sheet, beside pixel row 30
        var at = sheet.TranslatePoint(local, w)!.Value;
        w.MouseWheel(at, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(16, sheet.Zoom);
        Assert.Equal(1600, slider.Value);
        double y = w.TranslatePoint(at, sheet)!.Value.Y;
        Assert.Equal(30, (int)(y / sheet.Zoom));
        Assert.True(gsv.Offset.Y > 0);
    }

    /// <summary>
    /// A palette edit, saved. It used to save fine and LOOK unsaved: the level counted as dirty
    /// for as long as it had any palette edit at all, hydrated ones included, so the title kept
    /// its marker after Ctrl+S and the save read as having done nothing. And on a Mac the key is
    /// Cmd, which arrives as Meta — taken alongside Ctrl on every platform now.
    /// </summary>
    [AvaloniaFact]
    public void a_palette_edit_saves_on_cmd_s_and_the_marker_clears()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, _) = o;
        string dir = Path.Combine(Path.GetTempPath(), "pdpal-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var session = SessionOf(w);
            Assert.True(session.NewProject(Path.Combine(dir, "p"), Prepped!), session.Status);
            Invoke(w, "AdoptSession");
            Dispatcher.UIThread.RunJobs();

            int index = 0x0A;                                    // under Layer3.PaletteSpace: a layer-3 colour
            ushort colour = (ushort)(session.PaletteBgr(index) ^ 0x1F);
            Assert.True(session.SetPaletteColor(index, colour));
            Invoke(w, "UpdateTitle");
            Assert.True(session.HasUnsavedWork);
            Assert.Contains("*", w.Title);

            w.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Meta);          // Cmd+S
            Dispatcher.UIThread.RunJobs();
            Assert.False(session.HasUnsavedWork, $"Cmd+S left work unsaved: {session.Status}");
            Assert.DoesNotContain("*", w.Title);

            // And the colour is really in the project, not just no longer flagged.
            var again = new EditorSession();
            Assert.True(again.OpenProject(Path.Combine(dir, "p", "project.pdp")), again.Status);
            again.ShowLevel(session.LevelNum);
            Assert.Equal(colour, again.PaletteBgr(index));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>The menu's shortcuts are HotKeys now, not captions: they fire from the keyboard
    /// with the menu closed, on the platform's command key, and the caption the menu shows is
    /// the gesture that works.</summary>
    [AvaloniaFact]
    public void menu_hotkeys_fire_from_the_keyboard()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        bool before = c.ShowGrid;

        w.KeyPressQwerty(PhysicalKey.G, command);
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(before, c.ShowGrid);
        w.KeyPressQwerty(PhysicalKey.G, command);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, c.ShowGrid);

        var item = w.GetControl<MenuItem>("ViewGridItem");
        Assert.NotNull(item.HotKey);
        Assert.NotNull(item.InputGesture);                     // the caption follows the HotKey
        Assert.Equal(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control, item.HotKey!.KeyModifiers);
    }

    /// <summary>
    /// Ctrl+S saves. The File menu draws "Ctrl+S" next to Save, but a MenuItem's InputGesture
    /// in Avalonia is DECORATION — it registers no gesture — so the key only works because
    /// OnWindowKeyDown handles it, and it silently did nothing until it did.
    /// </summary>
    [AvaloniaFact]
    public void ctrl_s_saves_the_project()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        string dir = Path.Combine(Path.GetTempPath(), "pdsave-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // Save needs a project to write to. A .pdp on the command line only opens through
            // OnFirstOpened, which headless runs deliberately never wire up, so the project is
            // made on the window's own session the way NewProjectFlow does it.
            var session = SessionOf(w);
            Assert.True(session.NewProject(Path.Combine(dir, "p"), Prepped!), session.Status);
            Invoke(w, "AdoptSession");
            Dispatcher.UIThread.RunJobs();

            int before = EditOf(w).Objects.Count;
            w.MouseDown(At(c, w, 2, 12), MouseButton.Right);      // stamp, so there is work to lose
            w.MouseMove(At(c, w, 7, 12));
            w.MouseUp(At(c, w, 7, 12), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.True(EditOf(w).Objects.Count > before);
            Assert.True(session.HasUnsavedWork);

            w.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.False(session.HasUnsavedWork, $"Ctrl+S left work unsaved: {session.Status}");
            Assert.Contains("saved", session.Status);
            Assert.DoesNotContain("*", w.Title);                  // and the title's marker cleared
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>The Tiles drawer lassoes like the Map16 editor: a dragged rectangle becomes the
    /// level's brush, row-major, and one right-click stamps the whole block. A single click is
    /// still a pick, and picking again drops the block.</summary>
    [AvaloniaFact]
    public void a_lasso_in_the_tiles_drawer_is_a_block_brush()
    {
        if (Open() is not { } o) return;
        var (w, c) = o;
        var edit = EditOf(w);
        var palette = w.GetControl<Map16PaletteView>("Palette");
        Point OnSheet(int col, int row) => palette.TranslatePoint(new Point(col * 16 * palette.Zoom + 4, row * 16 * palette.Zoom + 4), w)!.Value;

        // Row 16 is tile 0x100: drag tiles (1,16)-(2,17), a 2x2 block.
        w.MouseDown(OnSheet(1, 16), MouseButton.Left);
        w.MouseMove(OnSheet(2, 17));
        w.MouseUp(OnSheet(2, 17), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((1, 16, 2, 2), palette.Selection);
        Assert.Equal(0x101, palette.Selected);

        w.MouseDown(At(c, w, 30, 10), MouseButton.Right);
        w.MouseUp(At(c, w, 30, 10), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0x101, edit.Scene.Grid.Get(30, 10));
        Assert.Equal(0x102, edit.Scene.Grid.Get(31, 10));
        Assert.Equal(0x111, edit.Scene.Grid.Get(30, 11));
        Assert.Equal(0x112, edit.Scene.Grid.Get(31, 11));

        w.MouseDown(OnSheet(0, 16), MouseButton.Left);
        w.MouseUp(OnSheet(0, 16), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(palette.Selection);
        Assert.Equal(0x100, palette.Selected);
    }
}
