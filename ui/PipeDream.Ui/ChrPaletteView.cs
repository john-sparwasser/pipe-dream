using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PipeDream.Ui;

/// <summary>
/// The 8x8 GFX picker: what the left drawer becomes in Map16 mode. Pick here, stamp in the
/// Map16 canvas — the same grammar as picking a Map16 tile and stamping it in the level.
///
/// 0x400 tiles laid out 16 per row, drawn in one palette row at a time because an 8x8 tile
/// has no palette of its own; the palette comes from the Map16 word it lands in, which is why
/// the row is chosen HERE and travels with the brush.
/// </summary>
public class ChrPaletteView : Control
{
    public const int Cols = GfxSheets.ChrCols, Count = GfxSheets.ChrCount;

    public double Zoom { get; set; } = 2.0;

    /// <summary>Selected 8x8 tile, and the rectangle when several were lassoed.</summary>
    public int Selected { get; private set; }
    public (int X, int Y, int W, int H) Brush { get; private set; } = (0, 0, 1, 1);

    public event EventHandler? BrushChanged;

    private WriteableBitmap? sheet;

    public ChrPaletteView() => Focusable = true;

    /// <summary>Take a composed sheet (see <see cref="GfxSheets.Chr"/>). The view never loads
    /// graphics itself — it is handed pixels.</summary>
    public void SetSheet(uint[] px, int w, int h)
    {
        sheetW = w; sheetH = h;
        sheet?.Dispose();
        sheet = LevelBitmap.FromPixels(px, w, h);
        InvalidateVisual();
        InvalidateMeasure();
    }

    private int sheetW = Cols * 8, sheetH = Count / Cols * 8;

    private double Cell => 8 * Zoom;

    public int? TileAt(Point p)
    {
        int col = (int)(p.X / Cell), row = (int)(p.Y / Cell);
        if (col is < 0 or >= Cols || row < 0) return null;
        int t = row * Cols + col;
        return t < Count ? t : null;
    }

    protected override Size MeasureOverride(Size available) => new(Cols * Cell, Count / Cols * Cell);

    private (int X, int Y)? dragStart;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (TileAt(e.GetPosition(this)) is not { } t) return;
        dragStart = (t % Cols, t / Cols);
        Brush = (dragStart.Value.X, dragStart.Value.Y, 1, 1);
        Selected = t;
        e.Pointer.Capture(this);
        BrushChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        // Dragging takes a RECTANGLE of 8x8 tiles as the brush, so a 2x2 block can be stamped
        // into a whole Map16 tile in one go.
        if (dragStart is not { } a || TileAt(e.GetPosition(this)) is not { } t) return;
        int bx = t % Cols, by = t / Cols;
        Brush = (Math.Min(a.X, bx), Math.Min(a.Y, by), Math.Abs(bx - a.X) + 1, Math.Abs(by - a.Y) + 1);
        BrushChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        dragStart = null;
        e.Pointer.Capture(null);
    }

    /// <summary>The 8x8 tile index at a brush cell, for stamping.</summary>
    public int TileOfBrushCell(int i, int j)
        => Math.Clamp(Brush.Y + j, 0, Count / Cols - 1) * Cols + Math.Clamp(Brush.X + i, 0, Cols - 1);

    public override void Render(DrawingContext ctx)
    {
        double c = Cell;
        var full = new Rect(0, 0, Cols * c, Count / Cols * c);
        ctx.FillRectangle(Brushes.Black, full);
        if (sheet is not null) ctx.DrawImage(sheet, new Rect(0, 0, sheetW, sheetH), full);

        ctx.DrawRectangle(null, new Pen(UiColors.Accent, 2),
                          new Rect(Brush.X * c, Brush.Y * c, Brush.W * c, Brush.H * c));
    }
}
