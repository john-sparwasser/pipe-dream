using Xunit;

namespace PipeDream.Tests;

/// <summary>Skips at discovery when the real SMW ROM is not on this machine.</summary>
public sealed class RealRomFactAttribute : FactAttribute
{
    public RealRomFactAttribute()
    {
        if (!File.Exists(TestRom.RealRomPath))
            Skip = "real ROM not present: " + TestRom.RealRomPath;
    }
}

/// <summary>
/// Thin high-value checks against a real SMW ROM. Deep integration coverage lives in
/// RomSelfCheck.cs; these only pin the basics a fresh checkout relies on.
/// </summary>
public class RealRomTests
{
    [RealRomFact]
    public void real_rom_loads_as_lorom_with_the_smw_title()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        Assert.Equal("SUPER MARIOWORLD", rom.Title);
        Assert.True(rom.IsLoRom);
        Assert.True(rom.ActualRomSize >= 0x80000);
    }

    [RealRomFact]
    public void level_105_parses_with_objects()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var level = LevelParser.Parse(rom, 0x105);
        Assert.False(level.Empty);
        Assert.NotEmpty(level.Objects);
        Assert.InRange(level.Header.Screens, 1, 32);
    }

    [RealRomFact]
    public void gfx00_decompresses_to_a_full_tile_sheet()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        byte[] gfx = Gfx.DecompressFile(rom, 0);
        // A full 128-tile file: 3072 bytes at 3bpp (vanilla) or 4096 at 4bpp (LM-saved).
        Assert.Contains(gfx.Length, new[] { 3072, 4096 });
    }
}
