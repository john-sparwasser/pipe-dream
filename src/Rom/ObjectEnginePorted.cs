namespace PipeDream;

// Hand-ported object handlers (bank 0D). Fallback path used only when the emulated
// engine bails (LM-patched loaders); see ObjectEngine.Render. Partial of ObjectEngine.
public static partial class ObjectEngine
{
    public static Map16Grid RenderPorted(Rom rom, LevelHeader header, IReadOnlyList<LevelObject> objects)
    {
        int w = Math.Max(16, header.Screens * 16);
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
        int[] hpEnd = ReadTable(rom, 0x0DAAA4, 8);     // horizontal pipe end tiles (type*2+row)
        int[] hpMid = ReadTable(rom, 0x0DAAAC, 8);     // horizontal pipe middle tiles
        int[] netV = ReadTable(rom, 0x0DB49C, 2);      // net vertical edge tiles (by type)
        int[] netVT = ReadTable(rom, 0x0DB4D5, 2);     // ... when joining a top edge (tile 0x08)
        int[] netVB = ReadTable(rom, 0x0DB4D7, 2);     // ... when joining a bottom edge (tile 0x0E)
        int[] swTile = ReadTable(rom, 0x0DB91A, 2);    // switch block tiles (blue, red)
        int[] trunkT = ReadTable(rom, 0x0DB962, 2);    // small trunk top tiles (by type)
        int[] trunkB = ReadTable(rom, 0x0DB964, 2);    // small trunk lower tiles
        int[] fedgeT = ReadTable(rom, 0x0DBA44, 4);    // forest edge top tiles (by type)
        int[] fedgeB = ReadTable(rom, 0x0DBA48, 4);    // forest edge body tiles
        int[] treeTop = ReadTable(rom, 0x0DBA7C, 96);  // forest tree top 16x6 stamp
        int[] plantL = ReadTable(rom, 0x0DD1CB, 4);    // plant-on-column left tiles (by type)
        int[] plantR = ReadTable(rom, 0x0DD1CF, 4);    // plant-on-column right tiles
        int[] colTile = ReadTable(rom, 0x0DD1D3, 6);   // column body tiles (cycled pairs)
        int[] brTiles = ReadTable(rom, 0x0DB42B, 2);   // obj 0x1C top/bottom tiles
        int[] mwTop = ReadTable(rom, 0x0DB212, 3), mwMid = ReadTable(rom, 0x0DB215, 3), mwBot = ReadTable(rom, 0x0DB218, 3);
        int[] glTop = ReadTable(rom, 0x0DB21B, 3), glMid = ReadTable(rom, 0x0DB21E, 3), glBot = ReadTable(rom, 0x0DB221, 3);

        foreach (var o in objects)
        {
            if (o.IsScreenExit) continue;                       // no tiles
            int ax = o.AbsoluteX, ay = o.Y;

            if (o.IsDm16)                                       // LM Direct Map16: w×h of one tile
            {
                if (o.Number == 0x29) continue;                 // BG-page form: layer 2, no L1 tiles
                // Extended form (page bit7): 7-bit width from the size byte; bits 7+6 add a
                // height-override byte. ponytail: ExtX run semantics approximated as plain w×h.
                int dw = o.Dm16ExtX >= 0 ? (o.Byte3 & 0x7F) + 1 : o.Width;
                int dh = o.Dm16ExtH >= 0 ? o.Dm16ExtH + 1 : o.Height;
                for (int dy = 0; dy < dh; dy++)
                    for (int dx = 0; dx < dw; dx++)
                        g.Set(ax + dx, ay + dy, o.Dm16Tile);
                continue;
            }

            if (o.Extended)
            {
                int ext = o.ExtendedNumber;
                if (ext == 0x01) continue;                      // screen jump: no tiles
                // Dispatch by handler address from the global ext-object table ($0DA10F).
                switch (rom.ReadValue(0x0DA10F + ext * 3, 3))
                {
                    case 0x0DA57B or 0x0DA64D:                  // single tile via DATA_0DA548
                    {
                        int idx = ext - 0x10;
                        if (idx >= 0 && idx < single.Length)
                            g.Set(ax, ay, single[idx] | (idx >= 0x13 ? 0x100 : 0)); // $0DA5B1 page rule
                        else
                            g.Set(ax, ay, Marker | ext);
                        break;
                    }
                    case 0x0DA68E:                              // midway point bar
                        g.Set(ax - 1, ay, 0x035); g.Set(ax, ay, 0x038);
                        break;
                    case 0x0DCE94:                              // rope line-guide tiles (0x51-0x54)
                        g.Set(ax, ay, ReadTable(rom, 0x0DCE90, 4)[(ext - 0x51) & 3]);
                        break;
                    case 0x0DCEA6:                              // canvas 1: vertical pair 84/85
                        g.Set(ax, ay, 0x084); g.Set(ax, ay + 1, 0x085);
                        break;
                    case 0x0DCEC0:                              // line-guide end: pair 96/97
                        g.Set(ax, ay, 0x096); g.Set(ax, ay + 1, 0x097);
                        break;
                    case 0x0DDA68:                              // underground deco (0x75-0x7B)
                        g.Set(ax, ay, ReadTable(rom, 0x0DDA61, 7)[(ext - 0x75) % 7]);
                        break;
                    case 0x0DDA80:                              // canvas 2-4: vertical pairs
                        g.Set(ax, ay, ReadTable(rom, 0x0DDA7A, 3)[(ext - 0x7C) % 3]);
                        g.Set(ax, ay + 1, ReadTable(rom, 0x0DDA7D, 3)[(ext - 0x7C) % 3]);
                        break;
                    case 0x0DB583:                              // yellow switch block (outline)
                        g.Set(ax, ay, ReadTable(rom, 0x0DB589, 2)[1]);
                        break;
                    case 0x0DB58B:                              // green switch block (outline)
                        g.Set(ax, ay, ReadTable(rom, 0x0DB589, 2)[0]);
                        break;
                    case 0x0DB2CA:                              // Yoshi coin (top + bottom)
                        g.Set(ax, ay, 0x02D); g.Set(ax, ay + 1, 0x02E);
                        break;
                    case 0x0DA71B:                              // big bush stamp (9x5)
                        BushStamp(g, ax, ay, 9, 5, ReadTable(rom, 0x0DA6EE, 45));
                        break;
                    case 0x0DA760:                              // small bush stamp (6x4)
                        BushStamp(g, ax, ay, 6, 4, ReadTable(rom, 0x0DA748, 24));
                        break;
                    case 0x0DA7E7:                              // 2x2 block (DATA_0DA7E3)
                    {
                        var t4 = ReadTable(rom, 0x0DA7E3, 4);
                        g.Set(ax, ay, t4[0]); g.Set(ax + 1, ay, t4[1]);
                        g.Set(ax, ay + 1, t4[2]); g.Set(ax + 1, ay + 1, t4[3]);
                        break;
                    }
                    case 0x0DA673:                              // purple triangle L/R: top + 1EB below
                        g.Set(ax, ay, 0x100 | ReadTable(rom, 0x0DA671, 2)[(ext - 0x44) & 1]);
                        g.Set(ax, ay + 1, 0x1EB);
                        break;
                    case 0x0DDA57:                              // lava/mud top-right corner: tile 1FE
                        g.Set(ax, ay, 0x1FE);
                        break;
                    case 0x0DE1B0:                              // LM secondary exit: no tiles
                    case 0x0DE1E0:                              // LM screen jump (0x03): no tiles
                        break;
                    default:
                        g.Set(ax, ay, Marker | ext);
                        break;
                }
                continue;
            }

            int n = o.Number, b3 = o.Byte3;
            // Tileset-aware dispatch, exactly like the ROM: tileset dispatcher pointer at
            // $0DA41E + tileset*3, per-object handler table at dispatcher+0xA, entry (n-1)*3.
            // Handlers are shared across tilesets (theming comes from the Map16 defs), so we
            // key the port on the handler address.
            switch (Handler(rom, header.Tileset, n))
            {
                case 0x0DA8C3:                                  // rectangle family
                {
                    int i = n - 1, tile = rect[i] | (i >= 7 ? 0x100 : 0);
                    for (int dy = 0; dy < o.Height; dy++)
                        for (int dx = 0; dx < o.Width; dx++)
                            g.Set(ax + dx, ay + dy, tile);
                    break;
                }
                case 0x0DB1D4:                                  // ground ledge
                    GroundLedge(g, ax, ay, b3 & 0x0F, b3 >> 4);
                    break;
                case 0x0DB1C8:                                  // long ground ledge (obj 0x21)
                    GroundLedge(g, ax, ay, b3, 2);
                    break;
                case 0x0DB075:                                  // ledge edges
                    LedgeEdge(g, ax, ay, b3 & 0x0F, b3 >> 4, ledTop, ledMid, ledBot);
                    break;
                case 0x0DB5B7:                                  // bushes
                    Bush(g, ax, ay, b3 & 0x0F, b3 >> 4, bushL, bushM, bushR);
                    break;
                case 0x0DAA26:                                  // vertical pipes
                    VertPipe(g, ax, ay, b3 & 0x0F, b3 >> 4, pipeTL, pipeTR);
                    break;
                case 0x0DB3BD:                                  // rope/clouds
                {
                    int t = Math.Min(b3 >> 4, rope.Length - 1);
                    for (int i = 0; i <= (b3 & 0x0F); i++) g.Set(ax + i, ay, rope[t] | 0x100);
                    break;
                }
                case 0x0DB51F:                                  // vertical pipe/bone/log
                    VertBoneLog(g, ax, ay, b3 >> 4);
                    break;
                case 0x0DB224:                                  // midway/goal point
                    MidwayGoal(g, ax, ay, b3 >> 4, (b3 & 0x0F) != 0,
                               mwTop, mwMid, mwBot, glTop, glMid, glBot);
                    break;
                case 0x0DAB3E:                                  // slopes — approx
                    Slope(g, ax, ay, b3);
                    break;
                case 0x0DB73F:                                  // diagonal pipe — approx
                    DiagStair(g, ax, ay, b3, ledge: false);
                    break;
                case 0x0DB7AA:                                  // diagonal ledge — approx
                    DiagStair(g, ax, ay, b3, ledge: true);
                    break;
                case 0x0DAAB4:                                  // horizontal pipes
                    HorizPipe(g, ax, ay, b3 & 0x0F, b3 >> 4, hpEnd, hpMid);
                    break;
                case 0x0DAB0D:                                  // bullet shooter
                    Column(g, ax, ay, b3 >> 4, 0x141, 0x142, 0x143, repeatBottom: true);
                    break;
                case 0x0DB336:                                  // invisible coin blocks (item memory ignored)
                    Fill(g, ax, ay, (b3 & 0x0F) + 1, (b3 >> 4) + 1, 0x02C);
                    break;
                case 0x0DB42D:                                  // 2-row strip: top page-0 / bottom page-1
                    Fill(g, ax, ay, (b3 & 0x0F) + 1, 1, brTiles[0]);
                    Fill(g, ax, ay + 1, (b3 & 0x0F) + 1, 1, brTiles[1] | 0x100);
                    break;
                case 0x0DB461:                                  // net rectangle: body rows + bottom row
                    Fill(g, ax, ay, (b3 & 0x0F) + 1, b3 >> 4, 0x00B);
                    Fill(g, ax, ay + (b3 >> 4), (b3 & 0x0F) + 1, 1, 0x00E);
                    break;
                case 0x0DB49E:                                  // net vertical edge (context-adjusted ends)
                    NetVertEdge(g, ax, ay, b3 & 0x01, b3 >> 4, netV, netVT, netVB);
                    break;
                case 0x0DB547:                                  // horizontal rope
                    HorizStrip(g, ax, ay, b3 & 0x0F, 0x156, 0x157, 0x158);
                    break;
                case 0x0DB916:                                  // blue switch blocks
                    Fill(g, ax, ay, (b3 & 0x0F) + 1, (b3 >> 4) + 1, swTile[0]);
                    break;
                case 0x0DB91E:                                  // red switch blocks
                    Fill(g, ax, ay, (b3 & 0x0F) + 1, (b3 >> 4) + 1, swTile[1]);
                    break;
                case 0x0DB966:                                  // small tree trunk (alternating 16x32)
                    for (int i = 0; i <= (b3 >> 4); i++)
                        g.Set(ax, ay + i, (i % 2 == 0 ? trunkT : trunkB)[b3 & 0x01]);
                    break;
                case 0x0DB9C0:                                  // large tree trunk (2 wide, row pairs)
                    LargeTrunk(g, ax, ay, b3 >> 4);
                    break;
                case 0x0DBA0A:                                  // forest ledge: top row 0x10E + dirt
                    Fill(g, ax, ay, (b3 & 0x0F) + 1, 1, 0x10E);
                    Fill(g, ax, ay + 1, (b3 & 0x0F) + 1, b3 >> 4, 0x0B8);
                    break;
                case 0x0DBA4C:                                  // forest left/right/top edge
                {
                    int t = (b3 & 0x0F) < 4 ? b3 & 0x0F : 0;
                    g.Set(ax, ay, fedgeT[t] | 0x100);
                    for (int i = 1; i <= (b3 >> 4) + 1; i++)
                        g.Set(ax, ay + i, fedgeB[t] | (t < 2 ? 0x100 : 0));
                    break;
                }
                case 0x0DBADC:                                  // forest tree top: 16x6 stamp per screen
                    for (int r = 0; r <= b3; r++)
                        for (int i = 0; i < 96; i++)
                            g.Set(ax + r * 16 + i % 16, ay + i / 16, treeTop[i]);
                    break;
                case 0x0DBB2C:                                  // ice-blue vertical pipe (2 wide)
                    for (int i = 0; i <= (b3 >> 4); i++)
                    {
                        g.Set(ax, ay + i, (i == 0 ? 0x161 : 0x163));
                        g.Set(ax + 1, ay + i, (i == 0 ? 0x162 : 0x164));
                    }
                    break;
                case 0x0DBB63:                                  // ice-blue turn tiles = rect family idx 0x0E
                    Fill(g, ax, ay, o.Width, o.Height, rect[0x0E] | 0x100);
                    break;
                case 0x0DD1D9:                                  // plants on columns
                    PlantColumn(g, ax, ay, b3 & 0x03, b3 >> 4, plantL, plantR, colTile);
                    break;
                case 0x0DD1A5:                                  // vertical log
                    Column(g, ax, ay, b3 >> 4, 0x15C, 0x15D, 0x15E, repeatBottom: false);
                    break;
                case 0x0DDAC8:                                  // mud/lava column: top + repeated mid
                {
                    int t = b3 & 0x01;
                    g.Set(ax, ay, ReadTable(rom, 0x0DDAC4, 2)[t] | 0x100);
                    for (int i = 1; i <= (b3 >> 4) + 1; i++)
                        g.Set(ax, ay + i, ReadTable(rom, 0x0DDAC6, 2)[t] | 0x100);
                    break;
                }
                case 0x0DE135:                                  // framed box: L/M/R per row kind
                {
                    int bw = (b3 & 0x0F) + 1, bh = b3 >> 4;
                    int[] lt = ReadTable(rom, 0x0DE12C, 3), md = ReadTable(rom, 0x0DE12F, 3), rt = ReadTable(rom, 0x0DE132, 3);
                    for (int r = 0; r <= bh; r++)
                    {
                        int k = r == 0 ? 0 : r == bh ? 2 : 1;
                        for (int i = 0; i < bw; i++)
                        {
                            int tile = i == 0 ? lt[k] : i == bw - 1 ? md[k] : md[k];
                            g.Set(ax + i, ay + r, (i == bw - 1 && bw > 1 ? rt[k] : tile) | 0x100);
                        }
                        if (bw == 1) g.Set(ax, ay + r, rt[k] | 0x100);
                    }
                    break;
                }
                case 0x0DDCEA:                                  // upside-down ledge: dirt + 14E bottom
                {
                    int uw = (b3 & 0x0F) + 1, uh = b3 >> 4;
                    Fill(g, ax, ay, uw, Math.Max(0, uh - 1), 0x165);
                    Fill(g, ax, ay + Math.Max(0, uh - 1), uw, 1, 0x14E);
                    break;
                }
                case 0x0DDD2E:                                  // solid edge column: uppers + bottom
                {
                    int et = b3 & 0x03, eh = b3 >> 4;
                    for (int i = 0; i < eh; i++)
                        g.Set(ax, ay + i, ReadTable(rom, 0x0DDD26, 4)[et] | 0x100);
                    g.Set(ax, ay + eh, ReadTable(rom, 0x0DDD2A, 4)[et] | 0x100);
                    break;
                }
                case 0x0DDD5C:                                  // solid dirt: rect of 0x165
                    Fill(g, ax, ay, (b3 & 0x0F) + 1, (b3 >> 4) + 1, 0x165);
                    break;
                case 0x0DDAF2:                                  // mud/lava slopes — approx like Slope
                {
                    // ponytail: geometry approximated (surface + fill); refine against LM.
                    int rows = (b3 >> 4) + 1, sub = b3 & 0x03;
                    bool rightS = sub >= 2, steep = (sub & 1) != 0;
                    int step = steep ? 1 : 2;
                    for (int r = 0; r < rows; r++)
                    {
                        int y = ay + r, xs = ax + (rightS ? r * step : -r * step);
                        if (steep)
                        {
                            g.Set(xs, y, rightS ? 0x1D7 : 0x1D6);
                            g.Set(xs, y + 1, rightS ? 0x1FE : 0x1FD);
                        }
                        else
                        {
                            g.Set(xs, y, rightS ? 0x1D4 : 0x1D2);
                            g.Set(xs + 1, y, rightS ? 0x1D5 : 0x1D3);
                        }
                        // fill toward the mound side
                        if (rightS) for (int x = ax; x < xs; x++) g.Set(x, y, 0x1FF);
                        else for (int x = xs + 2; x <= ax + 1; x++) g.Set(x, y, 0x1FF);
                    }
                    break;
                }
                case 0x0DDCA9:                                  // mud/lava rect (0x3A top row / 0x3B plain)
                {
                    int mw = (b3 & 0x0F) + 1, mh = b3 >> 4;
                    if (n == 0x3A) { Fill(g, ax, ay, mw, 1, 0x159); Fill(g, ax, ay + 1, mw, mh, 0x1FF); }
                    else Fill(g, ax, ay, mw, mh + 1, 0x1FF);
                    break;
                }
                case 0x0DDD87:                                  // very steep slopes — approx like Slope
                {
                    // ponytail: vertical-stripe geometry approximated; refine against LM.
                    int sw = b3 & 0x0F;
                    bool right = ((b3 >> 4) & 1) != 0;
                    int top = right ? 0x1CC : 0x1CA, mid = right ? 0x1CD : 0x1CB;
                    for (int i = 0; i <= sw; i++)
                    {
                        int x = ax + i, ty = ay + 2 * (right ? i : sw - i);
                        g.Set(x, ty, top);
                        g.Set(x, ty + 1, mid);
                        for (int yy = ty + 2; yy <= ay + 2 * sw + 1; yy++) g.Set(x, yy, 0x1FF);
                    }
                    break;
                }
                case 0x0DD24E:                                  // log bridge: log row + page-1 0E row
                {
                    int[] lb = ReadTable(rom, 0x0DD24C, 2);
                    Fill(g, ax, ay, (b3 & 0x0F) + 1, 1, lb[0]);
                    Fill(g, ax, ay + 1, (b3 & 0x0F) + 1, 1, lb[1] | 0x100);
                    break;
                }
                case 0x0DB3E3:                                  // vanilla placeholder: no tiles
                case 0x0DF130:                                  // LM obj 0x26 directive (no tiles)
                case 0x0DF160:                                  // LM obj 0x28 directive (no tiles)
                    break;
                default:                                        // handler not ported yet
                    g.Set(ax, ay, Marker | n);
                    break;
            }
        }
        return g;
    }

