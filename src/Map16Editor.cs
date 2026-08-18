using System.Numerics;
using System.Runtime.InteropServices;
using Foster.Framework;
using ImGuiNET;

namespace PipeDream;

// ---- Map16 edit mode (canvas view toggle: the canvas becomes the Map16 sheet, the
// left drawer becomes the 8x8 GFX palette; same grammar as level editing) ----
// Owns the composed Map16 sheets (picker tab + edit canvas), the 8x8 stamp brush and
// GFX palette drawer, tile-def editing (stroke-batched, undoable), the selected-tile
// properties drawer, and deferred page allocation.
internal sealed class Map16Editor(EditorApp app)
{
    private readonly EditorApp app = app;

    // Composed Map16 sheet, one texture per animation phase (CONTRACT §12). The level
    // canvas itself is owned by the LevelCanvas compositor.
    private readonly Texture?[] map16Texs = new Texture?[4];
    private int map16W, map16H;
    // LM-style Map16 banks: 8 banks x 0x2000 tiles. FG defs live in bank 0; the vanilla
    // BG pages appear at 0x4000 (bank 2, the DM16 BG form's +0x40 page numbering).
    private int map16Bank;
    private readonly Texture?[] map16BgTexs = new Texture?[4];
    private int map16BgW, map16BgH;
    private int? map16AllocPending;   // tile painted on an empty page; allocated next frame
    private (int tile, int quad, ushort raw)? map16AllocStamp;   // the paint that asked for it

    private Texture? m16ChrTex;                    // 8x8 GFX palette sheet (one palette row)
    internal int m16ChrPal = -1;                   // sheet cache key (reset on RebuildGraphics)
    private int m16ChrPhase = -1;
    // The 8x8 stamp brush: a lassoed WxH block of chr tiles (empty = nothing selected —
    // no reticle, no stamping). Single click in the drawer = a 1x1 block.
    private int m16BrushW, m16BrushH;
    private int[] m16BrushChr = Array.Empty<int>();
    private (int x, int y)? m16ChrDrag;            // drawer lasso anchor (chr cells)
    private int m16BrushPal = 2;                   // stamp palette row
    private bool m16BrushFX, m16BrushFY, m16BrushP;
    // Map16 canvas selection: a lassoed rect of 16x16 tiles that can be dragged to move
    // their defs (sources cleared to LM's default-empty), one undo step.
    internal (int x, int y)? m16Lasso;             // lasso anchor (tile cells in the bank)
    private (int x, int y, int w, int h)? m16Sel;  // selected tile rect (current bank)
    internal (int x, int y)? m16Move;              // move-drag anchor (tile cells)
    // A paint stroke over quadrants: raw byte edits batched into ONE undo entry and ONE
    // graphics rebuild on release (per-quadrant rebuilds would make dragging sluggish).
    private readonly List<(int fo, ushort before, ushort after)> m16Stroke = new();

    private const float Map16Zoom = 1f;   // tile picker (16px tiles at native size)

    // Map16 paint strokes commit when the right button is up — at frame start, so the
    // graphics rebuild never disposes textures already submitted to this frame.
    internal void CommitStrokeOnRelease()
    {
        if (m16Stroke.Count > 0 && !ImGui.IsMouseDown(ImGuiMouseButton.Right)) CommitM16Stroke();
    }

    /// <summary>Whether a tile's page can be brought into existence. Banks 0-1 (tiles
    /// 0x200-0x3FFF) are the four lookup-ladder ranges EnsureMap16Tiles can allocate and
    /// prep v3 dispatches to; bank 2 is the BG table, a fixed 0x200 defs at $0D9100 that
    /// cannot grow at all. Whether a specific BASE honours a range is EnsureMap16Tiles'
    /// answer, not this one — a prep-v2 base reports the upgrade hint instead.</summary>
    internal static bool CanAllocate(int tile) => tile is >= 0x200 and < 0x4000;

    /// <summary>What to say over an empty page, so the editor never advertises an allocation
    /// that cannot happen — the old label promised "click to allocate" on every unused page in
    /// every bank, including the ones that could not.</summary>
    internal static string UnusedPageNote(int bank, int page) =>
        bank == 2 ? "BG definitions are a fixed table"
        : bank < 2 ? "unused — paint here to create it"
        : "past the supported 0x3FFF tiles";

