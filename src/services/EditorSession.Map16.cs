namespace PipeDream.Services;

// EditorSession — Map16 definition editing for the current tileset: the editor itself, the
// picker sheets it draws from, and the cheap repaint after a committed definition edit.
// The Map16 property and Map16Committed event are declared with the rest of the state in
// EditorSession.cs.
public sealed partial class EditorSession
{
    /// <summary>One Map16 editor per level, re-raising its commits under this class's event so
    /// the UI subscribes once instead of re-subscribing to a new object each level.</summary>
    private void NewMap16Edit()
    {
        if (Rom is null || Scene is null) return;
        Map16 = new Map16Edit(Rom, Scene.Level.Header.Tileset, Project);
        Map16.Committed += () => Map16Committed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Delete on a Map16 selection: put the tiles back to the base ROM's definitions.
    /// The pristine base is its on-disk copy — the session ROM is that plus the hydrated edits —
    /// so this needs a project. Undoable as one stroke, like any other Map16 edit.</summary>
    public bool ResetMap16Tiles(IEnumerable<int> tiles)
    {
        if (Map16 is not { } m) return false;
        if (Project is not { } p) { Report("no project — nothing to reset to"); return false; }
        m.Reset(tiles, Rom.Load(p.BaseRomPath));
        return true;
    }

    /// <summary>
    /// Repaint after a committed Map16 edit, touching only the tiles it actually changed.
    /// A definition edit used to cost a full scene rebuild — a quarter of a second before the
    /// stamped tile appeared — for a change to 256 pixels of artwork.
    ///
    /// Falls back to the full in-place recompose when the editor cannot say which tiles moved
    /// (undo and redo replay byte offsets, not tiles).
    /// </summary>
    public void RecomposeAfterMap16()
    {
        if (Rom is null || Scene is null) return;
        spriteCatalog = null;
        objectCatalog = null;
        if (Map16?.CommittedTiles is { } tiles) Scene.RecomposeTiles(Rom, LevelNum, tiles, paletteEdits);
        else Recolour();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- picker sheets ----
    /// <summary>The Map16 tile sheet the drawer picks from, one image per animation phase — a
    /// tile made of animated graphics has to animate wherever it is DRAWN, not only in the
    /// level.</summary>
    public (uint[]?[] Px, int W, int H) SheetPhases()
    {
        if (Scene is not { } s) return (new uint[4][], 0, 0);
        var px = new uint[4][];
        int w = 0, h = 0;
        for (int p = 0; p < 4; p++) (px[p], w, h) = s.Sheet(p);
        return (px, w, h);
    }

    /// <summary>The empty-page tile per phase, for the picker to tile over unallocated pages.</summary>
    public uint[]?[] PlaceholderPhases()
    {
        var px = new uint[4][];
        if (Rom is { } r && Scene is { } s)
            for (int p = 0; p < 4; p++) px[p] = s.Placeholder(r, LevelNum, p);
        return px;
    }

    /// <summary>The level's 8x8 GFX sheet in one palette row, for the Map16 editor's picker —
    /// again one per phase, off the scene's own per-phase graphics.</summary>
    public (uint[]?[] Px, int W, int H) ChrPhases(int palRow)
    {
        if (Rom is not { } r || Scene is not { } s) return (new uint[4][], 0, 0);
        var px = new uint[4][];
        int w = 0, h = 0;
        for (int p = 0; p < 4; p++)
        {
            if (s.Palettes[p] is not { } pal) continue;
            (px[p], w, h) = GfxSheets.Chr(s.Fg(r, LevelNum, p), pal, palRow);
        }
        return (px, w, h);
    }
}
