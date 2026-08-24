namespace PipeDream.Services;

/// <summary>
/// Pixel sheets the drawer picks from. Composing them needs the ROM, the level's GFX slots and
/// a palette, which is exactly why it cannot live in a view: a control that loaded graphics
/// itself would be a control that only works with a ROM behind it.
/// </summary>
public static class GfxSheets
{
    public const int ChrCols = 16, ChrCount = 0x400;

    /// <summary>
    /// The level's 8x8 GFX as one sheet, 16 tiles per row, drawn in a single palette row.
    ///
    /// An 8x8 tile has no palette of its own — the palette comes from the Map16 word it lands
    /// in — so the row is chosen by the caller and travels with the brush. Colour 0 is drawn as
    /// flat grey rather than transparent: in a Map16 def it means "show what is behind", and a
    /// checkerboard there would read as part of the tile.
    /// </summary>
    public static (uint[] Px, int W, int H) Chr(Rom rom, LevelHeader header, int level, int phase,
                                               Palette palette, int palRow)
        => Chr(Gfx.FgTiles.Load(rom, header.Tileset, level, phase), palette, palRow);

    /// <summary>The same, from graphics already loaded — the scene keeps one set per animation
    /// phase, and the picker animates with the level off exactly those.</summary>
    public static (uint[] Px, int W, int H) Chr(Gfx.FgTiles fg, Palette palette, int palRow)
    {
        int w = ChrCols * 8, h = ChrCount / ChrCols * 8;
        var px = new uint[w * h];
        for (int t = 0; t < ChrCount; t++)
        {
            var src = fg.Fetch(t);
            int ox = t % ChrCols * 8, oy = t / ChrCols * 8;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int idx = src[y * 8 + x];
                    px[(oy + y) * w + ox + x] = idx == 0 ? 0xFF303030u : palette.Rgba[palRow * 16 + idx];
                }
        }
        return (px, w, h);
    }
}
