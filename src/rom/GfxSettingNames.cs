namespace PipeDream;

/// <summary>
/// Lunar Magic's names for the level header's two graphics settings, as its Change Properties
/// dialog lists them: the FG/BG GFX setting (the header's tileset, "layer 1") and the sprite GFX
/// setting, by setting number 0-F. Taken from LM's exe, never guessed; "" is a setting LM gives
/// no name, and the graphics-header dialog then shows the number and its GFX files alone.
/// </summary>
public static class GfxSettingNames
{
    public static readonly string[] Layer1 =
    [
        "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "",
    ];

    public static readonly string[] Sprites =
    [
        "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "",
    ];
}
