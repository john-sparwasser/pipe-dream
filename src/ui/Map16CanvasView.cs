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
///   RIGHT click/drag   stamp the drawer's 8x8 brush at the hovered quadrant
///   LEFT click+drag    lasso tiles; a plain click selects one and arms it as the level brush
///   LEFT on selection  drag to move the lassoed tiles (overlap-safe, one undo)
///   X / Y / P          flip horizontally, flip vertically, toggle priority — on the quadrant
///                      under the cursor, not the selection
///
/// Empty pages are drawn as ordinary black tiles and are painted on directly: the page is
/// allocated as a consequence of the edit, never as something to ask for first.
/// </summary>
public class Map16CanvasView : Control
{
    public double Zoom { get; set; } = 2.0;
    public int Bank { get; set; }
    public int TileCount { get; private set; }
    public Point Origin { get; set; }

    private WriteableBitmap? sheet;
    private int sheetW, sheetH;

    /// <summary>Tile the level brush is armed with, highlighted when it is in this bank.</summary>
    public int SelectedTile { get; set; } = 0x100;

    /// <summary>Lassoed tile rectangle in bank-local cells, or null.</summary>
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
    /// <summary>X, Y or P pressed over a quadrant: the bit to toggle in its word.</summary>
    public event EventHandler<(int Tile, int Quad, ushort Bit)>? QuadFlagToggled;

    /// <summary>Raised when the selected tile or lasso changes, so an inspector can follow it.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>The tiles the properties panel acts on: the lasso when there is one, else the
    /// single selected tile.</summary>
    public IEnumerable<int> SelectedTiles()
    {
        if (Selection is not { } s) { yield return SelectedTile; yield break; }
        for (int j = 0; j < s.H; j++)
            for (int i = 0; i < s.W; i++)
                yield return Bank * Map16Layout.BankTiles + (s.Y + j) * Map16Layout.Cols + s.X + i;
    }

    public Map16CanvasView() => Focusable = true;

    public void SetSheet(uint[] px, int w, int h, int tileCount)
    {
        sheetW = w; sheetH = h; TileCount = tileCount;
        sheet?.Dispose();
        sheet = LevelBitmap.FromPixels(px, w, h);
        InvalidateVisual();
        InvalidateMeasure();
    }

    public void ClearSelection() { Selection = null; InvalidateVisual(); }

    private double TileSize => 16 * Zoom;
    private double QuadSize => 8 * Zoom;

    /// <summary>Screen point → (bank-local column/row, absolute tile, visual quadrant).</summary>
    public (int Col, int Row, int Tile, int Quad)? At(Point p)
    {
        double ts = TileSize, qs = QuadSize;
        int col = (int)((p.X + Origin.X) / ts), row = (int)((p.Y + Origin.Y) / ts);
        if (col is < 0 or >= Map16Layout.Cols || row < 0 || row >= Map16Layout.BankRows) return null;
        int qcol = (int)((p.X + Origin.X) / qs), qrow = (int)((p.Y + Origin.Y) / qs);
        int quad = ((qrow & 1) << 1) | (qcol & 1);           // visual TL, TR, BL, BR
        return (col, row, Bank * Map16Layout.BankTiles + row * Map16Layout.Cols + col, quad);
    }

    protected override Size MeasureOverride(Size available)
        => new(Map16Layout.Cols * TileSize, Map16Layout.BankRows * TileSize);

    // ---- interaction ----

