using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.Controls;
using PipeDream.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Phase-0 spike (a): what does the level canvas actually cost under Avalonia?
///
/// The migration's whole bet is that the expensive part — composing Map16 tiles into RGBA on
/// the CPU — is framework-agnostic and carries over untouched, leaving only the upload to be
/// re-done. These measure both halves against a real ROM and a real level, so the bet is
/// priced before any of the UI is rewritten rather than after.
///
/// A full-width SMW level is ~8192x432 = 14MB per animation phase, which is the number that
/// decides whether a per-repaint copy is viable at all.
/// </summary>
public class CanvasCostTests(ITestOutputHelper log)
{
    private static string RomPath => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static bool HaveRom => File.Exists(RomPath);

    [AvaloniaFact]
    public void a_real_level_composes_and_uploads_within_budget()
    {
        if (!HaveRom) { log.WriteLine($"SKIP: no ROM at {RomPath}"); return; }

        var rom = Rom.Load(RomPath);

        var sw = Stopwatch.StartNew();
        var scene = LevelScene.Build(rom, 0x105);
        double compose = sw.Elapsed.TotalMilliseconds;

        var bmp = new LevelBitmap();
        sw.Restart();
        bmp.SetImages(scene.Phases, scene.Width, scene.Height, 0);
        double firstUpload = sw.Elapsed.TotalMilliseconds;

        // Steady state: new pixels arrive and reach the bitmap, with it already allocated at
        // the right size. SetImages performs the copy for the visible phase, so IT is the
        // thing to time — calling Refresh afterwards measures nothing, because the phase is
        // no longer stale.
        var repaints = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            sw.Restart();
            bmp.SetImages(scene.Phases, scene.Width, scene.Height, 0);
            repaints.Add(sw.Elapsed.TotalMilliseconds);
        }
        repaints.Sort();
        double median = repaints[repaints.Count / 2];   // median to shrug off GC

        double mb = scene.Width * (double)scene.Height * 4 / (1024 * 1024);
        log.WriteLine($"level $105: {scene.Width}x{scene.Height}px = {mb:F1}MB/phase");
        log.WriteLine($"  compose 4 phases : {compose:F1}ms   (core work, unchanged by the migration)");
        log.WriteLine($"  first upload     : {firstUpload:F1}ms  (allocates the bitmap)");
        log.WriteLine($"  repaint (median) : {median:F2}ms  <- the per-frame cost Avalonia adds");

