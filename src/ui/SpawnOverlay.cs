using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PipeDream.Ui;

/// <summary>
/// What a block releases when hit, drawn over it: the sprite's catalog thumbnail, translucent,
/// standing on the block the way it pops out in play. One instance per level — the thumbnails
/// are composed with the level's own sprite graphics — shared by the level canvas, the Map16
/// editor and the Tiles drawer, so the three agree.
///
/// <paramref name="spriteOf"/> answers "which sprite does this tile release" (through the tile's
/// acts-as, so a custom block set to act as 127 shows the shell); <paramref name="thumbOf"/> is
/// the catalog's 32x32 thumbnail for a sprite, drawn with its origin cell at (8,16).
/// </summary>
public sealed class SpawnOverlay(Func<int, int?> spriteOf, Func<int, uint[]?> thumbOf)
{
    private const int Thumb = 32;
    private const double Opacity = 0.7;
    private readonly Dictionary<int, WriteableBitmap?> bitmaps = [];

    public void Draw(Visual owner, DrawingContext ctx, int tile, Rect cell)
    {
        if (spriteOf(tile) is not { } sprite) return;
        if (!bitmaps.TryGetValue(sprite, out var bmp))
            bitmaps[sprite] = bmp = thumbOf(sprite) is { } px ? LevelBitmap.FromPixels(px, Thumb, Thumb) : null;
        if (bmp is null) return;
        // The thumbnail's origin cell is its (8..24, 16..32) square; put that on the block, so a
        // 16px sprite sits on the block and a 32px one stands up out of it.
        double z = cell.Width / 16;
        var dst = new Rect(cell.X - 8 * z, cell.Y - 16 * z, Thumb * z, Thumb * z);
        using (ctx.PushOpacity(Opacity))
            PixelBlit.Icon(owner, ctx, bmp, new Rect(0, 0, Thumb, Thumb), dst);
    }
}