    // Deferred Map16 page allocation, requested by PAINTING on an empty page — runs before
    // any drawing so texture rebuilds never race the frame's draw data, then replays the
    // stamp that asked for it so the edit that triggered allocation is not swallowed.
    internal void RunPendingAlloc()
    {
        if (map16AllocPending is not int allocTile) return;
        map16AllocPending = null;
        var stamp = map16AllocStamp;
        map16AllocStamp = null;
        var err = app.rom is null ? "no ROM loaded" : app.rom.EnsureMap16Tiles(allocTile + 1);
        if (err is not null) { app.saveStatus = err; return; }

        // Allocation relocates the extended def region: recorded file offsets into the
        // now-dead block would write garbage into abandoned bytes and silently no-op on
        // screen. Drop the undo stack AND any stroke still buffering such offsets — the
        // bytes themselves survive, because the old defs are copied into the new block.
        app.history.Clear();
        m16Stroke.Clear();
        if (app.project is not null)
        { app.project.Data.Map16.TileCount = app.rom!.Map16TileCount; app.project.MarkDirty(); }
        app.session.RebuildGraphics();     // recompose caches/sheets with the new count
        app.levelDirty = true;             // the def region rides along on the next save
        app.saveStatus = $"Map16 page 0x{allocTile >> 8:X2} created";
        // Apply the edit that caused the allocation, now that its page exists.
        if (stamp is { } s) StampDefWord(s.tile, s.quad, s.raw);
    }

