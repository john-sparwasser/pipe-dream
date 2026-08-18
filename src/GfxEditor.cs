using System.Numerics;
using System.Runtime.InteropServices;
using Foster.Framework;
using ImGuiNET;

namespace PipeDream;

// ---- GFX edit mode (canvas view toggle: the canvas becomes an 8x8 tile sheet editable
// per pixel, the left drawer becomes the color picker + the level's bin quick-list) ----
// Tools: pencil (drag-paints) + fill (4-connected flood within one 8x8 tile); right-click
// always eyedrops. Edits write through Rom.ImportedGfx — stock ROM files fork on first
// touch (copy-on-write under the SAME id, so every consumer sees the edit), and the
// import plumbing inherits persistence/build for free. Strokes batch into one undo entry
// committed at frame start (Map16Editor's stroke grammar).
internal sealed class GfxEditor(EditorApp app)
{
    private readonly EditorApp app = app;

    internal int gfxFile = 0x14;                   // current file id (0x000-0xFFF)
    internal int palRow = 2;                       // CGRAM row coloring the sheet
    internal int selectedColor = 1;                // paint color index (0 = transparent)
    internal enum Tool { Pencil, Fill }
    internal Tool tool = Tool.Pencil;
    private static readonly float[] Zooms = { 4f, 8f, 16f };
    private int zoomIdx = 1;

    // A paint stroke: write-through byte edits batched into ONE undo entry and ONE
    // consumer recompose on release (per-pixel rebuilds would make dragging sluggish).
    // strokeFile pins the id the buffered offsets belong to.
    internal readonly List<(int off, byte before, byte after)> stroke = new();
    private int strokeFile;

    // The sheet surface: raw file bytes rendered through TileSheet, SetData'd in place for
    // live paint feedback; disposed+recreated ONLY when the sheet size changes (different
    // file length). Identity = (file, palette row, byte-array reference) + a stale flag
    // RebuildGraphics sets (covers undo/redo, palette edits, and re-imports).
    private Texture? sheetTex;
    private byte[]? sheetBytes;
    private int sheetFile = -1, sheetPalRow = -1;
    private bool sheetStale;

    /// <summary>External bytes/palette change (RebuildGraphics) — recompose the sheet on
    /// the next draw.</summary>
    internal void InvalidateSheet() => sheetStale = true;

    // ---- pure pixel/stroke logic (static: testable without a GraphicsDevice) ----

    /// <summary>One pixel write with stroke capture: record each plane byte that actually
    /// changed as (offset, before, after). A same-color write records nothing.</summary>
    internal static void WritePixel(byte[] gfx, int tileOff, int bpp, int x, int y, int color,
                                    List<(int off, byte before, byte after)> stroke)
    {
        int tb = Gfx.TileBytes(bpp);
        if (tileOff < 0 || tileOff + tb > gfx.Length) return;
        Span<int> offs = stackalloc int[4];
        offs[0] = tileOff + y * 2; offs[1] = tileOff + y * 2 + 1;
        int n = 2;
        if (bpp == 3) offs[n++] = tileOff + 16 + y;
        else if (bpp == 4) { offs[n++] = tileOff + 16 + y * 2; offs[n++] = tileOff + 16 + y * 2 + 1; }
        Span<byte> before = stackalloc byte[4];
        for (int i = 0; i < n; i++) before[i] = gfx[offs[i]];
        Gfx.SetTilePixel(gfx, tileOff, bpp, x, y, color);
        for (int i = 0; i < n; i++)
            if (gfx[offs[i]] != before[i]) stroke.Add((offs[i], before[i], gfx[offs[i]]));
    }

    /// <summary>4-connected flood fill WITHIN the 8x8 tile containing sheet pixel (px,py):
    /// replaces the clicked pixel's color region with <paramref name="color"/>. No-op when
    /// the target already is the color. Sheet layout = 16 tiles per row.</summary>
    internal static void FillTile(byte[] gfx, int bpp, int px, int py, int color,
                                  List<(int off, byte before, byte after)> stroke)
    {
        int tb = Gfx.TileBytes(bpp);
        int tileOff = ((py / 8) * 16 + px / 8) * tb;
        if (tileOff < 0 || tileOff + tb > gfx.Length) return;
        var idx = Gfx.DecodeTile(gfx, tileOff, bpp);
        int sx = px & 7, sy = py & 7;
        byte target = idx[sy * 8 + sx];
        if (target == color) return;
        var work = new Stack<(int x, int y)>();
        work.Push((sx, sy));
        while (work.Count > 0)
        {
            var (x, y) = work.Pop();
            if (x is < 0 or > 7 || y is < 0 or > 7 || idx[y * 8 + x] != target) continue;
            idx[y * 8 + x] = (byte)color;
            WritePixel(gfx, tileOff, bpp, x, y, color, stroke);
            work.Push((x + 1, y)); work.Push((x - 1, y)); work.Push((x, y + 1)); work.Push((x, y - 1));
        }
    }

