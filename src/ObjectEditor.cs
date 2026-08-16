using System.Numerics;
using System.Runtime.InteropServices;
using Foster.Framework;
using ImGuiNET;

namespace PipeDream;

// ---- Layer-1 object editing (placed tiles are DM16 objects — same pipeline) ----
// Object placement/move/duplicate/delete/resize plumbing, the tracked re-render with
// per-cell owner attribution, object undo snapshots, the save path, and the Objects
// palette tab + its catalog atlas.
internal sealed class ObjectEditor(EditorApp app)
{
    private readonly EditorApp app = app;

    internal Texture? objCatTex;                          // catalog thumbnail atlas
    private int[] objCatNums = Array.Empty<int>();
    private readonly Dictionary<int, (int cw, int ch, float u0, float v0, float u1, float v1)> objCatUV = new();
    internal int objCatTileset = -1;                      // tileset the catalog was built for
    private const int ObjDefaultSize = 0x22;              // default placed size: 3 wide x 3 tall
    // Exact footprints from the tracked object render: per-cell topmost owner (objList
    // index + 1; 0/Empty = none), per-cell full writer stack bottom→top (overlap/z-order;
    // key = y*Width+x), and per-object FULL-extent bounds (buried cells included).
    // Null/empty when emulation failed.
    internal Map16Grid? objOwners;
    internal Dictionary<int, ushort[]>? objStacks;
    internal (int x0, int y0, int x1, int y1)?[] objBounds = Array.Empty<(int, int, int, int)?>();

    // Re-render the level grid from the edited object list.
    internal void RenderObjects()
    {
        if (app.rom is null || app.level is null || app.objList is null || app.grid is null) return;
        if (RenderObjectsTracked() is not { } g) return;
        app.baseGrid = g;
        app.grid = g;
        app.canvasFull = true;
        app.levelDirty = true;
    }

    // Tracked render of the current object list: the grid plus per-cell owner attribution
    // (owner id = objList index + 1, mapped through NormalizeStream provenance and each
    // record's byte offsets). Refreshes objOwners/objBounds; null (and no owners) on
    // emulation failure — callers fall back to declared rects.
    internal Map16Grid? RenderObjectsTracked()
    {
        objOwners = null;
        objStacks = null;
        objBounds = Array.Empty<(int, int, int, int)?>();
        if (app.rom is null || app.level is null || app.objList is null) return null;
        var prov = new List<int>();
        var norm = LevelEncoder.NormalizeStream(app.objList, prov);
        var offsets = new List<int>();
        byte[] encoded = LevelEncoder.Encode(app.level, app.rom, norm, offsets);
        var streamOwner = new ushort[encoded.Length];
        for (int i = 0; i < norm.Count; i++)
        {
            if (prov[i] < 0) continue;                    // inserted screen jump
            int end = i + 1 < norm.Count ? offsets[i + 1] : encoded.Length - 1;   // stop before 0xFF
            for (int b = offsets[i]; b < end; b++) streamOwner[b] = (ushort)(prov[i] + 1);
        }
        Map16Grid g;
        Map16Grid? owners;
        Dictionary<int, ushort[]>? stacks;
        try { g = ObjectEngine.RenderEmulatedStream(app.rom, app.level.Header, encoded, 0, streamOwner, out owners, out stacks); }
        catch { return null; }
        objOwners = owners;
        objStacks = stacks;
        // Bounds from the full writer stacks, so a buried object keeps its real extent.
        var bounds = new (int x0, int y0, int x1, int y1)?[app.objList.Count];
        if (owners is not null && stacks is not null)
            foreach (var (cell, ids) in stacks)
            {
                int x = cell % owners.Width, y = cell / owners.Width;
                foreach (ushort v in ids)
                {
                    if (v < 1 || v > app.objList.Count) continue;
                    bounds[v - 1] = bounds[v - 1] is { } e
                        ? (Math.Min(e.x0, x), Math.Min(e.y0, y), Math.Max(e.x1, x), Math.Max(e.y1, y))
                        : (x, y, x, y);
                }
            }
        objBounds = bounds;
        return g;
    }

