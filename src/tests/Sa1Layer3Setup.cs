using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Builds the pair of ROMs for the SA-1 layer-3 check (reference/SA1_PORT.md): level $105 with all
/// three layer-3 bypasses, through the project pipeline, then built again with ASM ▸ SA-1 on
/// (<see cref="EditorSession.SetSa1"/>). A Mesen script then reads each back inside one run:
///
///  • LG2 is a file whose byte i is (i*7+3) &amp; 0xFF, so VRAM at its window can be checked against
///    the formula without trusting anything else in VRAM;
///  • the layer-3 tilemap is the row-number diagnostic Layer3SmokeSetup uses (row r → glyph r % 10,
///    palette 2), counted in VRAM;
///  • both scroll axes are "Fast", the rate that uses the SNES divider: position = offset + camera +
///    camera / 5, checked against the camera in the same frame.
///
/// A second pair (owl3-*.smc) gives Yoshi's Island an overworld tilemap bypass of GFX 28, whose
/// hash the log prints, to compare with VRAM word $3000 on the map.
///
/// Skipped unless PIPEDREAM_SA1_L3 names the output folder.
/// </summary>
public class Sa1Layer3Setup(ITestOutputHelper log)
{
    [Fact]
    public async Task build_the_layer_3_pair_for_the_sa1_check()
    {
        if (Environment.GetEnvironmentVariable("PIPEDREAM_SA1_L3") is not { } dir)
        { log.WriteLine("SKIP: set PIPEDREAM_SA1_L3 to an output folder"); return; }
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        s.ShowLevel(0x105);
        s.ApplyEntry(s.MainEntrance!.Value with { Layer3Option = 3 });
        s.ShowLevel(0x105);

        string pattern = Path.Combine(dir, "pattern.bin");
        File.WriteAllBytes(pattern, Enumerable.Range(0, 0x800).Select(i => (byte)((i * 7 + 3) & 0xFF)).ToArray());
        var (id, why) = s.ImportGfx(pattern);
        Assert.True(id >= 0, why);
        log.WriteLine(s.SetGfxSlot(14, id));                     // w14 = LG2, VRAM word $4400

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

        int fast = Array.IndexOf(Layer3.VScrollNames, "Fast");
        Assert.True(s.ApplyLayer3Advanced(new Layer3.Advanced(false, false, false, fast, fast, 1, 2)), s.Status);
        s.Save();

        string built = Path.Combine(s.Project!.Folder, "build", s.Project.Name + ".smc");
        log.WriteLine(s.Build());
        File.Copy(built, Path.Combine(dir, "lorom.smc"));
        Assert.True(TestConfigDir.SeedSa1(), "no SA-1 Pack beside the oracle ROMs");
        log.WriteLine(await s.SetSa1(true));
        log.WriteLine(s.Build());
        File.Copy(built, Path.Combine(dir, "sa1.smc"));

        log.WriteLine($"prep v{s.Project.Data.BaseRom.PrepVersion}; LG2 = ExGFX {id:X3}; scroll code {Layer3.ScrollCodes[fast]:X2}");
        foreach (var name in (string[])["lorom.smc", "sa1.smc"])
        {
            var r = Rom.Load(Path.Combine(dir, name));
            log.WriteLine($"{name}: map mode {r.Map.GetType().Name}; LM layer-3 GFX {r.HasLmLayer3Gfx}, tilemap {r.HasLmLayer3Tilemap}, advanced {r.HasLmLayer3Advanced}");
        }

        // The overworld's tilemap bypass: Yoshi's Island (submap 1, where a new game lands) gets
        // GFX 28 as its layer-3 tilemap, 0x800 bytes at the start of the window (OverworldGfxTests).
        foreach (var name in (string[])["lorom", "sa1"])
        {
            var ow = Rom.Load(Path.Combine(dir, name + ".smc"));
            int fo = ow.FileOffset(ow.LmGfxBypassBase + (RomPrep.OwGfxRecordIndex + 1) * 0x20);
            ow.Data[fo + 1] |= 0x20;
            ow.Data[fo + 2] = 0x28; ow.Data[fo + 3] = 0x60;
            RatsWriter.FixChecksum(ow);
            File.WriteAllBytes(Path.Combine(dir, $"owl3-{name}.smc"), ow.Data);
        }
        int h = 0;
        foreach (byte b in Gfx.DecompressFile(Rom.Load(Path.Combine(dir, "lorom.smc")), 0x28).AsSpan(0, 0x800)) h = (h * 31 + b) % 251;
        log.WriteLine($"GFX 28's first 0x800 bytes hash {h}");
    }
}
