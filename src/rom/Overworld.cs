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
/// table. Lunar Magic moves the layer 2 streams and the Map16 table when it saves an
/// overworld, and adds a second byte to every layer 1 cell; <see cref="Tables"/> reads where
/// this ROM keeps them out of the loader's own operands, so an LM-edited map reads as LM
/// wrote it (reference/OVERWORLD.md §11; bank 04 traced in reference/smw-disasm).
/// </summary>
public sealed class Overworld
{
    // ---- where the tables live (vanilla; Tables.Of says where THIS ROM keeps them) ----
    public const int Layer1Tilemap = 0x0CF7DF;         // 0x800 bytes: tile number per 16x16 cell
    public const int Map16Defs = 0x05D000;             // 8 bytes per tile: words TL, BL, TR, BR
    public const int VanillaMap16Count = 0xC1;         // the next table starts at $05D608
    public const int Layer2Low = 0x04A533;             // RLE stream of 8x8 tile numbers
    public const int Layer2High = 0x04C02B;            // RLE stream of their property bytes

    /// <summary>
    /// Where a ROM keeps the overworld tables Lunar Magic relocates, read from the operands of
    /// the code that loads them — the same bytes the game follows, so they are right on a ROM
    /// LM has saved and on vanilla alike. Layer 2: <c>LDA #$addr</c> at $04DC71 and $04DC8C
    /// with the shared bank at $04DC79. Map16: <c>LDX #$addr</c> at $04DCBB, bank at $04DCC0;
    /// LM's table is two pages (its "STAR" marker follows entry 0x200). Layer 1's second byte:
    /// LM replaces the translevel scan at $04D7F2 with two LZ2 blobs decompressed by $00B8DE —
    /// the per-tile level table into $7ED000, then the layer 1 high bytes into $7FC800 —
    /// each behind <c>LDX #$addr : STX $8A : LDA #$bank : STA $8C</c>. A blob address of 0
    /// means the ROM has none (vanilla).
    /// </summary>
    public readonly record struct Tables(int Layer2Low, int Layer2High, int Map16Defs, int Map16Count, int LevelTableBlob, int Layer1HighBlob)
    {
        public static Tables Of(Rom rom)
        {
            var d = rom.Data;
            int low = Overworld.Layer2Low, high = Overworld.Layer2High, defs = Overworld.Map16Defs;
            int p = rom.FileOffset(0x04DC71), q = rom.FileOffset(0x04DC8C);
            if (d[p] == 0xA9 && d[p + 7] == 0xA9 && d[q] == 0xA9)
            {
                int bank = d[p + 8] << 16;
                low = bank | d[p + 1] | d[p + 2] << 8;
                high = bank | d[q + 1] | d[q + 2] << 8;
            }
            p = rom.FileOffset(0x04DCBB);
            if (d[p] == 0xA2 && d[p + 5] == 0xA9) defs = d[p + 6] << 16 | d[p + 1] | d[p + 2] << 8;
            // The two blobs, in order, wherever they sit in LM's 0x40-byte rewrite of the scan.
            int level = 0, l1hi = 0;
            p = rom.FileOffset(0x04D7F2);
            for (int i = 0; i < 0x40; i++)
                if (d[p + i] == 0xA2 && d[p + i + 3] == 0x86 && d[p + i + 4] == 0x8A && d[p + i + 5] == 0xA9 && d[p + i + 7] == 0x85 && d[p + i + 8] == 0x8C)
                {
                    int at = d[p + i + 6] << 16 | d[p + i + 1] | d[p + i + 2] << 8;
                    if (level == 0) level = at; else if (l1hi == 0) l1hi = at;
                }
            return new(low, high, defs, defs == Overworld.Map16Defs ? VanillaMap16Count : 0x200, level, l1hi);
        }
    }

    /// <summary>Where this ROM keeps its tables.</summary>
    public Tables At { get; }

    // ---- what a layer 1 tile does underfoot ($049140, $0492F2) ----

