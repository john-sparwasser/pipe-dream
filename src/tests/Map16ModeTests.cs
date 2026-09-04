using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PipeDream.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The Map16 canvas mode: editing tile definitions per 8x8 quadrant.
///
/// The trap this pins hardest is quadrant ORDER. The ROM stores a def as TL, BL, TR, BR while
/// the editor works in visual TL, TR, BL, BR, and mixing them mirrors every tile you draw —
/// which looks like a graphics bug, not an indexing one.
/// </summary>
public class Map16ModeTests(ITestOutputHelper log)
{
    private static string RomPath => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    /// <summary>The shared prepped ROM (see PreppedRom): prep is expensive and a private copy
    /// per class raced once the tests became one assembly.</summary>
    private static string? Prepped => PreppedRom.Path;

    private static (Rom Rom, Map16Edit Edit)? Edit()
    {
        if (Prepped is not { } p) return null;
        var rom = Rom.Load(p);
        return (rom, new Map16Edit(rom, tileset: 1, project: null));
    }

    /// <summary>Visual quadrant order must survive a write/read round trip. Four distinct
    /// values in, the same four back, in the same visual positions.</summary>
    [Fact]
    public void quadrants_round_trip_in_visual_order()
    {
        if (Edit() is not { } e) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, edit) = e;
        Assert.Null(edit.EnsurePage(0x200));

        for (int q = 0; q < 4; q++) Assert.True(edit.StampQuad(0x200, q, (ushort)(0x100 + q)));
        edit.EndStroke();

