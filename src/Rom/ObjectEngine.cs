namespace PipeDream;

/// <summary>A level's expanded Map16 tilemap (16-bit tile indices; 0xFFFF = empty).</summary>
public sealed class Map16Grid
{
    public readonly int Width, Height;
    public readonly ushort[] Tiles;
    public const ushort Empty = 0xFFFF;

    public Map16Grid(int w, int h)
    {
        Width = w; Height = h;
        Tiles = new ushort[w * h];
        Array.Fill(Tiles, Empty);
    }

    public void Set(int x, int y, int tile)
    {
        if ((uint)x < (uint)Width && (uint)y < (uint)Height) Tiles[y * Width + x] = (ushort)tile;
    }
    public int Get(int x, int y)
        => (uint)x < (uint)Width && (uint)y < (uint)Height ? Tiles[y * Width + x] : Empty;
    public int PlacedCount()
    {
        int c = 0;
        foreach (var t in Tiles) if (t != Empty) c++;
        return c;
    }

    public Map16Grid Clone()
    {
        var g = new Map16Grid(Width, Height);
        Array.Copy(Tiles, g.Tiles, Tiles.Length);
        return g;
    }
}

/// <summary>
/// Expands a parsed <see cref="Level"/> into a Map16 grid by replicating the SMW object
/// handlers (bank 0D). See reference/CONTRACT.md §4a/§4b. Currently implements the two
/// shared families (rectangle fill + single-tile lookup); other object families place a
/// marker tile (bit 0x8000 set) until their handlers are ported.
/// </summary>
public static class ObjectEngine
{
    // Object-number bit distinguishing a not-yet-implemented placeholder from a real tile.
    public const int Marker = 0x8000;

