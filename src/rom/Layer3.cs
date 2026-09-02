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
/// A tilemap word's 3-bit palette field names one of EIGHT groups, and 2bpp makes a group four
/// colours, so layer 3's whole palette space is CGRAM 00-1F — the two 16-colour rows SMW keeps
/// for it. Half of that is layer 3's own: <see cref="Palette"/> loads the $00B170 block into
/// CGRAM 08-0F and 18-1F, groups 2/3/6/7, from a FIXED address no header byte indexes, so those
/// four are identical on every level. The other four groups are the backdrop, white and the
/// level's own background palette — and vanilla's layer 3 uses them too, which is how the
/// mode-14 scrolling rocks tint with the level (see <see cref="IsLayer3Palette"/> for the
/// measured counts).
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
         // A BUILT rom carries its custom tilemap as an ordinary GFX file named by the level's
         // record. Without this, opening one shows the picture the build replaced.
         : rom.LmLayer3Tilemap(level) is { } lt3 && Gfx.Cached(rom, lt3.File) is { } file
           ? FromBytes(file)
         : Tilemap(rom, levelMode, option);

    /// <summary>
    /// A flat tilemap file as VRAM words. LM's LT3 files are plain little-endian 16-bit maps of
    /// 0x800, 0x1000 or 0x2000 bytes; they land at the start of the window and whatever the file
    /// does not cover stays untouched (-1), exactly as an unwritten stripe-image word does.
    /// Word $5000 is the top of the window; the destination this editor builds with skips the
    /// status bar's five rows (<see cref="BuiltTilemapDestination"/>), and the four offsets are
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
    /// The one fully transparent tile in vanilla's layer-3 set — the blank SMW's own status bar
    /// pads with. MEASURED: of the 512 tiles LG1-LG4 supply, exactly two have every pixel on
    /// colour 0, this one (in LG2) and 0x179 (in LG3); LG1 and LG4 have none.
    ///
    /// ponytail: a constant, not a per-level search of <see cref="Tiles"/>. The ceiling is a
    /// level that bypasses LG2 with a file whose tile 0xFC is not blank — then the gaps paint
    /// that tile instead of nothing. Upgrade path is to pick the filler from the level's real
    /// tiles at build time.
    /// </summary>
    public const int BlankTile = 0x0FC;

    /// <summary>
    /// The word <see cref="ToBytes"/> pads with: <see cref="BlankTile"/> in palette 0, priority
    /// CLEAR and neither flip. Every bit outside the tile number has to be zero here — see the
    /// warning in <see cref="ToBytes"/> for what a set priority bit does.
    /// </summary>
    public const int BlankWord = BlankTile;

    /// <summary>
    /// The inverse: a word buffer back to a full 0x2000-byte file. A flat file has no way to say
    /// "untouched", so an unwritten word (-1) is stored as <see cref="BlankWord"/>.
    ///
    /// It must NOT be 0xFFFF, which is what this used to write on the reasoning that tile 0x3FF
    /// is past the 512 the window holds and so draws as nothing. It draws as nothing in THIS
    /// editor, because <see cref="Render"/> and <see cref="CellPixels"/> skip a tile number
    /// >= <see cref="TileCount"/>. The console has no such rule and every bit of 0xFFFF hurts:
    /// BG3's character base is word $4000 and a 2bpp tile is 8 words, so tile 0x3FF reads its
    /// graphics from word $5FF8 — inside the tilemap region itself — and 0xFFFF also sets
    /// palette 7, both flips, and bit 13, the PRIORITY bit, which puts the result in FRONT of
    /// layer 1. A built map is stamped "Start of Layer 3" at full 0x2000, so every cell the user
    /// did not draw became a garbage tile covering the level.
    /// </summary>
    public static byte[] ToBytes(int[] map)
    {
        var raw = new byte[MapWords * 2];
        for (int i = 0; i < MapWords; i++)
        {
            int w = i < map.Length && map[i] >= 0 ? map[i] : BlankWord;
            raw[i * 2] = (byte)w;
            raw[i * 2 + 1] = (byte)(w >> 8);
        }
        return raw;
    }

    /// <summary>(column, row) → the VRAM word index that holds it. A 64x64 BG is four 32x32
    /// screens, so this is the inverse of <see cref="At"/> and the one place the layout lives.</summary>
    public static int CellIndex(int col, int row)
        => (row / ScreenRows << 11) | (col / ScreenCols << 10) | (row % ScreenRows) << 5 | col % ScreenCols;

    /// <summary>How many palette groups a tilemap word can name: bits 10-12, so eight.</summary>
    public const int PaletteGroups = 8;

    /// <summary>Colours in one of them. Layer 3 is 2bpp, so FOUR — not the sixteen every other
    /// palette strip in this editor shows. Colour 0 is transparent, as everywhere else.</summary>
    public const int PaletteColors = 1 << Bpp;

    /// <summary>Where group <paramref name="group"/>'s colours start in CGRAM. A 2bpp BG reads
    /// four entries per group, so the groups tile CGRAM 00-1F four at a time.</summary>
    public static int PaletteBase(int group) => (group & (PaletteGroups - 1)) * PaletteColors;

    /// <summary>
    /// The last CGRAM index a layer-3 tile can name. All eight groups are addressable and 2bpp
    /// is four colours each, so layer 3's whole palette space is CGRAM 00-1F — the two 16-colour
    /// rows SMW keeps for it, and a thirty-second of the 256 the palette page shows.
    /// </summary>
    public const int PaletteSpace = PaletteGroups * PaletteColors;

    /// <summary>
    /// Whether this group's colours come from SMW's DEDICATED layer-3 block, `$00B170` — which
    /// `Palette.Load` puts at CGRAM 08-0F and 18-1F, i.e. groups 2, 3, 6 and 7. It is loaded
    /// from a fixed address with no header byte indexing it, so those four are the same on every
    /// level (until an LM custom palette overrides them, which is what a palette edit here
    /// writes).
    ///
    /// The other four are NOT invalid, and calling them that would be wrong: groups 0/1/4/5 are
    /// the backdrop, white, and the level's own BACKGROUND palette, so a tile drawn with one of
    /// them tints with the level. Vanilla does exactly that — measured over every layer-3 tilemap
    /// in the ROM (`--layer3` with no level prints the counts), the tides spend 1982 cells in
    /// group 6, the ghost house 190 in 6 and 136 in 7, and the mode-14 scrolling rocks 194 in
    /// group 4 and 94 in group 5. Group 2 is the only one nothing uses.
    /// </summary>
    public static bool IsLayer3Palette(int group) => group is 2 or 3 or 6 or 7;

    /// <summary>The palette group a tilemap word names.</summary>
    public static int PaletteOf(int word) => word >> 10 & (PaletteGroups - 1);

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
    /// The destination a BUILT tilemap is stamped with: "Under Status Bar", word $50A0 — five
    /// rows down, past the 32x5 the status bar occupies.
    ///
    /// It used to be "Start of Layer 3" (word $5000) on the reasoning that the editor draws an
    /// imported map from the top, so the two pictures agree. They do — and the game's HUD is
    /// part of the picture that got agreed over. A full 0x2000 map at $5000 copies its own rows
    /// 0-4 across the score, coins, time and lives, and since a custom layer 3 usually sets the
    /// tilemap's PRIORITY bit (that is how mist draws in front of the level), the result covers
    /// the status bar rather than blending with it. MEASURED on a real project: a mist tilemap
    /// turned the whole HUD into mist tiles.
    ///
    /// The offset applies to the SOURCE as well as the destination (CONTRACT §12b), so nothing
    /// shifts: the file's row 5 still lands on row 5. The only difference is that the file's
    /// first 0x140 bytes are not uploaded, which is exactly the trade LM offers this destination
    /// for. The editor still DRAWS those rows — they are the user's data and the status bar is
    /// not ours to render — so the top five rows of the layer-3 view are the one place the
    /// editor's picture and the console's deliberately differ.
    /// </summary>
    public const int BuiltTilemapDestination = 0;

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

    /// <summary>
    /// Vanilla's screen setup, picked by LEVEL MODE rather than by level: $05:8505-8517 reads
    /// three 0x20-byte tables into $0D9D (main screen, $212C), $0D9E (subscreen, $212D) and
    /// $40 (CGADSUB, $2131). LM's <see cref="Advanced"/> record is the per-level override.
    ///
    /// Mode 0 is the one worth knowing by heart: main $15 = BG1 + BG3 + sprites, sub $02 = BG2,
    /// CGADSUB $24 = backdrop and BG3 add the subscreen. So in an ordinary level the background
    /// image is NOT on the main screen at all — layer 3 sits above it and the image adds into
    /// it, which is the opposite of what mode 1's priority order alone would suggest.
    /// </summary>
    public const int MainScreenTable = 0x058437, SubScreenTable = 0x058457, CgAdSubTable = 0x058477;

    /// <summary>BG3's bit in every one of the three screen registers.</summary>
    private const int Bg3Bit = 0x04, Bg2Bit = 0x02, HalfBit = 0x40;

    /// <summary>Where layer 3 lands relative to the background image, and whether it adds to
    /// what is under it rather than covering it.</summary>
    public readonly record struct Screens(bool AboveBg2, bool Blend, bool Half);

    /// <summary>
    /// Read that setup for one level. LM's CGADSUB box adds BG3 to the colour-math targets; its
    /// Subscreen box takes BG3 off the main screen, which in practice is how a hack asks for a
    /// layer 3 that floats over everything — modelled here as "above the image, blended" rather
    /// than by following the subscreen through the maths.
    /// </summary>
    public static Screens ScreenSetup(Rom rom, int mode, Advanced? adv)
    {
        int main = rom.ReadByte(MainScreenTable + (mode & 0x1F));
        int sub = rom.ReadByte(SubScreenTable + (mode & 0x1F));
        int math = rom.ReadByte(CgAdSubTable + (mode & 0x1F));
        bool onSub = adv?.Subscreen == true;
        // Nothing to add when the subscreen is empty, which is what the modes with sub $00 say.
        bool blend = onSub || ((math & Bg3Bit) != 0 || adv?.CgAdSub == true) && sub != 0;
        return new Screens(AboveBg2: onSub || (main & Bg2Bit) == 0, blend, (math & HalfBit) != 0);
    }

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

    /// <summary>
    /// The twelve auto-scroll entries — the ones that move layer 3 on their own rather than as a
    /// fraction of layer 1. Codes 0x06-0x11: a speed in 8.8 fixed point accumulated per frame,
    /// from LM's table (CONTRACT §12b). The tide variant of them (a level whose `$1403` is
    /// non-zero) is the one part still unported.
    /// </summary>
    public static bool IsAutoScroll(int dropdownIndex) => dropdownIndex is >= 9 and <= 20;

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
    ///
    /// <paramref name="priority"/> keeps only the cells whose priority bit matches, which is what
    /// splits the layer in two on the console: with mode 1's BG3-priority bit set (the level
    /// header's Layer 3 Priority), a priority cell draws in FRONT of every other layer while its
    /// neighbour without the bit stays at the very back. Null draws both.
    /// </summary>
    public static (uint[] Px, int W, int H) Render(int[] map, byte[]?[] tiles, Palette pal, uint backdrop,
                                                  int? priority = null)
    {
        int w = Cols * 8, h = Rows * 8;
        var px = new uint[w * h];
        if (backdrop != 0) Array.Fill(px, backdrop);
        for (int i = 0; i < map.Length; i++)
        {
            int word = map[i], chr = word & 0x3FF;
            if (word < 0 || chr >= TileCount || tiles[chr] is not { } t) continue;
            if (priority is { } want && (word >> 13 & 1) != want) continue;
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
