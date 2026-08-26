using SelRect = (int X, int Y, int W, int H);

namespace PipeDream.Services;

/// <summary>
/// Editing a GFX file's pixels.
///
/// Edits write THROUGH to the file's byte array immediately, so the sheet redraws as you drag,
/// and a stroke batches into one undo entry on release — the same grammar as Map16 quadrant
/// stamping. Stock ROM files fork on first touch: a copy-on-write copy is stored under the SAME
/// id, so every consumer (levels, sprites, the Map16 sheet) sees the edit and the import
/// plumbing carries persistence and the build for free.
///
/// The pixel and stroke-replay work itself lives in <see cref="Gfx"/>, with the format. This
/// class is the session around it: which file, which colour, which tool, and undo.
/// </summary>
public sealed class GfxEdit
{
    private readonly Rom rom;

    internal GfxEdit(Rom rom) => this.rom = rom;

    /// <summary>Eraser is the pencil writing colour 0 — in this format that IS transparent, so
    /// "erase" needs no separate concept. Dropper writes nothing at all; it reads. Select paints
    /// nothing either: it marks a rectangle for copy/cut/paste and moving.</summary>
    public enum Tool { Pencil, Fill, Eraser, Dropper, Select, Rect, Ellipse, Line }

    /// <summary>Whether <see cref="Tool.Rect"/> fills its rectangle or draws the outline only.
    /// A variant of ONE tool rather than two tools: it is one button on the bar with a
    /// dropdown, and F should step past the pair once, not twice.</summary>
    public bool RectFilled { get; set; }

    /// <summary>The same, for <see cref="Tool.Ellipse"/>. Kept SEPARATE from
    /// <see cref="RectFilled"/> so each shape remembers how you last drew it — sharing one flag
    /// would make picking a filled circle silently change what the square tool does.</summary>
    public bool EllipseFilled { get; set; }

    /// <summary>True while a tool draws by dragging out a shape rather than per pixel.</summary>
    public static bool IsShape(Tool t) => t is Tool.Rect or Tool.Ellipse or Tool.Line;

    public int File { get; private set; } = 0x14;
    public int PalRow { get; set; } = 2;

    /// <summary>Paint colour index, clamped to what the ROM's depth can hold. Never 0: index 0
    /// is transparent, and painting transparent is the Eraser — a paint colour that erases would
    /// be a second way to do one thing, and it makes "the pencil did nothing" a real state.</summary>
    public int Color
    {
        get => color;
        set => color = Math.Clamp(value, 1, MaxColor);
    }
    private int color = 1;
    public Tool Current { get; set; } = Tool.Pencil;

    /// <summary>Committed edits that project.pdp does not have yet. Cleared by the session's save,
    /// which is what greys the mode's Save button back out.</summary>
    public bool Dirty { get; internal set; }
    public int UndoDepth => undo.Count;

    /// <summary>Raised when committed bytes changed: every consumer of this file is stale.</summary>
    public event EventHandler? Committed;

    private readonly List<(int Off, byte Before, byte After)> stroke = [];
    private int strokeFile;
    // Sel carries where the selection rectangle sat before/after a cut, paste or move, so undo
    // can walk the marquee back with the pixels. Null on plain paint strokes: leave it alone.
    private readonly Stack<(int File, (int Off, byte Before, byte After)[] Edits,
                            (SelRect? Before, SelRect? After)? Sel)> undo = new();
    private readonly Stack<(int File, (int Off, byte Before, byte After)[] Edits,
                            (SelRect? Before, SelRect? After)? Sel)> redo = new();
    private (SelRect? Before, SelRect? After)? strokeSel;

    /// <summary>Where the selection belongs after the last Undo/Redo. Has only when the entry
    /// was a selection operation on the OPEN file — a paint stroke, or an entry replayed into
    /// some other file, must not move the marquee. Rect null means "no selection".</summary>
    public (bool Has, SelRect? Rect) SelectionHint { get; private set; }

