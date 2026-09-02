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
    // Count stops at 0x300: tiles 300-3FF are the animated region the game streams into every
    // frame — LM's 8x8 editor hides them by default too, so the picker does not offer them.
    public const int Cols = GfxSheets.ChrCols, Count = 0x300;

    /// <summary>Set by <see cref="MeasureOverride"/> from the width the drawer gives it — the grid
    /// is always 16 tiles across, so the tile size is whatever divides the space, not a fixed 2x
    /// with a dead gutter beside it. The initial value only stands until the first measure.</summary>
    public double Zoom { get; private set; } = 2.0;

    /// <summary>Selected 8x8 tile, and the rectangle when several were lassoed.</summary>
    public int Selected { get; private set; }
    public (int X, int Y, int W, int H) Brush { get; private set; } = (0, 0, 1, 1);

    /// <summary>Whether that pick is still standing. Deselecting in the Map16 canvas drops it
    /// here too — one deselect, not one per surface.</summary>
    public bool HasSelection { get; private set; }

    /// <summary>Drop the pick, footprint and all. The brush goes back to a single tile rather
    /// than keeping the shape of a selection that is no longer there — a deselect that leaves a
    /// 2x2 cursor behind on the Map16 canvas has not deselected anything the eye can see.</summary>
    public void ClearSelection()
    {
        HasSelection = false;
        Brush = (0, 0, 1, 1);
        InvalidateVisual();
    }

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
        => Lasso.CellAt(p, Cell, Cols, Count / Cols) is { } c ? c.Y * Cols + c.X : null;

    /// <summary>Fit the width. The host must not offer infinity here — the ScrollViewer around
    /// this one scrolls vertically only, so it does not — but a stale zoom beats a NaN one.</summary>
    protected override Size MeasureOverride(Size available)
    {
        if (available.Width > 0 && !double.IsInfinity(available.Width))
        {
            double want = available.Width / (Cols * 8);
            if (Math.Abs(want - Zoom) > 0.0001) { Zoom = want; InvalidateVisual(); }
        }
        return new(Cols * Cell, Count / Cols * Cell);
    }

    private (int X, int Y)? dragStart;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (TileAt(e.GetPosition(this)) is not { } t) return;
        dragStart = (t % Cols, t / Cols);
        Brush = (dragStart.Value.X, dragStart.Value.Y, 1, 1);
        Selected = t;
        HasSelection = true;
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
        Brush = Lasso.Span(a, (t % Cols, t / Cols));
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
            blit.Draw(this, ctx, bmp, new Rect(0, 0, sheetW, Math.Min(sheetH, Count / Cols * 8)),
                      full, VisualRoot?.RenderScaling ?? 1);

        if (HasSelection) Overlay.Armed(ctx, new Rect(Brush.X * c, Brush.Y * c, Brush.W * c, Brush.H * c));
    }
}
