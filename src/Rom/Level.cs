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
    public readonly int Time;            // byte3 bits5-7 (TimerTable index)
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
        Time = b3 >> 5;
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
    // For standard (rectangle-family) objects; other families reinterpret Byte3.
    public int Width => (Byte3 & 0x0F) + 1;
    public int Height => (Byte3 >> 4) + 1;
    public int ExtendedNumber => Byte3;   // when Extended
    public int AbsoluteX => Screen * 16 + XNibble;
    // Screen exit = extended object 0x00; the only variable-length object (4 bytes).
    public bool IsScreenExit => Extended && Byte3 == 0x00;
    // Direct Map16: LM object # 0x23 (Form A, page 1) or 0x27 (Form B, any page).
    public bool IsDm16 => Dm16Tile >= 0;

    public LevelObject(bool newScreen, int number, int screen, int xNibble, int y, int b3, int extra, int dm16 = -1)
    {
        NewScreen = newScreen; Number = number; Screen = screen;
        XNibble = xNibble; Y = y; Byte3 = b3; ExtraByte = extra; Dm16Tile = dm16; Extended = number == 0;
    }

    /// <summary>Create a Direct Map16 object placing <paramref name="tile"/> at a cell.</summary>
    public static LevelObject MakeDm16(int tile, int screen, int xNib, int y, int w = 1, int h = 1, bool newScreen = false)
    {
        int num = tile is >= 0x100 and <= 0x1FF ? 0x23 : 0x27;   // Form A page-1, else Form B
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
        var data = rom.Data;

        var header = new LevelHeader(data.AsSpan(fo, 5));
        int p = fo + 5;

        var objs = new List<LevelObject>();
        bool empty = data[p] == 0xFF;
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
            int y = b1 & 0x1F;
            int xNib = b2 & 0x0F;
            p += 3;
            int extra = -1, dm16 = -1;
            if (number2 == 0 && b3 == 0x00)
            {
                // Screen exit (extended object 0x00): reads a 4th byte ($0DA512 does $65 += 1).
                extra = p < data.Length ? data[p] : -1; p += 1;
            }
            else if (dm16Rom && number2 == 0x23)
            {
                // DM16 Form A (page-1 tile): 1 extra byte = tile low, tile = 0x100 | low.
                dm16 = 0x100 | (p < data.Length ? data[p] : 0); p += 1;
            }
            else if (dm16Rom && number2 == 0x27)
            {
                // DM16 Form B (any page): 2 extra bytes = tile high, low.
                dm16 = (p + 1 < data.Length ? (data[p] << 8) | data[p + 1] : 0); p += 2;
            }
            objs.Add(new LevelObject(newScreen, number2, screen, xNib, y, b3, extra, dm16));
        }
        return new Level(number, ptr, header, objs, empty);
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
            byte db1 = (byte)((o.NewScreen ? 0x80 : 0) | 0x40 | (o.Y & 0x1F));
            if (o.Number == 0x23)                          // Form A: page-1, 1 tile byte
            {
                outb.Add(db1); outb.Add((byte)(0x30 | (o.XNibble & 0x0F)));
                outb.Add((byte)o.Byte3); outb.Add((byte)(o.Dm16Tile & 0xFF));
            }
            else                                           // Form B: any page, 2 tile bytes
            {
                outb.Add(db1); outb.Add((byte)(0x70 | (o.XNibble & 0x0F)));
                outb.Add((byte)o.Byte3);
                outb.Add((byte)((o.Dm16Tile >> 8) & 0xFF)); outb.Add((byte)(o.Dm16Tile & 0xFF));
            }
            return;
        }
        byte b1 = (byte)((o.NewScreen ? 0x80 : 0) | ((o.Number & 0x30) << 1) | (o.Y & 0x1F));
        byte b2 = (byte)(((o.Number & 0x0F) << 4) | (o.XNibble & 0x0F));
        outb.Add(b1); outb.Add(b2); outb.Add((byte)o.Byte3);
        if (o.IsScreenExit && o.ExtraByte >= 0) outb.Add((byte)o.ExtraByte);
    }
}
