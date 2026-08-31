using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The editor-side overlay of ExAnimation slots onto composed tiles (Gfx.OverlayAnimatedTiles) —
/// what the level canvas and the Map16 sheet show. Destination tile 000 is a valid dest, and
/// every SOURCE kind must draw: the list's alt file 60-63, AN1's RAM region, and AN2's ($7E:AD00,
/// the bypass file) — the AN2 one silently showed the static tile until it got its own branch,
/// and a global slot with any RAM source was dropped (its manifest address is $7E:xxxx, not ROM).
/// </summary>
public class ExAnimOverlayTests(ITestOutputHelper log) : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pdovl-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private EditorSession? Open()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return null; }
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "p"), Vanilla), s.Status);
        s.ShowLevel(0x105);
        // file 60: tile 0 = solid colour 1, tile 1 = solid colour 2
        var gfx = new byte[0x40];
        for (int r = 0; r < 8; r++) gfx[r * 2] = 0xFF;
        for (int r = 0; r < 8; r++) gfx[0x20 + r * 2 + 1] = 0xFF;
        Assert.True(s.SetExAnimSource(0, gfx), s.Status);
        return s;
    }

    private static byte[] Tile0(EditorSession s, int phase)
        => Gfx.FgTiles.Load(s.Rom!, 1, 0x105, phase).Fetch(0);

    [Fact]
    public void dest_000_from_the_alt_file_overlays_and_animates_in_both_lists()
    {
        if (Open() is not { } s) return;
        var slot = new ExAnimation.Slot(0, 1, ExAnimation.TriggerNone, 2, 0x8000, [0x0000, 0x0020], 0);
        Assert.True(s.SetExAnimSlot(global: false, slot), s.Status);
        Assert.Equal(Enumerable.Repeat((byte)1, 64), Tile0(s, 0));   // frame 0: solid colour 1
        Assert.Equal(Enumerable.Repeat((byte)2, 64), Tile0(s, 1));   // frame 1: solid colour 2

        Assert.True(s.SetExAnim(global: false, [], 0), s.Status);
        Assert.True(s.SetExAnimSlot(global: true, slot), s.Status);
        Assert.Equal(Enumerable.Repeat((byte)1, 64), Tile0(s, 0));
        Assert.Equal(Enumerable.Repeat((byte)2, 64), Tile0(s, 1));
    }

    [Fact]
    public void an2_sourced_frames_draw_in_both_lists()
    {
        if (Open() is not { } s) return;
        Assert.StartsWith("bin", s.SetGfxSlot(0, 0x60));             // AN2 = file 60 (bypass word 0)
        var slot = new ExAnimation.Slot(0, 1, ExAnimation.TriggerNone, 2, 0x0000, [0xAD00, 0xAD20], 0);
        Assert.True(s.SetExAnimSlot(global: false, slot), s.Status);
        Assert.Equal(Enumerable.Repeat((byte)1, 64), Tile0(s, 0));
        Assert.Equal(Enumerable.Repeat((byte)2, 64), Tile0(s, 1));

        Assert.True(s.SetExAnim(global: false, [], 0), s.Status);
        Assert.True(s.SetExAnimSlot(global: true, slot), s.Status);  // RAM source through the engine
        Assert.Equal(Enumerable.Repeat((byte)1, 64), Tile0(s, 0));
    }
}
