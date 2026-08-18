namespace PipeDream;

/// <summary>
/// READING level data: ROM bytes → header + decoded object lists (CONTRACT §4).
///
/// Mirrors the SMW loader. A Layer-1 level is a 5-byte header followed by a 3-byte object
/// stream (some objects carry extra bytes), terminated by a 0xFF lead byte. The screen
/// counter advances on the new-screen flag and is retargeted by screen-jump commands,
/// exactly as `$1928` is driven in `LoadLevelData`. Layer 2 is either the same object format
/// or a run-length-encoded background image (see DecodeBgImage). The inverse (Level → bytes)
/// lives in LevelEncoder.
/// </summary>
public static class LevelParser
{
    /// <summary>
    /// Parse a level's Layer-1 data (header + object stream) from the ROM.
    /// Header is 5 bytes; objects are 3 bytes each starting at header+5, terminated by a
    /// 0xFF lead byte.
    /// </summary>
    public static Level Parse(Rom rom, int number)
    {
        int ptr = rom.Layer1Pointer(number);
        int fo = rom.FileOffset(ptr);
        var header = new LevelHeader(rom.LevelHeaderOverrides.TryGetValue(number, out var over)
                                     ? over : rom.Data.AsSpan(fo, 5));
        var objs = ParseObjects(rom, fo + 5, out bool empty);
        return new Level(number, ptr, header, objs, empty);
    }

    /// <summary>
    /// Layer-2 object list, or null when layer 2 is a background image (bank $FF pointer).
    /// The stream has its own 5-byte header copy which the game skips ($0583FB).
    /// </summary>
    public static List<LevelObject>? ParseLayer2(Rom rom, int number)
    {
        if (rom.Layer2IsBackground(number)) return null;
        int ptr = rom.Layer2Pointer(number);
        return ParseObjects(rom, rom.FileOffset(ptr) + 5, out _);
    }

    /// <summary>
    /// Layer-2 background image: BG Map16 def indices (two 16x27 screens), or null when
    /// layer 2 is object data. RLE at $0C:(ptr&FFFF) (CONTRACT §10): cmd bit7 = run of next
    /// byte, else literal copy; FF FF ends; high/page byte = 0 or 1 — page 1 when ptr
    /// low16 >= 0xE8FE INCLUSIVE (`CPX #$E8FE : BCC` at $058046; the disasm comment says
    /// $E8FF and is off by one — level 0x10A sits exactly on 0xE8FE). Buffer init = tile 0x25.
    /// </summary>
    public static ushort[]? DecodeBgImage(Rom rom, int number)
    {
        if (!rom.Layer2IsBackground(number)) return null;
        int lo16 = rom.Layer2Pointer(number) & 0xFFFF;
        int page = BgImage.PageFor(lo16);
        byte[] low = BgImage.Decode(rom, lo16, out _);
        var tiles = new ushort[low.Length];
        for (int i = 0; i < low.Length; i++) tiles[i] = (ushort)((page << 8) | low[i]);
        return tiles;
    }

    /// <summary>Parse an encoded stream buffer (5-byte header + objects + FF) — the
    /// exact inverse of LevelEncoder.Encode, for round-trip testing of edited objects.</summary>
    public static List<LevelObject> ParseEncoded(Rom rom, byte[] encoded)
        => ParseObjects(rom, encoded, 5, out _);

    private static List<LevelObject> ParseObjects(Rom rom, int p, out bool empty)
        => ParseObjects(rom, rom.Data, p, out empty);

    private static List<LevelObject> ParseObjects(Rom rom, byte[] data, int p, out bool empty)
    {
        int fo = p - 5;
        var objs = new List<LevelObject>();
        empty = data[p] == 0xFF;
        bool dm16Rom = rom.HasDm16Hijack;        // obj# 0x23/0x27 are DM16 when installed
        int screen = 0;                          // ROM zeroes $1928 at layer-1 start
        // Safety cap: a level can't exceed its own bank; stop at 0xFF or a sane bound.
        int limit = fo + 0x8000;
        while (p + 2 < data.Length && p < limit)
        {
            byte b1 = data[p];
            if (b1 == 0xFF) break;               // terminator (lead byte)
            byte b2 = data[p + 1], b3 = data[p + 2];
            bool newScreen = (b1 & 0x80) != 0;
            if (newScreen) screen++;             // ROM: $1928 += 1 on the flag
            int number2 = ((b1 & 0x60) >> 1) | (b2 >> 4);
            // Screen jumps retarget the counter for all following objects:
            // vanilla ext 0x01 ($0DA53D): screen = Y bits; LM ext 0x03 ($0DE1E0): screen = b2.
            if (number2 == 0 && b3 == 0x01) screen = b1 & 0x1F;
            else if (dm16Rom && number2 == 0 && b3 == 0x03) screen = b2;
            int y = b1 & 0x1F;
            int xNib = b2 & 0x0F;
            p += 3;
            int extra = -1, dm16 = -1, dm16Page = -1, dm16ExtX = -1, dm16ExtH = -1;
            if (number2 == 0 && b3 == 0x00)
            {
                // Screen exit (extended object 0x00): reads a 4th byte ($0DA512 does $65 += 1).
                extra = p < data.Length ? data[p] : -1; p += 1;
            }
            else if (dm16Rom && number2 == 0 && b3 == 0x02)
            {
                // LM secondary exit (ext obj 0x02, handler $0DE1B0): 2 extra bytes = exit word.
                extra = p + 1 < data.Length ? data[p] | (data[p + 1] << 8) : 0; p += 2;
            }
            else if (dm16Rom && number2 == 0x22)
            {
                // DM16 page-0 form: 1 extra byte = tile low, tile = 0x000 | low ($0DF08A).
                dm16 = p < data.Length ? data[p] : 0; p += 1;
            }
            else if (dm16Rom && number2 == 0x23)
            {
                // DM16 Form A (page-1 tile): 1 extra byte = tile low, tile = 0x100 | low.
                dm16 = 0x100 | (p < data.Length ? data[p] : 0); p += 1;
            }
            else if (dm16Rom && (number2 == 0x27 || number2 == 0x29))
            {
                // DM16 Form B ($0DF150) / BG form ($0DFF50): page byte + tile low, plus
                // page-bit-7 → run byte, page-bits-7+6 → height override (CONTRACT §8).
                int pg = data[p], low = data[p + 1]; p += 2;
                int page = (pg & 0x3F) | (number2 == 0x29 ? 0x40 : 0);
                dm16 = (page << 8) | low;
                dm16Page = pg;
                if ((pg & 0x80) != 0) { dm16ExtX = data[p]; p += 1; }
                if ((pg & 0xC0) == 0xC0) { dm16ExtH = data[p]; p += 1; }
            }
            objs.Add(new LevelObject(newScreen, number2, screen, xNib, y, b3, extra, dm16, dm16Page, dm16ExtX, dm16ExtH));
        }
        return objs;
    }
}
