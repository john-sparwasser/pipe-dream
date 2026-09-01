namespace PipeDream;

/// <summary>
/// Layer 3 — the scenery layer the status bar shares: cave walls, the castle entrance, mist,
/// water. CONTRACT §12b.
///
/// Three pieces, all fixed by the game's own PPU setup at <c>SetUpScreen</c> ($008A7F):
/// BG3SC = $53 puts a <b>64×64 tilemap at VRAM word $5000</b> (four 32×32 screens), and
/// BG34NBA = $04 puts its <b>character data at word $4000</b> — 512 2bpp tiles, which is
/// exactly the four GFX files $00A993 uploads back to back from there (LG1-LG4 = GFX 28-2B,
/// 0x800 bytes each). So a layer 3 is 512×512 pixels, and a tilemap word names one of 512
/// tiles. That geometry is also where the ExAnimation destination range 1C00-1DFF comes from
/// (§12e) and why LM's tilemap-bypass file size defaults to 0x2000 = 64×64 words.
///
/// The tilemap itself is not per level but per (level mode, layer-3 option): $009FB8 indexes
/// <see cref="PtrTable">Layer3Ptr</see> with <c>mode*3 + (option-1)</c> and runs the block it
/// finds through vanilla's stripe-image uploader ($00871E). Option 0 means the level has no
/// layer 3 at all.
///
/// Colours come from <see cref="Palette"/>'s $00B170 block — CGRAM 08-0F and 18-1F, i.e. BG
/// palettes 2, 3, 6 and 7, which is what a tilemap word's 3-bit palette field selects.
/// </summary>
public static class Layer3
{
    public const int Cols = 64, Rows = 64;      // BG3SC $53 bits 0-1 = 64×64 tiles
    public const int MapBase = 0x5000;          // BG3SC $53 bits 2-7: base word 0x14 × 0x400
    public const int MapWords = 0x1000;         // four 32×32 screens
    public const int TileCount = 0x200;         // BG34NBA $04: 4 files × 128 2bpp tiles at $4000
    public const int SlotTiles = 0x80;
    private const int ScreenCols = 32, ScreenRows = 32;

    /// <summary>
    /// Layer 3 is ALWAYS two bit planes. $00A993 streams 0x800 bytes per slot into a 128-tile
    /// window, so the depth is fixed by the upload, not by the file: vanilla 28-2B are listed
    /// 2bpp in <see cref="Gfx.FileBpp"/>, but an ExGFX file that a bypassed slot points at is
    /// not, and reading one at the ROM's depth halves its tile count and garbles every tile.
    /// </summary>
    public const int Bpp = 2;

    /// <summary>LG1-LG4, in the order $00A99F uploads them (LG1 first, at word $4000).</summary>
    public static readonly int[] VanillaGfx = [0x28, 0x29, 0x2A, 0x2B];

    /// <summary>LM's four Layer 3 Options, in its dropdown's order — the values of the
    /// per-level field. 0 and 3 are confirmed by controlled save; 1 and 2 are the two tides
    /// options, inferred from the order (CONTRACT §12b).</summary>
    public static readonly string[] OptionNames =
        ["Blank Layer 3", "Water, high and low tides", "Water, low tide only", "Tileset specific"];

    // The vanilla per-level byte LM's "Change Layer 3 Settings" dialog writes: bits 6-7 land in
    // $1BE3 at $05D928. MainEntrance carries the same bits as Layer3Option.
    private const int OptionTable = 0x05F200;

    // Layer3Ptr — 3-byte pointers, one per (mode, option-1). The table ends where the first
    // tilemap block (DATA_059087) starts, which is 45 entries: level modes 0-14 only.
    private const int PtrTable = 0x059000;
    private const int PtrCount = 45;

    /// <summary>The level's Layer 3 Options value, 0-3. 0 = the level has no layer 3.</summary>
    public static int Option(Rom rom, int level) => (rom.ReadByte(OptionTable + (level & 0x1FF)) >> 6) & 3;

    /// <summary>
    /// The four GFX files this level loads into the layer-3 window, LG1-LG4. Vanilla's 28-2B
    /// unless LM's per-level layer-3 GFX bypass repoints them — and its slots live in the SAME
    /// per-level record as the Super GFX Bypass, so there is no second table to find. A
    /// bypassed slot left at 0x7F keeps its vanilla file, as everywhere else in that record.
    /// </summary>
    public static int[] GfxFiles(Rom rom, int level)
        => level >= 0 && rom.LmLayer3Gfx(level) is { } lg
           ? [.. lg.Select((f, i) => f == 0x7F ? VanillaGfx[i] : f)]
           : VanillaGfx;

