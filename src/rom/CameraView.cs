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
    /// <param name="LookAhead">Only while L or R is held, so it is drawn as the fainter case.</param>
    public readonly record struct Band(string Name, int From, int To, bool Vertical, bool LookAhead = false);

    // $142A is the centre of the "static camera region", and the two scroll lines are built from
    // it every frame at $00F6E0: left = centre - 0x0C, right = centre + 0x18. The player is held
    // between those two; crossing either is what moves the screen ($00F721-$00F737).
    private const int LeftLine = 0x0C, RightLine = 0x18;

    // Where the centre settles. $00A7B9 starts it at 0x80, but it does not stay there: while the
    // screen is scrolling, $00F8B7 writes 8 or 0x0A into $1400 depending on which side of
    // DATA_00F6B3 the centre is, and $00CDEC reads that back next frame to step the centre 2px
    // toward it ($00CE5E), stopping when it arrives ($00CE65). DATA_00F6B3 is indexed by $13FF
    // (facing x 2): 0x90 facing left, 0x60 facing right. So a level opens at 0x80 and settles to
    // one of these as soon as the player runs.
    private const int CentreFacingRight = 0x60, CentreFacingLeft = 0x90;

    // L/R look-ahead. Holding one drives the centre to DATA_00F6CB's target instead, 2px a frame
    // ($00CE18-$00CE5E). The index is ControllerB's L/R bits shifted down three ($00CE08): R
    // (0x10) gives 2, reading the word at DATA_00F6CB+2 = 0x0020; L (0x20) gives 4, which runs
    // into DATA_00F6CF = 0x00D0. Hold R and the player is pushed left to see right, and the
    // reverse for L — which is why Lunar Magic's help calls these the two bands "on the far
    // sides" (view_game_screen.htm), and that agreement is what pins the index rule.
    private const int CentreLookRight = 0x20, CentreLookLeft = 0xD0;

    // Vertical, at $00F7F4: the player's on-screen Y is measured against 0x70 to pick a
    // direction, then DATA_00F69F's 0x64 (up) or 0x7C (down) is subtracted. Above 0x64 the screen
    // follows up, below 0x7C it follows down; between them neither branch fires, so it holds.
    // Unlike the horizontal centre this one never drifts — the two are immediates in the table.
    private const int UpLine = 0x64, DownLine = 0x7C;

    // Where sprites come from. LoadSprFromLevel ($02A7FB, every even frame) checks ONE 16px
    // column per frame and loads every listed sprite whose column matches: $1A plus
    // DATA_02A7F6[$55] in the low byte, masked to 16 ($02A81D), and $1B plus DATA_02A7F9[$55] in
    // the high. $55 is the scroll direction the camera code left behind — 0 heading left/up, 2
    // heading right/down — so the two entries are (0xD0, 0xFF) = -0x30 and (0x20, 0x01) = +0x120.
    // A vertical level runs the same two offsets down $1C/$1D instead ($02A809).
    private const int SpawnBehind = -0x30;                    // three tiles past the leading edge...
    private const int SpawnAhead = 0x120;                     // ...two past the trailing one: the screen is 0x100 wide
    public const int SpawnColumn = 16;

    /// <summary>The two spawn columns for a camera whose left edge is at <paramref name="cameraX"/>
    /// level pixels, aligned to 16 the way the loader aligns them — so an 8-snapped camera lands
    /// on the column the game would actually test, not a half-tile beside it.</summary>
    public static (int Left, int Right) SpawnColumns(int cameraX)
        => ((cameraX + SpawnBehind) & ~0xF, (cameraX + SpawnAhead) & ~0xF);

    /// <summary>The same two offsets down the screen, for a vertical level. The screen is only
    /// 0xE0 tall, so the lower row sits four tiles under it rather than two.</summary>
    public static (int Above, int Below) SpawnRows(int cameraY)
        => ((cameraY + SpawnBehind) & ~0xF, (cameraY + SpawnAhead) & ~0xF);

    /// <summary>The bands, in on-screen pixels from the top left of the visible screen. Each is
    /// the span the player can move through WITHOUT the screen following — its edges are what
    /// the scroll code compares against.</summary>
    public static Band[] Bands =>
    [
        new("Facing right", CentreFacingRight - LeftLine, CentreFacingRight + RightLine, true),
        new("Facing left", CentreFacingLeft - LeftLine, CentreFacingLeft + RightLine, true),
        new("Looking right (R held)", CentreLookRight - LeftLine, CentreLookRight + RightLine, true, true),
        new("Looking left (L held)", CentreLookLeft - LeftLine, CentreLookLeft + RightLine, true, true),
        new("Vertical", UpLine, DownLine, false),
    ];
}
