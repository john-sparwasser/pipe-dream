using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The overworld reader and the Overworld canvas mode: the map comes out of the ROM's own
/// tables the size the engine addresses it, Mario's front door is a level tile, the animated
/// tiles are filled in from GFX14, and the window shows it all on one canvas with the
/// overworld's graphics listed in the Graphics drawer.
/// </summary>
public class OverworldTests(ITestOutputHelper log)
{
    private static Overworld? Open() => PreppedRom.Path is { } p ? new Overworld(Rom.Load(p)) : null;

    [Fact]
    public void the_two_layers_decode_to_the_engines_sizes()
    {
        if (Open() is not { } ow) { log.WriteLine("SKIP: no ROM"); return; }
        Assert.Equal(0x800, ow.Layer1.Length);              // two 32x32 maps of 16x16 cells
        Assert.Equal(0x2000, ow.Layer2.Length);             // two 64x64 maps of 8x8 cells
        // The RLE streams end exactly on the buffer: a short or long decode means the format
        // (or its address) is wrong, and the land would be shifted or truncated.
        Assert.Contains(ow.Layer2, w => (w & 0xFF) != 0);
        Assert.Contains(ow.Layer2.Skip(0x1000), w => (w & 0xFF) != 0);   // the submap map has land too
    }

    /// <summary>The engine's index rules ($049885 for layer 1, the SNES 64x64 tilemap for layer 2):
    /// four screens each, top-left top-right bottom-left bottom-right.</summary>
    [Fact]
    public void cells_are_addressed_in_screen_order()
    {
        Assert.Equal(0x000, Overworld.Layer1Index(0, 0, false));
        Assert.Equal(0x10F, Overworld.Layer1Index(31, 0, false));       // top-right screen, last column
        Assert.Equal(0x2F0, Overworld.Layer1Index(0, 31, false));       // bottom-left screen, last row
        Assert.Equal(0x3FF, Overworld.Layer1Index(31, 31, false));
        Assert.Equal(0x400, Overworld.Layer1Index(0, 0, true));
        Assert.Equal(0x000, Overworld.Layer2Index(0, 0, false));
        Assert.Equal(0x41F, Overworld.Layer2Index(63, 0, false));       // top-right screen
        Assert.Equal(0xFFF, Overworld.Layer2Index(63, 63, false));
        Assert.Equal(0x1000, Overworld.Layer2Index(0, 0, true));
    }

    /// <summary>A new game puts Mario on Yoshi's Island at tile (6, 7) ($009EF0): a level tile
    /// has to be there, and the main map's first level tile is Yoshi's House's neighbour too.</summary>
    [Fact]
    public void marios_start_is_a_level_tile()
    {
        if (Open() is not { } ow) { log.WriteLine("SKIP: no ROM"); return; }
        int tile = ow.Layer1At(6, 7, submapMap: true);
        log.WriteLine($"tile under Mario's start: 0x{tile:X2}");
        Assert.InRange(tile, 0x56, 0x86);
        Assert.Equal(1, Overworld.SubmapAt(6, 7, true));                 // Yoshi's Island, top-left
        Assert.Equal(5, Overworld.SubmapAt(20, 15, true));               // Special World, right-middle
        Assert.Equal(0, Overworld.SubmapAt(20, 15, false));              // the main map is submap 0
    }

    /// <summary>The water the main map floats on is VRAM tiles 0x75-0x7F, which the game builds
    /// from GFX14 every frame: without that copy they draw as whatever the FG file has there.</summary>
    [Fact]
    public void animated_water_tiles_are_filled_from_gfx14()
    {
        if (Open() is not { } ow) { log.WriteLine("SKIP: no ROM"); return; }
        // Cell (0,0) of the main map is open sea in vanilla; every pixel is a water colour.
        var px = ow.CellPixels(0, 0, false);
        Assert.Equal(256, px.Length);
        Assert.DoesNotContain(0u, px);                      // layer 2 is opaque land or water
        Assert.True(px.Distinct().Count() <= 8, "sea uses a few colours, not a whole garbage tile");
        var sheet = ow.Map16Pixels(0x56, 0);                // a level tile has art
        Assert.Contains(sheet, c => c != 0);
    }

    /// <summary>The cliff caps at (5,5) on the main map are 8x8 tiles 0x50-0x55 of GFX1C. The level
    /// engine's tile animation overwrites those VRAM slots, and the overworld never runs it: a
    /// loader that applied it drew every cliff top as a black block.</summary>
    [Fact]
    public void cliff_tiles_are_not_blanked_by_the_level_animation_overlay()
    {
        if (Open() is not { } ow) { log.WriteLine("SKIP: no ROM"); return; }
        var px = ow.CellPixels(5, 5, false);
        // Outlines may use colour 0, but no 8x8 quadrant is colour 0 throughout.
        for (int q = 0; q < 4; q++)
        {
            int ox = (q & 1) * 8, oy = (q >> 1) * 8;
            Assert.Contains(Enumerable.Range(0, 64), i => px[(oy + i / 8) * 16 + ox + i % 8] != 0xFF000000u);
        }
    }

