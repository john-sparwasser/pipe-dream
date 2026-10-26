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
    public static uint[][] ComposeAll(Rom rom, LevelHeader h, Gfx.FgTiles fg, Palette pal, int wordOr = 0)
    {
        var defPtr = BuildDefPointers(rom, h.Tileset);
        // 0x200, + LM extended pages (§7a) up to the highest range; a tile in a hole stays null.
        var tiles = new uint[rom.Map16TileEnd][];
        for (int t = 0; t < FgTiles; t++)
            tiles[t] = Compose(Or(Definition(rom, defPtr, t), wordOr), fg.Fetch, pal);
        for (int t = FgTiles; t < tiles.Length; t++)
            if (rom.HasMap16Def(t)) tiles[t] = Compose(Or(LmExtendedDef(rom, t), wordOr), fg.Fetch, pal);
        return tiles;
    }

    private static Word[] Or(Word[] words, int bits)
        => bits == 0 ? words : [.. words.Select(w => new Word((ushort)(w.Raw | bits)))];

    /// <summary>
    /// What the game ORs into every layer-2 tilemap word it uploads for a level whose layer 2 is level data: $1000 — the
    /// palette's high bit, so palettes 0-3 become 4-7 — on tileset 3, nothing on the rest. `$05:8B9D` and `$05:8C81`:
    /// `LDA $1931 : CMP #$03 : BNE : LDY #$1000 : STY $03`. It is why Valley of Bowser's moving walls are yellow where the
    /// same tiles on layer 1 are grey, and Lunar Magic draws them so (009, 115, 1E2, 1E3, 1EC, 1EF: 4-26% → about 1%).
    /// </summary>
    public static int Layer2WordOr(int tileset) => tileset == 3 ? 0x1000 : 0;

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
                cache[t] = !rom.HasMap16Def(t) ? null!
                         : Compose(t < FgTiles ? Definition(rom, defPtr, t) : LmExtendedDef(rom, t), fg.Fetch, pal);
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
        var fg = Gfx.FgTiles.Load(rom, h.Tileset, level, animPhase);
        var pal = Palette.Load(rom, h, level, animPhase);
        var cache = ComposeAll(rom, h, fg, pal);
        bool vertical = rom.IsVerticalMode(h.LevelMode);
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
            DrawBgImage(img, W, H, grid.Width, bgImg, bgCache, BgImage.Period(rom, level), Layer2Halved(rom, h.LevelMode));
        }
        else if (level >= 0 && ObjectEngine.RenderLayer2(rom, h, level) is { } l2grid)
        {
            int bits = Layer2WordOr(h.Tileset);
            var l2Cache = bits != 0 ? ComposeAll(rom, h, fg, pal, bits) : cache;
            DrawGrid(img, W, H, l2grid, l2Cache, Layer2Halved(rom, h.LevelMode), ComposePipes(rom, fg, pal, vertical, bits));
        }

        DrawGrid(img, W, H, grid, cache, pipes: ComposePipes(rom, fg, pal, vertical));
        return (img, W, H);
    }

    /// <summary>
    /// Tile a decoded BG image across the canvas: it repeats every 2 screens horizontally and every 32 rows vertically,
    /// layer 2's tilemap wrap, which vertical levels show. Only the first <paramref name="period"/> rows hold picture — 27
    /// for a vanilla background, 32 for LM's custom kind (<see cref="BgImage.Period"/>; cells by <see cref="BgImage.Cell"/>)
    /// — and the rest stay empty, as Lunar Magic draws them. <paramref name="half"/> draws it at half brightness
    /// (<see cref="Layer2Halved"/>).
    /// </summary>
    public static void DrawBgImage(uint[] img, int W, int H, int gridW, ushort[] bgImg, uint[][] bgCache, int period = BgImage.VanillaRows,
                                   bool half = false)
    {
        for (int cy = 0; cy < H / 16; cy++)
            for (int cx = 0; cx < gridW; cx++)
            {
                int row = cy % BgImage.CustomRows;
                if (row >= period) continue;
                int within = cx & 0x1F;                          // 2-screen horizontal repeat
                int idx = bgImg[BgImage.Cell(within, row)];
                uint[] tile = bgCache[idx & 0x1FF];
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        uint c = tile[y * 16 + x];
                        if (c != 0) img[(cy * 16 + y) * W + (cx * 16 + x)] = half ? Half(c) : c;
                    }
            }
    }

    /// <summary>
    /// Whether a level mode shows layer 2 at half brightness: its colour math halves (CGADSUB bit 6) and layer 2 takes part,
    /// on the subscreen or in the math itself. That is modes 0C, 0D and 11 — the dark-background ghost houses and castles —
    /// and those are exactly the vanilla levels Lunar Magic draws with a halved layer 2 (004, 0F8, 114, 1D9, 1EA, 1FA,
    /// 10E, 1BD). Layer 1 stays as it is, as in LM.
    /// </summary>
    public static bool Layer2Halved(Rom rom, int mode)
    {
        int sub = rom.ReadByte(Layer3.SubScreenTable + (mode & 0x1F));
        int math = rom.ReadByte(Layer3.CgAdSubTable + (mode & 0x1F));
        return (math & 0x40) != 0 && ((sub & 0x02) != 0 || (math & 0x02) != 0);
    }

    /// <summary>A colour at half brightness, the way the console halves: on the 5-bit channels, then widened again.</summary>
    public static uint Half(uint c)
    {
        uint o = c & 0xFF000000;
        for (int s = 0; s <= 16; s += 8)
        {
            uint v = (c >> s & 0xFF) >> 3 >> 1;
            o |= (v << 3 | v >> 2) << s;
        }
        return o;
    }

    /// <summary>
    /// Tile a rendered layer-3 surface across the canvas. A PREVIEW: the real thing scrolls at
    /// its own rate and sits wherever the level's scroll settings put it, none of which is
    /// modelled — this repeats it from the top-left, the way the background image repeats, so
    /// the whole canvas shows something rather than one 512px corner.
    /// </summary>
    /// <param name="blend">Add what is already there instead of covering it — CGADSUB, which
    /// names layer 3 a colour-math target so the subscreen adds into it.</param>
    /// <param name="half">The maths' half bit: the sum is halved rather than saturating. SMW's
    /// own mode-0 value ($24) does NOT set it, so an ordinary level's layer 3 comes out bright.</param>
    /// <param name="ox">Layer 3's own scroll, in pixels: the console shows tilemap pixel
    /// x + $22 at screen x, so a positive offset moves the picture LEFT. Same for
    /// <paramref name="oy"/> and $24. This is where the level's Initial X/Y Position lands. Rows a negative
    /// <paramref name="oy"/> puts above the tilemap's top stay empty rather than wrapping round, as Lunar Magic draws a
    /// tide: nothing above the level's water.</param>
    public static void DrawLayer3(uint[] img, int W, int H, uint[] l3, int l3W, int l3H,
                                  bool blend = false, bool half = false, int ox = 0, int oy = 0)
    {
        if (l3W <= 0 || l3H <= 0) return;
        ox = ((ox % l3W) + l3W) % l3W;                  // negative offsets wrap the other way
        for (int y = 0; y < H; y++)
        {
            if (y + oy < 0) continue;
            int row = (y + oy) % l3H;
            for (int x = 0; x < W; x++)
            {
                uint c = l3[row * l3W + (x + ox) % l3W];
                if (c == 0) continue;               // colour 0 is transparent on BG3
                img[y * W + x] = blend ? Add(img[y * W + x], c, half) : c;
            }
        }
    }

    /// <summary>Two colours added channel by channel, saturating — or halved first, which is
    /// what the maths' half bit does. Alpha stays opaque.</summary>
    private static uint Add(uint a, uint b, bool half)
    {
        uint o = 0xFF000000;
        for (int s = 0; s <= 16; s += 8)
        {
            int v = (int)(a >> s & 0xFF) + (int)(b >> s & 0xFF);
            o |= (uint)(half ? v >> 1 : Math.Min(v, 255)) << s;
        }
        return o;
    }

    // ---- pipe colours ----

    /// <summary>The eight pipe tiles, 0x133-0x13A, whose definitions the game swaps per screen.</summary>
    public const int PipeFirst = 0x133, PipeCount = 8;

    /// <summary>`$058776`: the four pipe sets' addresses in bank $0D, by screen mod 4 — orange, green, yellow, blue.</summary>
    private const int PipeSetTable = 0x058776;

    /// <summary>
    /// The pipe tiles' four colour sets, or null when the ROM no longer has the routine that picks them. Before the game
    /// draws a column it points tiles 0x133-0x13A at set <c>(column >> 4) &amp; 3</c> of <see cref="PipeSetTable"/>
    /// (<c>LDA $47 : LSR #3 : AND #$0006 : TAX : ... LDA $058776,X : STA $0FBE,Y</c>, the read at `$05:80E8`, and at
    /// `$05:87B7` for the other layer), so a pipe's colour is its screen's number mod 4 — down the level in a vertical one.
    /// Lunar Magic draws them so; every pipe here was the green set.
    /// </summary>
    public static int[]? PipeSets(Rom rom)
    {
        // The read and store of that loop, at both sites.
        foreach (int at in new[] { 0x0580E8, 0x0587B7 })
            if (rom.ReadByte(at) != 0xBF || rom.ReadValue(at + 1, 3) != PipeSetTable || rom.ReadByte(at + 4) != 0x99) return null;
        return [.. Enumerable.Range(0, 4).Select(i => 0x0D0000 | rom.ReadValue(PipeSetTable + i * 2, 2))];
    }

    /// <summary>The pipe tiles as each set draws them: [set][tile - <see cref="PipeFirst"/>].</summary>
    public readonly record struct PipeColours(uint[][][] Sets, bool Vertical)
    {
        /// <summary>The picture for <paramref name="tile"/> at cell (cx, cy), or null when it is not a pipe tile.</summary>
        public uint[]? At(int tile, int cx, int cy)
            => tile is >= PipeFirst and < PipeFirst + PipeCount ? Sets[((Vertical ? cy : cx) >> 4) & 3][tile - PipeFirst] : null;
    }

    public static PipeColours? ComposePipes(Rom rom, Gfx.FgTiles fg, Palette pal, bool vertical, int wordOr = 0)
    {
        if (PipeSets(rom) is not { } sets) return null;
        var px = new uint[4][][];
        for (int s = 0; s < 4; s++)
        {
            px[s] = new uint[PipeCount][];
            for (int t = 0; t < PipeCount; t++)
            {
                int fo = rom.FileOffset(sets[s] + t * 8);
                var w = new Word[4];
                for (int i = 0; i < 4; i++) w[i] = new Word((ushort)(rom.Data[fo + i * 2] | rom.Data[fo + i * 2 + 1] << 8));
                px[s][t] = Compose(Or(w, wordOr), fg.Fetch, pal);
            }
        }
        return new PipeColours(px, vertical);
    }

    /// <summary>Draw a Map16 grid onto an existing canvas (transparent pixels leave it).</summary>
    public static void DrawGrid(uint[] img, int W, int H, Map16Grid grid, uint[][] cache, bool half = false, PipeColours? pipes = null)
    {
        for (int cy = 0; cy < Math.Min(grid.Height, H / 16); cy++)
            for (int cx = 0; cx < grid.Width; cx++)
            {
                int t = grid.Get(cx, cy);
                if (t == Map16Grid.Empty) continue;
                uint[]? tile = (t & ObjectEngine.Marker) != 0 || t >= cache.Length ? null : pipes?.At(t, cx, cy) ?? cache[t];
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        uint c = tile is null ? 0xFFFF00FFu : tile[y * 16 + x];
                        if (c == 0) continue;                   // transparent → keep what's behind
                        img[(cy * 16 + y) * W + (cx * 16 + x)] = half ? Half(c) : c;
                    }
            }
    }

    /// <summary>
    /// A level's layer 3, ready to compose: the cells that stay at the back, the cells that come
    /// out in front (null unless the header grants BG3 priority), and whether the level's colour
    /// math has them blend with what they meet rather than cover it.
    /// </summary>
    public readonly record struct Layer3Draw(uint[] Back, uint[]? Front, int W, int H,
                                             Layer3.Screens Screens, int OffX = 0, int OffY = 0, bool FrontBlend = false);

    /// <summary>Compose a level canvas from precomputed caches (fast; for live edits).</summary>
    public static (uint[] px, int w, int h) ComposeLevel(uint[][] cache, uint backdrop, Map16Grid grid,
        ushort[]? bgImg = null, uint[][]? bgCache = null, Map16Grid? l2 = null, int visibleRows = 27,
        Layer3Draw? layer3 = null, int bgPeriod = BgImage.VanillaRows, bool halfL2 = false, uint[][]? l2Cache = null,
        PipeColours? pipes = null, PipeColours? l2Pipes = null)
    {
        int rows = Math.Min(visibleRows, grid.Height);
        int W = grid.Width * 16, H = rows * 16;
        return (ComposeLevelInto(new uint[W * H], cache, backdrop, grid, bgImg, bgCache, l2,
                                 visibleRows, layer3, bgPeriod, halfL2, l2Cache, pipes, l2Pipes),
                W, H);
    }

    /// <summary>
    /// The same, into a buffer the caller already has. A full-width level is 13.5MB a phase, so
    /// recomposing one repeatedly — which is what dragging a colour does — allocates and
    /// discards more than the work itself costs.
    /// </summary>
    public static uint[] ComposeLevelInto(uint[] img, uint[][] cache, uint backdrop, Map16Grid grid,
        ushort[]? bgImg, uint[][]? bgCache, Map16Grid? l2, int visibleRows,
        Layer3Draw? layer3 = null, int bgPeriod = BgImage.VanillaRows, bool halfL2 = false, uint[][]? l2Cache = null,
        PipeColours? pipes = null, PipeColours? l2Pipes = null)
    {
        int rows = Math.Min(visibleRows, grid.Height);
        int W = grid.Width * 16, H = rows * 16;
        Array.Fill(img, backdrop);
        // Layer 3 sits BEHIND layer 2 and layer 1, which is the whole reason a preview has to go
        // through the compose rather than being painted over the finished canvas. Its priority
        // cells are the exception, and only when the header sets mode 1's BG3-priority bit: then
        // they come out on top of everything instead — solid, as Lunar Magic draws them: a tide's water is its own
        // colour there (0C1, 002), where adding it to what is under it washed the level out. LM's CGADSUB box, ticked,
        // blends them (FrontBlend), which is what its help says the box is for.
        void L3(uint[] px, Layer3Draw d, bool blend)
            => DrawLayer3(img, W, H, px, d.W, d.H, blend, d.Screens.Half, d.OffX, d.OffY);

        if (layer3 is { Screens.AboveBg2: false } under) L3(under.Back, under, under.Screens.Blend);
        if (bgImg is not null && bgCache is not null) DrawBgImage(img, W, H, grid.Width, bgImg, bgCache, bgPeriod, halfL2);
        else if (l2 is not null) DrawGrid(img, W, H, l2, l2Cache ?? cache, halfL2, l2Pipes);
        if (layer3 is { Screens.AboveBg2: true, Screens.AboveBg1: false } over) L3(over.Back, over, over.Screens.Blend);
        DrawGrid(img, W, H, grid, cache, pipes: pipes);
        if (layer3 is { Screens.AboveBg1: true } top) L3(top.Back, top, top.Screens.Blend);
        if (layer3 is { Front: { } fp } front) L3(fp, front, front.FrontBlend && front.Screens.Blend);
        return img;
    }

    /// <summary>Compose the 512-tile picker sheet from a precomputed cache.</summary>
    public static (uint[] px, int w, int h) ComposeSheet(uint[][] cache, int cols = 16)
        => ComposeSheet(cache, 0, cache.Length, cols);

    /// <summary>The same, for <paramref name="count"/> tiles from <paramref name="from"/>. A tile
    /// with no definition (null) stays fully transparent, so whatever the view lays under the
    /// sheet — the empty-page tile — shows through the hole.</summary>
    public static (uint[] px, int w, int h) ComposeSheet(uint[][] cache, int from, int count, int cols = 16)
    {
        int rows = (count + cols - 1) / cols;
        int w = cols * 16, ht = rows * 16;
        var sheet = new uint[w * ht];
        for (int t = 0; t < count; t++)
        {
            var img = from + t < cache.Length ? cache[from + t] : null;
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
    /// tile's 0x1000-tile range (CONTRACT §7a-rev). Caller must ensure rom.HasMap16Def(tile).
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

    /// <summary>Where the BG Map16 table sits in tile numbering — LM's own: pages 80-81, tiles
    /// 0x8000+ (CONTRACT §10). Below it, 0x0000-0x7FFF, every tile is FG, including LM's
    /// pages 40-7F that object 0x29 places.</summary>
    public const int BgTileBase = 0x8000;

    /// <summary>
    /// ROM file offset of a Map16 tile's 8-byte definition (word order TL,BL,TR,BR), or
    /// -1 when the tile has no backing def. FG &lt; 0x200: vanilla per-tileset/shared bank-0D
    /// tables; FG 0x200+: LM's extended region (when allocated); BG 0x8000-0x81FF: the
    /// fixed $0D9100 table. This is the write target for tile editing.
    /// </summary>
    public static int DefFileOffset(Rom rom, int tileset, int tile)
    {
        if (tile < 0x200) return rom.FileOffset(BuildDefPointers(rom, tileset)[tile]);
        if (tile < 0x8000 && rom.HasMap16Def(tile)) return rom.FileOffset(rom.LmMap16DefAddr(tile));
        if (tile is >= BgTileBase and < BgTileBase + 0x200) return rom.FileOffset(0x0D9100 + (tile - BgTileBase) * 8);
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
