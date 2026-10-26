namespace PipeDream;

/// <summary>
/// The layer-2 background image codec (CONTRACT §10). A background is an RLE stream living
/// in bank $0C, decoded into a buffer of BG Map16 def indices that the loader pre-fills with
/// the blank tile and then tiles horizontally across the level.
///
/// Two things constrain writing one back:
///
/// * The page byte is NOT in the data — the loader derives it from the pointer, page 1 when
///   the low 16 bits are >= $E8FE. So MOVING a background silently recolours every tile in
///   it. Rewriting in place is the only address-safe option, which is why
///   <see cref="Encode"/> is judged by whether it fits the original stream.
/// * The stream must live in bank $0C, because the loader hardcodes that bank; a RATS block
///   anywhere else is unreachable.
///
/// Both bullets are about VANILLA backgrounds. LM's CUSTOM backgrounds (CONTRACT §10b) share the
/// RLE but not the framing: they live at a full 24-bit address outside bank $0C, the stream is
/// 0x800 bytes (two positional planes — 0x400 low bytes then 0x400 page bytes, one run with no
/// terminator between them), and the geometry is 32 rows at stride <see cref="CustomStride"/>
/// rather than 27 at <see cref="VanillaStride"/>. <see cref="DecodeCustom"/> reads one and
/// <see cref="ToCustomPlanes"/>/<see cref="EncodeCustom"/> write one, which is what lifts both
/// constraints above: a custom background can be any size, lives anywhere, and is per level.
/// </summary>
public static class BgImage
{
    /// <summary>Buffer the loader fills: two 16x27 screens is 0x360 tiles, but it decodes up
    /// to 0x400 and the extra is harmless.</summary>
    public const int Tiles = 0x400;

    /// <summary>The tile the loader pre-fills with, so trailing runs of it need no bytes.</summary>
    public const byte Blank = 0x25;

    /// <summary>Bank $0C holds every vanilla background, and the loader reads only there.</summary>
    public const int Bank = 0x0C0000;

    /// <summary>Page byte for a stream at <paramref name="lo16"/> ($058046: `CPX #$E8FE : BCC`
    /// — inclusive, which is why level $10A sitting exactly on $E8FE is page 1).</summary>
    public static int PageFor(int lo16) => lo16 >= 0xE8FE ? 1 : 0;

    /// <summary>Every background a ROM actually uses, as (address, page, the levels pointing
    /// at it), in address order. Vanilla has 17 sharing them across ~150 levels. This is the
    /// set a level can be pointed at safely — each is a known-good stream at an address whose
    /// page it was authored for.</summary>
    public static List<(int Lo16, int Page, List<int> Levels)> Catalog(Rom rom)
    {
        var by = new Dictionary<int, List<int>>();
        for (int lvl = 0; lvl < Rom.LevelCount; lvl++)
        {
            if (!rom.Layer2IsBackground(lvl)) continue;
            int lo16 = rom.Layer2Pointer(lvl) & 0xFFFF;
            if (!by.TryGetValue(lo16, out var list)) by[lo16] = list = new List<int>();
            list.Add(lvl);
        }
        return by.OrderBy(kv => kv.Key)
                 .Select(kv => (kv.Key, PageFor(kv.Key), kv.Value))
                 .ToList();
    }

    /// <summary>Decode the stream at <paramref name="lo16"/> into low tile bytes.
    /// <paramref name="consumed"/> is the stream's byte length INCLUDING the FF FF
    /// terminator, which is what an in-place rewrite has to fit inside.</summary>
    public static byte[] Decode(Rom rom, int lo16, out int consumed)
    {
        var low = new byte[Tiles];
        Array.Fill(low, Blank);
        int start = rom.FileOffset(Bank | lo16);
        int p = start, o = 0;
        while (o < Tiles && p + 1 < rom.Data.Length)
        {
            int cmd = rom.Data[p++];
            if (cmd == 0xFF && rom.Data[p] == 0xFF) { p++; break; }
            int count = (cmd & 0x7F) + 1;
            if ((cmd & 0x80) != 0)
            {
                byte b = rom.Data[p++];
                for (int i = 0; i < count && o < Tiles; i++) low[o++] = b;
            }
            else
                for (int i = 0; i < count && o < Tiles; i++) low[o++] = rom.Data[p++];
        }
        consumed = p - start;
        return low;
    }