    /// <summary>
    /// Path tiles are 0x01-0x55. Their pose byte at $049FEB (read at $0495EF) says how Mario
    /// moves on them: bit 3 swims (0x28-0x3E, 0x50), bit 4 climbs (0x3F-0x41, the ladder tiles,
    /// which also set $1B80 for the ladder speed), anything else walks — bit 2 only picks the
    /// front-facing walk frames, not a ladder. The ten tiles listed at $049426 are the exit tiles
    /// that fire the map-to-map check at $049A24. 0x56-0x82 are level tiles; 0x83-0x86 can be
    /// stood on but not entered. Checked tile by tile against Lunar Magic's "Layer 1 Mario
    /// Paths" view on 2026-09-06 (a grid ROM of every tile): green walk, black rungs climb, blue
    /// swim, red exit, an X where Mario stops without entering.
    /// </summary>
    public enum PathKind { None, Walk, Climb, Swim, Exit, Level, Stop }
    public const int PathPoses = 0x049FEB, ExitTilesList = 0x049426;

    /// <summary>
    /// Lunar Magic's own picture for a layer 1 path tile, 16x16 RGBA with 0 for transparent, or
    /// null for a tile it draws nothing special for. Lifted pixel for pixel from LM's "Layer 1
    /// Mario Paths" view on 2026-09-06: a ROM whose layer 1 was a grid of every tile, captured
    /// with the view off and on over two different stretches of land, "Future Layer 1 Tiles" off
    /// so the level tiles' octagons are opaque, keeping only the pixels both captures agreed on
    /// (the unused tiles 0x52-0x55 LM draws nothing opaque for; they fall back to
    /// <see cref="KindOf"/>'s fill). OwPathGlyphs.bin: a palette count, RGB triples, then 256
    /// palette indexes per tile for tiles 0x00-0x86.
    /// </summary>
    public static uint[]? PathGlyph(int tile)
    {
        var all = glyphs ??= LoadGlyphs();
        return tile >= 0 && tile < all.Length ? all[tile] : null;
    }
    private static uint[]?[]? glyphs;
    private static uint[]?[] LoadGlyphs()
    {
        var table = new uint[]?[0x87];
        try
        {
            using var s = typeof(Overworld).Assembly.GetManifestResourceStream("OwPathGlyphs.bin");
            if (s is null) return table;
            var d = new byte[s.Length];
            s.ReadExactly(d);
            int n = d[0], p = 1;
            var pal = new uint[n + 1];
            for (int i = 1; i <= n; i++, p += 3) pal[i] = 0xFF000000u | (uint)d[p + 2] << 16 | (uint)d[p + 1] << 8 | d[p];
            for (int t = 0; t < table.Length && p + 256 <= d.Length; t++, p += 256)
            {
                bool any = false;
                var px = new uint[256];
                for (int i = 0; i < 256; i++) if (d[p + i] != 0) { px[i] = pal[d[p + i]]; any = true; }
                if (any) table[t] = px;
            }
        }
        catch { /* no pictures, no crash: the kind fills stand in */ }
        return table;
    }

    public PathKind KindOf(int tile)
    {
        if (tile <= 0 || tile > 0x86) return PathKind.None;
        if (tile >= 0x83) return PathKind.Stop;
        if (tile >= 0x56) return PathKind.Level;
        var d = Rom.Data;
        int ex = Rom.FileOffset(ExitTilesList);
        for (int i = 0; i < 10; i++) if (d[ex + i] == tile) return PathKind.Exit;
        int pose = d[Rom.FileOffset(PathPoses) + tile - 1];
        return (pose & 0x10) != 0 ? PathKind.Climb : (pose & 8) != 0 ? PathKind.Swim : PathKind.Walk;
    }

    // ---- level tiles: translevel, level, base event ----

    /// <summary>The translevel (0-0x5F) each layer 1 cell enters, by index: Lunar Magic's
    /// per-tile table when the ROM has one, else what the vanilla scan at $04D7F2 computes —
    /// every 0x56-0x80 tile numbered 1, 2, 3… in index order.</summary>
    public byte[] Translevels { get; }
    public int TranslevelAt(int x, int y, bool submapMap) => Translevels[Layer1Index(x, y, submapMap)];

    /// <summary>The level a translevel enters ($05D8A2): 1-0x24 as they are, 0x25-0x5F as 0x101-0x13B.</summary>
    public static int LevelOf(int translevel) => translevel == 0 ? 0 : translevel < 0x25 ? translevel : (translevel - 0x24) | 0x100;

