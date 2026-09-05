namespace PipeDream.Services;

// EditorSession — the Background tab: layer 2 as a background IMAGE and layer 3, both painted
// through TilemapEdit, plus the saved-tilemap library and the advanced layer-3 bypass. Layer 2
// as an OBJECT stream is edited like layer 1 and lives in EditorSession.cs (SetLayer2ObjectMode).
// The rest of the class: EditorSession.cs and the other EditorSession.*.cs files.
public sealed partial class EditorSession
{
    // ---- Background (layer 2), reference/CONTRACT.md §10 / §10b ----

    /// <summary>True when this level's layer 2 is a background IMAGE, which is the only case the
    /// Background tab can edit. An object-mode layer 2 is edited in the Level tab instead — the
    /// same split LM draws ("if a level is set to have level data on layer 2 instead of an image,
    /// there will be nothing to edit here").</summary>
    public bool Layer2IsBackgroundImage => Scene?.BgImage is not null;

    /// <summary>Two screens side by side, which is how the background repeats horizontally
    /// (<c>within = cx &amp; 0x1F</c>), and vanilla's 27 rows. LM's custom backgrounds carry 32
    /// (§10b) — that arrives with the writer, not before.</summary>
    public const int BgCols = 32, BgRows = 27;

    /// <summary>BG Map16 defs: the fixed 0x200 at $0D9100, which is the whole picker.</summary>
    public const int BgSheetTiles = 0x200;

    /// <summary>The level's background drawn as pixels, one image per animation phase (BG tiles
    /// animate like any other). Empty when layer 2 is not a background image.</summary>
    public (uint[]?[] Px, int W, int H) BgPhases()
    {
        var px = new uint[4][];
        int w = 0, h = 0;
        if (Scene is not { } s) return (px, 0, 0);
        for (int p = 0; p < 4; p++)
        {
            var (img, iw, ih) = s.BgSurface(BgCols, BgRows, p);
            if (img.Length == 0) continue;
            px[p] = img; w = iw; h = ih;
        }
        return (px, w, h);
    }

    /// <summary>The BG Map16 defs as a picker sheet, one per phase. These are the fixed 0x200
    /// defs at $0D9100 — LM's BG pages 80-81, our virtual tiles 0x4000-0x41FF.</summary>
    public (uint[]?[] Px, int W, int H) BgSheetPhases()
    {
        var px = new uint[4][];
        int w = 0, h = 0;
        if (Scene is not { } s) return (px, 0, 0);
        for (int p = 0; p < 4; p++)
        {
            var (img, iw, ih) = s.BgSheet(p);
            if (img.Length == 0) continue;
            px[p] = img; w = iw; h = ih;
        }
        return (px, w, h);
    }

    // ---- Layer 3, reference/CONTRACT.md §12b ----

    /// <summary>The level's Layer 3 Options value, 0-3 — 0 means the level has no layer 3.
    /// Name it with <see cref="Layer3.OptionNames"/>.</summary>
    public int Layer3Option => Rom is { } r && HasLevel ? Layer3.Option(r, LevelNum) : 0;

    // ---- background tilemap EDITING ----
    // Both layers paint through TilemapEdit; what differs is where a (column, row) lands in the
    // level's own buffer, and what a cell's number means. Created per level in ShowLevel, and
    // each commit writes straight back into the session ROM's override store — the same store
    // the import path fills, so an edited map and an imported one are the same thing downstream.

    /// <summary>The layer-2 background as a paintable 32x27 grid of BG Map16 tiles, or null when
    /// this level's layer 2 is an object stream (nothing to paint) or no level is open.</summary>
    public TilemapEdit? BgMap { get; private set; }

    /// <summary>The layer-3 tilemap as a paintable 64x64 grid, or null when the level has no
    /// layer 3. Cells are whole BG3 words, so a stamp carries the brush's palette and flips.</summary>
    public TilemapEdit? Layer3Map { get; private set; }

    /// <summary>Build both editors for the level now open. An edit to either is a per-level
    /// override, so a level that has never been painted has no buffer of its own until it is.</summary>
    private void OpenBackgroundEdits()
    {
        BgMap = null; Layer3Map = null;
        if (Rom is not { } rom || Scene is not { } s) return;
        if (s.BgImage is not null) OpenBgMap(rom, s);
        OpenLayer3Map(rom, s);
    }

