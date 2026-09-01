namespace PipeDream;

/// <summary>
/// Map16 tile definitions + composition. A Map16 tile (16×16) = 4 SNES tilemap words in
/// TL/BL/TR/BR order (CONTRACT.md §5, confirmed via consumer $00C17C). The vanilla per-tile
/// definition pointer table is assembled by CODE_0581FB from a per-tile bitmap ($0581BB)
/// selecting a tileset-specific region (TilesetMAP16Loc[tileset]) or a shared region ($8000),
/// both in bank $0D. Definitions live at bank $0D (confirmed empirically).
///
/// NOTE: composing correct pixels also needs the VRAM/GFX-slot mapping (which GFX file backs
/// each 8×8 tile number) — not yet implemented, so Compose takes a caller-supplied tile source.
/// </summary>
public static class Map16
{
    public const int FgTiles = 0x200;

    /// <summary>A decoded SNES tilemap word.</summary>
    public readonly struct Word(ushort raw)
    {
        public readonly ushort Raw = raw;
        public int Tile => Raw & 0x3FF;
        public int Palette => (Raw >> 10) & 0x07;
        public bool Priority => (Raw & 0x2000) != 0;
        public bool FlipX => (Raw & 0x4000) != 0;
        public bool FlipY => (Raw & 0x8000) != 0;
    }

    /// <summary>Build tile# → 8-byte-definition SNES address (bank $0D) for a tileset.</summary>
    public static int[] BuildDefPointers(Rom rom, int tileset)
    {
        var ptr = new int[FgTiles];
        int bitmapFo = rom.FileOffset(0x0581BB);
        int pTileset = rom.ReadValue(0x058000 + tileset * 2, 2);  // TilesetMAP16Loc[tileset]
        int pShared = 0x8000;
        int t = 0;
        for (int by = 0; by < 0x40; by++)
        {
            int bits = rom.Data[bitmapFo + by];
            for (int b = 0; b < 8; b++)
            {
                bool shared = (bits & 0x80) != 0;   // ASL -> carry = top bit; set = shared region
                bits = (bits << 1) & 0xFF;
                if (shared) { ptr[t] = 0x0D0000 | pShared; pShared += 8; }
                else { ptr[t] = 0x0D0000 | pTileset; pTileset += 8; }
                t++;
            }
        }
        // Tileset 0/7: animated status tiles override tiles 0x1C4-0x1C7 and 0x1EC-0x1EF.
        if (tileset == 0 || tileset == 7)
        {
            int q = 0x8A70;
            foreach (int baseTile in new[] { 0x1C4, 0x1EC })
                for (int k = 0; k < 4; k++) { ptr[baseTile + k] = 0x0D0000 | q; q += 8; }
        }
        return ptr;
    }

    /// <summary>
    /// Compose every FG Map16 tile (512) into its own 16×16 RGBA image — a reusable cache for
    /// both the tile sheet and the level canvas. Color 0 stays transparent (0 alpha).
    /// </summary>
    public static uint[][] ComposeAll(Rom rom, LevelHeader h, int level = -1, int animPhase = 0,
                                      Palette? palOverride = null)
        => ComposeAll(rom, h, Gfx.FgTiles.Load(rom, h.Tileset, level, animPhase),   // bypass + phase
                      palOverride ?? Palette.Load(rom, h, level, animPhase));       // + LM custom palette

    /// <summary>
    /// The same, over GFX that is already loaded. Recolouring a level re-composes every tile
    /// but the graphics have not moved — and loading them is the expensive half (~11ms a phase
    /// against ~2ms to compose all 512 tiles), so a live colour drag reuses them.
    /// </summary>
    public static uint[][] ComposeAll(Rom rom, LevelHeader h, Gfx.FgTiles fg, Palette pal)
    {
        var defPtr = BuildDefPointers(rom, h.Tileset);
        var tiles = new uint[rom.Map16TileCount][];         // 0x200, + LM extended pages (§7a)
        for (int t = 0; t < FgTiles; t++)
            tiles[t] = Compose(Definition(rom, defPtr, t), fg.Fetch, pal);
        for (int t = FgTiles; t < tiles.Length; t++)
            tiles[t] = Compose(LmExtendedDef(rom, t), fg.Fetch, pal);
        return tiles;
    }