    /// <summary>ObjList indices stacked under a cell, bottom→top (z = stream order).</summary>
    private int[] ObjStackAt(int cx, int cy)
    {
        if (app.objList is not null && objOwners is { } og &&
            objStacks?.TryGetValue(cy * og.Width + cx, out var ids) == true)
            return ids.Where(v => v >= 1 && v <= app.objList.Count).Select(v => (int)v - 1).ToArray();
        return ObjIndexAt(cx, cy) is int i ? new[] { i } : Array.Empty<int>();
    }

    // LM-style stationary click on overlapping objects: selects the topmost; clicking
    // again selects the next object beneath the current one, wrapping back to the top.
    // A multi-selection collapses to the topmost object under the cursor.
    internal void CycleSelectionAt(int cx, int cy)
    {
        var stack = ObjStackAt(cx, cy);                  // bottom→top
        if (stack.Length == 0) { app.selObjs.Clear(); return; }
        int pick = stack[^1];
        if (app.selObjs.Count == 1)
        {
            int pi = Array.IndexOf(stack, app.selObjs.First());
            if (pi >= 0) pick = stack[(pi - 1 + stack.Length) % stack.Length];
        }
        app.selObjs.Clear();
        app.selObjs.Add(pick);
    }

    /// <summary>Footprint bounding box (exact, from the tracked render) with declared-rect fallback.</summary>
    internal (int x, int y, int w, int h) ObjBBox(int i)
        => i < objBounds.Length && objBounds[i] is { } b ? (b.x0, b.y0, b.x1 - b.x0 + 1, b.y1 - b.y0 + 1)
                                                         : ObjRect(app.objList![i]);

    // ---- object resizability (probe lives in ObjectEngine; cached per tileset here) ----

    private readonly Dictionary<(int tileset, int num), ObjectEngine.ObjResize> objResizeCache = new();

    internal ObjectEngine.ObjResize ResizeInfo(LevelObject o)
    {
        const ObjectEngine.SizeSrc N = ObjectEngine.SizeSrc.None;
        var rect = new ObjectEngine.ObjResize(ObjectEngine.SizeSrc.Lo, ObjectEngine.SizeSrc.Hi);
        if (o.Extended || o.IsScreenExit) return new(N, N);
        if (o.IsDm16) return rect;   // size math handled DM16-specifically (Dm16Size/Dm16Resized)
        if (app.rom is null || app.level is null) return rect;
        var key = (app.level.Header.Tileset, o.Number);
        if (objResizeCache.TryGetValue(key, out var r)) return r;
        return objResizeCache[key] = ObjectEngine.ProbeResize(app.rom, app.level, o.Number);
    }

    internal void ReplaceObject(int oi, LevelObject o)
    {
        if (app.objList is null) return;
        var before = new List<LevelObject>(app.objList);
        app.objList[oi] = o;
        PushObjectEdit(before);
        RenderObjects();
    }

    // Stamp the current tile brush as Direct Map16 OBJECTS (LM parity: a placed tile is
    // an object in the stream — selectable, movable, resizable like any other).
    internal void StampBrushObjects(int cx, int cy)
    {
        if (app.objList is null || app.rom is null || app.level is null || app.grid is null) return;
        if (!app.rom.HasDm16Hijack)
        { app.saveStatus = "ROM lacks LM Direct Map16 ASM — tile placement needs an LM-saved ROM."; return; }
        // Layer-1 DM16 references the FG lookup range only (< 0x1000); BG picks (0x4000+)
        // would stamp onto layer 2. ponytail: layer-2 tile editing later.
        if (!app.brushTiles.Any(t => t != Map16Grid.Empty && (t & ObjectEngine.Marker) == 0 && t < 0x1000))
        { app.saveStatus = "BG Map16 tiles live on layer 2 — layer-2 stamping isn't supported yet."; return; }
        bool vert = app.rom.IsVerticalMode(app.level.Header.LevelMode);
        // Trim at the level bottom — the engine would bleed writes into the next screen.
        int h = Math.Min(app.brushH, (vert ? app.grid.Height : 27) - cy);
        if (h <= 0) return;
        var added = Dm16Saver.FromBrush(app.brushTiles, app.brushW, h, cx, cy, vert);
        // ponytail: FromBrush reads the full brush; trim objects that start off-canvas.
        added.RemoveAll(o => o.AbsoluteX >= app.grid.Width);
        if (added.Count == 0) return;
        var before = new List<LevelObject>(app.objList);
        app.objList.AddRange(added);
        PushObjectEdit(before);
        RenderObjects();
    }

