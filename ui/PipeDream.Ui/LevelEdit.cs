namespace PipeDream.Ui;

/// <summary>
/// Editing a level's layer-1 objects.
///
/// THE OBJECT LIST IS THE LEVEL. Painting a Map16 tile does not write a grid — it appends
/// Direct Map16 objects to the stream, exactly as Lunar Magic does, and the grid is a
/// projection of that stream through the object engine. A grid-only edit renders correctly
/// and then vanishes on save, because the project stores objects.
///
/// So a stroke is optimistic on the way in (cells are painted straight into the image for
/// feedback) and authoritative on the way out (at stroke end the painted cells become DM16
/// objects, the stream is re-rendered, and the image is reconciled against the result).
/// Undo snapshots the object list, which makes one drag exactly one undo.
/// </summary>
public sealed class LevelEdit(Rom rom, LevelScene scene, IReadOnlyList<LevelObject> objects)
{
    private readonly List<LevelObject> objects = [.. objects];
    private readonly Stack<List<LevelObject>> undo = new();
    private readonly Stack<List<LevelObject>> redo = new();
    private readonly Dictionary<(int X, int Y), int> stroke = [];

    public LevelScene Scene { get; private set; } = scene;
    public IReadOnlyList<LevelObject> Objects => objects;
    public int UndoDepth => undo.Count;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool Dirty { get; private set; }
    public bool InStroke => stroke.Count > 0;

    /// <summary>Cells whose pixels changed since the last <see cref="TakeDirty"/>.</summary>
    private readonly HashSet<(int X, int Y)> dirty = [];

    public HashSet<(int X, int Y)> TakeDirty()
    {
        var d = new HashSet<(int, int)>(dirty);
        dirty.Clear();
        return d;
    }

    /// <summary>
    /// Why tile placement is unavailable on this base, or null when it works. Placed tiles
    /// ARE Direct Map16 objects, and a ROM without LM's DM16 ASM renders them as nothing —
    /// so without this the paint would appear to work and then vanish on reconcile.
    /// </summary>
    public string? TilePlacementBlocked => rom.HasDm16Hijack
        ? null
        : "this base has no Direct Map16 support — use a prepped project base, "
          + "or open the ROM in Lunar Magic once";

    /// <summary>Paint one cell into the current stroke. Optimistic: the pixels change now so
    /// the drag feels immediate, and the object stream catches up at <see cref="EndStroke"/>.
    /// Returns false when nothing visibly changed.</summary>
    public bool Paint(int x, int y, int tile)
    {
        if (TilePlacementBlocked is not null) return false;
        if (x < 0 || y < 0 || x >= Scene.Grid.Width || y >= Scene.Grid.Height) return false;
        if (y >= Scene.VisibleRows) return false;                 // rows the game never draws
        // DM16 objects address the FG lookup range only; BG-space picks (0x4000+) belong to
        // layer 2, and stamping one here would silently do nothing on save.
        if (tile is < 0 or >= 0x4000) return false;
        if (Scene.Grid.Get(x, y) == tile && !stroke.ContainsKey((x, y))) return false;

        stroke[(x, y)] = tile;
        Scene.Grid.Set(x, y, tile);
        Scene.RecomposeCell(x, y);
        dirty.Add((x, y));
        return true;
    }

    /// <summary>
    /// Close the stroke: turn its cells into DM16 objects, append them, and re-render. The
    /// whole stroke goes through Dm16Saver.FromBrush in ONE call so its run-merging applies
    /// across the stroke — a 40-cell horizontal drag becomes one wide object, not 40.
    /// </summary>
    public void EndStroke()
    {
        if (stroke.Count == 0) return;
        // COPY before clearing: aliasing the field and then clearing it empties both.
        var painted = new Dictionary<(int X, int Y), int>(stroke);
        stroke.Clear();

        int x0 = painted.Keys.Min(c => c.X), x1 = painted.Keys.Max(c => c.X);
        int y0 = painted.Keys.Min(c => c.Y), y1 = painted.Keys.Max(c => c.Y);
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;

        // Cells the stroke did not touch are left Empty, which FromBrush skips — so a
        // diagonal drag does not fill in its bounding box.
        var brush = new ushort[bw * bh];
        Array.Fill(brush, Map16Grid.Empty);
        foreach (var (cell, tile) in painted) brush[(cell.Y - y0) * bw + (cell.X - x0)] = (ushort)tile;

        bool vert = rom.IsVerticalMode(Scene.Level.Header.LevelMode);
        var added = Dm16Saver.FromBrush(brush, bw, bh, x0, y0, vert);
        added.RemoveAll(o => o.AbsoluteX >= Scene.Grid.Width);
        if (added.Count == 0) { Reconcile(); return; }

        undo.Push([.. objects]);
        redo.Clear();
        objects.AddRange(added);
        Dirty = true;
        Reconcile();
    }

    public bool Undo()
    {
        EndStroke();                              // an open stroke is still undoable
        if (undo.Count == 0) return false;
        redo.Push([.. objects]);
        Replace(undo.Pop());
        return true;
    }

    public bool Redo()
    {
        if (redo.Count == 0) return false;
        undo.Push([.. objects]);
        Replace(redo.Pop());
        return true;
    }

    /// <summary>
    /// This level's state in the shape the save path takes. The UI holds no opinion about
    /// what a .pdp records — it hands over the object streams and the sprites, and
    /// LevelEditState.Stash applies the same rules the ImGui editor's save uses, because it
    /// IS the same code.
    /// </summary>
    public LevelEditState EditState() => new()
    {
        Layer1 = [.. objects],
        Layer2 = Scene.Layer2 is null ? null : baseLayer2,
        BaseLayer2 = baseLayer2,
        Sprites = Scene.Sprites,
    };

    /// <summary>Layer-2 objects are not editable here yet; carrying the base stream through
    /// unchanged is what keeps Stash from recording an edit that never happened.</summary>
    private List<LevelObject>? baseLayer2;

    private void Replace(List<LevelObject> next)
    {
        objects.Clear();
        objects.AddRange(next);
        Dirty = true;
        Reconcile();
    }

    /// <summary>Re-render the object stream and reconcile the image against it. This is what
    /// makes the optimistic paint honest: if the engine renders something different from what
    /// was painted (a clipped object, an overlap), the canvas ends up showing the ENGINE's
    /// answer rather than the guess.</summary>
    private void Reconcile()
    {
        // Encode and run the EMULATED engine, which is what produced the baseline grid and
        // what the ImGui editor re-renders with. PortedObjectEngine is a C# reimplementation
        // and does not agree with it cell-for-cell, so mixing the two makes every edit look
        // like it changed parts of the level nobody touched.
        var norm = LevelEncoder.NormalizeStream(objects);
        byte[] encoded = LevelEncoder.Encode(Scene.Level, norm);
        Map16Grid next;
        try { next = ObjectEngine.RenderEmulatedStream(rom, Scene.Level.Header, encoded, 0); }
        catch { return; }                      // emulation failed: keep the optimistic pixels
        foreach (var cell in Changed(Scene.Grid, next)) dirty.Add(cell);
        Scene.ReplaceGrid(next);
    }

    private static IEnumerable<(int X, int Y)> Changed(Map16Grid a, Map16Grid b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (a.Get(x, y) != b.Get(x, y)) yield return (x, y);
    }
}