    /// <summary>Switch files. An uncommitted stroke is REVERTED rather than committed — a
    /// write-through stroke must not survive as bytes nobody can undo.</summary>
    public void Open(int file)
    {
        if (file == File) return;
        AbortStroke();
        BppOverride = null;              // a depth argued for one file says nothing about the next
        File = Math.Clamp(file, 0, 0xFFF);
    }

    /// <summary>Whether this file resolves to anything, and how it is backed.</summary>
    public string Status => rom.ImportedGfx.ContainsKey(File) ? "imported"
                          : Gfx.Cached(rom, File) is not null ? "stock" : "missing";

    public string? Name => rom.GfxName(File) is { Length: > 0 } n ? n : null;

    /// <summary>The file's bytes, or null when the id resolves nowhere.</summary>
    public byte[]? Bytes => Gfx.Cached(rom, File);

    /// <summary>The depth THIS file is read at — the ROM's depth for tile data, but 2bpp for
    /// the layer-3 files, which are read by a different routine in the game and would decode as
    /// noise at any other depth. <see cref="MaxColor"/> follows it, so a 2bpp file offers four
    /// colours rather than pretending to eight.</summary>
    public int Bpp => BppOverride ?? Gfx.FileBpp(rom, File);

    /// <summary>Read this file at a depth of your choosing instead of the one it is known (or
    /// assumed) to be. Null = follow the file. Nothing in a raw planar file says how deep it is
    /// — a custom ExGFX bound for a 2bpp slot looks exactly like a short 4bpp one — so the
    /// depth the bar shows has to be arguable with. It is dropped when the file changes: it is
    /// a statement about THIS file, and carrying it to the next one would decode that one
    /// wrong.</summary>
    public int? BppOverride { get; set; }

    /// <summary>Read the open file as one of the two depths the SNES DISPLAYS: 4bpp tile data
    /// (FG/BG/sprites) or 2bpp (layer 3). Three planes is a storage format, not a display one —
    /// the game's upload expands it — so "4bpp" here means the ROM's tile stride, which is 3
    /// bytes a row on an unconverted base and 4 on a converted one. The colour it paints with
    /// comes along, because a shallower read holds fewer colours.</summary>
    public void ViewAs(int displayBpp)
    {
        BppOverride = displayBpp == 2 ? 2 : Gfx.RomBpp(rom);
        Color = color;
    }

    /// <summary>The highest colour index this FILE's depth can hold — 7 on a vanilla 3bpp ROM,
    /// 3 in a 2bpp layer-3 file. Offering more would let you pick a colour that silently paints
    /// its low bits instead, which looks like the editor ignoring the click.</summary>
    public int MaxColor => (1 << Bpp) - 1;

    /// <summary>Tiles in the file, and the sheet's size in GFX pixels at 16 tiles per row.</summary>
    public (int Tiles, int W, int H) Layout
    {
        get
        {
            int tb = Gfx.TileBytes(Bpp);
            int tiles = Bytes is { } b && b.Length >= tb ? b.Length / tb : 0;
            return (tiles, 128, (tiles + 15) / 16 * 8);
        }
    }

    /// <summary>The sheet as RGBA, coloured by the chosen palette row.</summary>
    public (uint[] Px, int W, int H) Sheet(Palette pal)
        => Bytes is { } b && b.Length >= Gfx.TileBytes(Bpp)
            ? Gfx.TileSheet(b, Bpp, pal, PalRow) : ([], 0, 0);

    /// <summary>The colour index at a sheet pixel — right-click eyedrop.</summary>
    public int? ColorAt(int px, int py)
    {
        int tb = Gfx.TileBytes(Bpp);
        if (Bytes is not { } b) return null;
        int off = ((py / 8) * 16 + px / 8) * tb;
        if (off < 0 || off + tb > b.Length) return null;
        return Gfx.DecodeTile(b, off, Bpp)[(py & 7) * 8 + (px & 7)];
    }

