using System.Diagnostics;
using Avalonia.Headless.XUnit;
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
}