    /// <summary>The layer-2 background image as a paintable grid, committing into the ROM's
    /// per-level override and the project.</summary>
    private void OpenBgMap(Rom rom, LevelScene s)
    {
        // Whole BG tile numbers, page in bit 8 — the drawer's 0x000-0x1FF. Cells stayed
        // bytes for a long time, with the page fixed per background by its address (§10a);
        // a page-1 tile stamped onto a page-0 background then showed here and built as
        // page 0's tile of the same number. A custom background carries a page per tile.
        ushort[] tiles = rom.BgTilemaps.TryGetValue(LevelNum, out var edited) ? edited : s.BgImage!;
        var cells = new int[tiles.Length];
        for (int i = 0; i < tiles.Length; i++) cells[i] = tiles[i];
        BgMap = new TilemapEdit(cells, BgCols, BgRows, 16,
                                (c, r) => (c / 16) * 0x1B0 + r * 16 + (c % 16));
        BgMap.Committed += () =>
        {
            rom.BgTilemaps[LevelNum] = [.. BgMap.Cells.Select(v => (ushort)v)];
            if (Project is not null)
            {
                var (low, page) = BgImage.Split(rom.BgTilemaps[LevelNum]);
                var state = Project.Data.Level(LevelNum);
                state.BgTilemap = Convert.ToBase64String(low);
                state.BgTilemapPages = Convert.ToBase64String(page);
                Project.MarkDirty();
            }
            touched.Add(LevelNum);
            RecomposeScene();
        };
    }

    /// <summary>The layer-3 tilemap as a paintable grid, when the level has one; the first
    /// commit forks vanilla's shared map into a per-level override.</summary>
    private void OpenLayer3Map(Rom rom, LevelScene s)
    {
        if (Layer3.LevelTilemap(rom, LevelNum, s.Level.Header.LevelMode, Layer3.Option(rom, LevelNum))
            is { } map)
        {
            Layer3Map = new TilemapEdit(map, Layer3.Cols, Layer3.Rows, 8, Layer3.CellIndex);
            Layer3Map.Committed += () =>
            {
                // The first stroke turns a level using vanilla's shared (mode, option) tilemap
                // into one with a map of its own — the same move LM makes when you edit a shared
                // background, and the only way to avoid editing every level that shares it.
                rom.Layer3Tilemaps[LevelNum] = Layer3.ToBytes(Layer3Map.Cells);
                if (Project is not null)
                {
                    Project.Data.Level(LevelNum).Layer3Tilemap =
                        Convert.ToBase64String(rom.Layer3Tilemaps[LevelNum]);
                    Project.MarkDirty();
                }
                touched.Add(LevelNum);
                // The level canvas composes layer 3 INTO the scene's pixels (LevelScene.Build),
                // so like the layer-2 commit above it has to rebuild the scene — otherwise the
                // Background tab shows the paint and the level canvas keeps the old picture until
                // something unrelated rebuilds it, which read as "the edit did not save".
                RecomposeScene();
            };
        }
    }

    /// <summary>Pixels for one BG Map16 tile, for the Background canvas and its drawer.</summary>
    public uint[]? BgCellPixels(int tile, int phase = 0)
        => Scene?.BgCaches[phase & 3] is { } cache && (uint)(tile & 0x1FF) < cache.Length
           ? cache[tile & 0x1FF] : null;

    /// <summary>Pixels for one layer-3 tilemap WORD — tile, palette group and both flips, which
    /// is why it is keyed by the word rather than by the tile number. Cached per distinct word:
    /// a 64x64 map redraws on every stamp and only ever names a handful of them.</summary>
    public uint[]? Layer3CellPixels(int word)
    {
        if (Rom is not { } r || Scene?.Palettes[0] is not { } pal) return null;
        if (word < 0 || (word & 0x3FF) >= Layer3.TileCount) return null;
        if (layer3Cells.TryGetValue(word, out var hit)) return hit;
        layer3Tiles ??= Layer3.Tiles(r, LevelNum);
        return layer3Cells[word] = Layer3.CellPixels(word, layer3Tiles, pal);
    }

    private readonly Dictionary<int, uint[]?> layer3Cells = [];
    private byte[]?[]? layer3Tiles;

    /// <summary>Drop the layer-3 pixel caches — a repointed LG slot or a palette edit changes
    /// what every word draws as.</summary>
    private void InvalidateLayer3Cells() { layer3Cells.Clear(); layer3Tiles = null; }

