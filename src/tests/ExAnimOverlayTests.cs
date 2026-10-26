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

    /// <summary>
    /// The game steps its animated tiles a group at a time — on frame f, group f &amp; 7 (three ids each) moves to step
    /// (f &gt;&gt; 3) &amp; 3 — so the still Lunar Magic draws, frame 12, has the groups up to 4 on step 1 and the rest on
    /// step 0. Every phase is 8 frames on, and the glint colour is its own frame 4's (measured against LM's coins).
    /// </summary>
    [Fact]
    public void the_still_is_frame_12_with_each_group_on_its_own_step()
    {
        Assert.Equal(12, Gfx.FgTiles.StillFrame);
        Assert.Equal(1, Gfx.FgTiles.StepAt(0, 0));              // group 0, last stepped at frame 8
        Assert.Equal(1, Gfx.FgTiles.StepAt(14, 0));             // group 4, stepped at frame 12 itself
        Assert.Equal(0, Gfx.FgTiles.StepAt(15, 0));             // group 5, last stepped at frame 5
        Assert.Equal(2, Gfx.FgTiles.StepAt(0, 1));              // a phase later, all a step on
        Assert.Equal(0, Gfx.FgTiles.StepAt(0, 3));              // four steps wrap
        if (!File.Exists(Vanilla)) return;
        var rom = Rom.Load(Vanilla);
        var pal = Palette.Load(rom, LevelParser.Parse(rom, 0x105).Header, 0x105);
        Assert.Equal(rom.ReadValue(0x00B60C + 2, 2), pal.Bgr[0x64]);   // the glint at frame 4: the cycle's second word
    }

    /// <summary>
    /// The coin, door and ? block a blue POW brings out are blank 8x8s until it does (`$05:BB74` swaps to the frames $26
    /// ids on), so 014's room of coins was empty here. Lunar Magic draws what they show then at half strength; the coins
    /// that turn into blocks show something either way and are drawn as they are.
    /// </summary>
    [Fact]
    public void an_invisible_pow_coin_is_drawn_at_half_strength()
    {
        Assert.Equal(0xFF7B7B7Bu, Map16.Over(0xFF000000, Map16.GhostAlpha | 0xFFFFFF));
        Assert.Equal(0xFFFFFFFFu, Map16.Over(0xFF000000, 0xFFFFFFFF));
        Assert.Equal(0x807B7B7Bu, Map16.Over(0, Map16.GhostAlpha | 0xFFFFFF));          // premultiplied, alone
        if (!File.Exists(Vanilla)) return;
        var rom = Rom.Load(Vanilla);
        var h = LevelParser.Parse(rom, 0x014).Header;
        var fg = Gfx.FgTiles.Load(rom, h.Tileset, 0x014);
        Assert.All(fg.Fetch(0x54), p => Assert.Equal(0, p));                   // the POW coin: blank
        Assert.Contains(fg.Ghost(0x54)!, p => p != 0);                         // ... with the coin it becomes
        Assert.NotNull(fg.Ghost(0x50));                                        // the POW door
        Assert.NotNull(fg.Ghost(0x78));                                        // the POW ? block
        Assert.Null(fg.Ghost(0x6C));                                           // the coin that turns into a block
        var cache = Map16.ComposeAll(rom, h, fg, Palette.Load(rom, h, 0x014));
        Assert.Contains(cache[0x2A], c => c >> 24 == 0x80);
        Assert.DoesNotContain(cache[0x2B], c => c >> 24 == 0x80);
    }

    /// <summary>The blocks a hit from below brings out (021-024) draw nothing in the game; they are drawn as a
    /// see-through ? block (11F), so they can be seen and picked.</summary>
    [Fact]
    public void a_hidden_block_is_a_see_through_question_block()
    {
        if (!File.Exists(Vanilla)) return;
        var rom = Rom.Load(Vanilla);
        var h = LevelParser.Parse(rom, 0x105).Header;
        var fg = Gfx.FgTiles.Load(rom, h.Tileset, 0x105);
        var pal = Palette.Load(rom, h, 0x105);
        var cache = Map16.ComposeAll(rom, h, fg, pal);
        var ghostQ = cache[0x11F].Select(c => c == 0 ? 0 : c & 0xFFFFFF | Map16.GhostAlpha);
        foreach (int t in new[] { 0x21, 0x22, 0x23, 0x24 }) Assert.Equal(ghostQ, cache[t]);
        Assert.DoesNotContain(cache[0x25], c => c >> 24 == 0x80);

        cache[0x22] = new uint[256];
        Map16.ComposeInto(cache, rom, h, fg, pal, [0x11F]);                   // recomposing the ? block redraws them
        Assert.Equal(ghostQ, cache[0x22]);
    }
}
