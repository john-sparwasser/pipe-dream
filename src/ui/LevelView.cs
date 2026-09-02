using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace PipeDream.Ui;

/// <summary>
/// The level canvas. Controls are a deliberate match for the ImGui editor's ObjectTool, so
/// muscle memory carries over:
///
///   RIGHT click/drag   place: the armed catalog object, else the drawer's Map16 tile as a
///                      Direct Map16 object. A selection does NOT change what it does —
///                      pick a tile, right-click, it lands there.
///   CTRL + RIGHT click duplicate the selection at the cursor
///   LEFT click         on a selected object → drag to move it, live under the cursor
///                      elsewhere            → rubber-band select (live, while dragging)
///   LEFT click, still  cycle the overlap stack under the cursor (LM-style: topmost, then
///                      the one beneath, wrapping)
///   CTRL + LEFT drag   grab the covered tiles as the stamp brush instead of selecting
///   ALT + LEFT click   eyedropper: select the CGRAM colour under the pixel
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

    /// <summary>Scroll offset in level pixels (not screen pixels). Zero in the app: the control
    /// measures to the WHOLE level and the hosting ScrollViewer does the scrolling, so
    /// control-local coordinates are already level coordinates. ponytail: kept because it makes
    /// hit-testing testable without a ScrollViewer — set it and you are simulating a scroll,
    /// nothing more.</summary>
    public Point Origin { get; set; }

    public LevelBitmap? Source { get; set; }
    public LevelEdit? Edit { get; set; }

    /// <summary>Sprite editing, active in <see cref="EditMode.Sprites"/>. Esc toggles between
    /// the two modes, exactly as in the ImGui editor.</summary>
    public SpriteEdit? Sprites { get; set; }

    public enum EditMode { Objects, Sprites, Exits, Entrances }
    public EditMode Mode { get; set; } = EditMode.Objects;

    /// <summary>Screen exits to draw in <see cref="EditMode.Exits"/>: which screen leads where.
    /// The view only paints and hit-tests them — the table itself lives in the level stream, and
    /// the host writes it.</summary>
    public IReadOnlyList<(int Screen, int Dest, bool LmForm)> Exits { get; set; } = [];

    /// <summary>A screen was clicked in exits mode. The argument is the screen number, whether
    /// or not it already has an exit.</summary>
    public event EventHandler<int>? ExitScreenClicked;

    /// <summary>The destination BADGE was clicked, rather than the screen behind it — go to
    /// where this exit leads instead of editing it. The argument is the screen number; what it
    /// leads to is the host's to resolve, since a secondary exit's destination is an entrance
    /// index and only the session can read that record.</summary>
    public event EventHandler<int>? ExitBadgeClicked;

    /// <summary>Where each badge was last drawn, for hit-testing. Rebuilt every render — the
    /// badges move with the scroll and the zoom, so anything cached across frames would send
    /// clicks to the level number that used to be under the cursor.</summary>
    private readonly List<(Rect Box, int Screen)> badges = [];

    /// <summary>Where the badges were last drawn. Exposed so a test can aim at one rather than
    /// re-deriving the layout arithmetic and testing its own copy of it.</summary>
    internal IReadOnlyList<(Rect Box, int Screen)> Badges => badges;

    /// <summary>The entrances to draw in <see cref="EditMode.Entrances"/> — where this level
    /// puts Mario. Positions are level pixels; the host resolves them from the records.</summary>
    public IReadOnlyList<LevelEntrance> Entrances { get; set; } = [];

    /// <summary>An entrance was dragged to a level-pixel position. It will not land exactly
    /// there — the ROM stores a screen and two indices — so the host snaps it and hands back a
    /// refreshed list.</summary>
    public event EventHandler<(EntranceKind Kind, int Index, int X, int Y)>? EntranceMoved;

    /// <summary>Which entrance is under the cursor, and the grab offset while dragging one.</summary>
    private (LevelEntrance E, Point Grab)? dragEntrance;

    /// <summary>The marker the cursor is over (or whose edit badge it is over). Hovering is what
    /// makes the badge appear: the settings behind an entrance are a dialog's worth, and a
    /// button on every marker all the time would bury the level under chrome.</summary>
    private LevelEntrance? hoverEntrance;
    private readonly List<(Rect Box, LevelEntrance E)> editBadges = [];
    private readonly List<(Rect Box, LevelEntrance E)> labelBoxes = [];      // the label pill keeps the hover alive on the way to the badge
    internal IReadOnlyList<(Rect Box, LevelEntrance E)> EditBadges => editBadges;

    /// <summary>The hovered marker's edit badge was clicked, or the marker double-clicked: open
    /// that entrance's settings.</summary>
    public event EventHandler<LevelEntrance>? EntranceEditRequested;

    private LevelEntrance? EditBadgeAt(Point p)
    {
        foreach (var (box, e) in editBadges) if (box.Contains(p)) return e;
        return null;
    }

    private LevelEntrance? LabelAt(Point p)
    {
        foreach (var (box, e) in labelBoxes) if (box.Contains(p)) return e;
        return null;
    }

    /// <summary>Big Mario standing, out of the ROM's own player graphics (16x32 RGBA, see
    /// <see cref="PlayerGfx"/>); null falls back to the drawn cap.</summary>
    public IImage? MarioIcon { get; set; }

    /// <summary>The entrance position IS Mario's top-left as the game draws him big: a 16x32 box
    /// in level pixels, so the marker is the cell he stands in and the one above it.</summary>
    private Rect MarkerRect(LevelEntrance e, double z)
        => new(e.X * z - Origin.X, e.Y * z - Origin.Y, 16 * z, 32 * z);

    private LevelEntrance? EntranceAt(Point p, double z)
    {
        for (int i = Entrances.Count - 1; i >= 0; i--)          // topmost first
            if (MarkerRect(Entrances[i], z).Contains(p)) return Entrances[i];
        return null;
    }

    private int? BadgeAt(Point p)
    {
        foreach (var (box, screen) in badges) if (box.Contains(p)) return screen;
        return null;
    }

    /// <summary>Which screen a cell belongs to. Vertical levels stack their screens down the
    /// same 16-cell-wide column, so the axis swaps with the level's mode.</summary>
    public int ScreenOf((int X, int Y) cell) => (Vertical ? cell.Y : cell.X) / 16;

    /// <summary>Sprite number armed from the catalog, or -1. Right-click places it.</summary>
    public int CatalogSprite { get; set; } = -1;

    /// <summary>Object number armed from the Objects catalog, or -1. Right-click places it
    /// INSTEAD of stamping the tile brush; arming one is explicit, so it wins.</summary>
    public int CatalogObject { get; set; } = -1;

    public event EventHandler? SpritesChanged;

    /// <summary>Selected sprites moved by (Dx, Dy) cells during a live drag — the cheap
    /// overlay-shift refresh, not the full sprite-list rebuild SpritesChanged asks for.</summary>
    public event EventHandler<(int Dx, int Dy)>? SpritesMoved;
    public int Phase { get; set; }
    public bool ShowGrid { get; set; } = true;
    public bool Vertical { get; set; }

    public (int X, int Y)? HoverCell { get; private set; }
    public (int X, int Y)? LastClickedCell { get; private set; }

    /// <summary>Raised for every cell a RIGHT drag passes through — the paint stroke.</summary>
    public event EventHandler<(int X, int Y)>? CellPainted;
    public event EventHandler? StrokeEnded;
    public event EventHandler<(int X, int Y)>? CellPressed;

    /// <summary>Ctrl+right-click with a selection: duplicate it here.</summary>
    public event EventHandler<(int X, int Y)>? DuplicateRequested;

    /// <summary>Right-click with a catalog object armed: place it here.</summary>
    public event EventHandler<(int X, int Y)>? PlaceRequested;

    /// <summary>Alt+left-click: sample the CGRAM colour under this LEVEL PIXEL (not cell — a
    /// 16x16 cell holds up to 16 colours, so the pixel is the whole question).</summary>
    public event EventHandler<(int X, int Y)>? SampleRequested;

    /// <summary>Ctrl+drag finished: take these cells as the stamp brush.</summary>
    public event EventHandler<(int X, int Y, int W, int H)>? GrabRequested;

    public event EventHandler? SelectionChanged;
    public event EventHandler? DeleteRequested;

    /// <summary>Wheel scrolling is handled here (horizontal by default) and applied by the
    /// host, which owns the scroll viewer.</summary>
    public event EventHandler<(double Dx, double Dy)>? ScrollRequested;

    static LevelView()
    {
        AffectsRender<LevelView>(ZoomProperty);
        AffectsMeasure<LevelView>(ZoomProperty);
    }

    /// <summary>Centred in whatever hosts it: on an axis where the level is SMALLER than the
    /// viewport it floats in the middle instead of hugging the top-left — a horizontal level
    /// centres vertically, a vertical one horizontally, once the zoom is far enough out for it
    /// to fit. On an axis that overflows this is a no-op: Arrange clamps the size to what is
    /// available, so the scrolling axis still starts at its origin.</summary>
    public LevelView()
    {
        Focusable = true;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
    }

    /// <summary>
    /// The whole level at the current zoom. Without this the control reports no desired size,
    /// the hosting ScrollViewer arranges it at exactly the viewport, extent == viewport, and
    /// the level simply cannot be scrolled — everything past the first screenful is
    /// unreachable, which is what the Avalonia port shipped with.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
        => Source is { HasImages: true } s ? new Size(s.PxW * Zoom, s.PxH * Zoom) : default;

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
    private bool painting, grabbing, sampling;
    /// <summary>A move drag has already applied at least one step, so the rest coalesce into
    /// the same undo entry and a release is a drag, not a stationary click.</summary>
    private bool moved;
    private (int X, int Y)? lastPainted;
    private (int Obj, (int DX, int DY) Edge, int Cx, int Cy)? resizeDrag;
    // The lasso works in LEVEL PIXELS, not cells — it follows the cursor exactly instead of
    // snapping to the 16px grid. (Sprites are also SELECTED by pixel: a sprite is picked by
    // what it draws, and its drawn area rarely lines up with its spawn cell.)
    private (int X, int Y)? pixelStart, pixelEnd;

    private (int X, int Y) LevelPixel(Point p)
        => ((int)((p.X + Origin.X) / Zoom), (int)((p.Y + Origin.Y) / Zoom));

    /// <summary>The ImGui tool's 6px grip tolerance.</summary>
    private const double GripPx = 6;

    /// <summary>The grip under a screen point on the single selected object, on the axes it can
    /// resize along; (0, 0) when there is none.</summary>
    private (int DX, int DY) HandleEdgeAt(Point m)
    {
        if (Edit is not { Selection.Count: 1 } ed) return (0, 0);
        int sel = ed.Selection.First();
        if (ed.BBox(sel) is not { } b || sel >= ed.Objects.Count) return (0, 0);
        var (wOk, hOk) = ed.CanResize(sel);
        return Grips.EdgeAt(m, CellRect(b.X, b.Y, b.W, b.H, Zoom), GripPx, wOk, hOk);
    }

    /// <summary>The sprite lasso rectangle, in level pixels.</summary>
    public (int X, int Y, int W, int H)? PixelBand =>
        pixelStart is { } a && pixelEnd is { } b
            ? (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y))
            : null;

    public (int X, int Y, int W, int H)? Band =>
        bandStart is { } a && bandEnd is { } b ? Lasso.Span(a, b) : null;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (CellAt(e.GetPosition(this)) is not { } cell) return;
        var props = e.GetCurrentPoint(this).Properties;
        LastClickedCell = cell;
        CellPressed?.Invoke(this, cell);

        // Exits mode owns the canvas: a click picks a SCREEN, and nothing else in here runs.
        // The badge is the exception — it is a link to where the exit goes, so it gets the
        // click before the screen under it does.
        if (Mode == EditMode.Exits)
        {
            if (props.IsLeftButtonPressed)
            {
                if (BadgeAt(e.GetPosition(this)) is { } dest) ExitBadgeClicked?.Invoke(this, dest);
                else ExitScreenClicked?.Invoke(this, ScreenOf(cell));
            }
            e.Handled = true;
            return;
        }
        // Entrances owns the canvas too: a press on a marker picks it up, and a press anywhere
        // else is swallowed rather than reaching the layer underneath.
        if (Mode == EditMode.Entrances)
        {
            var p = e.GetPosition(this);
            if (props.IsLeftButtonPressed && EditBadgeAt(p) is { } badge)
                EntranceEditRequested?.Invoke(this, badge);
            else if (props.IsLeftButtonPressed && EntranceAt(p, Zoom) is { } hit)
            {
                if (e.ClickCount == 2) EntranceEditRequested?.Invoke(this, hit);
                else
                {
                    dragEntrance = (hit, p - new Point(hit.X * Zoom - Origin.X, hit.Y * Zoom - Origin.Y));
                    e.Pointer.Capture(this);
                }
            }
            e.Handled = true;
            return;
        }

        // Alt+left is the eyedropper, in every mode. A modifier rather than an armed tool: a
        // mode you can forget you are in costs more than a key you have to hold.
        if (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            sampling = true;
            e.Pointer.Capture(this);
            SampleRequested?.Invoke(this, LevelPixel(e.GetPosition(this)));
            e.Handled = true;
            return;
        }

        if (Mode == EditMode.Sprites && Sprites is { } sp)
        {
            var lp = LevelPixel(e.GetPosition(this));
            if (props.IsRightButtonPressed)
            {
                // Same rule as objects: right-click places, Ctrl+right duplicates.
                bool did = e.KeyModifiers.HasFlag(KeyModifiers.Control) && sp.Selection.Count > 0
                         ? sp.DuplicateSelected(cell.X, cell.Y)
                         : CatalogSprite >= 0 && sp.Place(CatalogSprite, cell.X, cell.Y);
                if (did) SpritesChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (props.IsLeftButtonPressed)
            {
                int hit = sp.SpriteAt(lp.X, lp.Y);
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    // Ctrl+left toggles one sprite in or out, so a selection can be picked
                    // rather than lassoed. No band and no drag: Ctrl is the toggle here, not
                    // the grab it is over objects.
                    if (hit >= 0 && !sp.Selection.Remove(hit)) sp.Selection.Add(hit);
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                }
                // Pressing on what a selected sprite DRAWS drags it, like a selected object.
                // The test is by pixel because that is how sprites are selected and drawn.
                else if (sp.SelectionCovers(lp.X, lp.Y)) { moveStart = cell; bandEnd = cell; e.Pointer.Capture(this); }
                else
                {
                    // A press picks the sprite under that pixel and nothing when there is
                    // nothing there, so clicking empty space clears the selection. The band
                    // overwrites this the moment you actually drag.
                    sp.Selection.Clear();
                    if (hit >= 0) sp.Selection.Add(hit);
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                    pixelStart = pixelEnd = lp;
                    bandEnd = cell;
                    e.Pointer.Capture(this);
                }
            }
            InvalidateVisual();
            return;
        }

        if (props.IsRightButtonPressed)
        {
            // Right-click is PLACE, whatever is selected: pick a tile in the drawer, right-click,
            // it lands. Duplicating a selection moved to Ctrl+right — having a stray selection
            // silently turn every right-click into a duplicate is what made placing look broken.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && Edit is { Selection.Count: > 0 })
                DuplicateRequested?.Invoke(this, cell);
            else if (CatalogObject >= 0) PlaceRequested?.Invoke(this, cell);
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
            var edge = grabbing ? (0, 0) : HandleEdgeAt(e.GetPosition(this));
            if (edge != (0, 0) && Edit is { Selection.Count: 1 } ed)
                resizeDrag = (ed.Selection.First(), edge, cell.X, cell.Y);
            // Grabbing always bands, even over a selected object — Ctrl+drag is "take these
            // tiles", not "move this".
            else if (!grabbing && Edit?.ObjectAt(cell.X, cell.Y) is int hit && Edit.Selection.Contains(hit))
                moveStart = cell;
            else
            {
                bandStart = cell;
                var lp = LevelPixel(e.GetPosition(this));
                pixelStart = pixelEnd = lp;
            }
            bandEnd = cell;
            e.Pointer.Capture(this);
        }
        InvalidateVisual();
    }

    /// <summary>The hover ends with the pointer: a highlight left painted where the cursor no longer
    /// is claims to be tracking something, and the gutter readout would go stale with it.</summary>
    protected override void OnPointerExited(PointerEventArgs e)
    { base.OnPointerExited(e); HoverCell = null; hoverEntrance = null; InvalidateVisual(); }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        // A badge is a link, so it says so under the cursor — otherwise nothing distinguishes it
        // from the rest of the screen, which opens the prompt instead.
        if (Mode == EditMode.Exits)
            Cursor = BadgeAt(e.GetPosition(this)) is null ? Cursor.Default : UiCursors.Hand;

        if (Mode == EditMode.Entrances)
        {
            var p = e.GetPosition(this);
            var badge = EditBadgeAt(p);
            var over = EntranceAt(p, Zoom);
            Cursor = badge is not null ? UiCursors.Hand
                   : dragEntrance is not null || over is not null ? UiCursors.Move
                   : Cursor.Default;
            var hov = over ?? badge ?? LabelAt(p);
            if (hov != hoverEntrance) { hoverEntrance = hov; InvalidateVisual(); }
            // The drag PREVIEWS by moving the marker: the drop snaps to what the ROM can store,
            // and seeing that happen is how the 8x16 grid explains itself.
            if (dragEntrance is { } d)
            {
                var at = p - d.Grab;
                static int Snap(double v) => (int)Math.Round(v / 16) * 16;    // the 16px cell Mario lands in
                dragEntrance = (d.E with { X = Snap((at.X + Origin.X) / Zoom), Y = Snap((at.Y + Origin.Y) / Zoom) },
                                d.Grab);
                InvalidateVisual();
            }
            return;
        }

        var cell = CellAt(e.GetPosition(this));
        if (cell != HoverCell) { HoverCell = cell; InvalidateVisual(); }

        // Dragging the eyedropper reads continuously, so you can sweep it across the artwork and
        // watch the readout rather than clicking once per guess.
        if (sampling) { SampleRequested?.Invoke(this, LevelPixel(e.GetPosition(this))); return; }
        if (cell is not { } c) return;

        if (painting)
        {
            // Every cell the drag crosses stamps, not just the ones a move event lands on —
            // at speed the pointer skips cells and a stroke with holes in it is a bug.
            if (lastPainted is { } prev) foreach (var s in Lasso.Between(prev, c)) CellPainted?.Invoke(this, s);
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
            else if (moveStart is not null)
            {
                // Live: the sprites go where the cursor goes, one step per cell crossed, all
                // coalesced into a single undo entry.
                var prev = bandEnd ?? moveStart!.Value;
                if (c != prev && sp.MoveSelected(c.X - prev.X, c.Y - prev.Y, moved))
                {
                    moved = true;
                    SpritesMoved?.Invoke(this, (c.X - prev.X, c.Y - prev.Y));
                }
                bandEnd = c;
                InvalidateVisual();
            }
            else
            {
                // Same affordance objects get: the hand says this one is draggable.
                var hp = LevelPixel(e.GetPosition(this));
                Cursor = sp.SelectionCovers(hp.X, hp.Y) ? UiCursors.Hand : Cursor.Default;
            }
            return;
        }
        // Hovering an edge of a lone selection shows the resize cursor, as the ImGui tool does.
        if (resizeDrag is null && bandStart is null && moveStart is null)
            Cursor = Grips.CursorFor(HandleEdgeAt(e.GetPosition(this)))
                  ?? (Edit?.ObjectAt(c.X, c.Y) is int ov && Edit.Selection.Contains(ov)
                        ? UiCursors.Hand : Cursor.Default);

        if (resizeDrag is not null || bandStart is not null || moveStart is not null)
        {
            if (moveStart is not null)
            {
                // Live: the selection goes where the cursor goes, one step per cell crossed,
                // all coalesced into a single undo entry.
                var prev = bandEnd ?? moveStart.Value;
                if (c != prev && Edit?.MoveSelected(c.X - prev.X, c.Y - prev.Y, moved) == true)
                {
                    moved = true;
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            bandEnd = c;
            if (bandStart is not null) pixelEnd = LevelPixel(e.GetPosition(this));
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
        if (dragEntrance is { } drop)
        {
            dragEntrance = null;
            e.Pointer.Capture(null);
            EntranceMoved?.Invoke(this, (drop.E.Kind, drop.E.Index, drop.E.X, drop.E.Y));
            InvalidateVisual();
            return;
        }
        if (sampling)
        {
            sampling = false;
            e.Pointer.Capture(null);
            return;
        }
        if (painting)
        {
            painting = false;
            lastPainted = null;
            e.Pointer.Capture(null);
            StrokeEnded?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (Mode == EditMode.Sprites && Sprites is not null)
        {
            // The move already happened, live, on the way here.
            pixelStart = pixelEnd = null;
            moveStart = bandEnd = null;
            moved = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
            return;
        }

        if (resizeDrag is { } rd && bandEnd is { } rc)
        {
            if (Edit?.Resize(rd.Obj, Grips.Mask(rd.Edge), rc.X - rd.Cx, rc.Y - rd.Cy) == true)
                SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (bandStart is { } a && bandEnd is { } b)
        {
            if (a == b) { Edit?.CycleSelectionAt(a.X, a.Y); SelectionChanged?.Invoke(this, EventArgs.Empty); }
            else if (grabbing && Band is { } g) GrabRequested?.Invoke(this, g);
        }
        else if (moveStart is { } m && !moved)
        {
            // Never dragged anywhere: it was a stationary click, so cycle the stack instead.
            Edit?.CycleSelectionAt(m.X, m.Y);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        bandStart = bandEnd = moveStart = null;
        pixelStart = pixelEnd = null;
        resizeDrag = null;
        grabbing = moved = false;
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

    /// <summary>Pixel-art drawing, the same rule every other pixel surface uses. Also the source of
    /// the diagnostics below, which are what a test can pin when it cannot time a frame.</summary>
    private readonly PixelBlit blit = new();
    private readonly PixelBlit iconBlit = new();   // its own intermediate: the level's is sized to the level

    internal PixelSize ScalerSize => blit.MidSize;
    internal int ScalerBuilds => blit.Builds;
    internal int ScalerTarget => blit.FinIndex;
    internal string LastDraw => blit.LastDraw;


    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        // Unused space — right of and below the level, or all of it before a ROM opens — is
        // the diamond desk the ImGui editor drew; the level image covers its own area next.
        ctx.FillRectangle(UiColors.DeskPattern, bounds);
        if (Source?.For(Phase) is not { } bmp) return;

        // One scaled blit of whatever the control covers; the ScrollViewer clips it to the
        // viewport. The Origin/Min dance only does anything when Origin is set by hand.
        double z = Zoom;
        var src = new Rect(Origin.X / z, Origin.Y / z, Math.Min(bounds.Width / z, bmp.PixelSize.Width),
                           Math.Min(bounds.Height / z, bmp.PixelSize.Height));
        var dst = new Rect(0, 0, src.Width * z, src.Height * z);
        blit.Draw(this, ctx, bmp, src, dst, VisualRoot?.RenderScaling ?? 1);

        if (ShowGrid) DrawScreenBoundaries(ctx, dst, z);

        // Exits mode owns the overlay outright: no selection, no handles, no band — the whole
        // point is that the level is being read screen by screen, not edited object by object.
        if (Mode == EditMode.Exits) { DrawExits(ctx, z); return; }
        if (Mode == EditMode.Entrances) { DrawEntrances(ctx, z); return; }

        if (Mode == EditMode.Sprites && Sprites is { } spv)
        {
            // Sprites highlight over their whole PIXEL display, not their spawn cell — the
            // cell is often nowhere near what you can see.
            foreach (int i in spv.Selection)
            {
                if (i >= spv.Sprites.Sprites.Count) continue;
                var (x0, y0, x1, y1) = spv.PixelRect(i);
                ctx.DrawRectangle(UiColors.SpriteFill, SpritePen, PixelRect(x0, y0, x1 - x0, y1 - y0, z));
            }
            if (PixelBand is { } pb) Overlay.Band(ctx, PixelRect(pb.X, pb.Y, pb.W, pb.H, z));
        }
        // Selection: the object's real footprint, from the tracked render, in the same filled
        // ring the Map16 and background canvases use — "these tiles are selected" reads the
        // same in every editor.
        else if (Edit is { } ed)
            foreach (int i in ed.Selection)
                if (ed.BBox(i) is { } b) Overlay.Selection(ctx, CellRect(b.X, b.Y, b.W, b.H, z));

        // Resize preview while dragging an edge, then handles on a lone idle selection.
        if (resizeDrag is { } rd && bandEnd is { } rc && Edit is { } re
            && re.PreviewResizeBox(rd.Obj, Grips.Mask(rd.Edge), rc.X - rd.Cx, rc.Y - rd.Cy) is { } pv)
            Overlay.Outline(ctx, CellRect(pv.X, pv.Y, pv.W, pv.H, z));
        else if (Edit is { Selection.Count: 1 } he && bandStart is null && moveStart is null)
            DrawHandles(ctx, he, z);

        // Rubber band: cyan while selecting, green while grabbing tiles — the ImGui colours.
        // The lasso follows the cursor exactly; only a GRAB snaps, because it takes whole cells.
        if (grabbing)
        {
            if (Band is { } band && bandStart is not null)
                Overlay.Grab(ctx, CellRect(band.X, band.Y, band.W, band.H, z));
        }
        else if (PixelBand is { } lasso && bandStart is not null)
            Overlay.Band(ctx, PixelRect(lasso.X, lasso.Y, lasso.W, lasso.H, z));
    }

    private static readonly Pen SpritePen = new(UiColors.Sprite, 2);

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
        var (wOk, hOk) = ed.CanResize(sel);
        if (wOk || hOk) Grips.Draw(ctx, CellRect(b.X, b.Y, b.W, b.H, z), GripPx, wOk, hOk);
    }

    /// <summary>The screen's own rectangle, in cells: a full-height column of the level (a
    /// full-width band, in a vertical level).</summary>
    private Rect ScreenRect(int screen, double z)
    {
        int cols = (Source?.PxW ?? 0) / 16, rows = (Source?.PxH ?? 0) / 16;
        return Vertical
            ? CellRect(0, screen * 16, cols, Math.Min(16, rows - screen * 16), z)
            : CellRect(screen * 16, 0, Math.Min(16, cols - screen * 16), rows, z);
    }

    /// <summary>Exits mode's overlay: every screen that HAS an exit is bordered in blue and
    /// badged with its destination, and the screen under the cursor is ringed on top — so the
    /// level's connections are readable at a glance instead of through a table of numbers.
    /// The hover ring is a different hue on purpose: "this one leads somewhere" and "this is
    /// the one you are about to click" are different statements and overlap constantly.</summary>
    private void DrawExits(DrawingContext ctx, double z)
    {
        if (Source is not { HasImages: true }) return;

        // Exits first, hover second: on a screen that is both, the ring wins.
        var border = new Pen(UiColors.Accent, 3);
        foreach (var (screen, _, _) in Exits)
        {
            var r = ScreenRect(screen, z);
            if (r.Width > 0) ctx.DrawRectangle(null, border, r);
        }

        if (HoverCell is { } hc) Overlay.Selection(ctx, ScreenRect(ScreenOf(hc), z));

        badges.Clear();
        const double size = 13, padX = 6, padY = 4;
        foreach (var (screen, dest, lm) in Exits)
        {
            var r = ScreenRect(screen, z);
            if (r.Width <= 0) continue;
            // Two digits for a byte, three once the destination carries its ninth bit — the
            // badge should read as the level number the user typed.
            var text = Overlay.Text(lm ? $"{dest:X4}" : dest > 0xFF ? $"{dest:X3}" : $"{dest:X2}", size);
            // The box is sized to the DIGITS, not to the line box.
            var box = new Rect(r.Right - text.Width - padX * 2 - 8, r.Top + 6,
                               text.Width + padX * 2, size + padY * 2);
            // Anchored to the screen's top-right corner, not the viewport's: the badge names
            // THIS screen, and one that floated would name whichever is scrolled into view.
            ctx.DrawRectangle(UiColors.Accent, BadgeEdge, box, 3, 3);
            Overlay.DrawText(ctx, text, size, box.X + padX, box.Center.Y);
            badges.Add((box, screen));
        }
    }

    /// <summary>
    /// Entrances mode: a marker per place this level puts Mario, standing ON the spot with its
    /// kind written beside it. The one being dragged is drawn at the cursor rather than at its
    /// stored position, snapped to the 16px cell as it moves — cells are what the ROM stores.
    /// </summary>
    private void DrawEntrances(DrawingContext ctx, double z)
    {
        if (Source is not { HasImages: true }) return;

        editBadges.Clear();
        labelBoxes.Clear();
        foreach (var e in Entrances)
        {
            // The dragged one is drawn from the drag's own copy, so it follows the cursor.
            var shown = dragEntrance is { } d && d.E.Kind == e.Kind && d.E.Index == e.Index ? d.E : e;
            var r = MarkerRect(shown, z);

            // Mario himself, standing where the game will put him, drawn cell-sharp at the zoom.
            // Without the ROM's graphics (blob failed to decode) a red cap with a white M stands in.
            if (MarioIcon is { } icon)
                iconBlit.Draw(this, ctx, icon, new Rect(0, 0, 16, 32), r, VisualRoot?.RenderScaling ?? 1);
            else
            {
                var cap = new Rect(r.Center.X - 9, r.Y, 18, 18);
                ctx.DrawEllipse(MarioRed, CapEdge, cap.Center, 9, 9);
                Overlay.Label(ctx, "M", 11, cap.Center);
            }
            ctx.DrawRectangle(null, BadgeEdge, r);

            var label = Overlay.Text(shown.Label, 11);
            var box = new Rect(r.Right + 3, r.Y + 1, label.Width + 11, 16);
            ctx.DrawRectangle(UiColors.SelectionFill, LabelEdge, box, 3, 3);
            Overlay.DrawText(ctx, label, 11, box.X + 5, box.Center.Y);
            labelBoxes.Add((box.Inflate(new Thickness(6)), e));   // generous: crossing a 3px gap must not drop the badge

            // Hovered: an edit badge after the label, the way into the entrance's settings.
            if (hoverEntrance is { } h && h.Kind == e.Kind && h.Index == e.Index && dragEntrance is null)
            {
                var pencil = Overlay.Text("edit", 11);
                var bb = new Rect(box.Right + 3, box.Y, pencil.Width + 11, 16);
                ctx.DrawRectangle(UiColors.SelectionFill, LabelEdge, bb, 3, 3);
                Overlay.DrawText(ctx, pencil, 11, bb.Center.X - pencil.Width / 2, bb.Center.Y);
                editBadges.Add((bb.Inflate(new Thickness(4)), e));
            }
        }
    }

    private static readonly Pen BadgeEdge = new(Brushes.White, 1);
    private static readonly Pen CapEdge = new(Brushes.White, 1.5);
    private static readonly Pen LabelEdge = new(UiColors.Selection, 1);

    /// <summary>Mario's cap red. Not from the ROM's palette on purpose: the marker has to read
    /// against whatever artwork is under it, and the level's own colours are the artwork.</summary>
    private static readonly IBrush MarioRed = new SolidColorBrush(Color.Parse("#D63A2F"));

    // SMW screens are 16 cells wide; the boundary lines are the editor's main orientation cue.
    private void DrawScreenBoundaries(DrawingContext ctx, Rect dst, double z)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
        double step = 16 * 16 * z;
        for (double x = -Origin.X % step; x < dst.Width; x += step)
            ctx.DrawLine(pen, new Point(x, 0), new Point(x, dst.Height));
    }
}