    /// <summary>
    /// Paint one sheet pixel with the current tool. Returns true when bytes changed, which is
    /// the caller's cue to redraw the sheet. Forks a stock file on first touch —
    /// <paramref name="forked"/> reports that, because it is worth telling the user their edit
    /// now shadows the stock file everywhere.
    ///
    /// The Dropper writes nothing and is refused here rather than silently painting: reading a
    /// colour also has to move the palette selection, which belongs to whoever draws it.
    /// </summary>
    public bool Paint(int px, int py, out bool forked)
    {
        forked = false;
        if (Current == Tool.Dropper) return false;
        var (tiles, w, h) = Layout;
        if (px < 0 || py < 0 || px >= w || py >= h || (py / 8) * 16 + px / 8 >= tiles) return false;
        if (Gfx.EditableBytes(rom, File, out forked) is not { } g) return false;

        if (stroke.Count == 0) strokeFile = File;
        int before = stroke.Count;
        if (Current == Tool.Fill)
            Gfx.FillTile(g, Bpp, px, py, Color, stroke);
        else
            Gfx.WritePixel(g, ((py / 8) * 16 + px / 8) * Gfx.TileBytes(Bpp), Bpp, px & 7, py & 7,
                           Current == Tool.Eraser ? 0 : Color, stroke);
        return stroke.Count != before;
    }

    /// <summary>
    /// Paint a rectangle between two corners (inclusive, either order) with the current colour,
    /// outline or filled per <see cref="RectFilled"/>. Every pixel goes into the SAME open
    /// stroke, so the whole rectangle is one undo entry — which is the point of a shape tool
    /// over dragging the pencil around four edges.
    ///
    /// Off-sheet corners are clamped rather than refused: a drag that leaves the sheet should
    /// land on the border, the same rule the canvas uses for a selection drag.
    /// </summary>
    public bool PaintRect(int x0, int y0, int x1, int y1, out bool forked)
        => PaintPixels(RectPixels(x0, y0, x1, y1), out forked);

    private IEnumerable<(int X, int Y)> RectPixels(int x0, int y0, int x1, int y1)
    {
        var (tiles, lx, ty, rx, by) = Box(x0, y0, x1, y1);
        for (int py = ty; py <= by; py++)
            for (int px = lx; px <= rx; px++)
            {
                // The outline is the two full rows and the two full columns; everything else in
                // between is skipped unless the rectangle is filled.
                if (!RectFilled && py != ty && py != by && px != lx && px != rx) continue;
                if ((py / 8) * 16 + px / 8 >= tiles) continue;          // past the file's last tile
                yield return (px, py);
            }
    }

    /// <summary>
    /// Paint an ellipse inscribed in the box between two corners, outline or filled per
    /// <see cref="EllipseFilled"/>. One stroke, so one undo entry, exactly like PaintRect.
    ///
    /// Rasterized by testing pixel CENTRES against the ellipse rather than by a Bresenham arc.
    /// It costs a pass over the box — nothing at sheet sizes — and buys two things a stepped
    /// arc makes fiddly: the outline is closed by construction (it is the inside edge of the
    /// filled region, so it cannot leak), and degenerate boxes behave. A 1-wide box is a line
    /// and a 1x1 box is a dot, which is what dragging a tiny circle should give you.
    /// </summary>
    public bool PaintEllipse(int x0, int y0, int x1, int y1, out bool forked)
        => PaintPixels(EllipsePixels(x0, y0, x1, y1), out forked);

