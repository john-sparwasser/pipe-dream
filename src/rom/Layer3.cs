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
    /// (level mode, option) pick, else none.
    ///
    /// An import still needs the level to have a layer 3 at all. The bypass is copied in at the
    /// tail of vanilla's own tilemap picker (prep v15, CONTRACT §12b), and option 0 makes the
    /// game skip that routine entirely — so a bypassed map on an option-0 level never loads,
    /// and showing one here would be a picture the console does not draw.
    /// </summary>
    public static int[]? LevelTilemap(Rom rom, int level, int levelMode, int option)
        => option is < 1 or > 3 ? null
         : rom.Layer3Tilemaps.TryGetValue(level, out var raw) ? FromBytes(raw)
         : Tilemap(rom, levelMode, option);

    /// <summary>
    /// A flat tilemap file as VRAM words. LM's LT3 files are plain little-endian 16-bit maps of
    /// 0x800, 0x1000 or 0x2000 bytes; they land at the start of the window and whatever the file
    /// does not cover stays untouched (-1), exactly as an unwritten stripe-image word does.
    /// Word $5000 is the top of the window and the destination this editor builds with
    /// (<see cref="BuiltTilemapDestination"/>); the other three land further in, by the offsets
    /// in <see cref="TilemapDestinationWords"/>.
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

    /// <summary>LM's four tilemap destinations, indexed by the record's destination field.</summary>
    public static readonly string[] TilemapDestinations =
        ["Under Status Bar", "Start of Layer 3", "Last Line of Status Bar", "Bottom Half of Layer 3"];

    /// <summary>
    /// The VRAM word each destination copies to, read straight out of LM's own table at
    /// $0FFEBC (CONTRACT §12b). Relative to the window base $5000 they are +$A0, 0, +$80 and
    /// +$800: five rows down (past the 32x5 status bar), the very top, four rows down (over the
    /// status bar's last line), and the bottom half. LM's help names the same address as the
    /// byte to patch when a hack shortens its status bar, which is the independent confirmation.
    /// </summary>
    public static readonly int[] TilemapDestinationWords = [0x50A0, 0x5000, 0x5080, 0x5800];

    /// <summary>
    /// The destination a BUILT tilemap is stamped with: "Start of Layer 3", the one that lands
    /// at word $5000 — the top of the window, which is where <see cref="FromBytes"/> draws an
    /// imported map. Confirmed against LM's destination table, so the editor's picture and the
    /// console's agree.
    /// </summary>
    public const int BuiltTilemapDestination = 1;

    // ---- LM's advanced layer-3 bypass (CONTRACT §12b) --------------------------------------

    /// <summary>
    /// LM's "Enable advanced bypass settings for Layer 3" group: how the level's layer 3
    /// SCROLLS and BLENDS, as opposed to which tilemap it shows.
    ///
    /// This is the part that matters for a custom layer 3. The tilemap bypass replaces the
    /// picture, but LM's own help is explicit that "the behavior and scrolling of the original
    /// setting will remain unless you enable the advanced bypass settings" — so a custom map on
    /// a level whose Layer 3 Option is "Tileset Specific" still scrolls like the beta cage
    /// until this is on.
    ///
    /// <see cref="VScroll"/> and <see cref="HScroll"/> are indices into
    /// <see cref="VScrollNames"/> / <see cref="HScrollNames"/>, not the codes the ROM stores —
    /// LM's dropdown order and its code space are different orders, and
    /// <see cref="ScrollCodes"/> is the map between them. <see cref="XPos"/> likewise indexes
    /// <see cref="XPositions"/>. <see cref="Y"/> is the raw signed value, in 16x16 tiles.
    /// </summary>
    public readonly record struct Advanced(
        bool CgAdSub, bool Subscreen, bool FixScrollSync,
        int VScroll, int HScroll, int XPos, int Y);

    /// <summary>LM's vertical scroll dropdown, in its own order. "Constant" scrolls in place
    /// with layer 1, the Mediums and Slows are fractions of it, "Fast" is 1.2x, and the
    /// auto-scrolls move on their own at the speeds in LM's table at $109D3B.</summary>
    public static readonly string[] VScrollNames =
    [
        "None", "Constant", "Medium", "Medium 2", "Medium 3", "Medium 4", "Slow", "Slow 2", "Fast",
        "Auto-Scroll Up Slow", "Auto-Scroll Up Medium", "Auto-Scroll Up Fast",
        "Auto-Scroll Up Fast 2", "Auto-Scroll Up Fast 3", "Auto-Scroll Up Fast 4",
        "Auto-Scroll Down Slow", "Auto-Scroll Down Medium", "Auto-Scroll Down Fast",
        "Auto-Scroll Down Fast 2", "Auto-Scroll Down Fast 3", "Auto-Scroll Down Fast 4",
    ];

    /// <summary>The horizontal list — the same 21 entries, with the auto-scrolls named for the
    /// direction they move.</summary>
    public static readonly string[] HScrollNames =
    [
        "None", "Constant", "Medium", "Medium 2", "Medium 3", "Medium 4", "Slow", "Slow 2", "Fast",
        "Auto-Scroll Left Slow", "Auto-Scroll Left Medium", "Auto-Scroll Left Fast",
        "Auto-Scroll Left Fast 2", "Auto-Scroll Left Fast 3", "Auto-Scroll Left Fast 4",
        "Auto-Scroll Right Slow", "Auto-Scroll Right Medium", "Auto-Scroll Right Fast",
        "Auto-Scroll Right Fast 2", "Auto-Scroll Right Fast 3", "Auto-Scroll Right Fast 4",
    ];

    /// <summary>
    /// Dropdown index → the 5-bit code the record stores, measured one save per entry. The two
    /// orders are NOT the same: LM groups the list by feel (Medium 3 sits between Medium 2 and
    /// Medium 4) while the codes group by implementation — 0-5 are the six rate handlers,
    /// 6-0x11 the twelve auto-scroll speeds sharing one handler, and 0x18-0x1A three more
    /// rate handlers LM added later and appended to the code space rather than the list.
    /// </summary>
    public static readonly int[] ScrollCodes =
    [
        0x00, 0x01, 0x02, 0x03, 0x18, 0x19, 0x04, 0x1A, 0x05,
        0x06, 0x07, 0x08, 0x09, 0x10, 0x11,
        0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
    ];

    /// <summary>The four initial X positions LM offers, in 16x16 tiles. Not evenly spaced: the
    /// game multiplies the index by 0x40 pixels except for index 3, which it special-cases to
    /// $100 — which is why the list reads 00/04/08/10 and not 00/04/08/0C.</summary>
    public static readonly int[] XPositions = [0x00, 0x04, 0x08, 0x10];

    /// <summary>The range LM's Initial Y Position/Offset accepts, in 16x16 tiles. It reaches
    /// the ROM multiplied by 8 in a 14-bit signed field, which is exactly this range.</summary>
    public const int MinY = -0x400, MaxY = 0x3FF;

    private static int Nib(ushort[] r, int i) => r[i] >> 12 & 0xF;

    private static void SetNib(ushort[] r, int i, int v) =>
        r[i] = (ushort)(r[i] & 0x0FFF | (v & 0xF) << 12);

    /// <summary>
    /// The advanced settings out of a per-level bypass record, or null when the level does not
    /// use them.
    ///
    /// They live in the HIGH NIBBLE of nine of the record's sixteen words, which is why they
    /// cost no space at all: every word's low 12 bits are a GFX file id, and the top four were
    /// spare. LM's reader at $0FFD9F glues them back into four variables and the rest of the
    /// game reads those, so the grouping below is its grouping, not ours:
    ///
    ///   $7FC01A = w11         X position (bits 0-1), CGADSUB (2), layer 3 to subscreen (3)
    ///   $7FC01B = w3,w2       not used by anything we have seen — most likely "Make tides act
    ///                         as", which is greyed out on every level that has no tides
    ///   $7FC01C = w10,w9      the Y offset's high bits, plus the two scrolls' bit 4
    ///   $145E   = w15..w12    enable (bit 0), scroll-sync fix (1), the Y offset's low bits
    ///                         (3-7), then the horizontal and vertical scroll codes
    /// </summary>
    public static Advanced? ReadAdvanced(ushort[] r)
    {
        int lo = Nib(r, 12);
        if ((lo & 1) == 0) return null;
        int c = Nib(r, 10) << 4 | Nib(r, 9);
        int y8 = (c << 8 | Nib(r, 13) << 4 & 0xF0 | lo & 0x8) & 0x3FFF;
        return new Advanced(
            CgAdSub:       (Nib(r, 11) & 4) != 0,
            Subscreen:     (Nib(r, 11) & 8) != 0,
            FixScrollSync: (lo & 2) != 0,
            VScroll:       ScrollIndex(Nib(r, 15) | ((c & 0x40) != 0 ? 0x10 : 0)),
            HScroll:       ScrollIndex(Nib(r, 14) | ((c & 0x80) != 0 ? 0x10 : 0)),
            XPos:          Nib(r, 11) & 3,
            Y:             (y8 << 18 >> 18) / 8);        // 14-bit signed, then undo the *8
    }

    /// <summary>Write the advanced settings back into a record's spare nibbles, or clear them
    /// (null). Clearing zeroes every nibble this owns, so a level that stops using the advanced
    /// group leaves no half-read scroll setting behind.</summary>
    public static void WriteAdvanced(ushort[] r, Advanced? adv)
    {
        foreach (int w in (int[])[2, 3, 9, 10, 11, 12, 13, 14, 15]) SetNib(r, w, 0);
        if (adv is not { } a) return;
        int v = ScrollCodes[Math.Clamp(a.VScroll, 0, ScrollCodes.Length - 1)];
        int h = ScrollCodes[Math.Clamp(a.HScroll, 0, ScrollCodes.Length - 1)];
        int y8 = Math.Clamp(a.Y, MinY, MaxY) * 8 & 0x3FFF;
        SetNib(r, 11, a.XPos & 3 | (a.CgAdSub ? 4 : 0) | (a.Subscreen ? 8 : 0));
        SetNib(r, 12, 1 | (a.FixScrollSync ? 2 : 0) | (y8 & 8));
        SetNib(r, 13, y8 >> 4 & 0xF);
        SetNib(r, 9, y8 >> 8 & 0xF);
        SetNib(r, 10, y8 >> 12 & 3 | (v & 0x10) >> 2 | (h & 0x10) >> 1);
        SetNib(r, 14, h & 0xF);
        SetNib(r, 15, v & 0xF);
    }

    /// <summary>A stored scroll code back to its place in LM's dropdown. An unknown code falls
    /// back to "None" rather than throwing — the code space has gaps (0x12-0x17) that no list
    /// entry names, and a record can hold one.</summary>
    public static int ScrollIndex(int code)
    {
        int i = Array.IndexOf(ScrollCodes, code);
        return i < 0 ? 0 : i;
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
