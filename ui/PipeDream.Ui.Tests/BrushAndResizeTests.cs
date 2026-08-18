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

    private static (Rom Rom, LevelScene Scene, LevelEdit Edit)? Edit(int level = 0x105)
    {
        if (Prepped.Value is not { } path) return null;
        var rom = Rom.Load(path);
        var scene = LevelScene.Build(rom, level, showSprites: false);
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
