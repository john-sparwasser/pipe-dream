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
///   LEFT drag INSIDE   move the lassoed block, as the GFX canvas's select tool moves pixels
///   LEFT drag a GRIP   grow or shrink the lasso; growing REPEATS the block into the new space
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

    /// <summary>Take the whole width offered instead of a fixed <see cref="Zoom"/> — what a
    /// drawer sheet wants, where the width is the splitter's to decide and a fixed zoom leaves
    /// dead desk beside the tiles. Zoom then follows the drawer, including a splitter drag,
    /// because measure runs again on every resize.</summary>
    public bool FitWidth { get; set; }

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

    /// <summary>
    /// A finished selection drag: where the block came from, where it is now, and whether the
    /// source should be left behind. The owner does the writing — this control knows what cells
    /// SHOW, not what they mean, and a layer's idea of "blank" is the layer's business.
    ///
    /// Resize and move are the same event because they are the same write: fill the new
    /// rectangle by repeating the old one. When the sizes match that repeat is a plain copy, so
    /// a move needs no case of its own — only the extra step of clearing what it left.
    /// </summary>
    public readonly record struct SelectionDrag(
        (int X, int Y, int W, int H) From, (int X, int Y, int W, int H) To, bool Move);

    public event EventHandler<(int Col, int Row)>? Painted;
    public event EventHandler? StrokeEnded;
    public event EventHandler<(int Col, int Row)>? Picked;
    public event EventHandler? SelectionChanged;
    public event EventHandler<SelectionDrag>? SelectionDragged;

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
    {
        // Never below 1: a drawer dragged narrower than one tile per cell would otherwise shrink
        // the sheet away rather than scroll it.
        if (FitWidth && Cols > 0 && CellPx > 0 && double.IsFinite(availableSize.Width))
            Zoom = Math.Max(1, availableSize.Width / (Cols * CellPx));
        return new(Cols * Step, Rows * Step);
    }

    /// <summary>Screen point → the cell under it, or null past the grid.</summary>
    /// <summary>The cell under the pointer, or null when it is off the grid — what the gutter
    /// readout asks for.</summary>
    public (int Col, int Row)? Hover => hover;

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
        // The cursor, one cell, and ONLY when there is no lasso. It used to outline the whole
        // stamp footprint instead, which with a lasso up put a second rectangle of exactly the
        // selection's size chasing the pointer around the selection itself — two reticles for
        // one gesture, and the drawn one is the one you can grab.
        if (hover is { } h && !PickOnLeft && Selection is null)
            ctx.DrawRectangle(null, new Pen(UiColors.Band, 1.5),
                              new Rect(h.Col * Step, h.Row * Step, Step, Step));
        if (Selection is { } grips && !PickOnLeft) DrawHandles(ctx, grips);
    }

    /// <summary>The eight grips, drawn ON the selection's border so the thing you grab is the
    /// thing you see. Sized in screen pixels, not cells: a 64x64 layer 3 zoomed out has cells
    /// smaller than a comfortable grab, and the grip has to stay grabbable either way.</summary>
    private void DrawHandles(DrawingContext ctx, (int X, int Y, int W, int H) s)
    {
        double g = GripPx, x0 = s.X * Step, y0 = s.Y * Step;
        double x1 = x0 + s.W * Step, y1 = y0 + s.H * Step;
        var fill = UiColors.Selection;
        foreach (var (dx, dy) in Handles)
        {
            double cx = dx < 0 ? x0 : dx > 0 ? x1 : (x0 + x1) / 2;
            double cy = dy < 0 ? y0 : dy > 0 ? y1 : (y0 + y1) / 2;
            var r = new Rect(cx - g / 2, cy - g / 2, g, g);
            ctx.FillRectangle(fill, r);
            ctx.DrawRectangle(null, new Pen(Brushes.Black, 1), r);
        }
    }

    private static readonly (int DX, int DY)[] Handles =
        [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];

    private const double GripPx = 9;

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
            PressAt(p);
            e.Pointer.Capture(this);
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var at = e.GetPosition(this);
        var cell = At(at);
        if (cell != hover) { hover = cell; if (!PickOnLeft) InvalidateVisual(); }
        // The grab hand says the selection is draggable before you press, the same tell the GFX
        // canvas gives; a grip says which way it would grow.
        Cursor = PickOnLeft || painting ? null
               : Dragging || GrabAt(at) != Grab.Lasso ? DragCursor : null;
        if (painting && cell is { } c) Painted?.Invoke(this, c);
        else MoveTo(at);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        Release();
        if (!painting) return;
        painting = false;
        StrokeEnded?.Invoke(this, EventArgs.Empty);
    }

    // ---- selection drags ----
    //
    // Three things a left press can start, and which one is decided by WHERE it lands: on one of
    // the selection's grips it resizes, inside the selection it moves the block, anywhere else it
    // starts a fresh lasso. Press/move/release are public because that is the behaviour worth
    // testing, and synthesising pointer events would be testing Avalonia instead.

    public enum Grab { Lasso, Move, Resize }

    private static readonly Cursor DragCursor = new(StandardCursorType.SizeAll);

    private (int Col, int Row)? lassoStart;
    private (int X, int Y, int W, int H)? dragFrom;     // the rect the drag started from
    private (int Col, int Row) dragGrab;                // the cell the pointer grabbed
    private (int DX, int DY) dragEdge;                  // which grip, (0,0) for a move

    /// <summary>A drag is running, which is what suppresses the stamp reticle.</summary>
    public bool Dragging => dragFrom is not null || lassoStart is not null;

    /// <summary>What a press at this point would start.</summary>
    public Grab GrabAt(Point p)
        => Selection is not { } s ? Grab.Lasso
         : EdgeAt(p, s) != (0, 0) ? Grab.Resize
         : At(p) is { } c && c.Col >= s.X && c.Col < s.X + s.W && c.Row >= s.Y && c.Row < s.Y + s.H
           ? Grab.Move : Grab.Lasso;

    /// <summary>Which grip is under the point, as a direction per axis. Screen pixels, so the
    /// grab zone is the same size however far the view is zoomed out.</summary>
    private (int DX, int DY) EdgeAt(Point p, (int X, int Y, int W, int H) s)
    {
        double x0 = s.X * Step, y0 = s.Y * Step, x1 = x0 + s.W * Step, y1 = y0 + s.H * Step;
        // Never wider than a third of the rect: on a small selection an over-eager grip would
        // swallow the middle and there would be nowhere left to grab it by.
        double g = Math.Min(GripPx, Math.Min(x1 - x0, y1 - y0) / 3);
        int dx = Math.Abs(p.X - x0) <= g ? -1 : Math.Abs(p.X - x1) <= g ? 1 : 0;
        int dy = Math.Abs(p.Y - y0) <= g ? -1 : Math.Abs(p.Y - y1) <= g ? 1 : 0;
        // Only ON the border: a point level with an edge but far above the rectangle is not a grip.
        if (p.X < x0 - g || p.X > x1 + g) return (0, 0);
        if (p.Y < y0 - g || p.Y > y1 + g) return (0, 0);
        return (dx, dy);
    }

    public void PressAt(Point p)
    {
        if (At(p) is not { } cell) return;
        if (Selection is { } s && GrabAt(p) is var kind && kind != Grab.Lasso)
        {
            dragFrom = s;
            dragGrab = cell;
            dragEdge = kind == Grab.Resize ? EdgeAt(p, s) : (0, 0);
            return;
        }
        // A press starts the lasso and settles it at one cell straight away, so a plain
        // click IS a selection — there is no drag threshold to discover.
        BeginSelection(cell.Col, cell.Row);
    }

    public void MoveTo(Point p)
    {
        if (dragFrom is { } from)
        {
            if (ClampedAt(p) is not { } cell) return;
            SetSelection(dragEdge == (0, 0) ? Moved(from, cell) : Resized(from, cell));
            return;
        }
        if (lassoStart is not null && At(p) is { } l) ExtendSelection(l.Col, l.Row);
    }

    /// <summary>The cell under a point, clamped into the grid — a fast drag past an edge should
    /// land ON the edge rather than stop dead, as the GFX canvas already does.</summary>
    private (int Col, int Row)? ClampedAt(Point p)
        => Cols <= 0 || Rows <= 0 ? null
         : (Math.Clamp((int)Math.Floor(p.X / Step), 0, Cols - 1),
            Math.Clamp((int)Math.Floor(p.Y / Step), 0, Rows - 1));

    private (int X, int Y, int W, int H) Moved((int X, int Y, int W, int H) from, (int Col, int Row) to)
        => (Math.Clamp(from.X + to.Col - dragGrab.Col, 0, Math.Max(0, Cols - from.W)),
            Math.Clamp(from.Y + to.Row - dragGrab.Row, 0, Math.Max(0, Rows - from.H)),
            from.W, from.H);

    /// <summary>The grabbed edge follows the pointer; the opposite one stays put.</summary>
    private (int X, int Y, int W, int H) Resized((int X, int Y, int W, int H) from, (int Col, int Row) to)
    {
        int x0 = from.X, x1 = from.X + from.W - 1, y0 = from.Y, y1 = from.Y + from.H - 1;
        if (dragEdge.DX < 0) x0 = Math.Min(to.Col, x1);
        if (dragEdge.DX > 0) x1 = Math.Max(to.Col, x0);
        if (dragEdge.DY < 0) y0 = Math.Min(to.Row, y1);
        if (dragEdge.DY > 0) y1 = Math.Max(to.Row, y0);
        return (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    public void Release()
    {
        lassoStart = null;
        if (dragFrom is not { } from) return;
        dragFrom = null;
        InvalidateVisual();                       // the grips move with it
        if (Selection is not { } to) return;
        // A press inside the selection that never moved is a click, and a click is how you
        // re-anchor a one-cell lasso — the eyedropper has to survive being aimed at a selection.
        if (to == from && dragEdge == (0, 0)) { BeginSelection(dragGrab.Col, dragGrab.Row); return; }
        if (to != from) SelectionDragged?.Invoke(this, new SelectionDrag(from, to, dragEdge == (0, 0)));
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
        SetSelection((X: Math.Min(from.Col, to.Col), Y: Math.Min(from.Row, to.Row),
                      W: Math.Abs(to.Col - from.Col) + 1, H: Math.Abs(to.Row - from.Row) + 1));
    }

    private void SetSelection((int X, int Y, int W, int H) next)
    {
        if (Selection == next) return;
        Selection = next;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        hover = null;
        InvalidateVisual();
    }
}
