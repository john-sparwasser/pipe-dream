using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PipeDream.Ui;

/// <summary>
/// The Map16 canvas mode: the unified tile space, editable per 8x8 QUADRANT. Same grammar as
/// the level canvas, one level down — a tile here is what a cell is there.
///
/// It works at one of two GRAINS (see <see cref="TileGrain"/>): 16x16, where everything snaps to
/// whole tiles, or 8x8, where the quadrants inside a tile are in play. Picking in the 8x8 drawer
/// switches to 8x8 — that pick has no meaning at the other grain.
///
///   RIGHT click/drag   (8x8, nothing selected) stamp the drawer's 8x8 brush at the hovered
///                      quadrant
///   RIGHT with a lasso put a COPY of the selection here instead — a Map16 selection outranks the
///                      brush at either grain, and survives so it can be stamped again
///   LEFT click+drag    lasso, at the grain in force: whole tiles at 16x16, where a plain click
///                      selects one tile and also arms it as the level brush, or single 8x8
///                      quadrants at 8x8
///   LEFT on selection  (16x16 only) drag to move the lassoed tiles (overlap-safe, one undo)
///   X / Y / P          flip horizontally, flip vertically, toggle priority — on the quadrant
///                      under the cursor, not the selection
///
/// Empty pages are drawn as ordinary black tiles and are painted on directly: the page is
/// allocated as a consequence of the edit, never as something to ask for first.
/// </summary>
public class Map16CanvasView : Control
{
    /// <summary>What an edit acts on: whole 16x16 tiles, or the 8x8 quadrants inside them. The
    /// 8x8 brush only exists at <see cref="TileGrain.Quad8"/> — at Tile16 everything snaps to the
    /// tile grid, which is what makes "select tiles and move them" a mode of its own rather than
    /// something you do carefully with a quadrant-sized cursor.</summary>
    public enum TileGrain { Tile16, Quad8 }

    public TileGrain Grain { get; set; } = TileGrain.Tile16;

    public double Zoom { get; set; } = 2.0;
    public int Bank { get; set; }
    public int TileCount => sheet.TileCount;
    public Point Origin { get; set; }

    private readonly Map16Sheet sheet = new();

    /// <summary>Which animation phase to draw — stepped with every other surface, so a tile
    /// built from animated graphics animates while it is being edited.</summary>
    public int Phase { get; set; }

    /// <summary>Tile the level brush is armed with, highlighted when it is in this bank. Null
    /// when NOTHING is selected — clicking off the sheet — which is a real state here: the
    /// properties in the header act on the selection, so with none they have nothing to act on.</summary>
    public int? SelectedTile { get; set; } = 0x100;

    /// <summary>Lassoed rectangle in bank-local 8x8 QUADRANTS, or null. Quadrants at both grains
    /// — at 16x16 it is snapped out to whole tiles, so it is always an even rect on even
    /// boundaries there, and the one unit means the drawing and the hit-testing have no grain to
    /// branch on. <see cref="SelectedTiles"/> turns it back into tile numbers.</summary>
    public (int X, int Y, int W, int H)? Selection { get; private set; }

    public (int Tile, int Quad)? HoverQuad { get; private set; }

    /// <summary>Brush footprint in 8x8 quadrants — outlined under the cursor.</summary>
    public int BrushW { get; set; } = 1;
    public int BrushH { get; set; } = 1;

    /// <summary>Right-drag reached this quadrant: (tile, visual quadrant, brush cell).</summary>
    public event EventHandler<(int Tile, int Quad, int Bx, int By)>? QuadPainted;
    public event EventHandler? StrokeEnded;
    public event EventHandler<int>? TilePicked;
    public event EventHandler<(int X, int Y, int W, int H, int Dx, int Dy)>? MoveRequested;
    /// <summary>Right-click with a selection: put a COPY of that rectangle at this delta. In
    /// QUADRANTS, like <see cref="Selection"/> — unlike <see cref="MoveRequested"/>, which is a
    /// 16x16 action and speaks in tiles.</summary>
    public event EventHandler<(int X, int Y, int W, int H, int Dx, int Dy)>? DuplicateRequested;
    /// <summary>X, Y or P pressed over a quadrant: the bit to toggle in its word.</summary>
    public event EventHandler<(int Tile, int Quad, ushort Bit)>? QuadFlagToggled;

    /// <summary>Raised when the selected tile or lasso changes, so an inspector can follow it.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>The tiles the properties panel acts on: the lasso when there is one, else the
    /// single selected tile.</summary>
    public IEnumerable<int> SelectedTiles()
    {
        if (Selection is not { } s)
        {
            if (SelectedTile is { } one) yield return one;
            yield break;
        }
        // Every tile the quadrants touch, once each: the properties in the header are per-TILE
        // (acts-as has no smaller unit), so a half-covered tile is still a covered tile.
        for (int j = s.Y / 2; j <= (s.Y + s.H - 1) / 2; j++)
            for (int i = s.X / 2; i <= (s.X + s.W - 1) / 2; i++)
                yield return Bank * Map16Layout.BankTiles + j * Map16Layout.Cols + i;
    }

