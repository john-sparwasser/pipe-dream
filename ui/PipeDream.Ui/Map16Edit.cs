namespace PipeDream.Ui;

/// <summary>
/// Editing Map16 tile definitions: each 16x16 tile is four 8x8 quadrant words, written
/// straight into the session ROM and undoable as a stroke.
///
/// Two orderings are in play and mixing them silently mirrors tiles. The ROM stores a def as
/// TL, BL, TR, BR; the editor works in VISUAL order TL, TR, BL, BR, because that is how they
/// sit on screen. Everything here takes visual quadrants and maps them on the way out.
///
/// Writes land in the ROM immediately so later stamps in the same stroke read the new state;
/// the undo entry and the graphics rebuild are deferred to <see cref="EndStroke"/>.
/// </summary>
internal sealed class Map16Edit(Rom rom, int tileset, Project? project)
{
    /// <summary>Raw word order in the ROM for each visual quadrant (TL, TR, BL, BR).</summary>
    private static readonly int[] RawOfVisual = [0, 2, 1, 3];

    /// <summary>LM's default-empty quadrant word.</summary>
    public const ushort Empty = 0x1004;

    private readonly List<(int Fo, ushort Before, ushort After)> stroke = [];
    private readonly Stack<(int Fo, ushort Before, ushort After)[]> undo = new();
    private readonly Stack<(int Fo, ushort Before, ushort After)[]> redo = new();

    public bool Dirty { get; private set; }
    public bool InStroke => stroke.Count > 0;
    public int UndoDepth => undo.Count;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>Raised when committed bytes changed, so caches and sheets can be rebuilt.</summary>
    public event Action? Committed;

    /// <summary>A tile's four quadrant words in VISUAL order, or null when it has no def.</summary>
    public Map16.Word[]? ReadDef(int tile)
    {
        int fo = Map16.DefFileOffset(rom, tileset, tile);
        if (fo < 0) return null;
        Map16.Word W(int raw) => new((ushort)(rom.Data[fo + raw * 2] | (rom.Data[fo + raw * 2 + 1] << 8)));
        return [W(0), W(2), W(1), W(3)];
    }

    /// <summary>File offset of one visual quadrant's word, or -1 when unbacked.</summary>
    public int QuadOffset(int tile, int visualQuad)
    {
        int fo = Map16.DefFileOffset(rom, tileset, tile);
        return fo < 0 ? -1 : fo + RawOfVisual[visualQuad] * 2;
    }

    /// <summary>Write one quadrant into the current stroke. Same-value writes are skipped, so
    /// dragging over an already-correct quadrant does not bloat the undo entry.</summary>
    public bool StampQuad(int tile, int visualQuad, ushort raw)
    {
        int fo = QuadOffset(tile, visualQuad);
        if (fo < 0) return false;
        ushort before = (ushort)(rom.Data[fo] | (rom.Data[fo + 1] << 8));
        if (before == raw) return false;
        rom.Data[fo] = (byte)raw;
        rom.Data[fo + 1] = (byte)(raw >> 8);
        stroke.Add((fo, before, raw));
        Capture(tile);
        return true;
    }

    public void EndStroke()
    {
        if (stroke.Count == 0) return;
        undo.Push([.. stroke]);
        redo.Clear();
        stroke.Clear();
        Dirty = true;
        Committed?.Invoke();
    }

    public bool Undo()
    {
        EndStroke();
        if (undo.Count == 0) return false;
        var e = undo.Pop();
        Apply(e, redo: false);
        redo.Push(e);
        return true;
    }

    public bool Redo()
    {
        if (redo.Count == 0) return false;
        var e = redo.Pop();
        Apply(e, redo: true);
        undo.Push(e);
        return true;
    }

    /// <summary>
    /// Undo walks BACKWARD. A stroke records one entry per write, so a quadrant written twice
    /// would otherwise be restored to its intermediate value rather than its original.
    /// Reversing costs nothing when the offsets are distinct and removes the hazard either way.
    /// </summary>
    private void Apply((int Fo, ushort Before, ushort After)[] edits, bool redo)
    {
        for (int i = 0; i < edits.Length; i++)
        {
            var (fo, before, after) = edits[redo ? i : edits.Length - 1 - i];
            ushort v = redo ? after : before;
            rom.Data[fo] = (byte)v;
            rom.Data[fo + 1] = (byte)(v >> 8);
        }
        Dirty = true;
        Committed?.Invoke();
    }

    /// <summary>
    /// Make sure a tile's page exists, allocating it if not. Painting an empty page CREATES
    /// it — allocation is a consequence of editing, never a separate thing to ask for.
    /// Returns a problem, or null when the tile is now writable.
    /// </summary>
    public string? EnsurePage(int tile)
    {
        if (Map16.DefFileOffset(rom, tileset, tile) >= 0) return null;
        if (!Map16Layout.CanAllocate(tile)) return Map16Layout.UnusedPageNote(tile / 0x2000, tile >> 8);
        return rom.EnsureMap16Tiles(tile + 1);
    }

    /// <summary>
    /// Record the touched def slot in the project. The save re-reads the slot's current bytes
    /// from the ROM, so undo, redo and a page relocation need no extra bookkeeping.
    ///
    /// Extended FG tiles key by TILE NUMBER because their region moves when a page is
    /// allocated; vanilla FG and BG slots key by the def's SNES address, which is canonical
    /// across tilesets since tiles below 0x200 alias shared and per-tileset regions.
    /// </summary>
    private void Capture(int tile)
    {
        if (project is null) return;
        if (tile is >= 0x200 and < 0x4000)
            project.Data.Map16.Ext.TryAdd(tile.ToString("X3"), "");
        else
        {
            int fo = Map16.DefFileOffset(rom, tileset, tile);
            if (fo < 0) return;
            project.Data.Map16.Slots.TryAdd(Rom.PcToSnes(fo - rom.HeaderOffset).ToString("X6"), "");
        }
        project.MarkDirty();
    }

    /// <summary>
    /// Move a rectangle of tiles by a tile delta. Sources are read out first, cleared, then
    /// rewritten at the destination — overlap-safe, and one undo step because every write
    /// joins the same stroke. Refuses a partial move rather than dropping the tiles that
    /// would land on unallocated pages.
    /// </summary>
    public string? MoveTiles(int bank, int x, int y, int w, int h, int dx, int dy)
    {
        int TileAt(int tx, int ty) => bank * Map16Layout.BankTiles + ty * Map16Layout.Cols + tx;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                if (QuadOffset(TileAt(x + i + dx, y + j + dy), 0) < 0)
                    return "move target has unallocated tiles — paint there first.";

        var src = new Map16.Word[w * h][];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                src[j * w + i] = ReadDef(TileAt(x + i, y + j)) ?? new Map16.Word[4];
        for (int j = 0; j < h; j++)                       // clear sources first (overlap-safe)
            for (int i = 0; i < w; i++)
                for (int q = 0; q < 4; q++) StampQuad(TileAt(x + i, y + j), q, Empty);
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                for (int q = 0; q < 4; q++) StampQuad(TileAt(x + i + dx, y + j + dy), q, src[j * w + i][q].Raw);
        return null;
    }
}
