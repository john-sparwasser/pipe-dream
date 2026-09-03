using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PipeDream.Ui;

/// <summary>
/// One bank of the composed Map16 sheet, as both the Map16 canvas and the drawer's picker draw
/// it: black where there is nothing, LM's default tile tiled across the FG banks' unmade pages,
/// the sheet's window for this bank blitted pixel-sharp, and a separator every page. The two
/// surfaces must look the same because they are the same tiles — this is the one drawing.
///
/// The windowing is deliberately NOT re-derived here — <c>Map16Layout.SheetWindow</c> in the
/// core computes it and is unit-tested, including the case ("bank 1 shows nothing") that shipped
/// broken in the ImGui editor.
/// </summary>
internal sealed class Map16Sheet
{
    private readonly LevelBitmap sheet = new(), bgSheet = new();
    private readonly Bitmap?[] placeholder = new Bitmap?[4];
    private readonly PixelBlit blit = new();
    private int w, h, bgW, bgH;

    public int TileCount { get; private set; }

    public void SetSheet(uint[]?[] px, int w, int h, int tileCount, int phase)
    {
        this.w = w; this.h = h; TileCount = tileCount;
        sheet.SetImages(px, w, h, phase);
    }

    /// <summary>The BG definitions' sheet — bank 2, LM's pages 80-81, our 0x4000-0x41FF. Its own
    /// image because those defs are a fixed table beside the FG sheet, not a window into it.</summary>
    public void SetBgSheet(uint[]?[] px, int w, int h, int phase)
    {
        bgW = w; bgH = h;
        bgSheet.SetImages(px, w, h, phase);
    }

    /// <summary>The empty-page tile per phase: every FG page without defs yet is drawn as a
    /// field of these, the way LM shows its unused pages, and painting one creates the page.</summary>
    public void SetPlaceholder(uint[]?[] px)
    {
        for (int p = 0; p < 4; p++) placeholder[p] = px[p] is { } img ? LevelBitmap.FromPixels(img, 16, 16) : null;
    }

    private static readonly Pen PageLine = new(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));

    /// <summary>Draw <paramref name="bank"/> at <paramref name="tile"/> screen pixels per tile,
    /// from the owner's origin.</summary>
    public void Draw(Control owner, DrawingContext ctx, int bank, int phase, double tile)
    {
        var full = new Rect(0, 0, Map16Layout.Cols * tile, Map16Layout.BankRows * tile);
        // Empty pages are ordinary black tiles, not a roped-off region: painting one is what
        // brings it into existence, so nothing here may make it look unavailable.
        ctx.FillRectangle(Brushes.Black, full);
        if (bank < 2 && placeholder[phase & 3] is { } ph)
            ctx.FillRectangle(new ImageBrush(ph)
            {
                TileMode = TileMode.Tile, Stretch = Stretch.Fill,
                DestinationRect = new RelativeRect(0, 0, tile, tile, RelativeUnit.Absolute),
            }, full);

        if (bank == 2)
        {
            // The BG table fills the first two pages of the bank; the rest is genuinely empty.
            if (bgSheet.For(phase) is { } bg && bgH > 0)
                blit.Draw(owner, ctx, bg, new Rect(0, 0, bgW, bgH),
                          new Rect(0, 0, Map16Layout.Cols * tile, bgH / 16 * tile),
                          TopLevel.GetTopLevel(owner)?.RenderScaling ?? 1);
        }
        else if (sheet.For(phase) is { } bmp && h > 0)
        {
            var (v0, v1, rows, _) = Map16Layout.SheetWindow(bank, h, TileCount);
            if (rows > 0)
                blit.Draw(owner, ctx, bmp, new Rect(0, v0 * h, w, (v1 - v0) * h),
                          new Rect(0, 0, Map16Layout.Cols * tile, rows * tile),
                          TopLevel.GetTopLevel(owner)?.RenderScaling ?? 1);
        }

        // Page separators every 16 rows, LM-style.
        for (int page = 1; page < Map16Layout.BankTiles / 0x100; page++)
            ctx.DrawLine(PageLine, new Point(0, page * 16 * tile), new Point(full.Width, page * 16 * tile));
    }
}
