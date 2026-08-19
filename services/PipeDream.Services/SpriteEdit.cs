namespace PipeDream.Services;

/// <summary>
/// Editing a level's sprite list: place, lasso-select, move, duplicate, delete.
///
/// Sprites are positioned by CELL but selected by PIXEL. A sprite's spawn cell is a single
/// 16x16 square, while what it draws can be far larger and offset — a Banzai Bill's cell is
/// nowhere near its sprite — so lassoing by cell would miss most of what you can see. The
/// overlay's pixel bounds are the hit target, and the spawn cell is only used for placement.
/// </summary>
public sealed class SpriteEdit(SpriteData sprites, SpriteOverlay? overlay, bool vertical)
{
    private readonly Stack<List<Sprite>> undo = new();
    private readonly Stack<List<Sprite>> redo = new();

    public SpriteData Sprites { get; } = sprites;
    public SpriteOverlay? Overlay { get; set; } = overlay;
    public HashSet<int> Selection { get; } = [];
    public bool Dirty { get; private set; }
    public int UndoDepth => undo.Count;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>Sprites hidden while their originals are being dragged, so only the ghost shows.</summary>
    public HashSet<int>? Hidden { get; set; }

    /// <summary>Build a sprite record at a cell. Vertical levels swap the axes: the "screen"
    /// runs down the level, so the cell's Y is the absolute coordinate.</summary>
    public static Sprite At(int number, int extra, int cx, int cy, bool vert, byte[]? extraBytes = null)
    {
        int abs = vert ? cy : cx, y = vert ? cx : cy;
        return new Sprite(Screen: (abs >> 4) & 0x1F, XNibble: abs & 15, Y: y & 0x1F,
                          Extra: extra, Number: number, ExtraBytes: extraBytes);
    }

    /// <summary>Pixel rectangle a sprite occupies on screen, falling back to its spawn cell
    /// for sprites the overlay could not draw (badge-only).</summary>
    public (int X0, int Y0, int X1, int Y1) PixelRect(int i)
    {
        if (Overlay?.PixelBounds(i) is { } b) return (b.MinX, b.MinY, b.MaxX, b.MaxY);
        var (cx, cy) = Sprites.Sprites[i].Cell(vertical);
        return (cx * 16, cy * 16, cx * 16 + 16, cy * 16 + 16);
    }

    /// <summary>Sprite whose SPAWN CELL is this cell — placement and drag-start use the cell,
    /// because that is the thing being moved.</summary>
    public int? IndexAtCell(int cx, int cy)
    {
        for (int i = 0; i < Sprites.Sprites.Count; i++)
            if (Sprites.Sprites[i].Cell(vertical) == (cx, cy)) return i;
        return null;
    }

    /// <summary>Select everything whose drawn pixels overlap a level-pixel rectangle.</summary>
    public void SelectInPixelRect(int rx, int ry, int rw, int rh)
    {
        Selection.Clear();
        for (int i = 0; i < Sprites.Sprites.Count; i++)
        {
            var (x0, y0, x1, y1) = PixelRect(i);
            if (x0 < rx + rw && x1 > rx && y0 < ry + rh && y1 > ry) Selection.Add(i);
        }
    }

    public bool Place(int number, int cx, int cy)
    {
        Snapshot();
        Sprites.Sprites.Add(At(number, 0, cx, cy, vertical));
        return Commit();
    }

    public bool MoveSelected(int dx, int dy)
    {
        if (Selection.Count == 0 || (dx == 0 && dy == 0)) return false;
        Snapshot();
        foreach (int i in Selection)
        {
            var s = Sprites.Sprites[i];
            var (cx, cy) = s.Cell(vertical);
            Sprites.Sprites[i] = At(s.Number, s.Extra, cx + dx, cy + dy, vertical, s.ExtraBytes);
        }
        return Commit();
    }

    /// <summary>Copy the selection so its top-left cell lands on (cx, cy), and select the copies.</summary>
    public bool DuplicateSelected(int cx, int cy)
    {
        if (Selection.Count == 0) return false;
        Snapshot();
        var cells = Selection.Select(i => (I: i, Cell: Sprites.Sprites[i].Cell(vertical))).ToList();
        int ax = cells.Min(c => c.Cell.X), ay = cells.Min(c => c.Cell.Y);
        var added = new List<int>();
        foreach (var (i, cell) in cells)
        {
            var s = Sprites.Sprites[i];
            added.Add(Sprites.Sprites.Count);
            Sprites.Sprites.Add(At(s.Number, s.Extra, cx + cell.X - ax, cy + cell.Y - ay, vertical, s.ExtraBytes));
        }
        Selection.Clear();
        foreach (int i in added) Selection.Add(i);
        return Commit();
    }

    public bool DeleteSelected()
    {
        if (Selection.Count == 0) return false;
        Snapshot();
        foreach (int i in Selection.OrderByDescending(i => i))
            if (i < Sprites.Sprites.Count) Sprites.Sprites.RemoveAt(i);
        Selection.Clear();
        return Commit();
    }

    public bool Undo()
    {
        if (undo.Count == 0) return false;
        redo.Push([.. Sprites.Sprites]);
        Restore(undo.Pop());
        return true;
    }

    public bool Redo()
    {
        if (redo.Count == 0) return false;
        undo.Push([.. Sprites.Sprites]);
        Restore(redo.Pop());
        return true;
    }

    private void Snapshot() { undo.Push([.. Sprites.Sprites]); redo.Clear(); }

    private bool Commit() { Dirty = true; return true; }

    private void Restore(List<Sprite> list)
    {
        Sprites.Sprites.Clear();
        Sprites.Sprites.AddRange(list);
        // Indices shift when the list changes length, so a stale selection would point at
        // whatever moved into those slots.
        Selection.Clear();
        Dirty = true;
    }
}