    // A rough footprint for hit-testing/selection: the object's declared W×H rect
    // (extended objects are single-cell). Not pixel-exact for irregular objects.
    private (int x, int y, int w, int h) ObjRect(LevelObject o)
    {
        if (o.Extended || o.IsScreenExit) return (o.AbsoluteX, o.Y, 1, 1);
        if (o.IsDm16) { var (dw, dh) = o.Dm16Size(); return (o.AbsoluteX, o.Y, dw, dh); }
        return (o.AbsoluteX, o.Y, Math.Clamp(o.Width, 1, 32), Math.Clamp(o.Height, 1, 32));
    }

    internal int? ObjIndexAt(int cx, int cy)
    {
        if (app.objList is null) return null;
        // Exact: the object that last wrote this cell (LM behavior — topmost wins,
        // irregular shapes like slopes hit-test on their real tiles).
        if (objOwners is not null)
        {
            int v = objOwners.Get(cx, cy);
            if (v >= 1 && v != Map16Grid.Empty && v <= app.objList.Count) return v - 1;
        }
        // Fallback (no owner data, or cell unowned): topmost declared rect containing the cell.
        for (int i = app.objList.Count - 1; i >= 0; i--)
        {
            var (x, y, w, h) = ObjRect(app.objList[i]);
            if (cx >= x && cx < x + w && cy >= y && cy < y + h) return i;
        }
        return null;
    }

    internal static LevelObject ObjAt(LevelObject src, int cx, int cy)
        => new(src.NewScreen, src.Number, (cx >> 4) & 0x1F, cx & 15, cy & 0x1F,
               src.Byte3, src.ExtraByte, src.Dm16Tile, src.Dm16Page, src.Dm16ExtX, src.Dm16ExtH);

    internal void PlaceObject(int number, int cx, int cy)
    {
        if (app.objList is null) return;
        var before = new List<LevelObject>(app.objList);
        app.objList.Add(new LevelObject(false, number, (cx >> 4) & 0x1F, cx & 15, cy & 0x1F, ObjDefaultSize, -1));
        PushObjectEdit(before);
        RenderObjects();
    }

    internal void MoveSelectedObjects(int dx, int dy)
    {
        if (app.objList is null) return;
        var before = new List<LevelObject>(app.objList);
        foreach (int i in app.selObjs)
        {
            var o = app.objList[i];
            app.objList[i] = ObjAt(o, Math.Max(0, o.AbsoluteX + dx), Math.Clamp(o.Y + dy, 0, 0x1F));
        }
        PushObjectEdit(before);
        RenderObjects();
    }

    internal void DuplicateSelectedObjects(int cx, int cy)
    {
        if (app.objList is null || app.selObjs.Count == 0) return;
        var before = new List<LevelObject>(app.objList);
        int ax = app.selObjs.Min(i => app.objList[i].AbsoluteX), ay = app.selObjs.Min(i => app.objList[i].Y);
        var added = new List<int>();
        foreach (int i in app.selObjs.OrderBy(i => i))
        {
            var o = app.objList[i];
            added.Add(app.objList.Count);
            app.objList.Add(ObjAt(o, Math.Max(0, cx + o.AbsoluteX - ax), Math.Clamp(cy + o.Y - ay, 0, 0x1F)));
        }
        app.selObjs.Clear();
        foreach (int i in added) app.selObjs.Add(i);
        PushObjectEdit(before);
        RenderObjects();
    }