    /// <summary>
    /// Recompose JUST these tiles into an existing cache. Editing a Map16 definition changes one
    /// tile's pixels, and composing all 512 to find that out is most of the cost of the edit.
    /// </summary>
    public static void ComposeInto(uint[][] cache, Rom rom, LevelHeader h, Gfx.FgTiles fg,
                                   Palette pal, IEnumerable<int> tiles)
    {
        var defPtr = BuildDefPointers(rom, h.Tileset);
        foreach (int t in tiles)
            if (t >= 0 && t < cache.Length)
                cache[t] = Compose(t < FgTiles ? Definition(rom, defPtr, t) : LmExtendedDef(rom, t),
                                   fg.Fetch, pal);
    }

    /// <summary>Compose the 0x200 BG Map16 tiles (defs at fixed $0D9100 + idx*8, CONTRACT §10).</summary>
    public static uint[][] ComposeAllBg(Rom rom, LevelHeader h, int level = -1, int animPhase = 0,
                                        Palette? palOverride = null)
        => ComposeAllBg(rom, Gfx.FgTiles.Load(rom, h.Tileset, level, animPhase),
                        palOverride ?? Palette.Load(rom, h, level, animPhase));

    /// <summary>The same, over already-loaded GFX — see the ComposeAll overload.</summary>
    public static uint[][] ComposeAllBg(Rom rom, Gfx.FgTiles fg, Palette pal)
    {
        var tiles = new uint[0x200][];
        for (int t = 0; t < 0x200; t++)
        {
            int fo = rom.FileOffset(0x0D9100 + t * 8);
            var w = new Word[4];
            for (int i = 0; i < 4; i++)
                w[i] = new Word((ushort)(rom.Data[fo + i * 2] | (rom.Data[fo + i * 2 + 1] << 8)));
            tiles[t] = Compose(w, fg.Fetch, pal);
        }
        return tiles;
    }

    /// <summary>
    /// Compose a full level canvas: backdrop, then layer 2 (background image or object
    /// layer), then the layer-1 grid. Markers render magenta.
    /// </summary>
    public static (uint[] px, int w, int h) ComposeLevel(Rom rom, LevelHeader h, Map16Grid grid, int level = -1, int animPhase = 0)
    {
        var cache = ComposeAll(rom, h, level, animPhase);
        uint backdrop = Palette.Load(rom, h, level).Rgba[0];
        // The engine sizes the grid to the level's height (27 rows vanilla, LM's LUT height
        // otherwise, a vertical level's full extent), so every row is shown. A 32-row grid from
        // the ported fallback still shows only the 27 the game draws.
        int rows = rom.IsVerticalMode(h.LevelMode) || grid.Height != 32 ? grid.Height : 27;
        int W = grid.Width * 16, H = rows * 16;
        var img = new uint[W * H];
        Array.Fill(img, backdrop);

        if (level >= 0 && LevelParser.DecodeBgImage(rom, level) is { } bgImg)
        {
            var bgCache = ComposeAllBg(rom, h, level, animPhase);
            DrawBgImage(img, W, H, grid.Width, bgImg, bgCache);
        }
        else if (level >= 0 && ObjectEngine.RenderLayer2(rom, h, level) is { } l2grid)
        {
            DrawGrid(img, W, H, l2grid, cache);
        }

        DrawGrid(img, W, H, grid, cache);
        return (img, W, H);
    }

