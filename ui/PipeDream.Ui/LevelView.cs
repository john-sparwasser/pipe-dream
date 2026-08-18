using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PipeDream.Ui;

/// <summary>
/// The level canvas. Controls are a deliberate match for the ImGui editor's ObjectTool, so
/// muscle memory carries over:
///
///   RIGHT click/drag   stamp the drawer's tile brush as Direct Map16 objects. Right-click
///                      with a selection DUPLICATES it at the cursor instead.
///   LEFT click         on a selected object → drag to move it
///                      elsewhere            → rubber-band select (live, while dragging)
///   LEFT click, still  cycle the overlap stack under the cursor (LM-style: topmost, then
///                      the one beneath, wrapping)
///   CTRL + LEFT drag   grab the covered tiles as the stamp brush instead of selecting
///   DELETE             delete the selection
///   WHEEL              scroll horizontally (SHIFT: vertically). Vertical levels keep the
///                      normal up/down wheel.
///
/// Painting on the LEFT button was the obvious guess and the wrong one — in this editor the
/// left button belongs to selection, exactly as in Lunar Magic.
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
    public LevelEdit? Edit { get; set; }
    public int Phase { get; set; }
    public bool ShowGrid { get; set; } = true;
    public bool Vertical { get; set; }

    public (int X, int Y)? HoverCell { get; private set; }
    public (int X, int Y)? LastClickedCell { get; private set; }

    /// <summary>Raised for every cell a RIGHT drag passes through — the paint stroke.</summary>
    public event EventHandler<(int X, int Y)>? CellPainted;
    public event EventHandler? StrokeEnded;
    public event EventHandler<(int X, int Y)>? CellPressed;

    /// <summary>Right-click with a selection: duplicate it here rather than stamping.</summary>
    public event EventHandler<(int X, int Y)>? DuplicateRequested;

    /// <summary>Ctrl+drag finished: take these cells as the stamp brush.</summary>
    public event EventHandler<(int X, int Y, int W, int H)>? GrabRequested;

    public event EventHandler? SelectionChanged;
    public event EventHandler? DeleteRequested;

    /// <summary>Wheel scrolling is handled here (horizontal by default) and applied by the
    /// host, which owns the scroll viewer.</summary>
    public event EventHandler<(double Dx, double Dy)>? ScrollRequested;

    static LevelView() => AffectsRender<LevelView>(ZoomProperty);

    public LevelView() => Focusable = true;

    /// <summary>Screen point → 16x16 cell, or null when outside the composed level.</summary>
    public (int X, int Y)? CellAt(Point p)
    {
        if (Source is not { HasImages: true } src || Zoom <= 0) return null;
        int lx = (int)((p.X + Origin.X) / Zoom), ly = (int)((p.Y + Origin.Y) / Zoom);
        if (lx < 0 || ly < 0 || lx >= src.PxW || ly >= src.PxH) return null;
        return (lx / 16, ly / 16);
    }

    // ---- drag state, mirroring the ImGui tool's dragStart/dragEnd/moveDrag ----
    private (int X, int Y)? bandStart, bandEnd, moveStart;
    private bool painting, grabbing;
    private (int X, int Y)? lastPainted;

    public (int X, int Y, int W, int H)? Band =>
        bandStart is { } a && bandEnd is { } b
            ? (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X) + 1, Math.Abs(b.Y - a.Y) + 1)
            : null;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (CellAt(e.GetPosition(this)) is not { } cell) return;
        var props = e.GetCurrentPoint(this).Properties;
        LastClickedCell = cell;
        CellPressed?.Invoke(this, cell);

        if (props.IsRightButtonPressed)
        {
            // Right-click with a selection duplicates it; otherwise it stamps the brush.
            if (Edit is { Selection.Count: > 0 }) DuplicateRequested?.Invoke(this, cell);
            else
            {
                painting = true;
                lastPainted = cell;
                e.Pointer.Capture(this);
                CellPainted?.Invoke(this, cell);
            }
        }
        else if (props.IsLeftButtonPressed)
        {
            grabbing = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            // Grabbing always bands, even over a selected object — Ctrl+drag is "take these
            // tiles", not "move this".
            if (!grabbing && Edit?.ObjectAt(cell.X, cell.Y) is int hit && Edit.Selection.Contains(hit))
                moveStart = cell;
            else
                bandStart = cell;
            bandEnd = cell;
            e.Pointer.Capture(this);
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var cell = CellAt(e.GetPosition(this));
        if (cell != HoverCell) { HoverCell = cell; InvalidateVisual(); }
        if (cell is not { } c) return;

        if (painting)
        {
            // Every cell the drag crosses stamps, not just the ones a move event lands on —
            // at speed the pointer skips cells and a stroke with holes in it is a bug.
            if (lastPainted is { } prev) foreach (var s in Between(prev, c)) CellPainted?.Invoke(this, s);
            else CellPainted?.Invoke(this, c);
            lastPainted = c;
            return;
        }
        if (bandStart is not null || moveStart is not null)
        {
            bandEnd = c;
            // Live selection while banding, as the ImGui tool does — you see what you will get
            // before releasing. Ctrl+drag is a grab, so it selects nothing.
            if (bandStart is not null && !grabbing && Band is { } b && bandStart != bandEnd)
            {
                Edit?.SelectInRect(b.X, b.Y, b.W, b.H);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (painting)
        {
            painting = false;
            lastPainted = null;
            e.Pointer.Capture(null);
            StrokeEnded?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (bandStart is { } a && bandEnd is { } b)
        {
            if (a == b) { Edit?.CycleSelectionAt(a.X, a.Y); SelectionChanged?.Invoke(this, EventArgs.Empty); }
            else if (grabbing && Band is { } g) GrabRequested?.Invoke(this, g);
        }
        else if (moveStart is { } m && bandEnd is { } n)
        {
            if (m == n) { Edit?.CycleSelectionAt(m.X, m.Y); SelectionChanged?.Invoke(this, EventArgs.Empty); }
            else if (Edit?.MoveSelected(n.X - m.X, n.Y - m.Y) == true)
                SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        bandStart = bandEnd = moveStart = null;
        grabbing = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Horizontal levels scroll sideways with the wheel (Shift = vertical); vertical
        // levels keep the normal up/down wheel. Same rule as the ImGui viewport.
        double step = e.Delta.Y * 64 * Zoom;
        if (Vertical) return;                       // let the scroll viewer handle it
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) ScrollRequested?.Invoke(this, (0, -step));
        else ScrollRequested?.Invoke(this, (-step, 0));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Delete && Edit is { Selection.Count: > 0 })
        {
            DeleteRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    /// <summary>Cells on the line between two drag samples, exclusive of the start.</summary>
    private static IEnumerable<(int X, int Y)> Between((int X, int Y) a, (int X, int Y) b)
    {
        int steps = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
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

        // Selection: the object's real footprint, from the tracked render.
        if (Edit is { } ed)
        {
            var pen = new Pen(Brushes.DodgerBlue, 1.5);
            foreach (int i in ed.Selection)
                if (ed.BBox(i) is { } b) ctx.DrawRectangle(null, pen, CellRect(b.X, b.Y, b.W, b.H, z));
        }

        // Rubber band: cyan while selecting, green while grabbing tiles — the ImGui colours.
        if (Band is { } band && (bandStart is not null || moveStart is not null))
            ctx.DrawRectangle(null, new Pen(grabbing ? Brushes.Lime : Brushes.Cyan, 1.5),
                              CellRect(band.X, band.Y, band.W, band.H, z));

        if (HoverCell is { } h)
            ctx.DrawRectangle(null, new Pen(Brushes.White, 1), CellRect(h.X, h.Y, 1, 1, z));
    }

    private Rect CellRect(int x, int y, int w, int h, double z)
        => new(x * 16 * z - Origin.X, y * 16 * z - Origin.Y, w * 16 * z, h * 16 * z);

    // SMW screens are 16 cells wide; the boundary lines are the editor's main orientation cue.
    private void DrawScreenBoundaries(DrawingContext ctx, Rect dst, double z)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
        double step = 16 * 16 * z;
        for (double x = -Origin.X % step; x < dst.Width; x += step)
            ctx.DrawLine(pen, new Point(x, 0), new Point(x, dst.Height));
    }
}