        Assert.True(scene.Width > 0 && scene.Height > 0);
        // A repaint must fit comfortably inside a 16.6ms frame, with room for everything else.
        Assert.True(median < 8.0, $"repaint {median:F2}ms is too slow for 60fps");
    }

    /// <summary>The widest level SMW can express is the real worst case; if that repaints in
    /// budget, nothing else will be a problem.</summary>
    [AvaloniaFact]
    public void the_widest_level_still_repaints_in_budget()
    {
        if (!HaveRom) { log.WriteLine($"SKIP: no ROM at {RomPath}"); return; }

        var rom = Rom.Load(RomPath);
        int widest = 0, wpx = 0, hpx = 0;
        LevelScene? worst = null;
        foreach (int lv in new[] { 0x105, 0x0C5, 0x101, 0x104, 0x106, 0x11B })
        {
            try
            {
                var s = LevelScene.Build(rom, lv);
                if (s.Width > wpx) { worst = s; wpx = s.Width; hpx = s.Height; widest = lv; }
            }
            catch { /* not every level number is a normal level */ }
        }
        Assert.NotNull(worst);

        var bmp = new LevelBitmap();
        bmp.SetImages(worst!.Phases, wpx, hpx, 0);
        var sw = new Stopwatch();
        var times = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            sw.Restart();
            bmp.SetImages(worst.Phases, wpx, hpx, 0);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        double median = times[times.Count / 2];
        log.WriteLine($"widest sampled level ${widest:X3}: {wpx}x{hpx}px " +
                      $"({wpx * (double)hpx * 4 / (1024 * 1024):F1}MB) repaint {median:F2}ms");
        Assert.True(median < 8.0, $"repaint {median:F2}ms is too slow for 60fps");
    }

    /// <summary>
    /// What sharp-bilinear is allowed to cost. A fractional zoom draws twice — nearest into an
    /// intermediate, then one filtered step down — and two viewport-sized blits are nothing. The
    /// two ways that stops being true are the ones pinned here:
    ///
    ///   * a LEVEL-sized intermediate. This level is 4096px wide; at 210% the whole thing tripled
    ///     would be 12288x1296 = 63MB and a new surface every repaint.
    ///   * REALLOCATING it per frame. A GPU surface per repaint is a stall no matter its size.
    ///
    /// Frame TIME cannot be measured here: the headless renderer records draw calls and never
    /// rasterizes (its render target refuses CopyPixels), so a stopwatch around Render reads 0.01ms
    /// whatever it draws. The shape is what is checkable, and the shape is what decides the cost.
    /// </summary>
    [AvaloniaFact]
    public void a_fractional_zoom_scales_only_the_viewport_and_reuses_the_surface()
    {
        if (!HaveRom) { log.WriteLine($"SKIP: no ROM at {RomPath}"); return; }

        var scene = LevelScene.Build(Rom.Load(RomPath), 0x105);
        var bmp = new LevelBitmap();
        bmp.SetImages(scene.Phases, scene.Width, scene.Height, 0);

        const int vw = 1280, vh = 720;
        var view = new LevelView { Source = bmp, Zoom = 2.1 };
        var w = new Window { Width = vw, Height = vh, Content = view };
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        using var surface = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new PixelSize(vw, vh), new Avalonia.Vector(96, 96));
        var targets = new List<int>();
        for (int i = 0; i < 12; i++)
        {
            using (var dc = surface.CreateDrawingContext())
                view.Render(dc);
            targets.Add(view.ScalerTarget);
        }

        var size = view.ScalerSize;
        log.WriteLine($"level {scene.Width}x{scene.Height}px at 210% in a {vw}x{vh} viewport: " +
                      $"intermediate {size.Width}x{size.Height} " +
                      $"({size.Width * (long)size.Height * 4 / (1024 * 1024)}MB), " +
                      $"{view.ScalerBuilds} allocation(s) over 12 repaints");

        // The intermediate covers the VIEWPORT (rounded out to whole source pixels at the next
        // whole multiple), never the level. Three source pixels per screen pixel is the ceiling at
        // this zoom, so twice the viewport plus a pixel of slop is the honest bound.
        Assert.True(size.Width > 0, "the sharp-bilinear path never ran");
        Assert.True(size.Width <= vw * 2 + 8 && size.Height <= vh * 2 + 8,
                    $"intermediate {size.Width}x{size.Height} is not viewport-sized");
        Assert.True(size.Width < scene.Width, "the whole level went through the scaler");
        // Three surfaces — the oversampled one, and the PAIR of device-sized ones the filtered
        // step alternates between — each built once and reused for every repaint.
        Assert.Equal(3, view.ScalerBuilds);
        Assert.Equal("sharp", view.LastDraw);

        // The pair has to alternate. Drawing into the same target every repaint records an
        // identical draw, which the compositor drops — so a repaint whose only change is INSIDE
        // the bitmap (a phase of tile animation, an edit at a fractional zoom) never appears.
        Assert.Equal(12, targets.Count);
        for (int i = 1; i < targets.Count; i++)
            Assert.NotEqual(targets[i - 1], targets[i]);
    }

    /// <summary>
    /// A repaint with no usable viewport still draws SHARP. Coming back from another canvas mode,
    /// the first repaint can land while the level canvas is still marked invisible, and the layout
    /// then cannot say which part of it is on screen. Treating that as "all of it" sized the
    /// intermediate off the whole 8192px level, blew the cap, and silently dropped the draw to the
    /// blurry one-step filter — where it stayed until something else forced another repaint. That is
    /// what "it goes back to the old rendering when I tab away and back" was.
    /// </summary>
    [AvaloniaFact]
    public void a_repaint_with_no_viewport_still_draws_sharp()
    {
        if (!HaveRom) { log.WriteLine($"SKIP: no ROM at {RomPath}"); return; }

        var scene = LevelScene.Build(Rom.Load(RomPath), 0x105);
        var bmp = new LevelBitmap();
        bmp.SetImages(scene.Phases, scene.Width, scene.Height, 0);

        // No ScrollViewer at all: the worst case of "the layout cannot tell me where I am".
        var view = new LevelView { Source = bmp, Zoom = 2.1 };
        var w = new Window { Width = 1280, Height = 720, Content = view };
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        using var surface = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new PixelSize(1280, 720), new Avalonia.Vector(96, 96));
        using (var dc = surface.CreateDrawingContext()) view.Render(dc);

        log.WriteLine($"no viewport: draw={view.LastDraw} intermediate {view.ScalerSize}");
        Assert.Equal("sharp", view.LastDraw);
        Assert.True(view.ScalerSize.Width < scene.Width, "it sized the intermediate off the level");
    }
}