    /// <summary>Centred like the level canvas: the sheet is 16 tiles wide and hundreds of rows
    /// tall, so it always scrolls vertically and always has room to spare horizontally — which
    /// it takes in the middle of the viewport rather than against the left edge.</summary>
    public Map16CanvasView()
    {
        Focusable = true;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        stroke = new(q => PaintQuad(q));
    }

    public void SetSheet(uint[]?[] px, int w, int h, int tileCount)
    {
        sheet.SetSheet(px, w, h, tileCount, Phase);
        InvalidateVisual();
        InvalidateMeasure();
    }

    public void SetPlaceholder(uint[]?[] px) { sheet.SetPlaceholder(px); InvalidateVisual(); }

    public void SetBgSheet(uint[]?[] px, int w, int h) { sheet.SetBgSheet(px, w, h, Phase); InvalidateVisual(); }

    /// <summary>Deselect everything — the lasso AND the armed tile.</summary>
    public void ClearSelection() { Selection = null; SelectedTile = null; InvalidateVisual(); }

    private double TileSize => 16 * Zoom;
    private double QuadSize => 8 * Zoom;

    /// <summary>Screen point → (bank-local column/row, absolute tile, visual quadrant).</summary>
    public (int Col, int Row, int Tile, int Quad)? At(Point p)
    {
        if (QuadAt(p) is not { } q) return null;
        int col = q.Col / 2, row = q.Row / 2;
        int quad = ((q.Row & 1) << 1) | (q.Col & 1);          // visual TL, TR, BL, BR
        return (col, row, Bank * Map16Layout.BankTiles + row * Map16Layout.Cols + col, quad);
    }

    protected override Size MeasureOverride(Size available)
        => new(Map16Layout.Cols * TileSize, Map16Layout.BankRows * TileSize);

    // ---- interaction ----

    private readonly Stroke stroke;
    private (int Col, int Row)? lassoStart, lassoEnd, moveStart;
    private Point brushOrigin;

    /// <summary>Point → bank-local QUADRANT column/row, or null when off the sheet. The unit the
    /// lasso works in at both grains; <see cref="Snapped"/> grows it out to tiles at 16x16.</summary>
    private (int Col, int Row)? QuadAt(Point p)
        => Lasso.CellAt(p + Origin, QuadSize, Map16Layout.Cols * 2, Map16Layout.BankRows * 2);

