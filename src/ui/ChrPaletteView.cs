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

    /// <summary>Width the grid needs at a zoom, margin included — what the drawer sizes to so the
    /// last tile column is not cut off. Mirrors <see cref="Map16PaletteView.ContentWidth"/>; Pad is
    /// the view's margin in the window's XAML.</summary>
    public const double Pad = 8;
    public static double ContentWidth(double zoom) => Cols * 8 * zoom + Pad * 2;

    /// <summary>Selected 8x8 tile, and the rectangle when several were lassoed.</summary>
    public int Selected { get; private set; }
    public (int X, int Y, int W, int H) Brush { get; private set; } = (0, 0, 1, 1);

    public event EventHandler? BrushChanged;

    private readonly LevelBitmap sheet = new();

    /// <summary>Which animation phase to draw — the same phase the level and the Map16 sheet
    /// are showing, so an animated 8x8 tile animates in the picker it is taken from.</summary>
    public int Phase { get; set; }

    public ChrPaletteView() => Focusable = true;

    /// <summary>Take a composed sheet (see <see cref="GfxSheets.Chr"/>). The view never loads
    /// graphics itself — it is handed pixels.</summary>
    public void SetSheet(uint[]?[] px, int w, int h)
    {
        sheetW = w; sheetH = h;
        sheet.SetImages(px, w, h, Phase);
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

    private readonly PixelBlit blit = new();

    public override void Render(DrawingContext ctx)
    {
        double c = Cell;
        var full = new Rect(0, 0, Cols * c, Count / Cols * c);
        ctx.FillRectangle(Brushes.Black, full);
        // Same pixel rule as every other surface — at 125% or 150% display scaling even this whole
        // 2x grid is a fractional number of device pixels per source pixel.
        if (sheet.For(Phase) is { } bmp)
            blit.Draw(this, ctx, bmp, new Rect(0, 0, sheetW, sheetH), full, VisualRoot?.RenderScaling ?? 1);

        ctx.DrawRectangle(null, new Pen(UiColors.Accent, 2),
                          new Rect(Brush.X * c, Brush.Y * c, Brush.W * c, Brush.H * c));
    }
}
