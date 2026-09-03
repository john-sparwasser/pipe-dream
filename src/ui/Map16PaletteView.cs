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

    public double Zoom { get; set; } = 2.0;
    public int Bank { get; set; }

    /// <summary>Draw a square around each Map16 page (16 rows).</summary>
    public bool ShowPages { get; set; }
    public int TileCount => sheet.TileCount;

    /// <summary>Selected Map16 tile number, in the unified numbering the level canvas uses.</summary>
    public int Selected { get; private set; } = 0x100;

    public event EventHandler<int>? SelectionChanged;

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
        => new(Map16Layout.Cols * 16 * Zoom, Map16Layout.BankRows * 16 * Zoom);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (TileAt(e.GetPosition(this)) is { } tile)
        {
            Selected = tile;
            SelectionChanged?.Invoke(this, tile);
            InvalidateVisual();
        }
    }

    private static readonly IBrush PageBrush = new SolidColorBrush(Color.Parse("#205C99"));
    private static readonly Pen PagePen = new(PageBrush, 2);

    public override void Render(DrawingContext ctx)
    {
        double cell = 16 * Zoom;
        sheet.Draw(this, ctx, Bank, Phase, cell);

        // "Pages" toggle: a square around each Map16 page plus its number, for when the
        // subtle lines aren't enough to keep track of which page a tile lands on.
        if (ShowPages)
        {
            int perBank = Map16Layout.BankTiles / 0x100;
            for (int page = 0; page < perBank; page++)
            {
                double y = page * 16 * cell;
                ctx.DrawRectangle(null, PagePen, new Rect(1, y + 1, 16 * cell - 2, 16 * cell - 2));
                var ft = Overlay.Text($"{Bank * perBank + page:X2}", 12);
                var badge = new Rect(2, y + 2, ft.Width + 10, ft.Height + 6);
                ctx.FillRectangle(PageBrush, badge);
                Overlay.DrawText(ctx, ft, 12, badge.X + 5, badge.Center.Y);
            }
        }

        if (Selected / Map16Layout.BankTiles == Bank)
        {
            int idx = Selected % Map16Layout.BankTiles;
            Overlay.Armed(ctx, new Rect(idx % 16 * cell, idx / 16 * cell, cell, cell));
        }
    }
}