    /// <summary>LM's custom-background geometry: two screens of 16 wide x 32 tall at stride
    /// <see cref="CustomStride"/>, where vanilla's screens are 27 tall at stride 0x1B0
    /// (CONTRACT §10b). Same 0x400 entries either way, laid out differently — LM's five extra
    /// rows are the 432px -> 512px expansion its help describes.</summary>
    public const int CustomStride = 0x200, CustomRows = 32, VanillaStride = 0x1B0, VanillaRows = 27;

    /// <summary>Where LM's five extra rows (27-31) live in the editor's map: the 0xA0 cells past the two vanilla screens, which
    /// is exactly 2 screens x 5 rows x 16. Rows 0-26 stay where vanilla has them, so every reader of those is unchanged, and
    /// a map decoded from a vanilla stream has the loader's <see cref="Blank"/> there.</summary>
    public const int ExtraBase = 2 * VanillaStride;

    /// <summary>A cell's index in the editor's map: column 0-31 (two screens of 16), row 0-31.</summary>
    public static int Cell(int col, int row)
        => row < VanillaRows ? (col / 16) * VanillaStride + row * 16 + (col & 0x0F)
                             : ExtraBase + (col / 16) * ((CustomRows - VanillaRows) * 16) + (row - VanillaRows) * 16 + (col & 0x0F);

    /// <summary>
    /// Give LM's five extra rows a picture when nothing has painted them (every cell still the loader's <see cref="Blank"/>):
    /// the game shows rows 27-28 below the background when it scrolls down and 29-31 above it when it scrolls up (the layer
    /// wraps at 32), so 27-28 repeat row 26 and 29-31 repeat row 0. Left as Blank they were the bottom's fill tile, a striped
    /// band above the sky. Painted rows are kept. Both planes, in place.
    /// </summary>
    public static void ExtendRows(byte[] low, byte[] page)
    {
        for (int c = 0; c < 32; c++)
            for (int r = VanillaRows; r < CustomRows; r++)
                if (low[Cell(c, r)] != Blank) return;
        for (int c = 0; c < 32; c++)
            for (int r = VanillaRows; r < CustomRows; r++)
            {
                int from = Cell(c, r < 29 ? VanillaRows - 1 : 0), to = Cell(c, r);
                low[to] = low[from];
                page[to] = page[from];
            }
    }

    /// <summary><see cref="ExtendRows(byte[], byte[])"/> on whole tile numbers (page in bit 8).</summary>
    public static ushort[] ExtendRows(ushort[] tiles)
    {
        var (low, page) = Split(tiles);
        ExtendRows(low, page);
        return Join(low, page);
    }

    /// <summary>An EDITED background as the build will write it: with <see cref="ExtendRows(ushort[])"/> on a base that takes
    /// LM's custom kind (RomBuilder.WriteBackground does the same), unchanged on one that rewrites in place. An unedited
    /// background is never rewritten, so it keeps its blank rows and is shown with them.</summary>
    public static ushort[] AsBuilt(Rom rom, ushort[] tiles) => rom.HasLmLayer2Custom ? ExtendRows(tiles) : tiles;

    /// <summary>How many of a level's background rows hold picture: 32 when it is (or the build will make it) LM's custom
    /// kind, vanilla's 27 otherwise. Either way the layer wraps at 32 rows, its 512-pixel tilemap, so a vanilla background
    /// in a vertical level shows an empty band between repeats — as Lunar Magic draws it (0C2, 109, 0EA, 1ED). Repeating
    /// every 27 shifted everything past the first picture (<see cref="Map16.DrawBgImage"/>).</summary>
    public static int Period(Rom rom, int level)
        => rom.HasLmLayer2Custom && (rom.Layer2IsCustomBackground(level) || rom.BgTilemaps.ContainsKey(level)) ? CustomRows : VanillaRows;