    internal void DeleteSelectedObjects()
    {
        if (app.objList is null || app.selObjs.Count == 0) return;
        var before = new List<LevelObject>(app.objList);
        foreach (int i in app.selObjs.OrderByDescending(i => i)) app.objList.RemoveAt(i);
        app.selObjs.Clear();
        PushObjectEdit(before);
        RenderObjects();
    }

    private const int AirTile = 0x25;   // blank sky; a lone air tile is not a real selection

    // Copy a level region into the brush (LM-style: what you select is what you stamp).
    // A 1x1 grab also syncs the Map16 palette selection.
    // Grab the tiles under a cell rect as the stamp brush (Ctrl+lasso). Arms the brush:
    // right-click then stamps it as DM16 objects.
    internal void GrabSelection(int x, int y, int w, int h)
    {
        if (app.grid is null) return;
        app.brushW = w; app.brushH = h;
        app.brushTiles = new ushort[w * h];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                app.brushTiles[j * w + i] = (ushort)app.grid.Get(x + i, y + j);
        if (w == 1 && h == 1 && app.brushTiles[0] != Map16Grid.Empty && (app.brushTiles[0] & ObjectEngine.Marker) == 0)
            app.selectedMap16 = app.brushTiles[0];
        app.selectedObjCat = -1;             // brush armed: right-click stamps tiles
    }

    internal void PushObjectEdit(List<LevelObject> before)
    {
        if (app.objList is null) return;
        app.currentLevelTouched = true;
        var after = new List<LevelObject>(app.objList);
        app.history.Push(() => RestoreObjects(before), () => RestoreObjects(after));
    }

    private void RestoreObjects(List<LevelObject> list)
    {
        if (app.objList is null) return;
        app.currentLevelTouched = true;
        app.objList.Clear();
        app.objList.AddRange(list);
        app.selObjs.Clear();
        RenderObjects();
    }

    // Objects palette tab: the level's parsed object list. Selection is groundwork for
    // object editing later; today it's an inspector.
    // Objects tab: the placeable-object catalog (thumbnails from this tileset), right-click
    // the level to place the selected one. Names from the SMW source dispatch comments.
    internal void DrawObjectsTab()
    {
        if (app.level is null) { ImGui.TextDisabled("No level."); return; }
        ImGui.TextDisabled($"tileset {app.level.Header.Tileset}  —  select, then right-click the level to place");
        if (objCatTex is null) BuildObjectCatalog();   // lazy: first view of the tab (per tileset)
        if (ImGui.BeginChild("objcat"))
        {
            for (int i = 0; i < objCatNums.Length; i++)
            {
                int num = objCatNums[i];
                if (objCatTex is not null && objCatUV.TryGetValue(num, out var uv))
                {
                    ImGui.Image(app.imgui!.GetTextureID(objCatTex), new Vector2(48, 48),
                                new Vector2(uv.u0, uv.v0), new Vector2(uv.u1, uv.v1));
                    ImGui.SameLine();
                }
                if (ImGui.Selectable($"{num:X2}  {ObjectNames.Standard(num)}###objcat{num}",
                                     app.selectedObjCat == num, ImGuiSelectableFlags.None, new Vector2(0, 48)))
                    app.selectedObjCat = num;
            }
            ImGui.EndChild();
        }
    }

    // Per-tileset object footprint geometry (changed cells vs an empty render), cached so
    // the object engine runs only on tileset change; thumbnails recompose per palette.
    private readonly Dictionary<int, (int bx, int by, int bw, int bh, (int cx, int cy, ushort t)[] cells)> objCatCells = new();

