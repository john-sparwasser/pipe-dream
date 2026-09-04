using Avalonia.Headless;
using Avalonia.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using PipeDream.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The Background canvas mode and its two layers.
///
/// Each layer has a state where there is genuinely nothing to draw, and the whole point of the
/// mode is that it says WHICH one rather than showing an empty pane: layer 2 is empty when the
/// level puts objects there instead of an image (the same split Lunar Magic draws), and layer 3
/// is empty when the level's Layer 3 Options is Blank. Those two notes are what these tests
/// hold on to, because a silently blank canvas passes any assertion about pixels.
///
/// Levels used: $105 has a background image and no layer 3; $009 (a ghost house) is the exact
/// opposite — objects on layer 2, and a Tileset Specific layer 3.
/// </summary>
public class BackgroundModeTests(ITestOutputHelper log)
{
    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static bool HaveRom => File.Exists(Vanilla);

    /// <summary>A window in Background mode, showing one level, on one of its two layers.</summary>
    private static MainWindow Open(int level, bool layer3)
    {
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();

        w.GetControl<ComboBox>("LevelBox").SelectedIndex = level;
        Click(w, "ModeBg");
        if (layer3) Click(w, "BgLayer3");
        return w;
    }

    private static void Click(MainWindow w, string name)
    {
        w.GetControl<ToggleButton>(name).RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static TilemapView View(MainWindow w) => w.GetControl<TilemapView>("BgView");
    private static TilemapView Sheet(MainWindow w) => w.GetControl<TilemapView>("BgSheet");

    /// <summary>Alt/Cmd+wheel zooms the map about the cell under the cursor: after a notch, the
    /// same cell is still under the pointer. The plain wheel scrolls, on either layer, and the
    /// drawer sheet keeps its fit-to-width zoom.</summary>
    [AvaloniaFact]
    public void alt_wheel_zooms_the_map_about_the_cursor_and_the_plain_wheel_scrolls()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x009, layer3: true);
        var view = View(w);
        var sheet = Sheet(w);
        double zoom0 = view.Zoom, sheetZoom = sheet.Zoom;

        // On screen at the starting zoom, and far enough in that the zoomed map outgrows the
        // viewport, so the offset has room to follow.
        double step = view.CellPx * view.Zoom;
        var local = new Point(24 * step + 3, 24 * step + 3);
        var at = view.TranslatePoint(local, w)!.Value;
        var cell = view.At(local);
        Assert.NotNull(cell);

        for (int i = 0; i < 3; i++)
        {
            w.MouseWheel(at, new Vector(0, 1), i == 1 ? RawInputModifiers.Meta : RawInputModifiers.Alt);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal(zoom0 + 1.5, view.Zoom);
        Assert.Equal(cell, view.At(w.TranslatePoint(at, view)!.Value));
        // The footer slider is the zoom's owner: it follows the wheel, and a refresh reads it
        // back instead of snapping the view to a fixed zoom.
        Assert.Equal((zoom0 + 1.5) * 100, w.GetControl<Slider>("ZoomSlider").Value);
        Click(w, "BgLayer3"); Click(w, "BgLayer3");           // two refreshes, back on layer 3
        Assert.Equal(zoom0 + 1.5, view.Zoom);
        w.GetControl<Slider>("ZoomSlider").Value = 400;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, view.Zoom);
        w.GetControl<Slider>("ZoomSlider").Value = (zoom0 + 1.5) * 100;
        Dispatcher.UIThread.RunJobs();

        w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(zoom0 + 1.0, view.Zoom);
        Assert.Equal(cell, view.At(w.TranslatePoint(at, view)!.Value));

        // The plain wheel is a scroll, not a zoom — here on layer 3, and below on a layer 2.
        var sv = w.GetControl<ScrollViewer>("BgScroll");
        w.GetControl<Slider>("ZoomSlider").Value = 400;     // in far enough that there is room to scroll
        Dispatcher.UIThread.RunJobs();
        double y = sv.Offset.Y;
        w.MouseWheel(at, new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, view.Zoom);
        Assert.True(sv.Offset.Y > y, $"layer 3: the plain wheel did not scroll (offset {sv.Offset.Y})");

