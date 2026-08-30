namespace PipeDream;

/// <summary>One sprite entry (CONTRACT §11). Decoded from the 3-byte format confirmed at
/// $02A82C: b1 = YYYYEEsy, b2 = XXXXSSSS, b3 = sprite number. <paramref name="Band"/> is Lunar
/// Magic's 32-row band from the list's `FF nn` markers (extended list, header bit 5): the
/// sprite's row is Band * 32 + Y, exactly as objects use ext 01/03.</summary>
public readonly record struct Sprite(int Screen, int XNibble, int Y, int Extra, int Number, byte[]? ExtraBytes = null, int Band = 0)
{
    public int AbsoluteX => Screen * 16 + XNibble;
    public int AbsoluteY => Band * 32 + Y;
    /// <summary>Numbers >= 0xE7 are scroll commands, not real sprites ($02A866).</summary>
    public bool IsScrollCommand => Number >= 0xE7;
    /// <summary>
    /// Cell position honoring the level orientation. Vertical levels use the same decode
    /// "with X and Y coords swapped" ($02A943): the Y field becomes the X cell (0-31) and
    /// the screen walk (our AbsoluteX) runs down the level. Bands are a horizontal thing.
    /// </summary>
    public (int X, int Y) Cell(bool vertical) => vertical ? (Y, AbsoluteX) : (AbsoluteX, AbsoluteY);
}
