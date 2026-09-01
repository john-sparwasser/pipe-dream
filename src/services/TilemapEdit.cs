namespace PipeDream.Services;

/// <summary>
/// A grid of tile numbers, painted one cell at a time and undoable as a stroke.
///
/// Both background layers are this same thing at different grains, which is why it is one class:
/// layer 2 is 32x27 cells of BG Map16 tiles (16px each), layer 3 is 64x64 cells of layer-3 8x8
/// tiles. What differs is only how a (column, row) reaches its slot in the underlying array —
/// layer 2's two 16x27 screens are 0x1B0 apart, layer 3's four 32x32 screens are 0x400 apart —
/// so that mapping is the constructor's job and nothing else here has a grain to branch on.
///
/// The array IS the level's data: callers hand in the live buffer and read it back out to
/// serialise. Undo replays cell values, not whole grids, so a stroke costs what it touched.
/// </summary>
public sealed class TilemapEdit
{
    private readonly int[] cells;
    private readonly Func<int, int, int> indexOf;
    private readonly List<(int At, int Before, int After)> stroke = [];
    private readonly Stack<(int At, int Before, int After)[]> undo = new();
    private readonly Stack<(int At, int Before, int After)[]> redo = new();

    public int Cols { get; }
    public int Rows { get; }

    /// <summary>Pixels per cell — 16 for a BG Map16 tile, 8 for a layer-3 tile.</summary>
    public int CellPx { get; }

    public bool Dirty { get; internal set; }
    public bool InStroke => stroke.Count > 0;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public int UndoDepth => undo.Count;

    /// <summary>Raised when a stroke settles, or undo/redo moves one — the point at which the
    /// level's data changed and everything downstream needs rebuilding.</summary>
    public event Action? Committed;

    internal TilemapEdit(int[] cells, int cols, int rows, int cellPx, Func<int, int, int> indexOf)
    {
        this.cells = cells;
        this.indexOf = indexOf;
        Cols = cols; Rows = rows; CellPx = cellPx;
    }

    /// <summary>The live array, in its own storage order — what a caller serialises.</summary>
    public int[] Cells => cells;

    public bool InBounds(int col, int row) => (uint)col < Cols && (uint)row < Rows;

    /// <summary>The cell's value, or -1 outside the grid. -1 is also a legal VALUE for layer 3,
    /// where it means the tilemap never wrote that word (CONTRACT §12b).</summary>
    public int At(int col, int row) => InBounds(col, row) ? cells[indexOf(col, row)] : -1;

    /// <summary>Write one cell into the open stroke. A write of the value already there is
    /// dropped, so dragging back over painted cells does not pad the undo entry.</summary>
    public bool Stamp(int col, int row, int value)
    {
        if (!InBounds(col, row)) return false;
        int at = indexOf(col, row);
        if (cells[at] == value) return false;
        stroke.Add((at, cells[at], value));
        cells[at] = value;
        return true;
    }

    /// <summary>Close the open stroke into one undo entry. False when nothing was painted —
    /// a click that changed no cell is not an edit and must not clear the redo stack.</summary>
    public bool EndStroke()
    {
        if (stroke.Count == 0) return false;
        undo.Push([.. stroke]);
        redo.Clear();
        stroke.Clear();
        Dirty = true;
        Committed?.Invoke();
        return true;
    }

    public bool Undo() => Move(undo, redo, back: true);
    public bool Redo() => Move(redo, undo, back: false);

    private bool Move(Stack<(int At, int Before, int After)[]> from,
                      Stack<(int At, int Before, int After)[]> to, bool back)
    {
        if (from.Count == 0) return false;
        var entry = from.Pop();
        foreach (var (at, before, after) in entry) cells[at] = back ? before : after;
        to.Push(entry);
        Dirty = true;
        Committed?.Invoke();
        return true;
    }
}