    private IEnumerable<(int X, int Y)> EllipsePixels(int x0, int y0, int x1, int y1)
    {
        var (tiles, lx, ty, rx, by) = Box(x0, y0, x1, y1);
        // Semi-axes and centre in pixel-centre coordinates: a pixel is at x + 0.5.
        double ra = (rx - lx + 1) / 2.0, rb = (by - ty + 1) / 2.0;
        double cx = lx + ra, cy = ty + rb;
        bool In(int x, int y)
        {
            if (x < lx || x > rx || y < ty || y > by) return false;
            double dx = (x + 0.5 - cx) / ra, dy = (y + 0.5 - cy) / rb;
            return dx * dx + dy * dy <= 1.0;
        }

        for (int py = ty; py <= by; py++)
            for (int px = lx; px <= rx; px++)
            {
                if (!In(px, py)) continue;
                // The outline is the inside pixels that touch the outside — a closed ring for
                // free, at any size, without a special case for the poles.
                if (!EllipseFilled && In(px - 1, py) && In(px + 1, py) && In(px, py - 1) && In(px, py + 1))
                    continue;
                if ((py / 8) * 16 + px / 8 >= tiles) continue;
                yield return (px, py);
            }
    }

    /// <summary>Draw whichever shape the current tool is, into one stroke. The canvas reports a
    /// bounding box and does not care which shape it becomes; this is where that is decided.</summary>
    public bool PaintShape(int x0, int y0, int x1, int y1, out bool forked)
        => PaintPixels(ShapePixels(x0, y0, x1, y1), out forked);

    /// <summary>
    /// A straight line between the two ends, one pixel thick. Note this is the only shape that
    /// reads its arguments as ENDS rather than as a box: \ and / have the same bounding box, so
    /// normalizing the corners here would lose which way the drag went.
    /// </summary>
    public bool PaintLine(int x0, int y0, int x1, int y1, out bool forked)
        => PaintPixels(LinePixels(x0, y0, x1, y1), out forked);

    private IEnumerable<(int X, int Y)> LinePixels(int x0, int y0, int x1, int y1)
    {
        var (tiles, w, h) = Layout;
        if (tiles == 0) yield break;
        x0 = Math.Clamp(x0, 0, w - 1); x1 = Math.Clamp(x1, 0, w - 1);
        y0 = Math.Clamp(y0, 0, h - 1); y1 = Math.Clamp(y1, 0, h - 1);
        // One step per pixel of the LONGER axis, so the line is connected whatever its slope;
        // rounding rather than truncating keeps it symmetric end to end.
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        for (int i = 0; i <= steps; i++)
        {
            int x = steps == 0 ? x0 : x0 + (int)Math.Round((double)(x1 - x0) * i / steps);
            int y = steps == 0 ? y0 : y0 + (int)Math.Round((double)(y1 - y0) * i / steps);
            if ((y / 8) * 16 + x / 8 >= tiles) continue;
            yield return (x, y);
        }
    }

    /// <summary>The sheet pixels the current shape tool WOULD paint between two corners. The one
    /// definition of the geometry — painting writes exactly these, so a live preview drawn from
    /// them cannot disagree with what lands.</summary>
    public IEnumerable<(int X, int Y)> ShapePixels(int x0, int y0, int x1, int y1)
        => Current switch
        {
            Tool.Rect => RectPixels(x0, y0, x1, y1),
            Tool.Ellipse => EllipsePixels(x0, y0, x1, y1),
            Tool.Line => LinePixels(x0, y0, x1, y1),
            _ => [],
        };

    /// <summary>The drag box clamped to the sheet. Off-sheet corners clamp rather than being
    /// refused: a drag that leaves the sheet should land on the border, the same rule the canvas
    /// uses for a selection drag.</summary>
    private (int Tiles, int L, int T, int R, int B) Box(int x0, int y0, int x1, int y1)
    {
        var (tiles, w, h) = Layout;
        if (tiles == 0) return (0, 0, 0, -1, -1);            // empty box: every loop below skips
        return (tiles,
                Math.Clamp(Math.Min(x0, x1), 0, w - 1), Math.Clamp(Math.Min(y0, y1), 0, h - 1),
                Math.Clamp(Math.Max(x0, x1), 0, w - 1), Math.Clamp(Math.Max(y0, y1), 0, h - 1));
    }

