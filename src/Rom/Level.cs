namespace PipeDream;

/// <summary>
/// The 5-byte SMW level header. Field extraction confirmed against the disassembly
/// header decoder (bank 05 `CODE_0584E3`). See reference/CONTRACT.md §4.
/// </summary>
public readonly struct LevelHeader
{
    public readonly int Screens;         // byte0 bits0-4 (+1)
    public readonly int BgPalette;       // byte0 bits5-7
    public readonly int LevelMode;       // byte1 bits0-4
    public readonly int BackAreaColor;   // byte1 bits5-7
    public readonly int SpriteSet;       // byte2 bits0-3
    public readonly int Music;           // byte2 bits4-6
    public readonly int Layer3Priority;  // byte2 bit7
    public readonly int Time;            // byte3 bits6-7 (TimerTable index, $05857B)
    public readonly int SpritePalette;   // byte3 bits3-5
    public readonly int FgPalette;       // byte3 bits0-2
    public readonly int Tileset;         // byte4 bits0-3  <-- drives object rendering dispatch
    public readonly int ItemMemory;      // byte4 bits6-7
    public readonly int ScrollSetting;   // byte4 bits4-5

    public LevelHeader(ReadOnlySpan<byte> h)
    {
        byte b0 = h[0], b1 = h[1], b2 = h[2], b3 = h[3], b4 = h[4];
        Screens = (b0 & 0x1F) + 1;
        BgPalette = b0 >> 5;
        LevelMode = b1 & 0x1F;
        BackAreaColor = b1 >> 5;
        SpriteSet = b2 & 0x0F;
        Music = (b2 >> 4) & 0x07;
        Layer3Priority = b2 >> 7;
        Time = b3 >> 6;
        SpritePalette = (b3 >> 3) & 0x07;
        FgPalette = b3 & 0x07;
        Tileset = b4 & 0x0F;
        ItemMemory = b4 >> 6;
        ScrollSetting = (b4 >> 4) & 0x03;
    }
}

/// <summary>One decoded Layer-1 object. Encoding confirmed via bank 05 `LoadLevelData`.</summary>
public readonly struct LevelObject
{
    public readonly bool NewScreen;
    public readonly bool Extended;   // object# == 0
    public readonly int Number;      // 0x00-0x3F (0 = extended)
    public readonly int Screen;      // absolute screen number
    public readonly int XNibble;     // 0-15 within screen
    public readonly int Y;           // 0-0x1F
    public readonly int Byte3;       // raw settings byte (= size for DM16)
    public readonly int ExtraByte;   // 4th byte for screen exits, else -1
    public readonly int Dm16Tile;    // Direct-Map16 tile number, else -1
    // Extended DM16 forms (obj 0x27/0x29 with page-byte bits 6-7 set, CONTRACT §8):
    public readonly int Dm16Page;    // raw page byte (-1 = simple/none)
    public readonly int Dm16ExtX;    // extra run byte (page bit7), -1 = absent
    public readonly int Dm16ExtH;    // height-override byte (page bits 6+7), -1 = absent
    // For standard (rectangle-family) objects; other families reinterpret Byte3.
    public int Width => (Byte3 & 0x0F) + 1;
    public int Height => (Byte3 >> 4) + 1;
    public int ExtendedNumber => Byte3;   // when Extended
    public int AbsoluteX => Screen * 16 + XNibble;
    // Screen exit = extended object 0x00; the only variable-length object (4 bytes).
    public bool IsScreenExit => Extended && Byte3 == 0x00;
    // Direct Map16: LM object # 0x23 (Form A, page 1) or 0x27 (Form B, any page).
    public bool IsDm16 => Dm16Tile >= 0;

    public LevelObject(bool newScreen, int number, int screen, int xNibble, int y, int b3, int extra, int dm16 = -1,
                       int dm16Page = -1, int dm16ExtX = -1, int dm16ExtH = -1)
    {
        NewScreen = newScreen; Number = number; Screen = screen;
        XNibble = xNibble; Y = y; Byte3 = b3; ExtraByte = extra; Dm16Tile = dm16; Extended = number == 0;
        Dm16Page = dm16Page; Dm16ExtX = dm16ExtX; Dm16ExtH = dm16ExtH;
    }

    /// <summary>Create a Direct Map16 object placing <paramref name="tile"/> at a cell.</summary>
    public static LevelObject MakeDm16(int tile, int screen, int xNib, int y, int w = 1, int h = 1, bool newScreen = false)
    {
        // Page-0 Form (obj 0x22), page-1 Form A (0x23), or general Form B (0x27).
        int num = tile <= 0xFF ? 0x22 : tile <= 0x1FF ? 0x23 : 0x27;
        int size = ((h - 1) << 4) | (w - 1);
        return new LevelObject(newScreen, num, screen, xNib, y, size, -1, tile);
    }
}

/// <summary>Parsed Layer-1 level: header + decoded object list.</summary>
public sealed class Level
{
    public readonly int Number;
    public readonly int DataPointer;     // SNES address of the header
    public readonly LevelHeader Header;
    public readonly IReadOnlyList<LevelObject> Objects;
    public readonly bool Empty;          // first data byte was 0xFF (no objects)

    private Level(int number, int ptr, LevelHeader header, List<LevelObject> objs, bool empty)
    {
        Number = number; DataPointer = ptr; Header = header; Objects = objs; Empty = empty;
    }

