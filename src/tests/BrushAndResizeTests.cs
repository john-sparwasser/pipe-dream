using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The two features that were half-shipped: a grabbed multi-tile brush that could be captured
/// but not stamped, and object resizing.
///
/// Resizing is the intricate one — DM16 tiles carry their own size model (LM's extended
/// Form B, up to 128x256) while standard objects pack width and height into byte-3 nibbles
/// whose meaning is probed per tileset, and objects with one shared nibble (diagonal slopes)
/// resize on both axes at once.
/// </summary>
public class BrushAndResizeTests(ITestOutputHelper log)
{
    private static string RomPath => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    /// <summary>The shared prepped ROM (see PreppedRom): prep is expensive and a private copy
    /// per class raced once the tests became one assembly.</summary>
    private static string? Prepped => PreppedRom.Path;

    private static (Rom Rom, LevelScene Scene, LevelEdit Edit)? Edit(int level = 0x105)
    {
        if (Prepped is not { } path) return null;
        var rom = Rom.Load(path);
        var scene = LevelScene.Build(rom, level, LevelScene.SpriteDraw.Skip);
        var ed = new LevelEdit(rom, scene, scene.Level.Objects);
        ed.Rerender();                      // tracked render: selection and bounds need it
        return (rom, scene, ed);
    }

    // ---- multi-tile brush ----

    [Fact]
    public void a_grabbed_brush_stamps_every_one_of_its_tiles()
    {
        if (Edit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, scene, edit) = r;

        // Lay down a recognisable 3x2 patch, then grab it.
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++) edit.Paint(4 + x, 6 + y, 0x100 + x);
        edit.EndStroke();

        var (tiles, w, h) = edit.GrabTiles(4, 6, 3, 2);
        Assert.Equal(3, w);
        Assert.Equal(2, h);