    /// <summary>The event a translevel's normal exit fires ($05D608, read at $05D9CC; a secret
    /// exit adds its number), or -1 for $FF, none.</summary>
    public const int BaseEvents = 0x05D608;
    public int BaseEventOf(int translevel) => Rom.Data[Rom.FileOffset(BaseEvents) + (translevel & 0x7F)] is var e && e != 0xFF ? e : -1;

    // ---- transitions: star/pipe warps, exit tiles, Koopa Kid drops ----

    /// <summary>A star, pipe or location teleport. Source in tiles of the map the submap lives
    /// on ($048431 word: submap&lt;&lt;8 | $1F1F, whose low five bits are the tile X; $048467:
    /// tile Y); destination in pixels ($04849D: X | submap&lt;&lt;9; $0484D3: Y). DestIndex is
    /// the entry whose source cell is this destination, or -1 — Lunar Magic's "N/A", a one-way
    /// trip.</summary>
    public readonly record struct Warp(int Index, int Submap, int X, int Y, int DestSubmap, int DestX, int DestY, int DestIndex);
    public const int WarpSources = 0x048431, WarpSourceYs = 0x048467, WarpDestXs = 0x04849D, WarpDestYs = 0x0484D3;

    /// <summary>How many warps the lookup at $048509 walks: 27 in vanilla; Lunar Magic's hook
    /// there counts them in its <c>LDX #$n*2</c> at hook+$F (zero on a hack with none).</summary>
    public int WarpCount
    {
        get
        {
            var d = Rom.Data;
            int p = Rom.FileOffset(0x048509);
            if (d[p] != 0x22) return 27;
            int hook = Rom.FileOffset(d[p + 1] | d[p + 2] << 8 | d[p + 3] << 16);
            return d[hook + 0xF] == 0xA2 ? (d[hook + 0x10] | d[hook + 0x11] << 8) / 2 : 27;
        }
    }

    private List<Warp>? warps;
    public IReadOnlyList<Warp> Warps => warps ??= ReadWarps();
    private List<Warp> ReadWarps()
    {
        var d = Rom.Data;
        int s = Rom.FileOffset(WarpSources), sy = Rom.FileOffset(WarpSourceYs), dx = Rom.FileOffset(WarpDestXs), dy = Rom.FileOffset(WarpDestYs);
        int n = WarpCount;
        var raw = new List<(int Sub, int X, int Y, int DSub, int DX, int DY)?>();
        for (int i = 0; i < n; i++)
        {
            int sw = d[s + 2 * i] | d[s + 2 * i + 1] << 8, yw = d[sy + 2 * i] | d[sy + 2 * i + 1] << 8;
            int dw = d[dx + 2 * i] | d[dx + 2 * i + 1] << 8, dyw = d[dy + 2 * i] | d[dy + 2 * i + 1] << 8;
            raw.Add(sw == 0xFFFF ? null : ((sw >> 8) & 0xF, sw & 0x1F, yw & 0x1F, (dw >> 9) & 0xF, dw & 0x1FF, dyw & 0x1FF));
        }
        var list = new List<Warp>();
        for (int i = 0; i < n; i++)
        {
            if (raw[i] is not { } w) continue;
            int dest = raw.FindIndex(o => o is { } q && q.Sub == w.DSub && q.X == w.DX >> 4 && q.Y == w.DY >> 4);
            list.Add(new(i, w.Sub, w.X, w.Y, w.DSub, w.DX, w.DY, dest));
        }
        return list;
    }

    /// <summary>An exit tile's teleport: walk onto a red path tile at the source and arrive at
    /// the destination on another map. Fourteen 5-byte entries ($049964 Y px, $049966 X px,
    /// $049968 submap; destinations $0499AA/AC/AE), walked at $049A3F. Positions are pixels
    /// in the table and tiles here. DestIndex as for <see cref="Warp"/>.</summary>
    public readonly record struct ExitPath(int Index, int Submap, int X, int Y, int DestSubmap, int DestX, int DestY, int DestIndex);
    public const int ExitSources = 0x049964, ExitDests = 0x0499AA, ExitCount = 14;

