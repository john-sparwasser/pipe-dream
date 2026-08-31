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
    public void a_level_with_no_layer_3_says_which_option_left_it_empty()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = Open(0x105, layer3: true);

        Assert.Null(w.GetControl<PixelImage>("BgView").Source);
        Assert.Contains("Blank Layer 3", w.GetControl<TextBlock>("BgNote").Text);
    }
}