        // Stamp it somewhere else and check every cell landed.
        Assert.True(edit.PaintBrush(20, 10, tiles, w, h));
        edit.EndStroke();
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
                Assert.Equal(0x100 + x, scene.Grid.Get(20 + x, 10 + y));
    }

    /// <summary>A brush with holes must stamp holes — the whole point of skipping Empty cells
    /// rather than filling the bounding box.</summary>
    [Fact]
    public void empty_cells_in_a_brush_are_not_stamped()
    {
        if (Edit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, scene, edit) = r;

        int untouched = scene.Grid.Get(21, 10);
        ushort[] brush = [0x100, Map16Grid.Empty, 0x100];
        Assert.True(edit.PaintBrush(20, 10, brush, 3, 1));
        edit.EndStroke();

        Assert.Equal(0x100, scene.Grid.Get(20, 10));
        Assert.Equal(untouched, scene.Grid.Get(21, 10));     // the hole stayed a hole
        Assert.Equal(0x100, scene.Grid.Get(22, 10));
    }

    [Fact]
    public void a_whole_brush_drag_is_still_one_undo()
    {
        if (Edit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, _, edit) = r;
        ushort[] brush = [0x100, 0x100, 0x100, 0x100];

        for (int x = 0; x < 6; x++) edit.PaintBrush(20 + x, 10, brush, 2, 2);
        edit.EndStroke();

        Assert.Equal(1, edit.UndoDepth);
    }

    // ---- resize ----

    /// <summary>A stamped tile is a DM16 object, and DM16 objects resize on both axes.</summary>
    [Fact]
    public void a_placed_tile_resizes_on_both_axes()
    {
        if (Edit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, scene, edit) = r;

        edit.Paint(20, 10, 0x100);
        edit.EndStroke();
        int idx = edit.Objects.Count - 1;
        Assert.True(edit.Objects[idx].IsDm16);

        // Drag the bottom-right corner (edges 2|8) three right and two down.
        var pv = edit.PreviewResize(idx, 2 | 8, 3, 2);
        Assert.NotNull(pv);
        Assert.Equal((20, 10, 4, 3), pv!.Value);

        Assert.True(edit.Resize(idx, 2 | 8, 3, 2));
        Assert.Equal(0x100, scene.Grid.Get(23, 12));         // the far corner really filled in
    }

    /// <summary>How a run of tiles re-lays when its box changes: one tile repeats; a pair grows
    /// by its leading edge; three or more keep both edges and fill from the tile just inside the
    /// dragged one, so a framed box stays a frame. Shrinking undoes the same interior first.</summary>
    [Fact]
    public void a_stretched_run_repeats_the_right_tile_for_its_length()
    {
        Assert.Equal([0, 0, 0], LevelEdit.StretchMap(1, 3, fromEnd: true));
        Assert.Equal([0, 1, 1, 1], LevelEdit.StretchMap(2, 4, fromEnd: true));      // right edge dragged
        Assert.Equal([0, 0, 0, 1], LevelEdit.StretchMap(2, 4, fromEnd: false));     // left edge dragged
        Assert.Equal([0], LevelEdit.StretchMap(2, 1, fromEnd: true));
        Assert.Equal([0, 1, 1, 1, 2], LevelEdit.StretchMap(3, 5, fromEnd: true));   // frame: inner tile fills
        Assert.Equal([0, 1, 1, 1, 2], LevelEdit.StretchMap(3, 5, fromEnd: false));
        Assert.Equal([0, 1, 2, 2, 2, 3], LevelEdit.StretchMap(4, 6, fromEnd: true));
        Assert.Equal([0, 1, 1, 1, 2, 3], LevelEdit.StretchMap(4, 6, fromEnd: false));
        Assert.Equal([0, 2], LevelEdit.StretchMap(3, 2, fromEnd: true));            // shrunk to its edges
        Assert.Equal([0, 1, 3], LevelEdit.StretchMap(4, 3, fromEnd: true));         // drops the interior at the dragged edge
        Assert.Equal([0, 2, 3], LevelEdit.StretchMap(4, 3, fromEnd: false));
        Assert.Equal([0, 1, 2], LevelEdit.StretchMap(3, 3, fromEnd: true));         // unchanged axis stays put
    }

    /// <summary>A block of Direct Map16 tiles selected together resizes as one piece: the tiles
    /// under the selection are re-laid to the new box, one undo entry for the drag, and the
    /// selection follows the rebuilt block.</summary>
    [Fact]
    public void a_selected_block_stretches_as_one_and_keeps_its_frame()
    {
        if (Edit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, scene, edit) = r;

        // A 3x3 frame of nine different tiles: every cell its own object.
        ushort[] frame = [0x100, 0x101, 0x102, 0x110, 0x111, 0x112, 0x120, 0x121, 0x122];
        Assert.True(edit.PaintBrush(20, 8, frame, 3, 3));
        edit.EndStroke();
        edit.SelectInRect(20, 8, 3, 3);
        Assert.Equal(9, edit.Selection.Count);
        Assert.Equal((true, true), edit.CanResizeSelection());
        Assert.Equal((20, 8, 3, 3), edit.SelectionBounds());
        int depth = edit.UndoDepth;

        // Drag the right edge two cells: the middle column fills, the right column stays right.
        Assert.True(edit.ResizeSelection(2, 2, 0));
        Assert.Equal((20, 8, 5, 3), edit.SelectionBounds());
        Assert.Equal(new[] { 0x100, 0x101, 0x101, 0x101, 0x102 },
                     Enumerable.Range(0, 5).Select(x => scene.Grid.Get(20 + x, 8)).ToArray());
        Assert.Equal(new[] { 0x120, 0x121, 0x121, 0x121, 0x122 },
                     Enumerable.Range(0, 5).Select(x => scene.Grid.Get(20 + x, 10)).ToArray());
        Assert.Equal(depth + 1, edit.UndoDepth);

        // A later step of the same drag measures from the press and joins the same entry.
        Assert.True(edit.ResizeSelection(2 | 8, 1, 1, coalesce: true));
        Assert.Equal((20, 8, 4, 4), edit.SelectionBounds());
        Assert.Equal(0x111, scene.Grid.Get(22, 10));          // the inner tile fills the new row too
        Assert.Equal(0x122, scene.Grid.Get(23, 11));          // the corner is still the corner
        Assert.Equal(depth + 1, edit.UndoDepth);
        edit.EndResize();

        Assert.True(edit.Undo());
        Assert.Equal(0x102, scene.Grid.Get(22, 8));           // back to the 3x3

        // A pair grows by its leading edge.
        edit.Selection.Clear();
        Assert.True(edit.PaintBrush(30, 8, [0x100, 0x101, 0x110, 0x111], 2, 2));
        edit.EndStroke();
        edit.SelectInRect(30, 8, 2, 2);
        Assert.True(edit.ResizeSelection(2, 2, 0));
        Assert.Equal(new[] { 0x100, 0x101, 0x101, 0x101 },
                     Enumerable.Range(0, 4).Select(x => scene.Grid.Get(30 + x, 8)).ToArray());
        edit.EndResize();
    }

    /// <summary>Dragging a LEFT or TOP edge moves the anchor as well as the size, or the
    /// object would grow the wrong way.</summary>
    [Fact]
    public void dragging_the_left_edge_moves_the_anchor()
    {
        if (Edit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, _, edit) = r;
        edit.Paint(20, 10, 0x100);
        edit.EndStroke();
        int idx = edit.Objects.Count - 1;

        var pv = edit.PreviewResize(idx, 1, -3, 0);          // left edge, three cells left
        Assert.NotNull(pv);
        Assert.Equal(17, pv!.Value.X);                       // anchor moved
        Assert.Equal(4, pv.Value.W);                         // and it got wider
    }

    /// <summary>The engine will happily write past the last visible row, bleeding into the
    /// next screen's RAM. A resize must clamp instead (LM parity).</summary>
    [Fact]
    public void a_resize_cannot_drag_past_the_bottom_of_the_level()
    {
        if (Edit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, _, edit) = r;
        edit.Paint(20, 24, 0x100);
        edit.EndStroke();
        int idx = edit.Objects.Count - 1;

        var pv = edit.PreviewResize(idx, 8, 0, 50);          // drag the bottom edge way down
        Assert.NotNull(pv);
        Assert.True(pv!.Value.Y + pv.Value.H <= 27,
                    $"resize reached row {pv.Value.Y + pv.Value.H}, past the visible 27");
    }

    [Fact]
    public void a_resize_is_undoable_as_one_step()
    {
        if (Edit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, scene, edit) = r;
        edit.Paint(20, 10, 0x100);
        edit.EndStroke();
        int idx = edit.Objects.Count - 1;
        int depth = edit.UndoDepth;

        Assert.True(edit.Resize(idx, 2 | 8, 3, 2));
        Assert.Equal(depth + 1, edit.UndoDepth);

        Assert.True(edit.Undo());
        Assert.Equal(0x100, scene.Grid.Get(20, 10));         // the tile itself survives
        Assert.NotEqual(0x100, scene.Grid.Get(23, 12));      // the growth is gone
    }

    /// <summary>A live resize drag is steps measured from where the drag began, all in one undo
    /// entry: the third step's width is the start plus that step's travel, not a pile of deltas;
    /// a step back to the start is a change (the screen shows the previous step) and the object
    /// comes back to its original size.</summary>
    [Fact]
    public void a_coalesced_resize_measures_from_the_drag_start_as_one_undo()
    {
        if (Edit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, scene, edit) = r;
        edit.Paint(20, 10, 0x100);
        edit.EndStroke();
        int idx = edit.Objects.Count - 1;
        int depth = edit.UndoDepth;

        Assert.True(edit.Resize(idx, 2, 1, 0));                       // the drag's first step
        Assert.True(edit.Resize(idx, 2, 3, 0, coalesce: true));       // cursor now 3 right of the press
        Assert.Equal(4, edit.Objects[idx].Dm16Size().w);              // 1 + 3, not 1 + 1 + 3
        Assert.Equal(0x100, scene.Grid.Get(23, 10));
        Assert.True(edit.Resize(idx, 2, 0, 0, coalesce: true));       // back to where it started
        Assert.Equal(1, edit.Objects[idx].Dm16Size().w);
        Assert.NotEqual(0x100, scene.Grid.Get(23, 10));
        Assert.False(edit.Resize(idx, 2, 0, 0, coalesce: true));      // and staying there is nothing
        Assert.Equal(depth + 1, edit.UndoDepth);
        edit.EndResize();
    }

    /// <summary>The drag preview box must sit on the rendered footprint — where the selection
    /// and handles are — not on the object's declared rect, which can differ in both anchor
    /// and size. A zero-delta drag on any object must reproduce its BBox exactly.</summary>
    [Fact]
    public void the_resize_preview_box_hugs_the_footprint()
    {
        if (Edit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, _, edit) = r;

        for (int i = 0; i < edit.Objects.Count; i++)
        {
            var (wOk, hOk) = edit.CanResize(i);
            if ((!wOk && !hOk) || edit.BBox(i) is not { } b) continue;
            Assert.Equal((b.X, b.Y, b.W, b.H), edit.PreviewResizeBox(i, 2 | 8, 0, 0));
        }
    }

    /// <summary>Extended objects and screen exits have no size to drag, so they must report
    /// no resizable axis rather than offering handles that do nothing.</summary>
    [Fact]
    public void objects_without_a_size_offer_no_handles()
    {
        if (Edit() is not { } r) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, _, edit) = r;
        var exit = LevelObject.ScreenExit(screen: 1, destination: 0x20, water: false, secondary: false);
        var rz = edit.ResizeInfo(exit);
        Assert.Equal(ObjectEngine.SizeSrc.None, rz.W);
        Assert.Equal(ObjectEngine.SizeSrc.None, rz.H);
    }
}
