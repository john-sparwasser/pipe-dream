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
    private readonly Map16Sheet sheet = new();

    /// <summary>Which animation phase to draw. The window steps it for every surface at once,
    /// so a tile animates the same here as it does in the level.</summary>
    public int Phase { get; set; }

    /// <summary>Tile scale. Not set from outside: the picker fits a whole 16-tile row to whatever
    /// width the drawer gives it, so the splitter IS the zoom control — and so is Alt/Cmd+wheel,
    /// which the window turns into a drawer resize (MainWindow.DrawerWheel) for every drawer
    /// sheet that fits its width.</summary>
    public double Zoom { get; private set; } = DefaultZoom;
    public const double MinZoom = 1, MaxZoom = 6, DefaultZoom = 2, ZoomStep = 0.5;
    public int Bank { get; set; }

    /// <summary>Draw a square around each Map16 page (16 rows).</summary>
    public bool ShowPages { get; set; }
    public int TileCount => sheet.TileCount;

    /// <summary>Selected Map16 tile number, in the unified numbering the level canvas uses.</summary>
    public int Selected { get; private set; } = 0x100;

    public event EventHandler<int>? SelectionChanged;

    /// <summary>A lassoed block of tiles, in bank-local cells; null when one tile is armed. It
    /// works the way the Map16 editor's lasso does: drag a rectangle, and the whole block is
    /// what the level places.</summary>
    public (int X, int Y, int W, int H)? Selection { get; private set; }

    /// <summary>A lassoed block became the level's brush: its tiles row-major, W by H.</summary>
    public event EventHandler<(ushort[] Tiles, int W, int H)>? BrushPicked;

    private (int X, int Y)? lassoStart, lassoEnd;

    public void ClearSelection() { Selection = null; InvalidateVisual(); }

    /// <summary>What each block releases when hit, drawn over it; null turns it off.</summary>
    public SpawnOverlay? Spawns { get; set; }

    public Map16PaletteView() => Focusable = true;

    public void SetSheet(uint[]?[] px, int w, int h, int tileCount)
    {
        sheet.SetSheet(px, w, h, tileCount, Phase);
        InvalidateVisual();
        InvalidateMeasure();
    }

    public void SetPlaceholder(uint[]?[] px) { sheet.SetPlaceholder(px); InvalidateVisual(); }

    public void SetBgSheet(uint[]?[] px, int w, int h) { sheet.SetBgSheet(px, w, h, Phase); InvalidateVisual(); }

    /// <summary>Tile under a screen point, or null past the end of the bank.</summary>
    public int? TileAt(Point p)
    {
        if (Lasso.CellAt(p, 16 * Zoom, Map16Layout.Cols, Map16Layout.BankRows) is not { } c) return null;
        return Bank * Map16Layout.BankTiles + c.Y * Map16Layout.Cols + c.X;
    }

    /// <summary>Margin around the sheet inside the drawer (matches the XAML).</summary>
    public const double Pad = 8;

    /// <summary>
    /// How wide the picker's CONTENT is at a given tile zoom: a full row of Map16 tiles plus
    /// its margins. The drawer is sized from this rather than from a guessed constant —
    /// a hardcoded width clips the right-hand tiles the moment the tile size changes, which
    /// is not a rendering bug the user can diagnose, it just looks like missing tiles.
    /// </summary>
    public static double ContentWidth(double zoom) => Map16Layout.Cols * 16 * zoom + Pad * 2;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width))
            Zoom = Math.Clamp(availableSize.Width / (Map16Layout.Cols * 16), MinZoom, MaxZoom);
        return new(Map16Layout.Cols * 16 * Zoom, Map16Layout.BankRows * 16 * Zoom);
    }

    private (int X, int Y)? CellAt(Point p) => Lasso.CellAt(p, 16 * Zoom, Map16Layout.Cols, Map16Layout.BankRows);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || CellAt(e.GetPosition(this)) is not { } c) return;
        lassoStart = lassoEnd = c;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (lassoStart is null) return;
        // Past the edge the band stops at the edge, so a fast drag still lands a full block.
        var c = Lasso.Clamped(e.GetPosition(this), 16 * Zoom, Map16Layout.Cols, Map16Layout.BankRows);
        if (c != lassoEnd) { lassoEnd = c; InvalidateVisual(); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (lassoStart is not { } a) return;
        var r = Lasso.Span(a, lassoEnd ?? a);
        lassoStart = lassoEnd = null;
        e.Pointer.Capture(null);

        int TileOf(int x, int y) => Bank * Map16Layout.BankTiles + y * Map16Layout.Cols + x;
        Selected = TileOf(r.X, r.Y);
        if (r is { W: 1, H: 1 })
        {
            // One tile is a pick, as it always was.
            Selection = null;
            SelectionChanged?.Invoke(this, Selected);
        }
        else
        {
            Selection = r;
            var tiles = new ushort[r.W * r.H];
            for (int y = 0; y < r.H; y++)
                for (int x = 0; x < r.W; x++) tiles[y * r.W + x] = (ushort)TileOf(r.X + x, r.Y + y);
            BrushPicked?.Invoke(this, (tiles, r.W, r.H));
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        double cell = 16 * Zoom;
        sheet.Draw(this, ctx, Bank, Phase, cell);
        if (ShowPages) Map16Sheet.DrawPages(ctx, Bank, cell);
        if (Spawns is { } spawns)
        {
            var vis = PixelBlit.Visible(this);
            int r0 = Math.Max(0, (int)(vis.Y / cell)), r1 = Math.Min(Map16Layout.BankRows - 1, (int)(vis.Bottom / cell));
            for (int r = r0; r <= r1; r++)
                for (int c = 0; c < Map16Layout.Cols; c++)
                    spawns.Draw(this, ctx, Bank * Map16Layout.BankTiles + r * Map16Layout.Cols + c, new Rect(c * cell, r * cell, cell, cell));
        }

        // The live band, else the settled block, else the one armed tile — the Map16 editor's
        // order, drawn with its pens.
        if (lassoStart is { } a && lassoEnd is { } b)
        {
            var l = Lasso.Span(a, b);
            Overlay.Band(ctx, new Rect(l.X * cell, l.Y * cell, l.W * cell, l.H * cell));
        }
        else if (Selection is { } sel)
            Overlay.Selection(ctx, new Rect(sel.X * cell, sel.Y * cell, sel.W * cell, sel.H * cell));
        else if (Selected / Map16Layout.BankTiles == Bank)
        {
            int idx = Selected % Map16Layout.BankTiles;
            Overlay.Armed(ctx, new Rect(idx % 16 * cell, idx / 16 * cell, cell, cell));
        }
    }
}
