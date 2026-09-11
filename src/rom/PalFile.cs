namespace PipeDream;

/// <summary>
/// Lunar Magic's palette FILES — both kinds, decoded 2026-09-11 against LM 3.40's own output
/// (reference/LM_FILE_FORMATS.md).
///
/// They are not the same thing and LM keeps them on separate menu commands:
///   * the SHARED palettes, `smw.pal` — every level's common palette data, and nothing
///     level-specific. LM's "Extract/Insert Shared Palettes to ROM".
///   * a LEVEL's palette, a 256-colour `.pal` in YY-CHR's layout. LM's "Export/Import Level
///     Palette to File", which also offers TPL and its own MW3; only `.pal` is handled here
///     because it is the one every tile editor reads.
/// </summary>
public static class PalFile
{
    /// <summary>
    /// The shared palettes are a flat ROM range, and LM's file is that range verbatim: a
    /// `-ExportSharedPalette` of a prepped base came out byte-for-byte equal to `$00B0A0`
    /// onwards for its whole length. That length is the tell that this is the whole of SMW's
    /// shared palette data — `$00B0A0`-`$00B881`, back-area colours through the last sprite row.
    /// </summary>
    public const int SharedSnes = 0x00B0A0, SharedSize = 0x7E2;

    public static byte[] ExportShared(Rom rom) =>
        rom.Data.AsSpan(rom.FileOffset(SharedSnes), SharedSize).ToArray();

    /// <summary>Returns an error message, or null on success.</summary>
    public static string? ImportShared(Rom rom, byte[] file)
    {
        if (file.Length != SharedSize)
            return $"not a shared palette file: expected {SharedSize} bytes, got {file.Length}.";
        file.CopyTo(rom.Data, rom.FileOffset(SharedSnes));
        return null;
    }

    /// <summary>256 colours, three bytes each, red first — YY-CHR's layout and LM's default.</summary>
    public const int LevelSize = 256 * 3;

    /// <summary>
    /// A level's palette as LM writes it. Each 5-bit SNES channel is shifted left 3 and NOT
    /// bit-replicated: LM's own files put 0x1D out as 0xE8, where <see cref="Palette.ToRgba"/>
    /// would say 0xEF. So this goes through the raw BGR555 words rather than the display RGBA —
    /// matched against DogsOfWar's `107.pal`, which LM produced.
    /// </summary>
    public static byte[] ExportLevel(Rom rom, int level)
    {
        var pal = Palette.Load(rom, LevelParser.Parse(rom, level).Header, level);
        var file = new byte[LevelSize];
        for (int i = 0; i < 256; i++)
        {
            ushort c = pal.Bgr[i];
            file[i * 3] = (byte)((c & 0x1F) << 3);
            file[i * 3 + 1] = (byte)((c >> 5 & 0x1F) << 3);
            file[i * 3 + 2] = (byte)((c >> 10 & 0x1F) << 3);
        }
        return file;
    }

    /// <summary>
    /// ...and back, into the level's LM custom palette. LM says it turns the custom-palette
    /// setting on for a level that had none, and <see cref="LunarMagic.WriteLmCustomPalette"/>
    /// is that same act: the blob IS the setting, pointed at from `$0EF600` (CONTRACT §7e).
    /// Colour 0 of the file becomes the back-area colour, which is where the palette keeps it.
    /// </summary>
    public static string? ImportLevel(Rom rom, int level, byte[] file)
    {
        if (file.Length != LevelSize)
            return $"not a 256-colour .pal: expected {LevelSize} bytes, got {file.Length}.";
        var colors = new ushort[256];
        for (int i = 0; i < 256; i++)
            colors[i] = (ushort)(file[i * 3] >> 3
                               | (file[i * 3 + 1] >> 3) << 5
                               | (file[i * 3 + 2] >> 3) << 10);
        try { rom.WriteLmCustomPalette(level, colors[0], colors); }
        catch (InvalidOperationException e) { return e.Message; }
        return null;
    }
}
