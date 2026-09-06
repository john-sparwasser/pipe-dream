namespace PipeDream;

/// <summary>
/// The overworld as SMW stores it, read from the ROM and drawn the way Lunar Magic draws it:
/// one canvas, the main map on top and the map that holds all six submaps beneath it.
///
/// Two maps, each 32x32 16x16-tiles. The main map is index 0x000-0x3FF; the six submaps share
/// ONE second map (index 0x400-0x7FF), laid out two columns by three rows, and a "submap" is
/// only a fixed camera plus a palette — the tiles are all in the same table. Layer 2 is the
/// land, 8x8 tiles from two RLE streams; layer 1 is the level tiles, clouds and Mario's
/// invisible path tiles, one byte per 16x16 cell, drawn through the overworld's own Map16
/// table. Every address is a constant here so a Lunar Magic hijack that re-points a table is
/// a one-line change (bank 04 traced in reference/smw-disasm; see the research note in the
/// v0.4.x commit that introduced this file).
/// </summary>
public sealed class Overworld
{
    // ---- where the tables live (vanilla) ----
    public const int Layer1Tilemap = 0x0CF7DF;         // 0x800 bytes: tile number per 16x16 cell
    public const int Map16Defs = 0x05D000;             // 8 bytes per tile: words TL, BL, TR, BR
    public const int Map16Count = 0xC1;                // the next table starts at $05D608
    public const int Layer2Low = 0x04A533;             // RLE stream of 8x8 tile numbers
    public const int Layer2High = 0x04C02B;            // RLE stream of their property bytes
    /// <summary>GFX list row for the main map; submap n uses row Tileset + n (GFX1C 1D 08 1E on
    /// every one of them in vanilla, so the rows exist for Lunar Magic to bypass per submap).</summary>
    public const int Tileset = 0x11;
    public const int SpriteSet = 0x11;

    public const int Cols = 32, Rows = 32;             // one map, in 16x16 tiles
    public const int MapTiles = Cols * Rows;           // 0x400
    public const int Submaps = 7;                      // main + six

    public Rom Rom { get; }
    /// <summary>Layer 1, 0x800 bytes: main map then the submap map, in the engine's index order.</summary>
    public byte[] Layer1 { get; }
    /// <summary>Layer 2, 0x2000 words: two 64x64 8x8 tilemaps in SNES screen order. The ROM's
    /// edited copy when the project has one, so an edit shows here and builds from one array.</summary>
    public ushort[] Layer2 { get; }

    private readonly Map16.Word[][] defs = new Map16.Word[Map16Count][];
    private readonly Gfx.FgTiles?[] fg = new Gfx.FgTiles?[Submaps];
    private readonly Palette?[] pal = new Palette?[Submaps];
    private readonly Dictionary<(int Tile, int Submap), uint[]> map16Px = [];

    public Overworld(Rom rom)
    {
        Rom = rom;
        Layer1 = rom.Data.AsSpan(rom.FileOffset(Layer1Tilemap), 2 * MapTiles).ToArray();
        Layer2 = rom.OwLayer2 ??= DecodeLayer2(rom);
        int d = rom.FileOffset(Map16Defs);
        for (int t = 0; t < Map16Count; t++)
            defs[t] = [.. Enumerable.Range(0, 4).Select(q =>
                new Map16.Word((ushort)(rom.Data[d + t * 8 + q * 2] | (rom.Data[d + t * 8 + q * 2 + 1] << 8))))];
    }

    /// <summary>The engine's layer 1 index for a 16x16 cell ($049885): the map is four 16x16-tile
    /// screens, TL TR BL BR, each 0x100 bytes.</summary>
    public static int Layer1Index(int x, int y, bool submapMap)
        => (x & 0xF) | ((x & 0x10) << 4) | ((y & 0xF) << 4) | ((y & 0x10) != 0 ? 0x200 : 0) | (submapMap ? MapTiles : 0);