    [AvaloniaFact]
    public void the_overworld_mode_shows_the_whole_map_on_one_canvas()
    {
        if (PreppedRom.Path is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeOverworld").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(w.GetControl<DockPanel>("OverworldPane").IsVisible);
        Assert.False(w.GetControl<DockPanel>("LevelPane").IsVisible);
        // The Tiles tab is layer 2, the land, at its own grain: 8x8 cells, the main map over the
        // submap map, and a drawer of the 256 8x8 tiles the two FG files give it.
        var view = w.GetControl<TilemapView>("OwView");
        Assert.Equal((64, 128, 8), (view.Cols, view.Rows, view.CellPx));
        var sheet = w.GetControl<TilemapView>("OwSheet");
        Assert.Equal((16, 16, 8), (sheet.Cols, sheet.Rows, sheet.CellPx));
        Assert.True(w.GetControl<DockPanel>("OwToolPanel").IsVisible);
        Assert.True(w.GetControl<StackPanel>("OwBrushBar").IsVisible);

        // The five sub-modes are the drawer's tabs, as the Level drawer's are. The layer 1 tabs
        // show the map at layer 1's grain and its Map16 tiles.
        var tabs = w.GetControl<TabStrip>("OwTabs");
        Assert.Equal(5, tabs.ItemCount);
        tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("walks", w.GetControl<TextBlock>("OwNote").Text);
        Assert.Equal((32, 64, 16), (view.Cols, view.Rows, view.CellPx));
        Assert.Equal((Overworld.Map16Count + 15) / 16, sheet.Rows);
        Assert.False(w.GetControl<StackPanel>("OwBrushBar").IsVisible);
    }

    /// <summary>The layer 2 streams round-trip through the encoder, and the vanilla map re-packs
    /// into the room the ROM gives it — the writer refuses a map that would not.</summary>
    [Fact]
    public void layer_2_round_trips_and_the_vanilla_map_fits_its_own_space()
    {
        if (PreppedRom.Fork() is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        var rom = Rom.Load(p);
        var words = Overworld.DecodeLayer2(rom, out int lowEnd, out int highEnd);
        var (lo, hi) = Overworld.EncodeLayer2(words);
        log.WriteLine($"vanilla streams {rom.FileOffset(Overworld.Layer2High) - rom.FileOffset(Overworld.Layer2Low)}+{highEnd - rom.FileOffset(Overworld.Layer2High)} bytes; re-encoded {lo.Length}+{hi.Length}");
        // Decode what we encode: the exact inverse, plane by plane.
        var back = new ushort[words.Length];
        int o = 0, i = 0;
        while (o < back.Length) { int n = lo[i++]; if ((n & 0x80) == 0) for (int k = 0; k <= n; k++) back[o++] = lo[i++]; else { byte v = lo[i++]; for (int k = 0; k <= (n & 0x7F); k++) back[o++] = v; } }
        o = 0; i = 0;
        while (o < back.Length) { int n = hi[i++]; if ((n & 0x80) == 0) for (int k = 0; k <= n; k++) back[o++] |= (ushort)(hi[i++] << 8); else { byte v = hi[i++]; for (int k = 0; k <= (n & 0x7F); k++) back[o++] |= (ushort)(v << 8); } }
        Assert.Equal(words, back);

        // An edit that changes one word writes back and reads back through the ROM's own decoder.
        words[Overworld.Layer2Index(10, 10, false)] ^= 0x0001;
        Assert.Null(Overworld.WriteLayer2(rom, words));
        Assert.Equal(words, Overworld.DecodeLayer2(rom));
        // A map of all-different words cannot pack: the writer says so instead of overflowing.
        var noise = new ushort[words.Length];
        for (int k = 0; k < noise.Length; k++) noise[k] = (ushort)(k * 7919);
        Assert.Contains("room", Overworld.WriteLayer2(rom, noise));
    }

    /// <summary>Painting the Tiles tab writes layer 2 words with undo, lands them in the ROM's edited
    /// copy the map draws from, and stashes them in the project.</summary>
    [AvaloniaFact]
    public void right_click_paints_layer_2_and_the_edit_is_kept()
    {
        if (PreppedRom.Fork() is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeOverworld").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var session = (Services.EditorSession)typeof(MainWindow)
            .GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(w)!;
        var map = session.OwMap!;
        var view = w.GetControl<TilemapView>("OwView");
        var sheet = w.GetControl<TilemapView>("OwSheet");

        // Pick tile 0x23 (row 2, column 3) in the drawer, palette row 5 from the bar.
        var pick = sheet.TranslatePoint(new Point(3 * 8 * sheet.Zoom + 2, 2 * 8 * sheet.Zoom + 2), w)!.Value;
        w.MouseDown(pick, MouseButton.Left); w.MouseUp(pick, MouseButton.Left);
        w.GetControl<ComboBox>("OwPalRow").SelectedIndex = 5;
        Dispatcher.UIThread.RunJobs();

        int before = map.At(20, 20), depth = map.UndoDepth;
        var at = view.TranslatePoint(new Point(20 * 8 * view.Zoom + 2, 20 * 8 * view.Zoom + 2), w)!.Value;
        w.MouseDown(at, MouseButton.Right); w.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0x23 | 5 << 10, map.At(20, 20));
        // What the Tiles tab paints and moves is layer 2 alone: a cell under Yoshi's House (the
        // submap map's (6,7), 8x8 (12,14) of canvas row 64+) draws the land word, and the level
        // tile comes from the overlay, which a lasso never carries.
        int cell = (128 + 14) * Services.EditorSession.Ow8Cols + 12;
        Assert.Equal(session.Overworld!.TilePixels(map.At(12, 128 + 14), 1), session.Ow8CellPixels(cell));
        Assert.Contains(session.Ow8OverlayPixels(12, 128 + 14)!, c => c != 0);
        Assert.NotNull(view.OverlayPixels);
        w.GetControl<ToggleButton>("OwShowLayer1").IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.OverlayPixels);
        Assert.Equal(depth + 1, map.UndoDepth);
        Assert.Equal(0x23 | 5 << 10, session.Overworld!.Layer2[Overworld.Layer2Index(20, 20, false)]);   // the map draws from it
        Assert.Equal(map.At(20, 20), session.Rom!.OwLayer2![Overworld.Layer2Index(20, 20, false)]);   // and the ROM carries it
        Assert.True(map.Undo());
        Assert.Equal(before, map.At(20, 20));

        // A lasso across the sheet arms a block, as the level's Tiles drawer does: tiles (1,1)
        // to (2,2) stamp as a 2x2 in the bar's row, and the sheet keeps the lasso as its ring.
        var from = sheet.TranslatePoint(new Point(1 * 8 * sheet.Zoom + 2, 1 * 8 * sheet.Zoom + 2), w)!.Value;
        var to = sheet.TranslatePoint(new Point(2 * 8 * sheet.Zoom + 2, 2 * 8 * sheet.Zoom + 2), w)!.Value;
        w.MouseDown(from, MouseButton.Left); w.MouseMove(to); w.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((1, 1, 2, 2), sheet.Selection);
        Assert.Null(sheet.Selected);
        var at2 = view.TranslatePoint(new Point(30 * 8 * view.Zoom + 2, 30 * 8 * view.Zoom + 2), w)!.Value;
        w.MouseDown(at2, MouseButton.Right); w.MouseUp(at2, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0x11 | 5 << 10, map.At(30, 30));
        Assert.Equal(0x12 | 5 << 10, map.At(31, 30));
        Assert.Equal(0x21 | 5 << 10, map.At(30, 31));
        Assert.Equal(0x22 | 5 << 10, map.At(31, 31));
        Assert.Contains("2x2 block", w.GetControl<TextBlock>("OwNote").Text);

        // Dragging a lasso LIFTS the block: it floats where the lasso is, its old place shows the
        // fill, and nothing is written until the lasso is dropped elsewhere — then both land as
        // one undo entry. Passing a block over tiles must not eat them on the way.
        Point Cell(int cx, int cy) => view.TranslatePoint(new Point(cx * 8 * view.Zoom + 3, cy * 8 * view.Zoom + 3), w)!.Value;
        w.MouseDown(Cell(30, 30), MouseButton.Left); w.MouseMove(Cell(31, 31)); w.MouseUp(Cell(31, 31), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((30, 30, 2, 2), view.Selection);
        int fill = map.At(0, 0), under = map.At(40, 40), depth2 = map.UndoDepth;
        // Grab the block by its middle (its corners are resize grips) and carry it ten cells.
        Point Mid(int cx, int cy) => view.TranslatePoint(new Point((cx + 1) * 8 * view.Zoom, (cy + 1) * 8 * view.Zoom), w)!.Value;
        w.MouseDown(Mid(30, 30), MouseButton.Left); w.MouseMove(Mid(40, 40)); w.MouseUp(Mid(40, 40), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((40, 40, 2, 2), view.Selection);
        Assert.Equal(under, map.At(40, 40));                 // not written yet: it floats
        Assert.Equal(0x11 | 5 << 10, map.At(30, 30));
        Assert.Equal(depth2, map.UndoDepth);
        Assert.Equal(session.Overworld!.TilePixels(0x11 | 5 << 10, 0), view.CellPixels!(40 * 64 + 40));   // but it is drawn there
        Assert.Equal(session.Overworld!.TilePixels(fill, 0), view.CellPixels!(30 * 64 + 30));               // over a hole of fill

        // The drag preview's hole: fill where the block is being lifted from, but under a block
        // already floating it shows the map — nothing was written there, so a drag back must not
        // look as if it had cleared the spot.
        Assert.Equal(session.Overworld!.TilePixels(fill, 0), view.HolePixels!(30, 30));
        Assert.Equal(session.Overworld!.TilePixels(under, 0), view.HolePixels!(40, 40));

        // Carried on a second time: the first landing spot shows the map again, the float draws
        // at the new one, still nothing written. A zoom (a full recompose) changes none of that.
        w.MouseDown(Mid(40, 40), MouseButton.Left); w.MouseMove(Mid(50, 50)); w.MouseUp(Mid(50, 50), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((50, 50, 2, 2), view.Selection);
        Assert.Equal(under, map.At(40, 40));
        Assert.Equal(session.Overworld!.TilePixels(under, 0), view.CellPixels!(40 * 64 + 40));
        Assert.Equal(session.Overworld!.TilePixels(0x11 | 5 << 10, 0), view.CellPixels!(50 * 64 + 50));
        Assert.Equal(session.Overworld!.TilePixels(fill, 0), view.CellPixels!(30 * 64 + 30));
        w.GetControl<Slider>("ZoomSlider").Value -= 50;      // out, so every cell below stays inside the window
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(session.Overworld!.TilePixels(0x11 | 5 << 10, 0), view.CellPixels!(50 * 64 + 50));
        Assert.Equal(depth2, map.UndoDepth);

        w.MouseDown(Cell(10, 10), MouseButton.Left); w.MouseUp(Cell(10, 10), MouseButton.Left);   // a lasso elsewhere drops it
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0x11 | 5 << 10, map.At(50, 50));
        Assert.Equal(0x22 | 5 << 10, map.At(51, 51));
        Assert.Equal(under, map.At(40, 40));                 // the way station was never written
        Assert.Equal(fill, map.At(30, 30));
        Assert.Equal(depth2 + 1, map.UndoDepth);
        Assert.True(map.Undo());
        Assert.Equal(0x11 | 5 << 10, map.At(30, 30));
        Assert.True(map.Redo());

        // Painting with a float up lands the float, drops the lasso, and paints the DRAWER's brush
        // under the pointer — never a copy of the floating block, which is what a lasso used to
        // paste and what made the moved tiles seem to come back.
        w.MouseDown(Cell(50, 50), MouseButton.Left); w.MouseMove(Cell(51, 51)); w.MouseUp(Cell(51, 51), MouseButton.Left);
        w.MouseDown(Mid(50, 50), MouseButton.Left); w.MouseMove(Mid(20, 50)); w.MouseUp(Mid(20, 50), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((20, 50, 2, 2), view.Selection);
        Assert.Equal(0x11 | 5 << 10, map.At(50, 50));         // floating: still on the map at 50
        int was55 = map.At(5, 5);
        w.MouseDown(Cell(5, 5), MouseButton.Right); w.MouseUp(Cell(5, 5), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0x11 | 5 << 10, map.At(20, 50));         // landed where it floated
        Assert.Equal(fill, map.At(50, 50));                   // its hole is fill
        Assert.Equal(0x11 | 5 << 10, map.At(5, 5));           // the armed 2x2 sheet block, not a copy of the lasso
        Assert.Equal(0x22 | 5 << 10, map.At(6, 6));
        Assert.Null(view.Selection);
        Assert.NotEqual(was55, map.At(5, 5));
    }

    [AvaloniaFact]
    public void the_graphics_drawer_lists_the_overworlds_files()
    {
        if (PreppedRom.Path is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var texts = w.GetControl<StackPanel>("GfxBins").GetLogicalDescendants().OfType<TextBlock>()
                     .Select(t => t.Text).ToList();
        Assert.Contains("Overworld", texts);
        // Vanilla's overworld rows: FG GFX1C 1D 08 1E, sprites GFX10 0F 1C 1D — the group is
        // the eight bins after the heading, named as Lunar Magic's Submap GFX dialog names them.
        int at = texts.IndexOf("Overworld");
        var after = texts.Skip(at).ToList();
        foreach (string name in new[] { "[FG1]", "[FG2]", "[FG3]", "[FG4]", "[SP1]", "[SP2]", "[SP3]", "[SP4]" })
            Assert.Contains(name, after);
    }
}