    /// <summary>
    /// The 512 layer-3 8×8 tiles as palette indices (0-3), in VRAM order — slot k holds tiles
    /// k*128..k*128+127. That is how $00A993's straight-through upload lays them out, and LM's
    /// own destination table at $0FFA7F agrees: LG1 → word $4000, LG2 → $4400, LG3 → $4800,
    /// LG4 → $4C00.
    /// </summary>
    public static byte[]?[] Tiles(Rom rom, int level = -1)
    {
        var tiles = new byte[]?[TileCount];
        var files = GfxFiles(rom, level);
        for (int slot = 0; slot < files.Length; slot++)
        {
            int file = files[slot];
            if (Gfx.Cached(rom, file) is not { } data) continue;
            int tb = Gfx.TileBytes(Bpp);
            for (int t = 0; t < SlotTiles && (t + 1) * tb <= data.Length; t++)
                tiles[slot * SlotTiles + t] = Gfx.DecodeTile(data, t * tb, Bpp);
        }
        return tiles;
    }

    /// <summary>
    /// The layer-3 tilemap as VRAM words (index 0 = word $5000), or null when this level mode
    /// and option have no tilemap — which includes every level whose option is 0.
    ///
    /// A word the script never writes comes back as <b>-1</b>, not 0. The scripts cover only
    /// the part of the screen the layer is meant to occupy, and what the console has in the
    /// rest of that VRAM is whatever the last level left there plus the status bar the game
    /// redraws every frame. Tile 0 is a real tile (a font glyph in GFX28), so filling the
    /// untouched region with it would draw a screen of noise that the game never shows.
    /// </summary>
    public static int[]? Tilemap(Rom rom, int levelMode, int option)
    {
        if (option is < 1 or > 3) return null;
        int index = levelMode * 3 + (option - 1);
        if (index is < 0 or >= PtrCount) return null;
        int ptr = rom.ReadValue(PtrTable + index * 3, 3);
        if (ptr <= 0xFFFF) return null;
        var map = new int[MapWords];
        Array.Fill(map, -1);
        RunStripe(rom, ptr, map);
        return map;
    }

    /// <summary>
    /// The tilemap to DRAW for a level: a tilemap the project imported for it, else vanilla's
    /// (level mode, option) pick. An import still needs the level to have a layer 3 at all —
    /// option 0 means the loader never runs, and showing a map the game would not is worse than
    /// showing none.
    /// </summary>
    public static int[]? LevelTilemap(Rom rom, int level, int levelMode, int option)
        => option is < 1 or > 3 ? null
         : rom.Layer3Tilemaps.TryGetValue(level, out var raw) ? FromBytes(raw)
         : Tilemap(rom, levelMode, option);

    /// <summary>
    /// A flat tilemap file as VRAM words. LM's LT3 files are plain little-endian 16-bit maps of
    /// 0x800, 0x1000 or 0x2000 bytes; they land at the start of the window and whatever the file
    /// does not cover stays untouched (-1), exactly as an unwritten stripe-image word does.
    /// (LM's per-file "Destination" — Under Status Bar / Start / Last Line / Bottom Half — is
    /// not decoded, so everything starts at word $5000 for now.)
    /// </summary>
    public static int[] FromBytes(ReadOnlySpan<byte> raw)
    {
        var map = new int[MapWords];
        Array.Fill(map, -1);
        for (int i = 0; i < MapWords && i * 2 + 1 < raw.Length; i++)
            map[i] = raw[i * 2] | (raw[i * 2 + 1] << 8);
        return map;
    }

    /// <summary>
    /// The inverse: a word buffer back to a full 0x2000-byte file. An unwritten word (-1) is
    /// stored as 0xFFFF, which names tile 0x3FF — past the 512 the window holds, so it draws as
    /// nothing here and would draw as nothing on the console either. A flat file has no way to
    /// say "untouched", and picking a real tile instead would paint the gaps with GFX28's font.
    /// </summary>
    public static byte[] ToBytes(int[] map)
    {
        var raw = new byte[MapWords * 2];
        for (int i = 0; i < MapWords; i++)
        {
            int w = i < map.Length && map[i] >= 0 ? map[i] : 0xFFFF;
            raw[i * 2] = (byte)w;
            raw[i * 2 + 1] = (byte)(w >> 8);
        }
        return raw;
    }

    /// <summary>(column, row) → the VRAM word index that holds it. A 64x64 BG is four 32x32
    /// screens, so this is the inverse of <see cref="At"/> and the one place the layout lives.</summary>
    public static int CellIndex(int col, int row)
        => (row / ScreenRows << 11) | (col / ScreenCols << 10) | (row % ScreenRows) << 5 | col % ScreenCols;

