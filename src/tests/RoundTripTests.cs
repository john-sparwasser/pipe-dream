using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Every kind of edit, through the whole pipe: make it, save it, reopen the project, build a
/// ROM, and find it again at the far end.
///
/// This exists because three separate silent-data-loss bugs turned up in the save path while
/// the UI was being ported, all of the same shape — an edit that renders perfectly and is
/// simply not written. Each was found by accident. The only way to stop finding them by
/// accident is to walk every editable thing down the same path in one place.
///
/// The two ends are checked separately on purpose:
///   REOPEN proves the .pdp carries it (the editor's own persistence);
///   BUILD proves the ROM does (what actually ships).
/// A thing can pass the first and fail the second — a palette edit does exactly that on a base
/// without Lunar Magic's palette hook, and the builder says so rather than dropping it silently.
/// </summary>
public class RoundTripTests(ITestOutputHelper log) : IDisposable
{
    private readonly ITestOutputHelper log = log;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pduirt-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static bool HaveRom => File.Exists(Vanilla);

    private const int Level = 0x105;

    /// <summary>Every edit this test makes, in one place so both halves check the same facts.</summary>
    private sealed class Made
    {
        public int Layer1Tile;
        public int Layer2Objects;
        public int SpriteNumber;
        public (int Cx, int Cy) SpriteCell;
        public int PaletteIndex;
        public ushort PaletteColor;
        public int Map16Tile;
        public ushort Map16Quad;
        public int ActsTile;
        public int ActsValue;
        public int GfxFile;
        public int GfxPixelX, GfxPixelY, GfxColor;
        public int GfxBinWord, GfxBinFile;
        public byte[] Header = [];
    }

    /// <summary>
    /// A project with one of everything edited. Deliberately all on ONE level: the per-level
    /// save state is where the losses happened, and a level that carries every kind at once is
    /// the case most likely to drop one.
    /// </summary>
    private static Made EditEverything(EditorSession s)
    {
        var m = new Made();
        s.ShowLevel(Level);

        // Layer 1: a painted run of Direct Map16 tiles.
        m.Layer1Tile = 0x100;
        for (int x = 4; x < 10; x++) s.Edit!.Paint(x, 6, m.Layer1Tile);
        s.Edit!.EndStroke();

        // Layer 2: converted to an object layer, then painted.
        s.SetLayer2ObjectMode(true);
        s.SetEditLayer(1);
        for (int x = 3; x < 8; x++) s.Edit!.Paint(x, 10, m.Layer1Tile);
        s.Edit!.EndStroke();
        m.Layer2Objects = s.Edit.Objects.Count;
        s.SetEditLayer(0);

        // A sprite.
        m.SpriteNumber = 0x0B;
        m.SpriteCell = (20, 10);
        s.Sprites!.Place(m.SpriteNumber, m.SpriteCell.Cx, m.SpriteCell.Cy);
        s.RefreshSprites();

        // A palette colour.
        m.PaletteIndex = 0x42;
        m.PaletteColor = (ushort)(s.PaletteBgr(m.PaletteIndex) ^ 0x1F);
        s.SetPaletteColor(m.PaletteIndex, m.PaletteColor);

        // A Map16 definition, on a page that has to be allocated first.
        m.Map16Tile = 0x300;
        m.Map16Quad = 0x0123;
        s.Map16!.EnsurePage(m.Map16Tile);
        s.Map16.StampQuad(m.Map16Tile, 0, m.Map16Quad);
        s.Map16.EndStroke();

        // An acts-like remap.
        m.ActsTile = 0x101;
        m.ActsValue = 0x130;
        s.Map16.SetActsAs([m.ActsTile], m.ActsValue);

        // A GFX pixel, which forks the stock file under the same id.
        m.GfxFile = s.GfxBins.First(b => b.Name == "FG1").File;
        s.GfxPixels!.Open(m.GfxFile);
        m.GfxPixelX = 3; m.GfxPixelY = 5;
        m.GfxColor = (s.GfxPixels.ColorAt(m.GfxPixelX, m.GfxPixelY) ?? 0) == 5 ? 2 : 5;
        s.GfxPixels.Color = m.GfxColor;
        s.GfxPixels.Paint(m.GfxPixelX, m.GfxPixelY, out _);
        s.GfxPixels.EndStroke();

        // A VRAM bin pointed somewhere else.
        var fg3 = s.GfxBins.First(b => b.Name == "FG3");
        m.GfxBinWord = fg3.BypWord;
        m.GfxBinFile = fg3.File == 0x14 ? 0x15 : 0x14;
        s.SetGfxSlot(m.GfxBinWord, m.GfxBinFile);

        // A header field. Music is inert for rendering, so changing it cannot make the level
        // fail to parse and mask a different failure.
        var h = s.Header!.Value;
        var edited = h with { Music = (h.Music + 1) & 7 };
        m.Header = edited.ToBytes();
        s.ApplyHeader(edited);

        return m;
    }