    // edge tiles (0x49-0x53) morph when overlapping an existing bush: +1 over body (0x49),
    // +2 over another edge.
    private static void BushStamp(Map16Grid g, int ax, int ay, int w, int h, int[] stamp)
    {
        for (int i = 0; i < w * h; i++)
        {
            int t = stamp[i];
            if (t == 0x25) continue;
            int x = ax + i % w, y = ay + i / w;
            if (t is >= 0x49 and < 0x54)
            {
                int under = g.Get(x, y);
                if (under == 0x49) t += 1;
                else if (under != 0x25 && under != Map16Grid.Empty) t += 2;
            }
            g.Set(x, y, t);
        }
    }

    // Rectangle fill helper (used by several ported handlers).
    private static void Fill(Map16Grid g, int ax, int ay, int w, int h, int tile)
    {
        for (int dy = 0; dy < h; dy++)
            for (int dx = 0; dx < w; dx++)
                g.Set(ax + dx, ay + dy, tile);
    }

    // $0DAAB4: horizontal pipes — 2 rows; types 0/1 have the end cap on the left, 2/3 on the
    // right; tables indexed type*2 + row. All tiles page 1.
    private static void HorizPipe(Map16Grid g, int ax, int ay, int w0, int type, int[] end, int[] mid)
    {
        for (int row = 0; row < 2; row++)
        {
            int x = ((type & 3) * 2 + row) & 7;
            for (int i = 0; i <= w0; i++)
            {
                bool isEnd = type < 2 ? i == 0 : i == w0;
                g.Set(ax + i, ay + row, (isEnd ? end[x] : mid[x]) | 0x100);
            }
        }
    }

