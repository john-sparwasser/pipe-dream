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

    /// <summary>One of the controlled Lunar Magic saves next to the vanilla ROM.</summary>
    private Rom? OpenRef(params string[] parts)
    {
        string p = Path.Combine([Path.GetDirectoryName(Vanilla)!, .. parts]);
        if (File.Exists(p)) return Rom.Load(p);
        log.WriteLine($"SKIP: no {string.Join("/", parts)}");
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

    // ---- Lunar Magic's layer-3 GFX bypass (CONTRACT §12b) ----

    [Fact]
    public void the_bypass_slots_are_the_tail_of_the_gfx_record_that_7d_left_unnamed()
    {
        if (OpenRef("layer3", "l3_e.smc") is not { } rom) return;

        // l3_e was saved with LG1 repointed to GFX 30 on level 105 and nothing else touched.
        Assert.Equal([0x30, 0x29, 0x2A, 0x2B], rom.LmLayer3Gfx(0x105)!);
        Assert.Equal([0x30, 0x29, 0x2A, 0x2B], Layer3.GfxFiles(rom, 0x105));

        // ...and only on level 105. Every other record is at LM's install defaults, which is
        // the same table's tail reading 2B 2A 29 28 with the enable bit clear.
        for (int level = 0; level < 0x200; level++)
            if (level != 0x105)
                Assert.True(rom.LmLayer3Gfx(level) is null, $"level {level:X3} claims a bypass");
        Assert.Equal(Layer3.VanillaGfx, Layer3.GfxFiles(rom, 0x104));
    }

    [Fact]
    public void bit_14_is_the_layer_3_bypass_and_bit_15_is_the_fg_bg_sp_one()
    {
        // The two features share one 16-word record, so the bits have to stay apart: l3_e has
        // ONLY the layer-3 bypass on level 105 (w0 = 407F), gfx_after ONLY the other one
        // (w0 = 8008). Read either gate as "the record is in use" and both ROMs break.
        if (OpenRef("layer3", "l3_e.smc") is { } l3)
        {
            Assert.Equal(0x407F, l3.LmGfxRecord(0x105)![0]);
            Assert.NotNull(l3.LmLayer3Gfx(0x105));
            Assert.Null(l3.LmGfxBypass(0x105));
        }
        if (OpenRef("gfx_after.smc") is { } gfx)
        {
            Assert.Equal(0x8008, gfx.LmGfxRecord(0x105)![0]);
            Assert.Null(gfx.LmLayer3Gfx(0x105));
            Assert.NotNull(gfx.LmGfxBypass(0x105));
            // Its tail is the vanilla layer-3 set, untouched — this ROM predates the hack.
            Assert.Equal(Layer3.VanillaGfx, Layer3.GfxFiles(gfx, 0x105));
        }
    }

    /// <summary>
    /// The tilemap bypass, all of it in word 1 behind w0 bit 13 (CONTRACT §12b). l3_g ends its
    /// six controlled saves at LT3 = ExGFX 80, size 0x2000, destination "Last Line of Status
    /// Bar" — w1 = 8080 — with the two OTHER enables in w0 untouched by any of it.
    /// </summary>
    [Fact]
    public void the_tilemap_bypass_is_word_1_behind_its_own_enable_bit()
    {
        if (OpenRef("layer3", "l3_g.smc") is not { } rom) return;

        Assert.Equal(0x8080, rom.LmGfxRecord(0x105)![1]);
        var (file, dest, size) = rom.LmLayer3Tilemap(0x105)!.Value;
        Assert.Equal(0x080, file);
        Assert.Equal(2, dest);
        Assert.Equal(0, size);
        Assert.Equal(0x2000, Layer3.TilemapSizes[size]);
        Assert.Equal("Last Line of Status Bar", Layer3.TilemapDestinations[dest]);

        // Three independent enables in w0, and this save set only the third.
        Assert.Equal(0x607F, rom.LmGfxRecord(0x105)![0]);
        Assert.NotNull(rom.LmLayer3Gfx(0x105));       // bit 14, from the earlier saves
        Assert.Null(rom.LmGfxBypass(0x105));          // bit 15, still off

        // Only level 105, and only where the bit says so.
        for (int level = 0; level < 0x200; level++)
            if (level != 0x105)
                Assert.Null(rom.LmLayer3Tilemap(level));
        Assert.Null(OpenRef("layer3", "l3_e.smc")?.LmLayer3Tilemap(0x105));
    }

    /// <summary>
    /// A layer-3 tilemap now REACHES the built ROM: inserted as an ExGFX file, named by the
    /// record's LT3 slot, with bit 13 lit. It needs a base carrying LM's tilemap loader, so this
    /// builds on l3_g — the save that installed it — rather than on a prepped vanilla.
    /// </summary>
    [Fact]
    public void a_tilemap_is_inserted_and_named_by_the_record_when_the_base_can_load_it()
    {
        if (OpenRefPath("layer3", "l3_g.smc") is not { } basePath) return;
        string dir = Path.Combine(Path.GetTempPath(), "pdl3w-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var s = new EditorSession();
            Assert.True(s.NewProject(Path.Combine(dir, "proj"), basePath), s.Status);
            s.ShowLevel(0x009);
            Assert.True(s.Rom!.HasLmLayer3Tilemap);

            var map = s.Layer3Map!;
            map.Stamp(10, 10, (2 << 10) | 0x41);
            Assert.True(map.EndStroke());
            s.Save();

            string status = s.Build();
            log.WriteLine(status);
            string built = Path.Combine(s.Project!.Folder, "build", s.Project.Name + ".smc");
            Assert.True(File.Exists(built), status);
            var rom = Rom.Load(built);

            var bypass = rom.LmLayer3Tilemap(0x009);
            Assert.NotNull(bypass);
            var (file, dest, size) = bypass!.Value;
            Assert.InRange(file, 0x80, 0xFF);
            Assert.Equal(Layer3.BuiltTilemapDestination, dest);
            Assert.Equal(0x2000, Layer3.TilemapSizes[size]);

            // The file the slot names really is there, and really is the map that was painted.
            var inserted = Gfx.Cached(rom, file);
            Assert.NotNull(inserted);
            Assert.Equal(0x2000, inserted!.Length);
            int at = Layer3.CellIndex(10, 10) * 2;
            Assert.Equal((2 << 10) | 0x41, inserted[at] | (inserted[at + 1] << 8));
            Assert.DoesNotContain("editor-only", status);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>...and it no longer needs an LM-saved base to do it: prep v15 installs the
    /// loader, so an ordinary project on a prepped vanilla stamps the bypass and stops
    /// apologising. This is the case every project here actually is.</summary>
    [Fact]
    public void a_painted_tilemap_reaches_a_rom_built_on_a_plain_prepped_vanilla()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        string dir = Path.Combine(Path.GetTempPath(), "pdl3n-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var s = new EditorSession();
            Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
            s.ShowLevel(0x009);
            Assert.True(s.Rom!.HasLmLayer3Tilemap);        // prep v15 installs it
            s.Layer3Map!.Stamp(10, 10, (2 << 10) | 0x41);
            s.Layer3Map.EndStroke();
            s.Save();

            string status = s.Build();
            Assert.DoesNotContain("editor-only", status);
            var rom = Rom.Load(Path.Combine(s.Project!.Folder, "build", s.Project.Name + ".smc"));
            var bypass = rom.LmLayer3Tilemap(0x009);
            Assert.NotNull(bypass);
            Assert.InRange(bypass!.Value.File, 0x80, 0xFFF);
            Assert.Equal(Layer3.BuiltTilemapDestination, bypass.Value.Destination);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private string? OpenRefPath(params string[] parts)
    {
        string p = Path.Combine([Path.GetDirectoryName(Vanilla)!, .. parts]);
        if (File.Exists(p)) return p;
        log.WriteLine($"SKIP: no {string.Join("/", parts)}");
        return null;
    }

    [Fact]
    public void a_rom_without_lms_layer_3_hack_keeps_the_vanilla_files()
    {
        if (Open() is { } vanilla)                                  // no Lunar Magic at all
        {
            Assert.Null(vanilla.LmLayer3Gfx(0x105));
            Assert.Equal(Layer3.VanillaGfx, Layer3.GfxFiles(vanilla, 0x105));
        }
        if (OpenRef("layer3", "l3_0.smc") is { } pre)               // LM, but before the hack
        {
            Assert.Null(pre.LmLayer3Gfx(0x105));
            Assert.Equal(Layer3.VanillaGfx, Layer3.GfxFiles(pre, 0x105));
        }
    }

    [Fact]
    public void a_bypassed_slot_actually_changes_the_tiles_it_loads()
    {
        if (OpenRef("layer3", "l3_e.smc") is not { } rom) return;

        var bypassed = Layer3.Tiles(rom, 0x105);                    // LG1 = GFX 30
        var vanilla = Layer3.Tiles(rom, 0x104);                     // LG1 = GFX 28
        Assert.NotEqual(vanilla[0], bypassed[0]);                   // slot 1 is repointed...
        Assert.Equal(vanilla[Layer3.SlotTiles], bypassed[Layer3.SlotTiles]);   // ...LG2 is not

        // Slot 1 is tiles 0-127, which is VRAM word $4000 — LM's own destination table.
        var file30 = Gfx.Cached(rom, 0x30)!;
        Assert.Equal(Gfx.DecodeTile(file30, 0, 2), bypassed[0]);
    }

    /// <summary>
    /// Building writes the LG slots into the record and lights bit 14 — WITHOUT lighting bit 15,
    /// which belongs to the unrelated FG/BG/SP bypass. And it says out loud that a base with no
    /// layer-3 GFX loader will ignore them, because a slot that silently does nothing in game is
    /// worse than one the build refuses.
    /// </summary>
    [Fact]
    public void building_a_layer_3_override_lights_bit_14_and_warns_when_the_base_cannot_use_it()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        string dir = Path.Combine(Path.GetTempPath(), "pdl3-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var s = new EditorSession();
            Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
            s.ShowLevel(0x105);
            s.SetGfxSlot(15, 0x30);                       // LG1 → GFX 30
            s.Save();

            string status = s.Build();
            log.WriteLine(status);
            string built = Path.Combine(s.Project!.Folder, "build", s.Project.Name + ".smc");
            Assert.True(File.Exists(built), status);

            var rom = Rom.Load(built);
            var rec = rom.LmGfxRecord(0x105);
            Assert.NotNull(rec);
            Assert.Equal(0x4000, rec![0] & 0xC000);       // layer 3 on, FG/BG/SP untouched
            Assert.Equal(0x30, rec[15] & 0xFFF);          // w15 is LG1
            Assert.Equal([0x30, 0x29, 0x2A, 0x2B], Layer3.GfxFiles(rom, 0x105));

            // Prep v14 installs the layer-3 GFX loader, so the slot reaches the game and the
            // build no longer has to apologise for it.
            Assert.True(rom.HasLmLayer3Gfx);
            Assert.DoesNotContain("LG1-LG4", status);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// Layer 3 is 2bpp because of WHERE it is loaded, not because of which file is loaded. A
    /// vanilla 28-2B is listed 2bpp already, but an ExGFX file a bypassed LG slot points at is
    /// not — and reading a 0x800-byte sheet at the ROM's 4bpp yields 64 tiles of 16 colours
    /// instead of 128 of four: half the window empty, and every tile that does appear garbled.
    /// </summary>
    [Fact]
    public void a_bypassed_slot_reads_its_file_2bpp_whatever_the_rom_stores()
    {
        if (Open() is not { } rom) return;
        Assert.NotEqual(2, Gfx.RomBpp(rom));                  // the ROM stores 3 or 4 planes, never 2

        // A full 2bpp slot: 0x800 bytes of 0xFF is 128 tiles, every pixel colour 3.
        rom.ImportedGfx[0x100] = [.. Enumerable.Repeat((byte)0xFF, 0x800)];
        rom.GfxSlotOverrides[(0x009, 15)] = 0x100;            // w15 = LG1
        Assert.Equal(0x100, Layer3.GfxFiles(rom, 0x009)[0]);

        var tiles = Layer3.Tiles(rom, 0x009);
        // Read at 4bpp the file would run out after 64 tiles, so the tail is the whole point.
        Assert.All(tiles[..Layer3.SlotTiles], t => Assert.NotNull(t));
        Assert.NotNull(tiles[Layer3.SlotTiles - 1]);
        Assert.All(tiles[..Layer3.SlotTiles], t => Assert.All(t!, px => Assert.Equal(3, px)));
        Assert.Equal(2, Layer3.Bpp);
    }

    // ---- imported tilemaps ----

    [Fact]
    public void an_imported_tilemap_replaces_vanillas_pick_and_survives_a_project_round_trip()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        string dir = Path.Combine(Path.GetTempPath(), "pdl3i-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var s = new EditorSession();
            Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
            s.ShowLevel(0x009);
            var (before, _, _) = s.Layer3Image();

            // A full 64x64 map of one tile — LM's default LT3 size.
            string file = Path.Combine(dir, "flat.bin");
            var raw = new byte[0x2000];
            for (int i = 0; i < 0x1000; i++) { raw[i * 2] = 0x40; raw[i * 2 + 1] = 0x18; }
            File.WriteAllBytes(file, raw);

            Assert.True(s.ImportLayer3Tilemap(file), s.Status);
            Assert.True(s.Layer3TilemapImported);
            var (after, w, h) = s.Layer3Image();
            Assert.NotEqual(before, after);
            // Every word written, so nothing falls back to the backdrop.
            Assert.Equal(w * h, after.Length);

            // Reopened from disk, the level still draws the imported map.
            s.Save();
            var reopened = new EditorSession();
            Assert.True(reopened.OpenProject(s.Project!.FilePath), reopened.Status);
            reopened.ShowLevel(0x009);
            Assert.True(reopened.Layer3TilemapImported);
            Assert.Equal(after, reopened.Layer3Image().Px);

            Assert.True(reopened.ClearLayer3Tilemap());
            Assert.False(reopened.Layer3TilemapImported);
            Assert.Equal(before, reopened.Layer3Image().Px);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void a_tilemap_that_is_not_a_whole_map_is_refused_with_a_reason()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        string dir = Path.Combine(Path.GetTempPath(), "pdl3r-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var s = new EditorSession();
            Assert.True(s.OpenRom(Vanilla), s.Status);
            s.ShowLevel(0x009);

            string odd = Path.Combine(dir, "odd.bin");
            File.WriteAllBytes(odd, new byte[0x123]);
            Assert.False(s.ImportLayer3Tilemap(odd));
            Assert.Contains("0x800, 0x1000 or 0x2000", s.Status);
            Assert.False(s.Layer3TilemapImported);

            Assert.True(Layer3.IsTilemapSize(0x800) && Layer3.IsTilemapSize(0x1000)
                        && Layer3.IsTilemapSize(0x2000));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void a_short_tilemap_leaves_the_rest_of_the_window_untouched()
    {
        // 0x800 bytes is one 32x32 screen, so the other three stay -1 rather than tile 0.
        var map = Layer3.FromBytes(new byte[0x800]);
        Assert.Equal(Layer3.MapWords, map.Length);
        Assert.All(map[..0x400], v => Assert.Equal(0, v));
        Assert.All(map[0x400..], v => Assert.Equal(-1, v));
    }

    [Fact]
    public void rendering_covers_the_written_words_and_leaves_the_rest_as_backdrop()
    {
        if (Open() is not { } rom) return;
        var header = LevelParser.Parse(rom, 0x009).Header;
        var map = Layer3.Tilemap(rom, header.LevelMode, Layer3.Option(rom, 0x009));
        var pal = Palette.Load(rom, header, 0x009);
        var (px, w, h) = Layer3.Render(map!, Layer3.Tiles(rom), pal, pal.Rgba[0]);

        Assert.Equal(Layer3.Cols * 8, w);
        Assert.Equal(Layer3.Rows * 8, h);
        // The ghost house window is a small block; most of the map is untouched, and untouched
        // must stay the back-area colour rather than becoming a screen of tile 0.
        int drawn = px.Count(p => p != pal.Rgba[0]);
        Assert.True(drawn is > 1000 && drawn < w * h / 2, $"{drawn} of {w * h} pixels drawn");
    }

    /// <summary>
    /// The advanced bypass, read back off the controlled save that set it (CONTRACT §12b).
    /// l3_i ended with Vertical = Fast, Horizontal = Slow, layer 3 on the subscreen, the
    /// scroll-sync fix on, CGADSUB off, Initial X = 10 and Initial Y = 123 — and none of it
    /// touched w0, because the advanced group has no enable bit there.
    /// </summary>
    [Fact]
    public void the_advanced_bypass_reads_back_the_save_that_set_it()
    {
        if (OpenRef("layer3", "l3_i.smc") is not { } rom) return;

        var a = rom.LmLayer3Advanced(0x105)!.Value;
        Assert.Equal("Fast", Layer3.VScrollNames[a.VScroll]);
        Assert.Equal("Slow", Layer3.HScrollNames[a.HScroll]);
        Assert.True(a.Subscreen);
        Assert.True(a.FixScrollSync);
        Assert.False(a.CgAdSub);
        Assert.Equal(0x10, Layer3.XPositions[a.XPos]);
        Assert.Equal(0x123, a.Y);

        Assert.Equal(0x607F, rom.LmGfxRecord(0x105)![0]);   // still only the two GFX enables
        Assert.True(rom.HasLmLayer3Advanced);
        for (int level = 0; level < 0x200; level++)
            if (level != 0x105) Assert.Null(rom.LmLayer3Advanced(level));
    }

    /// <summary>
    /// The second controlled save, which moved the three checkboxes and pushed the vertical
    /// scroll past the 4-bit nibble: "Auto-Scroll Up Fast 3" is code 0x10, so its bit 4 lives
    /// in a different word from the rest of the field. Horizontal stayed None, which is what
    /// pins WHICH of the two spare bits belongs to which scroll.
    /// </summary>
    [Fact]
    public void a_five_bit_scroll_code_and_the_flags_survive_the_split_across_words()
    {
        if (OpenRef("layer3", "l3_j.smc") is not { } rom) return;

        var a = rom.LmLayer3Advanced(0x105)!.Value;
        Assert.Equal("Auto-Scroll Up Fast 3", Layer3.VScrollNames[a.VScroll]);
        Assert.Equal(0x10, Layer3.ScrollCodes[a.VScroll]);
        Assert.Equal("None", Layer3.HScrollNames[a.HScroll]);
        Assert.True(a.CgAdSub);
        Assert.False(a.Subscreen);
        Assert.False(a.FixScrollSync);
    }

    /// <summary>
    /// The advanced group reaches a BUILT ROM, and the level next door keeps the settings the
    /// base already gave it. That second half is the one that can silently break: the build
    /// rebuilds the record from all-defaults, so a level with no advanced edit of its own has to
    /// have its nibbles carried across rather than zeroed.
    /// </summary>
    [Fact]
    public void advanced_settings_reach_a_built_rom_without_wiping_the_level_that_had_them()
    {
        if (OpenRefPath("layer3", "l3_i.smc") is not { } basePath) return;
        string dir = Path.Combine(Path.GetTempPath(), "pdl3a-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var s = new EditorSession();
            Assert.True(s.NewProject(Path.Combine(dir, "proj"), basePath), s.Status);
            Assert.True(s.Rom!.HasLmLayer3Advanced);

            var mine = new Layer3.Advanced(CgAdSub: true, Subscreen: false, FixScrollSync: false,
                                           VScroll: 13, HScroll: 2, XPos: 1, Y: -0x120);
            s.ShowLevel(0x009);
            Assert.True(s.ApplyLayer3Advanced(mine), s.Status);
            s.Save();

            string status = s.Build();
            log.WriteLine(status);
            var rom = Rom.Load(Path.Combine(s.Project!.Folder, "build", s.Project.Name + ".smc"));

            Assert.Equal(mine, rom.LmLayer3Advanced(0x009));
            Assert.Equal(0x123, rom.LmLayer3Advanced(0x105)!.Value.Y);   // l3_i's own, untouched
            Assert.DoesNotContain("editor-only", status);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>Every field survives a write/read round trip through the record's spare
    /// nibbles, including a negative Y — it reaches the ROM multiplied by 8 in a 14-bit signed
    /// field, which is the one place a sign can be lost.</summary>
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(8, 6, 3, 0x123)]
    [InlineData(13, 20, 2, -0x400)]
    [InlineData(20, 14, 1, 0x3FF)]
    [InlineData(4, 7, 0, -1)]
    public void advanced_settings_round_trip_through_the_spare_nibbles(int v, int h, int x, int y)
    {
        var a = new Layer3.Advanced(CgAdSub: true, Subscreen: false, FixScrollSync: true,
                                    VScroll: v, HScroll: h, XPos: x, Y: y);
        var w = new ushort[16];
        for (int i = 0; i < 16; i++) w[i] = 0x07F;    // a record of "slot uses the default"
        Layer3.WriteAdvanced(w, a);

        Assert.Equal(a, Layer3.ReadAdvanced(w));
        // The low 12 bits of every word are GFX file ids and must come through untouched.
        Assert.All(w, x2 => Assert.Equal(0x07F, x2 & 0xFFF));

        Layer3.WriteAdvanced(w, null);
        Assert.Null(Layer3.ReadAdvanced(w));
        Assert.All(w, x2 => Assert.Equal(0x007F, x2));
    }
}