    // The Map16 edit canvas: the unified tile space at 2x, editable per 8x8 quadrant.
    // Same grammar as the level canvas — right-click stamps the drawer's 8x8 brush,
    // left-click selects a tile (arming it as the level stamp brush too), clicking an
    // unallocated FG page allocates it, X/Y/P flip the hovered quadrant, Ctrl+Z undoes.
    internal void DrawMap16Canvas()
    {
        // Bank row (shared state with the Map16 tab's picker).
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 7);
        ImGui.TextDisabled("bank");
        for (int b = 0; b < 8; b++)
        {
            ImGui.SameLine();
            if (b == map16Bank) ImGui.PushStyleColor(ImGuiCol.Button, 0xFF884400u);
            if (ImGui.SmallButton($"{b}##m16cbank") && map16Bank != b)
            { map16Bank = b; m16Sel = null; m16Lasso = null; m16Move = null; }   // selection is bank-relative
            if (b == map16Bank) ImGui.PopStyleColor();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(map16Bank == 0 ? "FG" : map16Bank == 2 ? "BG" : "");
        ImGui.SameLine();
        ImGui.TextDisabled($"selected 0x{app.selectedMap16:X4}   right-click: stamp 8x8   X/Y/P: flip/priority under cursor");

        if (ImGui.BeginChild("m16canvas", Vector2.Zero, 0, ImGuiWindowFlags.HorizontalScrollbar))
        {
            const float Z = 2f;                 // 16px tile → 32px on screen, 8x8 cell = 16px
            const int BankTiles = 0x2000, Cols = 16;
            float ts = 16 * Z, qs = 8 * Z;
            LevelViewport.DrawDeskBackdrop();

            app.SnapCursorToPixel();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
            var origin = ImGui.GetCursorScreenPos();
            var tex = map16Bank == 0 ? map16Texs[app.AnimPhase] ?? map16Texs[0]
                    : map16Bank == 2 ? map16BgTexs[app.AnimPhase] ?? map16BgTexs[0] : null;
            int realCount = map16Bank == 0 ? app.tileCaches?[0].Length ?? 0
                          : map16Bank == 2 && tex is not null ? 0x200 : 0;
            int texH = map16Bank == 0 ? map16H : map16BgH;
            if (tex is not null)
                ImGui.Image(app.imgui!.GetTextureID(tex), new Vector2(map16W * Z, texH * Z));
            int realRows = tex is not null ? texH / 16 : 0;
            int totalRows = BankTiles / Cols;
            var dl = ImGui.GetWindowDrawList();
            if (totalRows > realRows)
            {
                var pp0 = new Vector2(origin.X, origin.Y + realRows * ts);
                var pp1 = new Vector2(origin.X + Cols * ts, origin.Y + totalRows * ts);
                dl.AddRectFilled(pp0, pp1, 0xFF1C1C1Cu);
                for (int pg = (realRows + 15) / 16; pg < BankTiles / 0x100; pg++)
                {
                    float y = origin.Y + pg * 16 * ts;
                    dl.AddLine(new Vector2(pp0.X, y), new Vector2(pp1.X, y), 0xFF2A2A2Au);
                    dl.AddText(new Vector2(pp0.X + 6, y + 6), 0xFF585858u,
                               $"page {map16Bank * 0x20 + pg:X2} — {UnusedPageNote(map16Bank, map16Bank * 0x20 + pg)}");
                }
                ImGui.Dummy(new Vector2(Cols * ts, (totalRows - realRows) * ts));
            }
            // Page separators over the real region too, LM-style.
            for (int pg = 1; pg <= (realRows + 15) / 16; pg++)
                dl.AddLine(new Vector2(origin.X, origin.Y + pg * 16 * ts),
                           new Vector2(origin.X + Cols * ts, origin.Y + pg * 16 * ts), 0x30FFFFFFu);

            // Hover state in tile + quadrant space.
            var m = ImGui.GetMousePos();
            int col = (int)((m.X - origin.X) / ts), row = (int)((m.Y - origin.Y) / ts);
            int qcol = (int)((m.X - origin.X) / qs), qrow = (int)((m.Y - origin.Y) / qs);
            bool hovered = ImGui.IsWindowHovered() && col is >= 0 and < Cols && row >= 0 && row < totalRows;
            int hTile = map16Bank * BankTiles + row * Cols + col;
            bool hReal = hovered && row * Cols + col < realCount;
            int tileset = app.level!.Header.Tileset;

            // Right-click: stamp the drawer's 8x8 block at the hovered quadrant (drag
            // paints; one undo per stroke). No brush selected = nothing to stamp.
            if (hovered && m16BrushW > 0 && m16Move is null && m16Lasso is null)
            {
                var q0 = new Vector2(origin.X + qcol * qs, origin.Y + qrow * qs);
                dl.AddRect(q0, new Vector2(q0.X + m16BrushW * qs, q0.Y + m16BrushH * qs), 0xFF00FFFF, 0, 0, 1.5f);
                if (ImGui.IsMouseDown(ImGuiMouseButton.Right))
                    for (int j = 0; j < m16BrushH; j++)
                        for (int i = 0; i < m16BrushW; i++)
                        {
                            int qx = qcol + i, qy = qrow + j;
                            if (qx >= Cols * 2 || qy >= totalRows * 2) continue;
                            int cell = (qy >> 1) * Cols + (qx >> 1);
                            int tTile = map16Bank * BankTiles + cell;
                            ushort raw = (ushort)((m16BrushChr[j * m16BrushW + i] & 0x3FF) | (m16BrushPal << 10) |
                                                  (m16BrushP ? 0x2000 : 0) | (m16BrushFX ? 0x4000 : 0) | (m16BrushFY ? 0x8000 : 0));
                            int quad = ((qy & 1) << 1) | (qx & 1);
                            if (cell >= realCount)
                            {
                                // Painting an empty page CREATES it — allocation is a
                                // consequence of editing, not a separate thing to ask for.
                                // Deferred to frame start (it relocates the def region and
                                // rebuilds textures), carrying this stamp so the stroke that
                                // triggered it still lands.
                                if (CanAllocate(tTile) && map16AllocPending is null)
                                { map16AllocPending = tTile; map16AllocStamp = (tTile, quad, raw); }
                                continue;
                            }
                            if (Map16.DefFileOffset(app.rom!, tileset, tTile) < 0) continue;
                            StampDefWord(tTile, quad, raw);
                        }
            }

            // Left button: move the selection when grabbing inside it, else lasso tiles.
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                if (m16Sel is { } s && col >= s.x && col < s.x + s.w && row >= s.y && row < s.y + s.h)
                    m16Move = (col, row);
                else if (hReal) m16Lasso = (col, row);
                // An empty cell isn't selectable — painting it is what creates its page, so
                // there is no allocate-by-clicking to explain. Just say what it is.
                else app.saveStatus = $"Map16 page 0x{hTile >> 8:X2}: " +
                                      UnusedPageNote(map16Bank, hTile >> 8);
            }
            if (m16Lasso is { } la)
            {
                int bx = Math.Clamp(col, 0, Cols - 1), by = Math.Clamp(row, 0, totalRows - 1);
                int rx = Math.Min(la.x, bx), ry = Math.Min(la.y, by);
                int rw = Math.Abs(bx - la.x) + 1, rh = Math.Abs(by - la.y) + 1;
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    dl.AddRect(new Vector2(origin.X + rx * ts, origin.Y + ry * ts),
                               new Vector2(origin.X + (rx + rw) * ts, origin.Y + (ry + rh) * ts), 0xFF00FFFF, 0, 0, 1.5f);
                else
                {
                    m16Lasso = null;
                    m16Sel = rw == 1 && rh == 1 ? null : (rx, ry, rw, rh);
                    app.selectedMap16 = map16Bank * BankTiles + ry * Cols + rx;
                    if (rw == 1 && rh == 1 && app.selectedMap16 < 0x4000)   // plain click: arm level brush
                    { app.brushTiles = new[] { (ushort)app.selectedMap16 }; app.brushW = app.brushH = 1; app.selectedObjCat = -1; }
                }
            }
            if (m16Move is { } mv && m16Sel is { } sel)
            {
                int dxT = Math.Clamp(col - mv.x, -sel.x, Cols - sel.x - sel.w);
                int dyT = Math.Clamp(row - mv.y, -sel.y, totalRows - sel.y - sel.h);
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    dl.AddRect(new Vector2(origin.X + (sel.x + dxT) * ts, origin.Y + (sel.y + dyT) * ts),
                               new Vector2(origin.X + (sel.x + sel.w + dxT) * ts, origin.Y + (sel.y + sel.h + dyT) * ts),
                               0xFF00FF00, 0, 0, 1.5f);
                }
                else
                {
                    m16Move = null;
                    if (dxT != 0 || dyT != 0) MoveMap16Selection(sel, dxT, dyT);
                }
            }
            // Selection highlight.
            if (m16Sel is { } shl)
            {
                var s0 = new Vector2(origin.X + shl.x * ts, origin.Y + shl.y * ts);
                var s1 = new Vector2(origin.X + (shl.x + shl.w) * ts, origin.Y + (shl.y + shl.h) * ts);
                dl.AddRectFilled(s0, s1, 0x200080FFu);
                dl.AddRect(s0, s1, 0xFF0080FF, 0, 0, 2f);
            }
            else if (app.selectedMap16 / BankTiles == map16Bank)
            {
                int idx = app.selectedMap16 % BankTiles;
                var s0 = new Vector2(origin.X + (idx % Cols) * ts, origin.Y + (idx / Cols) * ts);
                dl.AddRect(s0, new Vector2(s0.X + ts, s0.Y + ts), 0xFF00FFFF, 0, 0, 2f);
            }