    /// <summary>
    /// A vanilla-stride map (what the editor holds) re-laid-out for LM's custom format and paired
    /// with a page plane, as the 0x800 bytes a custom stream decodes to: 0x400 low bytes then
    /// 0x400 page bytes, one continuous RLE run with a single terminator (MEASURED on three LM
    /// saves — CONTRACT §10b).
    ///
    /// The page plane is UNIFORM here. Vanilla has no per-tile page — the loader derives one page
    /// for the whole background from the stream's address — so an edit of a vanilla background
    /// carries that same page across, which is what keeps its colours identical. Per-tile pages
    /// are what the format allows, not what this edit has to say.
    /// </summary>
    public static byte[] ToCustomPlanes(ReadOnlySpan<byte> vanillaLow, int page)
    {
        var uniform = new byte[Tiles];
        Array.Fill(uniform, (byte)page);
        return ToCustomPlanes(vanillaLow, uniform);
    }

    /// <summary>The same relayout with a page PER TILE — what a background edited across pages
    /// carries. A cell's page rides beside its low byte through the 0x1B0 → 0x200 move, so a
    /// page-1 tile painted onto a page-0 background builds as the tile that was painted, which
    /// the uniform plane could not say.</summary>
    public static byte[] ToCustomPlanes(ReadOnlySpan<byte> vanillaLow, ReadOnlySpan<byte> vanillaPage)
    {
        var flat = new byte[Tiles * 2];
        Array.Fill(flat, Blank, 0, Tiles);                  // a short map's missing cells: blank...
        Array.Fill(flat, vanillaPage.Length > 0 ? vanillaPage[0] : (byte)0, Tiles, Tiles);   // ...on the plane's page
        // All 32 rows: 27-31 come from the map's extra cells (Cell), which the Background tab paints and a vanilla decode left
        // Blank. They used to be filled here, which put the bottom's fill tile above the sky when the game scrolled up.
        for (int screen = 0; screen < 2; screen++)
            for (int row = 0; row < CustomRows; row++)
                for (int col = 0; col < 16; col++)
                {
                    int from = Cell(screen * 16 + col, row);
                    int to = screen * CustomStride + row * 16 + col;
                    if (from < vanillaLow.Length) flat[to] = vanillaLow[from];
                    if (from < vanillaPage.Length) flat[Tiles + to] = vanillaPage[from];
                }
        return flat;
    }

    // ---- the editor's view: one 9-bit tile number per cell, page in bit 8 ----
    // The ROM stores a background as two planes (or one plus an address-derived page); the
    // editor thinks in whole BG tile numbers 0x000-0x1FF, the way the drawer shows them. These
    // are the two conversions, so no caller has to know which side of the boundary it is on.

    /// <summary>Low bytes and a page plane as whole tile numbers.</summary>
    public static ushort[] Join(ReadOnlySpan<byte> low, ReadOnlySpan<byte> page)
    {
        var tiles = new ushort[low.Length];
        for (int i = 0; i < tiles.Length; i++)
            tiles[i] = (ushort)((i < page.Length ? page[i] << 8 : 0) | low[i]);
        return tiles;
    }

    /// <summary>Low bytes with one page for every cell — a vanilla background, whose page comes
    /// from its address (§10a).</summary>
    public static ushort[] WithPage(ReadOnlySpan<byte> low, int page)
    {
        var tiles = new ushort[low.Length];
        for (int i = 0; i < tiles.Length; i++) tiles[i] = (ushort)(page << 8 | low[i]);
        return tiles;
    }

    /// <summary>Whole tile numbers back to the two planes the ROM and the project store.</summary>
    public static (byte[] Low, byte[] Page) Split(ReadOnlySpan<ushort> tiles)
    {
        var low = new byte[tiles.Length];
        var page = new byte[tiles.Length];
        for (int i = 0; i < tiles.Length; i++) { low[i] = (byte)tiles[i]; page[i] = (byte)(tiles[i] >> 8); }
        return (low, page);
    }

    /// <summary>The page plane a level's background has BEFORE any edit: per tile from a custom
    /// stream, or the address-derived page across a vanilla one. What an older project file,
    /// which stored only low bytes, is hydrated against.</summary>
    public static byte[] PagePlane(Rom rom, int level)
    {
        if (rom.Layer2IsCustomBackground(level)
            && DecodeCustom(rom, rom.Layer2Pointer(level)) is var (_, page)) return page;
        var uniform = new byte[Tiles];
        Array.Fill(uniform, (byte)PageFor(rom.Layer2Pointer(level) & 0xFFFF));
        return uniform;
    }

