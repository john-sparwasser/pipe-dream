namespace PipeDream;

/// <summary>
/// Real sprite graphics: run each sprite's own init + one main frame ($018172/$0185C3)
/// in the CPU interpreter and capture the OAM entries its GFX routine writes, then draw
/// those tiles from the SP GFX slots. Sprites whose routines fail fall back to badges.
/// </summary>
public static class SpriteRender
{
    public readonly record struct Oam(int X, int Y, int Tile, int Attr, bool Big);

    public static List<Oam>? Capture(Rom rom, Sprite s, int cellX = -1, int cellY = -1)
    {
        try
        {
            var cpu = new Cpu65816(rom);
            var r = cpu.Ram7E;
            for (int i = 0; i < 0x200; i += 4) r[0x201 + i] = 0xF0;   // OAM Y offscreen
            // OAM_TileSize default 8x8: the draw finisher ($01B7F0) writes $02 for the 16x16
            // tiles a sprite actually draws; unwritten entries must stay a single 8x8 tile,
            // else a large default draws 3 garbage neighbour tiles.

            int wx = (cellX >= 0 ? cellX : s.AbsoluteX) * 16, wy = (cellY >= 0 ? cellY : s.Y) * 16;
            int bx = Math.Max(0, wx - 0x40), by = Math.Max(0, wy - 0x40);
            r[0x1A] = (byte)bx; r[0x1B] = (byte)(bx >> 8);            // screen boundary X
            r[0x1C] = (byte)by; r[0x1D] = (byte)(by >> 8);
            r[0x94] = (byte)(bx + 0x180); r[0x95] = (byte)((bx + 0x180) >> 8);  // Mario far right
            r[0x96] = (byte)by; r[0x97] = (byte)(by >> 8);
            r[0x9E] = (byte)s.Number;                                  // slot 0
            r[0xE4] = (byte)wx; r[0x14E0] = (byte)(wx >> 8);
            r[0xD8] = (byte)wy; r[0x14D4] = (byte)(wy >> 8);
            r[0x15EA] = 0x30;                                          // OAM index for slot 0
            r[0x64] = 0x30;                                            // priority
            r[0x187B] = (byte)s.Extra;                                 // LM extra bits
            r[0x190F] = 0; r[0x9D] = 0;                                // sprites not locked

            cpu.PresetX(0); cpu.CallNear(0x018172, 400_000);           // init
            cpu.PresetX(0); cpu.CallNear(0x0185C3, 400_000);           // main (draws OAM)

            var list = new List<Oam>();
            for (int i = 0; i < 0x80; i++)
            {
                int y = r[0x201 + i * 4];
                if (y >= 0xE0) continue;
                // Sprites write per-entry size/X-high to OAM_TileSize ($0460); the packed
                // $0420 table is only built at frame end, which runs outside our capture.
                int sz = r[0x460 + i];
                int x = r[0x200 + i * 4] | ((sz & 1) << 8);
                int tile = r[0x202 + i * 4], attr = r[0x203 + i * 4];
                list.Add(new Oam(x + bx, y + by, tile | ((attr & 1) << 8), attr, (sz & 2) != 0));
            }
            return list.Count is > 0 and < 40 ? list : null;
        }
        catch { return null; }
    }

    /// <summary>Decoded SP-slot 8x8 tiles (512) for a sprite GFX set, honoring the bypass.</summary>
    public static byte[][] LoadSpTiles(Rom rom, LevelHeader h, int level)
    {
        var tiles = new byte[0x200][];
        var byp = level >= 0 ? rom.LmGfxBypass(level) : null;
        int[] w = { 11, 10, 9, 8 };                                    // SP1..SP4 record words
        for (int slot = 0; slot < 4; slot++)
        {
            int file = rom.ReadByte(0x00A8C3 + h.SpriteSet * 4 + slot);   // SPRITEGFXLIST
            if (byp is not null && (byp[w[slot]] & 0xFFF) != 0x7F) file = byp[w[slot]] & 0xFFF;
            int src = Gfx.SourceSnes(rom, file);
            if (src < 0) continue;
            byte[] data;
            try { data = Gfx.Lz2Decompress(rom.Data, rom.FileOffset(src)); } catch { continue; }
            int bpp = data.Length >= 0x1000 ? 4 : 3, tb = Gfx.TileBytes(bpp);
            for (int t = 0; t < 0x80 && t * tb + tb <= data.Length; t++)
                tiles[slot * 0x80 + t] = Gfx.DecodeTile(data, t * tb, bpp);
        }
        return tiles;
    }

    /// <summary>Draw one captured OAM list onto the canvas.</summary>
    public static void Draw(uint[] img, int W, int H, List<Oam> oam, byte[][] sp, Palette pal)
    {
        // Later OAM entries draw behind earlier ones in SNES priority: draw in reverse.
        for (int n = oam.Count - 1; n >= 0; n--)
        {
            var e = oam[n];
            int cells = e.Big ? 2 : 1;
            for (int ty = 0; ty < cells; ty++)
                for (int tx = 0; tx < cells; tx++)
                {
                    // 16x16 sprites use tiles T, T+1, T+16, T+17 with flips swapping quadrants.
                    int qx = (e.Attr & 0x40) != 0 ? cells - 1 - tx : tx;
                    int qy = (e.Attr & 0x80) != 0 ? cells - 1 - ty : ty;
                    var px = sp[(e.Tile + qx + qy * 16) & 0x1FF];
                    if (px is null) continue;
                    int baseColor = 0x80 + ((e.Attr >> 1) & 7) * 16;
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                        {
                            int sx = (e.Attr & 0x40) != 0 ? 7 - x : x;
                            int sy = (e.Attr & 0x80) != 0 ? 7 - y : y;
                            int ci = px[sy * 8 + sx];
                            if (ci == 0) continue;
                            int dx = e.X + tx * 8 + x, dy = e.Y + ty * 8 + y;
                            if ((uint)dx < (uint)W && (uint)dy < (uint)H)
                                img[dy * W + dx] = pal.Rgba[baseColor + ci];
                        }
                }
        }
    }
}
