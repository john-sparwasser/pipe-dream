using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PipeDream.Ui;

/// <summary>
/// The level canvas as a retained control: blits the composed level bitmap and draws the
/// overlays (grid, selection) with <see cref="DrawingContext"/>. The ImGui version drew the
/// same things into a per-frame draw list; the translation is close to 1:1
/// (AddRectFilled → FillRectangle, AddRect/AddLine → DrawRectangle/DrawLine).
///
/// Interaction arrives as pointer events instead of being re-derived from the mouse position
/// every frame, so hit-testing lives in one place and can be exercised headlessly. Cell
/// coordinates are exposed through <see cref="CellAt"/> for exactly that reason.
/// </summary>
public class LevelView : Control
{
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<LevelView, double>(nameof(Zoom), 2.0);

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>Scroll offset in level pixels (not screen pixels).</summary>
    public Point Origin { get; set; }

    public LevelBitmap? Source { get; set; }
    public int Phase { get; set; }
    public bool ShowGrid { get; set; } = true;

    /// <summary>Last cell the pointer went down on — what a headless test asserts on.</summary>
    public (int X, int Y)? LastClickedCell { get; private set; }

    /// <summary>Cell under the pointer, for the status readout and the hover outline.</summary>
    public (int X, int Y)? HoverCell { get; private set; }

    public event EventHandler<(int X, int Y)>? CellPressed;

    /// <summary>Raised for every cell a drag passes through, including the first — the paint
    /// stroke. <see cref="StrokeEnded"/> closes the undo group.</summary>
    public event EventHandler<(int X, int Y)>? CellPainted;

    public event EventHandler? StrokeEnded;

    static LevelView()
    {
        // Custom-drawn content: repaint when the things it is drawn from change.
        AffectsRender<LevelView>(ZoomProperty);
    }

    public LevelView() => Focusable = true;

    /// <summary>Screen point → 16x16 cell, or null when outside the composed level.</summary>
    public (int X, int Y)? CellAt(Point p)
    {
        if (Source is not { HasImages: true } src || Zoom <= 0) return null;
        int lx = (int)((p.X + Origin.X) / Zoom), ly = (int)((p.Y + Origin.Y) / Zoom);
        if (lx < 0 || ly < 0 || lx >= src.PxW || ly >= src.PxH) return null;
        return (lx / 16, ly / 16);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (CellAt(e.GetPosition(this)) is not { } cell) return;
        LastClickedCell = cell;
        CellPressed?.Invoke(this, cell);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            painting = true;
            e.Pointer.Capture(this);            // keep the stroke even if it leaves the control
            CellPainted?.Invoke(this, cell);
            // Seed the interpolation from the press cell, or the gap between it and the
            // first move sample is never filled and every stroke starts with a hole.
            lastPainted = cell;
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var cell = CellAt(e.GetPosition(this));
        if (cell != HoverCell) { HoverCell = cell; InvalidateVisual(); }
        // Every cell the drag crosses paints, not just the ones a move event happens to land
        // on — at speed the pointer skips cells, and a stroke with holes in it is a bug.
        if (painting && cell is { } c)
        {
            if (lastPainted is { } prev) foreach (var s in Between(prev, c)) CellPainted?.Invoke(this, s);
            else CellPainted?.Invoke(this, c);
            lastPainted = c;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!painting) return;
        painting = false;
        lastPainted = null;
        e.Pointer.Capture(null);
        StrokeEnded?.Invoke(this, EventArgs.Empty);
    }

    private bool painting;
    private (int X, int Y)? lastPainted;

    /// <summary>Cells on the line between two drag samples, exclusive of the start.</summary>
    private static IEnumerable<(int X, int Y)> Between((int X, int Y) a, (int X, int Y) b)
    {
        int dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y);
        int steps = Math.Max(dx, dy);
        if (steps == 0) { yield return b; yield break; }
        for (int i = 1; i <= steps; i++)
            yield return (a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps);
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(Brushes.Black, bounds);
        if (Source?.For(Phase) is not { } bmp) return;

        // Draw only the visible slice, scaled — the level is far wider than the viewport, so
        // blitting the whole bitmap every frame would scale megabytes for nothing.
        double z = Zoom;
        var src = new Rect(Origin.X / z, Origin.Y / z, Math.Min(bounds.Width / z, bmp.PixelSize.Width),
                           Math.Min(bounds.Height / z, bmp.PixelSize.Height));
        var dst = new Rect(0, 0, src.Width * z, src.Height * z);
        ctx.DrawImage(bmp, src, dst);

        if (ShowGrid) DrawScreenBoundaries(ctx, dst, z);
        // Hover outline only — the "last clicked" marker was spike scaffolding, and with
        // painting wired up the cell under the cursor is the useful thing to show.
        if (HoverCell is { } h)
            ctx.DrawRectangle(null, new Pen(Brushes.White, 1),
                              new Rect(h.X * 16 * z - Origin.X, h.Y * 16 * z - Origin.Y, 16 * z, 16 * z));
    }

    // SMW screens are 16 cells wide; the boundary lines are the editor's main orientation cue.
    private void DrawScreenBoundaries(DrawingContext ctx, Rect dst, double z)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
        double step = 16 * 16 * z;
        for (double x = -Origin.X % step; x < dst.Width; x += step)
            ctx.DrawLine(pen, new Point(x, 0), new Point(x, dst.Height));
    }
}
