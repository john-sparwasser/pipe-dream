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
    public void layer_2_draws_a_background_image_and_the_bg_map16_beside_it()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x105, layer3: false);

        Assert.NotNull(w.GetControl<PixelImage>("BgView").Source);
        Assert.NotNull(w.GetControl<PixelImage>("BgSheet").Source);
        Assert.Contains("pages 80-81", w.GetControl<TextBlock>("BgDrawerTitle").Text);
    }

    [AvaloniaFact]
    public void layer_2_points_at_the_level_canvas_when_the_level_puts_objects_there()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x009, layer3: false);

        Assert.Null(w.GetControl<PixelImage>("BgView").Source);
        Assert.Contains("object stream", w.GetControl<TextBlock>("BgNote").Text);
    }

    [AvaloniaFact]
    public void layer_3_draws_the_levels_tilemap_and_names_the_option_that_chose_it()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x009, layer3: true);

        var view = w.GetControl<PixelImage>("BgView");
        Assert.NotNull(view.Source);
        Assert.Equal(Layer3.Cols * 8 * 2, view.Width);        // 512x512, drawn at 2x like the BG
        Assert.NotNull(w.GetControl<PixelImage>("BgSheet").Source);
        Assert.Contains("Tileset specific", w.GetControl<TextBlock>("BgNote").Text);
        Assert.Contains("Layer 3 tiles", w.GetControl<TextBlock>("BgDrawerTitle").Text);
    }

    [AvaloniaFact]
    public void a_level_with_no_layer_3_says_where_to_turn_one_on()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x105, layer3: true);

        Assert.Null(w.GetControl<PixelImage>("BgView").Source);
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
        Assert.Null(w.GetControl<PixelImage>("BgView").Source);

        // What Level ▸ Properties ▸ "Layer 3 option" does: 3 = Tileset specific.
        session.ApplyEntry(session.MainEntrance!.Value with { Layer3Option = 3 });
        Invoke(w, "AdoptSession");
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(w.GetControl<PixelImage>("BgView").Source);
        Assert.Contains("Tileset specific", w.GetControl<TextBlock>("BgNote").Text);
    }

    /// <summary>
    /// Repointing an LG slot has to reach the pane. It did not: RefreshBg pushed the new pixels
    /// into the SAME WriteableBitmap and re-assigned it, and PixelImage.Source only repaints on
    /// a value change — so layer 3, which is one still image rather than a cycling phase set,
    /// kept drawing the tiles it loaded first.
    ///
    /// Level 009's tilemap names tiles from LG2 and LG4 only, so LG4 is the slot to move: a test
    /// on LG1 would pass without the fix, because nothing on screen references it.
    /// </summary>
    [AvaloniaFact]
    public void repointing_a_layer_3_gfx_slot_redraws_the_pane()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x009, layer3: true);          // ghost house: option 3, a real layer 3
        var session = SessionOf(w);
        var view = w.GetControl<PixelImage>("BgView");
        var before = view.Source;
        Assert.NotNull(before);

        session.SetGfxSlot(12, 0x14);               // w12 = LG4, tiles 180-1FF
        Invoke(w, "AdoptSession");
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(before, view.Source);        // a new bitmap, so the control actually draws it
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
        Assert.NotNull(w.GetControl<PixelImage>("BgView").Source);
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

    private static EditorSession SessionOf(MainWindow w) => (EditorSession)typeof(MainWindow)
        .GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .GetValue(w)!;

    private static void Invoke(MainWindow w, string method) => typeof(MainWindow)
        .GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .Invoke(w, null);
}
