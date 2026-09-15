namespace PipeDream;

/// <summary>
/// What each value of a level's bounded header/entrance fields MEANS, so the properties dialog
/// can offer a list of choices instead of a number nobody can read.
///
/// Every meaning here is either read from the ROM's own table (music, layer 2 scroll, the timer,
/// the level height LUT) or taken from the disassembly site that consumes the field — cited on
/// each member. Nothing is invented: a field whose values have no names stays a number, and a
/// ROM that has moved a table is followed, not assumed.
/// </summary>
public static class LevelOptions
{
    // ---- header fields ----

    /// <summary>Header byte 4 bits 6-7 → <c>TimerTable</c> $0584D7, whose byte is the timer's
    /// hundreds digit (decoded at $058583). Vanilla reads 00/02/03/04.</summary>
    public static string[] Time(Rom? rom) =>
        [.. Enumerable.Range(0, 4).Select(i =>
            (rom is null ? i == 0 ? 0 : i + 1 : rom.ReadByte(0x0584D7 + i)) is var h && h == 0
                ? $"{i} — no timer" : $"{i} — {h}00")];

    /// <summary>Header byte 3 bits 4-6 → <c>LevelMusicTable</c> $0584DB, eight track numbers.
    /// The tracks have no names in the ROM — Lunar Magic shows numbers too unless a .msc file
    /// supplies them (info_custom_music_names.htm) — so the number is what we show.</summary>
    public static string[] Music(Rom? rom) =>
        [.. Enumerable.Range(0, 8).Select(i => $"{i} — track {(rom?.ReadByte(0x0584DB + i) ?? 0):X2}")];

    /// <summary>Header byte 5 bits 4-5 → $1412, read at $0585B6: 3 zeroes $1411 as well, and the
    /// scroller at $00F7F4/$00F85E branches on 0 (off), 1 (free) and the rest (only while the
    /// level's own vertical-scroll flag $13F1 is up — flying, climbing, swimming).</summary>
    public static readonly string[] VerticalScroll =
    [
        "0 — none",
        "1 — at will",
        "2 — only while flying or climbing",
        "3 — none, and no horizontal scrolling",
    ];

    /// <summary>Header byte 5 bits 6-7 → $13BE, the slot the game records collected items in.
    /// Index 3 was unimplemented in the original game; the hack Lunar Magic inserts makes it
    /// "track nothing", so those items always come back (level_change_properties.htm).</summary>
    public static readonly string[] ItemMemory =
    [
        "0 — slot 0",
        "1 — slot 1",
        "2 — slot 2",
        "3 — track nothing (items respawn)",
    ];

    /// <summary>Header byte 2 bits 0-4 → $1925, which indexes every level-mode table in bank 05.
    /// Vertical comes from <c>VerticalTable</c> $058417 bit 0; the rest of the classification is
    /// Lunar Magic's own (level_change_properties.htm): 09/0B/10 are boss rooms with no object
    /// data, 03-06 are the mixed memory maps Nintendo abandoned, 12-1D are unused and will crash.
    /// </summary>
    public static string[] LevelMode(Rom? rom) =>
        [.. Enumerable.Range(0, 32).Select(m =>
        {
            bool vertical = rom is not null && (rom.ReadByte(0x058417 + m) & 1) != 0;
            string note = m is 0x09 or 0x0B or 0x10 ? "boss room, no objects"
                        : m is >= 0x12 and <= 0x1D ? "unused — do not use"
                        : m is >= 0x03 and <= 0x06 ? "mixed map, layer 2 misplaced"
                        : (vertical ? "vertical" : "horizontal")
                          + (Layer2Objects(m) ? ", layer 2 objects" : "")
                          + (m is 0x1E or 0x1F ? ", translucent" : "");
            return $"{m:X2} — {note}";
        })];

    /// <summary>The modes that get a second object pass, so layer 2 carries level data rather
    /// than a background image (LEVEL_PIPELINE_NOTES §C).</summary>
    private static bool Layer2Objects(int mode) => mode is not (0x0A or 0x0C or 0x0D or 0x0E or 0x11 or 0x1E);

    // ---- entrance fields ----

    /// <summary>$05F000 bits 4-7 → one index into BOTH rate tables, $05D720 for horizontal
    /// ($1413) and $05D710 for vertical ($1414). The scroller at $00F79D divides the screen
    /// position by the rate: 0 fixes the layer, 1 is 1:1, 2 is 1:2, and 3 (vertical only) 1:16.
    /// </summary>
    public static string[] Layer2Scroll(Rom? rom) =>
        [.. Enumerable.Range(0, 16).Select(i =>
        {
            int h = rom?.ReadByte(0x05D720 + i) ?? 0, v = rom?.ReadByte(0x05D710 + i) ?? 0;
            return $"{i:X} — H {Rate(h)}, V {Rate(v)}";
        })];

    private static string Rate(int r) => r switch { 0 => "fixed", 1 => "1:1", 2 => "1:2", _ => "1:16" };

    /// <summary>LM's $05DE00 bits 0-1 → $0BF4: how far above and below the screen a sprite may
    /// spawn (level_change_sprite.htm).</summary>
    public static readonly string[] SpriteSpawnRange =
    [
        "0 — horizontal level (12-13 tiles)",
        "1 — vertical level (3-4 tiles)",
        "2 — enhanced vertical level (8-9 tiles)",
        "3 — infinity (the level's full height)",
    ];

    /// <summary>LM's height byte bits 0-4 → its 32-entry LUT, in Map16 rows. A base without LM's
    /// level-height engine has only vanilla's 27 rows, so the list is that one entry.</summary>
    public static string[] LevelHeight(Rom? rom)
    {
        if (rom is null || !rom.HasLmLevelHeight) return ["0 — 27 rows (vanilla)"];
        return [.. Enumerable.Range(0, 32).Select(i =>
            $"{i:X2} — {rom.ReadValue(rom.LmLevelHeightTable + 0x200 + i * 2, 2) >> 4} rows")];
    }
}
