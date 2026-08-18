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
    private WriteableBitmap? sheet;
    private int sheetW, sheetH;

    public double Zoom { get; set; } = 2.0;
    public int Bank { get; set; }
    public int TileCount { get; private set; }

    /// <summary>Selected Map16 tile number, in the unified numbering the level canvas uses.</summary>
    public int Selected { get; private set; } = 0x100;

    public event EventHandler<int>? SelectionChanged;

    public Map16PaletteView() => Focusable = true;

    public void SetSheet(uint[] px, int w, int h, int tileCount)
    {
        sheetW = w; sheetH = h; TileCount = tileCount;
        sheet?.Dispose();
        sheet = LevelBitmap.FromPixels(px, w, h);
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

    protected override Size MeasureOverride(Size availableSize)
        => new(16 * 16 * Zoom, Map16Layout.BankRows * 16 * Zoom);

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

    public override void Render(DrawingContext ctx)
    {
        double z = Zoom, cell = 16 * z;
        var full = new Rect(0, 0, 16 * cell, Map16Layout.BankRows * cell);
        // Empty pages are ordinary black tiles, not a roped-off region: painting one is what
        // brings it into existence, so the drawer must not make it look unavailable.
        ctx.FillRectangle(Brushes.Black, full);

        if (sheet is not null && sheetH > 0)
        {
            var (v0, v1, rows, _) = Map16Layout.SheetWindow(Bank, sheetH, TileCount);
            if (rows > 0)
            {
                var src = new Rect(0, v0 * sheetH, sheetW, (v1 - v0) * sheetH);
                ctx.DrawImage(sheet, src, new Rect(0, 0, 16 * cell, rows * cell));
            }
        }

        // Page separators every 16 rows, LM-style — the drawer's only orientation cue.
        var line = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
        for (int page = 1; page < Map16Layout.BankTiles / 0x100; page++)
            ctx.DrawLine(line, new Point(0, page * 16 * cell), new Point(16 * cell, page * 16 * cell));

        if (Selected / Map16Layout.BankTiles == Bank)
        {
            int idx = Selected % Map16Layout.BankTiles;
            ctx.DrawRectangle(null, new Pen(Brushes.Orange, 2),
                              new Rect(idx % 16 * cell, idx / 16 * cell, cell, cell));
        }
    }
}
