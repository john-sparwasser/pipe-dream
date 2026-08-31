using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Layer 3 — the option byte, vanilla's stripe-image tilemap scripts, and the 512-tile window
/// their words name (CONTRACT §12b).
///
/// The one thing worth pinning hard is the SCREEN ORDER. A 64x64 BG is four 32x32 screens in
/// VRAM, and reading them as one linear 64-wide map (or getting the second and third the wrong
/// way round) still produces a plausible-looking picture — the water tilemap would come out
/// split down the middle instead of across, which is only obviously wrong if you already know
/// what it should look like. So the water level's map is asserted directly: nothing at all above
/// the surface, water below it, LEFT AND RIGHT alike.
/// </summary>
public class Layer3Tests(ITestOutputHelper log)
{
    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private Rom? Open()
    {
        if (File.Exists(Vanilla)) return Rom.Load(Vanilla);
        log.WriteLine("SKIP: no ROM");
        return null;
    }

    [Fact]
    public void the_option_is_the_two_high_bits_of_the_levels_05F200_byte()
    {
        if (Open() is not { } rom) return;
        // The same bits MainEntrance carries, and the ones $05D928 puts in $1BE3.
        for (int level = 0; level < 0x200; level++)
            Assert.Equal((rom.ReadByte(0x05F200 + level) >> 6) & 3, Layer3.Option(rom, level));

        Assert.Equal(0, Layer3.Option(rom, 0x105));   // level 105 has no layer 3
        Assert.Equal(1, Layer3.Option(rom, 0x127));   // water, high and low tides
        Assert.Equal(3, Layer3.Option(rom, 0x009));   // ghost house: tileset specific
    }

    [Fact]
    public void option_zero_has_no_tilemap_and_neither_does_a_mode_past_the_pointer_table()
    {
        if (Open() is not { } rom) return;
        Assert.Null(Layer3.Tilemap(rom, levelMode: 0, option: 0));
        // Layer3Ptr ends where the first tilemap block starts: 15 modes, 3 options each.
        Assert.Null(Layer3.Tilemap(rom, levelMode: 15, option: 1));
        Assert.NotNull(Layer3.Tilemap(rom, levelMode: 14, option: 3));
    }

    [Fact]
    public void every_vanilla_level_that_claims_a_layer_3_actually_has_one()
    {
        if (Open() is not { } rom) return;
        int found = 0;
        for (int level = 0; level < 0x200; level++)
        {
            int option = Layer3.Option(rom, level);
            if (option == 0) continue;
            found++;
            int mode = LevelParser.Parse(rom, level).Header.LevelMode;
            var map = Layer3.Tilemap(rom, mode, option);
            Assert.True(map is not null, $"level {level:X3} (mode {mode}, option {option}) has no tilemap");
            Assert.True(map!.Count(w => w >= 0) > 0x100,
                        $"level {level:X3} wrote only {map.Count(w => w >= 0)} words");
        }
        log.WriteLine($"{found} vanilla levels carry a layer 3");
        Assert.Equal(26, found);
    }

    [Fact]
    public void the_water_map_splits_across_the_screen_boundary_not_down_it()
    {
        if (Open() is not { } rom) return;
        // Level 127, mode 0, "Water, high and low tides".
        var map = Layer3.Tilemap(rom, 0, 1);
        Assert.NotNull(map);

        // (x, y) in TILES over the whole 64x64 map, through the four-screen VRAM layout.
        int WordAt(int x, int y) => map![(y / 32 << 11) | (x / 32 << 10) | (y % 32) << 5 | x % 32];

        // The surface sits on row 32 — exactly the boundary between the top screens and the
        // bottom ones. Above it the script writes nothing at all; below it, water everywhere.
        // Read the screens in the wrong order and this comes out as a LEFT/RIGHT split instead.
        for (int x = 0; x < 64; x++)
        {
            for (int y = 0; y < 32; y++) Assert.Equal(-1, WordAt(x, y));
            for (int y = 33; y < 63; y++) Assert.True(WordAt(x, y) >= 0, $"({x},{y}) is empty water");
            Assert.True(WordAt(x, 32) >= 0, $"({x},32) has no surface");
            Assert.Equal(WordAt(0, 40), WordAt(x, 40));       // the water is one tile, all the way across
        }
        Assert.NotEqual(WordAt(0, 32), WordAt(0, 40));        // the surface is not more water
    }

    [Fact]
    public void the_tiles_are_the_four_2bpp_layer_3_files_laid_end_to_end()
    {
        if (Open() is not { } rom) return;
        var tiles = Layer3.Tiles(rom);
        Assert.Equal(Layer3.TileCount, tiles.Length);
        Assert.All(tiles, t => Assert.NotNull(t));
        Assert.All(tiles, t => Assert.All(t!, px => Assert.InRange(px, 0, 3)));   // 2bpp

        // Slot k starts at tile k*128, which is what $00A993's straight-through upload does.
        for (int slot = 0; slot < 4; slot++)
        {
            var file = Gfx.Cached(rom, Layer3.VanillaGfx[slot]);
            Assert.Equal(0x800, file!.Length);
            Assert.Equal(Gfx.DecodeTile(file, 0, 2), tiles[slot * Layer3.SlotTiles]);
        }
    }

    [Fact]
    public void rendering_covers_the_written_words_and_leaves_the_rest_as_backdrop()
    {
        if (Open() is not { } rom) return;
        var header = LevelParser.Parse(rom, 0x009).Header;
        var map = Layer3.Tilemap(rom, header.LevelMode, Layer3.Option(rom, 0x009));
        var pal = Palette.Load(rom, header, 0x009);
        var (px, w, h) = Layer3.Render(map!, Layer3.Tiles(rom), pal);

        Assert.Equal(Layer3.Cols * 8, w);
        Assert.Equal(Layer3.Rows * 8, h);
        // The ghost house window is a small block; most of the map is untouched, and untouched
        // must stay the back-area colour rather than becoming a screen of tile 0.
        int drawn = px.Count(p => p != pal.Rgba[0]);
        Assert.True(drawn is > 1000 && drawn < w * h / 2, $"{drawn} of {w * h} pixels drawn");
    }
}
