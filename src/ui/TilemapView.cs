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
///   LEFT click/drag    lasso a rectangle of cells (canvas), or pick a tile (drawer)
///   RIGHT click/drag   stamp — a COPY of the lassoed rectangle when there is one, else the
///                      drawer's tile
///
/// The same grammar as the level and Map16 canvases, including the precedence: a selection made
/// HERE outranks the one made in the drawer, exactly as a Map16 lasso outranks its 8x8 brush.
/// A one-cell lasso is therefore also the eyedropper — it carries whatever that cell holds,
/// which on layer 3 is a whole BG3 word, palette group and flips included.
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

    /// <summary>Drawer mode: left picks a tile instead of lassoing, and nothing is paintable.</summary>
    public bool PickOnLeft { get; set; }

    /// <summary>The armed cell, drawn with a ring. In drawer mode this is the brush.</summary>
    public int? Selected { get; set; }

    /// <summary>The lassoed rectangle in cells, or null. A stamp copies THIS when it exists —
    /// the canvas outranks the drawer, which is what makes a one-cell lasso an eyedropper.</summary>
    public (int X, int Y, int W, int H)? Selection { get; private set; }

    /// <summary>The footprint a stamp would cover, for the cursor outline: the lasso's size, or
    /// one cell when the drawer's tile is what would land.</summary>
    public (int W, int H) Brush => Selection is { } s ? (s.W, s.H) : (1, 1);

    public event EventHandler<(int Col, int Row)>? Painted;
    public event EventHandler? StrokeEnded;
    public event EventHandler<(int Col, int Row)>? Picked;
    public event EventHandler? SelectionChanged;

    /// <summary>Drop the lasso — a drawer pick, or a click on the desk beside the grid.</summary>
    public void ClearSelection()
    {
        if (Selection is null) return;
        Selection = null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

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
        if (Selection is { } lasso)
        {
            var r = new Rect(lasso.X * Step, lasso.Y * Step, lasso.W * Step, lasso.H * Step);
            ctx.FillRectangle(UiColors.SelectionFill, r);
            ctx.DrawRectangle(null, new Pen(UiColors.Selection, 2), r);
        }
        // The cursor outlines what a stamp would COVER, not the cell it is over: with a lasso
        // armed that is the whole rectangle, and guessing where it lands is the one thing a
        // pattern brush should never make you do.
        if (hover is { } h && !PickOnLeft)
        {
            var (bw, bh) = Brush;
            ctx.DrawRectangle(null, new Pen(UiColors.Band, 1.5),
                              new Rect(h.Col * Step, h.Row * Step, bw * Step, bh * Step));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        if (At(p) is not { } cell) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (PickOnLeft)
        {
            Picked?.Invoke(this, cell);
        }
        else if (props.IsRightButtonPressed)
        {
            painting = true;
            e.Pointer.Capture(this);
            Painted?.Invoke(this, cell);
        }
        else if (props.IsLeftButtonPressed)
        {
            // A press starts the lasso and settles it at one cell straight away, so a plain
            // click IS a selection — there is no drag threshold to discover.
            BeginSelection(cell.Col, cell.Row);
            e.Pointer.Capture(this);
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var cell = At(e.GetPosition(this));
        if (cell != hover) { hover = cell; if (!PickOnLeft) InvalidateVisual(); }
        if (painting && cell is { } c) Painted?.Invoke(this, c);
        else if (lassoStart is not null && cell is { } l) ExtendSelection(l.Col, l.Row);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        lassoStart = null;
        if (!painting) return;
        painting = false;
        StrokeEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Anchor a lasso at this cell and settle it there — the gesture a left press starts.
    /// Public because it is the behaviour worth testing, and driving it through synthesised
    /// pointer events would be testing Avalonia rather than this.
    /// </summary>
    public void BeginSelection(int col, int row)
    {
        lassoStart = (col, row);
        SetLasso((col, row));
    }

    /// <summary>Grow the lasso to cover the anchor and this cell, in either direction.</summary>
    public void ExtendSelection(int col, int row) => SetLasso((col, row));

    private void SetLasso((int Col, int Row) to)
    {
        if (lassoStart is not { } from) return;
        var next = (X: Math.Min(from.Col, to.Col), Y: Math.Min(from.Row, to.Row),
                    W: Math.Abs(to.Col - from.Col) + 1, H: Math.Abs(to.Row - from.Row) + 1);
        if (Selection == next) return;
        Selection = next;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private (int Col, int Row)? lassoStart;

    protected override void OnPointerExited(PointerEventArgs e)
    {
        hover = null;
        InvalidateVisual();
    }
}