    private bool painting;
    private (int Col, int Row)? lassoStart, lassoEnd, moveStart;
    private Point brushOrigin;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (At(e.GetPosition(this)) is not { } h) return;
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsRightButtonPressed)
        {
            painting = true;
            e.Pointer.Capture(this);
            PaintAt(e.GetPosition(this));
        }
        else if (props.IsLeftButtonPressed)
        {
            if (Selection is { } s && h.Col >= s.X && h.Col < s.X + s.W && h.Row >= s.Y && h.Row < s.Y + s.H)
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
        // Snap the brush preview to the hovered QUADRANT, so it sits where a stamp would land.
        double q = QuadSize;
        var snapped = new Point(Math.Floor((p.X + Origin.X) / q) * q - Origin.X,
                                Math.Floor((p.Y + Origin.Y) / q) * q - Origin.Y);
        if (hq != HoverQuad || snapped != brushOrigin)
        { HoverQuad = hq; brushOrigin = snapped; InvalidateVisual(); }

        if (painting) { PaintAt(p); return; }
        if ((lassoStart is not null || moveStart is not null) && h is { } c)
        { lassoEnd = (c.Col, c.Row); InvalidateVisual(); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (painting)
        {
            painting = false;
            e.Pointer.Capture(null);
            StrokeEnded?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (lassoStart is { } a && lassoEnd is { } b)
        {
            int rx = Math.Min(a.Col, b.Col), ry = Math.Min(a.Row, b.Row);
            int rw = Math.Abs(b.Col - a.Col) + 1, rh = Math.Abs(b.Row - a.Row) + 1;
            // A single-cell lasso is a PICK, not a selection — it arms the level brush.
            if (rw == 1 && rh == 1)
            {
                Selection = null;
                SelectedTile = Bank * Map16Layout.BankTiles + ry * Map16Layout.Cols + rx;
                TilePicked?.Invoke(this, SelectedTile);
            }
            else Selection = (rx, ry, rw, rh);
        }
        else if (moveStart is { } m && lassoEnd is { } n && Selection is { } sel)
        {
            int dx = n.Col - m.Col, dy = n.Row - m.Row;
            if (dx != 0 || dy != 0)
            {
                MoveRequested?.Invoke(this, (sel.X, sel.Y, sel.W, sel.H, dx, dy));
                Selection = (sel.X + dx, sel.Y + dy, sel.W, sel.H);
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

    private void PaintAt(Point p)
    {
        if (At(p) is not { } h) return;
        double qs = QuadSize;
        int qcol = (int)((p.X + Origin.X) / qs), qrow = (int)((p.Y + Origin.Y) / qs);
        for (int j = 0; j < BrushH; j++)
            for (int i = 0; i < BrushW; i++)
            {
                int qx = qcol + i, qy = qrow + j;
                if (qx >= Map16Layout.Cols * 2 || qy >= Map16Layout.BankRows * 2) continue;
                int tile = Bank * Map16Layout.BankTiles
                         + (qy >> 1) * Map16Layout.Cols + (qx >> 1);
                QuadPainted?.Invoke(this, (tile, ((qy & 1) << 1) | (qx & 1), i, j));
            }
    }

    // ---- rendering ----

    private readonly PixelBlit blit = new();

    public override void Render(DrawingContext ctx)
    {
        double ts = TileSize;
        var full = new Rect(0, 0, Map16Layout.Cols * ts, Map16Layout.BankRows * ts);
        // Empty pages are ordinary black tiles, not a roped-off region.
        ctx.FillRectangle(Brushes.Black, full);

        if (sheet is not null && sheetH > 0)
        {
            var (v0, v1, rows, _) = Map16Layout.SheetWindow(Bank, sheetH, TileCount);
            if (rows > 0)
                blit.Draw(this, ctx, sheet, new Rect(0, v0 * sheetH, sheetW, (v1 - v0) * sheetH),
                          new Rect(0, 0, Map16Layout.Cols * ts, rows * ts), VisualRoot?.RenderScaling ?? 1);
        }

        // Page separators every 16 rows, LM-style.
        var line = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
        for (int page = 1; page < Map16Layout.BankTiles / 0x100; page++)
            ctx.DrawLine(line, new Point(0, page * 16 * ts), new Point(Map16Layout.Cols * ts, page * 16 * ts));

        // Live lasso, then the settled selection, then the armed tile.
        if ((lassoStart ?? moveStart) is { } s0 && lassoEnd is { } s1)
        {
            int rx = Math.Min(s0.Col, s1.Col), ry = Math.Min(s0.Row, s1.Row);
            int rw = Math.Abs(s1.Col - s0.Col) + 1, rh = Math.Abs(s1.Row - s0.Row) + 1;
            ctx.DrawRectangle(null, new Pen(UiColors.Band, 1.5), Cells(rx, ry, rw, rh, ts));
        }
        if (Selection is { } sel)
        {
            var r = Cells(sel.X, sel.Y, sel.W, sel.H, ts);
            ctx.FillRectangle(UiColors.SelectionFill, r);
            ctx.DrawRectangle(null, new Pen(UiColors.Selection, 2), r);
        }
        else if (SelectedTile / Map16Layout.BankTiles == Bank)
        {
            int idx = SelectedTile % Map16Layout.BankTiles;
            ctx.DrawRectangle(null, new Pen(UiColors.Selection, 2),
                              Cells(idx % Map16Layout.Cols, idx / Map16Layout.Cols, 1, 1, ts));
        }

        // Brush footprint, in QUADRANTS — this canvas edits at 8x8, so a cell-sized outline
        // would lie about where a stamp lands.
        if (HoverQuad is not null && IsPointerOver)
        {
            double qs = QuadSize;
            ctx.DrawRectangle(null, new Pen(UiColors.Brush, 1.5),
                              new Rect(brushOrigin.X, brushOrigin.Y, BrushW * qs, BrushH * qs));
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e)
    { base.OnPointerExited(e); HoverQuad = null; InvalidateVisual(); }

    private Rect Cells(int x, int y, int w, int h, double ts)
        => new(x * ts - Origin.X, y * ts - Origin.Y, w * ts, h * ts);
}
