namespace PipeDream;

/// <summary>
/// GFX file access + LC_LZ2 decompression (SMW's native format). Ported from the ROM
/// decompressor at $00B8DE; pointer tables at $00B992/$00B9C4/$00B9F6. See CONTRACT.md §6a.
/// </summary>
public static class Gfx
{
    public const int PtrLow = 0x00B992, PtrHigh = 0x00B9C4, PtrBank = 0x00B9F6, Count = 0x32;

    /// <summary>LM's ExGFX 0x80-0xFF pointer table (3 bytes/file) — fixed address (CONTRACT §7d).</summary>
    public const int ExGfx80Table = 0x0FF600;

    /// <summary>
    /// 24-bit SNES source address of a compressed GFX/ExGFX file, or -1 (0x7F = skip slot,
    /// or ExGFX file not inserted). 0x00-0x33 vanilla tables; 0x80-0xFF LM table at $0FF600;
    /// 0x100+ LM table located per-ROM (CONTRACT §7d).
    /// </summary>
    public static int SourceSnes(Rom rom, int file)
    {
        if (file < Count)
        {
            int lo = rom.Data[rom.FileOffset(PtrLow) + file];
            int hi = rom.Data[rom.FileOffset(PtrHigh) + file];
            int bk = rom.Data[rom.FileOffset(PtrBank) + file];
            return (bk << 16) | (hi << 8) | lo;
        }
        int ptr = file switch
        {
            < 0x80 => -1,                                                    // 0x32-0x7F invalid/skip
            < 0x100 => rom.ReadValue(ExGfx80Table + (file - 0x80) * 3, 3),
            _ => rom.LmExGfxBase < 0 ? -1 : rom.ReadValue(rom.LmExGfxBase + (file - 0x100) * 3, 3),
        };
        return ptr <= 0 || ptr == 0xFFFFFF ? -1 : ptr;
    }

    public static byte[] DecompressFile(Rom rom, int file)
        => Lz2Decompress(rom.Data, rom.FileOffset(SourceSnes(rom, file)));

    /// <summary>DecompressFile with a per-ROM cache (GFX data is immutable per ROM;
    /// recompose paths re-read the same files constantly). Null = absent/corrupt file.
    /// Locked: the phase composes run on parallel workers.</summary>
    public static byte[]? Cached(Rom rom, int file)
    {
        lock (rom.GfxFileCache)
        {
            if (rom.GfxFileCache.TryGetValue(file, out var hit)) return hit;
            byte[]? data = null;
            int src = SourceSnes(rom, file);
            if (src > 0) { try { data = Lz2Decompress(rom.Data, rom.FileOffset(src)); } catch { } }
            return rom.GfxFileCache[file] = data;
        }
    }

    /// <summary>
    /// The bit-depth every GFX/ExGFX file in this ROM is stored at. It is NOT knowable from
    /// a single file's decompressed size — a partial ExGFX file (e.g. 64 tiles) is ambiguous
    /// (2048 bytes could be 128×2bpp, 85×3bpp, or 64×4bpp). But SMW stores ALL graphics at
    /// one depth ROM-wide: vanilla is 3bpp (a full 128-tile file = 3072 bytes), and Lunar
    /// Magic re-normalizes everything to 4bpp on save (full file = 4096). So probe a
    /// guaranteed-full base file (GFX00) and read the depth off its size.
    /// ponytail: recomputed per call (one tiny LZ2 of GFX00); GFX loads aren't per-frame.
    /// </summary>
    public static int RomBpp(Rom rom)
    {
        try { return DecompressFile(rom, 0).Length >= 4096 ? 4 : 3; }
        catch { return 3; }
    }

    public static int TileBytes(int bpp) => bpp * 8;   // 2bpp=16, 3bpp=24, 4bpp=32

    public const int ObjectGfxList = 0x00A92B;         // FG/BG GFX file list, indexed by tileset*4