    /// <summary>Draw the level's layer 3 on the LEVEL canvas too, where the console puts it —
    /// above the background image or below it as the level mode's screen registers say, blended
    /// where its colour math says, and in front of the level for the cells the header's Layer 3
    /// Priority lifts. ON by default: a layer the level really draws is part of the picture, and
    /// off it looked like the level simply had none.
    ///
    /// Still a PREVIEW in one respect: layer 3 scrolls at its own rate, so where it sits over a
    /// LEVEL is a moving target. It is drawn from the level's top-left and wraps every 512px,
    /// which is what the console's 64x64 tilemap does.</summary>
    public bool PreviewLayer3 { get; private set; } = true;

    /// <summary>Turn the preview on or off and recompose. False when nothing changed.</summary>
    public bool SetPreviewLayer3(bool on)
    {
        if (PreviewLayer3 == on) return false;
        PreviewLayer3 = on;
        RecomposeScene();
        return true;
    }

    /// <summary>True when this level draws an imported tilemap rather than vanilla's.</summary>
    public bool Layer3TilemapImported => Rom is { } r && HasLevel && r.Layer3Tilemaps.ContainsKey(LevelNum);

    /// <summary>True when this level's background is one the project edited.</summary>
    public bool BgTilemapEdited => Rom is { } r && HasLevel && r.BgTilemaps.ContainsKey(LevelNum);

    /// <summary>The one BG page this level's background can use, or null when any page will do.
    /// A vanilla stream takes its page from its address, and a base without the custom-background
    /// hook can write nothing else — so on such a base a tile picked from the other page is
    /// remapped to this page's tile of the same number at paint time, which is what LM does with
    /// an out-of-bank paste (§10c), rather than showing one tile and building another.</summary>
    public int? BgFixedPage
        => Rom is { } r && HasLevel && r.Layer2IsBackground(LevelNum) && !r.HasLmLayer2Custom
           ? BgImage.PageFor(r.Layer2Pointer(LevelNum) & 0xFFFF) : null;

    /// <summary>The drawer's tile as this level can paint it: itself, or its number on the fixed page.</summary>
    public int BgPaintable(int tile) => BgFixedPage is { } p ? p << 8 | tile & 0xFF : tile;

    /// <summary>
    /// Import a raw layer-3 tilemap for this level — LM's LT3 file shape, a flat little-endian
    /// 16-bit map of 0x800, 0x1000 or 0x2000 bytes (0x2000 being the whole 64x64 window).
    ///
    /// The build inserts it as an ExGFX file and points the record's LT3 slot at it, so it
    /// reaches the console (CONTRACT §12b) — on a base that carries LM's tilemap loader. On one
    /// that does not it still renders and still persists, and the build says so.
    /// </summary>
    public bool ImportLayer3Tilemap(string path)
    {
        if (Rom is null || !HasLevel) { Report("no level open"); return false; }
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (FileProblem.IsFile(e)) { Fail(FileProblem.From(e, "read the file", path)); return false; }
        catch (Exception e) { Report($"import failed: {e.Message}"); return false; }

        return ImportLayer3Bytes(bytes, Path.GetFileName(path));
    }

    /// <summary>The import proper, from bytes already in hand — a file, or a saved tilemap.</summary>
    private bool ImportLayer3Bytes(byte[] bytes, string what)
    {
        if (Rom is null || !HasLevel) { Report("no level open"); return false; }
        if (!Layer3.IsTilemapSize(bytes.Length))
        {
            Report($"import rejected: {what} is 0x{bytes.Length:X} bytes — "
                 + "a layer-3 tilemap is 0x800, 0x1000 or 0x2000");
            return false;
        }
        Rom.Layer3Tilemaps[LevelNum] = bytes;
        if (Project is not null)
        {
            Project.Data.Level(LevelNum).Layer3Tilemap = Convert.ToBase64String(bytes);
            Project.MarkDirty();
        }
        touched.Add(LevelNum);
        // The paintable grid is built from the tilemap at ShowLevel time, so it still holds the
        // OLD map until it is rebuilt — an import that did not do this looked like it had done
        // nothing until the level was switched away and back. The level canvas has the same
        // problem for the same reason (layer 3 is composed into the scene), hence the recompose.
        OpenBackgroundEdits();
        RecomposeScene();
        Report($"layer 3 tilemap ← {what} (0x{bytes.Length:X} bytes)");
        return true;
    }

