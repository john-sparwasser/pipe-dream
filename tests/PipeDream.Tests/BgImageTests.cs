using Xunit;

namespace PipeDream.Tests;

/// <summary>Layer-2 background image codec (CONTRACT §10). Encode must be a lossless inverse
/// of Decode, and — because a background's page byte comes from its ADDRESS, so it cannot be
/// relocated without recolouring it — the re-encoded stream has to fit the original's bytes.</summary>
public class BgImageTests
{
    [Fact]
    public void round_trips_runs_literals_and_a_mixture()
    {
        void Trip(byte[] tiles)
        {
            var full = new byte[BgImage.Tiles];
            Array.Fill(full, BgImage.Blank);
            tiles.CopyTo(full, 0);
            byte[] rle = BgImage.Encode(full);
            Assert.Equal([0xFF, 0xFF], rle[^2..]);          // always terminated
            Assert.Equal(full, DecodeBytes(rle));
        }

        Trip([]);                                            // all blank
        Trip([1, 2, 3, 4, 5]);                               // pure literals
        Trip(Enumerable.Repeat((byte)7, 300).ToArray());     // runs past one command's max
        Trip([1, 1, 1, 2, 3, 4, 4, 4, 4, 5]);                // mixture
        Trip(Enumerable.Range(0, 260).Select(i => (byte)(i & 1)).ToArray());   // alternating
    }

    /// <summary>A 128-long run encodes its command byte as $FF; with $FF as the run value that
    /// spells the FF FF terminator and would truncate the image. The encoder must split it.</summary>
    [Fact]
    public void a_128_long_run_of_FF_does_not_encode_as_the_terminator()
    {
        var full = new byte[BgImage.Tiles];
        Array.Fill(full, BgImage.Blank);
        for (int i = 0; i < 128; i++) full[i] = 0xFF;

        byte[] rle = BgImage.Encode(full);
        Assert.NotEqual(0xFF, rle[0]);                       // not a disguised terminator
        Assert.Equal(full, DecodeBytes(rle));

        // And a 200-long run of $FF, which needs more than one command either way.
        Array.Fill(full, BgImage.Blank);
        for (int i = 0; i < 200; i++) full[i] = 0xFF;
        Assert.Equal(full, DecodeBytes(BgImage.Encode(full)));
    }

    [Fact]
    public void trailing_blank_costs_nothing_because_the_loader_prefills_it()
    {
        var justBlank = new byte[BgImage.Tiles];
        Array.Fill(justBlank, BgImage.Blank);
        Assert.Equal(2, BgImage.Encode(justBlank).Length);   // terminator only

        var oneTile = (byte[])justBlank.Clone();
        oneTile[0] = 0x10;
        Assert.True(BgImage.Encode(oneTile).Length < 8);
    }

    [Fact]
    public void the_page_byte_comes_from_the_address_inclusive_at_E8FE()
    {
        Assert.Equal(0, BgImage.PageFor(0xE8FD));
        Assert.Equal(1, BgImage.PageFor(0xE8FE));            // inclusive: level $10A sits here
        Assert.Equal(1, BgImage.PageFor(0xFFFF));
    }