    /// <summary>
    /// The FG 8×8 tile source for a level's tileset. SMW loads 4 GFX files into VRAM word
    /// addresses $0000/$0800/$1000/$1800 (DATA_00A9D6), so a Map16 word's tile number maps to
    /// slot = tile>>7, file = OBJECTGFXLIST[tileset*4 + slot] (see PrepLoadFGBG / $00AA22).
    /// Vanilla FG GFX are 3bpp. This resolves a tile number to its 64 palette-index pixels.
    /// </summary>
    public sealed class FgTiles
    {
        private static readonly byte[] Blank = new byte[64];
        // 8 pages of 0x80 8x8 tiles = tiles 0x000-0x3FF. Pages 0-3 are the vanilla FG/BG
        // slots; 4-5 are BG2/BG3, the two extra slots LM's VRAM patch adds (option_vram.htm:
        // "anything in slots BG2 and BG3 will not be loaded" without the patch). Pages 6-7
        // are the animated-tile region, filled by OverlayAnimatedTiles.
        private readonly byte[][][] slots = new byte[8][][];

        // Bypass record word per background page: FG1=w7, FG2=w6, BG1=w5, FG3=w4 (pages 0-3),
        // then BG2=w3, BG3=w2 (pages 4-5). CONTRACT §7d.
        private static readonly int[] BypassSlotWord = [7, 6, 5, 4, 3, 2];

        /// <summary>
        /// Overlay animation frame 0 onto the loaded slots (CONTRACT §12) — what LM displays.
        /// GFX32+33 sit at a fixed pointer (operands at $00B88B/$00B890, vanilla $08BFC0),
        /// 3bpp; the game's 4bpp conversion zero-fills plane 3, so source tile index =
        /// (addr - 0x7D00)/0x20 into the 3bpp data (24 bytes/tile).
        /// </summary>
        private void OverlayAnimatedTiles(Rom rom, int tileset, int phase, int level)
        {
            // Two boot-time blobs ($00B888/$00B8D7, operands read per-ROM):
            // blob1 (vanilla $08BFC0): 3bpp, converted to occupy $7E7D00-$7EACFF.
            // blob2 (vanilla $088000): raw 4bpp at $7E2000-$7E7CFF — the decompressor writes
            // via [$00],Y without advancing $00, so it lands over blob1's spent 3bpp source.
            int bank = rom.ReadByte(0x00B890) << 16;
            byte[] anim1, anim2;
            try
            {
                anim1 = Lz2Decompress(rom.Data, rom.FileOffset(bank | rom.ReadValue(0x00B88B, 2)));
                anim2 = Lz2Decompress(rom.Data, rom.FileOffset(bank | rom.ReadValue(0x00B8D8, 2)));
            }
            catch { return; }

            // blob1 is packed at the ROM's FG depth (3bpp vanilla, 4bpp on LM 4bpp ROms). Decoding
            // it as a fixed 3bpp garbles animated tiles on 4bpp hacks (e.g. ShaoBase munchers).
            int a1bpp = RomBpp(rom), a1tb = TileBytes(a1bpp);
            void Overlay(int vramTile, int srcAddr)
            {
                byte[]? px =
                    srcAddr >= 0x7D00 && (srcAddr - 0x7D00) / 0x20 * a1tb + a1tb <= anim1.Length
                        ? DecodeTile(anim1, (srcAddr - 0x7D00) / 0x20 * a1tb, a1bpp)
                    : srcAddr >= 0x2000 && srcAddr - 0x2000 + 0x20 <= anim2.Length
                        ? DecodeTile(anim2, srcAddr - 0x2000, 4)
                    : null;
                if (px is null) return;
                OverlayPx(vramTile, px);
            }

            void OverlayPx(int vramTile, byte[] px)
            {
                int s = (vramTile >> 7) & 7, t = vramTile & 0x7F;
                if (slots[s] is { } arr && t < arr.Length) slots[s][t] = px;
            }

            for (int id = 0; id < 24; id++)
            {
                int dest = rom.ReadValue(0x05B93B + id * 2, 2);
                if (dest == 0) continue;
                int behavior = rom.ReadByte(0x05B96B + id);
                int animId = id;
                if (behavior >= 2) animId += rom.ReadByte(0x05B98B + (tileset & 0x0F));
                // behavior 1 = POW-dependent: editor shows the inactive state (id unchanged)
                int srcAddr = rom.ReadValue(0x05B999 + animId * 8 + (phase & 3) * 2, 2);
                if (dest == 0x800)
                {
                    // Berry slot ($00A3DA split): tiles 80/81 then 90/91. The 4 phase
                    // pointers land in Mario's 4bpp sheet (Yoshi berry-eat tiles, wobble
                    // frames pre-stored incl. the mirrored one) — no transform needed.
                    Overlay(0x80, srcAddr); Overlay(0x81, srcAddr + 0x20);
                    Overlay(0x90, srcAddr + 0x40); Overlay(0x91, srcAddr + 0x60);
                }
                else
                {
                    for (int k = 0; k < 4; k++) Overlay(dest / 16 + k, srcAddr + k * 0x20);
                }
            }

            // LM ExAnimation (CONTRACT §12e): overlay each per-level slot's current frame onto
            // its dest tile. Frame source resolves through the same $7D00 model as vanilla
            // (custom ExGFX 60-63 not yet loaded there — standard animated GFX only).
            if (level >= 0)
                foreach (var slot in ExAnimation.ReadLevel(rom, level))
                    Overlay(slot.DestTile, slot.FrameSrcAddrs[phase % slot.FrameCount]);

            // LM global ExAnimation (CONTRACT §12f): resolved by emulating LM's engine. Unlike the
            // vanilla/per-level paths the source is raw ROM GFX (RomBpp), so decode it directly
            // rather than through the $7D00/$2000 anim buffers.
            if (rom.LmGlobalExAnimPtr >= 0)
            {
                int gbpp = RomBpp(rom), gtb = TileBytes(gbpp);
                foreach (var (destTile, anim) in ExAnimation.GlobalStates(rom)[phase & 3])
                {
                    int gfo = rom.FileOffset(anim.SrcSnes);
                    for (int k = 0; k < anim.TileCount; k++)
                    {
                        if (gfo < 0 || gfo + (k + 1) * gtb > rom.Data.Length) break;
                        OverlayPx(destTile + k, DecodeTile(rom.Data, gfo + k * gtb, gbpp));
                    }
                }
            }
        }

