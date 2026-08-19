namespace PipeDream;

/// <summary>
/// The 5-byte SMW level header. Field extraction confirmed against the disassembly
/// header decoder (bank 05 `CODE_0584E3`). See reference/CONTRACT.md §4.
/// A record struct so edits read as <c>h with { Tileset = n }</c>;
/// <see cref="ToBytes"/> is the exact inverse of the decode below.
/// </summary>
public readonly record struct LevelHeader
{
    public int Screens { get; init; }         // byte0 bits0-4 (+1)
    public int BgPalette { get; init; }       // byte0 bits5-7
    public int LevelMode { get; init; }       // byte1 bits0-4
    public int BackAreaColor { get; init; }   // byte1 bits5-7
    public int SpriteSet { get; init; }       // byte2 bits0-3
    public int Music { get; init; }           // byte2 bits4-6
    public int Layer3Priority { get; init; }  // byte2 bit7
    public int Time { get; init; }            // byte3 bits6-7 (TimerTable index, $05857B)
    public int SpritePalette { get; init; }   // byte3 bits3-5
    public int FgPalette { get; init; }       // byte3 bits0-2
    public int Tileset { get; init; }         // byte4 bits0-3  <-- drives object rendering dispatch
    public int ItemMemory { get; init; }      // byte4 bits6-7
    public int ScrollSetting { get; init; }   // byte4 bits4-5

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

    /// <summary>Re-pack the fields into the 5 ROM bytes. Every field is masked to its own
    /// bit width, so an out-of-range edit truncates rather than corrupting its neighbour.</summary>
    public byte[] ToBytes() =>
    [
        (byte)(((Screens - 1) & 0x1F) | ((BgPalette & 0x07) << 5)),
        (byte)((LevelMode & 0x1F) | ((BackAreaColor & 0x07) << 5)),
        (byte)((SpriteSet & 0x0F) | ((Music & 0x07) << 4) | ((Layer3Priority & 0x01) << 7)),
        (byte)((FgPalette & 0x07) | ((SpritePalette & 0x07) << 3) | ((Time & 0x03) << 6)),
        (byte)((Tileset & 0x0F) | ((ScrollSetting & 0x03) << 4) | ((ItemMemory & 0x03) << 6)),
    ];
}
