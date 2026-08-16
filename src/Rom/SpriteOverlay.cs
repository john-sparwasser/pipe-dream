namespace PipeDream;

/// <summary>
/// Cached sprite overlay: the expensive part (per-sprite 65816 OAM capture + SP tile
/// decode) runs once in Build; Draw is cheap pixel blits, safe to repeat on a live
/// canvas (used by the editor on every incremental repaint).
/// </summary>
public sealed class SpriteOverlay
{
    private readonly (Sprite s, List<SpriteRender.Oam>? oam)[] items;
    private readonly byte[][]? sp;
    private readonly bool vert;
    private readonly bool pixi;

    private SpriteOverlay((Sprite, List<SpriteRender.Oam>?)[] items, byte[][]? sp, bool vert, bool pixi)
    { this.items = items; this.sp = sp; this.vert = vert; this.pixi = pixi; }

    public static SpriteOverlay Build(Rom rom, SpriteData sprites, LevelHeader h, int level)
    {
        byte[][]? sp = null;
        try { sp = SpriteRender.LoadSpTiles(rom, h, level); } catch { }
        bool vert = rom.IsVerticalMode(h.LevelMode);
        bool pixi = rom.HasPixiSpriteHook;
        var items = new (Sprite, List<SpriteRender.Oam>?)[sprites.Sprites.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var s = sprites.Sprites[i];
            var (cx, cy) = s.Cell(vert);
            List<SpriteRender.Oam>? oam = null;
            // PIXI custom sprites (extra bits 2/3) stay null: their editor look is defined
            // by LM's .ssc/.dsc metadata, which we don't read — Draw shows a red-X box.
            if (sp is not null && !s.IsScrollCommand && !(pixi && s.Extra >= 2))
            {
                if (s.Extra < 2 && SpriteDisplay.TryGet(s.Number, out var rel))
                    oam = rel.Select(o => o with { X = o.X + cx * 16, Y = o.Y + cy * 16 }).ToList();
                else
                    oam = SpriteRender.Capture(rom, s, cx, cy, vert);
            }
            items[i] = (s, oam);
        }
        return new SpriteOverlay(items, sp, vert, pixi);
    }

    /// <summary>Re-resolve the SP tile sheets (e.g. after a GFX override) WITHOUT re-running
    /// the expensive OAM captures — captures produce tile indices, independent of GFX data.</summary>
    public SpriteOverlay WithReloadedTiles(Rom rom, LevelHeader h, int level)
    {
        byte[][]? sp2 = null;
        try { sp2 = SpriteRender.LoadSpTiles(rom, h, level); } catch { }
        return new SpriteOverlay(items, sp2, vert, pixi);
    }

    public void Draw(uint[] img, int W, int H, Palette pal, ISet<int>? skip = null)
    {
        for (int i = 0; i < items.Length; i++)
        {
            if (skip is not null && skip.Contains(i)) continue;   // e.g. sprites being dragged
            var (s, oam) = items[i];
            if (oam is not null && sp is not null) SpriteRender.Draw(img, W, H, oam, sp, pal);
            else if (pixi && s.Extra >= 2 && !s.IsScrollCommand) SpriteData.DrawCustomBox(img, W, H, s, vert);
            else SpriteData.DrawBadge(img, W, H, s, vert);
        }
    }

    /// <summary>Level-pixel bounds of one sprite's tiles (null when badge-only).</summary>
    public (int MinX, int MinY, int MaxX, int MaxY)? PixelBounds(int i)
    {
        var (_, oam) = items[i];
        if (oam is null || oam.Count == 0) return null;
        return (oam.Min(o => o.X), oam.Min(o => o.Y),
                oam.Max(o => o.X + (o.Big ? 16 : 8)), oam.Max(o => o.Y + (o.Big ? 16 : 8)));
    }

    /// <summary>Draw one sprite shifted by (shiftX, shiftY) pixels — for drag ghosts.</summary>
    public void DrawOne(int i, uint[] img, int W, int H, Palette pal, int shiftX, int shiftY)
    {
        var (_, oam) = items[i];
        if (oam is null || sp is null) return;
        SpriteRender.Draw(img, W, H, oam.Select(o => o with { X = o.X + shiftX, Y = o.Y + shiftY }).ToList(), sp, pal);
    }
}
