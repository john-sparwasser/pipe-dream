using Xunit;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Switch blocks are drawn with every switch palace pressed, as Lunar Magic draws them: the objects lay the solid
/// page-1 blocks (16A-16D) when `$1F27,X` is set and the dotted page-0 outline (06A-06D) when not — in the emulated
/// loader and the hand-ported fallback alike. 103's floor of ! blocks was all outline here.
/// </summary>
public class SwitchBlockTests
{
    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    [Fact]
    public void switch_blocks_are_drawn_pressed()
    {
        if (!File.Exists(Vanilla)) return;
        var rom = Rom.Load(Vanilla);
        var lv = LevelParser.Parse(rom, 0x103);
        foreach (var g in new[] { ObjectEngine.Render(rom, lv), PortedObjectEngine.Render(rom, lv.Header, lv.Objects) })
        {
            var tiles = Enumerable.Range(0, g.Height).SelectMany(y => Enumerable.Range(0, g.Width).Select(x => g.Get(x, y))).ToList();
            Assert.Contains(tiles, t => t is >= 0x16A and <= 0x16D);
            Assert.DoesNotContain(tiles, t => t is >= 0x6A and <= 0x6D);
        }
    }
}
