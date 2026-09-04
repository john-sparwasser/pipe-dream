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
    // The placeholder gets its own blit: sharing one would have the two draws, at two sizes,
    // rebuild each other's intermediates every frame.
    private readonly PixelBlit blit = new(), tailBlit = new(), dragBlit = new(), holeBlit = new();
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
    /// field of these, the way LM shows its unused pages, and painting one creates the page.
    /// Kept as a whole PAGE (16x16 of the tile) that PixelBlit repeats down the empty tail of
    /// the bank — one blit, snapped like the sheet's own. A tiled ImageBrush rounded its
    /// period to whole device pixels and drifted off the grid at any fractional zoom, which
    /// is every zoom the fit-to-width drawer has.</summary>
    public void SetPlaceholder(uint[]?[] px)
    {
        for (int p = 0; p < 4; p++) placeholder[p] = px[p] is { } img ? LevelBitmap.FromPixels(Page(img), 256, 256) : null;
    }

    private static uint[] Page(uint[] tile)
    {
        var page = new uint[256 * 256];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++) page[y * 256 + x] = tile[(y & 15) * 16 + (x & 15)];
        return page;
    }

    private static readonly Pen PageLine = new(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));

    /// <summary>Draw <paramref name="bank"/> at <paramref name="tile"/> screen pixels per tile,
    /// from the owner's origin.</summary>
    private static readonly IBrush PageBrush = new SolidColorBrush(Color.Parse("#205C99"));
    private static readonly Pen PagePen = new(PageBrush, 2);

    /// <summary>The "Pages" overlay: a square around each Map16 page plus its number, for when
    /// the faint separators are not enough to keep track of which page a tile lands on. Drawn
    /// over <see cref="Draw"/> by whichever view has the toggle on.</summary>
    public static void DrawPages(DrawingContext ctx, int bank, double tile)
    {
        int perBank = Map16Layout.BankTiles / 0x100;
        for (int page = 0; page < perBank; page++)
        {
            double y = page * 16 * tile;
            ctx.DrawRectangle(null, PagePen, new Rect(1, y + 1, 16 * tile - 2, 16 * tile - 2));
            var ft = Overlay.Text($"{bank * perBank + page:X2}", 12);
            var badge = new Rect(2, y + 2, ft.Width + 10, ft.Height + 6);
            ctx.FillRectangle(PageBrush, badge);
            Overlay.DrawText(ctx, ft, 12, badge.X + 5, badge.Center.Y);
        }
    }

    /// <summary>A rectangle of <paramref name="bank"/>'s tiles, in QUADRANTS, drawn into
    /// <paramref name="dst"/>: the selection being dragged, shown where it would land. Only the
    /// part the sheet has pixels for — an empty page has none to carry.</summary>
    public void DrawQuads(Control owner, DrawingContext ctx, int bank, int phase,
                          int qx, int qy, int qw, int qh, Rect dst)
    {
        var (bmp, sw, sh, top) = bank == 2 ? (bgSheet.For(phase), bgW, bgH, 0)
                                           : (sheet.For(phase), w, h, bank * Map16Layout.BankRows * 16);
        if (bmp is null || sh <= 0) return;
        var src = new Rect(qx * 8, top + qy * 8, qw * 8, qh * 8).Intersect(new Rect(0, 0, sw, sh));
        if (src.Width < 1 || src.Height < 1) return;
        double z = dst.Width / (qw * 8);
        dragBlit.Draw(owner, ctx, bmp, src, new Rect(dst.X, dst.Y, src.Width * z, src.Height * z),
                      TopLevel.GetTopLevel(owner)?.RenderScaling ?? 1);
    }

    /// <summary>What a rectangle of tiles looks like once moved away — the empty-page tile, which
    /// is what a move leaves behind — drawn over <paramref name="dst"/>, the dragged selection's
    /// origin. Black in the BG bank, whose empty tiles have no placeholder.</summary>
    public void DrawEmpty(Control owner, DrawingContext ctx, int bank, int phase, int qw, int qh, Rect dst)
    {
        if (bank == 2 || placeholder[phase & 3] is not { } ph) { ctx.FillRectangle(Brushes.Black, dst); return; }
        holeBlit.Draw(owner, ctx, ph, new Rect(0, 0, qw * 8, qh * 8), dst, TopLevel.GetTopLevel(owner)?.RenderScaling ?? 1);
    }

    public void Draw(Control owner, DrawingContext ctx, int bank, int phase, double tile)
    {
        var full = new Rect(0, 0, Map16Layout.Cols * tile, Map16Layout.BankRows * tile);
        // Empty pages are ordinary black tiles, not a roped-off region: painting one is what
        // brings it into existence, so nothing here may make it look unavailable.
        ctx.FillRectangle(Brushes.Black, full);
        if (bank < 2 && placeholder[phase & 3] is { } ph)
        {
            // Only the pages the sheet does not reach; the sheet is opaque over the rest.
            int from = (h > 0 ? Map16Layout.SheetWindow(bank, h, TileCount).Rows : 0) / 16 * 16;
            int rows = Map16Layout.BankRows - from;
            if (rows > 0)
                tailBlit.Draw(owner, ctx, ph, new Rect(0, 0, 256, rows * 16),
                              new Rect(0, from * tile, Map16Layout.Cols * tile, rows * tile),
                              TopLevel.GetTopLevel(owner)?.RenderScaling ?? 1);
        }

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
