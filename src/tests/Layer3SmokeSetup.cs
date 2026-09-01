using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Builds the ROM for the layer-3 in-game smoke test. Not an assertion about the editor — it is
/// the fixture a human (or a Mesen script) then runs, kept here because it needs the same
/// project pipeline the build does and nothing else in the repo can make one headlessly.
///
/// The tilemap is a DIAGNOSTIC: every row is filled with the font glyph for its own row number
/// mod 10, so a screenshot reads back which map row landed at the top of the screen — which is
/// the one thing "Destination for File" decides and the one thing we have not measured
/// (CONTRACT §12b, Layer3.BuiltTilemapDestination).
///
/// Layer 3 is given PRIORITY so it draws in front: behind an opaque layer-2 background it would
/// be invisible, and "invisible" is indistinguishable from "never loaded".
///
/// Skipped unless PIPEDREAM_L3_SMOKE is set, so a normal test run does not build ROMs.
/// </summary>
public class Layer3SmokeSetup(ITestOutputHelper log)
{
    private static string Root =>
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects";

    /// <summary>The level a NEW GAME enters — $0C5, not $105 (confirmed via $010B).</summary>
    private const int BootLevel = 0x0C5;

    [Fact]
    public void build_a_rom_whose_layer_3_reads_back_its_own_row_numbers()
    {
        if (Environment.GetEnvironmentVariable("PIPEDREAM_L3_SMOKE") is null)
        { log.WriteLine("SKIP: set PIPEDREAM_L3_SMOKE to build the smoke ROM"); return; }

        string basePath = Path.Combine(Root, ".resources", "layer3", "l3_g.smc");
        string dir = Path.Combine(Path.GetTempPath(), "pd-l3smoke");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), basePath), s.Status);
        Assert.True(s.Rom!.HasLmLayer3Tilemap, "base has no layer-3 tilemap loader");
        s.ShowLevel(BootLevel);

        // Give the level a layer 3 at all (option 3), and put it in FRONT of the level.
        s.ApplyEntry(s.MainEntrance!.Value with { Layer3Option = 3 });
        s.ApplyHeader(s.Header!.Value with { Layer3Priority = 1 });
        s.ShowLevel(BootLevel);

        // Row r → the font glyph for r % 10, in palette group 2 (CGRAM 08-0B).
        var raw = new byte[0x2000];
        for (int row = 0; row < Layer3.Rows; row++)
            for (int col = 0; col < Layer3.Cols; col++)
            {
                int w = (2 << 10) | (row % 10), at = Layer3.CellIndex(col, row) * 2;
                raw[at] = (byte)w; raw[at + 1] = (byte)(w >> 8);
            }
        string map = Path.Combine(dir, "rows.bin");
        File.WriteAllBytes(map, raw);
        Assert.True(s.ImportLayer3Tilemap(map), s.Status);
        s.Save();

        string status = s.Build();
        log.WriteLine(status);
        string built = Path.Combine(s.Project!.Folder, "build", s.Project.Name + ".smc");
        Assert.True(File.Exists(built), status);

        // What the game SHOULD show, so the screenshot has something to be compared against.
        var (px, w2, h2) = s.Layer3Image();
        Png.Write(Path.Combine(dir, "expected.png"), px, w2, h2);

        var rom = Rom.Load(built);
        var bypass = rom.LmLayer3Tilemap(BootLevel);
        Assert.NotNull(bypass);
        log.WriteLine($"built: {built}");
        log.WriteLine($"expected: {Path.Combine(dir, "expected.png")}");
        log.WriteLine($"LT3 file {bypass!.Value.File:X2}, destination {bypass.Value.Destination} "
                    + $"({Layer3.TilemapDestinations[bypass.Value.Destination]}), "
                    + $"size 0x{Layer3.TilemapSizes[bypass.Value.Size]:X}");
        log.WriteLine($"option {Layer3.Option(rom, BootLevel)}, "
                    + $"priority {LevelParser.Parse(rom, BootLevel).Header.Layer3Priority}");
    }
}
