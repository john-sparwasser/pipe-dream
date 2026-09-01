using Avalonia.Controls;
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

        // Without a lasso the drawer's tile is what lands, one cell of it.
        view.ClearSelection();
        Paint(w, 8, 8);
        Assert.Equal(0x100, map.At(8, 8));         // bgBrush's default
        Assert.NotEqual(0x100, map.At(9, 8));
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
        Assert.Contains("CGRAM", w.GetControl<TextBlock>("BgPalNote").Text);

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
