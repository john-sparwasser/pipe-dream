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
    /// Vanilla files that are NOT 3bpp tile-planar, and so must never be converted to 4bpp —
    /// a blanket "convert every file" would silently corrupt each of these, because the game
    /// reads them with a routine that is not the tile uploader:
    ///   0x27        Mode 7 (boss rooms). $00AB42 expands 3-bits-per-pixel to 8bpp indices
    ///               through the shifter at $00ABC4; consumes 0xC00 but is not tile-packed.
    ///   0x28-0x2B   layer-3 tiles, 2bpp (16 B/tile). $00A993 streams 0x800/file straight out.
    ///   0x2F        2bpp. $00955E streams 0x400.
    ///   0x32-0x33   the animation blobs. 0x33 (ROM $088000) is ALREADY raw 4bpp; 0x32 is 3bpp
    ///               but is expanded by its own reader at $00B8AD into $7E7D00, not uploaded,
    ///               and neither is reachable through the pointer tables at all (their
    ///               addresses are the fixed operands at $00B88B/$00B8D8/$00B890).
    /// Everything else below <see cref="Count"/> is FG/BG/sprite tile data that goes through
    /// the expand-upload, which prep v4 teaches to read four planes.
    /// </summary>
    public static bool IsTilePlanar3Bpp(int file)
        => file is not (0x27 or 0x2F or (>= 0x28 and <= 0x2B) or 0x32 or 0x33);

    /// <summary>
    /// 24-bit SNES source address of a compressed GFX/ExGFX file, or -1 (0x7F = skip slot,
    /// or ExGFX file not inserted). 0x00-0x33 vanilla tables; 0x80-0xFF LM table at $0FF600
    /// (only meaningful when LM's loader is installed — on vanilla/prepped ROMs those bytes
    /// are arbitrary data); 0x100+ LM table located per-ROM (CONTRACT §7d).
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
            < 0x100 => rom.HasLmGfxLoader ? rom.ReadValue(ExGfx80Table + (file - 0x80) * 3, 3) : -1,
            _ => rom.LmExGfxBase < 0 ? -1 : rom.ReadValue(rom.LmExGfxBase + (file - 0x100) * 3, 3),
        };
        return ptr <= 0 || ptr == 0xFFFFFF ? -1 : ptr;
    }

    public static byte[] DecompressFile(Rom rom, int file)
        => Lz2Decompress(rom.Data, rom.FileOffset(SourceSnes(rom, file)));

    /// <summary>DecompressFile with a per-ROM cache (GFX data is immutable per ROM;
    /// recompose paths re-read the same files constantly). Project imports
    /// (Rom.ImportedGfx, already raw planar) win over the ROM's files — checked before
    /// the cache so a re-import can't serve a stale/negative entry. Null = absent/corrupt
    /// file. Locked: the phase composes run on parallel workers.</summary>
    public static byte[]? Cached(Rom rom, int file)
    {
        lock (rom.GfxFileCache)
        {
            if (rom.ImportedGfx.TryGetValue(file, out var imported)) return imported;
            if (rom.GfxFileCache.TryGetValue(file, out var hit)) return hit;
            byte[]? data = null;
            int src = SourceSnes(rom, file);
            if (src > 0) { try { data = Lz2Decompress(rom.Data, rom.FileOffset(src)); } catch { } }
            return rom.GfxFileCache[file] = data;
        }
    }

    /// <summary>Drop the decompressed-file cache. Call when Rom.ImportedGfx changes —
    /// a previously-missing id may be negative-cached, and consumers that copied decoded
    /// tiles rebuild through Cached on the next recompose.</summary>
    public static void InvalidateCache(Rom rom)
    {
        lock (rom.GfxFileCache) { rom.GfxFileCache.Clear(); rom.RomBppCache = -1; }
    }

    /// <summary>
    /// The bit-depth every GFX/ExGFX file in this ROM is stored at. It is NOT knowable from
    /// a single file's decompressed size — a partial ExGFX file (e.g. 64 tiles) is ambiguous
    /// (2048 bytes could be 128×2bpp, 85×3bpp, or 64×4bpp). But SMW stores ALL graphics at
    /// one depth ROM-wide: vanilla is 3bpp (a full 128-tile file = 3072 bytes), and Lunar
    /// Magic re-normalizes everything to 4bpp on save (full file = 4096). So probe a
    /// guaranteed-full base file (GFX00) and read the depth off its size.
    /// Cached on the Rom: the GFX editor asks every frame, and the answer can only change
    /// when the GFX cache is invalidated (SMW's depth is ROM-wide, and imports are
    /// normalized to it, so in practice it never changes at all).
    /// </summary>
    public static int RomBpp(Rom rom)
    {
        if (rom.RomBppCache > 0) return rom.RomBppCache;
        try { return rom.RomBppCache = DecompressFile(rom, 0).Length >= 4096 ? 4 : 3; }
        catch { return 3; }
    }

    /// <summary>
    /// The depth ONE file is stored at. <see cref="RomBpp"/> is the rule — SMW stores its
    /// graphics at a single depth and LM re-normalizes everything to 4bpp — but a handful of
    /// vanilla ids are read by routines that are not the tile uploader, and what THEY expect is
    /// fixed by the game's code rather than by the ROM's era. Those are exactly the ids
    /// <see cref="IsTilePlanar3Bpp"/> excludes, and the depth is not guessable from the file's
    /// size (0x800 bytes is 128 tiles of 2bpp, 85 of 3bpp or 64 of 4bpp — see
    /// <see cref="DetectBpp"/>), so it is read off that list instead of sniffed:
    ///   0x28-0x2B  layer-3 tiles, 2bpp — the status bar and the level's layer-3 scenery
    ///   0x2F       2bpp
    ///   0x33       already raw 4bpp on a vanilla ROM
    ///   0x27, 0x32 whatever a conversion left them at — see <see cref="UnconvertedBpp"/>
    /// Neither of those two is tile-packed at all, so the depth only says how wide their rows
    /// are; the picker says what they actually hold.
    /// ExGFX (0x80+) follow the ROM: a user file's depth is normalised on import.
    /// </summary>
    public static int FileBpp(Rom rom, int file) => file switch
    {
        (>= 0x28 and <= 0x2B) or 0x2F => 2,
        0x33 => 4,
        0x27 or 0x32 => UnconvertedBpp(rom),
        _ => RomBpp(rom),
    };

    /// <summary>
    /// The depth of the files a 4bpp conversion SKIPS — Mode 7 and the animation source. Their
    /// readers are not the tile uploader (the animation blob has its own 3bpp-to-4bpp expander
    /// at $00B8AD, which prep v4 does not patch), so prep v6 leaves them three planes deep even
    /// though <see cref="RomBpp"/> now reads 4. Lunar Magic converts them along with everything
    /// else, and its 4bpp ROMs are told apart by the upload: LM stubs vanilla's expand-upload
    /// and runs its own, where our prep rewrites vanilla's loops in place (CONTRACT §0).
    ///
    /// Reading the animation source at the wrong depth garbles every animated tile in the level
    /// view — the munchers, the lava, the question blocks — which is what "the ROM is 4bpp so
    /// this must be too" cost the moment v6 landed.
    /// </summary>
    private static int UnconvertedBpp(Rom rom) => RomBpp(rom) == 4 && !rom.HasGfx4bppUpload ? 4 : 3;

    public static int TileBytes(int bpp) => bpp * 8;   // 2bpp=16, 3bpp=24, 4bpp=32

    /// <summary>
    /// Bit depth of a raw planar .bin, from its size. Exact full 128-tile files first
    /// (0x1000 = 4bpp, 0xC00 = 3bpp), then whole-tile divisibility — sizes divisible by
    /// both 32 and 24 (e.g. 0x600) resolve 4bpp, the LM-era convention. 0 = not a valid
    /// 3/4bpp planar file.
    /// </summary>
    public static int DetectBpp(byte[] data) => data.Length switch
    {
        0x1000 => 4,
        0xC00 => 3,
        > 0 when data.Length % 32 == 0 => 4,
        > 0 when data.Length % 24 == 0 => 3,
        _ => 0,
    };

    /// <summary>
    /// Convert raw planar tiles between 3bpp and 4bpp (DecodeTile's layout: planes 0/1
    /// row-interleaved in the first 16 bytes, then plane 2 packed one byte per row (3bpp)
    /// or planes 2/3 row-interleaved (4bpp)). 3→4 zero-fills plane 3; 4→3 drops it and
    /// reports via <paramref name="plane3Dropped"/> when nonzero data was discarded.
    /// </summary>
    public static byte[] NormalizeBpp(byte[] data, int fromBpp, int toBpp, out bool plane3Dropped)
    {
        plane3Dropped = false;
        if (fromBpp == toBpp) return data;
        int from = TileBytes(fromBpp), to = TileBytes(toBpp), n = data.Length / from;
        var outp = new byte[n * to];
        for (int t = 0; t < n; t++)
        {
            Array.Copy(data, t * from, outp, t * to, 16);      // planes 0/1: same layout both depths
            for (int row = 0; row < 8; row++)
                if (toBpp == 4)
                {
                    outp[t * to + 16 + row * 2] = data[t * from + 16 + row];
                }
                else
                {
                    outp[t * to + 16 + row] = data[t * from + 16 + row * 2];
                    plane3Dropped |= data[t * from + 16 + row * 2 + 1] != 0;
                }
        }
        return outp;
    }

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

            // blob1's depth is the blob's own, NOT the ROM's: 3bpp on vanilla and on our prepped
            // bases (prep v6 converts the tile files and leaves this one alone, because its
            // reader at $00B8AD is not the tile uploader), 4bpp on an LM 4bpp hack, which
            // converts it too. Fixed 3bpp garbles ShaoBase's munchers; the ROM's depth garbles
            // every animated tile on a v6 base.
            int a1bpp = FileBpp(rom, 0x32), a1tb = TileBytes(a1bpp);
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
    /// DecodeTile's inverse for one pixel: set/clear the pixel's bit in each plane byte of
    /// the 8×8 planar tile at <paramref name="off"/>. x/y are within the tile (0-7),
    /// colorIdx 0..2^bpp-1. Same plane layout as DecodeTile.
    /// </summary>
    public static void SetTilePixel(byte[] src, int off, int bpp, int x, int y, int colorIdx)
    {
        int bit = 7 - x;
        void Plane(int o, int plane)
        {
            if (((colorIdx >> plane) & 1) != 0) src[o] |= (byte)(1 << bit);
            else src[o] &= (byte)~(1 << bit);
        }
        Plane(off + y * 2, 0);
        Plane(off + y * 2 + 1, 1);
        if (bpp == 3) Plane(off + 16 + y, 2);
        else if (bpp == 4) { Plane(off + 16 + y * 2, 2); Plane(off + 16 + y * 2 + 1, 3); }
    }

    // ---- pixel editing ----
    // Pure planar-byte work, so it lives with the format rather than in a UI: both editors
    // paint through these, and a stroke recorded by one replays identically in the other.

    /// <summary>One pixel write with stroke capture: record each plane byte that actually
    /// changed as (offset, before, after). A same-colour write records nothing.</summary>
    public static void WritePixel(byte[] gfx, int tileOff, int bpp, int x, int y, int color,
                                  List<(int off, byte before, byte after)> stroke)
    {
        int tb = TileBytes(bpp);
        if (tileOff < 0 || tileOff + tb > gfx.Length) return;
        Span<int> offs = stackalloc int[4];
        offs[0] = tileOff + y * 2; offs[1] = tileOff + y * 2 + 1;
        int n = 2;
        if (bpp == 3) offs[n++] = tileOff + 16 + y;
        else if (bpp == 4) { offs[n++] = tileOff + 16 + y * 2; offs[n++] = tileOff + 16 + y * 2 + 1; }
        Span<byte> before = stackalloc byte[4];
        for (int i = 0; i < n; i++) before[i] = gfx[offs[i]];
        SetTilePixel(gfx, tileOff, bpp, x, y, color);
        for (int i = 0; i < n; i++)
            if (gfx[offs[i]] != before[i]) stroke.Add((offs[i], before[i], gfx[offs[i]]));
    }

    /// <summary>4-connected flood fill WITHIN the 8x8 tile containing sheet pixel (px,py):
    /// replaces the clicked pixel's colour region with <paramref name="color"/>. No-op when the
    /// target already is the colour. Sheet layout = 16 tiles per row.</summary>
    public static void FillTile(byte[] gfx, int bpp, int px, int py, int color,
                               List<(int off, byte before, byte after)> stroke)
    {
        int tb = TileBytes(bpp);
        int tileOff = ((py / 8) * 16 + px / 8) * tb;
        if (tileOff < 0 || tileOff + tb > gfx.Length) return;
        var idx = DecodeTile(gfx, tileOff, bpp);
        int sx = px & 7, sy = py & 7;
        byte target = idx[sy * 8 + sx];
        if (target == color) return;
        var work = new Stack<(int x, int y)>();
        work.Push((sx, sy));
        while (work.Count > 0)
        {
            var (x, y) = work.Pop();
            if (x is < 0 or > 7 || y is < 0 or > 7 || idx[y * 8 + x] != target) continue;
            idx[y * 8 + x] = (byte)color;
            WritePixel(gfx, tileOff, bpp, x, y, color, stroke);
            work.Push((x + 1, y)); work.Push((x - 1, y)); work.Push((x, y + 1)); work.Push((x, y - 1));
        }
    }

    /// <summary>The editable byte array for a file: the existing import, or a copy-on-write fork
    /// of the stock bytes keyed under the SAME id (shadowing the ROM file for every consumer —
    /// deliberately opposite of an import's new-id allocation). Null when the id resolves
    /// nowhere.</summary>
    public static byte[]? EditableBytes(Rom rom, int file, out bool forked)
    {
        forked = false;
        if (rom.ImportedGfx.TryGetValue(file, out var b)) return b;
        if (Cached(rom, file) is not { } stock) return null;
        var fork = (byte[])stock.Clone();
        rom.ImportedGfx[file] = fork;
        InvalidateCache(rom);               // consumers re-resolve through the import
        forked = true;
        return fork;
    }

    /// <summary>Replay stroke bytes into the file's CURRENT array — re-looked-up by id and
    /// bounds-checked, so a re-import that replaced (or removed) the array cannot crash or
    /// corrupt a replay.
    ///
    /// UNDO WALKS BACKWARD. A stroke records one entry per byte WRITE, not per byte, and a
    /// single plane byte carries 8 pixels of a tile row — so painting along a row (or any fill)
    /// rewrites the same offset repeatedly: (off,A,B), (off,B,C), (off,C,D). Restoring those
    /// front-to-back ends on C, the second-to-last value, leaving most of the stroke painted.
    /// Last-to-first unwinds D→C→B→A and lands on the original. Redo is order-independent for a
    /// given offset (the last write wins either way) but stays forward so it mirrors the paint
    /// order.</summary>
    public static void ApplyStroke(Rom rom, int file, (int off, byte before, byte after)[] edits, bool redo)
    {
        if (!rom.ImportedGfx.TryGetValue(file, out var g)) return;
        if (redo)
            foreach (var (off, _, after) in edits)
                { if (off >= 0 && off < g.Length) g[off] = after; }
        else
            for (int i = edits.Length - 1; i >= 0; i--)
            {
                var (off, before, _) = edits[i];
                if (off >= 0 && off < g.Length) g[off] = before;
            }
    }

    /// <summary>
    /// Files worth offering in a picker, in id order: either the project's CUSTOM ExGFX files or
    /// the ROM's own base files, never both — a picker that mixes them makes "where did this come
    /// from" unanswerable.
    ///
    /// The split is by what the ROM RESOLVES, not by what has been edited: a copy-on-write fork of
    /// a base file lives in ImportedGfx but is still that base file, and listing it as custom would
    /// promise an ExGFX id it does not have.
    /// </summary>
    public static List<int> Candidates(Rom rom, bool custom, string filter)
    {
        var ids = custom
            ? rom.ImportedGfx.Keys.Where(id => SourceSnes(rom, id) < 0).ToList()
            : Enumerable.Range(0, 0x34).ToList();
        ids.Sort();
        if (filter.Length == 0) return ids;
        return ids.Where(id => Matches(rom, id, filter)).ToList();
    }

    /// <summary>A file matches when the filter appears anywhere in its NAME, or PREFIXES its hex
    /// id — so "grass" finds it by name and "10" finds $100-$10F. Ids deliberately are not
    /// substring-matched: a one-letter filter like "a" would otherwise drag in $00A, $01A, $02A…
    /// by coincidence. Both spellings of the id are tried so "a" still finds $00A.</summary>
    public static bool Matches(Rom rom, int id, string filter) =>
        (rom.GfxName(id) is { Length: > 0 } n &&
         n.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
        id.ToString("X").StartsWith(filter, StringComparison.OrdinalIgnoreCase) ||
        id.ToString("X3").StartsWith(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>One line describing what a file holds, for a picker row. The EFFECTIVE depth,
    /// not storage: the loader expands 3bpp storage to 4bpp planes on VRAM upload, so on the
    /// SNES display every FG/BG/SP file here is 4bpp — a 3bpp ROM just leaves colours 8-15 of
    /// the row unreachable, which is the half of the fact worth a note.</summary>
    public static string Describe(Rom rom, int id)
        => Cached(rom, id) is null ? "(empty)"
         : id == 0x27 ? "Mode 7 tiles — not an 8x8 sheet"
         : id == 0x32 ? "animation source — not an 8x8 sheet"
         : FileBpp(rom, id) == 2 ? "2 bits per pixel (layer 3, colours 0-3)"
         : RomBpp(rom) == 3 ? "4 bits per pixel (colours 0-7)" : "4 bits per pixel";

    /// <summary>
    /// The level's 10 VRAM GFX bins (FG/BG/SP), resolved through the tileset lists and the Super
    /// GFX Bypass (session overrides ride along inside LmGfxBypass). Def is the vanilla list
    /// entry (0x7F for the bypass-only BG2/BG3 bins) — File != Def means the bypass repointed it.
    /// </summary>
    public static (string Name, int PalRow, int BypWord, int Def, int File)[] LevelSlots(
        Rom rom, LevelHeader h, int levelNum)
    {
        var byp = rom.LmGfxBypass(levelNum);

        // (name, GFXLIST base, list index, palette row for the preview, bypass record word)
        var slots = new (string name, int listBase, int idx, int palRow, int bypWord)[]
        {
            ("FG1", ObjectGfxList, h.Tileset * 4 + 0, 2, 7),
            ("FG2", ObjectGfxList, h.Tileset * 4 + 1, 2, 6),
            ("BG1", ObjectGfxList, h.Tileset * 4 + 2, 0, 5),
            ("FG3", ObjectGfxList, h.Tileset * 4 + 3, 2, 4),
            // BG2/BG3 have no vanilla list entry (listBase -1) — only via the bypass (LM VRAM patch).
            ("BG2", -1, 0, 0, 3),
            ("BG3", -1, 0, 0, 2),
            ("SP1", 0x00A8C3, h.SpriteSet * 4 + 0, 8, 11),
            ("SP2", 0x00A8C3, h.SpriteSet * 4 + 1, 8, 10),
            ("SP3", 0x00A8C3, h.SpriteSet * 4 + 2, 8, 9),
            ("SP4", 0x00A8C3, h.SpriteSet * 4 + 3, 8, 8),
        };
        return slots.Select(s =>
        {
            int def = s.listBase < 0 ? 0x7F : rom.Data[rom.FileOffset(s.listBase) + s.idx];
            bool bypassed = byp is not null && (byp[s.bypWord] & 0xFFF) != 0x7F;
            return (s.name, s.palRow, s.bypWord, def, bypassed ? byp![s.bypWord] & 0xFFF : def);
        }).ToArray();
    }

    /// <summary>
    /// Compress bytes as LC_LZ2 (the exact inverse consumer is <see cref="Lz2Decompress"/>
    /// and the ROM decompressor $00B8DE). Greedy and deterministic: byte runs ≥ 3 → cmd 1,
    /// word-period runs ≥ 6 → cmd 2 (chunked to even lengths so a chunk boundary never
    /// flips the alternation phase), everything else accumulates into cmd-0 literals.
    /// Chunks cap at the format's 10-bit length (0x400). Terminator 0xFF.
    /// </summary>
    public static byte[] Lz2Compress(byte[] data)
    {
        var outp = new List<byte>(data.Length / 2 + 16);
        void Hdr(int cmd, int len)                       // len 1..0x400
        {
            if (len <= 0x20) outp.Add((byte)((cmd << 5) | (len - 1)));
            else { outp.Add((byte)(0xE0 | (cmd << 2) | ((len - 1) >> 8))); outp.Add((byte)(len - 1)); }
        }
        // Bytes at 'at' matching a repeating pattern of 'period' bytes.
        int Run(int at, int period)
        {
            int n = period;
            while (at + n < data.Length && data[at + n] == data[at + n % period]) n++;
            return at + period <= data.Length ? n : 0;
        }
        int lit = 0;                                     // start of the pending literal run
        void FlushLit(int end)
        {
            for (; lit < end; lit += Math.Min(0x400, end - lit))
            {
                int n = Math.Min(0x400, end - lit);
                Hdr(0, n);
                outp.AddRange(data.AsSpan(lit, n).ToArray());
            }
        }
        for (int i = 0; i < data.Length; )
        {
            int br = Run(i, 1);
            if (br >= 3)
            {
                FlushLit(i);
                for (int left = br; left > 0; )
                {
                    int n = Math.Min(0x400, left);
                    Hdr(1, n); outp.Add(data[i]);
                    left -= n;
                }
                i += br; lit = i;
                continue;
            }
            int wr = Run(i, 2) & ~1;                     // even total keeps chunk phase aligned
            if (wr >= 6)
            {
                FlushLit(i);
                for (int left = wr; left > 0; )
                {
                    int n = Math.Min(0x400, left);
                    Hdr(2, n); outp.Add(data[i]); outp.Add(data[i + 1]);
                    left -= n;
                }
                i += wr; lit = i;
                continue;
            }
            i++;
        }
        FlushLit(data.Length);
        outp.Add(0xFF);
        return outp.ToArray();
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
