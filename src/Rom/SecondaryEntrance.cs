namespace PipeDream;

/// <summary>
/// One secondary entrance — the destination side of a secondary screen exit. A record is
/// four bytes spread across four parallel 512-entry tables in bank 05, decoded by
/// $05D7D9-$05D838.
///
/// The index is 9 bits and only the low 8 come from the exit: $05D7CB puts the player's
/// submap flag ($1F11) into bit 8, so exit byte $BB reaches record $0BB from the main map
/// and $1BB from a submap. Vanilla populates 42 records, 24 low and 18 high, in exactly
/// that pattern.
///
/// The four tables:
///
///   $05F800,Y  destination level (low byte; the high bit comes from the submap state at
///              $1F11, so an entrance can only name levels $000-$0FF on its own)
///   $05FA00,Y  bits 0-3 → Mario Y index (DATA_05D730/40), bits 4-5 → screen boundary Y
///              (DATA_05D708 → $1C), bits 6-7 → $20 (DATA_05D70C)
///   $05FC00,Y  bits 5-7 → Mario X index (DATA_05D750/58)
///   $05FE00,Y  bits 0-2 → entrance action → $192A (0 = walk in, 5 = the ROR $86 case)
///
/// The bits vanilla never reads are carried in the Reserved* fields so a record survives a
/// decode/encode round trip untouched — LM and patches are free to use them.
///
/// Note the packing differs from the MAIN entrance path ($05F000-$05F600), which puts
/// Mario X in bits 0-2 and the action in bits 3-5 of a single byte. Don't share code.
/// </summary>
public readonly record struct SecondaryEntrance
{
    public int DestinationLevel { get; init; }   // $05F800
    public int MarioY { get; init; }             // $05FA00 bits 0-3
    public int ScreenBoundaryY { get; init; }    // $05FA00 bits 4-5
    public int VerticalScroll { get; init; }     // $05FA00 bits 6-7
    public int MarioX { get; init; }             // $05FC00 bits 5-7
    public int EntranceAction { get; init; }     // $05FE00 bits 0-2

    /// <summary>$05FC00 bits 0-4 — not read by the vanilla decode.</summary>
    public int ReservedX { get; init; }
    /// <summary>$05FE00 bits 3-7 — not read by the vanilla decode.</summary>
    public int ReservedMisc { get; init; }

    /// <summary>Decode from the four table bytes, in table order (F800, FA00, FC00, FE00).</summary>
    public SecondaryEntrance(ReadOnlySpan<byte> b)
    {
        DestinationLevel = b[0];
        MarioY = b[1] & 0x0F;
        ScreenBoundaryY = (b[1] >> 4) & 0x03;
        VerticalScroll = (b[1] >> 6) & 0x03;
        MarioX = (b[2] >> 5) & 0x07;
        ReservedX = b[2] & 0x1F;
        EntranceAction = b[3] & 0x07;
        ReservedMisc = (b[3] >> 3) & 0x1F;
    }

    /// <summary>Re-pack into the four table bytes — the exact inverse of the decode.</summary>
    public byte[] ToBytes() =>
    [
        (byte)(DestinationLevel & 0xFF),
        (byte)((MarioY & 0x0F) | ((ScreenBoundaryY & 0x03) << 4) | ((VerticalScroll & 0x03) << 6)),
        (byte)((ReservedX & 0x1F) | ((MarioX & 0x07) << 5)),
        (byte)((EntranceAction & 0x07) | ((ReservedMisc & 0x1F) << 3)),
    ];
}