    private List<ExitPath>? exits;
    public IReadOnlyList<ExitPath> ExitPaths => exits ??= ReadExitPaths();
    private List<ExitPath> ReadExitPaths()
    {
        var d = Rom.Data;
        int s = Rom.FileOffset(ExitSources), t = Rom.FileOffset(ExitDests);
        var raw = new List<(int Sub, int X, int Y, int DSub, int DX, int DY)?>();
        for (int i = 0; i < ExitCount; i++)
        {
            int p = s + 5 * i, q = t + 5 * i;
            int y = d[p] | d[p + 1] << 8, x = d[p + 2] | d[p + 3] << 8;
            raw.Add(y == 0xFFFF ? null : (d[p + 4] & 0xF, (x & 0x1FF) >> 4, (y & 0x1FF) >> 4, d[q + 4] & 0xF, ((d[q + 2] | d[q + 3] << 8) & 0x1FF) >> 4, ((d[q] | d[q + 1] << 8) & 0x1FF) >> 4));
        }
        var list = new List<ExitPath>();
        for (int i = 0; i < ExitCount; i++)
        {
            if (raw[i] is not { } e) continue;
            // The arrival is one tile short of the tile that comes back, in the direction of
            // travel — so the link is the source a tile away, which is why LM asks which side.
            int dest = raw.FindIndex(o => o is { } q && q.Sub == e.DSub && Math.Abs(q.X - e.DX) + Math.Abs(q.Y - e.DY) <= 1);
            list.Add(new(i, e.Sub, e.X, e.Y, e.DSub, e.DX, e.DY, dest));
        }
        return list;
    }

    // ---- layer 2 events: the pieces an event lays on the land ($04E496) ----

    /// <summary>
    /// One standard step of a layer 2 event: a piece written onto the land, one per frame with
    /// a sound while the event plays. A step is 4 bytes, [src][dst]: src at or past 0x900 is a
    /// 2x2 piece (one 16x16), below that a 6x6 (three); dst is a byte offset into the layer 2
    /// buffer at $7F4000, whose words sit in the same screen order as <see cref="Layer2Index"/>,
    /// so cells here are 8x8 columns and rows of the map the submap lives on. Event N owns steps
    /// ends[N-1]..ends[N] of the cumulative table at $04E35B (the word before it is 0). Lunar
    /// Magic leaves that table in place and relocates the steps (long operand at $04E49E).
    /// </summary>
    public readonly record struct EventStep(int Event, int Piece, int Cx, int Cy, bool SubmapMap, int Size);
    public const int EventStepEnds = 0x04E35B, EventSteps_ = 0x04DD8D, EventCount = 0x78;

    private List<EventStep>? eventSteps;
    public IReadOnlyList<EventStep> EventSteps => eventSteps ??= ReadEventSteps();
    private List<EventStep> ReadEventSteps()
    {
        var d = Rom.Data;
        int p = Rom.FileOffset(0x04E49E);
        int steps = d[p] == 0xBF ? Rom.FileOffset(d[p + 1] | d[p + 2] << 8 | d[p + 3] << 16) : Rom.FileOffset(EventSteps_);
        int ends = Rom.FileOffset(EventStepEnds);
        var list = new List<EventStep>();
        int start = d[ends - 2] | d[ends - 1] << 8;
        for (int ev = 0; ev < EventCount; ev++)
        {
            int end = d[ends + 2 * ev] | d[ends + 2 * ev + 1] << 8;
            for (int k = start; k < end && k < 0x1000; k++)
            {
                int src = d[steps + 4 * k] | d[steps + 4 * k + 1] << 8, dst = d[steps + 4 * k + 2] | d[steps + 4 * k + 3] << 8;
                int w = dst >> 1;
                bool sub = w >= 0x1000;
                w &= 0xFFF;
                int scr = w >> 10;
                list.Add(new(ev, src, (scr & 1) * 32 + (w & 31), (scr >> 1) * 32 + ((w >> 5) & 31), sub, src >= 0x900 ? 2 : 6));
            }
            start = Math.Max(start, end);
        }
        return list;
    }