    /// <summary>
    /// Tile a decoded BG image across the canvas (repeats every 2 screens horizontally AND
    /// every 27 rows vertically — vertical levels tile the same image down the level, as the
    /// game does). Tilemap screens are 16x27 tiles (0x1B0 bytes/screen) — NOT 16x32.
    /// </summary>
    public static void DrawBgImage(uint[] img, int W, int H, int gridW, ushort[] bgImg, uint[][] bgCache)
    {
        for (int cy = 0; cy < H / 16; cy++)
            for (int cx = 0; cx < gridW; cx++)
            {
                int within = cx & 0x1F;                          // 2-screen horizontal repeat
                int idx = bgImg[(within / 16) * 0x1B0 + (cy % 27) * 16 + (within & 0x0F)];
                uint[] tile = bgCache[idx & 0x1FF];
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        uint c = tile[y * 16 + x];
                        if (c != 0) img[(cy * 16 + y) * W + (cx * 16 + x)] = c;
                    }
            }
    }

    /// <summary>
    /// Tile a rendered layer-3 surface across the canvas. A PREVIEW: the real thing scrolls at
    /// its own rate and sits wherever the level's scroll settings put it, none of which is
    /// modelled — this repeats it from the top-left, the way the background image repeats, so
    /// the whole canvas shows something rather than one 512px corner.
    /// </summary>
    public static void DrawLayer3(uint[] img, int W, int H, uint[] l3, int l3W, int l3H)
    {
        if (l3W <= 0 || l3H <= 0) return;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                uint c = l3[(y % l3H) * l3W + (x % l3W)];
                if (c != 0) img[y * W + x] = c;      // colour 0 is transparent on BG3
            }
    }

    /// <summary>Draw a Map16 grid onto an existing canvas (transparent pixels leave it).</summary>
    public static void DrawGrid(uint[] img, int W, int H, Map16Grid grid, uint[][] cache)
    {
        for (int cy = 0; cy < Math.Min(grid.Height, H / 16); cy++)
            for (int cx = 0; cx < grid.Width; cx++)
            {
                int t = grid.Get(cx, cy);
                if (t == Map16Grid.Empty) continue;
                uint[]? tile = (t & ObjectEngine.Marker) != 0 || t >= cache.Length ? null : cache[t];
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        uint c = tile is null ? 0xFFFF00FFu : tile[y * 16 + x];
                        if (c == 0) continue;                   // transparent → keep what's behind
                        img[(cy * 16 + y) * W + (cx * 16 + x)] = c;
                    }
            }
    }

    /// <summary>Compose a level canvas from precomputed caches (fast; for live edits).</summary>
    public static (uint[] px, int w, int h) ComposeLevel(uint[][] cache, uint backdrop, Map16Grid grid,
        ushort[]? bgImg = null, uint[][]? bgCache = null, Map16Grid? l2 = null, int visibleRows = 27,
        (uint[] Px, int W, int H, bool Front)? layer3 = null)
    {
        int rows = Math.Min(visibleRows, grid.Height);
        int W = grid.Width * 16, H = rows * 16;
        return (ComposeLevelInto(new uint[W * H], cache, backdrop, grid, bgImg, bgCache, l2,
                                 visibleRows, layer3),
                W, H);
    }

    /// <summary>
    /// The same, into a buffer the caller already has. A full-width level is 13.5MB a phase, so
    /// recomposing one repeatedly — which is what dragging a colour does — allocates and
    /// discards more than the work itself costs.
    /// </summary>
    public static uint[] ComposeLevelInto(uint[] img, uint[][] cache, uint backdrop, Map16Grid grid,
        ushort[]? bgImg, uint[][]? bgCache, Map16Grid? l2, int visibleRows,
        (uint[] Px, int W, int H, bool Front)? layer3 = null)
    {
        int rows = Math.Min(visibleRows, grid.Height);
        int W = grid.Width * 16, H = rows * 16;
        Array.Fill(img, backdrop);
        // Layer 3 sits BEHIND layer 2 and layer 1 unless the header gives it priority, which is
        // the whole reason a preview has to go through the compose rather than being painted
        // over the finished canvas: on top it would hide the level it is meant to sit behind.
        if (layer3 is { Front: false } back) DrawLayer3(img, W, H, back.Px, back.W, back.H);
        if (bgImg is not null && bgCache is not null) DrawBgImage(img, W, H, grid.Width, bgImg, bgCache);
        else if (l2 is not null) DrawGrid(img, W, H, l2, cache);
        DrawGrid(img, W, H, grid, cache);
        if (layer3 is { Front: true } front) DrawLayer3(img, W, H, front.Px, front.W, front.H);
        return img;
    }

    /// <summary>Compose the 512-tile picker sheet from a precomputed cache.</summary>
    public static (uint[] px, int w, int h) ComposeSheet(uint[][] cache, int cols = 16)
    {
        int count = cache.Length;                        // 0x200 + LM extended pages
        int rows = (count + cols - 1) / cols;
        int w = cols * 16, ht = rows * 16;
        var sheet = new uint[w * ht];
        for (int t = 0; t < count; t++)
        {
            var img = cache[t];
            if (img is null) continue;
            int ox = (t % cols) * 16, oy = (t / cols) * 16;
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    uint c = img[y * 16 + x];
                    sheet[(oy + y) * w + (ox + x)] = c == 0 ? 0xFF303030u : c;
                }
        }
        return (sheet, w, ht);
    }

    /// <summary>Compose all 512 FG Map16 tiles into one RGBA sheet (cols wide, 16px each).</summary>
    public static (uint[] px, int w, int h) ComposeSheet(Rom rom, LevelHeader h, int cols = 16)
    {
        var tilesImg = ComposeAll(rom, h);
        int rows = (FgTiles + cols - 1) / cols;
        int w = cols * 16, ht = rows * 16;
        var sheet = new uint[w * ht];
        for (int t = 0; t < FgTiles; t++)
        {
            var img = tilesImg[t];
            int ox = (t % cols) * 16, oy = (t / cols) * 16;
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    uint c = img[y * 16 + x];
                    sheet[(oy + y) * w + (ox + x)] = c == 0 ? 0xFF303030u : c;   // grey = transparent
                }
        }
        return (sheet, w, ht);
    }

    /// <summary>
    /// The 4 words (TL, BL, TR, BR) of an extended Map16 tile (0x200+) from LM's def region:
    /// def(tile) = bank:(imm + tile*8) with (imm, bank) taken from the lookup slot covering
    /// tile's 0x1000-tile range (CONTRACT §7a-rev). Caller must ensure tile &lt;
    /// rom.Map16TileCount, which only counts ranges that have a slot.
    /// </summary>
    public static Word[] LmExtendedDef(Rom rom, int tile)
    {
        int addr = rom.LmMap16DefAddr(tile);
        // A hack can populate range 1 while leaving a hole below it; Map16TileCount stops at
        // the hole, but nothing stops a caller asking anyway. LM's default-empty def beats a
        // negative file offset.
        if (addr < 0) return [new Word(0x1004), new Word(0x1004), new Word(0x1004), new Word(0x1004)];
        int fo = rom.FileOffset(addr);
        var w = new Word[4];
        for (int i = 0; i < 4; i++)
            w[i] = new Word((ushort)(rom.Data[fo + i * 2] | (rom.Data[fo + i * 2 + 1] << 8)));
        return w;
    }

    /// <summary>Where this editor's virtual numbering puts the BG Map16 table — LM's pages
    /// 80-81, which it numbers 0x8000+ and this repo addresses as 0x4000+ (CONTRACT §10).</summary>
    public const int BgTileBase = 0x4000;

    /// <summary>
    /// ROM file offset of a Map16 tile's 8-byte definition (word order TL,BL,TR,BR), or
    /// -1 when the tile has no backing def. FG &lt; 0x200: vanilla per-tileset/shared bank-0D
    /// tables; FG 0x200+: LM's extended region (when allocated); BG 0x4000-0x41FF: the
    /// fixed $0D9100 table. This is the write target for tile editing.
    /// </summary>
    public static int DefFileOffset(Rom rom, int tileset, int tile)
    {
        if (tile < 0x200) return rom.FileOffset(BuildDefPointers(rom, tileset)[tile]);
        if (tile < rom.Map16TileCount) return rom.FileOffset(rom.LmMap16DefAddr(tile));
        if (tile is >= 0x4000 and < 0x4200) return rom.FileOffset(0x0D9100 + (tile - 0x4000) * 8);
        return -1;
    }

    /// <summary>The 4 words (TL, BL, TR, BR) of a Map16 tile.</summary>
    public static Word[] Definition(Rom rom, int[] defPtr, int tile)
    {
        int snes = defPtr[tile & (FgTiles - 1)];
        int fo = rom.FileOffset(snes);
        var w = new Word[4];
        for (int i = 0; i < 4; i++)
            w[i] = new Word((ushort)(rom.Data[fo + i * 2] | (rom.Data[fo + i * 2 + 1] << 8)));
        return w;
    }

    /// <summary>
    /// Compose a 16×16 RGBA image for a Map16 tile. <paramref name="fetch8"/> returns the 64
    /// palette-index pixels of an 8×8 VRAM tile by number; <paramref name="pal"/> is the level
    /// palette. Applies each quadrant word's palette row and H/V flip.
    /// </summary>
    public static uint[] Compose(Word[] words, Func<int, byte[]> fetch8, Palette pal)
    {
        var img = new uint[16 * 16];
        // quadrant screen offsets, matching word order TL, BL, TR, BR
        (int ox, int oy)[] q = { (0, 0), (0, 8), (8, 0), (8, 8) };
        for (int i = 0; i < 4; i++)
        {
            var w = words[i];
            var src = fetch8(w.Tile);
            int baseColor = w.Palette * 16;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int sx = w.FlipX ? 7 - x : x;
                    int sy = w.FlipY ? 7 - y : y;
                    int idx = src[sy * 8 + sx];
                    uint rgba = idx == 0 ? 0u : pal.Rgba[baseColor + idx]; // color 0 transparent
                    img[(q[i].oy + y) * 16 + (q[i].ox + x)] = rgba;
                }
        }
        return img;
    }
}
