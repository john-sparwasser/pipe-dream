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
///              bits 6-7 → LAYER 3 option → $1BE3 (Layer3.Option; §12b)
///   $05F400,Y  bits 0-1 → vertical scroll (DATA_05D70C → $20)
///              bits 2-3 → screen boundary Y (DATA_05D708 → $1C)
///              bits 4-7 → the MIDWAY entrance's screen ($05D9E1)
///   $05F600,Y  bits 0-4 → the main entrance's screen (stashed to $01 at $05D99C)
///              bits 5-6 → the vertical-level flag → $5B
///              bit 7    → skip the entrance walk → $141F
///
/// $05F600 is read BEFORE the $1B93 check at $05D94C and the rest after, so a level's
/// vertical flag and entrance-walk bit apply however it was entered, while the spawn
/// position only applies to a main entrance — a secondary exit has already placed Mario.
///
/// Lunar Magic adds two more per-level bytes, read by its routine at $05DD30 (hooked from
/// $05D97D, installed by every LM save — reference/LM_PARITY.md). They are "method 2": the
/// position stops being an index into the bank-05 tables and becomes 16px steps, with the
/// same two nibbles reinterpreted:
///
///   $05DE00,Y  bit 5    → method 2 on
///              bit 3    → X bit 7 (which half of the screen), bit 4 → X bit 8 (vertical levels)
///              bits 6-7 → $192A bits 6-7 (entrance action high bits)
///   $06FC00,Y  bits 0-5 → Y high byte (Y = high &lt;&lt; 8 | MarioY &lt;&lt; 4)
///
/// with X = screen &lt;&lt; 8 | XHigh bit 0 &lt;&lt; 7 | MarioX &lt;&lt; 4 on a horizontal level. Both
/// bytes are zero on a base that has no such routine, and <see cref="Rom.ReadMainEntrance"/>
/// only reads them where one exists.
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
    public int Layer3Option { get; init; }       // $05F200 bits 6-7 — the LAYER 3 option (see $1BE3)
    public int VerticalScroll { get; init; }     // $05F400 bits 0-1
    public int ScreenBoundaryY { get; init; }    // $05F400 bits 2-3
    public int VerticalLevel { get; init; }      // $05F600 bits 5-6
    public int SkipEntranceWalk { get; init; }   // $05F600 bit 7

    /// <summary>$05F400 bits 4-7 — the MIDWAY entrance's screen.</summary>
    public int ReservedBoundary { get; init; }
    /// <summary>$05F600 bits 0-4 — the main entrance's screen.</summary>
    public int ReservedMode { get; init; }

    // Lunar Magic's method 2 ($05DE00 / $06FC00) — see the type doc.
    public int Method2 { get; init; }            // $05DE00 bit 5
    public int XHigh { get; init; }              // $05DE00 bits 3-4
    public int ActionHigh { get; init; }         // $05DE00 bits 6-7
    public int SpriteSpawnRange { get; init; }   // $05DE00 bits 0-1 — LM's sprite vertical spawn range (→ $0BF4)
    public int SmartSpawn { get; init; }         // $05DE00 bit 2 — LM's "smart spawn" (→ $0BF4 bit 7)
    public int YHigh { get; init; }              // $06FC00 bits 0-5
    public int FgOffsetNegative { get; init; }   // $06FC00 bit 6 — the relative FG offset is downward
    public int ReservedYHigh { get; init; }      // $06FC00 bit 7

    // $06FE00 — LM's FG/BG byte, landing in $13CD for the $05DA17 tail (LmLevelEntry). With
    // FgBgRelative set the entrance's ScreenBoundaryY/VerticalScroll nibble becomes the FG offset
    // (x16 px, sign above) from Mario's Y instead of an index into the fixed-position tables.
    public int BgHeight { get; init; }           // bits 0-5 — BG height in tiles, for the BG position
    public int FaceLeft { get; init; }           // bit 6 — Mario faces left on entry (block A: BIT $13CD → STZ $76)
    public int FgBgRelative { get; init; }       // bit 7 — "set FG/BG relative to player"

    // LM's level-height byte (block B +0, per-ROM — Rom.LmLevelHeightTable): the one thing that
    // makes a horizontal level taller. HeightIndex picks one of 32 LUT heights, 0 = vanilla 0x1B0;
    // W columns of that height must fit the tilemap (W x height <= 0x3800).
    public int HeightIndex { get; init; }        // bits 0-4 → LUT (Rom.LevelHeightPx)
    public int ExtendedSprites { get; init; }    // bit 5 — LM's extended sprite stream ($0BF5 bit 5)
    public int HeightReserved { get; init; }     // bit 6
    public int VerticalPositioning { get; init; }// bit 7 — LM sets it on every level it saves

    // Lunar Magic's separate midway settings — four per-level bytes in its midway tables (see
    // Rom.LmMidwayTable), read by the routine hooked at $05D9E3. Zero everywhere until a level
    // opts in; MidwayScreenHigh applies even when it has not (a fifth screen bit).
    public int MidwayAction { get; init; }       // flags bits 0-2 → $192A bits 0-2
    public int MidwayXHigh { get; init; }        // flags bit 3 → X bit 8 (vertical levels only)
    public int MidwayScreenHigh { get; init; }   // flags bit 4 → midway screen bit 4
    public int MidwaySeparate { get; init; }     // flags bit 5 → the rest of these apply
    public int MidwayActionHigh { get; init; }   // flags bits 6-7 → $192A bits 6-7
    public int MidwayX { get; init; }            // position bits 0-3 → X bits 4-7
    public int MidwayY { get; init; }            // position bits 4-7 → Y bits 4-7
    public int MidwayFgBg { get; init; }         // FG/BG byte, $05F400's layout in bits 0-3; bit 5 = redirect
    public int MidwayYHigh { get; init; }        // Y-high byte: bits 0-5 → Y high, bit 6 set by LM

    /// <summary>Decode from the table bytes, in table order (F000, F200, F400, F600, then LM's
    /// DE00 and 06:FC00 when present, then the four midway bytes when present — four bytes is a
    /// base without method 2, six one without separate midway settings).</summary>
    public MainEntrance(ReadOnlySpan<byte> b)
    {
        MarioY = b[0] & 0x0F;
        Layer2Scroll = (b[0] >> 4) & 0x0F;
        MarioX = b[1] & 0x07;
        EntranceAction = (b[1] >> 3) & 0x07;
        Layer3Option = (b[1] >> 6) & 0x03;
        VerticalScroll = b[2] & 0x03;
        ScreenBoundaryY = (b[2] >> 2) & 0x03;
        ReservedBoundary = (b[2] >> 4) & 0x0F;
        ReservedMode = b[3] & 0x1F;
        VerticalLevel = (b[3] >> 5) & 0x03;
        SkipEntranceWalk = (b[3] >> 7) & 0x01;
        if (b.Length < 6) return;
        SpriteSpawnRange = b[4] & 0x03;
        SmartSpawn = (b[4] >> 2) & 0x01;
        XHigh = (b[4] >> 3) & 0x03;
        Method2 = (b[4] >> 5) & 0x01;
        ActionHigh = (b[4] >> 6) & 0x03;
        YHigh = b[5] & 0x3F;
        FgOffsetNegative = (b[5] >> 6) & 0x01;
        ReservedYHigh = (b[5] >> 7) & 0x01;
        if (b.Length < 10) return;
        MidwayAction = b[6] & 0x07;
        MidwayXHigh = (b[6] >> 3) & 0x01;
        MidwayScreenHigh = (b[6] >> 4) & 0x01;
        MidwaySeparate = (b[6] >> 5) & 0x01;
        MidwayActionHigh = (b[6] >> 6) & 0x03;
        MidwayX = b[7] & 0x0F;
        MidwayY = (b[7] >> 4) & 0x0F;
        MidwayFgBg = b[8];
        MidwayYHigh = b[9];
        if (b.Length < 11) return;
        BgHeight = b[10] & 0x3F;
        FaceLeft = (b[10] >> 6) & 0x01;
        FgBgRelative = (b[10] >> 7) & 0x01;
        if (b.Length < 12) return;
        HeightIndex = b[11] & 0x1F;
        ExtendedSprites = (b[11] >> 5) & 0x01;
        HeightReserved = (b[11] >> 6) & 0x01;
        VerticalPositioning = (b[11] >> 7) & 0x01;
    }

    /// <summary>Re-pack into the twelve table bytes — the exact inverse of the decode.</summary>
    public byte[] ToBytes() =>
    [
        (byte)((MarioY & 0x0F) | ((Layer2Scroll & 0x0F) << 4)),
        (byte)((MarioX & 0x07) | ((EntranceAction & 0x07) << 3) | ((Layer3Option & 0x03) << 6)),
        (byte)((VerticalScroll & 0x03) | ((ScreenBoundaryY & 0x03) << 2) | ((ReservedBoundary & 0x0F) << 4)),
        (byte)((ReservedMode & 0x1F) | ((VerticalLevel & 0x03) << 5) | ((SkipEntranceWalk & 0x01) << 7)),
        (byte)((SpriteSpawnRange & 0x03) | ((SmartSpawn & 0x01) << 2) | ((XHigh & 0x03) << 3)
               | ((Method2 & 0x01) << 5) | ((ActionHigh & 0x03) << 6)),
        (byte)((YHigh & 0x3F) | ((FgOffsetNegative & 0x01) << 6) | ((ReservedYHigh & 0x01) << 7)),
        (byte)((MidwayAction & 0x07) | ((MidwayXHigh & 0x01) << 3) | ((MidwayScreenHigh & 0x01) << 4)
               | ((MidwaySeparate & 0x01) << 5) | ((MidwayActionHigh & 0x03) << 6)),
        (byte)((MidwayX & 0x0F) | ((MidwayY & 0x0F) << 4)),
        (byte)MidwayFgBg,
        (byte)MidwayYHigh,
        (byte)((BgHeight & 0x3F) | ((FaceLeft & 0x01) << 6) | ((FgBgRelative & 0x01) << 7)),
        (byte)((HeightIndex & 0x1F) | ((ExtendedSprites & 0x01) << 5) | ((HeightReserved & 0x01) << 6)
               | ((VerticalPositioning & 0x01) << 7)),
    ];
}