    /// <summary>
    /// Write this level's layer-3 tilemap out as an LM-shaped LT3 file — the same flat
    /// little-endian 0x2000 <see cref="ImportLayer3Tilemap"/> takes, so a map painted here can go
    /// back into Lunar Magic, into another level, or into a backup.
    ///
    /// It exports what the level DRAWS, painted or not: a level still on vanilla's shared
    /// (mode, option) tilemap has never forked one of its own, and refusing to export until the
    /// first stroke would make "save what I am looking at" fail on exactly the picture someone
    /// wants to start from.
    /// </summary>
    public bool ExportLayer3Tilemap(string path)
    {
        if (Layer3Map is not { } map) { Report("this level has no layer 3 to export"); return false; }
        if (!Guard("export the tilemap", () => File.WriteAllBytes(path, Layer3.ToBytes(map.Cells)), path)) return false;
        Report($"wrote {Path.GetFileName(path)} — 0x{Layer3.MapWords * 2:X} bytes, "
             + $"{Layer3.Cols}x{Layer3.Rows} words");
        return true;
    }

    /// <summary>Drop an imported tilemap, back to vanilla's (level mode, option) pick.</summary>
    public bool ClearLayer3Tilemap()
    {
        if (Rom is null || !Rom.Layer3Tilemaps.Remove(LevelNum)) return false;
        if (Project is not null)
        {
            Project.Data.Level(LevelNum).Layer3Tilemap = null;
            Project.MarkDirty();
        }
        touched.Add(LevelNum);
        OpenBackgroundEdits();                    // same reason as the import: rebuild what is painted on
        RecomposeScene();
        Report("layer 3 tilemap ← the base ROM's");
        return true;
    }

    // ---- saved tilemaps ----
    // A project-scoped library of named maps for either layer, so a background painted once can
    // go onto any level. It lives in the .pdp beside the levels that use it, in exactly the bytes
    // the per-level field holds — applying one is the import path, from memory.

    /// <summary>The saved tilemaps for <paramref name="layer"/> (2 or 3), by name.</summary>
    public IEnumerable<string> TilemapPresets(int layer)
        => Project is null ? []
         : Project.Data.Tilemaps.Where(kv => kv.Value.Layer == layer).Select(kv => kv.Key)
                               .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Keep the current level's map for <paramref name="layer"/> under a name. What the level
    /// DRAWS, the export rule: a level still on vanilla's shared layer 3 can be the start of a
    /// library entry too. A name belongs to one layer — reusing it across layers would let a
    /// layer-3 save silently replace a layer-2 map of the same name.
    /// </summary>
    public bool SaveTilemapPreset(string name, int layer)
    {
        if (Project is null) { Report("no project open"); return false; }
        name = name.Trim();
        if (name.Length == 0) { Report("a tilemap needs a name"); return false; }
        if (Project.Data.Tilemaps.TryGetValue(name, out var had) && had.Layer != layer)
        { Report($"“{name}” is already a layer {had.Layer} tilemap"); return false; }
        byte[]? bytes = layer switch
        {
            3 => Layer3Map is { } m ? Layer3.ToBytes(m.Cells) : null,
            2 => BgMap is { } b ? [.. b.Cells.SelectMany(v => new[] { (byte)v, (byte)(v >> 8) })] : null,
            _ => null,
        };
        if (bytes is null) { Report($"this level has no layer {layer} to save"); return false; }
        Project.Data.Tilemaps[name] = new() { Layer = layer, Data = Convert.ToBase64String(bytes) };
        Project.MarkDirty();
        Report($"saved layer {layer} tilemap “{name}”");
        return true;
    }

    /// <summary>Put a saved map on the current level. Layer 3 is the file import from memory;
    /// layer 2 needs the level to have a background image, and one of the same size.</summary>
    public bool ApplyTilemapPreset(string name)
    {
        if (Project is null || Rom is null || !HasLevel) { Report("no level open"); return false; }
        if (!Project.Data.Tilemaps.TryGetValue(name, out var preset))
        { Report($"no saved tilemap “{name}”"); return false; }
        var bytes = Convert.FromBase64String(preset.Data);
        if (preset.Layer == 3) return ImportLayer3Bytes(bytes, $"“{name}”");

        if (BgMap is not { } bg)
        { Report("this level's layer 2 is an object stream — there is no background to replace"); return false; }
        // Two bytes a cell now (low, page); a preset saved when it was one byte takes this
        // level's own pages, which is the only page it could have meant.
        ushort[] tiles;
        if (bytes.Length == bg.Cells.Length * 2)
        {
            tiles = new ushort[bg.Cells.Length];
            for (int i = 0; i < tiles.Length; i++) tiles[i] = (ushort)(bytes[2 * i] | bytes[2 * i + 1] << 8);
        }
        else if (bytes.Length == bg.Cells.Length) tiles = BgImage.Join(bytes, BgImage.PagePlane(Rom, LevelNum));
        else { Report($"“{name}” holds {bytes.Length} bytes; this level's background has {bg.Cells.Length} cells"); return false; }
        if (BgFixedPage is { } fixedPage) for (int i = 0; i < tiles.Length; i++) tiles[i] = (ushort)BgPaintable(tiles[i]);
        Rom.BgTilemaps[LevelNum] = tiles;
        var (low, page) = BgImage.Split(tiles);
        var state = Project.Data.Level(LevelNum);
        state.BgTilemap = Convert.ToBase64String(low);
        state.BgTilemapPages = Convert.ToBase64String(page);
        Project.MarkDirty();
        touched.Add(LevelNum);
        OpenBackgroundEdits();
        RecomposeScene();
        Report($"layer 2 background ← “{name}”");
        return true;
    }

