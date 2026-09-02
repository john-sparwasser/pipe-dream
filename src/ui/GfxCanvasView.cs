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
        set { selection = value; SelectionChanged?.Invoke(this, EventArgs.Empty); InvalidateVisual(); }
    }
    private (int X, int Y, int W, int H)? selection;

    /// <summary>The marquee changed — appeared, moved, resized or went away. One funnel for
    /// everything that touches it, so a bar button that only works on a selection can grey
    /// itself without anyone remembering to tell it.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>A drag grabbed the selection. The owner is expected to LIFT it — take the
    /// pixels off the sheet and hand them back through <see cref="ShowFloat"/> — after which
    /// the drag continues as an ordinary float drag. Dragging a selection and dragging a paste
    /// are then the same gesture over the same layer, which is why neither disturbs the pixels
    /// it passes over until it lands.</summary>
    public event EventHandler<(int X, int Y, int W, int H)>? SelectionMoveStarted;

    private (int X, int Y)? bandAnchor;

    // ---- floating layer ----
    // Pasted or lifted pixels ride ABOVE the sheet until dropped: nothing under them is written
    // while they are positioned, so the drop is one undo entry and Esc throws it away.

    /// <summary>Where the floating paste sits in sheet pixels, or null when nothing floats.</summary>
    public (int X, int Y, int W, int H)? Float { get; private set; }
    private WriteableBitmap? floatBmp;
    private readonly PixelBlit floatBlit = new();
    private ((int X, int Y) Home, (int X, int Y) Grab)? floatDrag;

    /// <summary>A press landed outside the float: whoever owns the bytes should drop it into the
    /// file where it rests (and then clear it).</summary>
    public event EventHandler? FloatDropRequested;

    /// <summary>Start floating pixels (RGBA, transparent where the sheet should show through)
    /// at (x,y) — the corner for a paste, where it was lifted from for a move. The float
    /// replaces the marquee until it drops.</summary>
    public void ShowFloat(uint[] px, int w, int h, int x = 0, int y = 0)
    {
        floatBmp?.Dispose();
        floatBmp = LevelBitmap.FromPixels(px, w, h);
        Float = (x, y, w, h);
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

    private static readonly Cursor OpenHand = UiCursors.Hand, ClosedHand = UiCursors.Grab;

    private WriteableBitmap? sheet;
    private int sheetW, sheetH;
    private readonly PixelBlit blit = new();

    public (int X, int Y)? Hover { get; private set; }

    public GfxCanvasView()
    {
        Focusable = true;
        stroke = new(px => PixelPainted?.Invoke(this, px));
    }

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
        => Lasso.CellAt(p, Zoom, sheetW, sheetH) is { } px && (px.Y / 8) * 16 + px.X / 8 < Tiles ? px : null;

    /// <summary>Screen point → nearest sheet pixel, for drags that run past the edge.</summary>
    private (int X, int Y)? ClampedPixelAt(Point p) => Lasso.Clamped(p, Zoom, sheetW, sheetH);

    protected override Size MeasureOverride(Size available) => new(sheetW * Zoom, sheetH * Zoom);

    private readonly Stroke stroke;

    /// <summary>Drag out a SHAPE instead of painting pixel by pixel (the Rect and Ellipse
    /// tools). Set alongside <see cref="Selecting"/> by whoever owns the tool; never both.</summary>
    public bool Ranging { get; set; }

    private (int X, int Y)? shapeAnchor;

    /// <summary>The drag being made right now, as WHERE IT STARTED and where it is — not a
    /// normalized box. A line needs its direction (\ and / share one box) and a shape that does
    /// not care can normalize for itself. Null between drags.</summary>
    public (int X0, int Y0, int X1, int Y1)? ShapePreview { get; private set; }

    /// <summary>A finished shape drag, as its two corners in sheet pixels. The canvas neither
    /// draws the shape nor knows WHICH shape it is — it reports the drag and the editor writes
    /// the bytes, the same split as PixelPainted.</summary>
    public event EventHandler<(int X0, int Y0, int X1, int Y1)>? ShapeDragged;

    /// <summary>What the drag would actually paint: its pixels and their colour (0xAABBGGRR).
    /// Asked at every frame rather than pushed, so the preview follows the tool, the palette row
    /// and the colour without anyone remembering to refresh it. Null — no supplier, or nothing
    /// to draw — falls back to a marquee of the bounding box.</summary>
    public Func<(int X0, int Y0, int X1, int Y1), (IEnumerable<(int X, int Y)> Px, uint Rgba)?>? ShapeInk;

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
                if (Lasso.Contains(Float, px))
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
            if (Lasso.Contains(Selection, px))
            {
                Cursor = ClosedHand;
                SelectionMoveStarted?.Invoke(this, Selection!.Value);
                // The owner lifted it onto the floating layer; the rest of the drag is that.
                if (Float is { } lifted) floatDrag = ((lifted.X, lifted.Y), px);
            }
            else { bandAnchor = px; Selection = (px.X, px.Y, 1, 1); }
            e.Pointer.Capture(this);
            return;
        }

        if (Ranging)
        {
            shapeAnchor = px;
            ShapePreview = (px.X, px.Y, px.X, px.Y);
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        stroke.Begin(px);
        e.Pointer.Capture(this);
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
        Cursor = floatDrag is not null ? ClosedHand
               : Selecting && at is { } h2 && (Lasso.Contains(Float, h2) || Lasso.Contains(Selection, h2)) ? OpenHand
               : null;
        // Selection drags clamp to the sheet instead of dying past its edge, so a fast drag to
        // a border lands ON the border.
        if (floatDrag is { } fd && Float is { } f && ClampedPixelAt(e.GetPosition(this)) is { } fp)
        {
            Float = Lasso.Moved((fd.Home.X, fd.Home.Y, f.W, f.H), fd.Grab, fp, sheetW, sheetH);
            InvalidateVisual();
            return;
        }
        if (bandAnchor is { } a && ClampedPixelAt(e.GetPosition(this)) is { } cp)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)
                || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
                cp = Squared(a, cp);
            Selection = Lasso.Span(a, cp);
            return;
        }
        // The shape drag clamps to the sheet like a selection drag, so a fast drag past an
        // edge lands ON the edge rather than stopping short.
        if (shapeAnchor is { } ra && ClampedPixelAt(e.GetPosition(this)) is { } rp)
        {
            ShapePreview = (ra.X, ra.Y, rp.X, rp.Y);
            InvalidateVisual();
            return;
        }
        if (stroke.Active && at is { } px) stroke.MoveTo(px);
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
        if (bandAnchor is not null)
        {
            bandAnchor = null;
            e.Pointer.Capture(null);
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
        if (!stroke.End()) return;
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

    /// <summary>The dragged corner pulled onto the square the anchor and it span — the shorter
    /// axis wins, so the square is always inside the drag, and it shrinks again if the sheet
    /// edge is nearer than that.</summary>
    private (int X, int Y) Squared((int X, int Y) a, (int X, int Y) cp)
    {
        int dx = cp.X - a.X, dy = cp.Y - a.Y;
        int n = Math.Min(Math.Abs(dx), Math.Abs(dy));
        n = Math.Min(n, dx >= 0 ? sheetW - 1 - a.X : a.X);
        n = Math.Min(n, dy >= 0 ? sheetH - 1 - a.Y : a.Y);
        return (a.X + (dx >= 0 ? n : -n), a.Y + (dy >= 0 ? n : -n));
    }

    private static void Marquee(DrawingContext ctx, (int X, int Y, int W, int H) s, double z)
        => Overlay.Marquee(ctx, new Rect(s.X * z, s.Y * z, s.W * z, s.H * z));

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

        // The shape being dragged, drawn as the pixels it would land, in the colour it would
        // land them: sizing a circle by its bounding box means guessing at the rasterization.
        // Nothing is written to the file until the drag ends — this is paint on the glass.
        if (ShapePreview is { } rp2)
        {
            if (ShapeInk?.Invoke(rp2) is { } ink)
            {
                var brush = new SolidColorBrush(UiColors.FromRgba(ink.Rgba));
                foreach (var (x, y) in ink.Px)
                    ctx.FillRectangle(brush, new Rect(x * z, y * z, z, z));
            }
            else Marquee(ctx, (Math.Min(rp2.X0, rp2.X1), Math.Min(rp2.Y0, rp2.Y1),
                               Math.Abs(rp2.X1 - rp2.X0) + 1, Math.Abs(rp2.Y1 - rp2.Y0) + 1), z);
        }

        // The floating paste rides above the sheet, marquee'd like a selection.
        if (Float is { } fl && floatBmp is not null)
        {
            floatBlit.Draw(this, ctx, floatBmp, new Rect(0, 0, fl.W, fl.H),
                           new Rect(fl.X * z, fl.Y * z, fl.W * z, fl.H * z),
                           VisualRoot?.RenderScaling ?? 1);
            Marquee(ctx, fl, z);
        }

        // No reticle under the pointer tool: it paints nothing, so a crosshair on the pixel it
        // would land only competes with the marquee it is actually there to drag.
        if (Selecting || Hover is not { } h) return;
        Overlay.Outline(ctx, new Rect(h.X * z, h.Y * z, z, z));
        Overlay.Band(ctx, new Rect((h.X & ~7) * z, (h.Y & ~7) * z, 8 * z, 8 * z));
    }
}