    /// <summary>The editable byte array for a file: the existing import, or a copy-on-write
    /// fork of the stock bytes keyed under the SAME id (shadowing the ROM file for every
    /// consumer — deliberately opposite of Import's new-id allocation). Null when the id
    /// resolves nowhere.</summary>
    internal static byte[]? EditableBytes(Rom rom, int file, out bool forked)
    {
        forked = false;
        if (rom.ImportedGfx.TryGetValue(file, out var b)) return b;
        if (Gfx.Cached(rom, file) is not { } stock) return null;
        var fork = (byte[])stock.Clone();
        rom.ImportedGfx[file] = fork;
        Gfx.InvalidateCache(rom);           // consumers re-resolve through the import
        forked = true;
        return fork;
    }

    /// <summary>Replay stroke bytes into the file's CURRENT array — re-looked-up by id and
    /// bounds-checked, so a re-import that replaced (or removed) the array can't crash or
    /// corrupt a replay.
    ///
    /// UNDO WALKS BACKWARD. A stroke records one entry per byte WRITE, not per byte, and a
    /// single plane byte carries 8 pixels of a tile row — so painting along a row (or any
    /// fill) rewrites the same offset repeatedly: (off,A,B), (off,B,C), (off,C,D). Restoring
    /// those front-to-back ends on C, the second-to-last value, leaving most of the stroke
    /// painted. Last-to-first unwinds D→C→B→A and lands on the original. Redo is order-
    /// independent for a given offset (the last write wins either way) but stays forward so
    /// it mirrors the paint order. AbortStroke already reverses for the same reason.</summary>
    internal static void ApplyStroke(Rom rom, int file, (int off, byte before, byte after)[] edits, bool redo)
    {
        if (!rom.ImportedGfx.TryGetValue(file, out var g)) return;
        if (redo)
            foreach (var (off, _, after) in edits)
                { if (off >= 0 && off < g.Length) g[off] = after; }
        else
            for (int i = edits.Length - 1; i >= 0; i--)
            {
                var (off, before, _) = edits[i];
                if (off >= 0 && off < g.Length) g[off] = before;
            }
    }

    // ---- stroke lifecycle ----

    // GFX paint strokes commit when the left button is up — at frame start, so the
    // consumer recompose never disposes textures already submitted to this frame.
    internal void CommitStrokeOnRelease()
    {
        if (stroke.Count == 0 || ImGui.IsMouseDown(ImGuiMouseButton.Left) || app.rom is null) return;
        var edits = stroke.ToArray();
        stroke.Clear();
        int file = strokeFile;
        void Apply(bool redo)
        {
            ApplyStroke(app.rom!, file, edits, redo);
            app.session.RebuildGraphics();   // recompose consumers (also invalidates our sheet)
            app.gfxBrowser.Invalidate();     // the browser's thumbnail of this file is now stale
            app.levelDirty = true;
        }
        // Bytes already live in the array (write-through) — Apply(true) is idempotent and
        // triggers the deferred recompose. GFX bytes are GLOBAL like Map16 defs: the Push
        // marks the project dirty (history.Changed), never currentLevelTouched.
        Apply(true);
        app.history.Push(() => Apply(false), () => Apply(true));
    }

    /// <summary>Mode/file switch with a stroke still buffered: an uncommitted write-through
    /// stroke must not silently survive as un-undoable bytes — REVERT the buffered
    /// before-bytes (the stroke never happened) rather than committing it.</summary>
    internal void AbortStroke()
    {
        if (stroke.Count == 0 || app.rom is null) return;
        if (app.rom.ImportedGfx.TryGetValue(strokeFile, out var g))
            for (int i = stroke.Count - 1; i >= 0; i--)
                if (stroke[i].off < g.Length) g[stroke[i].off] = stroke[i].before;
        stroke.Clear();
        sheetStale = true;
    }

    // Copy-on-write on first touch, with the status note announcing the fork.
    private byte[]? EnsureForked()
    {
        var g = EditableBytes(app.rom!, gfxFile, out bool forked);
        if (forked)
            app.saveStatus = $"GFX{gfxFile:X3} forked into the project — edits shadow the stock file everywhere";
        return g;
    }

    // ---- the edit canvas ----

