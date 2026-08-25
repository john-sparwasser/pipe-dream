using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PipeDream.Ui;

/// <summary>
/// The GFX canvas mode: one GFX file's 8x8 tiles as an editable pixel sheet, 16 tiles per row.
///
/// Controls match the ImGui editor's GFX mode exactly:
///
///   LEFT drag     the current tool acts (pencil, fill, eraser; the eyedropper reads)
///   RIGHT click   eyedrop the colour under the cursor, whatever the tool
///   F             cycle the tools
///   [ ]           zoom out / in
///   up / down     step the palette row
///
/// Note the difference from the level canvas, where right-drag paints and left selects. This
/// mode uses the ordinary paint-program bindings, and that is what the ImGui version does too;
/// selecting is a tool here (like any paint program) rather than a button.
/// </summary>
public class GfxCanvasView : Control
{
    /// <summary>Screen pixels per GFX pixel.</summary>
    public double Zoom { get; set; } = 8;

    /// <summary>Tiles in the file, so the canvas can stop responding past the end of the sheet —
    /// the last row is usually part empty.</summary>
    public int Tiles { get; set; }

    /// <summary>Raised for every sheet pixel a drag passes through.</summary>
    public event EventHandler<(int X, int Y)>? PixelPainted;
    public event EventHandler? StrokeEnded;

    /// <summary>Right-click: the colour index under the cursor was picked.</summary>
    public event EventHandler<(int X, int Y)>? ColorPicked;

    /// <summary>Keys the mode owns: F cycles the tool, [ and ] zoom, up/down step the palette row.
    /// They live here rather than on the window because the window never sees them — the top level
    /// eats arrow keys for focus navigation before a bubbling handler runs.</summary>
    public event EventHandler? ToolToggled;
    public event EventHandler<int>? ZoomStepped;
    public event EventHandler<int>? PalRowStepped;

    /// <summary>Select tool active: left-drag rubber-bands a selection, or drags the one under
    /// the press. The view owns the rectangle; the byte work is the subscriber's.</summary>
    public bool Selecting { get; set; }

    /// <summary>The selection in sheet pixels, or null.</summary>
    public (int X, int Y, int W, int H)? Selection
    {
        get => selection;
        set { selection = value; InvalidateVisual(); }
    }
    private (int X, int Y, int W, int H)? selection;

    /// <summary>A drag grabbed the selection: capture its pixels before any preview moves them.</summary>
    public event EventHandler<(int X, int Y, int W, int H)>? SelectionMoveStarted;
    /// <summary>Total offset from where the grab started, for live preview.</summary>
    public event EventHandler<(int Dx, int Dy)>? SelectionMoved;
    public event EventHandler? SelectionMoveEnded;

    private (int X, int Y)? bandAnchor;
    private ((int X, int Y, int W, int H) Home, (int X, int Y) Grab)? moveDrag;

    // ---- floating paste layer ----
    // Pasted pixels ride ABOVE the sheet until dropped: nothing is written to the file while
    // they are positioned, so the drop is one undo entry and Esc simply throws them away.

    /// <summary>Where the floating paste sits in sheet pixels, or null when nothing floats.</summary>
    public (int X, int Y, int W, int H)? Float { get; private set; }
    private WriteableBitmap? floatBmp;
    private readonly PixelBlit floatBlit = new();
    private ((int X, int Y) Home, (int X, int Y) Grab)? floatDrag;

    /// <summary>A press landed outside the float: whoever owns the bytes should drop it into the
    /// file where it rests (and then clear it).</summary>
    public event EventHandler? FloatDropRequested;

    /// <summary>Start floating pasted pixels (RGBA, transparent where the sheet should show
    /// through) at the top-left corner. The float replaces the marquee until it drops.</summary>
    public void ShowFloat(uint[] px, int w, int h)
    {
        floatBmp?.Dispose();
        floatBmp = LevelBitmap.FromPixels(px, w, h);
        Float = (0, 0, w, h);
        Selection = null;
        InvalidateVisual();
    }