            // Quadrant hotkeys on the hovered cell (committed at next frame start).
            if (hovered && hReal && !ImGui.GetIO().WantTextInput && ReadDef(hTile) is { } wq)
            {
                int hQuad = ((qrow & 1) << 1) | (qcol & 1);
                if (ImGui.IsKeyPressed(ImGuiKey.X)) StampDefWord(hTile, hQuad, (ushort)(wq[hQuad].Raw ^ 0x4000));
                if (ImGui.IsKeyPressed(ImGuiKey.Y)) StampDefWord(hTile, hQuad, (ushort)(wq[hQuad].Raw ^ 0x8000));
                if (ImGui.IsKeyPressed(ImGuiKey.P)) StampDefWord(hTile, hQuad, (ushort)(wq[hQuad].Raw ^ 0x2000));
            }
            ImGui.EndChild();
        }
    }

    // Move a lassoed rect of Map16 tiles by a tile delta: defs are read out first, the
    // sources cleared to LM's default-empty, then rewritten at the destination — overlap-
    // safe, one undo step (the writes join one stroke committed at next frame start).
    private void MoveMap16Selection((int x, int y, int w, int h) sel, int dx, int dy)
    {
        if (app.rom is null || app.level is null) return;
        const int BankTiles = 0x2000, Cols = 16;
        int tileset = app.level.Header.Tileset;
        int TileAt(int tx, int ty) => map16Bank * BankTiles + ty * Cols + tx;
        // Every destination must have a backing def; refuse partial moves.
        for (int j = 0; j < sel.h; j++)
            for (int i = 0; i < sel.w; i++)
                if (Map16.DefFileOffset(app.rom, tileset, TileAt(sel.x + i + dx, sel.y + j + dy)) < 0)
                { app.saveStatus = "move target has unallocated tiles — allocate the page first."; return; }
        var src = new Map16.Word[sel.w * sel.h][];
        for (int j = 0; j < sel.h; j++)
            for (int i = 0; i < sel.w; i++)
                src[j * sel.w + i] = ReadDef(TileAt(sel.x + i, sel.y + j)) ?? new Map16.Word[4];
        for (int j = 0; j < sel.h; j++)                    // clear sources first (overlap-safe)
            for (int i = 0; i < sel.w; i++)
                for (int q = 0; q < 4; q++)
                    StampDefWord(TileAt(sel.x + i, sel.y + j), q, 0x1004);
        for (int j = 0; j < sel.h; j++)
            for (int i = 0; i < sel.w; i++)
                for (int q = 0; q < 4; q++)
                    StampDefWord(TileAt(sel.x + i + dx, sel.y + j + dy), q, src[j * sel.w + i][q].Raw);
        m16Sel = (sel.x + dx, sel.y + dy, sel.w, sel.h);
    }

    // ---- Map16 tile editing (defs written straight into the ROM copy, undoable) ----

    /// <summary>The selected tile's 4 def words in VISUAL order TL,TR,BL,BR (raw order is
    /// TL,BL,TR,BR), or null when the tile has no backing definition.</summary>
    private Map16.Word[]? ReadDef(int tile)
    {
        if (app.rom is null || app.level is null) return null;
        int fo = Map16.DefFileOffset(app.rom, app.level.Header.Tileset, tile);
        if (fo < 0) return null;
        Map16.Word W(int rawIdx) => new((ushort)(app.rom.Data[fo + rawIdx * 2] | (app.rom.Data[fo + rawIdx * 2 + 1] << 8)));
        return new[] { W(0), W(2), W(1), W(3) };
    }

    /// <summary>File offset of one quadrant word (visual TL,TR,BL,BR), or -1.</summary>
    private int DefWordFo(int tile, int visualQuad)
    {
        if (app.rom is null || app.level is null) return -1;
        int fo = Map16.DefFileOffset(app.rom, app.level.Header.Tileset, tile);
        return fo < 0 ? -1 : fo + new[] { 0, 2, 1, 3 }[visualQuad] * 2;   // raw order TL,BL,TR,BR
    }

    // Stroke-buffered quadrant write: bytes land in the ROM immediately (so later stamps in
    // the same stroke read the new state), rebuild + undo are deferred to CommitM16Stroke.
    private void StampDefWord(int tile, int visualQuad, ushort raw)
    {
        int fo = DefWordFo(tile, visualQuad);
        if (fo < 0 || app.rom is null) return;
        ushort before = (ushort)(app.rom.Data[fo] | (app.rom.Data[fo + 1] << 8));
        if (before == raw) return;
        app.rom.Data[fo] = (byte)raw; app.rom.Data[fo + 1] = (byte)(raw >> 8);
        m16Stroke.Add((fo, before, raw));
        CaptureDefSlot(tile);
    }

    // Project capture: record the touched def slot key; the autosave sync re-reads the
    // slot's current 8 bytes from the ROM, so undo/redo/allocation-relocation need no
    // extra bookkeeping. Extended FG tiles (0x200+) key by tile number (their region
    // relocates on page allocation); vanilla FG + BG slots key by the def's SNES address
    // (canonical across tilesets — tiles < 0x200 alias shared/per-tileset regions).
    private void CaptureDefSlot(int tile)
    {
        if (app.project is null || app.rom is null || app.level is null) return;
        if (tile is >= 0x200 and < 0x4000)
            app.project.Data.Map16.Ext.TryAdd(tile.ToString("X3"), "");
        else
        {
            int baseFo = Map16.DefFileOffset(app.rom, app.level.Header.Tileset, tile);
            if (baseFo < 0) return;
            app.project.Data.Map16.Slots.TryAdd(
                Rom.PcToSnes(baseFo - app.rom.HeaderOffset).ToString("X6"), "");
        }
        app.project.MarkDirty();
    }

    private void CommitM16Stroke()
    {
        if (m16Stroke.Count == 0) return;
        var edits = m16Stroke.ToArray();
        m16Stroke.Clear();
        var r = app.rom!;
        void Apply(bool redo)
        {
            // Undo walks backward, for the reason spelled out on GfxEditor.ApplyStroke: a
            // stroke records one entry per WRITE, so a repeated offset would otherwise be
            // restored to an intermediate value. StampDefWord skips same-value rewrites, so
            // repeats need the value at one offset to change mid-stroke — reversing costs
            // nothing when offsets are distinct and removes the hazard either way.
            for (int i = 0; i < edits.Length; i++)
            {
                var (fo, before, after) = edits[redo ? i : edits.Length - 1 - i];
                ushort v = redo ? after : before;
                r.Data[fo] = (byte)v; r.Data[fo + 1] = (byte)(v >> 8);
            }
            app.session.RebuildGraphics();
            app.levelDirty = true;
        }
        Apply(true);
        app.history.Push(() => Apply(false), () => Apply(true));
    }


    // The 8x8 picker sheet: the level's loaded VRAM tiles (0x000-0x3FF) under one palette
    // row, recomposed when the palette row / animation phase / graphics change.
    private void EnsureChrSheet(int pal)
    {
        if (m16ChrTex is not null && m16ChrPal == pal && m16ChrPhase == app.AnimPhase) return;
        if (app.rom is null || app.level is null) return;
        var fg = Gfx.FgTiles.Load(app.rom, app.level.Header.Tileset, app.levelNum, app.AnimPhase);
        var palette = app.paletteEditor.EditedPalette(app.AnimPhase)!;
        var px = new uint[128 * 512];
        for (int t = 0; t < 0x400; t++)
        {
            var src = fg.Fetch(t);
            int ox = (t % 16) * 8, oy = (t / 16) * 8;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int idx = src[y * 8 + x];
                    px[(oy + y) * 128 + (ox + x)] = idx == 0 ? 0xFF303030u : palette.Rgba[pal * 16 + idx];
                }
        }
        m16ChrTex?.Dispose();
        m16ChrTex = new Texture(app.GraphicsDevice, 128, 512, MemoryMarshal.AsBytes(px.AsSpan()));
        m16ChrPal = pal; m16ChrPhase = app.AnimPhase;
    }

    // ---- Map16 right drawer: properties of the selected tile(s) ----

    /// <summary>Tiles covered by the canvas selection (the lasso rect, else the single
    /// selected tile).</summary>
    private IEnumerable<int> SelectedM16Tiles()
    {
        if (m16Sel is { } s)
            for (int j = 0; j < s.h; j++)
                for (int i = 0; i < s.w; i++)
                    yield return map16Bank * 0x2000 + (s.y + j) * 16 + s.x + i;
        else yield return app.selectedMap16;
    }

    // Apply a per-quadrant word transform to every selected tile (skipping unbacked ones);
    // the writes join one stroke → one undo entry, rebuilt at next frame start.
    private void TransformSelectedM16(Func<Map16.Word, ushort> f)
    {
        foreach (int t in SelectedM16Tiles())
        {
            if (ReadDef(t) is not { } w) continue;
            for (int q = 0; q < 4; q++) StampDefWord(t, q, f(w[q]));
        }
    }

    // Mirror each selected tile in place: swap quadrant pairs + toggle the flip flag.
    private void FlipSelectedM16(bool vertical)
    {
        foreach (int t in SelectedM16Tiles())
        {
            if (ReadDef(t) is not { } w) continue;      // visual order TL,TR,BL,BR
            int flag = vertical ? 0x8000 : 0x4000;
            int[] src = vertical ? [2, 3, 0, 1] : [1, 0, 3, 2];
            for (int q = 0; q < 4; q++) StampDefWord(t, q, (ushort)(w[src[q]].Raw ^ flag));
        }
    }

    private void SetActsForSelection(int val)
    {
        if (app.rom is null || app.rom.LmActsAsBase <= 0) return;
        var edits = new List<(int fo, int before)>();
        foreach (int t in SelectedM16Tiles())
        {
            if (t >= 0x4000) continue;                  // acts-like is an FG concept
            int fo = app.rom.FileOffset(app.rom.LmActsAsBase + t * 2);
            int b = app.rom.Data[fo] | (app.rom.Data[fo + 1] << 8);
            if (b == val) continue;
            edits.Add((fo, b));
            // Project capture: value refreshed from the ROM at autosave (undo-proof).
            if (app.project is not null)
            { app.project.Data.Map16.ActsAs.TryAdd(t.ToString("X3"), val); app.project.MarkDirty(); }
        }
        if (edits.Count == 0) return;
        var r = app.rom;
        void Apply(bool redo)
        {
            foreach (var (fo, b) in edits)
            { int v = redo ? val : b; r.Data[fo] = (byte)v; r.Data[fo + 1] = (byte)(v >> 8); }
            app.levelDirty = true;
        }
        Apply(true);
        app.history.Push(() => Apply(false), () => Apply(true));
    }

    internal void DrawMap16PropsDrawer()
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 7);
        ImGui.TextDisabled(m16Sel is { } s ? $"{s.w}x{s.h} tiles selected"
                                           : $"tile 0x{app.selectedMap16:X4}");
        ImGui.Separator();
        var first = ReadDef(SelectedM16Tiles().First());
        if (first is null) { ImGui.TextDisabled("(unallocated)"); return; }

        // Acts As — LM's behavior remap for the selected tile(s).
        if (app.rom is not null && app.rom.LmActsAsBase > 0 && app.selectedMap16 < 0x4000)
        {
            ImGui.Text("Act As");
            ImGui.SameLine();
            string abuf = app.rom.ActsAs(SelectedM16Tiles().First()).ToString("X3");
            ImGui.SetNextItemWidth(52);
            ImGui.InputText("##acts", ref abuf, 4, ImGuiInputTextFlags.CharsHexadecimal);
            if (ImGui.IsItemDeactivatedAfterEdit() &&
                int.TryParse(abuf, System.Globalization.NumberStyles.HexNumber, null, out int av))
                SetActsForSelection(av & 0x3FFF);
        }
        else ImGui.TextDisabled(app.selectedMap16 >= 0x4000 ? "Act As: n/a (BG)" : "Act As: no LM acts table");

        // Priority — checkbox reflects the first tile; toggling applies to everything.
        bool prio = first[0].Priority;
        if (ImGui.Checkbox("Priority", ref prio))
            TransformSelectedM16(w => (ushort)(prio ? w.Raw | 0x2000 : w.Raw & ~0x2000));

        // Flips — actions: mirror the tile(s) in place.
        if (ImGui.Button("Flip X")) FlipSelectedM16(vertical: false);
        ImGui.SameLine();
        if (ImGui.Button("Flip Y")) FlipSelectedM16(vertical: true);

        // Palette — applies the row to all quadrants of the selection.
        ImGui.Text("Palette");
        ImGui.SameLine();
        int pal = first[0].Palette;
        ImGui.SetNextItemWidth(44);
        if (ImGui.Combo("##selpal", ref pal, ["0", "1", "2", "3", "4", "5", "6", "7"], 8))
            TransformSelectedM16(w => (ushort)((w.Raw & ~0x1C00) | (pal << 10)));
    }

    // The left drawer while the canvas shows the Map16 editor: stamp props (palette row,
    // flips, priority) and the 8x8 GFX palette grid.
    internal void DrawGfxPaletteDrawer()
    {
        ImGui.Text(m16BrushW == 0 ? "8x8: none"
                 : m16BrushW == 1 && m16BrushH == 1 ? $"8x8: 0x{m16BrushChr[0]:X3}"
                 : $"8x8: {m16BrushW}x{m16BrushH} block");
        ImGui.SameLine();
        ImGui.TextDisabled($"tile: 0x{app.selectedMap16:X4}");
        ImGui.SetNextItemWidth(36);
        ImGui.Combo("##m16pal", ref m16BrushPal, ["0", "1", "2", "3", "4", "5", "6", "7"], 8);
        ImGui.SameLine(); ImGui.Checkbox("X##bfx", ref m16BrushFX);
        ImGui.SameLine(); ImGui.Checkbox("Y##bfy", ref m16BrushFY);
        ImGui.SameLine(); ImGui.Checkbox("P##bp", ref m16BrushP);
        EnsureChrSheet(m16BrushPal);
        if (m16ChrTex is null) { ImGui.TextDisabled("No level."); return; }
        ImGui.PushStyleColor(ImGuiCol.Border, 0xFF404040u);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.BeginChild("m16chr", Vector2.Zero, ImGuiChildFlags.Border))
        {
            app.SnapCursorToPixel();
            var corigin = ImGui.GetCursorScreenPos();
            ImGui.Image(app.imgui!.GetTextureID(m16ChrTex), new Vector2(256, 1024));   // 2x zoom
            var m = ImGui.GetMousePos();
            int cx = (int)((m.X - corigin.X) / 16), cy = (int)((m.Y - corigin.Y) / 16);
            bool inSheet = cx is >= 0 and < 16 && cy is >= 0 and < 64;
            // Lasso-pick: drag selects a WxH block of 8x8s; a plain click is a 1x1 block.
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && inSheet)
                m16ChrDrag = (cx, cy);
            var dl = ImGui.GetWindowDrawList();
            if (m16ChrDrag is { } a)
            {
                int bx = Math.Clamp(cx, 0, 15), by = Math.Clamp(cy, 0, 63);
                int rx = Math.Min(a.x, bx), ry = Math.Min(a.y, by);
                int rw = Math.Abs(bx - a.x) + 1, rh = Math.Abs(by - a.y) + 1;
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    dl.AddRect(new Vector2(corigin.X + rx * 16, corigin.Y + ry * 16),
                               new Vector2(corigin.X + (rx + rw) * 16, corigin.Y + (ry + rh) * 16), 0xFF00FFFF, 0, 0, 1.5f);
                else
                {
                    m16BrushW = rw; m16BrushH = rh;
                    m16BrushChr = new int[rw * rh];
                    for (int j = 0; j < rh; j++)
                        for (int i = 0; i < rw; i++)
                            m16BrushChr[j * rw + i] = (ry + j) * 16 + rx + i;
                    m16ChrDrag = null;
                }
            }
            else if (m16BrushW > 0)
            {
                // Selection ring on the picked block.
                int t0 = m16BrushChr[0];
                var cs0 = new Vector2(corigin.X + (t0 % 16) * 16, corigin.Y + (t0 / 16) * 16);
                dl.AddRect(cs0, new Vector2(cs0.X + m16BrushW * 16, cs0.Y + m16BrushH * 16), 0xFF00FFFF, 0, 0, 2f);
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    // Map16 palette tab, LM-style: 8 banks of 0x2000 tiles. Bank 0 holds the FG defs
    // (vanilla + LM extended, up to the allocated bound), bank 2 starts with the BG pages
    // (unified tile numbers 0x4000+, matching the DM16 BG form's +0x40 page). Space past
    // the allocated tiles renders as flat "unused page" padding, like LM's editor.
    internal void DrawMap16Tab()
    {
        if (map16Texs[0] is null) { ImGui.TextDisabled("No level."); return; }
        ImGui.Text($"Selected: 0x{app.selectedMap16:X4}");
        ImGui.SameLine();
        ImGui.TextDisabled("bank");
        for (int b = 0; b < 8; b++)
        {
            ImGui.SameLine();
            if (b == map16Bank) ImGui.PushStyleColor(ImGuiCol.Button, 0xFF884400u);
            if (ImGui.SmallButton($"{b}##m16bank") && map16Bank != b)
            { map16Bank = b; m16Sel = null; m16Lasso = null; m16Move = null; }   // selection is bank-relative
            if (b == map16Bank) ImGui.PopStyleColor();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(map16Bank == 0 ? "FG" : map16Bank == 2 ? "BG" : "");
        ImGui.PushStyleColor(ImGuiCol.Border, 0xFF404040u);              // 1px dark grey frame
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);   // tiles flush to the border
        if (ImGui.BeginChild("m16sheet", System.Numerics.Vector2.Zero, ImGuiChildFlags.Border,
                             ImGuiWindowFlags.HorizontalScrollbar))
        {
            app.SnapCursorToPixel();
            var origin = ImGui.GetCursorScreenPos();
            float pz = app.SnappedZoom(Map16Zoom);
            float ts = 16 * pz;
            const int BankTiles = 0x2000, Cols = 16;
            // Real (allocated) tiles in this bank + their sheet texture.
            var tex = map16Bank == 0 ? map16Texs[app.AnimPhase] ?? map16Texs[0]
                    : map16Bank == 2 ? map16BgTexs[app.AnimPhase] ?? map16BgTexs[0] : null;
            int realCount = map16Bank == 0 ? app.tileCaches?[0].Length ?? 0
                          : map16Bank == 2 && tex is not null ? 0x200 : 0;
            int texH = map16Bank == 0 ? map16H : map16BgH;
            if (tex is not null)
                ImGui.Image(app.imgui!.GetTextureID(tex), new Vector2(map16W * pz, texH * pz));
            // Padding: flat "unused" pages for the rest of the bank, with page markers.
            int realRows = tex is not null ? texH / 16 : 0;
            int totalRows = BankTiles / Cols;
            var dl = ImGui.GetWindowDrawList();
            if (totalRows > realRows)
            {
                var p0 = new Vector2(origin.X, origin.Y + realRows * ts);
                var p1 = new Vector2(origin.X + Cols * ts, origin.Y + totalRows * ts);
                dl.AddRectFilled(p0, p1, 0xFF242424u);
                for (int pg = (realRows + 15) / 16; pg < BankTiles / 0x100; pg++)
                {
                    float y = origin.Y + pg * 16 * ts;
                    dl.AddLine(new Vector2(p0.X, y), new Vector2(p1.X, y), 0xFF303030u);
                    dl.AddText(new Vector2(p0.X + 4, y + 4), 0xFF585858u,
                               $"page {map16Bank * 0x20 + pg:X2} — {UnusedPageNote(map16Bank, map16Bank * 0x20 + pg)}");
                }
                ImGui.Dummy(new Vector2(Cols * ts, (totalRows - realRows) * ts));
            }
            // Pick: unified tile number = bank*0x2000 + cell (allocated tiles only).
            if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                var m = ImGui.GetMousePos();
                int col = (int)((m.X - origin.X) / ts), row = (int)((m.Y - origin.Y) / ts);
                int idx = row * Cols + col;
                if (col is >= 0 and < Cols && idx >= 0 && idx < realCount)
                {
                    app.selectedMap16 = map16Bank * BankTiles + idx;
                    app.brushTiles = new[] { (ushort)app.selectedMap16 };   // palette pick = 1x1 brush
                    app.brushW = app.brushH = 1;
                    app.selectedObjCat = -1;               // brush armed: right-click stamps tiles
                }
                else if (idx >= realCount && idx < BankTiles)
                {
                    // This is a PICKER — there is nothing to paint here, so an empty page just
                    // says what it is. Pages come into existence by painting them in the Map16
                    // canvas, not by being clicked in a list.
                    int page = (map16Bank * BankTiles + idx) >> 8;
                    app.saveStatus = $"Map16 page 0x{page:X2}: " + UnusedPageNote(map16Bank, page);
                }
            }
            // Selection ring (when the selected tile lives in this bank).
            if (app.selectedMap16 / BankTiles == map16Bank)
            {
                int idx = app.selectedMap16 % BankTiles;
                var stl = new Vector2(origin.X + (idx % Cols) * ts, origin.Y + (idx / Cols) * ts);
                dl.AddRect(stl, new Vector2(stl.X + ts, stl.Y + ts), 0xFF00FFFF, 0, 0, 2f);
            }
            ImGui.EndChild();
        }
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    // ROM inspector, reachable from File → ROM Info.
    internal void BuildMap16Sheet()
    {
        for (int p = 0; p < 4; p++)
        {
            map16Texs[p]?.Dispose(); map16Texs[p] = null;
            map16BgTexs[p]?.Dispose(); map16BgTexs[p] = null;
        }
        if (app.tileCaches is null) return;
        try
        {
            for (int p = 0; p < 4; p++)
            {
                var (px, w, h) = Map16.ComposeSheet(app.tileCaches[p]);
                map16Texs[p] = new Texture(app.GraphicsDevice, w, h, MemoryMarshal.AsBytes(px.AsSpan()));
                map16W = w; map16H = h;
                if (app.bgCaches is null) continue;
                var (bpx, bw, bh) = Map16.ComposeSheet(app.bgCaches[p]);
                map16BgTexs[p] = new Texture(app.GraphicsDevice, bw, bh, MemoryMarshal.AsBytes(bpx.AsSpan()));
                map16BgW = bw; map16BgH = bh;
            }
        }
        catch
        {
            for (int p = 0; p < 4; p++)
            {
                map16Texs[p]?.Dispose(); map16Texs[p] = null;
                map16BgTexs[p]?.Dispose(); map16BgTexs[p] = null;
            }
        }
    }
}
