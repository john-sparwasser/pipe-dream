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

    /// <summary>This object with a different NewScreen flag (struct is immutable).</summary>
    public LevelObject WithNewScreen(bool ns) =>
        new(ns, Number, Screen, XNibble, Y, Byte3, ExtraByte, Dm16Tile, Dm16Page, Dm16ExtX, Dm16ExtH);

    /// <summary>A vanilla screen-jump command (ext obj 0x01) targeting a screen.</summary>
    public static LevelObject ScreenJump(int screen) =>
        new(false, 0, screen, 0, screen & 0x1F, 0x01, -1);

    /// <summary>Create a Direct Map16 object placing <paramref name="tile"/> at a cell.</summary>
    public static LevelObject MakeDm16(int tile, int screen, int xNib, int y, int w = 1, int h = 1, bool newScreen = false)
    {
        // Page-0 Form (obj 0x22), page-1 Form A (0x23), or general Form B (0x27).
        int num = tile <= 0xFF ? 0x22 : tile <= 0x1FF ? 0x23 : 0x27;
        int size = ((h - 1) << 4) | (w - 1);
        return new LevelObject(newScreen, num, screen, xNib, y, size, -1, tile);
    }
}

/// <summary>
/// Parsed Layer-1 level: header + decoded object list. The read side (ROM bytes → this
/// object) lives in Level.Parse.cs; the write side (object list → ROM bytes) in
/// Level.Encode.cs. This core file is just the data model + fields.
/// </summary>
public sealed partial class Level
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
}
