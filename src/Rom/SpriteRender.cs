namespace PipeDream;

/// <summary>
/// Real sprite graphics: run each sprite's own init + one main frame ($018172/$0185C3)
/// in the CPU interpreter and capture the OAM entries its GFX routine writes, then draw
/// those tiles from the SP GFX slots. Sprites whose routines fail fall back to badges.
/// </summary>
public static class SpriteRender
{
    public readonly record struct Oam(int X, int Y, int Tile, int Attr, bool Big);

    public static List<Oam>? Capture(Rom rom, Sprite s, int cellX = -1, int cellY = -1,
                                     bool vertical = false)
    {
        // Sprite-list number classes ($02A866-$02A8D8): C9-CA shooters, CB-D9 generators,
        // DE/E0-E6 multi-sprite specials, E7+ scroll commands — none are in the 00-C8
        // dispatch tables. DA-DD/DF are koopa shells: loaded as sprite (num-$DA)+4 with
        // initial status 9 (stationary) per $02A97E.
        int num = s.Number;
        if (num is (>= 0xDA and <= 0xDD) or 0xDF) num = num - 0xDA + 4;
        else if (num >= 0xC9) return null;
        try
        {
            var cpu = new Cpu65816(rom);
            var r = cpu.Ram7E;

            int wx = (cellX >= 0 ? cellX : s.AbsoluteX) * 16, wy = (cellY >= 0 ? cellY : s.Y) * 16;
            int bx = Math.Max(0, wx - 0x40), by = Math.Max(0, wy - 0x40);
            r[0x1A] = (byte)bx; r[0x1B] = (byte)(bx >> 8);            // screen boundary X
            r[0x1C] = (byte)by; r[0x1D] = (byte)(by >> 8);
            // Mario far LEFT of the sprite: on the side sprites expect an approaching
            // player (Banzai Bill's init erases itself when Mario is to its right,
            // $01838B) and too far for any contact. Exactly -0x140 because proximity
            // gates like Monty Mole's ($01E2E3) use only the LOW byte of the distance:
            // -0x140 aliases to -0x40, "near" enough to activate, while the true 16-bit
            // position stays far away.
            int mx = Math.Max(0, wx - 0x140);
            r[0x94] = (byte)mx; r[0x95] = (byte)(mx >> 8);
            r[0x96] = (byte)by; r[0x97] = (byte)(by >> 8);
            // SubHorizPos/SubVertPos ($01AD30/$01AD42) read Mario from the $D1-$D4
            // mirrors, not $94-$97 — unseeded they put Mario at (0,0), which makes
            // proximity gates (e.g. Monty Mole state 0) fail by X-low-byte accident.
            r[0xD1] = (byte)mx; r[0xD2] = (byte)(mx >> 8);
            r[0xD3] = (byte)by; r[0xD4] = (byte)(by >> 8);
            r[0x9E] = (byte)num;                                       // slot 0
            r[0xE4] = (byte)wx; r[0x14E0] = (byte)(wx >> 8);
            r[0xD8] = (byte)wy; r[0x14D4] = (byte)(wy >> 8);
            r[0x15EA] = 0x30;                                          // OAM index for slot 0
            r[0x64] = 0x30;                                            // priority
            r[0x187B] = (byte)s.Extra;                                 // LM extra bits
            r[0x190F] = 0; r[0x9D] = 0;                                // sprites not locked

            // Solid ground (Map16 $130) a few rows below the sprite, level-wide, plus
            // $5D screens-in-level — the block probe ($019441) treats any position whose
            // screen number >= $5D as "no blocks", so unseeded NOTHING is ever solid:
            // walkers then run their in-air path (2px/frame gravity sag, stay-on-ledge
            // direction flip, walk animation frozen at the standing pose). $C800 layout
            // verified against the probe's pointer tables DATA_00BA60/BA9C (horizontal:
            // screen*$1B0, rows 16+ at +$100) and BA80/BABC (vertical: band*$200, right
            // half at +$100).
            r[0x5D] = 0x20;
            int cy2 = wy / 16;
            for (int gy = cy2 + 1; gy <= cy2 + 4; gy++)
            {
                if (!vertical && gy > 26) break;
                for (int gx = 0; gx < (vertical ? 32 : 512); gx++)
                {
                    int a = vertical
                        ? 0xC800 + (gy >> 4) * 0x200 + (gy & 15) * 16 + (gx & 15) + (gx >= 16 ? 0x100 : 0)
                        : 0xC800 + (gx >> 4) * 0x1B0 + (gy < 16 ? gy * 16 : 0x100 + (gy - 16) * 16) + (gx & 15);
                    if (a >= 0xC800 && a < 0x10000) { r[a] = 0x30; cpu.Ram7F[a] = 0x01; }
                }
            }

            cpu.PresetX(0); cpu.CallLong(0x07F7D2, 400_000);           // InitSpriteTables: tweaker + palette RAM
            r[0x14C8] = (byte)(s.Number >= 0xDA ? 9 : 1);              // status: shells stationary, else init
            cpu.PresetDbr(1);                                          // sprite engine runs with DBR=1

            // Run frames through HandleSprite ($018127): it dispatches on status $14C8 —
            // 1 → CallSpriteInit, 8 → CallSpriteMain, 9/A/B → the stunned/kicked/carried
            // handlers that draw carryables (POW, springboard, shells). $0180D2 first each
            // frame: it assigns the real OAM index and decrements the sprite timers (turn
            // animation $15AC etc. — without it, poses freeze on transitional frames).
            // Keep the LAST frame that drew tiles: first-frame poses are often transient
            // (stay-on-ledge walkers flip direction on frame 1 because the on-ground flag
            // is stale, $018B98 — the 8-frame turn image only settles after that). Facing
            // is pinned left each frame ($157C=1) for LM's editor-pose convention.
            //
            // OAM buffer $0200-$03FF (X,Y,tile,props per 4 bytes; sprites use the $0300 half).
            // Size table $0420-$049F: ONE byte per tile — FinishOAMWriteRt ($01B7BB) LSRs the
            // $0300-relative byte offset twice and indexes $0460, i.e. $0420 + entry index.
            // bit1 = 16x16; bit0 = 9th X bit, set when the tile hangs off the LEFT edge.
            // 11 frames: walkers alternate walk poses every 8 frames after landing
            // (frames 3-10 = the mid-stride image) — the last frame lands on the
            // stride, matching LM's walking editor pose.
            List<Oam>? last = null;
            for (int frame = 0; frame < 11; frame++)
            {
                for (int i = 0; i < 0x200; i += 4) r[0x201 + i] = 0xF0;   // OAM Y offscreen
                r[0x13]++; r[0x14]++;                                      // frame counters
                r[0xE4] = (byte)wx; r[0x14E0] = (byte)(wx >> 8);
                r[0xD8] = (byte)wy; r[0x14D4] = (byte)(wy >> 8);
                r[0x157C] = 1;                                             // face left
                // A frame stuck in a wait loop (e.g. fireball init spinning on state we
                // don't emulate) overruns the budget; later frames still draw fine.
                try { cpu.PresetX(0); cpu.CallNear(0x0180D2, 400_000); }
                catch (InvalidOperationException) { }
                try { cpu.PresetX(0); cpu.CallNear(0x018127, 400_000); }
                catch (InvalidOperationException) { }
                var list = new List<Oam>();
                for (int i = 0; i < 0x80; i++)
                {
                    int y = r[0x201 + i * 4];
                    if (y >= 0xE0) continue;
                    int sz = r[0x420 + i];
                    int x = r[0x200 + i * 4] - ((sz & 1) << 8);
                    int tile = r[0x202 + i * 4], attr = r[0x203 + i * 4];
                    list.Add(new Oam(x + bx, y + by, tile | ((attr & 1) << 8), attr, (sz & 2) != 0));
                }
                if (list.Count is > 0 and < 40) last = list;
            }
            return last;
        }
        catch { return null; }
    }

    /// <summary>
    /// Capture at a canonical mid-level cell and return CELL-RELATIVE OAM entries —
    /// the source data for the static sprite display table (SpriteDisplay).
    /// </summary>
    public static List<Oam>? CaptureRelative(Rom rom, int number)
    {
        var s = new Sprite(Screen: 1, XNibble: 4, Y: 20, Extra: 0, Number: number);   // cell (20,20)
        var oam = Capture(rom, s, 20, 20, false);
        return oam?.Select(o => o with { X = o.X - 320, Y = o.Y - 320 }).ToList();
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
