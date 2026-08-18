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

    /// <summary>Sprite editing, active in <see cref="EditMode.Sprites"/>. Esc toggles between
    /// the two modes, exactly as in the ImGui editor.</summary>
    public SpriteEdit? Sprites { get; set; }

    public enum EditMode { Objects, Sprites }
    public EditMode Mode { get; set; } = EditMode.Objects;

    /// <summary>Sprite number armed from the catalog, or -1. Right-click places it.</summary>
    public int CatalogSprite { get; set; } = -1;

    public event EventHandler? SpritesChanged;
    public int Phase { get; set; }
    public bool ShowGrid { get; set; } = true;
    public bool Vertical { get; set; }

    public (int X, int Y)? HoverCell { get; private set; }
    public (int X, int Y)? LastClickedCell { get; private set; }

    /// <summary>Footprint of the stamp brush, outlined under the cursor so a grabbed 4x3
    /// brush is visible before it is committed rather than after.</summary>
    public int BrushW { get; set; } = 1;
    public int BrushH { get; set; } = 1;

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

    // ---- drag state, mirroring the ImGui tool's dragStart/dragEnd/moveDrag/resizeDrag ----
    private (int X, int Y)? bandStart, bandEnd, moveStart;
    private bool painting, grabbing;
    private (int X, int Y)? lastPainted;
    private (int Obj, int Edges, int Cx, int Cy)? resizeDrag;
    // Sprite lasso works in LEVEL PIXELS, not cells: a sprite is selected by what it draws,
    // and its drawn area rarely lines up with its spawn cell.
    private (int X, int Y)? pixelStart, pixelEnd;

    private (int X, int Y) LevelPixel(Point p)
        => ((int)((p.X + Origin.X) / Zoom), (int)((p.Y + Origin.Y) / Zoom));

    /// <summary>Edge bitmask under a screen point for the single selected object: 1 left,
    /// 2 right, 4 top, 8 bottom (corners combine). 0 = not on a handle. Mirrors the ImGui
    /// tool's 6px tolerance and its "nearest edge wins" tie-break.</summary>
    private int HandleEdgesAt(Point m)
    {
        if (Edit is not { Selection.Count: 1 } ed) return 0;
        int sel = ed.Selection.First();
        if (ed.BBox(sel) is not { } b || sel >= ed.Objects.Count) return 0;
        var rz = ed.ResizeInfo(ed.Objects[sel]);
        bool wOk = rz.W != ObjectEngine.SizeSrc.None, hOk = rz.H != ObjectEngine.SizeSrc.None;
        if (!wOk && !hOk) return 0;

        var r = CellRect(b.X, b.Y, b.W, b.H, Zoom);
        const double t = 6;
        bool inX = m.X > r.Left - t && m.X < r.Right + t;
        bool inY = m.Y > r.Top - t && m.Y < r.Bottom + t;
        int e = 0;
        if (wOk && inY && Math.Abs(m.X - r.Left) <= t) e |= 1;
        if (wOk && inY && Math.Abs(m.X - r.Right) <= t) e |= 2;
        if (hOk && inX && Math.Abs(m.Y - r.Top) <= t) e |= 4;
        if (hOk && inX && Math.Abs(m.Y - r.Bottom) <= t) e |= 8;
        if ((e & 3) == 3) e &= Math.Abs(m.X - r.Left) < Math.Abs(m.X - r.Right) ? ~2 : ~1;
        if ((e & 12) == 12) e &= Math.Abs(m.Y - r.Top) < Math.Abs(m.Y - r.Bottom) ? ~8 : ~4;
        return e;
    }

    private static Cursor CursorForEdges(int e) => new(e switch
    {
        1 or 2 => StandardCursorType.SizeWestEast,
        4 or 8 => StandardCursorType.SizeNorthSouth,
        5 or 10 => StandardCursorType.TopLeftCorner,      // TL / BR
        6 or 9 => StandardCursorType.TopRightCorner,      // TR / BL
        _ => StandardCursorType.Arrow,
    });

    /// <summary>The sprite lasso rectangle, in level pixels.</summary>
    public (int X, int Y, int W, int H)? PixelBand =>
        pixelStart is { } a && pixelEnd is { } b
            ? (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y))
            : null;

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

        if (Mode == EditMode.Sprites && Sprites is { } sp)
        {
            var lp = LevelPixel(e.GetPosition(this));
            if (props.IsRightButtonPressed)
            {
                // Same rule as objects: duplicate a selection, else place from the catalog.
                bool did = sp.Selection.Count > 0 ? sp.DuplicateSelected(cell.X, cell.Y)
                         : CatalogSprite >= 0 && sp.Place(CatalogSprite, cell.X, cell.Y);
                if (did) SpritesChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (props.IsLeftButtonPressed)
            {
                if (sp.IndexAtCell(cell.X, cell.Y) is int hit && sp.Selection.Contains(hit))
                    moveStart = cell;
                else { pixelStart = lp; pixelEnd = lp; }
                bandEnd = cell;
                e.Pointer.Capture(this);
            }
            InvalidateVisual();
            return;
        }

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
            int edges = grabbing ? 0 : HandleEdgesAt(e.GetPosition(this));
            if (edges != 0 && Edit is { Selection.Count: 1 } ed)
                resizeDrag = (ed.Selection.First(), edges, cell.X, cell.Y);
            // Grabbing always bands, even over a selected object — Ctrl+drag is "take these
            // tiles", not "move this".
            else if (!grabbing && Edit?.ObjectAt(cell.X, cell.Y) is int hit && Edit.Selection.Contains(hit))
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

        if (Mode == EditMode.Sprites && Sprites is { } sp)
        {
            if (pixelStart is not null)
            {
                pixelEnd = LevelPixel(e.GetPosition(this));
                // Live selection, in pixels: what the band touches is selected as you drag.
                var (rx, ry, rw, rh) = PixelBand!.Value;
                sp.SelectInPixelRect(rx, ry, rw, rh);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
            }
            else if (moveStart is not null) { bandEnd = c; InvalidateVisual(); }
            return;
        }
        // Hovering an edge of a lone selection shows the resize cursor, as the ImGui tool does.
        if (resizeDrag is null && bandStart is null && moveStart is null)
        {
            int edges = HandleEdgesAt(e.GetPosition(this));
            Cursor = edges != 0 ? CursorForEdges(edges)
                   : Edit?.ObjectAt(c.X, c.Y) is int ov && Edit.Selection.Contains(ov)
                       ? new Cursor(StandardCursorType.Hand)
                       : Cursor.Default;
        }

        if (resizeDrag is not null || bandStart is not null || moveStart is not null)
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

        if (Mode == EditMode.Sprites && Sprites is { } sp)
        {
            if (moveStart is { } sm && bandEnd is { } sn && sp.MoveSelected(sn.X - sm.X, sn.Y - sm.Y))
                SpritesChanged?.Invoke(this, EventArgs.Empty);
            pixelStart = pixelEnd = null;
            moveStart = bandEnd = null;
            e.Pointer.Capture(null);
            InvalidateVisual();
            return;
        }

        if (resizeDrag is { } rd && bandEnd is { } rc)
        {
            if (Edit?.Resize(rd.Obj, rd.Edges, rc.X - rd.Cx, rc.Y - rd.Cy) == true)
                SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (bandStart is { } a && bandEnd is { } b)
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
        resizeDrag = null;
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
        if (e.Key != Key.Delete) return;
        if (Mode == EditMode.Sprites && Sprites is { Selection.Count: > 0 } sp)
        {
            if (sp.DeleteSelected()) SpritesChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (Edit is { Selection.Count: > 0 })
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

        if (Mode == EditMode.Sprites && Sprites is { } spv)
        {
            // Sprites highlight over their whole PIXEL display, not their spawn cell — the
            // cell is often nowhere near what you can see.
            var fill = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0xFF, 0x00));
            var pen = new Pen(Brushes.Lime, 2);
            foreach (int i in spv.Selection)
            {
                if (i >= spv.Sprites.Sprites.Count) continue;
                var (x0, y0, x1, y1) = spv.PixelRect(i);
                ctx.DrawRectangle(fill, pen, PixelRect(x0, y0, x1 - x0, y1 - y0, z));
            }
            if (PixelBand is { } pb)
                ctx.DrawRectangle(null, new Pen(Brushes.Cyan, 1.5), PixelRect(pb.X, pb.Y, pb.W, pb.H, z));
        }
        // Selection: the object's real footprint, from the tracked render.
        else if (Edit is { } ed)
        {
            var pen = new Pen(Brushes.DodgerBlue, 1.5);
            foreach (int i in ed.Selection)
                if (ed.BBox(i) is { } b) ctx.DrawRectangle(null, pen, CellRect(b.X, b.Y, b.W, b.H, z));
        }

        // Resize preview while dragging an edge, then handles on a lone idle selection.
        if (resizeDrag is { } rd && bandEnd is { } rc && Edit is { } re
            && re.PreviewResize(rd.Obj, rd.Edges, rc.X - rd.Cx, rc.Y - rd.Cy) is { } pv)
            ctx.DrawRectangle(null, new Pen(Brushes.DodgerBlue, 1.5), CellRect(pv.X, pv.Y, pv.W, pv.H, z));
        else if (Edit is { Selection.Count: 1 } he && bandStart is null && moveStart is null)
            DrawHandles(ctx, he, z);

        // Rubber band: cyan while selecting, green while grabbing tiles — the ImGui colours.
        if (Band is { } band && (bandStart is not null || moveStart is not null))
            ctx.DrawRectangle(null, new Pen(grabbing ? Brushes.Lime : Brushes.Cyan, 1.5),
                              CellRect(band.X, band.Y, band.W, band.H, z));

        // Hover shows the BRUSH footprint, not a single cell — with a multi-tile brush the
        // difference between "this cell" and "these twelve" matters before you commit.
        if (HoverCell is { } h)
            ctx.DrawRectangle(null, new Pen(Brushes.White, 1),
                              CellRect(h.X, h.Y, Math.Max(1, BrushW), Math.Max(1, BrushH), z));
    }

    private Rect CellRect(int x, int y, int w, int h, double z)
        => new(x * 16 * z - Origin.X, y * 16 * z - Origin.Y, w * 16 * z, h * 16 * z);

    private Rect PixelRect(int x, int y, int w, int h, double z)
        => new(x * z - Origin.X, y * z - Origin.Y, w * z, h * z);

    /// <summary>Knobs on the enabled edges' midpoints and on all corners (a corner resizes
    /// whichever axes are enabled), vector-editor style — same layout as the ImGui tool.</summary>
    private void DrawHandles(DrawingContext ctx, LevelEdit ed, double z)
    {
        int sel = ed.Selection.First();
        if (ed.BBox(sel) is not { } b || sel >= ed.Objects.Count) return;
        var rz = ed.ResizeInfo(ed.Objects[sel]);
        bool wOk = rz.W != ObjectEngine.SizeSrc.None, hOk = rz.H != ObjectEngine.SizeSrc.None;
        if (!wOk && !hOk) return;

        var r = CellRect(b.X, b.Y, b.W, b.H, z);
        var fill = Brushes.DodgerBlue;
        var edge = new Pen(Brushes.Black);
        void Knob(double x, double y)
            => ctx.DrawRectangle(fill, edge, new Rect(x - 3, y - 3, 6, 6));

        double mx = (r.Left + r.Right) / 2, my = (r.Top + r.Bottom) / 2;
        if (wOk) { Knob(r.Left, my); Knob(r.Right, my); }
        if (hOk) { Knob(mx, r.Top); Knob(mx, r.Bottom); }
        Knob(r.Left, r.Top); Knob(r.Right, r.Top); Knob(r.Left, r.Bottom); Knob(r.Right, r.Bottom);
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