    /// <summary>
    /// Read a custom background at a full 24-bit address back to the editor's map (vanilla-stride rows 0-26, LM's five extra
    /// rows in the cells past them) and the page plane, i.e. the inverse of <see cref="ToCustomPlanes"/> plus the RLE.
    ///
    /// Returns null when the stream does not decode to the full two planes — a half-length stream
    /// would otherwise come back as a background whose bottom half is silently blank.
    /// </summary>
    public static (byte[] Low, byte[] Page)? DecodeCustom(Rom rom, int snes)
    {
        int start = rom.FileOffset(snes);
        if (start < 0) return null;
        var flat = new byte[Tiles * 2];
        int p = start, o = 0;
        while (o < flat.Length && p + 1 < rom.Data.Length)
        {
            int cmd = rom.Data[p++];
            if (cmd == 0xFF && rom.Data[p] == 0xFF) break;
            int count = (cmd & 0x7F) + 1;
            if ((cmd & 0x80) != 0)
            {
                byte b = rom.Data[p++];
                for (int i = 0; i < count && o < flat.Length; i++) flat[o++] = b;
            }
            else
                for (int i = 0; i < count && o < flat.Length; i++) flat[o++] = rom.Data[p++];
        }
        if (o < flat.Length) return null;

        var low = new byte[Tiles];
        var page = new byte[Tiles];
        for (int screen = 0; screen < 2; screen++)
            for (int row = 0; row < CustomRows; row++)
                for (int col = 0; col < 16; col++)
                {
                    int from = screen * CustomStride + row * 16 + col;
                    int to = Cell(screen * 16 + col, row);
                    low[to] = flat[from];
                    page[to] = flat[Tiles + from];
                }
        return (low, page);
    }

    /// <summary>Encode a custom background: the two planes as ONE run, with no trailing trim.
    /// <see cref="Encode"/>'s trim is correct for vanilla (the loader pre-fills the buffer with
    /// <see cref="Blank"/> and a custom stream's page plane has no such pre-fill to lean on), so
    /// the planes go out whole.</summary>
    public static byte[] EncodeCustom(ReadOnlySpan<byte> planes) => Encode(planes, trimBlank: false);

    /// <summary>Encode low tile bytes back to an RLE stream, terminator included.
    ///
    /// Greedy: a run costs 2 bytes and a literal costs 1 per byte plus a block header, so a
    /// run only pays from length 3 up — switching out of an open literal block to encode a
    /// pair would cost the same and then charge a fresh header for whatever follows.
    /// Trailing <see cref="Blank"/> is dropped because the loader already pre-filled it.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> low) => Encode(low, trimBlank: true);

    /// <inheritdoc cref="Encode(ReadOnlySpan{byte})"/>
    public static byte[] Encode(ReadOnlySpan<byte> low, bool trimBlank)
    {
        int end = low.Length;
        if (trimBlank) while (end > 0 && low[end - 1] == Blank) end--;

        var outb = new List<byte>(256);
        var lit = new List<byte>(128);
        void FlushLit()
        {
            for (int i = 0; i < lit.Count; i += 128)
            {
                int n = Math.Min(128, lit.Count - i);
                outb.Add((byte)(n - 1));                       // copy: bit7 clear, count-1
                for (int j = 0; j < n; j++) outb.Add(lit[i + j]);
            }
            lit.Clear();
        }

        for (int p = 0; p < end;)
        {
            int run = 1;
            while (p + run < end && low[p + run] == low[p] && run < 128) run++;
            // A 128-long run encodes its command as $FF; with $FF as the value that spells
            // the FF FF terminator, so shorten it and let the remainder encode separately.
            if (run == 128 && low[p] == 0xFF) run = 127;
            if (run >= 3)
            {
                FlushLit();
                outb.Add((byte)(0x80 | (run - 1)));
                outb.Add(low[p]);
                p += run;
            }
            else lit.Add(low[p++]);
        }
        FlushLit();
        outb.Add(0xFF);
        outb.Add(0xFF);
        return outb.ToArray();
    }
}
