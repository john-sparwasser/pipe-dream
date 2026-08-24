using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PipeDream.Ui;

/// <summary>
/// The Map16 tile picker that lives in the left drawer: the composed tile sheet, 16 per row,
/// click to select. Same grammar as the ImGui editor — what you pick here is what you paint
/// in the canvas.
///
/// The sheet is one bitmap covering every tile; a bank is a slice of it. That windowing is
/// deliberately NOT re-derived here — <c>Map16Layout.SheetWindow</c> in the core already
/// computes it and is unit-tested, including the case ("bank 1 shows nothing") that shipped
/// broken in the ImGui editor.
/// </summary>
public class Map16PaletteView : Control
{
    private readonly LevelBitmap sheet = new();
    private int sheetW, sheetH;

    /// <summary>Which animation phase to draw. The window steps it for every surface at once,
    /// so a tile animates the same here as it does in the level.</summary>
    public int Phase { get; set; }

    public double Zoom { get; set; } = 2.0;
    public int Bank { get; set; }
    public int TileCount { get; private set; }

    /// <summary>Selected Map16 tile number, in the unified numbering the level canvas uses.</summary>
    public int Selected { get; private set; } = 0x100;

    public event EventHandler<int>? SelectionChanged;

    public Map16PaletteView() => Focusable = true;

    public void SetSheet(uint[]?[] px, int w, int h, int tileCount)
    {
        sheetW = w; sheetH = h; TileCount = tileCount;
        sheet.SetImages(px, w, h, Phase);
        InvalidateVisual();
        InvalidateMeasure();
    }

    /// <summary>Tile under a screen point, or null past the end of the bank.</summary>
    public int? TileAt(Point p)
    {
        if (Zoom <= 0) return null;
        int col = (int)(p.X / (16 * Zoom)), row = (int)(p.Y / (16 * Zoom));
        if (col is < 0 or >= 16 || row < 0) return null;
        int idx = row * 16 + col;
        return idx >= Map16Layout.BankTiles ? null : Bank * Map16Layout.BankTiles + idx;
    }

    /// <summary>Margin around the sheet inside the drawer (matches the XAML).</summary>
    public const double Pad = 8;

    /// <summary>
    /// How wide the picker's CONTENT is at a given tile zoom: a full row of Map16 tiles plus
    /// its margins. The drawer is sized from this rather than from a guessed constant —
    /// a hardcoded width clips the right-hand tiles the moment the tile size changes, which
    /// is not a rendering bug the user can diagnose, it just looks like missing tiles.
    /// </summary>
    public static double ContentWidth(double zoom) => Map16Layout.Cols * 16 * zoom + Pad * 2;

    protected override Size MeasureOverride(Size availableSize)
        => new(Map16Layout.Cols * 16 * Zoom, Map16Layout.BankRows * 16 * Zoom);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (TileAt(e.GetPosition(this)) is { } tile)
        {
            Selected = tile;
            SelectionChanged?.Invoke(this, tile);
            InvalidateVisual();
        }
    }

    private readonly PixelBlit blit = new();

    public override void Render(DrawingContext ctx)
    {
        double z = Zoom, cell = 16 * z;
        var full = new Rect(0, 0, 16 * cell, Map16Layout.BankRows * cell);
        // Empty pages are ordinary black tiles, not a roped-off region: painting one is what
        // brings it into existence, so the drawer must not make it look unavailable.
        ctx.FillRectangle(Brushes.Black, full);

        if (sheet.For(Phase) is { } bmp && sheetH > 0)
        {
            var (v0, v1, rows, _) = Map16Layout.SheetWindow(Bank, sheetH, TileCount);
            if (rows > 0)
            {
                var src = new Rect(0, v0 * sheetH, sheetW, (v1 - v0) * sheetH);
                blit.Draw(this, ctx, bmp, src, new Rect(0, 0, 16 * cell, rows * cell),
                          VisualRoot?.RenderScaling ?? 1);
            }
        }

        // Page separators every 16 rows, LM-style — the drawer's only orientation cue.
        var line = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
        for (int page = 1; page < Map16Layout.BankTiles / 0x100; page++)
            ctx.DrawLine(line, new Point(0, page * 16 * cell), new Point(16 * cell, page * 16 * cell));

        if (Selected / Map16Layout.BankTiles == Bank)
        {
            int idx = Selected % Map16Layout.BankTiles;
            ctx.DrawRectangle(null, new Pen(UiColors.Accent, 2),
                              new Rect(idx % 16 * cell, idx / 16 * cell, cell, cell));
        }
    }
}
