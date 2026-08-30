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
///   $05FC00,Y  bits 0-4 → screen, bits 5-7 → Mario X index (DATA_05D750/58)
///   $05FE00,Y  bits 0-2 → entrance action → $192A (0 = walk in, 5 = the ROR $86 case)
///
/// Lunar Magic reads the rest of $05FE00 and one more table, in its routine at $03BCE0
/// (hooked from $05D833, installed by every LM save — reference/LM_PARITY.md):
///
///   $05FE00,Y  bit 3    → destination level bit 8 (replaces the submap guess)
///              bits 4-5 → X bit 7 (which half of the screen) and X bit 8 (vertical levels)
///              bit 6    → method 2 on: X = screen &lt;&lt; 8 | XHigh bit 0 &lt;&lt; 7 | MarioX &lt;&lt; 4,
///                         Y = YHigh &lt;&lt; 8 | MarioY &lt;&lt; 4 — 16px steps instead of the tables
///              bit 7    → $192A bit 7
///   fifth table, at the address LM's reader at $05DC85 names: bits 0-5 → Y high byte
///
/// The fifth byte is zero on a base without that routine and <see cref="Rom.ReadSecondaryEntrance"/>
/// only reads it where one exists.
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

    /// <summary>$05FC00 bits 0-4 — the screen Mario enters on.</summary>
    public int ReservedX { get; init; }

    // Lunar Magic's bits of $05FE00 and its fifth table — see the type doc.
    public int DestinationHigh { get; init; }    // $05FE00 bit 3
    public int XHigh { get; init; }              // $05FE00 bits 4-5
    public int Method2 { get; init; }            // $05FE00 bit 6
    public int ActionHigh { get; init; }         // $05FE00 bit 7
    public int YHigh { get; init; }              // fifth table bits 0-5
    public int ReservedYHigh { get; init; }      // fifth table bits 6-7 (bit 7 = exit to overworld)
    /// <summary>Sixth table (reader at $05DC8A): bit 7 = FG/BG relative to player, bit 6 = face
    /// left (same $13CD path as the main entrance), bit 5 → $192A bit 6. Carried whole.</summary>
    public int FgBg { get; init; }

    /// <summary>Decode from the table bytes, in table order (F800, FA00, FC00, FE00, then LM's
    /// Y-high and FG/BG bytes when present — four bytes is a base without method 2).</summary>
    public SecondaryEntrance(ReadOnlySpan<byte> b)
    {
        DestinationLevel = b[0];
        MarioY = b[1] & 0x0F;
        ScreenBoundaryY = (b[1] >> 4) & 0x03;
        VerticalScroll = (b[1] >> 6) & 0x03;
        MarioX = (b[2] >> 5) & 0x07;
        ReservedX = b[2] & 0x1F;
        EntranceAction = b[3] & 0x07;
        DestinationHigh = (b[3] >> 3) & 0x01;
        XHigh = (b[3] >> 4) & 0x03;
        Method2 = (b[3] >> 6) & 0x01;
        ActionHigh = (b[3] >> 7) & 0x01;
        if (b.Length < 5) return;
        YHigh = b[4] & 0x3F;
        ReservedYHigh = (b[4] >> 6) & 0x03;
        if (b.Length < 6) return;
        FgBg = b[5];
    }

    /// <summary>Re-pack into the six table bytes — the exact inverse of the decode.</summary>
    public byte[] ToBytes() =>
    [
        (byte)(DestinationLevel & 0xFF),
        (byte)((MarioY & 0x0F) | ((ScreenBoundaryY & 0x03) << 4) | ((VerticalScroll & 0x03) << 6)),
        (byte)((ReservedX & 0x1F) | ((MarioX & 0x07) << 5)),
        (byte)((EntranceAction & 0x07) | ((DestinationHigh & 0x01) << 3) | ((XHigh & 0x03) << 4)
               | ((Method2 & 0x01) << 6) | ((ActionHigh & 0x01) << 7)),
        (byte)((YHigh & 0x3F) | ((ReservedYHigh & 0x03) << 6)),
        (byte)FgBg,
    ];
}
