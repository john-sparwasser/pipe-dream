using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    /// <summary>The one that would have caught the clipping: the drawer must be wide enough that
    /// the sixteenth column is inside it, at whatever zoom the drawer's width has fitted.</summary>
    [AvaloniaFact]
    public void a_whole_tile_row_fits_inside_the_drawer()
    {
        if (Open() is not { } w) { log.WriteLine("SKIP: no ROM"); return; }

        var drawer = w.GetControl<Border>("Drawer");
        var palette = w.GetControl<Map16PaletteView>("Palette");

        double tiles = Map16Layout.Cols * 16 * palette.Zoom;
        log.WriteLine($"zoom {palette.Zoom}: drawer {drawer.Bounds.Width:F0}px, needs {tiles:F0}px " +
                      $"of tiles (+{Map16PaletteView.Pad * 2} pad, +chrome)");

        Assert.True(drawer.Bounds.Width >= tiles + Map16PaletteView.Pad * 2,
                    $"drawer {drawer.Bounds.Width:F0}px cannot hold a {tiles:F0}px tile row");

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
        // The two canvas modes OPEN at the same width, so switching between them does not move
        // the splitter. Only the level pane, whose tile row is genuinely wider, differs.
        Assert.Equal(map16, gfx, 1);

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

    /// <summary>The level's tile row fits the drawer like the CHR grid does: dragging the splitter
    /// wider grows the tiles, and Alt+wheel (or Cmd+wheel) over the picker is the same resize one step
    /// at a time, stopped at the pane's floor and ceiling. The plain wheel is left to scroll.</summary>
    [AvaloniaFact]
    public void the_tile_picker_fills_the_level_drawer_and_the_wheel_resizes_it()
    {
        if (Open() is not { } w) { log.WriteLine("SKIP: no ROM"); return; }
        var palette = w.GetControl<Map16PaletteView>("Palette");
        var col = w.GetControl<Grid>("Split").ColumnDefinitions[0];

        double before = palette.Zoom;
        Assert.Equal(Map16Layout.Cols * 16 * before, palette.Bounds.Width, 1);
        col.Width = new GridLength(col.Width.Value + 128);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before + 0.5, palette.Zoom, 2);                  // 128px = half a tile scale
        Assert.Equal(Map16Layout.Cols * 16 * palette.Zoom, palette.Bounds.Width, 1);

        // A plain notch scrolls, it does not resize; Alt+notch in = one step wider; out = back.
        var at = palette.TranslatePoint(new Point(8, 8), w)!.Value;
        double width = col.Width.Value;
        w.MouseWheel(at, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width, col.Width.Value, 1);
        w.MouseWheel(at, new Vector(0, 1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width + 128, col.Width.Value, 1);
        Assert.Equal(before + 1, palette.Zoom, 2);
        w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Meta);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width, col.Width.Value, 1);

        // Wheeling out forever stops at one tile per 16px; the column never goes below its floor.
        for (int i = 0; i < 20; i++) w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(col.MinWidth, col.Width.Value, 1);
        // The floor is the drawer's, so the tiles sit at the minimum plus whatever the chrome
        // allowance leaves over (the scrollbar's width spread across the row).
        Assert.InRange(palette.Zoom, Map16PaletteView.MinZoom, Map16PaletteView.MinZoom + 0.1);
        // And in forever stops at the ceiling — or short of the window, whichever comes first.
        for (int i = 0; i < 40; i++) w.MouseWheel(at, new Vector(0, 1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.True(col.Width.Value <= col.MaxWidth + 0.5, $"drawer {col.Width.Value:F0} past its ceiling {col.MaxWidth:F0}");
        Assert.True(palette.Zoom <= Map16PaletteView.MaxZoom);
        Assert.True(col.Width.Value > col.MinWidth, "the wheel never widened the drawer");
    }

    /// <summary>The GFX drawer's bin cards stretch their preview to the drawer, so there too the
    /// width is the zoom, and Alt/Cmd+wheel over the cards steps it through the same handler.</summary>
    [AvaloniaFact]
    public void alt_wheel_over_the_gfx_bins_resizes_the_drawer_and_the_previews()
    {
        if (Open() is not { } w) { log.WriteLine("SKIP: no ROM"); return; }
        w.GetControl<ToggleButton>("ModeGfx")
         .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var bins = w.GetControl<StackPanel>("GfxBins");
        var col = w.GetControl<Grid>("Split").ColumnDefinitions[0];
        var preview = bins.GetVisualDescendants().OfType<PixelImage>().First();
        var at = preview.TranslatePoint(new Point(8, 8), w)!.Value;

        double width = col.Width.Value, shown = preview.Bounds.Width;
        w.MouseWheel(at, new Vector(0, 1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width + 128, col.Width.Value, 1);
        Assert.Equal(shown + 128, preview.Bounds.Width, 1);   // the preview is the drawer's width less chrome
        w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Meta);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width, col.Width.Value, 1);
        w.MouseWheel(at, new Vector(0, -1));                  // the plain wheel scrolls, it does not resize
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width, col.Width.Value, 1);
        for (int i = 0; i < 20; i++) w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(col.MinWidth, col.Width.Value, 1);
    }

    /// <summary>The Palette drawer's swatch grid fits the drawer too: widening it (splitter or
    /// Alt/Cmd+wheel) grows the swatches, a whole row of sixteen spans the width, and the lone
    /// background swatch above keeps the same size as the ones in the grid.</summary>
    [AvaloniaFact]
    public void the_palette_grid_fills_its_drawer_and_the_wheel_resizes_it()
    {
        if (Open() is not { } w) { log.WriteLine("SKIP: no ROM"); return; }
        w.GetControl<ToggleButton>("ModePalette")
         .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var grid = w.GetControl<PaletteGridView>("PaletteGrid");
        var bg = w.GetControl<PaletteGridView>("PaletteBg");
        var col = w.GetControl<Grid>("Split").ColumnDefinitions[0];

        // Sixteen swatches plus the numbered header band span the drawer exactly.
        Assert.Equal(grid.Cols * grid.Cell + grid.HeaderSize, grid.Bounds.Width, 1);
        Assert.True(grid.HeaderSize > 0);
        Assert.Equal(grid.Cell, bg.Cell, 1);
        double cell = grid.Cell, width = col.Width.Value;
        var at = grid.TranslatePoint(new Point(8, 8), w)!.Value;
        w.MouseWheel(at, new Vector(0, 1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width + 128, col.Width.Value, 1);
        Assert.Equal(cell + 128.0 / (grid.Cols + 0.6), grid.Cell, 1);
        Assert.Equal(grid.Cols * grid.Cell + grid.HeaderSize, grid.Bounds.Width, 1);
        // The header is not a swatch: a click in it picks nothing, one past it picks (row 0, col 0).
        Assert.Null(grid.IndexAt(new Point(grid.HeaderSize / 2, grid.HeaderSize + cell)));
        Assert.Equal(0x10, grid.IndexAt(new Point(grid.HeaderSize + 1, grid.HeaderSize + grid.Cell + 1)));
        Assert.Equal(grid.Cell, bg.Cell, 1);
        w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Meta);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(cell, grid.Cell, 1);
    }

    /// <summary>The Map16 drawer's CHR grid goes the other way round from the level's tile row:
    /// instead of the content setting the width, the width sets the tile size. A whole row of 16
    /// spans the drawer at any width, and widening it with the splitter grows the tiles.</summary>
    [AvaloniaFact]
    public void the_chr_grid_fills_the_map16_drawer_at_any_width()
    {
        if (Open() is not { } w) { log.WriteLine("SKIP: no ROM"); return; }
        w.GetControl<ToggleButton>("ModeMap16")
         .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var chr = w.GetControl<ChrPaletteView>("Chr");
        var col = w.GetControl<Grid>("Split").ColumnDefinitions[0];

        double fitted = ChrPaletteView.Cols * 8 * chr.Zoom;
        Assert.Equal(fitted, chr.Bounds.Width, 1);        // the row spans exactly what it was given
        log.WriteLine($"drawer {col.Width.Value:F0}px: CHR grid {chr.Bounds.Width:F0}px at {chr.Zoom:F2}x");

        double before = chr.Zoom;
        col.Width = new GridLength(col.Width.Value + 160);
        Dispatcher.UIThread.RunJobs();

        Assert.True(chr.Zoom > before, $"tiles stayed at {before:F2}x in a drawer 160px wider");
        Assert.Equal(ChrPaletteView.Cols * 8 * chr.Zoom, chr.Bounds.Width, 1);

        // Alt/Cmd+wheel over the grid is the same resize, one step at a time, through the one
        // drawer handler every fitting sheet shares — and it stops at the pane's floor and ceiling.
        var at = chr.TranslatePoint(new Point(8, 8), w)!.Value;
        double width = col.Width.Value, zoom = chr.Zoom;
        w.MouseWheel(at, new Vector(0, 1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width + 128, col.Width.Value, 1);
        Assert.Equal(zoom + 1, chr.Zoom, 2);                  // 128px is a whole 1x of a 128px-wide grid
        w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Meta);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width, col.Width.Value, 1);
        w.MouseWheel(at, new Vector(0, -1));                  // the plain wheel is a scroll, not a resize
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width, col.Width.Value, 1);
        for (int i = 0; i < 20; i++) w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(col.MinWidth, col.Width.Value, 1);
        for (int i = 0; i < 40; i++) w.MouseWheel(at, new Vector(0, 1), RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.True(col.Width.Value <= col.MaxWidth + 0.5 && col.Width.Value > col.MinWidth);
    }
}
