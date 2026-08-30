namespace PipeDream.Services;

/// <summary>Which entrance a marker is. The three read from different records, and only the
/// secondary one exists per-index rather than per-level.</summary>
public enum EntranceKind { Main, Midway, Secondary }

/// <summary>
/// One place a level puts Mario, resolved to level pixels — the editable half of a connection,
/// where <see cref="LevelExit"/> is the other.
///
/// The position is DERIVED (see <see cref="EntrancePlacement"/>): the ROM stores a screen and
/// two small indices — 16px steps with method 2, one of 8 x 16 table spots without. Moving
/// one snaps, and the snapped position is what comes back.
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

    /// <summary>Whether this base can place the entrance in 16px steps — Lunar Magic's "method 2"
    /// for the main and secondary ones, its "separate midway settings" for the midway; prep v10
    /// installs all three. When it cannot, a drag snaps to vanilla's 8 x 16 grid (or, for the
    /// midway, to a screen).</summary>
    public bool Free { get; init; }

    /// <summary>
    /// Without LM's separate midway settings the midway entrance carries ONLY a screen — its
    /// position within that screen is the main entrance's ($05D9E1 supplies just the screen). So
    /// a midway marker moves sideways a screen at a time and cannot move vertically at all, and
    /// the UI says so rather than pretend otherwise.
    /// </summary>
    public bool ScreenOnly => Kind == EntranceKind.Midway && !Free;
}