    /// <summary>Where a Koopa Kid drops Mario on the main map when he fails the level it pulled
    /// him into: three positions, X px at $048E49 and Y px at $048E4F ($048EBD), in tiles here.</summary>
    public const int KoopaXs = 0x048E49, KoopaYs = 0x048E4F;
    public IReadOnlyList<(int X, int Y)> KoopaTeleports
    {
        get
        {
            var d = Rom.Data;
            int xs = Rom.FileOffset(KoopaXs), ys = Rom.FileOffset(KoopaYs);
            var list = new List<(int, int)>();
            for (int i = 0; i < 3; i++)
            {
                int x = d[xs + 2 * i] | d[xs + 2 * i + 1] << 8, y = d[ys + 2 * i] | d[ys + 2 * i + 1] << 8;
                if (x != 0xFFFF) list.Add(((x & 0x1FF) >> 4, (y & 0x1FF) >> 4));
            }
            return list;
        }
    }
    /// <summary>How many Map16 tiles layer 1 can name: 0xC1 in vanilla, two pages once LM has saved.</summary>
    public int Map16Count => At.Map16Count;
    /// <summary>GFX list row for the main map; submap n uses row Tileset + n (GFX1C 1D 08 1E on
    /// every one of them in vanilla, so the rows exist for Lunar Magic to bypass per submap).</summary>
    public const int Tileset = 0x11;
    public const int SpriteSet = 0x11;

    public const int Cols = 32, Rows = 32;             // one map, in 16x16 tiles
    public const int MapTiles = Cols * Rows;           // 0x400
    public const int Submaps = 7;                      // main + six

    public Rom Rom { get; }
    /// <summary>Layer 1, 0x800 cells: main map then the submap map, in the engine's index order.
    /// Vanilla's byte from $0CF7DF, under the high byte LM keeps in its $7FC800 blob.</summary>
    public ushort[] Layer1 { get; }
    /// <summary>Layer 2, 0x2000 words: two 64x64 8x8 tilemaps in SNES screen order. The ROM's
    /// edited copy when the project has one, so an edit shows here and builds from one array.</summary>
    public ushort[] Layer2 { get; }

    private readonly Map16.Word[][] defs;
    private readonly Gfx.FgTiles?[] fg = new Gfx.FgTiles?[Submaps];
    private readonly Palette?[] pal = new Palette?[Submaps];
    private readonly Dictionary<(int Tile, int Submap), uint[]> map16Px = [];

    public Overworld(Rom rom)
    {
        Rom = rom;
        At = Tables.Of(rom);
        Layer1 = rom.OwLayer1 ??= ReadLayer1(rom, At);
        Translevels = new byte[2 * MapTiles];
        ReadTranslevels();
        Layer2 = rom.OwLayer2 ??= DecodeLayer2(rom);
        defs = new Map16.Word[Map16Count][];
        int d = rom.FileOffset(At.Map16Defs);
        for (int t = 0; t < Map16Count; t++)
            defs[t] = [.. Enumerable.Range(0, 4).Select(q =>
                new Map16.Word((ushort)(rom.Data[d + t * 8 + q * 2] | (rom.Data[d + t * 8 + q * 2 + 1] << 8))))];
    }

    /// <summary>Which level each cell enters, from LM's per-tile table where the ROM has one;
    /// vanilla numbers its level tiles by their order in the map, so an edited map renumbers
    /// itself the way the game would — run again after a layer 1 edit. ponytail: LM's table is
    /// read as it is, so a level tile moved on such a ROM leaves its settings behind until the
    /// Levels editor carries them.</summary>
    public void ReadTranslevels()
    {
        if (At.LevelTableBlob != 0)
        {
            Gfx.Lz2Decompress(Rom.Data, Rom.FileOffset(At.LevelTableBlob), 0x1000).AsSpan(0, 2 * MapTiles).CopyTo(Translevels);
            return;
        }
        Array.Clear(Translevels);
        for (int i = 0, n = 1; i < Translevels.Length; i++)
            if ((Layer1[i] & 0xFF) >= 0x56 && (Layer1[i] & 0xFF) <= 0x80) Translevels[i] = (byte)n++;
    }