    private void BuildObjectFootprints()
    {
        objCatCells.Clear();
        if (app.rom is null || app.level is null) return;
        var empty = new List<LevelObject>();
        Map16Grid baseG;
        try { baseG = ObjectEngine.RenderEmulatedStream(app.rom, app.level.Header, LevelEncoder.Encode(app.level, app.rom, empty), 0, ObjectEngine.SoloBudget); }
        catch { return; }
        // On LM ROMs, 0x22/0x23/0x27/0x29 dispatch to the DM16 handlers (which expect
        // extra tile bytes a bare 3-byte record doesn't have — the handler runs away)
        // and 0x26/0x28 are LM no-tile directives. Tiles come from the Map16 tab instead.
        bool dm16 = app.rom.HasDm16Hijack;
        for (int num = 1; num <= 0x3F; num++)
        {
            if (dm16 && num is 0x22 or 0x23 or 0x26 or 0x27 or 0x28 or 0x29) continue;
            var one = new List<LevelObject> { new(false, num, 0, 4, 10, ObjDefaultSize, -1) };
            Map16Grid g;
            try { g = ObjectEngine.RenderEmulatedStream(app.rom, app.level.Header, LevelEncoder.Encode(app.level, app.rom, one), 0, ObjectEngine.SoloBudget); }
            catch { continue; }
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            var cells = new List<(int, int, ushort)>();
            for (int y = 0; y < g.Height; y++)
                for (int x = 0; x < g.Width; x++)
                {
                    int t = g.Get(x, y);
                    if (t == baseG.Get(x, y) || t == Map16Grid.Empty) continue;
                    cells.Add((x, y, (ushort)t));
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
            if (cells.Count == 0) continue;
            objCatCells[num] = (minX, minY, maxX - minX + 1, maxY - minY + 1, cells.ToArray());
        }
    }

    // Catalog atlas: one 48x48 thumbnail per placeable object, composed from the cached
    // footprint geometry with the current tileset's Map16 tiles (phase 0).
    internal void BuildObjectCatalog()
    {
        objCatTex?.Dispose(); objCatTex = null;
        objCatNums = Array.Empty<int>();
        objCatUV.Clear();
        if (app.rom is null || app.level is null || app.tileCaches is null) return;
        if (objCatTileset != app.level.Header.Tileset) { BuildObjectFootprints(); objCatTileset = app.level.Header.Tileset; }
        var nums = objCatCells.Keys.OrderBy(n => n).ToArray();
        if (nums.Length == 0) return;
        const int cell = 48;
        var cache = app.tileCaches[0];
        var img = new uint[cell * cell * nums.Length];
        for (int i = 0; i < nums.Length; i++)
        {
            var fp = objCatCells[nums[i]];
            int srcW = fp.bw * 16, srcH = fp.bh * 16;
            // Nearest-neighbour fit into the cell, preserving aspect.
            int dw = srcW, dh = srcH;
            float scale = Math.Min(1f, (float)cell / Math.Max(srcW, srcH));
            dw = Math.Max(1, (int)(srcW * scale)); dh = Math.Max(1, (int)(srcH * scale));
            int ox = (cell - dw) / 2, oy = (cell - dh) / 2, rowBase = i * cell;
            foreach (var (cx, cy, t) in fp.cells)
            {
                uint[]? tile = (t & ObjectEngine.Marker) != 0 || t >= cache.Length ? null : cache[t];
                if (tile is null) continue;
                for (int py = 0; py < 16; py++)
                    for (int px = 0; px < 16; px++)
                    {
                        uint c = tile[py * 16 + px];
                        if (c == 0) continue;
                        int sx = (cx - fp.bx) * 16 + px, sy = (cy - fp.by) * 16 + py;
                        int dx = ox + (int)(sx * scale), dy = oy + (int)(sy * scale);
                        if (dx >= 0 && dx < cell && dy >= 0 && dy < cell) img[(rowBase + dy) * cell + dx] = c;
                    }
            }
            objCatUV[nums[i]] = (fp.bw, fp.bh, 0, (float)rowBase / (cell * nums.Length),
                                 1, (float)(rowBase + cell) / (cell * nums.Length));
        }
        objCatTex = new Texture(app.GraphicsDevice, cell, cell * nums.Length, MemoryMarshal.AsBytes(img.AsSpan()));
        objCatNums = nums;
    }
}
