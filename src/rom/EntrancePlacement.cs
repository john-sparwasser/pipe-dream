namespace PipeDream;

/// <summary>
/// Where an entrance actually puts Mario, in level pixels.
///
/// No entrance record stores a position. It stores INDICES into four small tables in bank 05,
/// plus a screen — and the three kinds of entrance assemble those three pieces from different
/// bits, which is why this lives in one place rather than in each record type.
///
/// From the decode ($05D7D9 secondary, $05D909-$05DA05 main/midway):
///
///   X low   `DATA_05D750,X` → `$94`, X index is 3 bits, so eight positions across a screen
///   X high  the SCREEN, and every kind keeps it somewhere different:
///             main       `$05F600` bits 0-4   (applied at $05D9EC: `LDA $01 : AND #$1F`)
///             midway     `$05F400` bits 4-7   (applied at $05D9E1: `LDA $02 : LSR x4`)
///             secondary  `$05FC00` bits 0-4   (stashed to `$01`, applied by the same tail)
///           `DATA_05D758` supplies a high byte too, but the tail overwrites it for horizontal
///           levels — which is exactly why a screen field exists at all.
///   Y       `DATA_05D730`/`DATA_05D740` → `$96`/`$97`, Y index is 4 bits: sixteen positions,
///           full 16-bit so it spans the level's height.
///
/// The consequence for an editor: a marker cannot be dragged anywhere. It snaps to one of
/// 8 x 16 positions per screen, because that is all the ROM can express.
/// </summary>
public static class EntrancePlacement
{
    public const int XTable = 0x05D750;                     // 8 entries, low byte
    public const int YTableLo = 0x05D730, YTableHi = 0x05D740;   // 16 entries
    public const int XCount = 8, YCount = 16, ScreenCount = 0x20;

    /// <summary>Level-pixel X for a screen and an X index.</summary>
    public static int X(Rom rom, int screen, int xIndex)
        => (screen & 0x1F) * 0x100 + rom.ReadByte(XTable + (xIndex & 7));

    /// <summary>Level-pixel Y for a Y index.</summary>
    public static int Y(Rom rom, int yIndex)
        => rom.ReadByte(YTableLo + (yIndex & 15)) | (rom.ReadByte(YTableHi + (yIndex & 15)) << 8);

    /// <summary>The (screen, index) pair whose X lands nearest <paramref name="px"/>. Searched
    /// over every combination rather than derived: the eight offsets are not evenly spaced, so
    /// "the screen you dropped it on" is not always the screen that gets closest.</summary>
    public static (int Screen, int Index) NearestX(Rom rom, int px)
    {
        (int screen, int index, int gap) best = (0, 0, int.MaxValue);
        for (int s = 0; s < ScreenCount; s++)
            for (int i = 0; i < XCount; i++)
            {
                int gap = Math.Abs(X(rom, s, i) - px);
                if (gap < best.gap) best = (s, i, gap);
            }
        return (best.screen, best.index);
    }

    /// <summary>The Y index landing nearest <paramref name="py"/>.</summary>
    public static int NearestY(Rom rom, int py)
    {
        (int index, int gap) best = (0, int.MaxValue);
        for (int i = 0; i < YCount; i++)
        {
            int gap = Math.Abs(Y(rom, i) - py);
            if (gap < best.gap) best = (i, gap);
        }
        return best.index;
    }
}