    public static Map16Grid Render(Rom rom, Level level)
    {
        int w = Math.Max(16, level.Header.Screens * 16);
        var g = new Map16Grid(w, 32);

        // Tile tables read from bank 0D (unchanged by Lunar Magic).
        int[] rect = ReadTable(rom, 0x0DA8B4, 15);     // rectangle fill tiles, obj 1-0x0E
        int[] single = ReadTable(rom, 0x0DA548, 0x33); // single-tile ext objects, ext 0x10+
        int[] ledTop = ReadTable(rom, 0x0DB039, 15);   // ledge-edge top tiles (by type)
        int[] ledMid = ReadTable(rom, 0x0DB057, 15);   // ledge-edge middle tiles
        int[] ledBot = ReadTable(rom, 0x0DB066, 15);   // ledge-edge bottom tiles (0xFF = none)
        int[] bushL = ReadTable(rom, 0x0DB5A8, 5);     // bush left/middle/right tiles (by type)
        int[] bushM = ReadTable(rom, 0x0DB5AD, 5);
        int[] bushR = ReadTable(rom, 0x0DB5B2, 5);
        int[] pipeTL = ReadTable(rom, 0x0DAA12, 5);    // vertical-pipe top-cap tiles (by type)
        int[] pipeTR = ReadTable(rom, 0x0DAA17, 5);
        int[] rope = ReadTable(rom, 0x0DB3BB, 2);      // rope/cloud tiles (by type)
        int[] mwTop = ReadTable(rom, 0x0DB212, 3), mwMid = ReadTable(rom, 0x0DB215, 3), mwBot = ReadTable(rom, 0x0DB218, 3);
        int[] glTop = ReadTable(rom, 0x0DB21B, 3), glMid = ReadTable(rom, 0x0DB21E, 3), glBot = ReadTable(rom, 0x0DB221, 3);

        foreach (var o in level.Objects)
        {
            if (o.IsScreenExit) continue;                       // no tiles
            int ax = o.AbsoluteX, ay = o.Y;

            if (o.IsDm16)                                       // LM Direct Map16: w×h of one tile
            {
                for (int dy = 0; dy < o.Height; dy++)
                    for (int dx = 0; dx < o.Width; dx++)
                        g.Set(ax + dx, ay + dy, o.Dm16Tile);
                continue;
            }

            if (o.Extended)
            {
                int ext = o.ExtendedNumber;
                if (ext == 0x01) continue;                      // screen jump: no tiles
                int idx = ext - 0x10;
                if (idx >= 0 && idx < single.Length)
                    g.Set(ax, ay, single[idx] | (idx >= 0x13 ? 0x100 : 0));   // $0DA5B1 page rule
                else
                    g.Set(ax, ay, Marker | ext);
                continue;
            }

            int n = o.Number, b3 = o.Byte3;
            switch (n)
            {
                case >= 1 and <= 0x0E:                          // rectangle family ($0DA8C3)
                {
                    int i = n - 1, tile = rect[i] | (i >= 7 ? 0x100 : 0);
                    for (int dy = 0; dy < o.Height; dy++)
                        for (int dx = 0; dx < o.Width; dx++)
                            g.Set(ax + dx, ay + dy, tile);
                    break;
                }
                case 0x14:                                      // ground ledge ($0DB1D4)
                    GroundLedge(g, ax, ay, b3 & 0x0F, b3 >> 4);
                    break;
                case 0x21:                                      // long ground ledge ($0DB1C8)
                    GroundLedge(g, ax, ay, b3, 2);
                    break;
                case 0x13:                                      // ledge edges ($0DB075)
                    LedgeEdge(g, ax, ay, b3 & 0x0F, b3 >> 4, ledTop, ledMid, ledBot);
                    break;
                case 0x3F:                                      // bushes ($0DB5B7)
                    Bush(g, ax, ay, b3 & 0x0F, b3 >> 4, bushL, bushM, bushR);
                    break;
                case 0x0F:                                      // vertical pipes ($0DAA26)
                    VertPipe(g, ax, ay, b3 & 0x0F, b3 >> 4, pipeTL, pipeTR);
                    break;
                case 0x17:                                      // rope/clouds ($0DB3BD)
                {
                    int t = Math.Min(b3 >> 4, rope.Length - 1);
                    for (int i = 0; i <= (b3 & 0x0F); i++) g.Set(ax + i, ay, rope[t] | 0x100);
                    break;
                }
                case 0x1F:                                      // vertical pipe/bone/log ($0DB51F)
                    VertBoneLog(g, ax, ay, b3 >> 4);
                    break;
                case 0x15:                                      // midway/goal point ($0DB224)
                    MidwayGoal(g, ax, ay, b3 >> 4, (b3 & 0x0F) != 0,
                               mwTop, mwMid, mwBot, glTop, glMid, glBot);
                    break;
                case 0x12:                                      // slopes ($0DAB3E) — approx
                    Slope(g, ax, ay, b3);
                    break;
                case 0x39:                                      // diagonal pipe ($0DB73F) — approx
                    DiagStair(g, ax, ay, b3, ledge: false);
                    break;
                case 0x3A:                                      // diagonal ledge ($0DB7AA) — approx
                    DiagStair(g, ax, ay, b3, ledge: true);
                    break;
                default:                                        // handler not ported yet
                    g.Set(ax, ay, Marker | n);
                    break;
            }
        }
        return g;
    }

    // $0DB1D4: grass top row (tile 0x100) + `height` dirt rows (tile 0x03F), width+1 wide.
    private static void GroundLedge(Map16Grid g, int ax, int ay, int width, int height)
    {
        for (int dx = 0; dx <= width; dx++) g.Set(ax + dx, ay, 0x100);
        for (int row = 1; row <= height; row++)
            for (int dx = 0; dx <= width; dx++) g.Set(ax + dx, ay + row, 0x03F);
    }

    // $0DB075: vertical strip — top + `height` middle tiles + optional bottom cap.
    private static void LedgeEdge(Map16Grid g, int ax, int ay, int type, int height,
                                  int[] top, int[] mid, int[] bot)
    {
        if (type >= top.Length) { g.Set(ax, ay, Marker | 0x13); return; }
        g.Set(ax, ay, top[type] | (type >= 3 ? 0x100 : 0));
        for (int row = 1; row <= height; row++)
            g.Set(ax, ay + row, mid[type] | LedgeMidPage(type));
        if (bot[type] != 0xFF)                                  // types 0x0B-0x0E have a bottom
            g.Set(ax, ay + height + 1, bot[type] | 0x100);
    }