    public bool DeleteTilemapPreset(string name)
    {
        if (Project is null || !Project.Data.Tilemaps.Remove(name)) return false;
        Project.MarkDirty();
        Report($"deleted tilemap “{name}”");
        return true;
    }

    /// <summary>
    /// The level's advanced layer-3 bypass, or null when it has none — in which case its layer 3
    /// scrolls and blends however its Layer 3 Option implies, which for "Tileset Specific" means
    /// however the level's TILESET does (CONTRACT §12b). This is the override for that.
    /// </summary>
    public Layer3.Advanced? Layer3Advanced
        => Rom is { } r && HasLevel ? r.LmLayer3Advanced(LevelNum) : null;

    /// <summary>Whether the base ROM can run advanced settings at all. Without LM's reader they
    /// still store and still show here, but the game never looks at them.</summary>
    public bool Layer3AdvancedSupported => Rom is { } r && r.HasLmLayer3Advanced;

    /// <summary>Set (or, with null, clear) the level's advanced layer-3 bypass. Recorded as a
    /// session override so that clearing it survives a save on a base ROM that has one.</summary>
    public bool ApplyLayer3Advanced(Layer3.Advanced? adv)
    {
        if (Rom is not { } r || !HasLevel) { Report("no level open"); return false; }
        r.Layer3AdvancedOverrides[LevelNum] = adv;
        if (Project is not null)
        {
            var st = Project.Data.Level(LevelNum);
            st.Layer3Advanced = adv;
            st.Layer3AdvancedOff = adv is null;
            Project.MarkDirty();
        }
        touched.Add(LevelNum);
        // These are visible on the level canvas now — where layer 3 sits, and whether it covers
        // the background image or adds into it — so the picture has to follow the dialog.
        RecomposeScene();
        Report(adv is null ? "advanced layer 3 settings off"
             : $"advanced layer 3: {Layer3.VScrollNames[adv.Value.VScroll]} / "
             + $"{Layer3.HScrollNames[adv.Value.HScroll]}"
             + (Layer3AdvancedSupported ? "" : "  (editor-only — base lacks LM's reader)"));
        return true;
    }

    /// <summary>Whether an option value would actually reach a tilemap on THIS level — the
    /// pointer table is indexed by (level mode, option) and only covers modes 0-14, so a legal
    /// option can still land on nothing (CONTRACT §12b).</summary>
    public bool Layer3HasTilemap(int option)
        => Rom is { } r && Scene is { } s
           && Layer3.LevelTilemap(r, LevelNum, s.Level.Header.LevelMode, option) is not null;

    /// <summary>The level's layer 3 drawn as one 512x512 image, empty when it has none. Not
    /// per-phase: its GFX are the fixed 2bpp files and its colours sit outside the animated
    /// ones, so unlike the background it does not move between phases.</summary>
    public (uint[] Px, int W, int H) Layer3Image()
    {
        if (Rom is not { } r || Scene is not { } s || s.Palettes[0] is not { } pal) return ([], 0, 0);
        return Layer3.LevelTilemap(r, LevelNum, s.Level.Header.LevelMode, Layer3.Option(r, LevelNum)) is { } map
            ? Layer3.Render(map, Layer3.Tiles(r, LevelNum), pal, pal.Rgba[0]) : ([], 0, 0);
    }

    /// <summary>The 512 layer-3 8x8s as a picker sheet, in BG palette 2 (CGRAM 08-0B) — the
    /// first of the four groups the layer-3 colours occupy.</summary>
    public (uint[] Px, int W, int H) Layer3Sheet()
        => Rom is { } r && Scene?.Palettes[0] is { } pal
           ? GfxSheets.Tiles(Layer3.Tiles(r, LevelNum), pal, 0, colorOffset: 8) : ([], 0, 0);
}