    /// <summary>The rectangle two dragged quadrants make, in quadrants — grown out to whole
    /// tiles at 16x16, where a selection that cut a tile in half would be a lie.</summary>
    private (int X, int Y, int W, int H) Snapped((int Col, int Row) a, (int Col, int Row) b)
    {
        var (x0, y0, w, h) = Lasso.Span(a, b);
        if (Grain == TileGrain.Quad8) return (x0, y0, w, h);
        int x1 = (x0 + w + 1) & ~1, y1 = (y0 + h + 1) & ~1;
        x0 &= ~1; y0 &= ~1;
        return (x0, y0, x1 - x0, y1 - y0);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (QuadAt(e.GetPosition(this)) is not { } h) return;
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsRightButtonPressed)
        {
            // A Map16 selection outranks the drawer's 8x8 brush at BOTH grains: right-click puts
            // a COPY of what is selected under the cursor — snapped to the tile grid at 16x16 —
            // and the selection stays put, so the next click puts down another copy.
            if (Selection is { } sel)
            {
                int qx = Grain == TileGrain.Quad8 ? h.Col : h.Col & ~1;
                int qy = Grain == TileGrain.Quad8 ? h.Row : h.Row & ~1;
                DuplicateRequested?.Invoke(this, (sel.X, sel.Y, sel.W, sel.H, qx - sel.X, qy - sel.Y));
            }
            else if (Grain == TileGrain.Quad8)
            {
                stroke.Begin(h);
                e.Pointer.Capture(this);
            }
        }
        else if (props.IsLeftButtonPressed)
        {
            // Dragging a selection to MOVE it is a 16x16 action: the edit layer moves whole
            // tiles, and there is nothing underneath it that moves quadrants.
            if (Grain != TileGrain.Quad8 && Lasso.Contains(Selection, h))
                moveStart = (h.Col, h.Row);
            else
                lassoStart = (h.Col, h.Row);
            lassoEnd = (h.Col, h.Row);
            e.Pointer.Capture(this);
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var h = At(p);
        var hq = h is { } x ? (x.Tile, x.Quad) : ((int, int)?)null;
        // Snap the cursor outline to what an edit would land on: the hovered QUADRANT at 8x8,
        // the hovered TILE at 16x16 — the grid you are working to has to be the one you see.
        double q = Grain == TileGrain.Quad8 ? QuadSize : TileSize;
        var snapped = new Point(Math.Floor((p.X + Origin.X) / q) * q - Origin.X,
                                Math.Floor((p.Y + Origin.Y) / q) * q - Origin.Y);
        if (hq != HoverQuad || snapped != brushOrigin)
        { HoverQuad = hq; brushOrigin = snapped; InvalidateVisual(); }

        if (QuadAt(p) is not { } c) return;
        if (stroke.Active) { stroke.MoveTo(c); return; }
        if (lassoStart is not null || moveStart is not null) { lassoEnd = c; InvalidateVisual(); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (stroke.End())
        {
            e.Pointer.Capture(null);
            StrokeEnded?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (lassoStart is { } a && lassoEnd is { } b)
        {
            var r = Snapped(a, b);
            Selection = r;
            // At 16x16 a single-TILE lasso is ALSO a pick: it arms the level brush. It stays a
            // selection as well, so one tile copies on right-click like any other. At 8x8 there
            // is nothing to arm — a quadrant is not something the level can place.
            if (Grain != TileGrain.Quad8 && r is { W: 2, H: 2 })
            {
                int picked = Bank * Map16Layout.BankTiles + r.Y / 2 * Map16Layout.Cols + r.X / 2;
                SelectedTile = picked;
                TilePicked?.Invoke(this, picked);
            }
        }
        else if (moveStart is { } m && lassoEnd is { } n && Selection is { } sel)
        {
            // Whole tiles: this path is 16x16 only, so the quadrant coordinates halve exactly.
            int dx = n.Col / 2 - m.Col / 2, dy = n.Row / 2 - m.Row / 2;
            if (dx != 0 || dy != 0)
            {
                MoveRequested?.Invoke(this, (sel.X / 2, sel.Y / 2, sel.W / 2, sel.H / 2, dx, dy));
                Selection = (sel.X + dx * 2, sel.Y + dy * 2, sel.W, sel.H);
            }
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        lassoStart = lassoEnd = moveStart = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Flags apply to the quadrant UNDER THE CURSOR, matching the ImGui editor — it is a
        // hover action, not a selection action.
        if (HoverQuad is not { } h) return;
        ushort bit = e.Key switch
        {
            Key.X => 0x4000,       // flip X
            Key.Y => 0x8000,       // flip Y
            Key.P => 0x2000,       // priority
            _ => 0,
        };
        if (bit == 0) return;
        QuadFlagToggled?.Invoke(this, (h.Tile, h.Quad, bit));
        e.Handled = true;
    }

    /// <summary>Stamp the brush with its top-left at this bank-local quadrant.</summary>
    private void PaintQuad((int Col, int Row) q)
    {
        for (int j = 0; j < BrushH; j++)
            for (int i = 0; i < BrushW; i++)
            {
                int qx = q.Col + i, qy = q.Row + j;
                if (qx >= Map16Layout.Cols * 2 || qy >= Map16Layout.BankRows * 2) continue;
                int tile = Bank * Map16Layout.BankTiles
                         + (qy >> 1) * Map16Layout.Cols + (qx >> 1);
                QuadPainted?.Invoke(this, (tile, ((qy & 1) << 1) | (qx & 1), i, j));
            }
    }

    // ---- rendering ----

    public override void Render(DrawingContext ctx)
    {
        double ts = TileSize;
        sheet.Draw(this, ctx, Bank, Phase, ts);

        // Live lasso, then the settled selection, then the armed tile. Both are in QUADRANTS —
        // at 16x16 they are snapped out to whole tiles, so they draw on the tile grid anyway.
        double qs = QuadSize;
        if ((lassoStart ?? moveStart) is { } s0 && lassoEnd is { } s1)
        {
            var l = Snapped(s0, s1);
            Overlay.Band(ctx, Cells(l.X, l.Y, l.W, l.H, qs));
        }
        if (Selection is { } sel) Overlay.Selection(ctx, Cells(sel.X, sel.Y, sel.W, sel.H, qs));
        else if (SelectedTile is { } armed && armed / Map16Layout.BankTiles == Bank)
        {
            int idx = armed % Map16Layout.BankTiles;
            Overlay.Outline(ctx, Cells(idx % Map16Layout.Cols, idx / Map16Layout.Cols, 1, 1, ts));
        }

        // The cursor: the brush footprint in QUADRANTS at 8x8 — a cell-sized outline would lie
        // about where a stamp lands — and the whole hovered tile at 16x16, where the brush is
        // not what right-click does.
        if (HoverQuad is not null && IsPointerOver)
        {
            double cw = Grain == TileGrain.Quad8 ? BrushW * qs : ts;
            double ch = Grain == TileGrain.Quad8 ? BrushH * qs : ts;
            Overlay.Brush(ctx, new Rect(brushOrigin.X, brushOrigin.Y, cw, ch));
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e)
    { base.OnPointerExited(e); HoverQuad = null; InvalidateVisual(); }

    private Rect Cells(int x, int y, int w, int h, double ts)
        => new(x * ts - Origin.X, y * ts - Origin.Y, w * ts, h * ts);
}