    /// <summary>The layer 2 word index for an 8x8 cell (0-63 each way): a 64x64 SNES tilemap is
    /// four 32x32 screens of 0x400 words, TL TR BL BR — the same quadrant order as layer 1.</summary>
    public static int Layer2Index(int cx, int cy, bool submapMap)
        => ((cy >> 5) * 2 + (cx >> 5)) * 0x400 + (cy & 31) * 32 + (cx & 31) + (submapMap ? 0x1000 : 0);

    public int Layer1At(int x, int y, bool submapMap) => Layer1[Layer1Index(x, y, submapMap)];

    /// <summary>
    /// Which submap a cell belongs to, for its palette. The six submaps sit on the second map
    /// two columns by three rows; their fixed cameras ($049A0C) show the left column from tile
    /// X 0 and the right from X 16, and the rows from about Y 0, 8 and 18. ponytail: the rows
    /// overlap by a few tiles on screen, so the split between rows is the midpoint of the two
    /// cameras — good enough to colour a cell, and only the palette rides on it.
    /// </summary>
    public static int SubmapAt(int x, int y, bool submapMap)
        => !submapMap ? 0 : 1 + (y < 10 ? 0 : y < 20 ? 1 : 2) + (x >= 16 ? 3 : 0);

    public Palette PaletteOf(int submap) => pal[submap] ??= Palette.LoadOverworld(Rom, submap);

    private Gfx.FgTiles TilesOf(int submap) => fg[submap] ??= WithAnimatedTiles(Gfx.FgTiles.Load(Rom, Tileset + submap, levelAnimation: false));

    /// <summary>
    /// The overworld's eleven animated tiles, at rest. Every frame the game builds VRAM tiles
    /// 0x75-0x7F out of GFX14 — the file decompressed last, so still in the buffer — and uploads
    /// them ($00A4E3: 0x160 bytes to VRAM word $0750). Three are fixed, GFX14 tiles 0x50-0x52
    /// ($048000); eight cycle through four frames each, tile 0x40 + 8k being frame 0 ($048006,
    /// stepped by the frame counter at $048123). Frame 0 is what Lunar Magic shows too.
    /// </summary>
    private Gfx.FgTiles WithAnimatedTiles(Gfx.FgTiles tiles)
    {
        if (Gfx.Cached(Rom, 0x14) is not { } gfx14) return tiles;
        int bpp = Gfx.FileBpp(Rom, 0x14), tb = Gfx.TileBytes(bpp);
        byte[] Tile(int n) => (n + 1) * tb <= gfx14.Length ? Gfx.DecodeTile(gfx14, n * tb, bpp) : new byte[64];
        for (int i = 0; i < 3; i++) tiles.Set(0x75 + i, Tile(0x50 + i));
        for (int k = 0; k < 8; k++) tiles.Set(0x78 + k, Tile(0x40 + 8 * k));
        return tiles;
    }

    /// <summary>A layer 1 tile as a 16x16 image, transparent where its art is. Cached: layer 1
    /// does not change under the layer 2 editor, and every 8x8 cell asks for a quarter of one.</summary>
    public uint[] Map16Pixels(int tile, int submap)
    {
        if (tile >= Map16Count) return new uint[256];
        if (map16Px.TryGetValue((tile, submap), out var done)) return done;
        return map16Px[(tile, submap)] = Map16.Compose(defs[tile], TilesOf(submap).Fetch, PaletteOf(submap));
    }

    /// <summary>One 8x8 tilemap word drawn in a submap's colours, opaque: layer 2 is the bottom
    /// layer, so its colour 0 shows the backdrop (CGRAM 0).</summary>
    public uint[] TilePixels(int word, int submap)
    {
        var w = new Map16.Word((ushort)word);
        var src = TilesOf(submap).Fetch(w.Tile);
        var p = PaletteOf(submap);
        var img = new uint[64];
        for (int py = 0; py < 8; py++)
            for (int px = 0; px < 8; px++)
            {
                int idx = src[(w.FlipY ? 7 - py : py) * 8 + (w.FlipX ? 7 - px : px)];
                img[py * 8 + px] = idx == 0 ? p.Rgba[0] | 0xFF000000 : p.Rgba[w.Palette * 16 + idx];
            }
        return img;
    }