        var def = edit.ReadDef(0x200);
        Assert.NotNull(def);
        for (int q = 0; q < 4; q++) Assert.Equal(0x100 + q, def![q].Raw);
    }

    /// <summary>The visual order is TL, TR, BL, BR while the ROM's is TL, BL, TR, BR — so the
    /// second visual quadrant must land in the THIRD raw word, not the second.</summary>
    [Fact]
    public void visual_quadrant_1_is_the_top_right_not_the_bottom_left()
    {
        if (Edit() is not { } e) { log.WriteLine("SKIP: no ROM"); return; }
        var (rom, edit) = e;
        Assert.Null(edit.EnsurePage(0x201));

        edit.StampQuad(0x201, 1, 0xBEEF);          // visual TR
        edit.EndStroke();

        int fo = Map16.DefFileOffset(rom, 1, 0x201);
        ushort raw2 = (ushort)(rom.Data[fo + 4] | (rom.Data[fo + 5] << 8));   // third raw word = TR
        ushort raw1 = (ushort)(rom.Data[fo + 2] | (rom.Data[fo + 3] << 8));   // second raw word = BL
        Assert.Equal(0xBEEF, raw2);
        Assert.NotEqual(0xBEEF, raw1);
    }

    [Fact]
    public void a_stroke_is_one_undo_and_redo_replays_it()
    {
        if (Edit() is not { } e) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, edit) = e;
        Assert.Null(edit.EnsurePage(0x202));
        var before = edit.ReadDef(0x202)!.Select(w => w.Raw).ToArray();

        for (int q = 0; q < 4; q++) edit.StampQuad(0x202, q, (ushort)(0x200 + q));
        edit.EndStroke();
        Assert.Equal(1, edit.UndoDepth);

        Assert.True(edit.Undo());
        Assert.Equal(before, edit.ReadDef(0x202)!.Select(w => w.Raw).ToArray());

        Assert.True(edit.Redo());
        Assert.Equal(0x203, edit.ReadDef(0x202)![3].Raw);
    }

    /// <summary>A quadrant written twice in one stroke must undo to its ORIGINAL value, not to
    /// the intermediate one — that is why the undo walks the entries backward.</summary>
    [Fact]
    public void rewriting_a_quadrant_within_a_stroke_undoes_to_the_original()
    {
        if (Edit() is not { } e) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, edit) = e;
        Assert.Null(edit.EnsurePage(0x203));
        ushort original = edit.ReadDef(0x203)![0].Raw;

        edit.StampQuad(0x203, 0, 0x0111);
        edit.StampQuad(0x203, 0, 0x0222);
        edit.EndStroke();

        Assert.True(edit.Undo());
        Assert.Equal(original, edit.ReadDef(0x203)![0].Raw);
    }

    /// <summary>Painting an empty page CREATES it. Allocation is a consequence of editing,
    /// never a separate thing to ask for.</summary>
    [Fact]
    public void painting_an_unallocated_page_allocates_it()
    {
        if (Edit() is not { } e) { log.WriteLine("SKIP: no ROM"); return; }
        var (rom, edit) = e;
        int tile = 0x800;
        Assert.True(Map16.DefFileOffset(rom, 1, tile) < 0, "page 08 was already allocated");

        Assert.Null(edit.EnsurePage(tile));
        Assert.True(Map16.DefFileOffset(rom, 1, tile) > 0);
        Assert.True(edit.StampQuad(tile, 0, 0x1234));
        edit.EndStroke();
        Assert.Equal(0x1234, edit.ReadDef(tile)![0].Raw);
    }

    /// <summary>Painting past the allocated pages grows the ROM, and the picker has to grow with
    /// it: the scene's tile caches were sized at open, ComposeInto skips tiles past their end, and
    /// a page that was created by the stroke stayed black — which read as "cannot paint here".</summary>
    [Fact]
    public void painting_a_new_page_grows_the_scene_caches_and_the_sheet()
    {
        if (Prepped is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(p), s.Status);
        s.ShowLevel(0x105);
        var m16 = s.Map16!;
        int before = s.Map16TileCount;
        int tile = before + 0x105;                     // a page past everything allocated
        Assert.Null(m16.ReadDef(tile));

        Assert.Null(m16.EnsurePage(tile));
        Assert.True(m16.StampQuad(tile, 0, 0x1234));
        m16.EndStroke();
        s.RecomposeAfterMap16();

        Assert.True(s.Map16TileCount > before);
        var (px, _, h) = s.SheetPhases();
        Assert.Equal(s.Map16TileCount / 16 * 16, h);   // the sheet covers the new pages
        // Drawn like the sheet's own tiles: transparent pixels are the sheet grey, never see-through.
        Assert.All(s.PlaceholderPhases(), ph => { Assert.NotNull(ph); Assert.DoesNotContain(0u, ph!); });
    }

    /// <summary>A copied or moved tile keeps what it acts as, and undoing the copy takes the
    /// behaviour back with the art. Priority and the flips travel in the definition words.</summary>
    [Fact]
    public void a_copy_or_move_carries_the_tiles_behaviour_and_undoes_with_it()
    {
        if (Edit() is not { } e) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, edit) = e;
        Assert.True(edit.HasActsAs);
        Assert.Null(edit.EnsurePage(0x220));
        edit.StampQuad(0x220, 0, 0x2ABC);                            // priority set in the word
        edit.EndStroke();
        edit.SetActsAs([0x220], 0x12A);
        int was = edit.ActsAs(0x222)!.Value;
        Assert.NotEqual(0x12A, was);

        Assert.Null(edit.CopyQuads(0, 0, 68, 2, 2, 4, 0));           // tile 0x220 → 0x222, whole tile
        edit.EndStroke();
        Assert.Equal(0x2ABC, edit.ReadDef(0x222)![0].Raw);
        Assert.Equal(0x12A, edit.ActsAs(0x222));
        Assert.True(edit.Undo());
        Assert.Equal(was, edit.ActsAs(0x222));

        Assert.Null(edit.MoveTiles(0, 0, 34, 1, 1, 3, 0));           // tile 0x220 → 0x223
        edit.EndStroke();
        Assert.Equal(0x12A, edit.ActsAs(0x223));

        // A quadrant copy that cuts tiles carries art only.
        int keep = edit.ActsAs(0x230)!.Value;
        Assert.Null(edit.CopyQuads(0, 6, 68, 1, 1, 0, 2));            // one quadrant of 0x223 → 0x233
        edit.EndStroke();
        Assert.Equal(keep, edit.ActsAs(0x230));
    }

    /// <summary>The BG bank is a fixed table, so it explains itself rather than allocating.</summary>
    [Fact]
    public void the_bg_bank_is_not_allocatable()
    {
        if (Edit() is not { } e) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, edit) = e;
        Assert.NotNull(edit.EnsurePage(0x5000));       // past the FG banks entirely
    }

    /// <summary>Moving a lassoed rect clears the sources before writing the destination, so an
    /// overlapping move does not eat its own tail — and it is one undo.</summary>
    [Fact]
    public void moving_tiles_is_overlap_safe_and_one_undo()
    {
        if (Edit() is not { } e) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, edit) = e;
        for (int t = 0x210; t < 0x214; t++) Assert.Null(edit.EnsurePage(t));

        // Mark two adjacent tiles, then shift them one to the right — the destination of the
        // first is the source of the second.
        edit.StampQuad(0x210, 0, 0xAAAA);
        edit.StampQuad(0x211, 0, 0xBBBB);
        edit.EndStroke();

        Assert.Null(edit.MoveTiles(0, 0, 33, 2, 1, 1, 0));   // tiles 0x210..0x211 -> 0x211..0x212
        edit.EndStroke();

        Assert.Equal(0xAAAA, edit.ReadDef(0x211)![0].Raw);
        Assert.Equal(0xBBBB, edit.ReadDef(0x212)![0].Raw);
        Assert.Equal(Map16Edit.Empty, edit.ReadDef(0x210)![0].Raw);   // source cleared

        Assert.True(edit.Undo());
        Assert.Equal(0xAAAA, edit.ReadDef(0x210)![0].Raw);            // one step back
    }

    /// <summary>The right-click copy works in QUADRANTS, which is what lets one operation serve
    /// both grains — and unlike the drag-move it leaves the source where it is.</summary>
    [Fact]
    public void copying_quadrants_leaves_the_source_in_place()
    {
        if (Edit() is not { } e) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, edit) = e;
        for (int t = 0x230; t < 0x234; t++) Assert.Null(edit.EnsurePage(t));

        // Tile 0x230 sits at tile row 35, so quadrant row 70; stamp its visual TL and TR.
        edit.StampQuad(0x230, 0, 0xAAAA);
        edit.StampQuad(0x230, 1, 0xBBBB);
        edit.EndStroke();

        // Those two quadrants, two quadrant rows down — one whole tile: into 0x240's top row.
        Assert.Null(edit.CopyQuads(0, 0, 70, 2, 1, 0, 2));
        edit.EndStroke();

        Assert.Equal(0xAAAA, edit.ReadDef(0x240)![0].Raw);
        Assert.Equal(0xBBBB, edit.ReadDef(0x240)![1].Raw);
        Assert.Equal(0xAAAA, edit.ReadDef(0x230)![0].Raw);               // source kept
        Assert.Equal(0xBBBB, edit.ReadDef(0x230)![1].Raw);
    }

    /// <summary>Half a tile is a legal copy at the 8x8 grain: one quadrant across, which lands in
    /// its neighbour's TR without touching anything else in either tile.</summary>
    [Fact]
    public void a_quadrant_copy_can_land_inside_another_tile()
    {
        if (Edit() is not { } e) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, edit) = e;
        for (int t = 0x250; t < 0x252; t++) Assert.Null(edit.EnsurePage(t));
        edit.StampQuad(0x250, 0, 0xCCCC);                                // tile 0x250, visual TL
        edit.StampQuad(0x251, 1, 0x0000);
        edit.EndStroke();

        int qx = 0, qy = (0x250 / Map16Layout.Cols) * 2;                 // that quadrant, in quads
        Assert.Null(edit.CopyQuads(0, qx, qy, 1, 1, 3, 0));              // three quadrants right
        edit.EndStroke();

        Assert.Equal(0xCCCC, edit.ReadDef(0x251)![1].Raw);               // next tile's visual TR
        Assert.Equal(0xCCCC, edit.ReadDef(0x250)![0].Raw);               // source untouched
    }

    [Fact]
    public void a_move_or_copy_onto_an_empty_page_creates_it_like_painting_does()
    {
        if (Edit() is not { } e) { log.WriteLine("SKIP: no ROM"); return; }
        var (rom, edit) = e;
        Assert.Null(edit.EnsurePage(0x220));
        edit.StampQuad(0x220, 0, 0xCCCC);
        edit.EndStroke();

        // Somewhere far past what is allocated: the page comes into being and the tile lands.
        Assert.Null(edit.MoveTiles(0, 0, 34, 1, 1, 0, 200));
        Assert.Equal(0xCCCC, edit.ReadDef(0xEA0)![0].Raw);
        Assert.Equal(Map16Edit.Empty, edit.ReadDef(0x220)![0].Raw);
        Assert.True(rom.Map16TileCount > 0xEA0);
        Assert.Null(edit.CopyQuads(0, 0, 468, 2, 2, 0, 40));           // quadrants of tile 0xEA0, 20 rows down
        Assert.Equal(0xCCCC, edit.ReadDef(0xEA0 + 20 * 16)![0].Raw);

        // The BG table cannot grow, so a move into its unused rows is refused before anything moves.
        Assert.NotNull(edit.MoveTiles(2, 0, 0, 1, 1, 0, 40));
        Assert.Equal(0xCCCC, edit.ReadDef(0xEA0)![0].Raw);
    }

    // ---- through the window ----

    /// <summary>The editor has the drawer's Pages overlay as a toggle of its own, off until asked.</summary>
    [AvaloniaFact]
    public void the_pages_overlay_is_a_toggle_on_the_editor_bar_and_starts_off()
    {
        if (PreppedRom.Path is not { } path) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = path;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeMap16").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var canvas = w.GetControl<Map16CanvasView>("Map16Canvas");
        var pages = w.GetControl<ToggleButton>("M16Pages");
        Assert.False(canvas.ShowPages);
        Assert.NotEqual(true, pages.IsChecked);

        pages.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(canvas.ShowPages);
        pages.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(canvas.ShowPages);
    }

    [AvaloniaFact]
    public void the_map16_mode_swaps_both_the_canvas_and_the_drawer()
    {
        if (Prepped is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(w.GetControl<DockPanel>("LevelPane").IsVisible);
        Assert.False(w.GetControl<DockPanel>("Map16Pane").IsVisible);

        var mode = w.GetControl<Avalonia.Controls.Primitives.ToggleButton>("ModeMap16");
        mode.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // The canvas IS the editor: the Map16 view takes the region, and the drawer follows
        // it to 8x8 GFX rather than opening a second panel.
        Assert.False(w.GetControl<DockPanel>("LevelPane").IsVisible);
        Assert.True(w.GetControl<DockPanel>("Map16Pane").IsVisible);
        Assert.False(w.GetControl<DockPanel>("TilesPanel").IsVisible);
        Assert.True(w.GetControl<DockPanel>("ChrPanel").IsVisible);
        Assert.True(w.GetControl<Border>("Drawer").IsVisible);
        // ...and the tabs that choose what the drawer shows for the LEVEL go with it: every one
        // of them is inert in this mode, so leaving them up only invites a dead click.
        Assert.False(w.GetControl<TabStrip>("PaletteTabs").IsVisible);

        w.GetControl<Avalonia.Controls.Primitives.ToggleButton>("ModeLevel")
         .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.GetControl<TabStrip>("PaletteTabs").IsVisible);
    }

    /// <summary>Map16 mode wears the same skeleton as GFX: a header over the canvas and one over
    /// the drawer, both under the mode bar and both the same height, so the two read as one strip
    /// and switching modes does not move the furniture. The drawer's height comes from a binding
    /// to the canvas bar, and a binding that fails to resolve gives it zero rather than an error.
    /// </summary>
    [AvaloniaFact]
    public void the_map16_headers_line_up_with_each_other()
    {
        if (Prepped is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        w.GetControl<ToggleButton>("ModeMap16")
         .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        double canvasBar = w.GetControl<Border>("Map16EditorBar").Bounds.Height;
        double drawerBar = w.GetControl<Border>("Map16DrawerBar").Bounds.Height;
        log.WriteLine($"canvas bar {canvasBar:F0}px, drawer bar {drawerBar:F0}px");

        Assert.True(canvasBar > 0, "the Map16 canvas has no header");
        Assert.Equal(canvasBar, drawerBar, 1);
        // The controls that used to sit in the drawer are gone with them — the bank footer is
        // the level picker's, and it does not follow the drawer into this mode.
        Assert.False(w.GetControl<DockPanel>("TilesPanel").IsVisible);
    }

    /// <summary>The sheet is centred, so there is desk either side of it. Clicking that desk is a
    /// click on nothing, and a click on nothing drops the selection — the canvas never sees it,
    /// since it is only as wide as the tile column.</summary>
    [AvaloniaFact]
    public void clicking_the_desk_beside_the_sheet_clears_the_selection()
    {
        if (Prepped is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        w.GetControl<ToggleButton>("ModeMap16")
         .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var sheet = w.GetControl<Map16CanvasView>("Map16Canvas");
        var scroll = w.GetControl<ScrollViewer>("Map16Scroll");
        Point OnSheet(double x, double y) => sheet.TranslatePoint(new Point(x, y), w)!.Value;

        // Lasso two tiles — one alone is a PICK, not a selection.
        w.MouseDown(OnSheet(8, 8), MouseButton.Left);
        w.MouseMove(OnSheet(8 + 16 * sheet.Zoom, 8));
        w.MouseUp(OnSheet(8 + 16 * sheet.Zoom, 8), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(sheet.Selection);

        var desk = scroll.TranslatePoint(new Point(4, 40), w)!.Value;
        Assert.True(sheet.TranslatePoint(default, w)!.Value.X > desk.X, "no desk left of the sheet");
        w.MouseDown(desk, MouseButton.Left);
        w.MouseUp(desk, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(sheet.Selection);
    }

    /// <summary>The gutter zoom drives whichever canvas is showing. In Map16 mode that is the
    /// Map16 sheet — it used to drive the level canvas from behind the hidden scroll viewer, so
    /// the slider looked dead — and each mode keeps its own remembered percent.</summary>
    [AvaloniaFact]
    public void the_zoom_slider_drives_the_map16_sheet_in_map16_mode()
    {
        if (Prepped is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var slider = w.GetControl<Slider>("ZoomSlider");
        var sheet = w.GetControl<Map16CanvasView>("Map16Canvas");
        var level = w.GetControl<LevelView>("Canvas");

        Assert.Equal(100, slider.Value);           // the level opens at 1:1
        Assert.Equal(1.0, level.Zoom);

        slider.Value = 500;                        // level mode: the level moves, the sheet does not
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(5.0, level.Zoom);

        w.GetControl<ToggleButton>("ModeMap16")
         .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(300, slider.Value);           // the sheet's own percent, not the level's 500%
        Assert.Equal(3.0, sheet.Zoom);

        slider.Value = 400;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4.0, sheet.Zoom);
        Assert.Equal(5.0, level.Zoom);             // and the level is left where it was

        w.GetControl<ToggleButton>("ModeLevel")
         .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(500, slider.Value);
        Assert.Equal(5.0, level.Zoom);
    }

    /// <summary>
    /// A committed definition edit used to rebuild the whole scene — a quarter of a second before
    /// the stamped tile appeared. It now recomposes only the edited tile and the cells that use
    /// it, and the thing that has to hold is that the shortcut is INVISIBLE: the image it
    /// produces must be the one a full rebuild would have produced, to the pixel. Missing a cell
    /// leaves stale artwork on screen, which reads as "the edit did not take".
    /// </summary>
    [Fact]
    public void a_targeted_repaint_matches_what_a_full_rebuild_would_draw()
    {
        if (Prepped is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(p), s.Status);
        s.ShowLevel(0x105);
        var m16 = s.Map16!;

        // A tile the level actually uses.
        int tile = -1, uses = 0;
        for (int y = 0; y < s.Scene!.Grid.Height && tile < 0; y++)
            for (int x = 0; x < s.Scene.Grid.Width; x++)
            {
                int t = s.Scene.Grid.Get(x, y);
                if (t != Map16Grid.Empty && m16.ReadDef(t) is not null) { tile = t; break; }
            }
        Assert.True(tile >= 0, "no usable tile in the level");
        for (int y = 0; y < s.Scene.Grid.Height; y++)
            for (int x = 0; x < s.Scene.Grid.Width; x++)
                if (s.Scene.Grid.Get(x, y) == tile) uses++;
        log.WriteLine($"tile 0x{tile:X3} used by {uses} cells");
        Assert.True(uses > 1, "need a tile used more than once for this to prove anything");

        // Blank every quadrant: whatever the tile looked like, it does not look like that now.
        for (int q = 0; q < 4; q++) m16.StampQuad(tile, q, Map16Edit.Empty);
        m16.EndStroke();
        Assert.Equal(new[] { tile }, m16.CommittedTiles!.ToArray());

        s.RecomposeAfterMap16();                       // the fast path
        var targeted = (uint[])s.Phases[0]!.Clone();

        s.RecomposeScene();                            // the full rebuild, same ROM state
        var full = s.Phases[0]!;

        Assert.Equal(full.Length, targeted.Length);
        int differing = 0;
        for (int i = 0; i < full.Length; i++) if (full[i] != targeted[i]) differing++;
        Assert.True(differing == 0, $"{differing} pixels differ from a full rebuild");
    }

    /// <summary>An acts-like edit commits Map16 bytes but moves no pixel, and says so — the
    /// level does not need repainting for it at all.</summary>
    [Fact]
    public void an_acts_like_edit_reports_that_nothing_visual_changed()
    {
        if (Edit() is not { } e) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, edit) = e;
        if (!edit.HasActsAs) { log.WriteLine("SKIP: base has no acts-as table"); return; }

        Assert.True(edit.SetActsAs([0x100], 0x130));
        Assert.NotNull(edit.CommittedTiles);
        Assert.Empty(edit.CommittedTiles!);
    }

    /// <summary>A 16x16 lasso covers whole tiles however it was dragged, and right-click there —
    /// where the 8x8 brush is not in play — puts a copy of them under the cursor. The selection
    /// is kept in quadrants, so a tile-grain lasso is always an even rect on even boundaries.
    /// </summary>
    [AvaloniaFact]
    public void a_16x16_lasso_snaps_to_whole_tiles_and_right_click_copies_them()
    {
        var (w, v) = Bare(Map16CanvasView.TileGrain.Tile16);

        int painted = 0;
        (int X, int Y, int W, int H, int Dx, int Dy)? dup = null;
        v.QuadPainted += (_, _) => painted++;
        v.DuplicateRequested += (_, d) => dup = d;
        Point Quad(int col, int row) => v.TranslatePoint(new Point(col * 16 + 4, row * 16 + 4), w)!.Value;

        // Drag from the middle of tile (0,1) to the middle of tile (1,1) — quadrants 1,3 to 2,3.
        w.MouseDown(Quad(1, 3), MouseButton.Left);
        w.MouseMove(Quad(2, 3));
        w.MouseUp(Quad(2, 3), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((0, 2, 4, 2), v.Selection);         // grown out to both whole tiles

        w.MouseDown(Quad(8, 12), MouseButton.Right);     // tile (4,6)
        w.MouseUp(Quad(8, 12), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, painted);                        // the 8x8 brush is not in play here
        Assert.Equal((0, 2, 4, 2, 8, 10), dup);          // in QUADRANTS, top-left under the cursor
        Assert.Equal((8, 12, 4, 2), v.Selection);        // the reticle follows the copy

        // One tile is a selection too — it arms the level brush AND copies on right-click.
        int picked = -1;
        v.TilePicked += (_, t) => picked = t;
        w.MouseDown(Quad(3, 5), MouseButton.Left);       // anywhere inside tile (1,2)
        w.MouseUp(Quad(3, 5), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((2, 4, 2, 2), v.Selection);         // the whole tile, in quadrants
        Assert.Equal(0x21, picked);                      // row 2, column 1

        w.MouseDown(Quad(9, 13), MouseButton.Right);     // tile (4,6) again
        w.MouseUp(Quad(9, 13), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((2, 4, 2, 2, 6, 8), dup);           // snapped to the tile grid, not to 9,13
        Assert.Equal((8, 12, 2, 2), v.Selection);        // and the selection sits on the copy
    }

    /// <summary>8x8 is a different mode, not a finer cursor: the lasso selects QUADRANTS and a
    /// click selects one quadrant rather than arming a level brush. The right button goes to the
    /// 8x8 brush only while nothing is selected — a Map16 selection outranks it at either grain.
    /// </summary>
    [AvaloniaFact]
    public void an_8x8_lasso_selects_quadrants_and_outranks_the_brush()
    {
        var (w, v) = Bare(Map16CanvasView.TileGrain.Quad8);
        int painted = 0, picks = 0;
        (int X, int Y, int W, int H, int Dx, int Dy)? dup = null;
        v.QuadPainted += (_, _) => painted++;
        v.TilePicked += (_, _) => picks++;
        v.DuplicateRequested += (_, d) => dup = d;
        Point Quad(int col, int row) => v.TranslatePoint(new Point(col * 16 + 4, row * 16 + 4), w)!.Value;

        // Nothing selected yet: the brush has the right button.
        w.MouseDown(Quad(4, 4), MouseButton.Right);
        w.MouseUp(Quad(4, 4), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.True(painted > 0, "the 8x8 brush did not stamp with nothing selected");
        Assert.Null(dup);

        w.MouseDown(Quad(1, 3), MouseButton.Left);       // the same drag as the 16x16 case
        w.MouseMove(Quad(2, 3));
        w.MouseUp(Quad(2, 3), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((1, 3, 2, 1), v.Selection);         // exactly the two quadrants dragged
        // Two quadrants straddling the tile boundary: the header acts on both tiles they touch.
        Assert.Equal([0x10, 0x11], v.SelectedTiles().ToArray());

        w.MouseDown(Quad(0, 0), MouseButton.Left);       // one quadrant is a selection, not a pick
        w.MouseUp(Quad(0, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((0, 0, 1, 1), v.Selection);
        Assert.Equal(0, picks);                          // a quadrant is not a level brush

        // ...and now the selection takes the button: a copy, unsnapped, at quadrant precision.
        painted = 0;
        w.MouseDown(Quad(5, 7), MouseButton.Right);
        w.MouseUp(Quad(5, 7), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, painted);
        Assert.Equal((0, 0, 1, 1, 5, 7), dup);
    }

    /// <summary>A selection is something you can pick up, and the pointer says so: the hand over
    /// it, the grab while it is held, and neither anywhere else.</summary>
    [AvaloniaFact]
    public void the_hand_shows_over_a_selection_and_the_grab_while_dragging_it()
    {
        var (w, v) = Bare(Map16CanvasView.TileGrain.Tile16);
        Point Quad(int col, int row) => v.TranslatePoint(new Point(col * 16 + 4, row * 16 + 4), w)!.Value;

        w.MouseMove(Quad(1, 3));
        Assert.Equal(Cursor.Default, v.Cursor);
        w.MouseDown(Quad(1, 3), MouseButton.Left);
        w.MouseUp(Quad(1, 3), MouseButton.Left);
        Assert.Equal((0, 2, 2, 2), v.Selection);
        Assert.Same(UiCursors.Hand, v.Cursor);

        w.MouseDown(Quad(1, 3), MouseButton.Left);
        w.MouseMove(Quad(5, 3));
        Assert.Same(UiCursors.Grab, v.Cursor);
        w.MouseUp(Quad(5, 3), MouseButton.Left);
        Assert.Equal((4, 2, 2, 2), v.Selection);            // moved two tiles right
        Assert.Same(UiCursors.Hand, v.Cursor);                // released over it, still holdable

        w.MouseMove(Quad(12, 12));
        Assert.Equal(Cursor.Default, v.Cursor);

        // Two tiles wide, grabbed by its right tile and dragged so that tile reaches the left
        // edge: the selection stops AT the edge rather than hanging off it.
        w.MouseDown(Quad(8, 3), MouseButton.Left);
        w.MouseMove(Quad(10, 3));
        w.MouseUp(Quad(10, 3), MouseButton.Left);
        Assert.Equal((8, 2, 4, 2), v.Selection);
        w.MouseDown(Quad(11, 3), MouseButton.Left);
        w.MouseMove(Quad(0, 3));
        w.MouseUp(Quad(0, 3), MouseButton.Left);
        Assert.Equal((0, 2, 4, 2), v.Selection);
    }

    /// <summary>A bare canvas in a window — geometry and input, no ROM. Zoom 2: a tile is 32px,
    /// a quadrant 16.</summary>
    private static (Window W, Map16CanvasView V) Bare(Map16CanvasView.TileGrain grain)
    {
        var v = new Map16CanvasView { Zoom = 2, Grain = grain };
        var w = new Window { Width = 400, Height = 400, Content = v };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (w, v);
    }

    /// <summary>Clicking off the sheet deselects everything, and the header says so: placeholder
    /// text, and the whole field row disabled so it greys. Leaving live-looking values there
    /// invites an edit that has nothing to land on.</summary>
    [AvaloniaFact]
    public void nothing_selected_greys_and_blanks_the_header_fields()
    {
        if (Prepped is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        w.GetControl<ToggleButton>("ModeMap16")
         .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var sheet = w.GetControl<Map16CanvasView>("Map16Canvas");
        var chr = w.GetControl<ChrPaletteView>("Chr");
        var label = w.GetControl<TextBlock>("M16SelLabel");
        var fields = w.GetControl<StackPanel>("M16Fields");
        Assert.True(fields.IsEnabled);                     // a tile is armed on the way in
        Assert.NotNull(sheet.SelectedTile);

        // Take a 2x2 brush in the drawer as well, so there is one of each to drop — and so the
        // cursor on the canvas is carrying a footprint that has to go with it.
        double cell = chr.Zoom * 8;
        Point At8(int col, int row) => chr.TranslatePoint(new Point(col * cell + 2, row * cell + 2), w)!.Value;
        w.MouseDown(At8(0, 0), MouseButton.Left);
        w.MouseMove(At8(1, 1));
        w.MouseUp(At8(1, 1), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(chr.HasSelection);
        Assert.Equal((0, 0, 2, 2), chr.Brush);
        Assert.Equal(2, sheet.BrushW);
        Assert.Equal(Map16CanvasView.TileGrain.Quad8, sheet.Grain);

        var desk = w.GetControl<ScrollViewer>("Map16Scroll").TranslatePoint(new Point(4, 40), w)!.Value;
        w.MouseDown(desk, MouseButton.Left);
        w.MouseUp(desk, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(sheet.SelectedTile);
        Assert.Empty(sheet.SelectedTiles());
        Assert.False(chr.HasSelection);                    // the 8x8 pick goes too...
        Assert.Equal((0, 0, 1, 1), chr.Brush);             // ...footprint and all
        Assert.Equal(1, sheet.BrushW);
        Assert.Equal(1, sheet.BrushH);
        Assert.Equal("Tile ######", label.Text);
        Assert.Equal("-", w.GetControl<TextBox>("M16Acts").Text);
        Assert.False(label.IsEnabled);
        Assert.False(fields.IsEnabled);                    // greys the box, toggles and flips
        Assert.True(fields.IsVisible, "the row must stay put, not vanish and shrink the bar");
    }

    /// <summary>Editing a property must not move the selection off the tile being edited. The
    /// commit refreshes the sheet, and the refresh used to re-adopt the LEVEL picker's tile — so
    /// a priority toggle deselected its own tile, and the next toggle landed somewhere else.
    /// </summary>
    [AvaloniaFact]
    public void a_property_change_keeps_the_tile_selected()
    {
        if (Prepped is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        w.GetControl<ToggleButton>("ModeMap16")
         .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var sheet = w.GetControl<Map16CanvasView>("Map16Canvas");
        double ts = 16 * sheet.Zoom;
        var at = sheet.TranslatePoint(new Point(3 * ts + 8, 2 * ts + 8), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0x23, sheet.SelectedTile);            // row 2, column 3

        var prio = w.GetControl<ToggleButton>("M16Priority");
        prio.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0x23, sheet.SelectedTile);
        Assert.Contains("0023", w.GetControl<TextBlock>("M16SelLabel").Text);
        Assert.True(prio.IsChecked, "the toggle was reset by the refresh it triggered");
    }

    /// <summary>The Map16 palette lives in the gutter, where the GFX one does, and shows the row
    /// the selected tile draws with. Its swatches are inert: a Map16 tile chooses a ROW, so a
    /// click that moved a selection ring would promise an edit this mode cannot make.</summary>
    [AvaloniaFact]
    public void the_map16_palette_sits_in_the_gutter_and_only_shows()
    {
        if (Prepped is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var bar = w.GetControl<Border>("M16PaletteBar");
        Assert.False(bar.IsVisible);                       // level mode: the GFX gutter's slot

        w.GetControl<ToggleButton>("ModeMap16")
         .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var swatches = w.GetControl<PaletteGridView>("M16Colors");
        Assert.True(bar.IsVisible);
        Assert.False(w.GetControl<Border>("GfxPaletteBar").IsVisible);
        Assert.True(w.GetControl<ComboBox>("M16Palette").SelectedIndex >= 0, "no row shown");
        Assert.Equal(16, swatches.Colors.Length);
        Assert.NotEqual(0u, swatches.Colors[1]);           // a real row, not a blank strip

        var at = swatches.TranslatePoint(new Point(30, 8), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(-1, swatches.Selected);               // the click went nowhere, as intended
    }

    /// <summary>Every canvas mode's header is the same strip: switching modes must not move the
    /// canvas below it. The heights come from the controls inside, so this is really a check that
    /// no mode's bar has a control taller than the 28px toolbar row.</summary>
    [AvaloniaFact]
    public void every_mode_header_is_the_same_height()
    {
        if (Prepped is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        void Mode(string name) => w.GetControl<ToggleButton>(name)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Mode("ModeMap16");
        Dispatcher.UIThread.RunJobs();
        double map16 = w.GetControl<Border>("Map16EditorBar").Bounds.Height;

        Mode("ModeGfx");
        Dispatcher.UIThread.RunJobs();
        double gfx = w.GetControl<Border>("GfxEditorBar").Bounds.Height;

        // When this fails, the tallest control in the offending bar is the answer:
        //   bar.GetVisualDescendants().OfType<Control>().Max(c => c.Bounds.Height)
        // Caveat: with UseHeadlessDrawing the text metrics are synthetic and SHORTER than the
        // real ones, so a bar made tall by a text control can read level here and not on screen.
        // Measuring that needs .UseSkia() in HeadlessSetup — worth doing by hand, once, when the
        // eye says one bar is taller and this says otherwise.
        log.WriteLine($"map16 bar {map16:F1}px, gfx bar {gfx:F1}px");
        Assert.Equal(gfx, map16, 1);
    }

    /// <summary>The two grains. At 16x16 the 8x8 brush is not in play at all — right-click stamps
    /// nothing — and picking in the 8x8 drawer is itself the switch to 8x8, since that pick has no
    /// meaning at the other grain.</summary>
    [AvaloniaFact]
    public void the_grain_decides_whether_the_8x8_brush_is_in_play()
    {
        if (Prepped is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        w.GetControl<ToggleButton>("ModeMap16")
         .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var sheet = w.GetControl<Map16CanvasView>("Map16Canvas");
        var chr = w.GetControl<ChrPaletteView>("Chr");
        Assert.Equal(Map16CanvasView.TileGrain.Tile16, sheet.Grain);     // opens on whole tiles

        int painted = 0;
        sheet.QuadPainted += (_, _) => painted++;
        var at = sheet.TranslatePoint(new Point(8, 8), w)!.Value;
        w.MouseDown(at, MouseButton.Right);
        w.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, painted);

        // Picking an 8x8 tile in the drawer switches the canvas over on its own.
        var pick = chr.TranslatePoint(new Point(4, 4), w)!.Value;
        w.MouseDown(pick, MouseButton.Left);
        w.MouseUp(pick, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Map16CanvasView.TileGrain.Quad8, sheet.Grain);
        Assert.True(w.GetControl<ToggleButton>("Grain8").IsChecked);
        Assert.False(w.GetControl<ToggleButton>("Grain16").IsChecked);

        w.MouseDown(at, MouseButton.Right);
        w.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.True(painted > 0, "the 8x8 brush still did not stamp at the 8x8 grain");
    }

    [AvaloniaFact]
    public void the_map16_canvas_maps_a_point_to_the_right_tile_and_quadrant()
    {
        var v = new Map16CanvasView { Zoom = 2, Bank = 0 };
        // Tile (2,1) is 16*2=32 wide cells; its bottom-right quadrant starts 8*2=16px in.
        Assert.Equal((2, 1, 1 * 16 + 2, 3), v.At(new Point(2 * 32 + 20, 1 * 32 + 20)));
        // Bank 1 offsets every tile by 0x2000.
        v.Bank = 1;
        Assert.Equal(0x2000, v.At(new Point(4, 4))!.Value.Tile);
    }

    /// <summary>Files 60-63 land in a RATS block with their $03BCC0 pointer; replacing one frees the
    /// old block; LmAltExGfx reads it back.</summary>
    [Fact]
    public void alt_exgfx_install_and_replace()
    {
        if (Prepped is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        var rom = Rom.Load(p);
        Assert.Equal(-1, rom.LmAltExGfx(0));
        var g1 = Enumerable.Range(0, 0x1000).Select(i => (byte)i).ToArray();
        rom.SetLmAltExGfx(0, g1);
        int a1 = rom.LmAltExGfx(0);
        Assert.True(a1 > 0);
        Assert.Equal(g1, rom.Data.AsSpan(rom.FileOffset(a1), g1.Length).ToArray());
        Assert.Equal("STAR", System.Text.Encoding.ASCII.GetString(rom.Data, rom.FileOffset(a1) - 8, 4));

        rom.SetLmAltExGfx(0, new byte[0x2000]);
        int a2 = rom.LmAltExGfx(0);
        Assert.Equal(a1, a2);                                  // old block released, run reused
        Assert.Equal(0x1FFF, rom.Data[rom.FileOffset(a2) - 4] | rom.Data[rom.FileOffset(a2) - 3] << 8);
        Assert.Equal(0, rom.Data[rom.FileOffset(a2) + 0x10]); // g1's 0x10 is gone
        Assert.Equal(-1, rom.LmAltExGfx(1));
    }

    /// <summary>Delete on a selection: every tile goes back to the base ROM's definition (one
    /// undo entry), and a tile on a page the base does not have resets to LM's empty word.</summary>
    [Fact]
    public void reset_puts_tiles_back_to_the_base_definition_and_is_one_undo_entry()
    {
        if (Edit() is not { } e) { log.WriteLine("SKIP: no ROM"); return; }
        var (rom, edit) = e;
        var baseRom = Rom.Load(Prepped!);
        var was130 = edit.ReadDef(0x130)!.Select(w => w.Raw).ToArray();
        var was131 = edit.ReadDef(0x131)!.Select(w => w.Raw).ToArray();

        for (int q = 0; q < 4; q++) { edit.StampQuad(0x130, q, 0x2222); edit.StampQuad(0x131, q, 0x3333); }
        edit.EndStroke();
        Assert.Null(edit.EnsurePage(0x200));                    // a page the BASE does not have
        for (int q = 0; q < 4; q++) edit.StampQuad(0x200, q, 0x4444);
        edit.EndStroke();

        edit.Reset([0x130, 0x131, 0x200], baseRom);
        Assert.Equal(was130, edit.ReadDef(0x130)!.Select(w => w.Raw).ToArray());
        Assert.Equal(was131, edit.ReadDef(0x131)!.Select(w => w.Raw).ToArray());
        Assert.Equal(Enumerable.Repeat(Map16Edit.Empty, 4), edit.ReadDef(0x200)!.Select(w => w.Raw));

        Assert.True(edit.Undo());                               // ONE entry takes the reset back
        Assert.Equal(Enumerable.Repeat((ushort)0x2222, 4), edit.ReadDef(0x130)!.Select(w => w.Raw));
        Assert.Equal(Enumerable.Repeat((ushort)0x4444, 4), edit.ReadDef(0x200)!.Select(w => w.Raw));
    }
}
