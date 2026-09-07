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

    /// <summary>What a layer 1 tile does underfoot, from the engine's own tables: the pose byte at
    /// $049FEB (bit 3 swims, bit 4 climbs) and the exit-tile list at $049426 — as Lunar Magic
    /// colours them.</summary>
    [Fact]
    public void path_tiles_are_classified_from_the_engines_tables()
    {
        if (Open() is not { } ow) { log.WriteLine("SKIP: no ROM"); return; }
        Assert.Equal(Overworld.PathKind.None, ow.KindOf(0x00));
        Assert.Equal(Overworld.PathKind.Walk, ow.KindOf(0x01));       // front-facing walk, still a walk
        Assert.Equal(Overworld.PathKind.Walk, ow.KindOf(0x08));
        Assert.Equal(Overworld.PathKind.Swim, ow.KindOf(0x28));
        Assert.Equal(Overworld.PathKind.Swim, ow.KindOf(0x50));
        Assert.Equal(Overworld.PathKind.Climb, ow.KindOf(0x3F));      // the ladder tiles are 3F-41
        Assert.Equal(Overworld.PathKind.Exit, ow.KindOf(0x40));       // the ladder that leaves the map: exit wins
        Assert.Equal(Overworld.PathKind.Exit, ow.KindOf(0x44));
        Assert.Equal(Overworld.PathKind.Level, ow.KindOf(0x56));
        Assert.Equal(Overworld.PathKind.Level, ow.KindOf(0x82));
        Assert.Equal(Overworld.PathKind.Stop, ow.KindOf(0x83));
        Assert.Equal(Overworld.PathKind.None, ow.KindOf(0x87));
    }

    /// <summary>Lunar Magic's path pictures ship with the editor: a straight walk is green, an exit
    /// red, a ladder carries black rungs; level tiles and the tiles LM only tints have none.</summary>
    [Fact]
    public void path_pictures_are_lunar_magics_own()
    {
        var walk = Overworld.PathGlyph(0x01);
        Assert.NotNull(walk);
        Assert.Contains(walk!, p => p == 0xFF00FF00);                  // green, RGBA little-endian
        Assert.Contains(Overworld.PathGlyph(0x25)!, p => p == 0xFF0000FF);   // red
        Assert.Contains(Overworld.PathGlyph(0x3F)!, p => p == 0xFF000000);   // rungs
        Assert.Contains(Overworld.PathGlyph(0x28)!, p => p == 0xFFFF0000);   // blue swim
        Assert.Null(Overworld.PathGlyph(0x00));
        Assert.Contains(Overworld.PathGlyph(0x56)!, p => p == 0xFF00FF00);   // a level tile's green octagon
        Assert.Contains(Overworld.PathGlyph(0x6A)!, p => p == 0xFFFF0000);   // and the blue one
        Assert.Contains(Overworld.PathGlyph(0x4D)!, p => p == 0xFF0000FF);   // the exit LM only tinted with future tiles on
        Assert.Null(Overworld.PathGlyph(0x52));                        // unused; LM draws nothing opaque
    }

    /// <summary>Vanilla numbers level tiles by scanning layer 1 ($04D7F2); Yoshi's House, under
    /// Mario's start, is translevel 0x28 and so level 104 ($05D8A2).</summary>
    [Fact]
    public void level_numbers_follow_the_translevel_table()
    {
        if (Open() is not { } ow) { log.WriteLine("SKIP: no ROM"); return; }
        Assert.Equal(0x104, Overworld.LevelOf(ow.TranslevelAt(6, 7, submapMap: true)));
        Assert.Equal(0x24, Overworld.LevelOf(0x24));
        Assert.Equal(0x101, Overworld.LevelOf(0x25));
        Assert.Equal(0, Overworld.LevelOf(0));
        Assert.Contains(Enumerable.Range(1, 0x5F), tl => ow.BaseEventOf(tl) >= 0);
        Assert.Equal(-1, ow.BaseEventOf(0));                          // $05D608[0] is $FF
    }

    /// <summary>Layer 2 event steps decode to cells of the land: passing Yoshi's Island 1 (event 1)
    /// lays 2x2 pieces up the path from (6,15) on the submap map, as the game does.</summary>
    [Fact]
    public void event_steps_decode_to_land_cells()
    {
        if (Open() is not { } ow) { log.WriteLine("SKIP: no ROM"); return; }
        Assert.Equal(371, ow.EventSteps.Count);
        var first = ow.EventSteps[0];
        Assert.Equal((1, 0x900, 6, 15, true, 2), (first.Event, first.Piece, first.Cx, first.Cy, first.SubmapMap, first.Size));
        Assert.Contains(ow.EventSteps, s => s.Size == 6);
        Assert.All(ow.EventSteps, s => { Assert.InRange(s.Cx, 0, 63); Assert.InRange(s.Cy, 0, 63); });
    }

    /// <summary>The warp tables read in the engine's units and pair up: vanilla's pipes link both
    /// ways, so destinations resolve to the entry that comes back.</summary>
    [Fact]
    public void warps_and_exit_paths_link_to_their_destinations()
    {
        if (Open() is not { } ow) { log.WriteLine("SKIP: no ROM"); return; }
        Assert.Equal(27, ow.WarpCount);
        foreach (var w in ow.Warps) log.WriteLine(w.ToString());
        Assert.NotEmpty(ow.Warps);
        Assert.Contains(ow.Warps, w => w.DestIndex >= 0);
        Assert.All(ow.Warps, w => { Assert.InRange(w.X, 0, 31); Assert.InRange(w.Y, 0, 31); Assert.InRange(w.Submap, 0, 6); });
        foreach (var e in ow.ExitPaths) log.WriteLine(e.ToString());
        Assert.NotEmpty(ow.ExitPaths);
        Assert.Contains(ow.ExitPaths, e => e.DestIndex >= 0);
        Assert.Equal(3, ow.KoopaTeleports.Count);
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

    /// <summary>The overworld's castles, level stars and signs come from GFX1E, which the game's
    /// uploader gives a fourth plane (the OR of the other three): every drawn pixel lands in
    /// colours 8-F of its row. Read as a plain 3bpp file, the Star World's stars came out red
    /// (row 6 colour 5) where the game and Lunar Magic paint them yellow (colour D).</summary>
    [Fact]
    public void gfx1e_and_gfx08_pixels_land_in_colours_8_to_f_on_the_overworld()
    {
        if (Open() is not { } ow) { log.WriteLine("SKIP: no ROM"); return; }
        var fg = Gfx.FgTiles.Load(ow.Rom, Overworld.Tileset, levelAnimation: false);
        Assert.All(fg.Fetch(0x1C8).Where(p => p != 0), p => Assert.True(p >= 8));   // a star quarter: GFX1E tile 0x48
        Assert.All(fg.Fetch(0x100).Where(p => p != 0), p => Assert.True(p >= 8));   // GFX08 on the overworld
        Assert.Contains(fg.Fetch(0x000), p => p is > 0 and < 8);                    // GFX1C is left alone
        var level = Gfx.FgTiles.Load(ow.Rom, 0, levelAnimation: false);
        Assert.Contains(level.Fetch(0x100), p => p is > 0 and < 8);                 // a level's third file is not filtered
        var pal6 = ow.PaletteOf(6).Rgba; var star = ow.Map16Pixels(0x5F, 6);
        Assert.Contains(pal6[6 * 16 + 0xD], star);                                  // the yellow the game paints
        Assert.DoesNotContain(pal6[6 * 16 + 5], star);                              // not the red a 3bpp read gave
        // The letters of SPECIAL are animated tile 0x7C on layer 2, shown on its second frame as
        // Lunar Magic shows it: GFX14 tile 0x61, an X of colour 2 on colour 1.
        int bpp14 = Gfx.FileBpp(ow.Rom, 0x14);
        var frame1 = Gfx.DecodeTile(Gfx.Cached(ow.Rom, 0x14)!, 0x61 * Gfx.TileBytes(bpp14), bpp14);
        var pal5 = ow.PaletteOf(5).Rgba;
        Assert.Equal(frame1.Select(i => pal5[5 * 16 + i]), ow.TilePixels(0x147C, 5));
        // Animating runs the game's cycle: eight frames on, slots 2-7 show their third picture,
        // while the waterfall slots (counter bits 4-6) are still on their first; parking on
        // Lunar Magic's counter brings the letters back.
        ow.Animate(16);
        var frame2 = Gfx.DecodeTile(Gfx.Cached(ow.Rom, 0x14)!, 0x62 * Gfx.TileBytes(bpp14), bpp14);
        var slot1 = Gfx.DecodeTile(Gfx.Cached(ow.Rom, 0x14)!, 0x49 * Gfx.TileBytes(bpp14), bpp14);    // slot 1 at counter 16: its frame 1
        Assert.Equal(frame2.Select(i => pal5[5 * 16 + i]), ow.TilePixels(0x147C, 5));
        Assert.Equal(slot1.Select(i => i == 0 ? pal5[0] : pal5[5 * 16 + i]), ow.TilePixels(0x1479, 5));
        ow.Animate(Overworld.LunarMagicCounter);
        Assert.Equal(frame1.Select(i => pal5[5 * 16 + i]), ow.TilePixels(0x147C, 5));
        // The water scrolls a pixel a tick instead: tile 0x75's top rows slide left and its bottom
        // rows right, 0x76 slides down, 0x77 left and down. At rest it is the file, as LM draws it.
        var still = (ow.TilePixels(0x1475, 5), ow.TilePixels(0x1476, 5), ow.TilePixels(0x1477, 5));
        ow.Animate(Overworld.LunarMagicCounter + 8);
        var moved = (ow.TilePixels(0x1475, 5), ow.TilePixels(0x1476, 5), ow.TilePixels(0x1477, 5));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                Assert.Equal(still.Item1[y * 8 + ((x + (y < 4 ? 1 : 7)) & 7)], moved.Item1[y * 8 + x]);
                Assert.Equal(still.Item2[((y + 7) & 7) * 8 + x], moved.Item2[y * 8 + x]);
                Assert.Equal(still.Item3[((y + 7) & 7) * 8 + ((x + 1) & 7)], moved.Item3[y * 8 + x]);
            }
        ow.Animate(Overworld.LunarMagicCounter);
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
        // The canvas is the land at its own grain, 8x8 cells, laid out as Lunar Magic lays it out:
        // the main map, then the submap map rotated two cells right and one down, its last
        // columns and row wrapping to its left and top; the Tiles tab's drawer is the 256 8x8
        // tiles the two FG files give it.
        var view = w.GetControl<TilemapView>("OwView");
        Assert.Equal((64, 128, 8), (view.Cols, view.Rows, view.CellPx));
        Assert.True(Services.EditorSession.OwMapCell(0, 0, out _, out _, out bool sub0) && !sub0);
        Assert.False(Services.EditorSession.OwMapCell(64, 0, out _, out _, out _));                // off the canvas
        Assert.True(Services.EditorSession.OwMapCell(2, 65, out int cx0, out int cy0, out bool sub1) && sub1 && cx0 == 0 && cy0 == 0);
        Assert.True(Services.EditorSession.OwMapCell(0, 64, out int cx1, out int cy1, out _) && cx1 == 62 && cy1 == 63);   // the wrapped corner
        Assert.False(Services.EditorSession.OwHasLayer1(0, 64));                                      // land only there
        Assert.True(Services.EditorSession.OwHasLayer1(2, 65));
        var sheet = w.GetControl<TilemapView>("OwSheet");
        Assert.Equal((16, 16, 8), (sheet.Cols, sheet.Rows, sheet.CellPx));
        Assert.True(w.GetControl<DockPanel>("OwToolPanel").IsVisible);
        Assert.True(w.GetControl<StackPanel>("OwBrushBar").IsVisible);

        // The four sub-modes are the drawer's tabs, as the Level drawer's are. The layer 1 tabs
        // keep the same canvas and show layer 1's Map16 tiles in the drawer.
        var tabs = w.GetControl<TabStrip>("OwTabs");
        Assert.Equal(4, tabs.ItemCount);
        tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("layer 1", w.GetControl<TextBlock>("OwNote").Text);
        Assert.Equal((64, 128, 8), (view.Cols, view.Rows, view.CellPx));
        Assert.Equal((Overworld.VanillaMap16Count + 15) / 16, sheet.Rows);
        Assert.False(w.GetControl<StackPanel>("OwBrushBar").IsVisible);

        // The drawer's layer 1 tiles wear LM's path pictures too, so a path tile can be told from a blank one.
        Assert.Contains(sheet.CellPixels!(0x01)!, p => p == 0xFF00FF00);
        w.GetControl<ToggleButton>("OwShowPaths").IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(sheet.CellPixels!(0x01)!, p => p == 0xFF00FF00);

        // Lunar Magic's View menu rides in the bar on every tab; each toggle only redraws.
        Assert.True(w.GetControl<StackPanel>("OwViewBar").IsVisible);
        foreach (var name in new[] { "OwShowPaths", "OwShowLevelNumbers", "OwShowWarps" })
        {
            w.GetControl<ToggleButton>(name).IsChecked = true;
            Dispatcher.UIThread.RunJobs();
        }
        Assert.NotNull(view.Decorate);
        // Event numbers are the Events tab's business: the toggle only shows there.
        Assert.False(w.GetControl<ToggleButton>("OwShowEventNumbers").IsVisible);
        tabs.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.GetControl<ToggleButton>("OwShowEventNumbers").IsVisible);
    }

    /// <summary>The Paths &amp; Levels tab is Lunar Magic's Layer 1 16x16 Editor: the drawer's
    /// Map16 tile is placed by right-click on the 16x16 cell under the pointer, a lasso snaps to
    /// those cells — a cell right and down on the lower map, where LM draws them — and dragging
    /// it moves the tiles, leaving empty ones behind. Edits land in the reader's array, so the
    /// map redraws from them, and in the project for the build.</summary>
    [AvaloniaFact]
    public void the_paths_and_levels_tab_places_and_moves_layer_1_tiles_in_16x16s()
    {
        if (PreppedRom.Path is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeOverworld").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var session = (Services.EditorSession)typeof(MainWindow)
            .GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(w)!;
        w.GetControl<TabStrip>("OwTabs").SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        var view = w.GetControl<TilemapView>("OwView");
        var sheet = w.GetControl<TilemapView>("OwSheet");
        var l1 = session.OwLayer1!;
        view.Zoom = 4;                      // a 16x16 block at 100% is all grips; zoomed in it has a middle to grab
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((32, 64, 16), (l1.Cols, l1.Rows, l1.CellPx));
        Assert.NotNull(view.Snap);
        Assert.True(view.EditsOverlay);

        // The snap: a 16x16 tile is a 2x2 block of canvas cells, on the main map's even grid and
        // the lower map's shifted one; the wrapped strips carry no layer 1 and snap to nothing.
        Assert.Equal((10, 10, 2, 2), Services.EditorSession.OwLayer1Block(11, 11));
        Assert.Equal((2, 65, 2, 2), Services.EditorSession.OwLayer1Block(3, 66));
        Assert.Equal((0, 64, 1, 1), Services.EditorSession.OwLayer1Block(0, 64));
        view.BeginSelection(11, 11);
        view.Release();
        Assert.Equal((10, 10, 2, 2), view.Selection);
        view.ClearSelection();

        // Pick tile 0x56, the first level tile (row 5, column 6 of the 16px sheet), and place it
        // by right-click on canvas cell (11, 11) — layer 1 cell (5, 5) of the main map.
        var pick = sheet.TranslatePoint(new Point(6 * 16 * sheet.Zoom + 2, 5 * 16 * sheet.Zoom + 2), w)!.Value;
        w.MouseDown(pick, MouseButton.Left); w.MouseUp(pick, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        int before = l1.At(5, 5);
        var at = view.TranslatePoint(new Point(11 * 8 * view.Zoom + 2, 11 * 8 * view.Zoom + 2), w)!.Value;
        w.MouseDown(at, MouseButton.Right); w.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(0x56, before);
        Assert.Equal(0x56, l1.At(5, 5));
        Assert.Equal(0x56, session.Overworld!.Layer1At(5, 5, false));
        Assert.Equal(0x56, session.OwLayer1At(10, 11));           // every quarter of the cell shows it
        Assert.Equal(1, l1.UndoDepth);

        // Lasso the tile and drag it two cells right and down: the block lands on the grid at
        // (7, 7) and its old cell is empty. One undo puts it back.
        double step = 8 * view.Zoom;
        Point Mid(int c, int r) => new((c + 0.5) * step, (r + 0.5) * step);
        view.BeginSelection(10, 10);
        view.Release();
        view.PressAt(Mid(10, 10));
        view.MoveTo(Mid(14, 15));
        view.Release();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0x56, l1.At(7, 7));
        Assert.Equal(0, l1.At(5, 5));
        Assert.Equal((14, 14, 2, 2), view.Selection);
        Assert.Equal(2, l1.UndoDepth);
        Assert.True(l1.Undo());
        Assert.Equal(0x56, l1.At(5, 5));
        Assert.Equal(before, l1.At(7, 7));

        // Delete empties the lassoed tiles (Backspace too, for keyboards without a Delete key).
        view.BeginSelection(10, 10);
        view.Release();
        w.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, l1.At(5, 5));
        Assert.Equal(2, l1.UndoDepth);                            // the undo above took one entry back; the delete adds one
    }

    /// <summary>Layer 1 writes back where the game reads it: the low bytes in place, and the
    /// edited map reads back from a ROM built with it. A tile from page 1 has nowhere to go on a
    /// vanilla ROM, and the writer says so rather than dropping its high byte silently.</summary>
    [Fact]
    public void layer_1_writes_in_place_and_reads_back()
    {
        if (PreppedRom.Path is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        var rom = Rom.Load(p);
        var ow = new Overworld(rom);
        var words = (ushort[])ow.Layer1.Clone();
        words[Overworld.Layer1Index(5, 5, false)] = 0x56;
        words[Overworld.Layer1Index(3, 4, true)] = 0x01;
        Assert.Null(Overworld.WriteLayer1(rom, words));
        var built = Rom.Load(p);                                 // a fresh ROM with the written bytes: what a build hands the game
        Array.Copy(rom.Data, built.Data, rom.Data.Length);
        var read = new Overworld(built);
        Assert.Equal(0x56, read.Layer1At(5, 5, false));
        Assert.Equal(0x01, read.Layer1At(3, 4, true));
        words[Overworld.Layer1Index(0, 0, false)] = 0x100;
        Assert.Contains("0x100", Overworld.WriteLayer1(rom, words));
    }

    /// <summary>The Palette drawer's Overworld tab shows a submap's colours and puts the map, not
    /// the level, beside them; the Level tab puts the level back.</summary>
    [AvaloniaFact]
    public void the_palette_drawer_has_an_overworld_tab_that_shows_the_map()
    {
        if (PreppedRom.Path is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModePalette").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var session = (Services.EditorSession)typeof(MainWindow)
            .GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(w)!;
        var tabs = w.GetControl<TabStrip>("PalScopeTabs");
        var grid = w.GetControl<PaletteGridView>("PaletteGrid");
        Assert.Equal(2, tabs.ItemCount);
        Assert.True(w.GetControl<DockPanel>("LevelPane").IsVisible);
        Assert.False(w.GetControl<DockPanel>("OverworldPane").IsVisible);
        Assert.Equal(session.PaletteRgba, grid.Colors);

        tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.GetControl<DockPanel>("LevelPane").IsVisible);
        Assert.True(w.GetControl<DockPanel>("OverworldPane").IsVisible);
        Assert.True(w.GetControl<StackPanel>("PalSubmapRow").IsVisible);
        Assert.Equal(session.Overworld!.PaletteOf(0).Rgba, grid.Colors);
        // Colours only: no brushes, no View toggles, layer 1 on, the path pictures off.
        Assert.False(w.GetControl<StackPanel>("OwBrushBar").IsVisible);
        Assert.False(w.GetControl<StackPanel>("OwViewBar").IsVisible);
        var owView = w.GetControl<TilemapView>("OwView");
        Assert.NotNull(owView.OverlayPixels);
        Assert.DoesNotContain(owView.OverlayPixels!(2 * 11, 2 * 3) ?? [], c => c == 0xFF00FF00);   // main map (11,3) is a walk tile: blank art, and no green picture over it
        // The zoom slider drives the map while it is the canvas showing: the overworld's half-steps.
        var zoom = w.GetControl<Slider>("ZoomSlider");
        Assert.Equal(50, zoom.TickFrequency);
        zoom.Value = 300;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, w.GetControl<TilemapView>("OwView").Zoom);
        w.GetControl<ComboBox>("PalSubmap").SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(session.Overworld!.PaletteOf(1).Rgba, grid.Colors);
        Assert.Contains("Yoshi", w.GetControl<TextBlock>("PaletteNote").Text);

        tabs.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.GetControl<DockPanel>("LevelPane").IsVisible);
        Assert.False(w.GetControl<DockPanel>("OverworldPane").IsVisible);
        Assert.Equal(session.PaletteRgba, grid.Colors);
        Assert.Equal(10, zoom.TickFrequency);                // and back to the level's steps
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
        // Delete over a lasso puts the region's fill back — the main map's sea — as one undo.
        view.BeginSelection(20, 20);
        view.Release();
        w.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(map.At(0, 0), map.At(20, 20));
        Assert.True(map.Undo());
        Assert.Equal(0x23 | 5 << 10, map.At(20, 20));
        // What the Tiles tab paints and moves is layer 2 alone: a cell under Yoshi's House (the
        // submap map's (6,7), 8x8 (12,14), which the canvas shows at (14, 79) — two right and
        // one down of the map's corner, as LM lays it out) draws the land word, and the level
        // tile comes from the overlay, which a lasso never carries. The wrapped strip at the lower
        // map's top is land from its last row, with no layer 1 over it.
        int cell = 79 * Services.EditorSession.Ow8Cols + 14;
        Assert.Equal(session.Overworld!.TilePixels(map.At(14, 79), 1), session.Ow8CellPixels(cell));
        Assert.Contains(session.Ow8OverlayPixels(14, 79)!, c => c != 0);
        Assert.Equal(session.Overworld!.Layer2[Overworld.Layer2Index(62, 63, true)], map.At(0, 64));
        Assert.Null(session.Ow8OverlayPixels(0, 64));
        Assert.NotNull(view.OverlayPixels);
        // Layer 1 off leaves Lunar Magic's path pictures in the overlay; Paths off too leaves nothing.
        w.GetControl<ToggleButton>("OwShowLayer1").IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(view.OverlayPixels);
        w.GetControl<ToggleButton>("OwShowPaths").IsChecked = false;
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
        Assert.Equal(session.Overworld!.TilePixels(0x11 | 5 << 10, 0), view.CellPixels!(40 * Services.EditorSession.Ow8Cols + 40));   // but it is drawn there
        Assert.Equal(session.Overworld!.TilePixels(fill, 0), view.CellPixels!(30 * Services.EditorSession.Ow8Cols + 30));               // over a hole of fill

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
        Assert.Equal(session.Overworld!.TilePixels(under, 0), view.CellPixels!(40 * Services.EditorSession.Ow8Cols + 40));
        Assert.Equal(session.Overworld!.TilePixels(0x11 | 5 << 10, 0), view.CellPixels!(50 * Services.EditorSession.Ow8Cols + 50));
        Assert.Equal(session.Overworld!.TilePixels(fill, 0), view.CellPixels!(30 * Services.EditorSession.Ow8Cols + 30));
        w.GetControl<Slider>("ZoomSlider").Value -= 50;      // out, so every cell below stays inside the window
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(session.Overworld!.TilePixels(0x11 | 5 << 10, 0), view.CellPixels!(50 * Services.EditorSession.Ow8Cols + 50));
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