    /// <summary>Take the float down — after a drop wrote its bytes, or to discard it (Esc).</summary>
    public void ClearFloat()
    {
        floatBmp?.Dispose();
        floatBmp = null;
        Float = null;
        floatDrag = null;
        InvalidateVisual();
    }

    // ponytail: Windows has no stock open/closed-hand cursors; Hand + DragMove are the nearest
    // native pair. Custom bitmap cursors if the real grab hands ever matter.
    private static readonly Cursor OpenHand = new(StandardCursorType.Hand);
    private static readonly Cursor ClosedHand = new(StandardCursorType.DragMove);

    private static bool Inside((int X, int Y, int W, int H)? r, (int X, int Y) p)
        => r is { } s && p.X >= s.X && p.X < s.X + s.W && p.Y >= s.Y && p.Y < s.Y + s.H;

    private WriteableBitmap? sheet;
    private int sheetW, sheetH;
    private readonly PixelBlit blit = new();

    public (int X, int Y)? Hover { get; private set; }

    public GfxCanvasView() => Focusable = true;

    public void SetSheet(uint[] px, int w, int h)
    {
        sheetW = w; sheetH = h;
        sheet?.Dispose();
        sheet = w > 0 && h > 0 ? LevelBitmap.FromPixels(px, w, h) : null;
        InvalidateVisual();
        InvalidateMeasure();
    }

    /// <summary>Screen point → sheet pixel, or null outside the tiles the file actually has.</summary>
    public (int X, int Y)? PixelAt(Point p)
    {
        if (Zoom <= 0 || sheetW == 0) return null;
        int x = (int)(p.X / Zoom), y = (int)(p.Y / Zoom);
        if (x < 0 || y < 0 || x >= sheetW || y >= sheetH) return null;
        return (y / 8) * 16 + x / 8 < Tiles ? (x, y) : null;
    }

    /// <summary>Screen point → nearest sheet pixel, for drags that run past the edge.</summary>
    private (int X, int Y)? ClampedPixelAt(Point p)
        => Zoom <= 0 || sheetW == 0 || sheetH == 0 ? null
         : (Math.Clamp((int)(p.X / Zoom), 0, sheetW - 1),
            Math.Clamp((int)(p.Y / Zoom), 0, sheetH - 1));

    protected override Size MeasureOverride(Size available) => new(sheetW * Zoom, sheetH * Zoom);

    private bool painting;
    private (int X, int Y)? lastPainted;

    /// <summary>Drag out a SHAPE instead of painting pixel by pixel (the Rect and Ellipse
    /// tools). Set alongside <see cref="Selecting"/> by whoever owns the tool; never both.</summary>
    public bool Ranging { get; set; }

    private (int X, int Y)? shapeAnchor;

    /// <summary>The bounding box being dragged right now, for the preview. Null between drags.</summary>
    public (int X, int Y, int W, int H)? ShapePreview { get; private set; }