    /// <summary>Write a shape's pixels in the current colour, all into the SAME open stroke, so
    /// the whole shape is one undo entry — which is the point of a shape tool over dragging the
    /// pencil around four edges.</summary>
    private bool PaintPixels(IEnumerable<(int X, int Y)> px, out bool forked)
    {
        forked = false;
        if (Layout.Tiles == 0) return false;
        if (Gfx.EditableBytes(rom, File, out forked) is not { } g) return false;

        if (stroke.Count == 0) strokeFile = File;
        int before = stroke.Count;
        foreach (var (x, y) in px)
            Gfx.WritePixel(g, ((y / 8) * 16 + x / 8) * Gfx.TileBytes(Bpp), Bpp,
                           x & 7, y & 7, Color, stroke);
        return stroke.Count != before;
    }

    // ---- selection: copy / cut / paste / move ----
    // The clipboard is colour INDICES, not plane bytes, so it pastes into any file of this ROM —
    // copy in one bin, paste in another. Everything writes through the same stroke machinery as
    // painting, so each operation is one undo entry.

    /// <summary>Copied pixels as colour indices, row-major. Survives switching files.</summary>
    public (int W, int H, byte[] Px)? Clipboard { get; private set; }

    private byte[] ReadRect(int x, int y, int w, int h)
    {
        var px = new byte[w * h];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                px[j * w + i] = (byte)(ColorAt(x + i, y + j) ?? 0);
        return px;
    }

