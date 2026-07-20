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
    public static uint[][] ComposeAll(Rom rom, LevelHeader h, int level = -1, int animPhase = 0)
    {
        var defPtr = BuildDefPointers(rom, h.Tileset);
        var fg = Gfx.FgTiles.Load(rom, h.Tileset, level, animPhase);   // bypass + animation phase
        var pal = Palette.Load(rom, h, level, animPhase);   //             + LM custom palette
        var tiles = new uint[rom.Map16TileCount][];         // 0x200, + LM extended pages (§7a)
        for (int t = 0; t < FgTiles; t++)
            tiles[t] = Compose(Definition(rom, defPtr, t), fg.Fetch, pal);
        for (int t = FgTiles; t < tiles.Length; t++)
            tiles[t] = Compose(LmExtendedDef(rom, t), fg.Fetch, pal);
        return tiles;
    }

    /// <summary>Compose the 0x200 BG Map16 tiles (defs at fixed $0D9100 + idx*8, CONTRACT §10).</summary>
    public static uint[][] ComposeAllBg(Rom rom, LevelHeader h, int level = -1, int animPhase = 0)
    {
        var fg = Gfx.FgTiles.Load(rom, h.Tileset, level, animPhase);
        var pal = Palette.Load(rom, h, level, animPhase);
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
        // Horizontal modes show 27 rows (16x27 screens); rows 27-31 exist in the object
        // grid but the game never displays them. Vertical modes keep the full grid.
        int rows = rom.IsVerticalMode(h.LevelMode) ? grid.Height : Math.Min(27, grid.Height);
        int W = grid.Width * 16, H = rows * 16;
        var img = new uint[W * H];
        Array.Fill(img, backdrop);

        if (level >= 0 && Level.DecodeBgImage(rom, level) is { } bgImg)
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
    /// Tile a decoded BG image across the canvas (repeats every 2 screens). Tilemap screens
    /// are 16x27 tiles (0x1B0 bytes/screen, same as layer 1) — NOT 16x32.
    /// </summary>
    public static void DrawBgImage(uint[] img, int W, int H, int gridW, ushort[] bgImg, uint[][] bgCache)
    {
        for (int cy = 0; cy < Math.Min(27, H / 16); cy++)
            for (int cx = 0; cx < gridW; cx++)
            {
                int within = cx & 0x1F;                          // 2-screen horizontal repeat
                int idx = bgImg[(within / 16) * 0x1B0 + cy * 16 + (within & 0x0F)];
                uint[] tile = bgCache[idx & 0x1FF];
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        uint c = tile[y * 16 + x];
                        if (c != 0) img[(cy * 16 + y) * W + (cx * 16 + x)] = c;
                    }
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
        ushort[]? bgImg = null, uint[][]? bgCache = null, Map16Grid? l2 = null, int visibleRows = 27)
    {
        int rows = Math.Min(visibleRows, grid.Height);
        int W = grid.Width * 16, H = rows * 16;
        var img = new uint[W * H];
        Array.Fill(img, backdrop);
        if (bgImg is not null && bgCache is not null) DrawBgImage(img, W, H, grid.Width, bgImg, bgCache);
        else if (l2 is not null) DrawGrid(img, W, H, l2, cache);
        DrawGrid(img, W, H, grid, cache);
        return (img, W, H);
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
    /// The 4 words (TL, BL, TR, BR) of an extended Map16 tile (0x200-0xFFF) from LM's def
    /// region: def(tile) = bank:(imm + tile*8) (CONTRACT §7a-rev). Caller must ensure
    /// rom.LmMap16Base >= 0 and tile &lt; rom.Map16TileCount.
    /// </summary>
    public static Word[] LmExtendedDef(Rom rom, int tile)
    {
        var (imm, bank) = rom.LmMap16Defs;
        int fo = rom.FileOffset((bank << 16) | (imm + tile * 8));
        var w = new Word[4];
        for (int i = 0; i < 4; i++)
            w[i] = new Word((ushort)(rom.Data[fo + i * 2] | (rom.Data[fo + i * 2 + 1] << 8)));
        return w;
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