    /// <summary>An 8x8 cell as it shows on the map: <paramref name="word"/> (the layer 2 word
    /// there — passed in so a stroke in progress draws before it is committed) under the
    /// quarter of the layer 1 tile that covers it.</summary>
    public uint[] Cell8Pixels(int word, int cx, int cy, bool submapMap)
    {
        int x = cx >> 1, y = cy >> 1;
        var img = TilePixels(word, SubmapAt(x, y, submapMap));
        var over = Map16Pixels(Layer1At(x, y, submapMap), SubmapAt(x, y, submapMap));
        int ox = (cx & 1) * 8, oy = (cy & 1) * 8;
        for (int py = 0; py < 8; py++)
            for (int px = 0; px < 8; px++)
                if (over[(oy + py) * 16 + ox + px] is var o && o != 0) img[py * 8 + px] = o;
        return img;
    }

    /// <summary>The quarter of the layer 1 tile over an 8x8 cell, transparent where it has no
    /// art — the layer drawn OVER the land, kept apart so a layer 2 edit never carries it.</summary>
    public uint[] Layer1QuarterPixels(int cx, int cy, bool submapMap)
    {
        int x = cx >> 1, y = cy >> 1;
        var over = Map16Pixels(Layer1At(x, y, submapMap), SubmapAt(x, y, submapMap));
        var img = new uint[64];
        int ox = (cx & 1) * 8, oy = (cy & 1) * 8;
        for (int py = 0; py < 8; py++) Array.Copy(over, (oy + py) * 16 + ox, img, py * 8, 8);
        return img;
    }

    /// <summary>A 16x16 cell as it shows on the map: its four 8x8s.</summary>
    public uint[] CellPixels(int x, int y, bool submapMap)
    {
        var img = new uint[256];
        for (int q = 0; q < 4; q++)
        {
            int cx = x * 2 + (q & 1), cy = y * 2 + (q >> 1);
            var quad = Cell8Pixels(Layer2[Layer2Index(cx, cy, submapMap)], cx, cy, submapMap);
            for (int py = 0; py < 8; py++) Array.Copy(quad, py * 8, img, ((q >> 1) * 8 + py) * 16 + (q & 1) * 8, 8);
        }
        return img;
    }

    /// <summary>
    /// The two layer 2 streams ($04DABA): a header byte with bit 7 clear copies the next n+1
    /// bytes, with bit 7 set repeats the next byte (n &amp; 0x7F)+1 times; each stream fills one
    /// byte of every word until 0x2000 words are out. Low bytes are tile numbers, high bytes
    /// the vhopppcc properties.
    /// </summary>
    public static ushort[] DecodeLayer2(Rom rom) => DecodeLayer2(rom, out _, out _);

    /// <summary>Decode, and say where each stream's bytes end — the room an edited map has.</summary>
    public static ushort[] DecodeLayer2(Rom rom, out int lowEnd, out int highEnd)
    {
        var words = new ushort[0x2000];
        int[] ends = new int[2];
        foreach (var (snes, shift, k) in new[] { (Layer2Low, 0, 0), (Layer2High, 8, 1) })
        {
            int p = rom.FileOffset(snes), o = 0;
            while (o < words.Length)
            {
                int n = rom.Data[p++];
                if ((n & 0x80) == 0)
                    for (int i = 0; i <= n && o < words.Length; i++) words[o++] |= (ushort)(rom.Data[p++] << shift);
                else
                {
                    ushort v = (ushort)(rom.Data[p++] << shift);
                    for (int i = 0; i <= (n & 0x7F) && o < words.Length; i++) words[o++] |= v;
                }
            }
            ends[k] = p;
        }
        (lowEnd, highEnd) = (ends[0], ends[1]);
        return words;
    }