        var onSheet = sheet.TranslatePoint(new Point(4, 4), w)!.Value;
        w.MouseWheel(onSheet, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(sheetZoom, sheet.Zoom);
    }

    /// <summary>Layer 2 follows the same rule as layer 3: the plain wheel scrolls the background,
    /// Alt/Cmd+wheel zooms it. Level 105 has a layer-2 image; level 9 above does not.</summary>
    [AvaloniaFact]
    public void layer_2_scrolls_on_the_wheel_and_zooms_on_the_chord()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x105, layer3: false);
        var view = View(w);
        var sv = w.GetControl<ScrollViewer>("BgScroll");
        Assert.True(view.Rows > 0, "level 105 should have a layer-2 background");
        w.GetControl<Slider>("ZoomSlider").Value = 400;
        Dispatcher.UIThread.RunJobs();
        var at = view.TranslatePoint(new Point(40, 40), w)!.Value;

        double y = sv.Offset.Y;
        w.MouseWheel(at, new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, view.Zoom);
        Assert.True(sv.Offset.Y > y, $"the plain wheel did not scroll (offset {sv.Offset.Y})");

        w.MouseWheel(at, new Vector(0, 1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4.5, view.Zoom);
        w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Meta);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, view.Zoom);
    }

    /// <summary>The drawer's BG Map16 sheet zooms the way the Tiles picker does: Alt/Cmd+wheel over
    /// it widens or narrows the drawer by half a tile scale, and the sheet, which fits the drawer,
    /// follows; the range stops it at the floor and the ceiling.</summary>
    [AvaloniaFact]
    public void alt_wheel_over_the_drawer_sheet_resizes_the_drawer()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x105, layer3: false);
        var sheet = Sheet(w);
        var col = w.GetControl<Grid>("Split").ColumnDefinitions[0];
        var at = sheet.TranslatePoint(new Point(8, 8), w)!.Value;

        double width = col.Width.Value, zoom = sheet.Zoom;
        w.MouseWheel(at, new Vector(0, 1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width + 128, col.Width.Value, 1);
        Assert.Equal(zoom + 0.5, sheet.Zoom, 2);
        w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Meta);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width, col.Width.Value, 1);
        Assert.Equal(zoom, sheet.Zoom, 2);

        for (int i = 0; i < 20; i++) w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(col.MinWidth, col.Width.Value, 1);
        for (int i = 0; i < 40; i++) w.MouseWheel(at, new Vector(0, 1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.True(col.Width.Value <= col.MaxWidth + 0.5 && col.Width.Value > col.MinWidth);
    }

    [AvaloniaFact]
    public void background_mode_takes_the_canvas_and_the_drawer()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x105, layer3: false);

        Assert.True(w.GetControl<DockPanel>("BgPane").IsVisible);
        Assert.True(w.GetControl<DockPanel>("BgToolPanel").IsVisible);
        Assert.False(w.GetControl<DockPanel>("LevelPane").IsVisible);
        Assert.False(w.GetControl<DockPanel>("GfxScroll").IsVisible);
    }

    [AvaloniaFact]
    public void layer_2_paints_bg_map16_tiles_over_two_screens()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x105, layer3: false);

        Assert.Equal(EditorSession.BgCols, View(w).Cols);
        Assert.Equal(EditorSession.BgRows, View(w).Rows);
        Assert.Equal(16, View(w).CellPx);
        Assert.NotNull(View(w).CellAt);
        // The drawer is the same control, picking rather than painting.
        Assert.True(Sheet(w).PickOnLeft);
        Assert.Equal(16, Sheet(w).Cols);
        Assert.Contains("pages 80-81", w.GetControl<TextBlock>("BgDrawerTitle").Text);
    }

    [AvaloniaFact]
    public void layer_2_points_at_the_level_canvas_when_the_level_puts_objects_there()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x009, layer3: false);

        Assert.Equal(0, View(w).Cols);
        Assert.Contains("object stream", w.GetControl<TextBlock>("BgNote").Text);
    }

    [AvaloniaFact]
    public void layer_3_paints_a_64_by_64_grid_of_8x8_tiles()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x009, layer3: true);

        Assert.Equal(Layer3.Cols, View(w).Cols);
        Assert.Equal(Layer3.Rows, View(w).Rows);
        Assert.Equal(8, View(w).CellPx);
        Assert.Equal(Layer3.TileCount / 16, Sheet(w).Rows);
        Assert.Contains("Tileset specific", w.GetControl<TextBlock>("BgNote").Text);
        Assert.Contains("Layer 3 tiles", w.GetControl<TextBlock>("BgDrawerTitle").Text);
    }

    [AvaloniaFact]
    public void a_level_with_no_layer_3_says_where_to_turn_one_on()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x105, layer3: true);

        Assert.Equal(0, View(w).Cols);
        // A dead end that names no way out is the same as no message at all — and the way out
        // is the button next to it, which is the point of having moved the setting here.
        Assert.Contains("Layer 3 Options", w.GetControl<TextBlock>("BgNote").Text);
        Assert.True(w.GetControl<Button>("BgOptions").IsVisible);
    }

    /// <summary>
    /// Turning the option on is what "activates" a layer 3, and the tab has to notice. The
    /// entrance byte the dialog writes is mostly spawn bookkeeping the canvas never draws, so
    /// the properties flow used not to repaint at all — which looked exactly like the setting
    /// not taking.
    /// </summary>
    [AvaloniaFact]
    public void setting_the_layer_3_option_makes_the_level_grow_one()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x105, layer3: true);
        var session = SessionOf(w);
        Assert.Equal(0, View(w).Cols);

        // What Level ▸ Properties ▸ "Layer 3 option" does: 3 = Tileset specific.
        session.ApplyEntry(session.MainEntrance!.Value with { Layer3Option = 3 });
        session.ShowLevel(session.LevelNum);          // the option picks the tilemap at load
        Invoke(w, "AdoptSession");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Layer3.Cols, View(w).Cols);
        Assert.Contains("Tileset specific", w.GetControl<TextBlock>("BgNote").Text);
    }

    /// <summary>
    /// Repointing an LG slot has to reach the pane. The tiles a word draws as are CACHED per
    /// word — a 64x64 grid recomposes on every stamp and only ever names a few dozen — so the
    /// cache has to go when the graphics under it move, or the pane keeps drawing the old sheet.
    ///
    /// Level 009's tilemap names tiles from LG2 and LG4 only, so LG4 is the slot to move: a test
    /// on LG1 would pass without the invalidation, because nothing on screen references it.
    /// </summary>
    [AvaloniaFact]
    public void repointing_a_layer_3_gfx_slot_redraws_the_pane()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x009, layer3: true);
        var session = SessionOf(w);

        // A word this level's map actually uses, from LG4 (tiles 180-1FF).
        int word = Enumerable.Range(0, Layer3.Cols * Layer3.Rows)
            .Select(i => session.Layer3Map!.At(i % Layer3.Cols, i / Layer3.Cols))
            .First(v => v >= 0 && (v & 0x3FF) >= 3 * Layer3.SlotTiles);
        var before = session.Layer3CellPixels(word);
        Assert.NotNull(before);

        session.SetGfxSlot(12, 0x14);                 // w12 = LG4
        Invoke(w, "AdoptSession");
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(before, session.Layer3CellPixels(word));
        Assert.Equal(0x14, session.GfxBins.Single(b => b.Name == "LG4").File);
    }

    /// <summary>The settings button belongs to Layer 3 and appears only there — on Layer 2 it
    /// could not apply to anything on screen.</summary>
    [AvaloniaFact]
    public void the_options_button_shows_on_layer_3_and_nowhere_else()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x105, layer3: false);
        var button = w.GetControl<Button>("BgOptions");
        Assert.False(button.IsVisible);

        Click(w, "BgLayer3");
        Assert.True(button.IsVisible);
        Assert.True(button.IsEnabled);

        Click(w, "BgLayer2");
        Assert.False(button.IsVisible);
    }

    /// <summary>The dialog's two fields come from two different records, and both have to land:
    /// the option is the entrance byte, the priority flag is in the header.</summary>
    [AvaloniaFact]
    public void the_options_dialog_writes_the_option_and_the_priority_flag()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x105, layer3: true);
        var session = SessionOf(w);
        Assert.Equal(0, session.MainEntrance!.Value.Layer3Option);
        Assert.Equal(0, session.Header!.Value.Layer3Priority);

        // What OnLayer3Options applies once the dialog returns (Option 3, priority on).
        session.ApplyEntry(session.MainEntrance.Value with { Layer3Option = 3 });
        session.ApplyHeader(session.Header.Value with { Layer3Priority = 1 });
        Invoke(w, "AdoptSession");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, session.MainEntrance!.Value.Layer3Option);
        Assert.Equal(1, session.Header!.Value.Layer3Priority);
    }

    /// <summary>The dialog warns before you commit, rather than leaving the empty pane to say
    /// it afterwards: an option is only as good as the level mode's tilemap for it.</summary>
    [AvaloniaFact]
    public void the_session_knows_which_options_this_levels_mode_can_reach()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var session = SessionOf(Open(0x105, layer3: true));

        Assert.False(session.Layer3HasTilemap(0));      // Blank never reaches one
        Assert.True(session.Layer3HasTilemap(3));       // level 105 is mode 0, which has all three
    }

    /// <summary>
    /// The precedence the other canvases run: a lasso made HERE outranks the tile picked in the
    /// drawer, so a right-click stamps the lassoed rectangle rather than the drawer's one tile.
    /// Dropping the lasso hands the drawer back its say.
    /// </summary>
    [AvaloniaFact]
    public void a_canvas_lasso_outranks_the_drawers_tile()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x105, layer3: false);
        var map = SessionOf(w).BgMap!;
        var view = View(w);

        // Two cells that differ, so stamping them somewhere else is unambiguous.
        map.Stamp(2, 2, 0x30); map.Stamp(3, 2, 0x31);
        map.EndStroke();

        view.BeginSelection(2, 2);
        view.ExtendSelection(3, 2);
        Assert.Equal((2, 2, 2, 1), view.Selection);
        Assert.Equal((2, 1), view.Brush);          // the cursor outlines what would land

        Paint(w, 5, 5);
        Assert.Equal(0x30, map.At(5, 5));
        Assert.Equal(0x31, map.At(6, 5));

        // Without a lasso the drawer's tile is what lands, one cell of it. bgBrush's default is
        // 0x100 — a PAGE-1 tile — and this is a bare vanilla ROM, where $105's background has one
        // page from its address, so what lands is that page's tile 0x00: the remap that used to
        // happen silently in the build now happens, visibly, at paint time.
        view.ClearSelection();
        int lands = SessionOf(w).BgPaintable(0x100), neighbour = map.At(9, 8);
        Paint(w, 8, 8);
        Assert.Equal(lands, map.At(8, 8));
        Assert.Equal(neighbour, map.At(9, 8));     // one cell: the next one over is untouched
    }

    /// <summary>Stamping a selection over itself is the ordinary case — nudging a pattern along
    /// by a cell — so the source is read WHOLE before any of it is written.</summary>
    [AvaloniaFact]
    public void stamping_a_selection_onto_itself_does_not_smear_it()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x105, layer3: false);
        var map = SessionOf(w).BgMap!;
        var view = View(w);

        for (int i = 0; i < 4; i++) map.Stamp(i, 10, 0x30 + i);
        map.EndStroke();

        view.BeginSelection(0, 10);
        view.ExtendSelection(3, 10);
        Paint(w, 1, 10);                            // one cell to the right, overlapping itself

        Assert.Equal([0x30, 0x30, 0x31, 0x32, 0x33],
                     Enumerable.Range(0, 5).Select(i => map.At(i, 10)));
    }

    /// <summary>
    /// The Layer 3 settings dialog carries the advanced group — the override for the tileset's
    /// own scroll and blend behaviour, which is the only thing in Lunar Magic that overrides it.
    /// The group is disabled until its checkbox is on, so that "off" and "on with defaults"
    /// cannot be confused, and OK hands back null for the former.
    /// </summary>
    [AvaloniaFact]
    public void the_layer_3_dialog_offers_the_tileset_override_and_returns_null_when_it_is_off()
    {
        var adv = new Layer3.Advanced(CgAdSub: false, Subscreen: true, FixScrollSync: true,
                                      VScroll: 8, HScroll: 6, XPos: 3, Y: 0x123);
        var dlg = new Layer3OptionsWindow(Layer3.OptionNames, 3, true, _ => true, adv);
        var pane = dlg.GetControl<StackPanel>("AdvancedPane");
        var box = dlg.GetControl<CheckBox>("AdvancedBox");

        Assert.True(box.IsChecked);
        Assert.True(pane.IsEnabled);
        Assert.Equal("Fast", dlg.GetControl<ComboBox>("VScrollBox").SelectedItem);
        Assert.Equal("Slow", dlg.GetControl<ComboBox>("HScrollBox").SelectedItem);
        Assert.Equal("123", dlg.GetControl<TextBox>("YBox").Text);
        Assert.Equal("10", dlg.GetControl<ComboBox>("XBox").SelectedItem);

        InvokeOn(dlg, "Commit");
        Assert.Equal(adv, dlg.Result!.Value.Advanced);

        box.IsChecked = false;
        Assert.False(pane.IsEnabled);
        InvokeOn(dlg, "Commit");
        Assert.Null(dlg.Result!.Value.Advanced);
    }

    /// <summary>
    /// The gutter palette is FOUR swatches on layer 3 and sixteen on layer 2. That is the whole
    /// reason this mode has a strip of its own: layer 3 is 2bpp, and a sixteen-wide row would
    /// show twelve colours the layer cannot draw next to the four it can.
    /// </summary>
    [AvaloniaFact]
    public void the_gutter_palette_is_four_colours_wide_on_layer_3_and_sixteen_on_layer_2()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x009, layer3: true);
        var colors = w.GetControl<PaletteGridView>("BgColors");

        Assert.True(w.GetControl<Border>("BgPaletteBar").IsVisible);
        Assert.Equal(Layer3.PaletteColors, colors.Cols);
        Assert.True(w.GetControl<ComboBox>("BgPalRow").IsEnabled);
        Assert.True(w.GetControl<Button>("BgEditPal").IsVisible);

        Click(w, "BgLayer2");
        Assert.Equal(16, colors.Cols);
        // A BG Map16 tile carries its palette in its own definition, so a live picker here would
        // promise an edit this mode cannot make.
        Assert.False(w.GetControl<ComboBox>("BgPalRow").IsEnabled);
    }

    /// <summary>
    /// Choosing a group rewrites bits 10-12 of the brush and nothing else, and "Apply" puts it on
    /// cells that ALREADY have tiles — the only sane way to recolour an imported tilemap, which
    /// is 4096 cells somebody else authored. Tiles, flips and the priority bit all survive it.
    /// </summary>
    [AvaloniaFact]
    public void applying_a_palette_group_recolours_the_map_without_moving_a_tile()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x009, layer3: true);
        var map = SessionOf(w).Layer3Map!;

        // A word with every other attribute bit set, so a sloppy mask shows up as a lost flip.
        int word = 0xE000 | 3 << 10 | 0x123;
        map.Stamp(4, 5, word);
        map.EndStroke();

        w.GetControl<ComboBox>("BgPalRow").SelectedIndex = 6;
        Click(w, "OnApplyBgPalette", null);

        int now = map.At(4, 5);
        Assert.Equal(6, Layer3.PaletteOf(now));
        Assert.Equal(word & ~0x1C00, now & ~0x1C00);      // tile, both flips, priority: untouched
    }

    /// <summary>
    /// Both drawer sheets fill the drawer rather than sitting at a fixed 256px with dead desk
    /// beside them. Layer 3's tiles are 8px and layer 2's are 16, so a single zoom cannot serve
    /// both — the fit is per-sheet, and asserting the WIDTH rather than the zoom is what says so.
    /// </summary>
    [AvaloniaFact]
    public void the_drawer_sheet_fills_the_width_the_drawer_gives_it_on_both_layers()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x009, layer3: true);
        var sheet = w.GetControl<TilemapView>("BgSheet");

        foreach (double width in new[] { 300.0, 520.0 })
        {
            sheet.Measure(new Avalonia.Size(width, double.PositiveInfinity));
            Assert.Equal(width, sheet.DesiredSize.Width, 1);
        }

        // Layer 2 needs a level that HAS a background image — $009 puts objects there, and an
        // empty drawer fills nothing.
        var sheet2 = Open(0x105, layer3: false).GetControl<TilemapView>("BgSheet");
        sheet2.Measure(new Avalonia.Size(520, double.PositiveInfinity));
        Assert.Equal(520, sheet2.DesiredSize.Width, 1);
    }

    /// <summary>
    /// Undo on the Background tab follows the LAYER on screen, from the key AND the Edit menu.
    /// The menu used to call the level-object editor's undo whatever mode was showing, so
    /// Edit ▸ Undo after a layer-3 stroke rewound nothing you could see — "layer 3 has no undo".
    /// Both entry points now share one dispatch, and this pins that they stay shared.
    /// </summary>
    [AvaloniaFact]
    public void layer_3_strokes_undo_and_redo_from_the_key_and_the_edit_menu()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x009, layer3: true);
        var map = SessionOf(w).Layer3Map!;
        int before = map.At(9, 9);
        int word = before == (3 << 10 | 0x1A5) ? 3 << 10 | 0x1A6 : 3 << 10 | 0x1A5;
        Assert.True(map.Stamp(9, 9, word));
        Assert.True(map.EndStroke());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(word, map.At(9, 9));

        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, map.At(9, 9));
        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(word, map.At(9, 9));

        static void Menu(MainWindow w, string name) =>
            w.GetControl<MenuItem>(name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Menu(w, "EditUndo");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, map.At(9, 9));
        Menu(w, "EditRedo");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(word, map.At(9, 9));
    }

    /// <summary>The strip's Edit button goes where colours are edited: Palette mode, narrowed
    /// to layer 3, with this group's first paintable colour selected.</summary>
    [AvaloniaFact]
    public void the_palette_strips_edit_button_opens_the_palette_tab_in_layer_3_view()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x009, layer3: true);
        int group = w.GetControl<ComboBox>("BgPalRow").SelectedIndex;
        Assert.True(group >= 0);

        w.GetControl<Button>("BgEditPal").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(w.GetControl<ToggleButton>("ModePalette").IsChecked);
        Assert.True(w.GetControl<DockPanel>("PalettePanel").IsVisible);
        Assert.True(w.GetControl<CheckBox>("PaletteLayer3").IsChecked);
        var grid = w.GetControl<PaletteGridView>("PaletteGrid");
        Assert.Equal(Layer3.PaletteColors, grid.Cols);
        Assert.Equal(Layer3.PaletteBase(group) + 1, grid.Selected);
    }

    /// <summary>Cmd+Z (Ctrl+Z) rewinds the background stroke you just painted, on either layer,
    /// with the view itself focused from the click — arriving from Palette mode, which used to
    /// be a drawer tab whose hidden index outranked the canvas mode in undo's dispatch.</summary>
    [AvaloniaTheory]
    [InlineData(0x009, true, RawInputModifiers.Meta)]
    [InlineData(0x105, false, RawInputModifiers.Control)]
    public void a_painted_background_cell_undoes_from_the_key_on_either_layer(int level, bool layer3, RawInputModifiers cmd)
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ComboBox>("LevelBox").SelectedIndex = level;
        Click(w, "ModePalette");
        Click(w, "ModeBg");
        if (layer3) Click(w, "BgLayer3");

        var view = View(w);
        var map = (layer3 ? SessionOf(w).Layer3Map : SessionOf(w).BgMap)!;
        // A cell the default brush will visibly change (on a vanilla base the brush lands as
        // tile 0), inside the part of the map the viewport shows.
        var (cx, cy) = Enumerable.Range(0, 20 * 30).Select(i => (i % 30, i / 30))
            .First(c => map.At(c.Item1, c.Item2) != 0);
        int before = map.At(cx, cy);
        double step = view.CellPx * view.Zoom;
        var at = view.TranslatePoint(new Point(cx * step + 2, cy * step + 2), w)!.Value;
        w.MouseDown(at, MouseButton.Right);
        w.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        int painted = map.At(cx, cy);
        Assert.NotEqual(before, painted);

        w.KeyPressQwerty(PhysicalKey.Z, cmd);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, map.At(cx, cy));
        w.KeyPressQwerty(PhysicalKey.Z, cmd | RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(painted, map.At(cx, cy));
    }

    /// <summary>
    /// What the two selection drags do to the TILES, driven through the canvas the way the mouse
    /// drives it. Growing repeats the block into the space the grip opened — phased on the block's
    /// own origin, so what was already there does not shift while the new space fills. Moving
    /// takes the block with it and leaves the layer's blank behind, which on layer 3 is a word
    /// the tilemap never wrote.
    ///
    /// Both are one undo entry, because both are one gesture.
    /// </summary>
    [AvaloniaFact]
    public void growing_a_selection_repeats_it_and_moving_one_takes_the_tiles_along()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x009, layer3: true);
        var view = View(w);
        var map = SessionOf(w).Layer3Map!;
        int depth = map.UndoDepth;
        double step = view.CellPx * view.Zoom;

        const int A = 6 << 10 | 0x111, B = 6 << 10 | 0x222;
        map.Stamp(4, 4, A);
        map.Stamp(5, 4, B);
        Assert.True(map.EndStroke());

        // Lasso the pair, then drag the right grip two cells out: A B A B.
        view.BeginSelection(4, 4);
        view.ExtendSelection(5, 4);
        view.Release();
        view.PressAt(new Avalonia.Point(6 * step, 4.5 * step));
        view.MoveTo(new Avalonia.Point(7.5 * step, 4.5 * step));
        view.Release();
        Assert.Equal((4, 4, 4, 1), view.Selection);
        Assert.Equal([A, B, A, B], new[] { map.At(4, 4), map.At(5, 4), map.At(6, 4), map.At(7, 4) });
        Assert.Equal(depth + 2, map.UndoDepth);           // the paint, then the grow

        // Now drag it by its middle, one column right as well as four rows down. The sideways
        // step is the half that matters: a move follows the block, so A B A B has to arrive as
        // A B A B — reading it through the repeat's own wrap would rotate it to B A B A.
        view.PressAt(new Avalonia.Point(5.5 * step, 4.5 * step));
        view.MoveTo(new Avalonia.Point(6.5 * step, 8.5 * step));
        view.Release();
        Assert.Equal((5, 8, 4, 1), view.Selection);
        Assert.Equal([A, B, A, B], new[] { map.At(5, 8), map.At(6, 8), map.At(7, 8), map.At(8, 8) });
        Assert.All(new[] { map.At(4, 4), map.At(5, 4), map.At(6, 4), map.At(7, 4) },
                   v => Assert.Equal(-1, v));             // and left nothing behind
        Assert.Equal(depth + 3, map.UndoDepth);

        // What the drag SHOWED is what it wrote: the preview asks the same mapping.
        var drag = new TilemapView.SelectionDrag((4, 4, 4, 1), (5, 8, 4, 1), Move: true);
        Assert.Equal((4, 4), drag.Source(5, 8));
        Assert.Equal((7, 4), drag.Source(8, 8));
    }

    /// <summary>Fire a Click handler the way the button would, with the sender it checks.</summary>
    private static void Click(MainWindow w, string method, object? sender) => typeof(MainWindow)
        .GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .Invoke(w, [sender, new Avalonia.Interactivity.RoutedEventArgs()]);

    private static void InvokeOn(Window w, string method) => w.GetType()
        .GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .Invoke(w, null);

    private static void Paint(MainWindow w, int col, int row)
        => typeof(MainWindow)
            .GetMethod("BgPaint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(w, [col, row]);

    private static EditorSession SessionOf(MainWindow w) => (EditorSession)typeof(MainWindow)
        .GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .GetValue(w)!;

    private static void Invoke(MainWindow w, string method) => typeof(MainWindow)
        .GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .Invoke(w, null);
}
