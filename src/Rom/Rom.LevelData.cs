namespace PipeDream;

/// <summary>
/// Rom — READING where a level's data lives (CONTRACT §3).
///
/// SMW keeps three parallel per-level pointer tables in bank $05, indexed by level number
/// (0x000-0x1FF). Each entry says where that level's raw data begins; the actual decoding
/// of that data lives elsewhere (Level.Parse for the header + Layer-1/2 object streams,
/// SpriteData.Parse for the sprite stream). This partial only resolves the pointers.
///
///   Layer 1  $05E000  3 bytes/level  → header (5 bytes) + Layer-1 object stream
///   Layer 2  $05E600  3 bytes/level  → Layer-2 object stream, OR a background image when
///                                       the pointer's bank is $FF (a background-image id,
///                                       not a real address)
///   Sprites  $05EC00  2 bytes/level  → sprite stream; the data bank is fixed at $07
/// </summary>
public sealed partial class Rom
{
    public const int Layer1TableSnes = 0x05E000; // 3 bytes/level
    public const int Layer2TableSnes = 0x05E600; // 3 bytes/level
    public const int SpriteTableSnes = 0x05EC00; // 2 bytes/level, data bank fixed $07
    public const int LevelCount = 0x200;

    /// <summary>24-bit SNES pointer to a level's Layer 1 header+object data.</summary>
    public int Layer1Pointer(int level) => ReadValue(Layer1TableSnes + level * 3, 3);

    /// <summary>Layer 2 pointer. Bank $FF means "layer 2 is a background image", not object data.</summary>
    public int Layer2Pointer(int level) => ReadValue(Layer2TableSnes + level * 3, 3);
    public bool Layer2IsBackground(int level) => (Layer2Pointer(level) >> 16) == 0xFF;

    /// <summary>Sprite data pointer (16-bit; bank is fixed $07).</summary>
    public int SpritePointer(int level) => 0x070000 | ReadValue(SpriteTableSnes + level * 2, 2);

    /// <summary>True if a level mode is vertical (VerticalTable $058417, bit 0).</summary>
    public bool IsVerticalMode(int levelMode) => (ReadByte(0x058417 + (levelMode & 0x1F)) & 1) != 0;
}
