using PipeDream;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Custom layer-2 backgrounds (CONTRACT §10b) — the route that lifts §10a's two constraints.
///
/// A vanilla background cannot grow and cannot move: the page byte is derived from the stream's
/// ADDRESS and the loader reads bank $0C only, where the largest free run is 402 bytes. So an
/// edit that re-encodes even one byte larger than the stream it replaces had nowhere to go, and
/// the build simply skipped it. LM's answer is to fork the level's own relocatable RATS block and
/// let a real 24-bit pointer name it, with the per-level settings byte at $0EF310 saying "custom
/// background", and the stream carrying its own page plane so nothing has to be derived.
///
/// Two properties matter and neither is visible from the bytes alone:
///   * the GEOMETRY changes — vanilla is 27 rows at stride 0x1B0, LM's is 32 at 0x200, so a
///     straight copy would shift the second screen by 0x50 tiles;
///   * it FORKS — a background four levels share stops being shared the moment one is edited,
///     which is the behaviour LM has and the old in-place write did not.
/// </summary>
public class BackgroundCustomTests(ITestOutputHelper log)
{
    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    /// <summary>A level that really is a background-image level in vanilla, per the catalog.</summary>
    private const int Level = 0x105;

    [Fact]
    public void the_stride_change_is_a_lossless_relayout()
    {
        // Distinct values everywhere the editor's 27-row grid can address, so a shifted screen or
        // a dropped row cannot pass.
        var low = new byte[BgImage.Tiles];
        Array.Fill(low, BgImage.Blank);
        for (int screen = 0; screen < 2; screen++)
            for (int row = 0; row < BgImage.VanillaRows; row++)
                for (int col = 0; col < 16; col++)
                    low[screen * BgImage.VanillaStride + row * 16 + col] =
                        (byte)(screen * 0x60 + row * 2 + col / 8 + 1);

        var planes = BgImage.ToCustomPlanes(low, page: 1);
        Assert.Equal(BgImage.Tiles * 2, planes.Length);
        // The page plane is uniform, which is what keeps a vanilla background's colours.
        Assert.All(planes[BgImage.Tiles..], p => Assert.Equal(1, p));

        // Every addressable cell survives the 0x1B0 -> 0x200 move...
        for (int screen = 0; screen < 2; screen++)
            for (int row = 0; row < BgImage.VanillaRows; row++)
                for (int col = 0; col < 16; col++)
                    Assert.Equal(low[screen * BgImage.VanillaStride + row * 16 + col],
                                 planes[screen * BgImage.CustomStride + row * 16 + col]);

        // ...and LM's five extra rows come out blank rather than as whatever fell there.
        for (int screen = 0; screen < 2; screen++)
            for (int row = BgImage.VanillaRows; row < BgImage.CustomRows; row++)
                for (int col = 0; col < 16; col++)
                    Assert.Equal(BgImage.Blank, planes[screen * BgImage.CustomStride + row * 16 + col]);
    }

    /// <summary>The codec round-trips: planes → RLE → planes, through the real decoder.</summary>
    [Fact]
    public void a_custom_stream_round_trips_through_the_rle()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        var rom = Rom.Load(Vanilla);
        RomPrep.Apply(rom);

        var low = new byte[BgImage.Tiles];
        for (int i = 0; i < low.Length; i++) low[i] = (byte)(i % 0x37);
        var planes = BgImage.ToCustomPlanes(low, page: 1);
        var stream = BgImage.EncodeCustom(planes);
        log.WriteLine($"0x{planes.Length:X} bytes of planes -> 0x{stream.Length:X} of stream");

        int snes = RatsWriter.Allocate(rom, stream, avoidBankCross: true);
        var back = BgImage.DecodeCustom(rom, snes);
        Assert.NotNull(back);
        var (gotLow, gotPage) = back!.Value;