    /// <summary>Write a w×h block of colour indices (null = clear to transparent) at (x,y) into
    /// the open stroke, clipped to the tiles the file actually has.</summary>
    private void WriteRect(int x, int y, int w, int h, byte[]? px)
    {
        if (Gfx.EditableBytes(rom, File, out _) is not { } g) return;
        if (stroke.Count == 0) strokeFile = File;
        var (tiles, sw, sh) = Layout;
        int tb = Gfx.TileBytes(Bpp);
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int dx = x + i, dy = y + j;
                if (dx < 0 || dy < 0 || dx >= sw || dy >= sh || (dy / 8) * 16 + dx / 8 >= tiles)
                    continue;
                Gfx.WritePixel(g, ((dy / 8) * 16 + dx / 8) * tb, Bpp, dx & 7, dy & 7,
                               px is null ? 0 : px[j * w + i], stroke);
            }
    }

    public void Copy(int x, int y, int w, int h) => Clipboard = (w, h, ReadRect(x, y, w, h));

    /// <summary>Turning a selection in place. Rotation is the pair of quarter turns; the flips
    /// are the mirrors.</summary>
    public enum Xform { RotateLeft, RotateRight, FlipH, FlipV }

    /// <summary>
    /// Turn a block of colour indices. A quarter turn swaps the block's sides, which is why the
    /// size comes back with it.
    ///
    /// PURE — nothing is written here. A rotate or a flip happens on the floating layer, the
    /// same place a move does: the block comes off the sheet first, turns in the air as often as
    /// you like, and only the drop writes. Turning in place would have to clear the footprint it
    /// no longer covers, which is exactly the "it ate what was underneath" bug.
    /// </summary>
    public static (int W, int H, byte[] Px) Turn(int w, int h, byte[] src, Xform t)
    {
        bool turn = t is Xform.RotateLeft or Xform.RotateRight;
        int dw = turn ? h : w, dh = turn ? w : h;
        var dst = new byte[src.Length];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                var (di, dj) = t switch
                {
                    Xform.FlipH => (w - 1 - i, j),
                    Xform.FlipV => (i, h - 1 - j),
                    Xform.RotateRight => (h - 1 - j, i),
                    _ => (j, w - 1 - i),
                };
                dst[dj * dw + di] = src[j * w + i];
            }
        return (dw, dh, dst);
    }

    public void Cut(int x, int y, int w, int h)
    {
        Copy(x, y, w, h);
        WriteRect(x, y, w, h, null);
        strokeSel = ((x, y, w, h), (x, y, w, h));
        EndStroke();
    }

    /// <summary>Stamp a block at (x,y) as one undo entry — the clipboard by default, or
    /// <paramref name="px"/> for pixels that never went through it (a lifted move landing).
    /// Pixels past the sheet edge are clipped away. False when there is nothing to paste (or
    /// nothing to paste onto). <paramref name="selBefore"/> is where the marquee sat before,
    /// so undoing it can put the marquee back too — the paste itself becomes the selection.</summary>
    public bool Paste(int x, int y, SelRect? selBefore = null, (int W, int H, byte[] Px)? px = null)
    {
        if ((px ?? Clipboard) is not { } c || Layout.Tiles == 0) return false;
        WriteRect(x, y, c.W, c.H, c.Px);
        strokeSel = (selBefore, (x, y, c.W, c.H));
        EndStroke();
        return true;
    }

    /// <summary>
    /// Take a block off the sheet to move it: its colour indices come back, and the hole it
    /// leaves goes into an OPEN stroke that the <see cref="Paste"/> which drops it closes. So
    /// the whole move — however far it wandered on the way — is one undo entry, and every pixel
    /// it passed over is untouched: only where it finally LANDS gets written.
    ///
    /// <see cref="AbortStroke"/> puts the block back, which is what Esc on a lifted move does.
    /// </summary>
    public byte[] Lift(int x, int y, int w, int h)
    {
        var px = ReadRect(x, y, w, h);
        WriteRect(x, y, w, h, null);
        return px;
    }

    /// <summary>Close the stroke into one undo entry. The bytes are already in place (the paint
    /// wrote through), so this only records history and announces the change.</summary>
    public void EndStroke()
    {
        var sel = strokeSel;
        strokeSel = null;                    // a no-op commit must not tag the next paint stroke
        if (stroke.Count == 0) return;
        undo.Push((strokeFile, [.. stroke], sel));
        redo.Clear();
        stroke.Clear();
        Dirty = true;
        Announce();
    }

    /// <summary>
    /// Throw away an uncommitted stroke by restoring its before-bytes. Used when the file or the
    /// canvas mode changes mid-drag: committing would be worse (bytes with no undo entry), and
    /// leaving them is worse still.
    /// </summary>
    public void AbortStroke()
    {
        if (stroke.Count == 0) return;
        Gfx.ApplyStroke(rom, strokeFile, [.. stroke], redo: false);
        stroke.Clear();
        Gfx.InvalidateCache(rom);
    }

    public bool Undo()
    {
        EndStroke();
        if (undo.Count == 0) return false;
        var e = undo.Pop();
        Gfx.ApplyStroke(rom, e.File, e.Edits, redo: false);
        redo.Push(e);
        SelectionHint = e.Sel is { } s && e.File == File ? (true, s.Before) : (false, null);
        Announce();
        return true;
    }

    /// <summary>Follow a file that changed id — a stock fork saved out as its own ExGFX. The open
    /// file and every history entry pointing at the old id now point at the new one, so undo
    /// still lands on the bytes it recorded instead of quietly doing nothing.</summary>
    internal void Retarget(int from, int to)
    {
        Remap(undo); Remap(redo);
        if (File == from) File = to;
        if (strokeFile == from) strokeFile = to;

        void Remap(Stack<(int File, (int Off, byte Before, byte After)[] Edits,
                          (SelRect? Before, SelRect? After)? Sel)> s)
        {
            var items = s.ToArray();                        // top first
            s.Clear();
            for (int i = items.Length - 1; i >= 0; i--)
                s.Push(items[i].File == from ? items[i] with { File = to } : items[i]);
        }
    }

    public bool Redo()
    {
        if (redo.Count == 0) return false;
        var e = redo.Pop();
        Gfx.ApplyStroke(rom, e.File, e.Edits, redo: true);
        undo.Push(e);
        SelectionHint = e.Sel is { } s && e.File == File ? (true, s.After) : (false, null);
        Announce();
        return true;
    }

    private void Announce()
    {
        Gfx.InvalidateCache(rom);        // every consumer re-resolves through the import
        Committed?.Invoke(this, EventArgs.Empty);
    }
}
