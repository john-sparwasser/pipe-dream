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

    /// <summary>
    /// Stamp a whole brush with its top-left at (cx, cy). Empty cells are skipped, so a
    /// grabbed brush with holes in it stamps holes rather than a solid block.
    ///
    /// Every stamped cell joins the SAME stroke, so dragging a 2x2 brush along a row still
    /// ends as one undo and one run-merged set of objects.
    /// </summary>
    public bool PaintBrush(int cx, int cy, ReadOnlySpan<ushort> tiles, int w, int h)
    {
        bool any = false;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                ushort t = tiles[j * w + i];
                if (t == Map16Grid.Empty) continue;
                any |= Paint(cx + i, cy + j, t);
            }
        return any;
    }

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
        // Layer 2 is not editable here yet, so the live stream IS the base stream. Reporting
        // both honestly matters: Stash records layer 2 only when the two DIFFER, and a null
        // base with a non-null live list is what marks a background-to-objects conversion.
        // Passing null for both happened to behave, but only by accident.
        Layer2 = Layer2Objects,
        BaseLayer2 = Layer2Objects,
        Sprites = HydratedSprites ?? Scene.Sprites,
    };

    /// <summary>The base ROM's layer-2 object stream, or null when layer 2 is a background
    /// image. Parsed once per level; layer-2 editing will make this the live copy.</summary>
    public List<LevelObject>? Layer2Objects { get; private set; }

    /// <summary>Sprites restored from the project, which win over the ROM's parsed list.</summary>
    public SpriteData? HydratedSprites { get; set; }

    /// <summary>Run the tracked render without recording an edit. Needed on every level load:
    /// it produces the per-cell object attribution that selection and hit-testing read, and it
    /// puts a project-hydrated level's own objects on screen instead of the ROM's parse.</summary>
    public void Rerender()
    {
        Layer2Objects ??= LevelParser.ParseLayer2(rom, Scene.Level.Number);
        Reconcile();
    }

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
    // ---- selection, from the tracked render ----

    /// <summary>Selected object indices.</summary>
    public HashSet<int> Selection { get; } = [];

    private Map16Grid? owners;                                   // per-cell topmost writer, id = index+1
    private Dictionary<int, ushort[]>? stacks;                   // per-cell full writer stack, bottom→top
    private (int x0, int y0, int x1, int y1)?[] bounds = [];

    /// <summary>Footprint bounding box from the tracked render, so a selection hugs what the
    /// object actually drew rather than its declared size.</summary>
    public (int X, int Y, int W, int H)? BBox(int i)
        => i >= 0 && i < bounds.Length && bounds[i] is { } b ? (b.x0, b.y0, b.x1 - b.x0 + 1, b.y1 - b.y0 + 1) : null;

    /// <summary>Topmost object at a cell, or null.</summary>
    public int? ObjectAt(int cx, int cy)
    {
        if (owners is null || cx < 0 || cy < 0 || cx >= owners.Width || cy >= owners.Height) return null;
        int v = owners.Get(cx, cy);
        return v >= 1 && v <= objects.Count ? v - 1 : null;
    }

    /// <summary>Indices stacked under a cell, bottom→top (z = stream order).</summary>
    private int[] StackAt(int cx, int cy)
    {
        if (owners is { } og && stacks?.TryGetValue(cy * og.Width + cx, out var ids) == true)
            return ids.Where(v => v >= 1 && v <= objects.Count).Select(v => (int)v - 1).ToArray();
        return ObjectAt(cx, cy) is int i ? [i] : [];
    }

    /// <summary>LM-style stationary click on overlapping objects: select the topmost, and
    /// clicking again steps to the one beneath, wrapping. A multi-selection collapses to the
    /// topmost under the cursor.</summary>
    public void CycleSelectionAt(int cx, int cy)
    {
        var stack = StackAt(cx, cy);
        if (stack.Length == 0) { Selection.Clear(); return; }
        int pick = stack[^1];
        if (Selection.Count == 1)
        {
            int pi = Array.IndexOf(stack, Selection.First());
            if (pi >= 0) pick = stack[(pi - 1 + stack.Length) % stack.Length];
        }
        Selection.Clear();
        Selection.Add(pick);
    }

    /// <summary>Select every object whose footprint overlaps a cell rectangle.</summary>
    public void SelectInRect(int rx, int ry, int rw, int rh)
    {
        Selection.Clear();
        for (int i = 0; i < objects.Count; i++)
            if (BBox(i) is { } b && b.X < rx + rw && b.X + b.W > rx && b.Y < ry + rh && b.Y + b.H > ry)
                Selection.Add(i);
    }

    /// <summary>The tiles under a rectangle, as a brush — Ctrl+drag "grab" in the ImGui editor.</summary>
    public (ushort[] Tiles, int W, int H) GrabTiles(int rx, int ry, int rw, int rh)
    {
        var t = new ushort[rw * rh];
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int gx = rx + x, gy = ry + y;
                t[y * rw + x] = gx >= 0 && gy >= 0 && gx < Scene.Grid.Width && gy < Scene.Grid.Height
                    ? (ushort)Scene.Grid.Get(gx, gy) : Map16Grid.Empty;
            }
        return (t, rw, rh);
    }

    public bool MoveSelected(int dx, int dy)
    {
        if (Selection.Count == 0 || (dx == 0 && dy == 0)) return false;
        undo.Push([.. objects]);
        redo.Clear();
        foreach (int i in Selection)
        {
            var o = objects[i];
            objects[i] = ObjectAtCell(o, o.AbsoluteX + dx, Math.Clamp(o.Y + dy, 0, 0x1F));
        }
        Dirty = true;
        Reconcile();
        return true;
    }

    public bool DeleteSelected()
    {
        if (Selection.Count == 0) return false;
        undo.Push([.. objects]);
        redo.Clear();
        foreach (int i in Selection.OrderByDescending(i => i))
            if (i >= 0 && i < objects.Count) objects.RemoveAt(i);
        Selection.Clear();
        Dirty = true;
        Reconcile();
        return true;
    }

    /// <summary>Copy the selection so its top-left lands on (cx,cy) — the ImGui editor's
    /// right-click-with-a-selection.</summary>
    public bool DuplicateSelected(int cx, int cy)
    {
        if (Selection.Count == 0) return false;
        var picked = Selection.OrderBy(i => i).Where(i => i >= 0 && i < objects.Count).ToList();
        if (picked.Count == 0) return false;
        int ox = picked.Min(i => BBox(i)?.X ?? objects[i].AbsoluteX);
        int oy = picked.Min(i => BBox(i)?.Y ?? objects[i].Y);

        undo.Push([.. objects]);
        redo.Clear();
        var added = picked.Select(i =>
        {
            var o = objects[i];
            return ObjectAtCell(o, o.AbsoluteX + (cx - ox), Math.Clamp(o.Y + (cy - oy), 0, 0x1F));
        }).ToList();
        objects.AddRange(added);
        Selection.Clear();
        for (int k = 0; k < added.Count; k++) Selection.Add(objects.Count - added.Count + k);
        Dirty = true;
        Reconcile();
        return true;
    }

    /// <summary>Default size byte for a catalog placement: 3 wide by 3 tall, as LM uses. Both
    /// nibbles are set because which one an object reads is per-object.</summary>
    private const int CatalogSize = 0x22;

    /// <summary>Place a standard object from the Objects catalog. Unlike a painted tile this is
    /// a real numbered object, so it resizes through byte 3 rather than the DM16 size model.</summary>
    public bool PlaceObject(int number, int cx, int cy)
    {
        undo.Push([.. objects]);
        redo.Clear();
        objects.Add(new LevelObject(false, number, (cx >> 4) & 0x1F, cx & 15, cy & 0x1F, CatalogSize, -1));
        Dirty = true;
        Reconcile();
        return true;
    }

    // ---- resize ----

    private readonly Dictionary<(int Tileset, int Num), ObjectEngine.ObjResize> resizeCache = [];

    /// <summary>Which axes an object can be resized on. DM16 tiles have their own size model
    /// (nibbles, or LM's extended Form B up to 128x256); standard objects use the probed
    /// byte-3 nibble sources, which is a per-tileset property and worth caching.</summary>
    public ObjectEngine.ObjResize ResizeInfo(LevelObject o)
    {
        const ObjectEngine.SizeSrc N = ObjectEngine.SizeSrc.None;
        var rect = new ObjectEngine.ObjResize(ObjectEngine.SizeSrc.Lo, ObjectEngine.SizeSrc.Hi);
        if (o.Extended || o.IsScreenExit) return new(N, N);
        if (o.IsDm16) return rect;
        var key = (Scene.Level.Header.Tileset, o.Number);
        if (resizeCache.TryGetValue(key, out var r)) return r;
        return resizeCache[key] = ObjectEngine.ProbeResize(rom, Scene.Level, o.Number);
    }

    /// <summary>The size a drag would produce, without committing: (x, y, w, h) in cells.
    /// <paramref name="edges"/> is the ImGui bitmask — 1 left, 2 right, 4 top, 8 bottom.</summary>
    public (int X, int Y, int W, int H)? PreviewResize(int index, int edges, int dx, int dy)
    {
        if (index < 0 || index >= objects.Count) return null;
        var o = objects[index];
        var rz = ResizeInfo(o);
        bool dm = o.IsDm16;
        var (w0, h0) = dm ? o.Dm16Size()
                          : (ObjectEngine.SizeOf(o.Byte3, rz.W), ObjectEngine.SizeOf(o.Byte3, rz.H));
        int maxW = dm ? 128 : ObjectEngine.MaxSize(rz.W), maxH = dm ? 256 : ObjectEngine.MaxSize(rz.H);
        int nx = o.AbsoluteX, ny = o.Y, nw = w0, nh = h0;
        if ((edges & 2) != 0) nw = Math.Clamp(w0 + dx, 1, maxW);
        if ((edges & 1) != 0) { nw = Math.Clamp(w0 - dx, 1, maxW); nx = Math.Max(0, nx + (w0 - nw)); }
        if ((edges & 8) != 0) nh = Math.Clamp(h0 + dy, 1, maxH);
        if ((edges & 4) != 0) { nh = Math.Clamp(h0 - dy, 1, maxH); ny = Math.Clamp(ny + (h0 - nh), 0, 0x1F); }
        // Clamp at the level bottom (LM parity): the engine will happily write past the last
        // row, bleeding into the next screen's RAM — a drag must not be able to do that.
        int maxRows = rom.IsVerticalMode(Scene.Level.Header.LevelMode) ? Scene.Grid.Height : 27;
        nh = Math.Max(1, Math.Min(nh, maxRows - ny));
        return (nx, ny, nw, nh);
    }

    /// <summary>Commit a resize drag. Returns false when the drag produced no change.</summary>
    public bool Resize(int index, int edges, int dx, int dy)
    {
        if (PreviewResize(index, edges, dx, dy) is not { } p) return false;
        var o = objects[index];
        var rz = ResizeInfo(o);
        bool dm = o.IsDm16;
        var (w0, h0) = dm ? o.Dm16Size()
                          : (ObjectEngine.SizeOf(o.Byte3, rz.W), ObjectEngine.SizeOf(o.Byte3, rz.H));
        if (p.X == o.AbsoluteX && p.Y == o.Y && p.W == w0 && p.H == h0) return false;

        undo.Push([.. objects]);
        redo.Clear();
        var moved = ObjectAtCell(o, p.X, p.Y);
        if (dm) objects[index] = moved.Dm16Resized(p.W, p.H);
        else
        {
            // Same source on both axes (diagonal slopes): one nibble drives both, so apply
            // whichever the drag actually changed.
            int b3 = rz.W == rz.H
                ? ObjectEngine.WithSize(o.Byte3, rz.W, p.W != w0 ? p.W : p.H)
                : ObjectEngine.WithSize(ObjectEngine.WithSize(o.Byte3, rz.W, p.W), rz.H, p.H);
            objects[index] = new LevelObject(false, o.Number, (p.X >> 4) & 0x1F, p.X & 15, p.Y,
                                             b3, o.ExtraByte, o.Dm16Tile, o.Dm16Page, o.Dm16ExtX, o.Dm16ExtH);
        }
        Dirty = true;
        Reconcile();
        return true;
    }

    private static LevelObject ObjectAtCell(LevelObject o, int x, int y)
        => new(o.NewScreen, o.Number, (x >> 4) & 0x1F, x & 15, y, o.Byte3, o.ExtraByte,
               o.Dm16Tile, o.Dm16Page, o.Dm16ExtX, o.Dm16ExtH);

    private void Reconcile()
    {
        // Encode and run the EMULATED engine, which is what produced the baseline grid and
        // what the ImGui editor re-renders with. PortedObjectEngine is a C# reimplementation
        // and does not agree with it cell-for-cell, so mixing the two makes every edit look
        // like it changed parts of the level nobody touched.
        //
        // The render is TRACKED — every cell remembers which stream record wrote it — because
        // selection has to hug an object's real footprint, not its declared rectangle.
        var prov = new List<int>();
        var norm = LevelEncoder.NormalizeStream(objects, prov);
        var offsets = new List<int>();
        byte[] encoded = LevelEncoder.Encode(Scene.Level, norm, offsets);
        var streamOwner = new ushort[encoded.Length];
        for (int i = 0; i < norm.Count; i++)
        {
            if (prov[i] < 0) continue;                      // inserted screen jump: owned by nobody
            int end = i + 1 < norm.Count ? offsets[i + 1] : encoded.Length - 1;   // stop before 0xFF
            for (int b = offsets[i]; b < end; b++) streamOwner[b] = (ushort)(prov[i] + 1);
        }

        Map16Grid next;
        try
        {
            next = ObjectEngine.RenderEmulatedStream(rom, Scene.Level.Header, encoded, 0,
                                                     streamOwner, out owners, out stacks);
        }
        catch { return; }                      // emulation failed: keep the optimistic pixels

        // Bounds from the FULL writer stacks, so an object buried under a later one keeps its
        // real extent instead of shrinking to whatever is still visible.
        var b2 = new (int x0, int y0, int x1, int y1)?[objects.Count];
        if (owners is not null && stacks is not null)
            foreach (var (cell, ids) in stacks)
            {
                int x = cell % owners.Width, y = cell / owners.Width;
                foreach (ushort v in ids)
                {
                    if (v < 1 || v > objects.Count) continue;
                    b2[v - 1] = b2[v - 1] is { } e
                        ? (Math.Min(e.x0, x), Math.Min(e.y0, y), Math.Max(e.x1, x), Math.Max(e.y1, y))
                        : (x, y, x, y);
                }
            }
        bounds = b2;

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