    // page selection for ledge below-top/middle tiles ($0DB0A1 rules)
    private static int LedgeMidPage(int type)
        => type >= 9 ? 0x100 : type >= 7 ? 0 : type >= 3 ? 0x100 : 0;

    // $0DB5B7: horizontal bush — left + (width-1) middle + right tiles (page 0).
    private static void Bush(Map16Grid g, int ax, int ay, int width, int type, int[] l, int[] m, int[] r)
    {
        if (type >= l.Length) type = l.Length - 1;
        g.Set(ax, ay, l[type]);
        for (int i = 1; i < width; i++) g.Set(ax + i, ay, m[type]);
        g.Set(ax + Math.Max(width, 1), ay, r[type]);
    }

    // $0DB51F: 1-wide vertical bone/pipe/log — top $153, middle $154, bottom $155 (page 1).
    private static void VertBoneLog(Map16Grid g, int ax, int ay, int height)
    {
        g.Set(ax, ay, 0x153);
        if (height <= 0) return;
        for (int r = 1; r < height; r++) g.Set(ax, ay + r, 0x154);
        g.Set(ax, ay + height, 0x155);
    }

    // $0DB224: 3-column midway (type 0) or goal (type != 0) post, `height` tall (page 0).
    private static void MidwayGoal(Map16Grid g, int ax, int ay, int height, bool goal,
        int[] mt, int[] mm, int[] mb, int[] gt, int[] gm, int[] gb)
    {
        int[] top = goal ? gt : mt, mid = goal ? gm : mm, bot = goal ? gb : mb;
        for (int c = 0; c < 3; c++)
        {
            g.Set(ax + c, ay, top[c]);
            for (int r = 1; r < height; r++) g.Set(ax + c, ay + r, mid[c]);
            if (height > 0) g.Set(ax + c, ay + height, bot[c]);
        }
    }

    // $0DAB3E slopes — APPROXIMATION (10 real staircase variants; refine after GFX lands).
    // Places a diagonal of a slope-surface tile with 0x03F fill below.
    private static void Slope(Map16Grid g, int ax, int ay, int b3)
    {
        int type = (b3 & 0x0F) % 10, size = (b3 >> 4) + 1;
        bool left = type < 3;
        for (int i = 0; i < size; i++)
        {
            int x = left ? ax + i : ax + size - 1 - i;
            g.Set(x, ay + i, 0x196);
            for (int r = i + 1; r < size; r++) g.Set(x, ay + r, 0x03F);
        }
    }

    // $0DB73F / $0DB7AA diagonal pipe/ledge — APPROXIMATION (staircase; refine after GFX).
    private static void DiagStair(Map16Grid g, int ax, int ay, int b3, bool ledge)
    {
        int size = Math.Max(b3 & 0x0F, b3 >> 4) + 1;
        int surf = ledge ? 0x1AA : 0x135;
        for (int i = 0; i < size; i++)
        {
            g.Set(ax + i, ay + i, surf);
            for (int r = i + 1; r < size; r++) g.Set(ax + i, ay + r, 0x03F);
        }
    }

    // $0DAA26: 2-wide vertical pipe — top cap (types 0-2) + body tiles $135/$136 down `height`.
    private static void VertPipe(Map16Grid g, int ax, int ay, int type, int height, int[] tl, int[] tr)
    {
        if (type < 3 && type < tl.Length)
        {
            g.Set(ax, ay, tl[type] | 0x100);
            g.Set(ax + 1, ay, tr[type] | 0x100);
        }
        else { g.Set(ax, ay, 0x135); g.Set(ax + 1, ay, 0x136); }
        for (int row = 1; row <= height; row++)
        {
            g.Set(ax, ay + row, 0x135);
            g.Set(ax + 1, ay + row, 0x136);
        }
    }

    private static int[] ReadTable(Rom rom, int snes, int n)
    {
        var t = new int[n];
        int fo = rom.FileOffset(snes);
        for (int i = 0; i < n; i++) t[i] = rom.Data[fo + i];
        return t;
    }
}