    /// <summary>Layer 1 as the ROM holds it: vanilla's byte per cell at $0CF7DF under the high
    /// byte Lunar Magic keeps in its LZ2 table, where the ROM has one.</summary>
    private static ushort[] ReadLayer1(Rom rom, Tables at)
    {
        var lo = rom.Data.AsSpan(rom.FileOffset(Layer1Tilemap), 2 * MapTiles);
        var hi = at.Layer1HighBlob != 0 ? Gfx.Lz2Decompress(rom.Data, rom.FileOffset(at.Layer1HighBlob), 2 * MapTiles) : [];
        var words = new ushort[2 * MapTiles];
        for (int i = 0; i < words.Length; i++) words[i] = (ushort)(lo[i] | (i < hi.Length ? hi[i] << 8 : 0));
        return words;
    }

    /// <summary>
    /// Write an edited layer 1 into the ROM: the low bytes in place at $0CF7DF, and the high
    /// bytes into Lunar Magic's table where the ROM has one and the packed table fits where the
    /// old one sat. Returns a reason when a tile from page 1 cannot be kept — a vanilla ROM has
    /// nowhere to put its high byte. ponytail: no relocation of LM's table, as with layer 2.
    /// </summary>
    public static string? WriteLayer1(Rom rom, ushort[] words)
    {
        int lo = rom.FileOffset(Layer1Tilemap);
        for (int i = 0; i < 2 * MapTiles; i++) rom.Data[lo + i] = (byte)words[i];
        if (!words.Any(w => w > 0xFF)) return null;
        var at = Tables.Of(rom);
        if (at.Layer1HighBlob == 0) return "layer 1 uses tiles 0x100+, which need Lunar Magic's high-byte table; this ROM has none";
        int blob = rom.FileOffset(at.Layer1HighBlob);
        int room = Gfx.Lz2Length(rom.Data, blob);
        var packed = Gfx.Lz2Compress([.. words.Select(w => (byte)(w >> 8))]);
        if (packed.Length > room) return $"layer 1's high bytes pack to {packed.Length} bytes, and Lunar Magic's table has room for {room}";
        packed.CopyTo(rom.Data, blob);
        return null;
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
    /// X 0 and the right from X 16, and the rows overlap on screen, so the split is a choice.
    /// Lunar Magic's, read off its own pixels on 2026-09-06, changes palette at 8x8 rows 22 and
    /// 43 of the lower map as it draws it — see <see cref="SubmapAtRow8"/>. This 16x16 form, for
    /// layer 1 art in the map's own grid, rounds those to tiles 11 and 21.
    /// </summary>
    public static int SubmapAt(int x, int y, bool submapMap)
        => !submapMap ? 0 : 1 + (y < 11 ? 0 : y < 21 ? 1 : 2) + (x >= 16 ? 3 : 0);

    /// <summary>The submap for a cell of the lower map by its DRAWN 8x8 column and row on Lunar
    /// Magic's canvas: the palette changes at rows 22 and 43, and at column 32.</summary>
    public const int SubmapRow8Middle = 22, SubmapRow8Bottom = 43;
    public static int SubmapAtRow8(int col8, int row8)
        => 1 + (row8 < SubmapRow8Middle ? 0 : row8 < SubmapRow8Bottom ? 1 : 2) + (col8 >= 32 ? 3 : 0);

    public Palette PaletteOf(int submap) => pal[submap] ??= Palette.LoadOverworld(Rom, submap);

    private Gfx.FgTiles TilesOf(int submap) => fg[submap] ??= WithAnimatedTiles(Gfx.FgTiles.Load(Rom, Tileset + submap, levelAnimation: false));

    /// <summary>The game's frame counter the animated tiles are drawn for; starts on Lunar
    /// Magic's frame. <see cref="Animate"/> moves it.</summary>
    public int AnimationCounter { get; private set; } = LunarMagicCounter;
    public const int LunarMagicCounter = 8;

    /// <summary>Show the animated tiles as the game has them at <paramref name="counter"/>:
    /// every loaded tileset takes the frames' pictures, and the layer 1 pictures composed
    /// from them are dropped to be composed again. A tick of the editor's animation is eight
    /// game frames, the step at which slots 2-7 change.</summary>
    public void Animate(int counter)
    {
        AnimationCounter = counter & 0x7F;
        foreach (var tiles in fg) if (tiles is not null) WithAnimatedTiles(tiles);
        map16Px.Clear();    // ponytail: drops every composed layer 1 tile per tick; keep only the ones that use slots 0x75-0x7F if the map ever stutters
    }

    /// <summary>The frame a cycling slot shows at a counter ($048123): slots 2-7 take counter
    /// bits 3-5, the two waterfall slots bits 4-6.</summary>
    private static int AnimationFrame(int counter, int slot) => slot < 2 ? (counter >> 4) & 7 : (counter >> 3) & 7;

    /// <summary>
    /// The overworld's eleven animated tiles, as Lunar Magic shows them. Every frame the game
    /// builds VRAM tiles 0x75-0x7F out of GFX14 — the file decompressed last, so still in the
    /// buffer — and uploads them ($00A4E3: 0x160 bytes to VRAM word $0750). Three are water,
    /// GFX14 tiles 0x50-0x52 ($048000) SCROLLED in RAM every eight frames ($0480E0): 0x75's rows
    /// 0-3 a pixel left and 4-7 a pixel right, 0x76 a row down, 0x77 a pixel left and a row
    /// down; Lunar Magic shows them unscrolled. Eight cycle through eight frames each, GFX14 tile
    /// 0x40 + 8k + frame ($048006, one table of 64), the frame read off the frame counter at
    /// $048123: bits 3-5 for slots 2-7, bits 4-6 for the two waterfall slots. Lunar Magic draws
    /// the map as the game has it at counter 8-15 — slots 2-7 on their second frame, the
    /// waterfall on its first (read off LM's pixels for the Special World's letters and level
    /// sparkles on 2026-09-06; vanilla places no waterfall tile, so those two follow the game).
    /// The tiles are those of <see cref="AnimationCounter"/>, so a running animation shows the
    /// game's cycle.
    /// </summary>
    private Gfx.FgTiles WithAnimatedTiles(Gfx.FgTiles tiles)
    {
        if (Gfx.Cached(Rom, 0x14) is not { } gfx14) return tiles;
        int bpp = Gfx.FileBpp(Rom, 0x14), tb = Gfx.TileBytes(bpp);
        byte[] Tile(int n) => (n + 1) * tb <= gfx14.Length ? Gfx.DecodeTile(gfx14, n * tb, bpp) : new byte[64];
        int scroll = ((AnimationCounter >> 3) + 7) & 7;            // no scroll at Lunar Magic's counter
        tiles.Set(0x75, Scrolled(Tile(0x50), row => row < 4 ? scroll : -scroll, 0));
        tiles.Set(0x76, Scrolled(Tile(0x51), _ => 0, scroll));
        tiles.Set(0x77, Scrolled(Tile(0x52), _ => scroll, scroll));
        for (int k = 0; k < 8; k++) tiles.Set(0x78 + k, Tile(0x40 + 8 * k + AnimationFrame(AnimationCounter, k)));
        return tiles;
    }

    /// <summary>A tile with each row moved <paramref name="left"/>(row) pixels left and the whole
    /// tile <paramref name="down"/> rows down, wrapping — what the game's ROL/ROR of a bitplane
    /// row and its row rotation do to the picture.</summary>
    private static byte[] Scrolled(byte[] px, Func<int, int> left, int down)
    {
        var img = new byte[64];
        for (int y = 0; y < 8; y++)
        {
            int sy = (y - down) & 7, dx = left(sy);
            for (int x = 0; x < 8; x++) img[y * 8 + x] = px[sy * 8 + ((x + dx) & 7)];
        }
        return img;
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
        var at = Tables.Of(rom);
        foreach (var (snes, shift, k) in new[] { (at.Layer2Low, 0, 0), (at.Layer2High, 8, 1) })
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
    /// ROM's one ended — the low stream runs up to the high stream's start, the high stream up
    /// to wherever the ROM's ends — because the loader hard-codes both addresses (LM's
    /// relocated ones included: the streams stay where <see cref="Tables.Of"/> found them).
    /// Returns a reason when it does not fit. ponytail: no relocation; a map that packs worse
    /// than the ROM's is refused rather than moved, until the loader's operands are re-pointed.
    /// </summary>
    public static string? WriteLayer2(Rom rom, ushort[] words)
    {
        DecodeLayer2(rom, out _, out int highEnd);
        var (lo, hi) = EncodeLayer2(words);
        var at = Tables.Of(rom);
        int loAt = rom.FileOffset(at.Layer2Low), hiAt = rom.FileOffset(at.Layer2High);
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