        // Only the cells the editor's grid addresses are asserted — the rest is LM's extra rows.
        for (int screen = 0; screen < 2; screen++)
            for (int row = 0; row < BgImage.VanillaRows; row++)
                for (int col = 0; col < 16; col++)
                {
                    int at = screen * BgImage.VanillaStride + row * 16 + col;
                    Assert.Equal(low[at], gotLow[at]);
                    Assert.Equal(1, gotPage[at]);
                }
    }

    /// <summary>
    /// A tile from the OTHER page. The drawer offers all 0x200 BG tiles, but a cell used to be one
    /// byte with the page fixed per background by its address — so a page-1 tile stamped onto a
    /// page-0 background showed as 0x1A5 on the Background canvas and built (and composed) as
    /// 0x0A5. With a page per cell it is the same tile everywhere: canvas, level scene, the
    /// reopened project, and the plane the built ROM's custom stream carries.
    /// </summary>
    [Fact]
    public void a_tile_from_the_other_page_survives_paint_save_and_build()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        string dir = Path.Combine(Path.GetTempPath(), "pdbgp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var s = new EditorSession();
            Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
            s.ShowLevel(Level);
            Assert.Null(s.BgFixedPage);                                  // a prepped base takes any page
            int page = BgImage.PageFor(s.Rom!.Layer2Pointer(Level) & 0xFFFF);
            int other = (1 - page) << 8 | 0xA5;
            int idx = 4 * 16 + 3;                                         // screen 0, row 4, col 3

            var bg = s.BgMap!;
            Assert.True(bg.Stamp(3, 4, s.BgPaintable(other)));
            Assert.True(bg.EndStroke());
            Assert.Equal(other, bg.At(3, 4));
            Assert.Equal(other, s.Scene!.BgImage![idx]);                  // the level canvas agrees
            Assert.Equal(other, s.Rom.BgTilemaps[Level][idx]);
            s.Save();

            var again = new EditorSession();
            Assert.True(again.OpenProject(Path.Combine(dir, "proj", "project.pdp")), again.Status);
            again.ShowLevel(Level);
            Assert.Equal(other, again.BgMap!.At(3, 4));

            again.Build();
            var built = Rom.Load(Path.Combine(dir, "proj", "build", "proj.smc"));
            Assert.True(built.Layer2IsCustomBackground(Level), again.Status);
            var (low, pages) = BgImage.DecodeCustom(built, built.Layer2Pointer(Level))!.Value;
            Assert.Equal(0xA5, low[idx]);
            Assert.Equal(1 - page, pages[idx]);
            Assert.Equal(page, pages[idx + 1]);                           // its neighbour kept the background's own page
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>A base WITHOUT the custom-background hook has one page per background, from its
    /// address, so the editor remaps a tile picked from the other page to this page's tile of the
    /// same number at paint time — LM's own rule for an out-of-bank paste (§10c) — instead of
    /// showing one tile and building another.</summary>
    [Fact]
    public void on_a_vanilla_base_the_other_page_remaps_to_the_backgrounds_own()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(Vanilla), s.Status);
        s.ShowLevel(Level);
        int page = BgImage.PageFor(s.Rom!.Layer2Pointer(Level) & 0xFFFF);
        Assert.Equal(page, s.BgFixedPage);
        Assert.Equal(page << 8 | 0xA5, s.BgPaintable((1 - page) << 8 | 0xA5));
        Assert.Equal(page << 8 | 0x12, s.BgPaintable(page << 8 | 0x12));   // its own page is untouched
    }

    /// <summary>
    /// The end-to-end property the old code could not deliver: an edit BIGGER than the stream it
    /// came from now ships, and reads back as the picture that was edited.
    /// </summary>
    [Fact]
    public void an_edit_too_big_for_its_vanilla_stream_now_ships_as_a_custom_background()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        string dir = Path.Combine(Path.GetTempPath(), "pdbg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var s = new EditorSession();
            Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
            Assert.True(s.Rom!.HasLmLayer2Custom, "a prepped base must carry the $05803B hook");
            s.ShowLevel(Level);
            Assert.NotNull(s.BgMap);

            int origPtr = s.Rom.Layer2Pointer(Level);
            Assert.Equal(0xFF, origPtr >> 16);                       // vanilla background image
            BgImage.Decode(s.Rom, origPtr & 0xFFFF, out int wasBytes);

            // Deliberately incompressible: alternate two tiles so the RLE cannot help, which is
            // exactly the edit that used to be dropped.
            var map = s.BgMap!;
            for (int c = 0; c < EditorSession.BgCols; c++)
                for (int r = 0; r < EditorSession.BgRows; r++)
                    map.Stamp(c, r, (c + r) % 2 == 0 ? 0x10 : 0x2A);
            map.EndStroke();
            s.Save();

            string status = s.Build();
            log.WriteLine(status);
            Assert.DoesNotContain("background edit skipped", status);

            var built = Rom.Load(Path.Combine(s.Project!.Folder, "build", s.Project.Name + ".smc"));
            int ptr = built.Layer2Pointer(Level);
            log.WriteLine($"pointer ${origPtr:X6} (0x{wasBytes:X} bytes in bank $0C) -> ${ptr:X6}");
            Assert.NotEqual(0xFF, ptr >> 16);                        // a real, relocatable address
            Assert.True(built.Layer2IsCustomBackground(Level), "the settings byte must say custom");
            Assert.True(built.Layer2IsBackground(Level), "...and it must still read as a background");

            // The settings byte carries exactly the two bits, and only for this level.
            int fo = built.FileOffset(RomPrep.Layer23Settings + Level);
            Assert.Equal(RomPrep.Layer23CustomBg, built.Data[fo] & RomPrep.Layer23CustomBg);
            Assert.Equal(0, built.Data[built.FileOffset(RomPrep.Layer23Settings + Level + 1)]);

            // And it reads back as the picture that was edited.
            var tiles = LevelParser.DecodeBgImage(built, Level);
            Assert.NotNull(tiles);
            for (int c = 0; c < EditorSession.BgCols; c++)
                for (int r = 0; r < EditorSession.BgRows; r++)
                {
                    int at = (c / 16) * BgImage.VanillaStride + r * 16 + (c % 16);
                    Assert.Equal((c + r) % 2 == 0 ? 0x10 : 0x2A, tiles![at] & 0xFF);
                }
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// Editing one level's background must not touch another level that shared it — the fork. In
    /// vanilla level $105's stream is shared, and the old in-place write edited it for everyone.
    /// </summary>
    [Fact]
    public void editing_a_shared_background_forks_it_instead_of_editing_every_level()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        var probe = Rom.Load(Vanilla);
        int lo = probe.Layer2Pointer(Level) & 0xFFFF;
        var sharers = BgImage.Catalog(probe).First(c => c.Lo16 == lo).Levels;
        log.WriteLine($"${lo:X4} is shared by {sharers.Count} level(s): "
                    + string.Join(" ", sharers.Select(l => $"{l:X3}")));
        int other = sharers.FirstOrDefault(l => l != Level, -1);
        if (other < 0) { log.WriteLine("SKIP: not shared in this ROM"); return; }

        string dir = Path.Combine(Path.GetTempPath(), "pdbg2-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var s = new EditorSession();
            Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
            s.ShowLevel(Level);
            s.BgMap!.Stamp(3, 3, 0x11);
            s.BgMap.EndStroke();
            s.Save();
            log.WriteLine(s.Build());

            var built = Rom.Load(Path.Combine(s.Project!.Folder, "build", s.Project.Name + ".smc"));
            Assert.True(built.Layer2IsCustomBackground(Level));
            // The other level keeps the $FF pointer and the untouched vanilla stream.
            Assert.Equal(0xFF, built.Layer2Pointer(other) >> 16);
            Assert.False(built.Layer2IsCustomBackground(other));
            var theirs = LevelParser.DecodeBgImage(built, other)!;
            var vanillaTiles = LevelParser.DecodeBgImage(probe, other)!;
            Assert.Equal(vanillaTiles, theirs);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
