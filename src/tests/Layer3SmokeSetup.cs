using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Builds the ROM for the layer-3 in-game smoke test. Not an assertion about the editor — it is
/// the fixture a human (or a Mesen script) then runs, kept here because it needs the same
/// project pipeline the build does and nothing else in the repo can make one headlessly.
///
/// The tilemap is a DIAGNOSTIC: every row is filled with the font glyph for its own row number
/// mod 10, so a screenshot reads back which map row landed at the top of the screen — a solid
/// colour would only have proved that SOMETHING loaded, and random VRAM looks like content.
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

    /// <summary>
    /// Builds the ROM for the prep-v14 layer-3 GFX smoke test, on a PREPPED VANILLA — the case
    /// that matters, because that is what every project here is built on.
    ///
    /// It repoints LG1, which is GFX 28, which is the status bar's own font. Any other slot
    /// would need a level whose layer 3 draws something; the status bar draws on every level,
    /// so a hit is unmissable and a miss is unambiguous. The replacement is solid colour 3, so
    /// the score and coin counters become blocks the instant the upload reaches VRAM.
    /// </summary>
    [Fact]
    public void build_a_rom_whose_status_bar_font_comes_from_a_bypassed_lg1()
    {
        if (Environment.GetEnvironmentVariable("PIPEDREAM_L3_SMOKE") is null)
        { log.WriteLine("SKIP: set PIPEDREAM_L3_SMOKE to build the smoke ROM"); return; }

        string dir = Path.Combine(Path.GetTempPath(), "pd-l3gfx");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Path.Combine(Root, ".resources", "SMW.smc")),
                    s.Status);
        Assert.True(s.Rom!.HasLmLayer3Gfx, "base was not prepped to v14");
        s.ShowLevel(BootLevel);

        string bin = Path.Combine(dir, "solid.bin");
        File.WriteAllBytes(bin, Enumerable.Repeat((byte)0xFF, 0x800).ToArray());
        var (id, why) = s.ImportGfx(bin);
        Assert.True(id >= 0, why);
        // The bypass is PER LEVEL, and a headless Mesen run cannot yet choose which level it
        // enters (reference/MESEN.md) — it lands in the title demo's. So every level the probe
        // might reach gets the same override, and whichever one loads answers the question.
        foreach (int lvl in (int[])[BootLevel, 0x0C7, 0x105, 0x024])
        {
            s.ShowLevel(lvl);
            log.WriteLine($"{lvl:X3}: {s.SetGfxSlot(15, id)}");   // w15 = LG1 = GFX 28
            s.Save();                                             // stash this level before moving on
        }

        string status = s.Build();
        log.WriteLine(status);
        string built = Path.Combine(s.Project!.Folder, "build", s.Project.Name + ".smc");
        Assert.True(File.Exists(built), status);
        var rom = Rom.Load(built);
        log.WriteLine($"built: {built}");
        log.WriteLine($"LG files: {string.Join(" ", Layer3.GfxFiles(rom, BootLevel).Select(f => f.ToString("X")))}");
        Assert.DoesNotContain("editor-only", status);
    }
}
