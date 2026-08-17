namespace PipeDream;

/// <summary>
/// A level's main entrance and entry settings — what applies when the level is entered
/// from the overworld rather than through a secondary exit. Four bytes across four parallel
/// 512-entry tables in bank 05, indexed by LEVEL number, decoded at $05D90D-$05D99F:
///
///   $05F000,Y  bits 0-3 → Mario Y index (DATA_05D730/40)
///              bits 4-7 → layer-2 scroll (DATA_05D720/10 → $1413 horizontal, $1414 vertical)
///   $05F200,Y  bits 0-2 → Mario X index (DATA_05D750/58)
///              bits 3-5 → entrance action → $192A
///              bits 6-7 → layer-2 BG setting → $1BE3
///   $05F400,Y  bits 0-1 → vertical scroll (DATA_05D70C → $20)
///              bits 2-3 → screen boundary Y (DATA_05D708 → $1C)
///   $05F600,Y  bits 5-6 → the vertical-level flag → $5B
///              bit 7    → skip the entrance walk → $141F
///
/// $05F600 is read BEFORE the $1B93 check at $05D94C and the rest after, so a level's
/// vertical flag and entrance-walk bit apply however it was entered, while the spawn
/// position only applies to a main entrance — a secondary exit has already placed Mario.
///
/// The same two RAM targets appear in <see cref="SecondaryEntrance"/> at DIFFERENT bit
/// positions ($1C at bits 4-5 and $20 at bits 6-7 of one byte there, bits 2-3 and 0-1 of
/// another byte here). The two records cannot share packing code.
/// </summary>
public readonly record struct MainEntrance
{
    public int MarioY { get; init; }             // $05F000 bits 0-3
    public int Layer2Scroll { get; init; }       // $05F000 bits 4-7
    public int MarioX { get; init; }             // $05F200 bits 0-2
    public int EntranceAction { get; init; }     // $05F200 bits 3-5
    public int Layer2Setting { get; init; }      // $05F200 bits 6-7
    public int VerticalScroll { get; init; }     // $05F400 bits 0-1
    public int ScreenBoundaryY { get; init; }    // $05F400 bits 2-3
    public int VerticalLevel { get; init; }      // $05F600 bits 5-6
    public int SkipEntranceWalk { get; init; }   // $05F600 bit 7

    /// <summary>$05F400 bits 4-7 — not read by the entry decode.</summary>
    public int ReservedBoundary { get; init; }
    /// <summary>$05F600 bits 0-4 — stashed to $01 at $05D99C but not decoded here.</summary>
    public int ReservedMode { get; init; }

    /// <summary>Decode from the four table bytes, in table order (F000, F200, F400, F600).</summary>
    public MainEntrance(ReadOnlySpan<byte> b)
    {
        MarioY = b[0] & 0x0F;
        Layer2Scroll = (b[0] >> 4) & 0x0F;
        MarioX = b[1] & 0x07;
        EntranceAction = (b[1] >> 3) & 0x07;
        Layer2Setting = (b[1] >> 6) & 0x03;
        VerticalScroll = b[2] & 0x03;
        ScreenBoundaryY = (b[2] >> 2) & 0x03;
        ReservedBoundary = (b[2] >> 4) & 0x0F;
        ReservedMode = b[3] & 0x1F;
        VerticalLevel = (b[3] >> 5) & 0x03;
        SkipEntranceWalk = (b[3] >> 7) & 0x01;
    }

    /// <summary>Re-pack into the four table bytes — the exact inverse of the decode.</summary>
    public byte[] ToBytes() =>
    [
        (byte)((MarioY & 0x0F) | ((Layer2Scroll & 0x0F) << 4)),
        (byte)((MarioX & 0x07) | ((EntranceAction & 0x07) << 3) | ((Layer2Setting & 0x03) << 6)),
        (byte)((VerticalScroll & 0x03) | ((ScreenBoundaryY & 0x03) << 2) | ((ReservedBoundary & 0x0F) << 4)),
        (byte)((ReservedMode & 0x1F) | ((VerticalLevel & 0x03) << 5) | ((SkipEntranceWalk & 0x01) << 7)),
    ];
}