    /// <summary>
    /// One tilemap word drawn: its tile from the level's 512, in the palette group its bits
    /// name, flipped as they say. Null when it names no tile the window holds. Colour 0 comes
    /// back as 0 (fully transparent) rather than a palette colour, so a caller can lay it over
    /// a backdrop the way the console does.
    /// </summary>
    public static uint[]? CellPixels(int word, byte[]?[] tiles, Palette pal)
    {
        int chr = word & 0x3FF;
        if (word < 0 || chr >= TileCount || tiles[chr] is not { } t) return null;
        var px = new uint[64];
        int color = (word >> 10 & 7) * 4;
        bool fx = (word & 0x4000) != 0, fy = (word & 0x8000) != 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int idx = t[(fy ? 7 - y : y) * 8 + (fx ? 7 - x : x)];
                px[y * 8 + x] = idx == 0 ? 0 : pal.Rgba[color + idx];
            }
        return px;
    }

    /// <summary>Sizes a tilemap file may be: whole 16-bit maps, up to the 64x64 window.
    /// LM offers exactly these three in its bypass dialog.</summary>
    public static bool IsTilemapSize(int bytes) => bytes is 0x800 or 0x1000 or 0x2000;

    /// <summary>LM's four tilemap sizes, indexed by the record's size field (CONTRACT §12b).
    /// Index 3 is "Do not use" — a bypass that names a file and then declines to load it.</summary>
    public static readonly int[] TilemapSizes = [0x2000, 0x1000, 0x800, 0];

    /// <summary>LM's four tilemap destinations, indexed by the record's destination field. Where
    /// each one actually lands in the window is NOT decoded — only which is selected.</summary>
    public static readonly string[] TilemapDestinations =
        ["Under Status Bar", "Start of Layer 3", "Last Line of Status Bar", "Bottom Half of Layer 3"];

    /// <summary>
    /// The destination a BUILT tilemap is stamped with: "Start of Layer 3", because that is the
    /// one whose name matches where this editor draws an imported map — word $5000, the top of
    /// the window (<see cref="FromBytes"/>). The VRAM offset each destination actually implies is
    /// undecoded, so this is the honest guess and not a measurement; if a built layer 3 comes out
    /// shifted, this is the constant to question first.
    /// </summary>
    public const int BuiltTilemapDestination = 1;

    /// <summary>
    /// Vanilla's stripe-image uploader ($00871E), run into a word buffer instead of VRAM.
    /// Each entry is a 4-byte header: the VRAM word address BIG-endian, then a flags/length
    /// pair — bit 15 steps down a column (+32 words, one row of a screen) instead of across,
    /// bit 14 is RLE, and bits 13-0 are the length in BYTES minus one. An RLE entry carries one
    /// word and repeats it; every other entry carries its words inline. A first header byte
    /// with bit 7 set ends the script.
    /// </summary>
    private static void RunStripe(Rom rom, int snes, int[] map)
    {
        var d = rom.Data;
        int i = rom.FileOffset(snes);
        if (i < 0) return;
        // The scripts are short (the longest vanilla block is a few dozen entries); the cap is
        // only so a corrupt or mis-pointed block cannot spin.
        for (int entry = 0; entry < 0x200; entry++)
        {
            if (i < 0 || i + 4 > d.Length || (d[i] & 0x80) != 0) return;
            int addr = (d[i] << 8) | d[i + 1];
            int flags = d[i + 2];
            bool down = (flags & 0x80) != 0, rle = (flags & 0x40) != 0;
            int len = (((flags & 0x3F) << 8) | d[i + 3]) + 1;
            i += 4;
            if (i + (rle ? 2 : len) > d.Length) return;
            for (int b = 0; b < len; b++)
            {
                // Two bytes per word, low first, and the address steps once per word.
                int at = addr + (b >> 1) * (down ? ScreenCols : 1) - MapBase;
                if ((uint)at >= map.Length) continue;
                int was = Math.Max(map[at], 0);                // -1 = untouched, so start from blank
                byte v = d[i + (rle ? b & 1 : b)];
                map[at] = (b & 1) == 0 ? (was & 0xFF00) | v : (was & 0x00FF) | (v << 8);
            }
            i += rle ? 2 : len;
        }
    }

    /// <summary>Where a tilemap word sits on screen. A 64×64 BG is four 32×32 screens in VRAM,
    /// in the order left-top, right-top, left-bottom, right-bottom.</summary>
    public static (int X, int Y) At(int index)
        => ((index >> 10 & 1) * ScreenCols + (index & 31), (index >> 11 & 1) * ScreenRows + (index >> 5 & 31));

    /// <summary>
    /// The tilemap drawn with the level's tiles and palette, 512×512 pixels over
    /// <paramref name="backdrop"/> — the back-area colour for a standalone view, or 0 to leave
    /// the gaps transparent so it can be composed UNDER a level. Colour 0 of a BG3 palette is
    /// never drawn either way.
    /// </summary>
    public static (uint[] Px, int W, int H) Render(int[] map, byte[]?[] tiles, Palette pal, uint backdrop)
    {
        int w = Cols * 8, h = Rows * 8;
        var px = new uint[w * h];
        if (backdrop != 0) Array.Fill(px, backdrop);
        for (int i = 0; i < map.Length; i++)
        {
            int word = map[i], chr = word & 0x3FF;
            if (word < 0 || chr >= TileCount || tiles[chr] is not { } t) continue;
            var (tx, ty) = At(i);
            int color = (word >> 10 & 7) * 4;
            bool fx = (word & 0x4000) != 0, fy = (word & 0x8000) != 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int idx = t[(fy ? 7 - y : y) * 8 + (fx ? 7 - x : x)];
                    if (idx != 0) px[(ty * 8 + y) * w + tx * 8 + x] = pal.Rgba[color + idx];
                }
        }
        return (px, w, h);
    }
}
