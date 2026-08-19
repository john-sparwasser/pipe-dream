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

    [Fact]
    public void a_move_onto_unallocated_tiles_is_refused_rather_than_partial()
    {
        if (Edit() is not { } e) { log.WriteLine("SKIP: no ROM"); return; }
        var (_, edit) = e;
        Assert.Null(edit.EnsurePage(0x220));
        edit.StampQuad(0x220, 0, 0xCCCC);
        edit.EndStroke();

        // Somewhere far past what is allocated.
        Assert.NotNull(edit.MoveTiles(0, 0, 34, 1, 1, 0, 200));
        Assert.Equal(0xCCCC, edit.ReadDef(0x220)![0].Raw);            // nothing moved
    }

    // ---- through the window ----

    [AvaloniaFact]
    public void the_map16_mode_swaps_both_the_canvas_and_the_drawer()
    {
        if (Prepped is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(w.GetControl<ScrollViewer>("CanvasScroll").IsVisible);
        Assert.False(w.GetControl<ScrollViewer>("Map16Scroll").IsVisible);

        var mode = w.GetControl<Avalonia.Controls.Primitives.ToggleButton>("ModeMap16");
        mode.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // The canvas IS the editor: the Map16 view takes the region, and the drawer follows
        // it to 8x8 GFX rather than opening a second panel.
        Assert.False(w.GetControl<ScrollViewer>("CanvasScroll").IsVisible);
        Assert.True(w.GetControl<ScrollViewer>("Map16Scroll").IsVisible);
        Assert.False(w.GetControl<ScrollViewer>("PaletteScroll").IsVisible);
        Assert.True(w.GetControl<DockPanel>("ChrPanel").IsVisible);
        Assert.True(w.GetControl<Border>("Drawer").IsVisible);
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
}
