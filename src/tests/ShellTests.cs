using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using PipeDream.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The shell, driven headlessly: menus, the canvas-mode toggle, the palette drawer and the
/// level canvas, with a real ROM behind them. This is the point of the migration — the
/// equivalent checks on the ImGui editor can only be made by a human clicking.
/// </summary>
public class ShellTests(ITestOutputHelper log)
{
    private static string RomPath => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static MainWindow? Open()
    {
        if (!File.Exists(RomPath)) return null;
        Program.RomPath = RomPath;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    private static T Find<T>(MainWindow w, string name) where T : Control
        => w.GetControl<T>(name);

    [AvaloniaFact]
    public void the_shell_opens_a_rom_and_renders_a_level()
    {
        if (Open() is not { } w) { log.WriteLine($"SKIP: no ROM at {RomPath}"); return; }

        var canvas = Find<LevelView>(w, "Canvas");
        Assert.NotNull(canvas.Source);
        Assert.True(canvas.Source!.HasImages, "no composed level reached the canvas");
        Assert.True(canvas.Source.PxW > 0 && canvas.Source.PxH > 0);

        var status = Find<TextBlock>(w, "Status").Text ?? "";
        log.WriteLine("status: " + status);
        Assert.Contains("level $", status);
    }

    [AvaloniaFact]
    public void the_palette_shows_tiles_and_a_click_selects_one()
    {
        if (Open() is not { } w) { log.WriteLine("SKIP: no ROM"); return; }

        var palette = Find<Map16PaletteView>(w, "Palette");
        Assert.True(palette.TileCount > 0, "the drawer got no tile sheet");

        // Row 1, column 2 of bank 0 at the default zoom.
        palette.Bank = 0;
        var at = new Point(2 * 16 * palette.Zoom + 8, 1 * 16 * palette.Zoom + 8);
        Assert.Equal(0x12, palette.TileAt(at));
    }

    /// <summary>Bank 1 must address tiles 0x2000+ — the ImGui editor shipped a version where
    /// this bank showed nothing at all, so it is worth pinning in the new UI from day one.</summary>
    [AvaloniaFact]
    public void the_palette_banks_address_different_tiles()
    {
        if (Open() is not { } w) { log.WriteLine("SKIP: no ROM"); return; }
        var palette = Find<Map16PaletteView>(w, "Palette");
        var at = new Point(8, 8);

        palette.Bank = 0;
        Assert.Equal(0x0000, palette.TileAt(at));
        palette.Bank = 1;
        Assert.Equal(0x2000, palette.TileAt(at));
    }

    [AvaloniaFact]
    public void the_canvas_mode_toggle_keeps_exactly_one_mode_active()
    {
        if (Open() is not { } w) { log.WriteLine("SKIP: no ROM"); return; }

        var modes = new[] { Find<ToggleButton>(w, "ModeLevel"), Find<ToggleButton>(w, "ModeMap16"),
                            Find<ToggleButton>(w, "ModeGfx") };
        Assert.Equal(1, modes.Count(m => m.IsChecked == true));

        // Switching editors is a canvas MODE, not another drawer panel: the drawer stays put.
        var drawer = Find<Border>(w, "Drawer");
        bool drawerWasVisible = drawer.IsVisible;
        modes[1].Command?.Execute(null);
        modes[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, modes.Count(m => m.IsChecked == true));
        Assert.True(modes[1].IsChecked);
        Assert.Equal(drawerWasVisible, drawer.IsVisible);
    }

    [AvaloniaFact]
    public void the_palette_drawer_can_be_hidden_and_the_canvas_takes_the_room()
    {
        if (Open() is not { } w) { log.WriteLine("SKIP: no ROM"); return; }
        var drawer = Find<Border>(w, "Drawer");
        var scroll = Find<ScrollViewer>(w, "CanvasScroll");
        Dispatcher.UIThread.RunJobs();
        double withDrawer = scroll.Bounds.Width;

        drawer.IsVisible = false;
        w.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();

        Assert.True(scroll.Bounds.Width > withDrawer,
                    $"canvas did not reclaim the drawer's width ({withDrawer} -> {scroll.Bounds.Width})");
    }

    /// <summary>Zoom is a PERCENT in 10% steps, stepped by - and =, and it lives in the status
    /// bar. Whole-number multipliers were the ImGui port's shortcut; Lunar Magic's zoom is a
    /// percent and jumping 100% at a time is far too coarse on a level this wide.</summary>
    [AvaloniaFact]
    public void minus_and_equals_step_the_zoom_by_ten_percent()
    {
        if (Open() is not { } w) { log.WriteLine("SKIP: no ROM"); return; }
        var canvas = Find<LevelView>(w, "Canvas");
        var slider = Find<Slider>(w, "ZoomSlider");
        var label = Find<TextBlock>(w, "ZoomLabel");
        Assert.Equal(200, slider.Value);
        Assert.Equal(2.0, canvas.Zoom);
        Assert.Equal("200%", label.Text);

        w.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(210, slider.Value);
        Assert.Equal(2.1, canvas.Zoom);
        Assert.Equal("210%", label.Text);

        w.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.None);
        w.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(190, slider.Value);
        Assert.Equal(1.9, canvas.Zoom);

        // The floor holds: - at 100% stays at 100% rather than inverting the level.
        slider.Value = slider.Minimum;
        w.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(100, slider.Value);
        Assert.Equal(1.0, canvas.Zoom);
    }

    [AvaloniaFact]
    public void clicking_the_canvas_reports_the_cell_under_the_cursor()
    {
        if (Open() is not { } w) { log.WriteLine("SKIP: no ROM"); return; }
        var canvas = Find<LevelView>(w, "Canvas");
        Dispatcher.UIThread.RunJobs();

        var pt = canvas.TranslatePoint(new Point(16 * 2 * 4 + 8, 16 * 2 * 3 + 8), w)!.Value;
        w.MouseDown(pt, MouseButton.Left);
        w.MouseUp(pt, MouseButton.Left);

        Assert.Equal((4, 3), canvas.LastClickedCell);
    }
}
