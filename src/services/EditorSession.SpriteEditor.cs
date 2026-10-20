namespace PipeDream.Services;

// EditorSession — the custom sprite editor (ui/SpriteConfigWindow, reference/PIXI_PORT.md phase 4):
// a library sprite's config as a model to edit and save, and the pictures the window draws it
// with — a display's tiles, Lunar Magic's sprite Map16 with the sprite's own tiles from 0x300,
// and the 8x8 tiles those are built from — all in this level's sprite GFX and palettes. The
// library itself is EditorSession.CustomSprites.cs.
public sealed partial class EditorSession
{
    /// <summary>A library sprite's config, read; null when there is no such sprite or PIXI could
    /// not read it (the text editor is the way to fix that).</summary>
    public PixiConfig? CustomSpriteModel(string name)
    {
        try { return FindSprite(name) is { } s ? PixiConfig.Parse(s.Config) : null; }
        catch (FormatException) { return null; }
    }

    /// <summary>Save an edited config, as the JSON PIXI reads.</summary>
    public string SetCustomSpriteConfig(string name, PixiConfig config) => SetCustomSpriteConfig(name, config.ToJson());

    /// <summary>Display tiles drawn on a clear w×h canvas with the placement cell's top-left at (ox, oy).</summary>
    public uint[] DrawSpriteDisplay(IEnumerable<PixiConfig.Tile> tiles, byte[] own, int w, int h, int ox, int oy)
    {
        var img = new uint[w * h];
        if (Map16Tiles() is not { } t || Scene?.Palettes[0] is not { } p) return img;
        var d = new PixiConfig.Display { Tiles = [.. tiles] };
        SpriteRender.Draw(img, w, h, SpriteMap16.Oam(d, own, ox, oy), t, p);
        return img;
    }

    /// <summary>The whole sprite Map16, 0x000-0x3FF, sixteen 16x16 tiles to a row, the sprite's own
    /// from 0x300.</summary>
    public (uint[] Px, int W, int H) SpriteMap16Sheet(byte[] own)
    {
        const int w = 16 * 16, h = SpriteMap16.Count / 16 * 16;
        var img = new uint[w * h];
        Array.Fill(img, 0xFF303030u);
        if (Map16Tiles() is not { } t || Scene?.Palettes[0] is not { } p) return (img, w, h);
        var d = new PixiConfig.Display();
        for (int n = 0; n < SpriteMap16.Count; n++)
        {
            d.Tiles = [new PixiConfig.Tile(0, 0, n)];
            SpriteRender.Draw(img, w, h, SpriteMap16.Oam(d, own, n % 16 * 16, n / 16 * 16), t, p);
        }
        return (img, w, h);
    }

    /// <summary>The 8x8 tiles sprite Map16 builds from — SP1-SP4 (000-1FF), then GFX33 (200-2FF) —
    /// sixteen to a row, in sprite palette 8+pal.</summary>
    public (uint[] Px, int W, int H) SpriteMap16Gfx(int pal)
        => Map16Tiles() is { } t && Scene?.Palettes[0] is { } p ? GfxSheets.Tiles(t, p, 8 + (pal & 7)) : ([], 0, 0);

    private byte[][]? Map16Tiles()
        => Rom is { } r && Scene is { } s ? SpriteRender.LoadMap16Tiles(r, SpriteRender.ResolveSpFiles(r, s.Level.Header, LevelNum)) : null;
}
