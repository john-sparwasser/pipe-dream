using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PipeDream.Ui;

/// <summary>
/// A grid of tiles, drawn from whatever pixels the session hands back for each cell's VALUE.
///
/// Two uses, one control: the Background tab's canvas (paintable) and the drawer sheet beside it
/// (a picker). They are the same thing — a grid of cells you point at — and the only difference
/// is which button does what, so the picker is a flag rather than a second class.
///
///   LEFT click/drag    paint the armed tile (canvas), or pick the cell (drawer)
///   RIGHT click        eyedropper: arm the tile already in this cell
///
/// Left paints here rather than stamping on the right, as the level and Map16 canvases do. Those
/// keep left for selection because they HAVE selection; this mode does not, so leaving left with
/// no job would be the only surprise on offer. It follows the GFX canvas instead, which is the
/// nearer sibling: a paint surface with a brush.
///
/// Cells are composed into one surface rather than drawn one rectangle at a time, so a 64x64
/// layer 3 is a single blit at whatever fractional zoom the viewport lands on.
/// </summary>
public sealed class TilemapView : Control
{
    private readonly PixelBlit blit = new();
    private uint[]? surface;
    private Avalonia.Media.Imaging.WriteableBitmap? bmp;
    private bool stale = true;
    private int surfW, surfH;
    private bool painting;
    private (int Col, int Row)? hover;

    public int Cols { get; set; }
    public int Rows { get; set; }
    public int CellPx { get; set; } = 16;
    public double Zoom { get; set; } = 2;

    /// <summary>What shows where a cell has nothing — the level's back-area colour, which is
    /// exactly what the console shows through a transparent tile.</summary>
    public uint Backdrop { get; set; } = 0xFF000000;

    /// <summary>(column, row) → the cell's value, or -1 for "nothing here".</summary>
    public Func<int, int, int>? CellAt { get; set; }

    /// <summary>A cell value → its CellPx × CellPx pixels, or null when it draws nothing.</summary>
    public Func<int, uint[]?>? CellPixels { get; set; }

    /// <summary>Drawer mode: left picks instead of painting, and the picked cell is ringed.</summary>
    public bool PickOnLeft { get; set; }

    /// <summary>The armed cell, drawn with a selection ring. In drawer mode this is the brush.</summary>
    public int? Selected { get; set; }

    public event EventHandler<(int Col, int Row)>? Painted;
    public event EventHandler? StrokeEnded;
    public event EventHandler<(int Col, int Row)>? Picked;

    public TilemapView()
    {
        Focusable = true;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
    }

    /// <summary>Rebuild the surface on the next render. Called after anything that changes what
    /// a cell shows — a stamp, an undo, a repointed GFX slot, a palette edit.</summary>
    public void Invalidate() { stale = true; InvalidateVisual(); }

    /// <summary>Grid size changed too, so the control has to re-measure as well.</summary>
    public void Reshape(int cols, int rows, int cellPx)
    {
        Cols = cols; Rows = rows; CellPx = cellPx;
        stale = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private double Step => CellPx * Zoom;

    protected override Size MeasureOverride(Size availableSize)
        => new(Cols * Step, Rows * Step);

    /// <summary>Screen point → the cell under it, or null past the grid.</summary>
    public (int Col, int Row)? At(Point p)
    {
        int col = (int)(p.X / Step), row = (int)(p.Y / Step);
        return (uint)col < Cols && (uint)row < Rows ? (col, row) : null;
    }

    private void Compose()
    {
        stale = false;
        surfW = Cols * CellPx; surfH = Rows * CellPx;
        if (surfW <= 0 || surfH <= 0) { surface = null; bmp = null; return; }
        if (surface is null || surface.Length != surfW * surfH)
        {
            surface = new uint[surfW * surfH];
            bmp = null;                                  // size changed: the bitmap follows it
        }
        Array.Fill(surface, Backdrop);
        if (CellAt is { } at && CellPixels is { } pixels)
            for (int row = 0; row < Rows; row++)
                for (int col = 0; col < Cols; col++)
                {
                    int v = at(col, row);
                    if (v < 0 || pixels(v) is not { } px) continue;
                    int ox = col * CellPx, oy = row * CellPx;
                    for (int y = 0; y < CellPx; y++)
                        for (int x = 0; x < CellPx; x++)
                        {
                            uint c = px[y * CellPx + x];
                            // Colour 0 is transparent in a BG or layer-3 tile: the backdrop
                            // stays, which is what the console shows through it.
                            if (c != 0) surface[(oy + y) * surfW + ox + x] = c;
                        }
                }
        bmp = LevelBitmap.FromPixels(surface, surfW, surfH);
    }

    public override void Render(DrawingContext ctx)
    {
        if (stale) Compose();
        var full = new Rect(0, 0, Cols * Step, Rows * Step);
        if (bmp is null) { ctx.FillRectangle(Brushes.Black, full); return; }
        blit.Draw(this, ctx, bmp, new Rect(0, 0, surfW, surfH), full, VisualRoot?.RenderScaling ?? 1);

        if (Selected is { } sel && Cols > 0)
        {
            var r = new Rect(sel % Cols * Step, sel / Cols * Step, Step, Step);
            ctx.FillRectangle(UiColors.SelectionFill, r);
            ctx.DrawRectangle(null, new Pen(UiColors.Selection, 2), r);
        }
        if (hover is { } h && !PickOnLeft)
            ctx.DrawRectangle(null, new Pen(UiColors.Band, 1.5),
                              new Rect(h.Col * Step, h.Row * Step, Step, Step));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        if (At(p) is not { } cell) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed || PickOnLeft)
        {
            Picked?.Invoke(this, cell);
        }
        else if (props.IsLeftButtonPressed)
        {
            painting = true;
            e.Pointer.Capture(this);
            Painted?.Invoke(this, cell);
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var cell = At(e.GetPosition(this));
        if (cell != hover) { hover = cell; if (!PickOnLeft) InvalidateVisual(); }
        if (painting && cell is { } c) Painted?.Invoke(this, c);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!painting) return;
        painting = false;
        e.Pointer.Capture(null);
        StrokeEnded?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        hover = null;
        InvalidateVisual();
    }
}
