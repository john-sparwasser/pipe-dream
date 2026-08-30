using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PipeDream.Ui;
using Xunit;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Phase-0 spike (b): can a UI change be verified without a human clicking?
///
/// These drive a real Avalonia window headlessly — real layout, real hit-testing, real
/// pointer input — and assert on editor state. Every UI defect this project has hit was of
/// exactly this shape ("clicking here does nothing", "this region draws nothing"), and none
/// of them were reachable by any existing test.
/// </summary>
public class LevelViewTests
{
    private static (Window W, LevelView V) Show(int pxW = 512, int pxH = 432)
    {
        var view = new LevelView { Zoom = 2.0, Source = FakeLevel(pxW, pxH) };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        // Layout is not automatic in a headless frame; force it so Bounds are real.
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    /// <summary>A composed level stands in as a solid image of the right size — these tests
    /// are about geometry and input, not pixels.</summary>
    private static LevelBitmap FakeLevel(int w, int h)
    {
        var phases = new uint[4][];
        for (int p = 0; p < 4; p++)
        {
            phases[p] = new uint[w * h];
            Array.Fill(phases[p], 0xFF3366CCu);
        }
        var bmp = new LevelBitmap();
        bmp.SetImages(phases, w, h, 0);
        return bmp;
    }

    [AvaloniaFact]
    public void a_click_lands_on_the_cell_under_the_cursor()
    {
        var (window, view) = Show();

        // Cell (3, 2) at zoom 2 spans screen x 96..127, y 64..95 — aim at its middle.
        window.MouseDown(new Point(3 * 16 * 2 + 16, 2 * 16 * 2 + 16), MouseButton.Left);
        window.MouseUp(new Point(3 * 16 * 2 + 16, 2 * 16 * 2 + 16), MouseButton.Left);

        Assert.Equal((3, 2), view.LastClickedCell);
    }

    [AvaloniaFact]
    public void zoom_changes_which_cell_a_screen_point_hits()
    {
        var (window, view) = Show();
        var at = new Point(100, 100);

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Assert.Equal((3, 3), view.LastClickedCell);          // 100/2/16 = 3

        view.Zoom = 4.0;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Assert.Equal((1, 1), view.LastClickedCell);          // 100/4/16 = 1
    }

    [AvaloniaFact]
    public void scrolling_offsets_hit_testing_by_the_same_amount()
    {
        var (window, view) = Show();
        view.Origin = new Point(16 * 2 * 5, 0);              // scrolled right by 5 cells

        window.MouseDown(new Point(16, 16), MouseButton.Left);
        window.MouseUp(new Point(16, 16), MouseButton.Left);

        Assert.Equal((5, 0), view.LastClickedCell);
    }

    /// <summary>Zoomed out far enough to fit an axis, the level is centred on it: a horizontal
    /// level sits in the middle vertically, a vertical one in the middle horizontally. The axis
    /// that still overflows must stay at the origin, or its first screenful is unreachable.</summary>
    [AvaloniaFact]
    public void a_level_that_fits_an_axis_is_centred_on_it()
    {
        var (wide, wideView) = InScroller(pxW: 2048, pxH: 432);      // horizontal level
        var at = wideView.TranslatePoint(default, wide)!.Value;
        Assert.Equal(0, at.X, 1);
        Assert.Equal((wide.Viewport.Height - 432) / 2, at.Y, 1);

        var (tall, tallView) = InScroller(pxW: 256, pxH: 2048);      // vertical level
        at = tallView.TranslatePoint(default, tall)!.Value;
        Assert.Equal((tall.Viewport.Width - 256) / 2, at.X, 1);
        Assert.Equal(0, at.Y, 1);
    }

    /// <summary>The canvas as the app hosts it — inside the scroll viewer, at zoom 1 so the
    /// pixel sizes above are the layout sizes.</summary>
    private static (ScrollViewer S, LevelView V) InScroller(int pxW, int pxH)
    {
        var view = new LevelView { Zoom = 1.0, Source = FakeLevel(pxW, pxH) };
        var scroller = new ScrollViewer
        {
            Content = view,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        new Window { Width = 800, Height = 600, Content = scroller }.Show();
        Dispatcher.UIThread.RunJobs();
        return (scroller, view);
    }

    [AvaloniaFact]
    public void clicking_past_the_end_of_the_level_selects_nothing()
    {
        var (window, view) = Show(pxW: 64, pxH: 64);         // tiny level, big window

        window.MouseDown(new Point(700, 500), MouseButton.Left);
        window.MouseUp(new Point(700, 500), MouseButton.Left);

        Assert.Null(view.LastClickedCell);
    }

    /// <summary>The desk behind the level: the ImGui editor's diamond backdrop, as a 32x32
    /// tile. Pin the tile's geometry — lighter-grey diamond (centre and edge midpoints) on the
    /// dark base (corners) — since a wrong predicate still "renders fine", just blank.</summary>
    [AvaloniaFact]
    public void the_desk_pattern_is_lighter_diamonds_on_dark_grey()
    {
        var brush = Assert.IsType<Avalonia.Media.ImageBrush>(UiColors.DeskPattern);
        Assert.Equal(Avalonia.Media.TileMode.Tile, brush.TileMode);
        Assert.NotNull(brush.Source);

        var px = UiColors.DeskTile();
        Assert.Equal(32 * 32, px.Length);
        Assert.Equal(0xFF1B1B1Bu, px[16 * 32 + 16]);   // diamond centre
        Assert.Equal(0xFF1B1B1Bu, px[0 * 32 + 16]);    // edge midpoint — diamonds touch tips
        Assert.Equal(0xFF101010u, px[0 * 32 + 0]);     // corner — base grey between diamonds
        Assert.Equal(0xFF101010u, px[8 * 32 + 2]);     // off-diamond interior
    }

    [AvaloniaFact]
    public void the_view_renders_without_a_gpu()
    {
        var (window, view) = Show();
        // Rendering must not throw with a headless drawing backend — this is what makes a
        // whole-editor smoke ("open a project, switch modes, nothing explodes") possible.
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.Bounds.Width > 0);
        Assert.NotNull(view.Source!.For(0));
    }
}

public class EntranceMarkerTests
{
    /// <summary>The marker is Mario's 16x32 box; picking it up anywhere inside and letting go
    /// drops him on a whole cell, whatever pixel the cursor is over — the drop is snapped as
    /// it moves, so the dragged marker and the stored position never disagree.</summary>
    [AvaloniaFact]
    public void dragging_an_entrance_drops_it_on_a_cell()
    {
        var view = new LevelView { Zoom = 2.0, Mode = LevelView.EditMode.Entrances };
        var phases = new uint[4][];
        for (int p = 0; p < 4; p++) { phases[p] = new uint[512 * 432]; Array.Fill(phases[p], 0xFF3366CCu); }
        var bmp = new LevelBitmap(); bmp.SetImages(phases, 512, 432, 0);
        view.Source = bmp;
        view.Entrances = [new Services.LevelEntrance(Services.EntranceKind.Main, 0, 64, 320) { Free = true }];
        var window = new Window { Width = 800, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        (Services.EntranceKind Kind, int Index, int X, int Y)? dropped = null;
        view.EntranceMoved += (_, m) => dropped = m;

        // Grab Mario by his knees (12px in, 24px down) and let go 37x53 level pixels away.
        var grab = new Point((64 + 12) * 2, (320 + 24) * 2);
        window.MouseDown(grab, MouseButton.Left);
        window.MouseMove(grab + new Vector(37 * 2, -53 * 2));
        window.MouseUp(grab + new Vector(37 * 2, -53 * 2), MouseButton.Left);

        Assert.NotNull(dropped);
        Assert.Equal((96, 272), (dropped!.Value.X, dropped.Value.Y));       // 101→96, 267→272: nearest cells
    }
}

public class EntranceEditBadgeTests
{
    /// <summary>Hovering a marker grows an edit badge after its label; clicking the badge asks the
    /// host to open that entrance's settings instead of starting a drag. A double-click on the
    /// marker itself does the same.</summary>
    [AvaloniaFact]
    public void hovering_a_marker_offers_an_edit_badge_that_opens_the_entrance()
    {
        var view = new LevelView { Zoom = 2.0, Mode = LevelView.EditMode.Entrances };
        var phases = new uint[4][];
        for (int p = 0; p < 4; p++) { phases[p] = new uint[512 * 432]; Array.Fill(phases[p], 0xFF3366CCu); }
        var bmp = new LevelBitmap(); bmp.SetImages(phases, 512, 432, 0);
        view.Source = bmp;
        var mid = new Services.LevelEntrance(Services.EntranceKind.Midway, 0, 64, 320) { Free = true };
        view.Entrances = [mid];
        var window = new Window { Width = 800, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var asked = new List<Services.LevelEntrance>();
        view.EntranceEditRequested += (_, e) => asked.Add(e);
        int moved = 0;
        view.EntranceMoved += (_, _) => moved++;

        // Not hovering: nothing after the label is clickable.
        window.MouseDown(new Point(400, 100), MouseButton.Left);
        window.MouseUp(new Point(400, 100), MouseButton.Left);
        Assert.Empty(asked);

        // Hover the marker, render, then read where the badge landed and click it.
        window.MouseMove(new Point((64 + 8) * 2, (320 + 16) * 2));
        Dispatcher.UIThread.RunJobs();
        var badge = view.EditBadges.Single();
        Assert.Equal(mid, badge.E);
        var at = view.TranslatePoint(badge.Box.Center, window)!.Value;   // badge is in view coords
        window.MouseMove(at);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(view.EditBadges);                                    // still offered while over the badge itself
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Assert.Equal([mid], asked);
        Assert.Equal(0, moved);

        // Double-click on Mario himself.
        var on = new Point((64 + 8) * 2, (320 + 16) * 2);
        window.MouseDown(on, MouseButton.Left); window.MouseUp(on, MouseButton.Left);
        window.MouseDown(on, MouseButton.Left); window.MouseUp(on, MouseButton.Left);
        Assert.Equal(2, asked.Count);
    }
}