    /// <summary>The one that decides whether editing backgrounds is possible at all: every
    /// vanilla background must survive decode → encode unchanged AND fit back inside the bytes
    /// it came from, since it cannot be moved without changing its page.</summary>
    [RealRomFact]
    public void every_vanilla_background_round_trips_and_fits_in_place()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var seen = new HashSet<int>();
        int checkedCount = 0, worst = 0;
        string worstAt = "";
        for (int lvl = 0; lvl < Rom.LevelCount; lvl++)
        {
            if (!rom.Layer2IsBackground(lvl)) continue;
            int lo16 = rom.Layer2Pointer(lvl) & 0xFFFF;
            if (!seen.Add(lo16)) continue;                   // many levels share a background

            byte[] low = BgImage.Decode(rom, lo16, out int consumed);
            byte[] re = BgImage.Encode(low);
            Assert.Equal(low, DecodeBytes(re));              // lossless
            int slack = consumed - re.Length;
            if (slack < worst) { worst = slack; worstAt = $"${lo16:X4}"; }
            checkedCount++;
        }
        Assert.True(checkedCount > 10, $"expected vanilla's backgrounds, found {checkedCount}");
        Assert.True(worst >= 0,
            $"re-encode grew by {-worst} bytes at {worstAt} — an in-place rewrite would not fit");
    }

    [RealRomFact]
    public void the_catalog_is_the_set_of_backgrounds_actually_in_use()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var cat = BgImage.Catalog(rom);
        Assert.Equal(17, cat.Count);                                   // vanilla's distinct set
        Assert.Equal(cat.OrderBy(c => c.Lo16).ToList(), cat);          // address order
        Assert.All(cat, c => Assert.NotEmpty(c.Levels));               // each is really used
        Assert.All(cat, c => Assert.Equal(BgImage.PageFor(c.Lo16), c.Page));
        // Every background level appears under exactly one entry.
        int bgLevels = Enumerable.Range(0, Rom.LevelCount).Count(rom.Layer2IsBackground);
        Assert.Equal(bgLevels, cat.Sum(c => c.Levels.Count));
        // Both pages are represented, so the page boundary is genuinely exercised.
        Assert.Contains(cat, c => c.Page == 0);
        Assert.Contains(cat, c => c.Page == 1);
    }

    /// <summary>BG tiles resolve through their OWN Map16 table (fixed $0D9100 + idx*8), indexed
    /// by the 9-bit page&lt;&lt;8|low — not the tileset-dependent FG tables, and not the low byte
    /// alone. Since the page comes from the stream's address, masking the index to 8 bits would
    /// silently render every page-1 background with page-0 definitions. This pins the renderer's
    /// side of that contract: a page-1 index must select the page-1 cache entry.</summary>
    [Fact]
    public void the_renderer_indexes_bg_defs_with_the_full_9_bit_page_index()
    {
        // A cache where entry n is a solid colour encoding n, so the drawn pixel names the
        // definition that was chosen.
        var cache = new uint[0x200][];
        for (int i = 0; i < cache.Length; i++)
        {
            cache[i] = new uint[16 * 16];
            Array.Fill(cache[i], (uint)(0xFF000000 | i));
        }
        var bg = new ushort[BgImage.Tiles];
        Array.Fill(bg, (ushort)0x125);          // page 1, tile $25
        bg[0] = 0x1AB;                          // page 1, tile $AB in the top-left cell

        var img = new uint[16 * 16];
        Map16.DrawBgImage(img, 16, 16, 1, bg, cache);
        Assert.Equal(0xFF0001ABu, img[0]);      // page bit honoured, def $1AB chosen
        Assert.NotEqual(0xFF0000ABu, img[0]);   // NOT the page-0 twin
    }

    [RealRomFact]
    public void bg_defs_come_from_the_fixed_table_not_the_tileset_fg_tables()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        // Map16.DefFileOffset routes the BG range ($4000-$41FF) at the fixed table, which is
        // what the Map16 editor writes through — the same 0x200 defs the renderer composes.
        for (int idx = 0; idx < 0x200; idx += 0x7F)
            Assert.Equal(rom.FileOffset(0x0D9100 + idx * 8), Map16.DefFileOffset(rom, 0, 0x4000 + idx));
        // A different TILESET must not move them: BG defs are tileset-independent.
        Assert.Equal(Map16.DefFileOffset(rom, 0, 0x4100), Map16.DefFileOffset(rom, 7, 0x4100));
    }

    // Decode straight from a byte[] so the codec can be exercised without a ROM.
    private static byte[] DecodeBytes(byte[] rle)
    {
        var low = new byte[BgImage.Tiles];
        Array.Fill(low, BgImage.Blank);
        int p = 0, o = 0;
        while (o < BgImage.Tiles && p + 1 < rle.Length)
        {
            int cmd = rle[p++];
            if (cmd == 0xFF && rle[p] == 0xFF) break;
            int count = (cmd & 0x7F) + 1;
            if ((cmd & 0x80) != 0)
            {
                byte b = rle[p++];
                for (int i = 0; i < count && o < BgImage.Tiles; i++) low[o++] = b;
            }
            else
                for (int i = 0; i < count && o < BgImage.Tiles; i++) low[o++] = rle[p++];
        }
        return low;
    }
}
