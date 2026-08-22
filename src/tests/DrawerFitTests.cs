using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PipeDream.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The drawer holds a WHOLE row of Map16 tiles at every tile size.
///
/// A hardcoded drawer width clipped the right-hand columns, and clipping does not look like a
/// layout bug from the outside — it looks like the editor is missing tiles. Deriving the width
/// from the tile geometry only helps if the chrome allowance is right too, so these measure
/// the real laid-out window rather than trusting the constant.
/// </summary>
public class DrawerFitTests(ITestOutputHelper log)
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

    [Fact]
    public void the_content_width_is_a_full_tile_row_plus_padding()
    {
        // 16 columns of 16px tiles, plus the margin on both sides.
        Assert.Equal(16 * 16 * 2 + 16, Map16PaletteView.ContentWidth(2));
        Assert.Equal(16 * 16 * 1 + 16, Map16PaletteView.ContentWidth(1));
        Assert.Equal(16 * 16 * 4 + 16, Map16PaletteView.ContentWidth(4));
    }

    /// <summary>The one that would have caught the clipping: at every tile size the drawer
    /// must be wide enough that the sixteenth column is inside it.</summary>
    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void every_tile_size_fits_a_whole_row_inside_the_drawer(double zoom)
    {
        if (Open() is not { } w) { log.WriteLine("SKIP: no ROM"); return; }

        w.GetControl<Slider>("TileZoom").Value = zoom;
        Dispatcher.UIThread.RunJobs();
        w.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();

        var drawer = w.GetControl<Border>("Drawer");
        var palette = w.GetControl<Map16PaletteView>("Palette");

        double tiles = Map16Layout.Cols * 16 * palette.Zoom;
        log.WriteLine($"zoom {zoom}: drawer {drawer.Bounds.Width:F0}px, needs {tiles:F0}px of tiles " +
                      $"(+{Map16PaletteView.Pad * 2} pad, +chrome)");

        Assert.True(drawer.Bounds.Width >= tiles + Map16PaletteView.Pad * 2,
                    $"drawer {drawer.Bounds.Width:F0}px cannot hold a {tiles:F0}px tile row at zoom {zoom}");

        // And the palette itself was actually given that room, not just the drawer.
        Assert.True(palette.Bounds.Width >= tiles - 0.5,
                    $"palette got {palette.Bounds.Width:F0}px for {tiles:F0}px of tiles");
    }

    /// <summary>Each canvas mode's drawer holds different content — a Map16 tile row, an 8x8 CHR
    /// grid, ten GFX bin cards — so each keeps its own width. One shared width either clips the
    /// tiles or spends half the window on cards, and a splitter drag in one mode must not drag
    /// the other two with it.</summary>
    [AvaloniaFact]
    public void each_canvas_mode_keeps_its_own_drawer_width()
    {
        if (Open() is not { } w) { log.WriteLine("SKIP: no ROM"); return; }
        var col = w.GetControl<Grid>("Split").ColumnDefinitions[0];
        void Mode(string name) => w.GetControl<ToggleButton>(name)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        double level = col.Width.Value;
        Mode("ModeGfx");
        Dispatcher.UIThread.RunJobs();
        double gfx = col.Width.Value;
        Assert.Equal(col.MinWidth, gfx);                  // first visit: exactly its content width
        Assert.True(gfx < level, $"graphics drawer {gfx:F0} should be narrower than level's {level:F0}");

        Mode("ModeMap16");
        Dispatcher.UIThread.RunJobs();
        double map16 = col.Width.Value;
        Assert.True(map16 < level, $"Map16 drawer {map16:F0} should be narrower than level's {level:F0}");
        log.WriteLine($"level {level:F0}px, Map16 {map16:F0}px, graphics {gfx:F0}px");

        // Widen Map16's, then walk the modes: only Map16 remembers the drag.
        col.Width = new GridLength(map16 + 150);
        Mode("ModeLevel");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(level, col.Width.Value);
        Mode("ModeGfx");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(gfx, col.Width.Value);
        Mode("ModeMap16");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(map16 + 150, col.Width.Value);
    }

    /// <summary>Growing the tile size widens the drawer; the splitter can still make it wider
    /// than the minimum, and that extra width is kept.</summary>
    [AvaloniaFact]
    public void the_drawer_grows_with_the_tile_size_and_keeps_a_manual_widening()
    {
        if (Open() is not { } w) { log.WriteLine("SKIP: no ROM"); return; }
        var split = w.GetControl<Grid>("Split");
        var zoomSlider = w.GetControl<Slider>("TileZoom");

        zoomSlider.Value = 1;
        Dispatcher.UIThread.RunJobs();
        double atOne = split.ColumnDefinitions[0].MinWidth;

        zoomSlider.Value = 3;
        Dispatcher.UIThread.RunJobs();
        Assert.True(split.ColumnDefinitions[0].MinWidth > atOne, "bigger tiles did not widen the drawer");

        // A manual drag past the minimum survives — the fit only raises the floor.
        double wide = split.ColumnDefinitions[0].MinWidth + 200;
        split.ColumnDefinitions[0].Width = new GridLength(wide);
        zoomSlider.Value = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(wide, split.ColumnDefinitions[0].Width.Value);
    }
}
