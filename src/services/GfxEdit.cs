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
    public enum Tool { Pencil, Fill, Eraser, Dropper, Select }

    public int File { get; private set; } = 0x14;
    public int PalRow { get; set; } = 2;

    /// <summary>Paint colour index, clamped to what the ROM's depth can hold. 0 = transparent.</summary>
    public int Color
    {
        get => color;
        set => color = Math.Clamp(value, 0, MaxColor);
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
        File = Math.Clamp(file, 0, 0xFFF);
    }

    /// <summary>Whether this file resolves to anything, and how it is backed.</summary>
    public string Status => rom.ImportedGfx.ContainsKey(File) ? "imported"
                          : Gfx.Cached(rom, File) is not null ? "stock" : "missing";

    public string? Name => rom.GfxName(File) is { Length: > 0 } n ? n : null;

    /// <summary>The file's bytes, or null when the id resolves nowhere.</summary>
    public byte[]? Bytes => Gfx.Cached(rom, File);

    public int Bpp => Gfx.RomBpp(rom);

    /// <summary>The highest colour index this ROM's depth can hold. A vanilla ROM is 3bpp, so
    /// colours run 0-7 — offering 16 would let you pick one that silently paints its low three
    /// bits instead, which looks like the editor ignoring the click.</summary>
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

    public void Cut(int x, int y, int w, int h)
    {
        Copy(x, y, w, h);
        WriteRect(x, y, w, h, null);
        strokeSel = ((x, y, w, h), (x, y, w, h));
        EndStroke();
    }

    /// <summary>Stamp the clipboard at (x,y) as one undo entry; pixels past the sheet edge are
    /// clipped away. False when there is nothing to paste (or nothing to paste onto).
    /// <paramref name="selBefore"/> is where the marquee sat before the paste, so undoing it can
    /// put the marquee back too — the paste itself becomes the selection.</summary>
    public bool Paste(int x, int y, SelRect? selBefore = null)
    {
        if (Clipboard is not { } c || Layout.Tiles == 0) return false;
        WriteRect(x, y, c.W, c.H, c.Px);
        strokeSel = (selBefore, (x, y, c.W, c.H));
        EndStroke();
        return true;
    }

    // A move previews by write-through, like a paint stroke: each step reverts the open stroke
    // and re-applies clear+stamp at the new offset, so release commits ONE undo entry.
    private (int X, int Y, int W, int H, byte[] Px)? moving;
    private (int Dx, int Dy) moved;

    public void BeginMove(int x, int y, int w, int h)
    {
        moving = (x, y, w, h, ReadRect(x, y, w, h));
        moved = (0, 0);
    }

    /// <summary>Preview the grabbed rectangle offset by (dx,dy) from where it started. Leaves the
    /// stroke open — <see cref="EndMove"/> commits it.</summary>
    public void MoveBy(int dx, int dy)
    {
        if (moving is not { } m) return;
        moved = (dx, dy);
        AbortStroke();
        if (dx == 0 && dy == 0) return;      // back home: no bytes changed, no empty undo entry
        WriteRect(m.X, m.Y, m.W, m.H, null);
        WriteRect(m.X + dx, m.Y + dy, m.W, m.H, m.Px);
    }

    public void EndMove()
    {
        if (moving is { } m)
            strokeSel = ((m.X, m.Y, m.W, m.H), (m.X + moved.Dx, m.Y + moved.Dy, m.W, m.H));
        moving = null;
        EndStroke();
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