    /// <summary>
    /// Parse a level's Layer-1 data (header + object stream) from the ROM.
    /// Mirrors the ROM loader: header is 5 bytes, objects are 3 bytes each starting at
    /// header+5, terminated by a 0xFF lead byte. Screen counter increments on the
    /// new-screen flag exactly as `$1928` does in `LoadLevelData`.
    /// </summary>
    public static Level Parse(Rom rom, int number)
    {
        int ptr = rom.Layer1Pointer(number);
        int fo = rom.FileOffset(ptr);
        var header = new LevelHeader(rom.Data.AsSpan(fo, 5));
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
    /// Layer-2 background image: 0x400 BG Map16 def indices (32 rows × 32 cols, two 16-wide
    /// screens), or null when layer 2 is object data. RLE at $0C:(ptr&FFFF) (CONTRACT §10):
    /// cmd bit7 = run of next byte, else literal copy; FF FF ends; high/page byte = 0 or 1
    /// (ptr low16 >= 0xE8FF → page 1). Buffer initialized to tile 0x25.
    /// </summary>
    public static ushort[]? DecodeBgImage(Rom rom, int number)
    {
        if (!rom.Layer2IsBackground(number)) return null;
        int lo16 = rom.Layer2Pointer(number) & 0xFFFF;
        int page = lo16 >= 0xE8FF ? 1 : 0;
        var tiles = new ushort[0x400];
        Array.Fill(tiles, (ushort)((page << 8) | 0x25));
        int p = rom.FileOffset(0x0C0000 | lo16), o = 0;
        while (o < 0x400 && p + 1 < rom.Data.Length)
        {
            int cmd = rom.Data[p++];
            if (cmd == 0xFF && rom.Data[p] == 0xFF) break;
            int count = (cmd & 0x7F) + 1;
            if ((cmd & 0x80) != 0)
            {
                byte b = rom.Data[p++];
                for (int i = 0; i < count && o < 0x400; i++) tiles[o++] = (ushort)((page << 8) | b);
            }
            else
            {
                for (int i = 0; i < count && o < 0x400; i++) tiles[o++] = (ushort)((page << 8) | rom.Data[p++]);
            }
        }
        return tiles;
    }

    private static List<LevelObject> ParseObjects(Rom rom, int p, out bool empty)
    {
        var data = rom.Data;
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

    /// <summary>
    /// Re-encode the header + object list back to the raw Layer-1 byte stream (the exact
    /// inverse of <see cref="Parse"/>). Header bytes are copied verbatim from the ROM (we
    /// don't yet re-derive header fields); objects re-emit their 3 bytes (+1 for screen exits),
    /// terminated by 0xFF. Verified by round-trip against the original bytes.
    /// </summary>
    public byte[] Encode(Rom rom) => Encode(rom, Objects);

    /// <summary>Encode this level's header (verbatim from ROM) + a given object list + 0xFF.</summary>
    public byte[] Encode(Rom rom, IEnumerable<LevelObject> objects)
    {
        var outb = new List<byte>(256);
        outb.AddRange(rom.Data.AsSpan(rom.FileOffset(DataPointer), 5).ToArray());   // header
        foreach (var o in objects) AppendObject(outb, o);
        outb.Add(0xFF);
        return outb.ToArray();
    }

    private static void AppendObject(List<byte> outb, LevelObject o)
    {
        if (o.IsDm16)
        {
            // b1 carries object# bits 4-5 (<<1), b2 high nibble = object# low nibble.
            byte db1 = (byte)((o.NewScreen ? 0x80 : 0) | ((o.Number & 0x30) << 1) | (o.Y & 0x1F));
            byte db2 = (byte)(((o.Number & 0x0F) << 4) | (o.XNibble & 0x0F));
            outb.Add(db1); outb.Add(db2); outb.Add((byte)o.Byte3);
            if (o.Number is 0x22 or 0x23)                  // 1 tile byte (page fixed 0/1)
            {
                outb.Add((byte)(o.Dm16Tile & 0xFF));
            }
            else                                           // 0x27/0x29: page byte + low (+extras)
            {
                outb.Add((byte)(o.Dm16Page >= 0 ? o.Dm16Page : (o.Dm16Tile >> 8) & 0x3F));
                outb.Add((byte)(o.Dm16Tile & 0xFF));
                if (o.Dm16ExtX >= 0) outb.Add((byte)o.Dm16ExtX);
                if (o.Dm16ExtH >= 0) outb.Add((byte)o.Dm16ExtH);
            }
            return;
        }
        byte b1 = (byte)((o.NewScreen ? 0x80 : 0) | ((o.Number & 0x30) << 1) | (o.Y & 0x1F));
        byte b2 = (byte)(((o.Number & 0x0F) << 4) | (o.XNibble & 0x0F));
        outb.Add(b1); outb.Add(b2); outb.Add((byte)o.Byte3);
        if (o.IsScreenExit && o.ExtraByte >= 0) outb.Add((byte)o.ExtraByte);
        else if (o.Extended && o.Byte3 == 0x02 && o.ExtraByte >= 0)
        {   // LM secondary exit: 2-byte exit word
            outb.Add((byte)(o.ExtraByte & 0xFF)); outb.Add((byte)(o.ExtraByte >> 8));
        }
    }
}