    /// <summary>The inverse of <see cref="DecodeLayer2"/> for one byte plane: runs of three or
    /// more repeat, everything else goes out as literals, both capped at the header's 128.</summary>
    public static byte[] EncodeStream(ReadOnlySpan<byte> plane)
    {
        var out_ = new List<byte>(plane.Length / 4);
        int i = 0;
        while (i < plane.Length)
        {
            int run = 1;
            while (i + run < plane.Length && run < 128 && plane[i + run] == plane[i]) run++;
            if (run >= 3) { out_.Add((byte)(0x80 | (run - 1))); out_.Add(plane[i]); i += run; continue; }
            int lit = 0;
            while (i + lit < plane.Length && lit < 128)
            {
                // Stop the literal where a run worth a repeat begins.
                int r = 1;
                while (i + lit + r < plane.Length && r < 3 && plane[i + lit + r] == plane[i + lit]) r++;
                if (r >= 3) break;
                lit++;
            }
            out_.Add((byte)(lit - 1));
            for (int k = 0; k < lit; k++) out_.Add(plane[i + k]);
            i += lit;
        }
        return [.. out_];
    }

    /// <summary>The two streams for a layer 2 map: tile numbers, then property bytes.</summary>
    public static (byte[] Low, byte[] High) EncodeLayer2(ushort[] words)
    {
        var lo = new byte[words.Length]; var hi = new byte[words.Length];
        for (int i = 0; i < words.Length; i++) { lo[i] = (byte)words[i]; hi[i] = (byte)(words[i] >> 8); }
        return (EncodeStream(lo), EncodeStream(hi));
    }

    /// <summary>
    /// Write an edited layer 2 into the ROM's own stream space. Each stream must fit where the
    /// vanilla one ended — the low stream runs up to the high stream's start, the high stream up
    /// to wherever the ROM's ends — because the loader hard-codes both addresses. Returns a
    /// reason when it does not fit. ponytail: no relocation; a map that packs worse than
    /// vanilla's is refused rather than moved, until the loader's operands are re-pointed.
    /// </summary>
    public static string? WriteLayer2(Rom rom, ushort[] words)
    {
        DecodeLayer2(rom, out _, out int highEnd);
        var (lo, hi) = EncodeLayer2(words);
        int loAt = rom.FileOffset(Layer2Low), hiAt = rom.FileOffset(Layer2High);
        int loRoom = hiAt - loAt, hiRoom = highEnd - hiAt;
        if (lo.Length > loRoom || hi.Length > hiRoom)
            return $"the overworld's layer 2 packs to {lo.Length}+{hi.Length} bytes, and the ROM has room for {loRoom}+{hiRoom}";
        lo.CopyTo(rom.Data, loAt);
        hi.CopyTo(rom.Data, hiAt);
        return null;
    }

    /// <summary>The graphics files the overworld loads, as bins for the Graphics drawer: the
    /// four FG files under the main map's tileset row and the four sprite files under its sprite
    /// set. Bypass words 0x70+ keep them clear of a level's real record words.</summary>
    public static (string Name, int PalRow, int BypWord, int Def, int File, int ColorOffset, int Bpp)[] GfxSlots(Rom rom)
    {
        int fgList = rom.FileOffset(Gfx.ObjectGfxList) + Tileset * 4;
        int spList = rom.FileOffset(Gfx.SpriteGfxList) + SpriteSet * 4;
        return [.. Enumerable.Range(0, 4).Select(i => ($"FG{i + 1}", 4, 0x70 + i, (int)rom.Data[fgList + i], (int)rom.Data[fgList + i], 0, 0)),
                .. Enumerable.Range(0, 4).Select(i => ($"SP{i + 1}", 8, 0x74 + i, (int)rom.Data[spList + i], (int)rom.Data[spList + i], 0, 0))];
    }
}
