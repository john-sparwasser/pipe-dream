namespace PipeDream;

/// <summary>
/// GFX file access + LC_LZ2 decompression (SMW's native format). Ported from the ROM
/// decompressor at $00B8DE; pointer tables at $00B992/$00B9C4/$00B9F6. See CONTRACT.md §6a.
/// </summary>
public static class Gfx
{
    public const int PtrLow = 0x00B992, PtrHigh = 0x00B9C4, PtrBank = 0x00B9F6, Count = 0x32;

    /// <summary>24-bit SNES source address of a compressed GFX file (vanilla tables).</summary>
    public static int SourceSnes(Rom rom, int file)
    {
        int lo = rom.Data[rom.FileOffset(PtrLow) + file];
        int hi = rom.Data[rom.FileOffset(PtrHigh) + file];
        int bk = rom.Data[rom.FileOffset(PtrBank) + file];
        return (bk << 16) | (hi << 8) | lo;
    }

    public static byte[] DecompressFile(Rom rom, int file)
        => Lz2Decompress(rom.Data, rom.FileOffset(SourceSnes(rom, file)));

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
        private readonly byte[][][] slots = new byte[4][][];

        public static FgTiles Load(Rom rom, int tileset)
        {
            var f = new FgTiles();
            for (int s = 0; s < 4; s++)
            {
                int file = rom.Data[rom.FileOffset(ObjectGfxList) + tileset * 4 + s];
                var data = DecompressFile(rom, file);
                int tb = TileBytes(3), n = data.Length / tb;
                var tiles = new byte[n][];
                for (int t = 0; t < n; t++) tiles[t] = DecodeTile(data, t * tb, 3);
                f.slots[s] = tiles;
            }
            return f;
        }

        public byte[] Fetch(int tileNum)
        {
            int s = (tileNum >> 7) & 3, t = tileNum & 0x7F;
            var arr = slots[s];
            return t < arr.Length ? arr[t] : Blank;
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
