using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Tests;

public class PlayerGfxTests(ITestOutputHelper log)
{
    /// <summary>Big Mario standing, decoded from the ROM the way $00F636/$00A300 fetch him: a
    /// 16x32 sprite that is opaque where Mario is and clear where he is not — the shape the
    /// entrance marker shows. Dumps a PNG beside the test output for eyeballing.</summary>
    [RealRomFact]
    public void big_mario_standing_comes_out_of_the_player_sheet()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var pal = Palette.Load(rom, LevelParser.Parse(rom, 0x105).Header);
        var px = PlayerGfx.BigMarioStanding(rom, pal);
        Assert.NotNull(px);
        Assert.Equal(16 * 32, px!.Length);
        string outPath = Path.Combine(Path.GetTempPath(), "pipe-dream-mario.png");
        Png.Write(outPath, px, 16, 32);
        log.WriteLine(outPath);
        for (int y = 0; y < 32; y++)
            log.WriteLine(string.Concat(Enumerable.Range(0, 16).Select(x => px[y * 16 + x] == 0 ? '.' : '#')));
        Assert.Contains(px.Skip(16 * 30), c => c != 0);                                // feet on the floor
        Assert.All(new[] { px[0], px[15] }, c => Assert.Equal(0u, c));                 // air at the top corners
        Assert.True(px.Count(c => c != 0) > 16 * 32 / 3);                               // mostly Mario
    }
}