    /// <summary>A finished shape drag, as a bounding box in sheet pixels. The canvas neither
    /// draws the shape nor knows WHICH shape it is — it reports the box and the editor writes
    /// the bytes, the same split as PixelPainted.</summary>
    public event EventHandler<(int X, int Y, int W, int H)>? ShapeDragged;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (PixelAt(e.GetPosition(this)) is not { } px) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed) { ColorPicked?.Invoke(this, px); return; }
        if (!props.IsLeftButtonPressed) return;

        if (Selecting)
        {
            if (Float is not null)
            {
                if (Inside(Float, px))
                {
                    floatDrag = ((Float.Value.X, Float.Value.Y), px);
                    Cursor = ClosedHand;
                    e.Pointer.Capture(this);
                    return;
                }
                // Clicking elsewhere drops the float where it rests; the press then goes on to
                // start a fresh selection like any other.
                FloatDropRequested?.Invoke(this, EventArgs.Empty);
            }
            if (Inside(Selection, px))
            {
                moveDrag = (Selection!.Value, px);
                Cursor = ClosedHand;
                SelectionMoveStarted?.Invoke(this, Selection.Value);
            }
            else { bandAnchor = px; Selection = (px.X, px.Y, 1, 1); }
            e.Pointer.Capture(this);
            return;
        }

        if (Ranging)
        {
            shapeAnchor = px;
            ShapePreview = (px.X, px.Y, 1, 1);
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        painting = true;
        lastPainted = px;
        e.Pointer.Capture(this);
        PixelPainted?.Invoke(this, px);
    }

    /// <summary>The hover ends with the pointer, as on the level canvas.</summary>
    protected override void OnPointerExited(PointerEventArgs e)
    { base.OnPointerExited(e); Hover = null; InvalidateVisual(); }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = PixelAt(e.GetPosition(this));
        if (at != Hover) { Hover = at; InvalidateVisual(); }
        // The grab hands: closed while dragging, open over anything draggable, default elsewhere.
        Cursor = floatDrag is not null || moveDrag is not null ? ClosedHand
               : Selecting && at is { } h2 && (Inside(Float, h2) || Inside(Selection, h2)) ? OpenHand
               : null;
        // Selection drags clamp to the sheet instead of dying past its edge, so a fast drag to
        // a border lands ON the border.
        if (floatDrag is { } fd && Float is { } f && ClampedPixelAt(e.GetPosition(this)) is { } fp)
        {
            Float = (Math.Clamp(fd.Home.X + fp.X - fd.Grab.X, 0, Math.Max(0, sheetW - f.W)),
                     Math.Clamp(fd.Home.Y + fp.Y - fd.Grab.Y, 0, Math.Max(0, sheetH - f.H)),
                     f.W, f.H);
            InvalidateVisual();
            return;
        }
        if ((bandAnchor is not null || moveDrag is not null)
            && ClampedPixelAt(e.GetPosition(this)) is { } cp)
        {
            if (bandAnchor is { } a)
                Selection = (Math.Min(a.X, cp.X), Math.Min(a.Y, cp.Y),
                             Math.Abs(cp.X - a.X) + 1, Math.Abs(cp.Y - a.Y) + 1);
            else if (moveDrag is { } d)
            {
                int dx = Math.Clamp(cp.X - d.Grab.X, -d.Home.X, sheetW - d.Home.W - d.Home.X);
                int dy = Math.Clamp(cp.Y - d.Grab.Y, -d.Home.Y, sheetH - d.Home.H - d.Home.Y);
                Selection = (d.Home.X + dx, d.Home.Y + dy, d.Home.W, d.Home.H);
                SelectionMoved?.Invoke(this, (dx, dy));
            }
            return;
        }
        // The rectangle drag clamps to the sheet like a selection drag, so a fast drag past an
        // edge lands ON the edge rather than stopping short.
        if (shapeAnchor is { } ra && ClampedPixelAt(e.GetPosition(this)) is { } rp)
        {
            ShapePreview = (Math.Min(ra.X, rp.X), Math.Min(ra.Y, rp.Y),
                           Math.Abs(rp.X - ra.X) + 1, Math.Abs(rp.Y - ra.Y) + 1);
            InvalidateVisual();
            return;
        }
        if (!painting || at is not { } px) return;
        // Interpolate: at speed the pointer skips pixels, and a stroke with gaps in it is a bug
        // rather than a style. Same rule as the level canvas.
        if (lastPainted is { } prev)
            foreach (var step in Between(prev, px)) PixelPainted?.Invoke(this, step);
        else PixelPainted?.Invoke(this, px);
        lastPainted = px;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (floatDrag is not null)
        {
            floatDrag = null;
            Cursor = OpenHand;               // still over what was just dragged
            e.Pointer.Capture(null);
            return;
        }
        if (bandAnchor is not null || moveDrag is not null)
        {
            bool moved = moveDrag is not null;
            bandAnchor = null;
            moveDrag = null;
            Cursor = moved ? OpenHand : Cursor;
            e.Pointer.Capture(null);
            if (moved) SelectionMoveEnded?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (shapeAnchor is not null)
        {
            var done = ShapePreview;
            shapeAnchor = null;
            ShapePreview = null;
            e.Pointer.Capture(null);
            InvalidateVisual();
            if (done is { } r) ShapeDragged?.Invoke(this, r);
            return;
        }
        if (!painting) return;
        painting = false;
        lastPainted = null;
        e.Pointer.Capture(null);
        StrokeEnded?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.F: ToolToggled?.Invoke(this, EventArgs.Empty); e.Handled = true; break;
            case Key.OemOpenBrackets: ZoomStepped?.Invoke(this, -1); e.Handled = true; break;
            case Key.OemCloseBrackets: ZoomStepped?.Invoke(this, 1); e.Handled = true; break;
            case Key.Up: PalRowStepped?.Invoke(this, -1); e.Handled = true; break;
            case Key.Down: PalRowStepped?.Invoke(this, 1); e.Handled = true; break;
        }
    }

    private static void Marquee(DrawingContext ctx, (int X, int Y, int W, int H) s, double z)
    {
        var r = new Rect(s.X * z, s.Y * z, s.W * z, s.H * z);
        ctx.DrawRectangle(null, new Pen(Brushes.Black, 1), r);
        ctx.DrawRectangle(null, new Pen(Brushes.White, 1) { DashStyle = DashStyle.Dash }, r);
    }

    private static IEnumerable<(int X, int Y)> Between((int X, int Y) a, (int X, int Y) b)
    {
        int steps = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        if (steps == 0) { yield return b; yield break; }
        for (int i = 1; i <= steps; i++)
            yield return (a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps);
    }

    public override void Render(DrawingContext ctx)
    {
        double z = Zoom;
        var full = new Rect(0, 0, sheetW * z, sheetH * z);
        ctx.FillRectangle(Brushes.Black, full);
        if (sheet is null) return;
        blit.Draw(this, ctx, sheet, new Rect(0, 0, sheetW, sheetH), full, VisualRoot?.RenderScaling ?? 1);

        // The 8x8 tile grid always; the per-pixel grid only once a pixel is big enough to see
        // it round, otherwise the lines are the picture.
        var tileLine = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)));
        var pixLine = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)));
        int step = z >= 8 ? 1 : 8;
        for (int x = 0; x <= sheetW; x += step)
            ctx.DrawLine((x & 7) == 0 ? tileLine : pixLine, new Point(x * z, 0), new Point(x * z, full.Height));
        for (int y = 0; y <= sheetH; y += step)
            ctx.DrawLine((y & 7) == 0 ? tileLine : pixLine, new Point(0, y * z), new Point(full.Width, y * z));

        // Marching-ants marquee: solid dark under dashed white stays visible on any pixels.
        if (Selection is { } sel) Marquee(ctx, sel, z);

        // The shape being dragged, in the same marquee. Its BOUNDING BOX, not its outline: the
        // pixels are not written until the drag ends, and a box reads as "about to happen"
        // where a preview of the shape itself would read as already drawn.
        if (ShapePreview is { } rp2) Marquee(ctx, rp2, z);

        // The floating paste rides above the sheet, marquee'd like a selection.
        if (Float is { } fl && floatBmp is not null)
        {
            floatBlit.Draw(this, ctx, floatBmp, new Rect(0, 0, fl.W, fl.H),
                           new Rect(fl.X * z, fl.Y * z, fl.W * z, fl.H * z),
                           VisualRoot?.RenderScaling ?? 1);
            Marquee(ctx, fl, z);
        }

        if (Hover is not { } h) return;
        ctx.DrawRectangle(null, new Pen(UiColors.Selection, 1.5), new Rect(h.X * z, h.Y * z, z, z));
        ctx.DrawRectangle(null, new Pen(UiColors.Band, 1),
                          new Rect((h.X & ~7) * z, (h.Y & ~7) * z, 8 * z, 8 * z));
    }
}