    internal void DrawCanvas()
    {
        if (app.rom is null || app.imgui is null) return;
        var io = ImGui.GetIO();

        // Header row: file id + badge, zoom, tool, hint.
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 7);
        ImGui.SetNextItemWidth(64);
        int file = gfxFile;
        if (ImGuiCompat.HexInput("GFX##gfxfile", ref file, 0xFFF, "%03X") && file != gfxFile)
        { AbortStroke(); gfxFile = file; }
        var bytes = Gfx.Cached(app.rom, gfxFile);
        ImGui.SameLine();
        ImGui.TextDisabled(app.rom.ImportedGfx.ContainsKey(gfxFile) ? "(imported)"
                           : bytes is not null ? "(stock)" : "");
        ImGui.SameLine();
        if (ImGui.SmallButton("-##gfxz") || (!io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.LeftBracket)))
            zoomIdx = Math.Max(0, zoomIdx - 1);
        ImGui.SameLine();
        if (ImGui.SmallButton("+##gfxz") || (!io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.RightBracket)))
            zoomIdx = Math.Min(Zooms.Length - 1, zoomIdx + 1);
        if (!io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.F))
            tool = tool == Tool.Pencil ? Tool.Fill : Tool.Pencil;
        foreach (var t in new[] { Tool.Pencil, Tool.Fill })
        {
            ImGui.SameLine();
            if (t == tool) ImGui.PushStyleColor(ImGuiCol.Button, 0xFF884400u);
            if (ImGui.SmallButton($"{t}##gfxtool")) tool = t;
            if (t == tool) ImGui.PopStyleColor();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("left: paint   right: pick color   F: tool   [ ]: zoom");

        int bpp = Gfx.RomBpp(app.rom), tb = Gfx.TileBytes(bpp);
        if (bytes is null || bytes.Length < tb)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 7);
            ImGui.TextDisabled("no such GFX file — import one or pick a bin");
            return;
        }
        int tiles = bytes.Length / tb, rows = (tiles + 15) / 16;
        int w = 128, h = rows * 8;
        EnsureSheet(bytes, bpp);
        if (sheetTex is null) return;

        if (ImGui.BeginChild("gfxcanvas", Vector2.Zero, 0, ImGuiWindowFlags.HorizontalScrollbar))
        {
            LevelViewport.DrawDeskBackdrop();
            float z = app.SnappedZoom(Zooms[zoomIdx]);
            app.SnapCursorToPixel();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
            var origin = ImGui.GetCursorScreenPos();
            ImGui.Image(app.imgui.GetTextureID(sheetTex), new Vector2(w * z, h * z));
            var dl = ImGui.GetWindowDrawList();

            // Grid overlays: 8x8 tile grid always, per-pixel grid once zoomed in enough.
            for (int gx = 0; gx <= w; gx += Zooms[zoomIdx] >= 8 ? 1 : 8)
                dl.AddLine(new Vector2(origin.X + gx * z, origin.Y),
                           new Vector2(origin.X + gx * z, origin.Y + h * z),
                           (gx & 7) == 0 ? 0x50FFFFFFu : 0x18FFFFFFu);
            for (int gy = 0; gy <= h; gy += Zooms[zoomIdx] >= 8 ? 1 : 8)
                dl.AddLine(new Vector2(origin.X, origin.Y + gy * z),
                           new Vector2(origin.X + w * z, origin.Y + gy * z),
                           (gy & 7) == 0 ? 0x50FFFFFFu : 0x18FFFFFFu);

            // Hover state in sheet-pixel space; tile = row-major 16 tiles per row.
            var m = ImGui.GetMousePos();
            int hx = (int)((m.X - origin.X) / z), hy = (int)((m.Y - origin.Y) / z);
            bool hovered = ImGui.IsWindowHovered() && m.X >= origin.X && m.Y >= origin.Y
                           && hx < w && hy < h && (hy / 8) * 16 + hx / 8 < tiles;
            if (hovered)
            {
                var c0 = new Vector2(origin.X + hx * z, origin.Y + hy * z);
                dl.AddRect(c0, new Vector2(c0.X + z, c0.Y + z), 0xFF00FFFFu);
                var t0 = new Vector2(origin.X + (hx & ~7) * z, origin.Y + (hy & ~7) * z);
                dl.AddRect(t0, new Vector2(t0.X + 8 * z, t0.Y + 8 * z), 0x8000FFFFu);

                // Right-click: eyedrop the hovered pixel's color index.
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    selectedColor = Gfx.DecodeTile(bytes, ((hy / 8) * 16 + hx / 8) * tb, bpp)[(hy & 7) * 8 + (hx & 7)];

                // Left: pencil drag-paints, fill floods once per click.
                bool paint = tool == Tool.Pencil ? ImGui.IsMouseDown(ImGuiMouseButton.Left)
                                                 : ImGui.IsMouseClicked(ImGuiMouseButton.Left);
                if (paint && EnsureForked() is { } g)
                {
                    if (stroke.Count == 0) strokeFile = gfxFile;
                    int n0 = stroke.Count;
                    if (tool == Tool.Pencil)
                        WritePixel(g, ((hy / 8) * 16 + hx / 8) * tb, bpp, hx & 7, hy & 7, selectedColor, stroke);
                    else
                        FillTile(g, bpp, hx, hy, selectedColor, stroke);
                    if (stroke.Count != n0) RefreshSheet(g, bpp);   // live feedback (SetData)
                }
            }
            ImGui.EndChild();
        }
    }

    // ---- the left drawer: color picker + level bin quick-list ----

    private static readonly string[] RowNames =
        Enumerable.Range(0, 16).Select(i => $"row {i}").ToArray();

    internal void DrawDrawer()
    {
        if (app.rom is null || app.level is null) { ImGui.TextDisabled("No level."); return; }
        ImGui.Text($"GFX{gfxFile:X3}");
        if (app.rom.GfxName(gfxFile) is { Length: > 0 } fname)
        { ImGui.SameLine(); ImGui.TextDisabled($"\"{fname}\""); }
        ImGui.SameLine();
        if (ImGui.SmallButton("Browse…"))
            app.gfxBrowser.Open("Open GFX in the tile editor", picked =>
            { AbortStroke(); gfxFile = picked; });
        ImGui.SameLine();
        ImGui.TextDisabled($"color {selectedColor}");
        ImGui.SetNextItemWidth(80);
        ImGui.Combo("##gfxpalrow", ref palRow, RowNames, 16);
        if (app.paletteEditor.EditedPalette(0) is not { } pal) return;

        // The row's 16 colors as paint swatches; index 0 keeps the sheet's grey convention.
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2, 2));
        for (int i = 0; i < 16; i++)
        {
            if (i != 0) ImGui.SameLine();
            uint c = i == 0 ? 0xFF303030u : pal.Rgba[palRow * 16 + i];
            var col = new Vector4((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, 1f);
            if (ImGui.ColorButton($"##gfxcol{i}", col,
                    ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoTooltip, new Vector2(17, 17)))
                selectedColor = i;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(i == 0 ? "color 0 — transparent in-game"
                                        : $"color {i}  (CGRAM 0x{palRow * 16 + i:X2})");
            if (i == selectedColor)
                ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                                                  0xFF00FFFFu, 0, 0, 2f);
        }
        ImGui.PopStyleVar();

        // The level's 10 VRAM bins as jump buttons (same resolution as the GFX tab).
        ImGui.Separator();
        ImGui.TextDisabled("level bins");
        var bins = LevelGfxPanel.ResolveSlots(app.rom, app.level, app.levelNum);
        for (int i = 0; i < bins.Length; i++)
        {
            var s = bins[i];
            if ((i & 1) != 0) ImGui.SameLine(110);
            bool cur = s.File == gfxFile;
            if (cur) ImGui.PushStyleColor(ImGuiCol.Button, 0xFF884400u);
            if (ImGui.SmallButton($"{s.Name} {s.File:X3}##gjump{i}") && !cur)
            {
                AbortStroke();
                gfxFile = s.File;
                palRow = s.PalRow;   // color through the bin's natural preview row
            }
            if (cur) ImGui.PopStyleColor();
        }
    }

    // ---- sheet surface ----

    // Recompose + upload the sheet. SetData when the size is unchanged (live paint, palette
    // row change); dispose+recreate ONLY when the file length differs — and always before
    // this frame's ImGui.Image bind, so a disposed texture is never in this frame's draw data.
    private void RefreshSheet(byte[] bytes, int bpp)
    {
        if (app.paletteEditor.EditedPalette(0) is not { } pal) return;
        var (px, w, h) = Gfx.TileSheet(bytes, bpp, pal, palRow);
        if (sheetTex is { } t && t.Width == w && t.Height == h) t.SetData<uint>(px);
        else
        {
            sheetTex?.Dispose();
            sheetTex = new Texture(app.GraphicsDevice, w, h, MemoryMarshal.AsBytes(px.AsSpan()));
        }
        sheetBytes = bytes; sheetFile = gfxFile; sheetPalRow = palRow; sheetStale = false;
    }

    private void EnsureSheet(byte[] bytes, int bpp)
    {
        if (sheetTex is not null && !sheetStale && ReferenceEquals(sheetBytes, bytes)
            && sheetFile == gfxFile && sheetPalRow == palRow) return;
        RefreshSheet(bytes, bpp);
    }
}
