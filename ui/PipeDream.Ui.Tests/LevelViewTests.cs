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

    [AvaloniaFact]
    public void clicking_past_the_end_of_the_level_selects_nothing()
    {
        var (window, view) = Show(pxW: 64, pxH: 64);         // tiny level, big window

        window.MouseDown(new Point(700, 500), MouseButton.Left);
        window.MouseUp(new Point(700, 500), MouseButton.Left);

        Assert.Null(view.LastClickedCell);
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
