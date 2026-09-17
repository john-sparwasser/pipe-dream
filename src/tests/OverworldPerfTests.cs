using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Tests;

/// <summary>
/// How long the overworld canvas takes to compose, in the numbers the window sees: every 8x8
/// cell of the Tiles canvas asked for its land and its overlay, the way TilemapView.Compose asks.
/// Logged, not asserted — a timing assertion is flaky on a loaded CI box — but it is what the
/// optimisation work was measured against, so it stays runnable.
/// </summary>
public class OverworldPerfTests(ITestOutputHelper log)
{
    [RealRomFact]
    public void compose_timings()
    {
        var s = new EditorSession();
        Assert.True(s.OpenRom(TestRom.RealRomPath));
        Assert.NotNull(s.Overworld);
        Assert.NotNull(s.OwMap);
        int cols = EditorSession.Ow8Cols, rows = s.Ow8VisibleRows;
        log.WriteLine($"canvas {cols}x{rows} = {cols * rows} cells");

        long Pass(string what)
        {
            var sw = Stopwatch.StartNew();
            int drawn = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    if (s.Ow8CellPixels(r * cols + c) is not null) drawn++;
                    s.Ow8Overlay(c, r, layer1: true, paths: true);
                }
            sw.Stop();
            log.WriteLine($"{what,-28} {sw.ElapsedMilliseconds,5} ms  ({drawn} cells drawn)");
            return sw.ElapsedMilliseconds;
        }

        Pass("cold");
        Pass("warm");
        Pass("warm");
        var ow = s.Overworld!;
        for (int i = 0; i < 3; i++)
        {
            ow.Animate(ow.AnimationCounter + 8);
            Pass("after Animate (one tick)");
        }

        // What a tick actually redraws now: the cells whose picture moves, not the canvas.
        var sw = Stopwatch.StartNew();
        int moving = 0;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++) if (s.OwCellAnimated(c, r)) moving++;
        sw.Stop();
        log.WriteLine($"cells a tick redraws: {moving} of {cols * rows} (found in {sw.ElapsedMilliseconds} ms)");
        Assert.InRange(moving, 1, cols * rows / 4);      // the sea is water and water moves; the land does not
    }
}
