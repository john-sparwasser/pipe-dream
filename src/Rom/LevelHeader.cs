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