    // Vertical strip: top + middles + bottom (repeatBottom: bottom tile repeats to the end,
    // like the bullet shooter; else a single bottom cap, like the vertical log). All page 1.
    private static void Column(Map16Grid g, int ax, int ay, int h, int top, int midT, int bot, bool repeatBottom)
    {
        for (int i = 0; i <= h; i++)
        {
            int t = i == 0 ? top : repeatBottom ? (i == 1 ? midT : bot) : i == h ? bot : midT;
            g.Set(ax, ay + i, t);
        }
    }

    // $0DB547-style 3-part horizontal strip (left/mid/right), page 1 tiles passed complete.
    private static void HorizStrip(Map16Grid g, int ax, int ay, int w0, int left, int mid, int right)
    {
        for (int i = 0; i <= w0; i++)
            g.Set(ax + i, ay, i == 0 ? left : i == w0 ? right : mid);
    }

    // $0DB49E: net vertical edge — a column of net[type]; the end tiles morph when they land
    // on a net top edge (tile 0x08) or bottom edge (0x0E). Page 0.
    private static void NetVertEdge(Map16Grid g, int ax, int ay, int type, int h,
                                    int[] baseT, int[] joinTop, int[] joinBot)
    {
        int rows = Math.Max(h, 1);
        for (int i = 0; i <= rows; i++)
        {
            int t = baseT[type];
            if (i == 0 || i == rows)
            {
                int under = g.Get(ax, ay + i);
                if (under == 0x008) t = joinTop[type];
                else if (under == 0x00E) t = joinBot[type];
            }
            g.Set(ax, ay + i, t);
        }
    }

