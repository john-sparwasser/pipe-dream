namespace PipeDream;

/// <summary>
/// Lunar Magic's names for the level header's two graphics settings, as its Change Properties
/// dialog lists them: the FG/BG GFX setting (the header's tileset, "layer 1") and the sprite GFX
/// setting, by setting number 0-F. Taken from LM's exe, never guessed; "" is a setting LM gives
/// no name, and the graphics-header dialog then shows the number and its GFX files alone.
///
/// Read off LM 3.40's Level ▸ GFX Index in header dialog by tools/lm/Update-GfxSettingNames.ps1,
/// which rewrites this file; the parenthesised name of each entry is what is kept.
/// </summary>
public static class GfxSettingNames
{
    public static readonly string[] Layer1 =
    [
        "Normal 1", "Castle 1", "Rope 1", "Underground 1", "Switch Palace 1", "Ghost House 1", "Rope 2", "Normal 2",
        "Rope 3", "Underground 2", "Switch Palace 2", "Castle 2", "Cloud/Forest", "Ghost House 2", "Underground 3", "",
    ];

    public static readonly string[] Sprites =
    [
        "Forest", "Castle", "Mushroom", "Underground", "Water", "Pokey", "Underground 2", "Ghost House",
        "Banzai Bill", "Yoshi's House", "Dino-Rhino", "Switch Palace", "Mecha-Koopa", "Wendy/Lemmy", "Ninji", "Not Used",
    ];
}