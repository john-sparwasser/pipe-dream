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
    /// The 512 layer-3 8×8 tiles as palette indices (0-3), in VRAM order — slot k holds tiles
    /// k*128..k*128+127, which is how $00A993's straight-through upload lays them out.
    /// </summary>
    // ponytail: always the vanilla four. LM's per-level Layer 3 GFX bypass would repoint them,
    // but its table cannot be located per-ROM yet (CONTRACT §12b: the reader is not a plain
    // `LDA.l base,X`, so the ScanOperand trick that finds LmGfxBypassBase does not reach it).
    public static byte[]?[] Tiles(Rom rom)
    {
        var tiles = new byte[]?[TileCount];
        for (int slot = 0; slot < VanillaGfx.Length; slot++)
        {
            int file = VanillaGfx[slot];
            if (Gfx.Cached(rom, file) is not { } data) continue;
            int bpp = Gfx.FileBpp(rom, file), tb = Gfx.TileBytes(bpp);
            for (int t = 0; t < SlotTiles && (t + 1) * tb <= data.Length; t++)
                tiles[slot * SlotTiles + t] = Gfx.DecodeTile(data, t * tb, bpp);
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
    /// The tilemap drawn with the level's tiles and palette: 512×512 pixels over the back-area
    /// colour, which is what shows through wherever layer 3 is transparent (colour 0 of a BG3
    /// palette is never drawn).
    /// </summary>
    public static (uint[] Px, int W, int H) Render(int[] map, byte[]?[] tiles, Palette pal)
    {
        int w = Cols * 8, h = Rows * 8;
        var px = new uint[w * h];
        Array.Fill(px, pal.Rgba[0]);
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
