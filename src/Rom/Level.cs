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
    public readonly int Byte3;       // raw settings byte
    public readonly int ExtraByte;   // 4th byte for screen exits, else -1
    // For standard (rectangle-family) objects; other families reinterpret Byte3.
    public int Width => (Byte3 & 0x0F) + 1;
    public int Height => (Byte3 >> 4) + 1;
    public int ExtendedNumber => Byte3;   // when Extended
    public int AbsoluteX => Screen * 16 + XNibble;
    // Screen exit = extended object 0x00; the only variable-length object (4 bytes).
    public bool IsScreenExit => Extended && Byte3 == 0x00;

    public LevelObject(bool newScreen, int number, int screen, int xNibble, int y, int b3, int extra)
    {
        NewScreen = newScreen; Number = number; Screen = screen;
        XNibble = xNibble; Y = y; Byte3 = b3; ExtraByte = extra; Extended = number == 0;
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
            // Screen exit (extended object 0x00) is the only object that reads a 4th byte
            // ($0DA512 does $65 += 1). Every other object is exactly 3 bytes.
            int extra = -1;
            if (number2 == 0 && b3 == 0x00) { extra = p < data.Length ? data[p] : -1; p += 1; }
            objs.Add(new LevelObject(newScreen, number2, screen, xNib, y, b3, extra));
        }
        return new Level(number, ptr, header, objs, empty);
    }
}
