using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

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
    private readonly Stroke stroke;
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
        (int X, int Y, int W, int H) From, (int X, int Y, int W, int H) To, bool Move)
    {
        /// <summary>
        /// Which source cell fills destination cell (<paramref name="col"/>, <paramref name="row"/>).
        /// The one place this mapping lives: the drag preview and the write that lands on release
        /// have to agree, and "what you saw is what you got" is the whole point of the preview.
        ///
        /// A move follows the block, so the offset is straight. A grow REPEATS, and the repeat is
        /// phased on the old rectangle's own origin rather than the new one's, so the cells that
        /// were already there do not shift while the space beside them fills in.
        /// </summary>
        public (int Col, int Row) Source(int col, int row)
            => Move ? (From.X + col - To.X, From.Y + row - To.Y)
                    : (From.X + Wrap(col - From.X, From.W), From.Y + Wrap(row - From.Y, From.H));

        /// <summary>Index into a repeating pattern, for offsets that run negative — growing a
        /// selection LEFTWARDS is the case C#'s % gets wrong on its own.</summary>
        private static int Wrap(int i, int n) => n <= 0 ? 0 : (i % n + n) % n;
    }

    public event EventHandler<(int Col, int Row)>? Painted;
    public event EventHandler? StrokeEnded;
    public event EventHandler<(int Col, int Row)>? Picked;
    public event EventHandler? SelectionChanged;
    public event EventHandler<SelectionDrag>? SelectionDragged;

    /// <summary>The wheel changed <see cref="Zoom"/>; the owner's zoom control catches up.</summary>
    public event EventHandler? ZoomChanged;

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
        stroke = new(c => Painted?.Invoke(this, c));
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

    public (int Col, int Row)? At(Point p) => Lasso.CellAt(p, Step, Cols, Rows);

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

        // The armed tile wears the same ring as the Map16 and 8x8 drawers' picks.
        if (Selected is { } sel && Cols > 0) Overlay.Armed(ctx, CellRect((sel % Cols, sel / Cols, 1, 1)));
        if (Selection is { } lasso) Overlay.Selection(ctx, CellRect(lasso));
        // The cursor, one cell, and ONLY when there is no lasso. It used to outline the whole
        // stamp footprint instead, which with a lasso up put a second rectangle of exactly the
        // selection's size chasing the pointer around the selection itself — two reticles for
        // one gesture, and the drawn one is the one you can grab.
        if (hover is { } h && !PickOnLeft && Selection is null) Overlay.Band(ctx, CellRect((h.Col, h.Row, 1, 1)));
        if (LiveDrag is { } drag) DrawDragPreview(ctx, drag);
        if (Selection is { } grips && !PickOnLeft) Grips.Draw(ctx, CellRect(grips), GripPx);
    }

    private Rect CellRect((int X, int Y, int W, int H) r)
        => new(r.X * Step, r.Y * Step, r.W * Step, r.H * Step);

    /// <summary>The drag as it stands right now, or null between drags — the same value that will
    /// be raised on release, which is what lets the preview and the result be the same thing.</summary>
    public SelectionDrag? LiveDrag
        => dragFrom is { } f && Selection is { } t && t != f
           ? new SelectionDrag(f, t, dragEdge == (0, 0)) : null;

    private readonly PixelBlit dragBlit = new();
    private Avalonia.Media.Imaging.WriteableBitmap? dragBmp, dragRetired;
    private SelectionDrag? dragBmpFor;

    /// <summary>
    /// The tiles under the drag, drawn where they are going while you are still dragging. Without
    /// it the rectangle moves and its contents do not, and a repeat cannot be judged at all until
    /// it has already been written.
    ///
    /// A move also empties where it came from, because that is what it will do: seeing the gap
    /// open is how you tell a move from a copy without letting go first.
    /// </summary>
    private void DrawDragPreview(DrawingContext ctx, SelectionDrag d)
    {
        if (CellAt is not { } at || CellPixels is not { } pixels) return;
        if (d.Move)
            ctx.FillRectangle(new SolidColorBrush(Rgba(Backdrop)),
                              new Rect(d.From.X * Step, d.From.Y * Step,
                                       d.From.W * Step, d.From.H * Step));

        int w = d.To.W * CellPx, h = d.To.H * CellPx;
        if (w <= 0 || h <= 0) return;
        // Rebuilt only when the drag actually moves a cell — a repaint at the same rectangle
        // (the tile animation stepping, say) reuses the bitmap.
        if (dragBmp is null || dragBmpFor != d)
        {
            var px = new uint[w * h];               // 0 is transparent: the canvas shows through
            for (int r = 0; r < d.To.H; r++)
                for (int c = 0; c < d.To.W; c++)
                {
                    var (sc, sr) = d.Source(d.To.X + c, d.To.Y + r);
                    int v = at(sc, sr);
                    if (v < 0 || pixels(v) is not { } tile) continue;
                    for (int y = 0; y < CellPx; y++)
                        for (int x = 0; x < CellPx; x++)
                            if (tile[y * CellPx + x] is var col && col != 0)
                                px[(r * CellPx + y) * w + c * CellPx + x] = col;
                }
            // Parked rather than disposed on the spot: the draw that used it was RECORDED this
            // frame and runs later, and freeing it under the compositor is the crash PixelBlit
            // documents. One frame of grace is enough, and one bitmap is the whole backlog.
            dragRetired?.Dispose();
            dragRetired = dragBmp;
            dragBmp = LevelBitmap.FromPixels(px, w, h);
            dragBmpFor = d;
        }
        dragBlit.Draw(this, ctx, dragBmp, new Rect(0, 0, w, h),
                      new Rect(d.To.X * Step, d.To.Y * Step, d.To.W * Step, d.To.H * Step),
                      VisualRoot?.RenderScaling ?? 1);
    }

    /// <summary>Composition packs 0xAABBGGRR, which is not the order Color takes.</summary>
    private static Color Rgba(uint c)
        => Color.FromArgb((byte)(c >> 24), (byte)c, (byte)(c >> 8), (byte)(c >> 16));

    /// <summary>Grip size in screen pixels — a 64x64 layer 3 zoomed out has cells smaller than
    /// a comfortable grab.</summary>
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
            stroke.Begin(cell);
            e.Pointer.Capture(this);
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
        // The move arrows say the selection is draggable before you press, the same tell the GFX
        // canvas gives; a grip says which way it would grow.
        Cursor = PickOnLeft || stroke.Active || lassoStart is not null ? null
               : dragFrom is not null ? Grips.CursorFor(dragEdge) ?? UiCursors.Move
               : Selection is { } sel && Grips.EdgeAt(at, CellRect(sel), GripPx) is var edge && edge != (0, 0)
                   ? Grips.CursorFor(edge)
               : GrabAt(at) == Grab.Move ? UiCursors.Move : null;
        if (!stroke.Active) MoveTo(at);
        else if (cell is { } c) stroke.MoveTo(c);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        Release();
        if (stroke.End()) StrokeEnded?.Invoke(this, EventArgs.Empty);
    }

    // ---- selection drags ----
    //
    // Three things a left press can start, and which one is decided by WHERE it lands: on one of
    // the selection's grips it resizes, inside the selection it moves the block, anywhere else it
    // starts a fresh lasso. Press/move/release are public because that is the behaviour worth
    // testing, and synthesising pointer events would be testing Avalonia instead.

    public enum Grab { Lasso, Move, Resize }

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
         : At(p) is { } c && Lasso.Contains(s, c) ? Grab.Move : Grab.Lasso;

    private (int DX, int DY) EdgeAt(Point p, (int X, int Y, int W, int H) s)
        => Grips.EdgeAt(p, CellRect(s), GripPx);

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
            // Clamped: a fast drag past an edge lands ON the edge rather than stopping dead.
            if (Lasso.Clamped(p, Step, Cols, Rows) is not { } cell) return;
            SetSelection(dragEdge == (0, 0) ? Lasso.Moved(from, dragGrab, cell, Cols, Rows)
                                            : Grips.Resized(from, dragEdge, cell));
            return;
        }
        if (lassoStart is not null && At(p) is { } l) ExtendSelection(l.Col, l.Row);
    }

    public void Release()
    {
        lassoStart = null;
        if (dragFrom is not { } from) return;
        dragFrom = null;
        dragRetired?.Dispose();
        dragRetired = dragBmp;                     // the preview's job is over; the map has it now
        dragBmp = null; dragBmpFor = null;
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
        if (lassoStart is { } from) SetSelection(Lasso.Span(from, to));
    }

    private void SetSelection((int X, int Y, int W, int H) next)
    {
        if (Selection == next) return;
        Selection = next;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private double zoomWheel;   // fractional wheel not yet spent: a trackpad sends a notch in pieces

    /// <summary>The wheel zooms, about the cell under the cursor — the map is a picture, and a
    /// picture is browsed by leaning in where you are looking. The drawer sheet is exempt: it
    /// sizes to the drawer's width, so there the wheel stays a scroll.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (FitWidth) return;
        zoomWheel += e.Delta.Y;
        int notches = (int)zoomWheel;
        zoomWheel -= notches;
        e.Handled = true;
        if (notches == 0) return;

        double before = Zoom;
        Zoom = Math.Clamp(Zoom + notches * 0.5, 1, 8);
        if (Zoom == before) return;

        // The point under the cursor is p in this control now and p*f after; the scroll offset
        // moves by the difference so that cell stays put. The layout pass has to run first —
        // an offset set against the old extent is clamped to it.
        var p = e.GetPosition(this);
        double f = Zoom / before;
        InvalidateMeasure();
        InvalidateVisual();
        if (this.FindAncestorOfType<ScrollViewer>() is { } sv)
        {
            sv.UpdateLayout();
            sv.Offset += new Vector(p.X * (f - 1), p.Y * (f - 1));
        }
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        hover = null;
        InvalidateVisual();
    }
}
