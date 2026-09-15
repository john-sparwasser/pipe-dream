namespace PipeDream;

/// <summary>
/// Where the camera puts the player, for the level editor's camera view — Lunar Magic's
/// View ▸ Game View Screen (F3).
///
/// The SNES shows 256x224 of the level at a time, and the scroll code does not keep the player
/// centred: it holds him inside a band and only moves the screen when he leaves it. Those bands
/// are what make a jump readable or blind, so they are worth seeing while building.
///
/// Every number here is a ROM constant, cited to the instruction that uses it. They are baked
/// rather than read because they are immediates in the scroll routine, not a table a hack can
/// repoint — changing them means changing the code, at which point the editor is wrong anyway.
/// </summary>
public static class CameraView
{
    /// <summary>The visible screen, in level pixels.</summary>
    public const int ScreenWidth = 256;
    public const int ScreenHeight = 224;

    /// <summary>A band the player is held in, in on-screen pixels, and what leaving it does.</summary>
    /// <param name="Name">For the tooltip and the tests.</param>
    /// <param name="Vertical">True for a vertical strip (a horizontal-scroll band).</param>
    public readonly record struct Band(string Name, int From, int To, bool Vertical);

    // $142A is the centre of the "static camera region" ($00A7B9 starts it at 0x80), and the two
    // scroll lines are built from it every frame at $00F6E0: left = centre - 0x0C, right =
    // centre + 0x18. The centre itself follows which way the player faces, through DATA_00F6B3
    // indexed by $13FF (direction x 2): 0x90 facing left, 0x60 facing right.
    private const int LeftLine = 0x0C, RightLine = 0x18;
    private const int CentreFacingRight = 0x60, CentreFacingLeft = 0x90;

    // Vertical, at $00F7F4: the player's on-screen Y is measured against 0x70 to pick a
    // direction, then DATA_00F69F's 0x64 (up) or 0x7C (down) is subtracted to get the distance
    // the screen has to make up. Between those two lines neither applies, so the screen holds.
    private const int UpLine = 0x64, DownLine = 0x7C;

    /// <summary>
    /// The bands, in on-screen pixels from the top left of the visible screen.
    ///
    /// Not here: the two extra bands Lunar Magic draws for the L/R look-ahead. Their targets are
    /// DATA_00F6CB, reached at $00CE1B with the controller byte itself as the index, and reading
    /// that table needs the index rule pinned rather than assumed — see reference/CONTRACT.md.
    /// </summary>
    public static Band[] Bands =>
    [
        new("Scrolling right", CentreFacingRight - LeftLine, CentreFacingRight + RightLine, true),
        new("Scrolling left", CentreFacingLeft - LeftLine, CentreFacingLeft + RightLine, true),
        new("No vertical scroll", UpLine, DownLine, false),
    ];
}