    // $0DB9C0: large tree trunk — 2-wide, alternating row pairs (0B9,0BA)/(0BB,0BC); the
    // (0B9,0BA) row becomes page-1 (10B,10C) when placed onto a forest ledge top (0x10E).
    private static void LargeTrunk(Map16Grid g, int ax, int ay, int h)
    {
        int y = ay, left = h;
        while (true)
        {
            bool onLedge = g.Get(ax, y) == 0x10E;
            g.Set(ax, y, onLedge ? 0x10B : 0x0B9);
            g.Set(ax + 1, y, onLedge ? 0x10C : 0x0BA);
            y++;
            if (--left < 0) break;
            g.Set(ax, y, 0x0BB);
            g.Set(ax + 1, y, 0x0BC);
            y++;
            if (--left < 0) break;
        }
    }

    // $0DD1D9: plants on columns — plant pair, then column top (15F,160), then body pairs
    // cycling (161,162)/(163,164)/(165,166).
    private static void PlantColumn(Map16Grid g, int ax, int ay, int type, int h,
                                    int[] plantL, int[] plantR, int[] col)
    {
        g.Set(ax, ay, plantL[type]);
        g.Set(ax + 1, ay, plantR[type]);
        if (h == 0) return;
        g.Set(ax, ay + 1, 0x15F);
        g.Set(ax + 1, ay + 1, 0x160);
        int k = 0;
        for (int i = 2; i <= h; i++)
        {
            g.Set(ax, ay + i, col[k] | 0x100);
            g.Set(ax + 1, ay + i, col[k + 1] | 0x100);
            k = (k + 2) % 6;
        }
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
