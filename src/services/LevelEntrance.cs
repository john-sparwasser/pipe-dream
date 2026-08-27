namespace PipeDream.Services;

/// <summary>Which entrance a marker is. The three read from different records, and only the
/// secondary one exists per-index rather than per-level.</summary>
public enum EntranceKind { Main, Midway, Secondary }

/// <summary>
/// One place a level puts Mario, resolved to level pixels — the editable half of a connection,
/// where <see cref="LevelExit"/> is the other.
///
/// The position is DERIVED (see <see cref="EntrancePlacement"/>): the ROM stores a screen and
/// two small indices, so a marker can only sit at one of 8 x 16 spots per screen. Moving one
/// snaps, and the snapped position is what comes back.
/// </summary>
public sealed record LevelEntrance(EntranceKind Kind, int Index, int X, int Y)
{
    /// <summary>What to write beside the marker. A secondary entrance is known by its record
    /// number, which is what an exit points at.</summary>
    public string Label => Kind switch
    {
        EntranceKind.Main => "main",
        EntranceKind.Midway => "mid",
        _ => $"{Index:X3}",
    };

    /// <summary>
    /// Vanilla's midway entrance carries ONLY a screen — its position within that screen is the
    /// main entrance's ($05D9E1 overrides just the X high byte). So a midway marker moves
    /// sideways a screen at a time and cannot move vertically at all.
    ///
    /// This is a limit of the DATA, not of the idea: Lunar Magic ships an enhancement that gives
    /// the midway its own settings, and another that drops the position tables entirely
    /// (reference/LM_PARITY.md). Neither is installed in a vanilla or prepped ROM, so until one
    /// is, this is the truth and the UI should say so rather than pretend otherwise.
    /// </summary>
    public bool ScreenOnly => Kind == EntranceKind.Midway;
}
