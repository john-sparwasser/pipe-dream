namespace PipeDream.Ui;

/// <summary>
/// Painting Map16 tiles into a level, with undo grouped by STROKE rather than by cell — one
/// drag is one undo, which is what "ctrl+Z undid only part of what I did" means when it goes
/// wrong. A stroke also refuses to record the same cell twice, so dragging back and forth
/// over one cell does not bury its original value.
///
/// Repaint is per-cell: only the cells a stroke touched are recomposed, in all four animation
/// phases, rather than rebuilding the whole level image on every mouse move.
/// </summary>
public sealed class LevelEdit(LevelScene scene)
{
    private readonly List<(int X, int Y, int Before, int After)> stroke = [];
    private readonly Stack<List<(int X, int Y, int Before, int After)>> undo = new();
    private readonly Stack<List<(int X, int Y, int Before, int After)>> redo = new();

    public LevelScene Scene { get; private set; } = scene;
    public bool InStroke => stroke.Count > 0;
    public int UndoDepth => undo.Count;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>Cells changed since the last <see cref="TakeDirty"/> — what the view needs to
    /// push to the bitmap.</summary>
    private readonly HashSet<(int X, int Y)> dirty = [];

    public HashSet<(int X, int Y)> TakeDirty()
    {
        var d = new HashSet<(int, int)>(dirty);
        dirty.Clear();
        return d;
    }

    public void Reset(LevelScene s)
    {
        Scene = s;
        stroke.Clear(); undo.Clear(); redo.Clear(); dirty.Clear();
    }

    /// <summary>Paint one cell as part of the current stroke. Returns false when nothing
    /// changed, so a drag across identical cells does not churn the image.</summary>
    public bool Paint(int x, int y, int tile)
    {
        if (x < 0 || y < 0 || x >= Scene.Grid.Width || y >= Scene.Grid.Height) return false;
        int before = Scene.Grid.Get(x, y);
        if (before == tile) return false;
        if (stroke.Any(e => e.X == x && e.Y == y)) { Apply(x, y, tile); return true; }

        stroke.Add((x, y, before, tile));
        Apply(x, y, tile);
        return true;
    }

    /// <summary>Close the current stroke; the next Paint starts a new undo entry.</summary>
    public void EndStroke()
    {
        if (stroke.Count == 0) return;
        undo.Push([.. stroke]);
        redo.Clear();                       // a new edit invalidates the redo branch
        stroke.Clear();
    }

    public bool Undo()
    {
        EndStroke();                        // an unfinished stroke is still undoable
        if (undo.Count == 0) return false;
        var entry = undo.Pop();
        // Reverse order: a stroke may have written the same cell more than once.
        for (int i = entry.Count - 1; i >= 0; i--) Apply(entry[i].X, entry[i].Y, entry[i].Before);
        redo.Push(entry);
        return true;
    }

    public bool Redo()
    {
        if (redo.Count == 0) return false;
        var entry = redo.Pop();
        foreach (var e in entry) Apply(e.X, e.Y, e.After);
        undo.Push(entry);
        return true;
    }

    private void Apply(int x, int y, int tile)
    {
        Scene.Grid.Set(x, y, tile);
        Scene.RecomposeCell(x, y);
        dirty.Add((x, y));
    }
}