        public static FgTiles Load(Rom rom, int tileset, int level = -1, int animPhase = 0)
        {
            var bypass = level >= 0 ? rom.LmGfxBypass(level) : null;
            int bpp = RomBpp(rom);                                  // ROM-wide depth (vanilla 3 / LM 4)
            var f = new FgTiles();
            for (int s = 0; s < f.slots.Length; s++) f.slots[s] = [];   // default all pages blank
            for (int s = 0; s < BypassSlotWord.Length; s++)
            {
                int file;
                if (s < 4)
                {
                    // Pages 0-3: the vanilla FG/BG list (4 files per tileset), bypass overrides.
                    file = rom.Data[rom.FileOffset(ObjectGfxList) + tileset * 4 + s];
                    if (bypass is not null && (bypass[BypassSlotWord[s]] & 0xFFF) != 0x7F)
                        file = bypass[BypassSlotWord[s]] & 0xFFF;
                }
                else
                {
                    // Pages 4-5 (BG2/BG3): only exist via the bypass; no vanilla default.
                    if (bypass is null) continue;
                    file = bypass[BypassSlotWord[s]] & 0xFFF;
                    if (file == 0x7F) continue;                    // slot off → blank
                }
                if (Cached(rom, file) is not { } data) continue;   // missing/corrupt → blank

                int tb = TileBytes(bpp), n = data.Length / tb;
                var tiles = new byte[n][];
                for (int t = 0; t < n; t++) tiles[t] = DecodeTile(data, t * tb, bpp);
                f.slots[s] = tiles;
            }
            f.OverlayAnimatedTiles(rom, tileset, animPhase, level);   // animated tiles (§12, §12e)
            return f;
        }

        public byte[] Fetch(int tileNum)
        {
            int s = (tileNum >> 7) & 7, t = tileNum & 0x7F;   // 8 pages (0x000-0x3FF)
            var arr = slots[s];
            return arr is not null && t < arr.Length ? arr[t] : Blank;
        }
    }