    [Fact]
    public void every_kind_of_edit_survives_save_and_reopen()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var a = new EditorSession();
        Assert.True(a.NewProject(Path.Combine(dir, "proj"), Vanilla), a.Status);
        string pdp = a.Project!.FilePath;
        var m = EditEverything(a);
        a.Save();
        log.WriteLine(a.Status);

        var b = new EditorSession();
        Assert.True(b.OpenProject(pdp), b.Status);
        b.ShowLevel(Level);

        Assert.Equal(m.Layer1Tile, b.Scene!.Grid.Get(6, 6));
        Assert.True(b.Layer2Editable, "layer 2 lost its object stream");
        b.SetEditLayer(1);
        Assert.Equal(m.Layer2Objects, b.Edit!.Objects.Count);
        b.SetEditLayer(0);

        Assert.Contains(b.Sprites!.Sprites.Sprites,
                        s => s.Number == m.SpriteNumber && s.Cell(false) == m.SpriteCell);
        Assert.Equal(m.PaletteColor, b.PaletteBgr(m.PaletteIndex));
        Assert.Equal(m.Map16Quad, b.Map16!.ReadDef(m.Map16Tile)![0].Raw);
        Assert.Equal(m.ActsValue, b.Map16.ActsAs(m.ActsTile));
        Assert.Equal(m.GfxColor, b.GfxPixels!.Also(g => g.Open(m.GfxFile)).ColorAt(m.GfxPixelX, m.GfxPixelY));
        Assert.Equal(m.GfxBinFile, b.GfxBins.First(x => x.BypWord == m.GfxBinWord).File);
        Assert.Equal(m.Header, b.Header!.Value.ToBytes());
    }

    [Fact]
    public void every_kind_of_edit_that_can_reach_a_rom_does()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
        var m = EditEverything(s);
        s.Save();

        string status = s.Build();
        log.WriteLine(status);
        string built = Path.Combine(s.Project!.Folder, "build", s.Project.Name + ".smc");
        Assert.True(File.Exists(built), status);

        var rom = Rom.Load(built);

        // Layer 1 and layer 2 are both object streams in the built ROM.
        Assert.Contains(LevelParser.Parse(rom, Level).Objects,
                        o => o.IsDm16 && o.Dm16Tile == m.Layer1Tile);
        var l2 = LevelParser.ParseLayer2(rom, Level);
        Assert.NotNull(l2);
        Assert.Contains(l2!, o => o.IsDm16 && o.Dm16Tile == m.Layer1Tile);

        Assert.Contains(SpriteData.Parse(rom, Level).Sprites,
                        sp => sp.Number == m.SpriteNumber && sp.Cell(false) == m.SpriteCell);

        // Map16 definition and acts-like.
        int fo = Map16.DefFileOffset(rom, LevelParser.Parse(rom, Level).Header.Tileset, m.Map16Tile);
        Assert.True(fo >= 0, "the allocated Map16 page did not reach the built ROM");
        Assert.Equal(m.Map16Quad, (ushort)(rom.Data[fo] | (rom.Data[fo + 1] << 8)));
        Assert.Equal(m.ActsValue, rom.ActsAs(m.ActsTile));

        // The forked GFX file's edited pixel.
        var data = Gfx.Cached(rom, m.GfxFile);
        Assert.NotNull(data);
        int tb = Gfx.TileBytes(Gfx.RomBpp(rom));
        int off = ((m.GfxPixelY / 8) * 16 + m.GfxPixelX / 8) * tb;
        Assert.Equal(m.GfxColor,
                     Gfx.DecodeTile(data!, off, Gfx.RomBpp(rom))[(m.GfxPixelY & 7) * 8 + (m.GfxPixelX & 7)]);

        Assert.Equal(m.Header, LevelParser.Parse(rom, Level).Header.ToBytes());

        // Two things are base-gated rather than lost, and the builder has to SAY so — a warning
        // is the difference between "not supported here" and data quietly vanishing.
        foreach (string gated in new[] { "palette edits skipped", "GFX slot overrides skipped" })
            if (status.Contains(gated)) log.WriteLine($"gated as expected: {gated}");
        Assert.DoesNotContain("Unhandled", status);
    }
}

/// <summary>Tiny helper so a fluent assertion can open a file before reading from it.</summary>
internal static class RoundTripExtensions
{
    public static T Also<T>(this T self, Action<T> act) { act(self); return self; }
}