    /// <summary>
    /// Render a decompressed GFX file as an RGBA tile sheet (cols tiles wide) using one
    /// palette row. Color index 0 is transparent (shown as dark grey so tiles are visible).
    /// Returns pixels (row-major RGBA) and dimensions.
    /// </summary>
    public static (uint[] px, int w, int h) TileSheet(byte[] gfx, int bpp, Palette pal, int palRow, int cols = 16)
    {
        int tb = TileBytes(bpp);
        int tiles = Math.Max(1, gfx.Length / tb);
        int rows = (tiles + cols - 1) / cols;
        int w = cols * 8, h = rows * 8;
        var px = new uint[w * h];
        int baseColor = (palRow & 0x0F) * 16;
        for (int t = 0; t < tiles; t++)
        {
            var tile = DecodeTile(gfx, t * tb, bpp);
            int ox = (t % cols) * 8, oy = (t / cols) * 8;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int idx = tile[y * 8 + x];
                    px[(oy + y) * w + (ox + x)] = idx == 0 ? 0xFF303030u : pal.Rgba[baseColor + idx];
                }
        }
        return (px, w, h);
    }

    /// <summary>
    /// Decode one 8×8 SNES planar tile at <paramref name="off"/> to 64 palette indices
    /// (row-major, 0..2^bpp-1). Planes 0/1 are row-interleaved in the first 16 bytes; 3bpp
    /// adds one plane-2 byte per row; 4bpp adds planes 2/3 row-interleaved.
    /// </summary>
    public static byte[] DecodeTile(byte[] src, int off, int bpp)
    {
        var px = new byte[64];
        for (int row = 0; row < 8; row++)
        {
            int p0 = src[off + row * 2], p1 = src[off + row * 2 + 1], p2 = 0, p3 = 0;
            if (bpp == 3) p2 = src[off + 16 + row];
            else if (bpp == 4) { p2 = src[off + 16 + row * 2]; p3 = src[off + 16 + row * 2 + 1]; }
            for (int col = 0; col < 8; col++)
            {
                int bit = 7 - col;
                px[row * 8 + col] = (byte)(((p0 >> bit) & 1)
                    | (((p1 >> bit) & 1) << 1) | (((p2 >> bit) & 1) << 2) | (((p3 >> bit) & 1) << 3));
            }
        }
        return px;
    }

    /// <summary>
    /// Decompress LC_LZ2 data starting at file offset <paramref name="p"/>. Reads contiguous
    /// PC bytes (LoROM bank-crossing is just contiguous in PC space). Terminates on a 0xFF
    /// command byte.
    /// </summary>
    public static byte[] Lz2Decompress(byte[] src, int p, int cap = 0x10000)
    {
        var outBuf = new List<byte>(cap);
        while (true)
        {
            int header = src[p++];
            if (header == 0xFF) break;
            int cmd = header >> 5;
            int length;
            if (cmd == 7)                                   // extended: real cmd in bits 2-4, 10-bit len
            {
                cmd = (header >> 2) & 7;
                length = (((header & 3) << 8) | src[p++]) + 1;
            }
            else
            {
                length = (header & 0x1F) + 1;
            }

            switch (cmd)
            {
                case 0:                                     // direct copy
                    for (int i = 0; i < length; i++) outBuf.Add(src[p++]);
                    break;
                case 1:                                     // byte fill (RLE)
                {
                    byte b = src[p++];
                    for (int i = 0; i < length; i++) outBuf.Add(b);
                    break;
                }
                case 2:                                     // word fill (2 bytes alternating)
                {
                    byte a = src[p++], b = src[p++];
                    for (int i = 0; i < length; i++) outBuf.Add((i & 1) == 0 ? a : b);
                    break;
                }
                case 3:                                     // increasing fill
                {
                    byte b = src[p++];
                    for (int i = 0; i < length; i++) { outBuf.Add(b); b++; }
                    break;
                }
                case 4:                                     // LZ copy from absolute output offset
                {
                    int off = (src[p++] << 8) | src[p++];
                    for (int i = 0; i < length; i++) outBuf.Add(outBuf[off + i]);
                    break;
                }
                default:
                    throw new InvalidDataException($"LC_LZ2: bad command {cmd} at src offset {p}");
            }
            if (outBuf.Count > cap) throw new InvalidDataException("LC_LZ2: output exceeded cap");
        }
        return outBuf.ToArray();
    }
}
