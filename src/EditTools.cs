using System.Numerics;
using Foster.Framework;
using ImGuiNET;

namespace PipeDream;

// The canvas edit tools (one per EditMode). Each owns its whole per-frame interaction —
// selection highlights, hover, rubber-band select, drag-move, place/duplicate, delete —
// so the canvas loop just calls ActiveTool.Frame(ctx) with no mode conditionals. They are
// nested in EditorApp so they can touch its edit state directly (composition: EditorApp
// holds the active tool); the shared drag state lives on EditorApp and is cleared on
// mode switch.
public partial class EditorApp
{
    // Per-frame canvas render context handed to the active tool.
    private readonly record struct CanvasCtx(Vector2 Origin, float Cs, ImDrawListPtr Dl,
                                             int Cx, int Cy, bool Hovered, bool Vertical);

    private abstract class EditTool(EditorApp app)
    {
        protected readonly EditorApp app = app;
        public abstract string Hint { get; }
        public abstract void Frame(in CanvasCtx c);

        // Shared helpers for the cell rubber-band (lives on EditorApp; cleared on mode switch).
        protected static (int x, int y, int w, int h) Band((int x, int y) a, (int x, int y) b) =>
            (Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Abs(b.x - a.x) + 1, Math.Abs(b.y - a.y) + 1);
        protected void DrawBand(in CanvasCtx c, int rx, int ry, int rw, int rh, uint col) =>
            c.Dl.AddRect(new Vector2(c.Origin.X + rx * c.Cs, c.Origin.Y + ry * c.Cs),
                         new Vector2(c.Origin.X + (rx + rw) * c.Cs, c.Origin.Y + (ry + rh) * c.Cs),
                         col, 0, 0, 1.5f);
    }

    // ---- Layer 1: Map16 tile painting (grab region as brush, stamp, move, erase) ----
    private sealed class TileTool(EditorApp app) : EditTool(app)
    {
        public override string Hint =>
            $"—  Layer 1:  left: select/grab ({app.brushW}x{app.brushH})   drag selection: move   right: stamp   Del: erase   Esc: sprites";

        public override void Frame(in CanvasCtx c)
        {
            var dl = c.Dl; var origin = c.Origin; float cs = c.Cs;
            if (app.selRect is { } sr)
                DrawBand(c, sr.x, sr.y, sr.w, sr.h, 0xFF00FFFF);

            if (c.Hovered)
            {
                int cx = c.Cx, cy = c.Cy;
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    // Click inside the selection drags it (move); outside starts a new one.
                    if (app.selRect is { } r && cx >= r.x && cx < r.x + r.w && cy >= r.y && cy < r.y + r.h)
                        app.moveDrag = (cx, cy);
                    else
                        app.dragStart = app.dragEnd = (cx, cy);
                }
                if (app.dragStart is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left)) app.dragEnd = (cx, cy);
                if (app.moveDrag is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left)) app.dragEnd = (cx, cy);
                if (ImGui.IsMouseDown(ImGuiMouseButton.Right)) app.StampBrush(cx, cy);

                bool overSel = app.selRect is { } hr && cx >= hr.x && cx < hr.x + hr.w && cy >= hr.y && cy < hr.y + hr.h;
                if (overSel && app.selRect is { } hr2)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    dl.AddRectFilled(new Vector2(origin.X + hr2.x * cs, origin.Y + hr2.y * cs),
                                     new Vector2(origin.X + (hr2.x + hr2.w) * cs, origin.Y + (hr2.y + hr2.h) * cs),
                                     0x3000FFFFu);
                }
                else
                {
                    var tl = new Vector2(origin.X + cx * cs, origin.Y + cy * cs);
                    dl.AddRect(tl, new Vector2(tl.X + cs, tl.Y + cs), 0xFFFFFFFF, 0, 0, 1.5f);
                }
            }

            // Rubber band; grab the region as the brush on release.
            if (app.dragStart is { } d0 && app.dragEnd is { } d1)
            {
                var (rx, ry, rw, rh) = Band(d0, d1);
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) DrawBand(c, rx, ry, rw, rh, 0xFF00FFFF);
                else { app.GrabSelection(rx, ry, rw, rh); app.dragStart = app.dragEnd = null; }
            }

            // Selection move: ghost tiles from the Map16 sheet preview the drop; on release
            // erase the source and stamp at the destination as one undo step.
            if (app.moveDrag is { } anchor && app.selRect is { } mr && app.dragEnd is { } cur && app.grid is not null)
            {
                int dx = Math.Clamp(mr.x + cur.x - anchor.x, 0, app.grid.Width - mr.w);
                int dy = Math.Clamp(mr.y + cur.y - anchor.y, 0, app.grid.Height - mr.h);
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    var sheetTex = app.map16Texs[app.AnimPhase] ?? app.map16Texs[0];
                    int tileCount = app.tileCaches?[0].Length ?? 0;
                    for (int j = 0; j < mr.h && sheetTex is not null; j++)
                        for (int i = 0; i < mr.w; i++)
                        {
                            int t = app.brushTiles[j * mr.w + i];
                            if (t == Map16Grid.Empty || (t & ObjectEngine.Marker) != 0 || t >= tileCount) continue;
                            var p0 = new Vector2(origin.X + (dx + i) * cs, origin.Y + (dy + j) * cs);
                            var uv0 = new Vector2((t % 16) * 16f / app.map16W, (t / 16) * 16f / app.map16H);
                            var uv1 = new Vector2(uv0.X + 16f / app.map16W, uv0.Y + 16f / app.map16H);
                            dl.AddImage(app.imgui!.GetTextureID(sheetTex), p0, new Vector2(p0.X + cs, p0.Y + cs), uv0, uv1, 0xC0FFFFFFu);
                        }
                    DrawBand(c, dx, dy, mr.w, mr.h, 0xFF00FFFF);
                }
                else
                {
                    if (dx != mr.x || dy != mr.y)
                    {
                        for (int y = mr.y; y < mr.y + mr.h; y++)
                            for (int x = mr.x; x < mr.x + mr.w; x++) app.PaintCell(x, y, Map16Grid.Empty);
                        for (int j = 0; j < mr.h; j++)
                            for (int i = 0; i < mr.w; i++) app.PaintCell(dx + i, dy + j, app.brushTiles[j * mr.w + i]);
                        app.CommitStroke();
                        app.selRect = (dx, dy, mr.w, mr.h);
                    }
                    app.moveDrag = null; app.dragEnd = null;
                }
            }

            if (app.selRect is { } er && !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Delete))
            {
                for (int y = er.y; y < er.y + er.h; y++)
                    for (int x = er.x; x < er.x + er.w; x++) app.PaintCell(x, y, Map16Grid.Empty);
                app.CommitStroke();
            }
        }
    }

    // ---- Sprites: place from catalog, lasso-select, drag-move, duplicate, delete ----
    private sealed class SpriteTool(EditorApp app) : EditTool(app)
    {
        public override string Hint =>
            $"—  Sprites:  left: select/drag-select   drag selection: move   right: {(app.selSprites.Count > 0 ? "duplicate" : app.selectedCatalog >= 0 ? $"place {app.selectedCatalog:X2}" : "place (pick in palette)")}   Del: delete   Esc: objects";

        public override void Frame(in CanvasCtx c)
        {
            var dl = c.Dl; var origin = c.Origin; float cs = c.Cs; bool vert = c.Vertical;
            if (app.sprites is null) return;

            if (app.moveDrag is null)
                foreach (int si in app.selSprites)
                {
                    if (si >= app.sprites.Sprites.Count) continue;
                    var (sx, sy) = app.sprites.Sprites[si].Cell(vert);
                    dl.AddRectFilled(new Vector2(origin.X + sx * cs, origin.Y + sy * cs),
                                     new Vector2(origin.X + (sx + 1) * cs, origin.Y + (sy + 1) * cs), 0x3000FF00u);
                    dl.AddRect(new Vector2(origin.X + sx * cs, origin.Y + sy * cs),
                               new Vector2(origin.X + (sx + 1) * cs, origin.Y + (sy + 1) * cs), 0xFF00FF00, 0, 0, 2f);
                }

            if (c.Hovered)
            {
                int cx = c.Cx, cy = c.Cy;
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    // Click on a selected sprite drags the selection; else rubber-band.
                    if (app.SpriteIndexAt(cx, cy, vert) is int hit && app.selSprites.Contains(hit))
                    {
                        app.moveDrag = (cx, cy);
                        app.BuildSpriteGhost();
                        app.hiddenSprites = new HashSet<int>(app.selSprites);   // hide originals; only the ghost shows
                        foreach (int si in app.selSprites)
                        {
                            var (hx, hy) = app.sprites.Sprites[si].Cell(vert);
                            app.MarkSpriteCells(hx, hy);
                        }
                        app.levelDirty = true;
                    }
                    else app.dragStart = app.dragEnd = (cx, cy);
                }
                if (app.dragStart is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left)) app.dragEnd = (cx, cy);
                if (app.moveDrag is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left)) app.dragEnd = (cx, cy);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    if (app.selSprites.Count > 0) app.DuplicateSelection(cx, cy, vert);
                    else if (app.selectedCatalog >= 0) app.PlaceSprite(app.selectedCatalog, cx, cy, vert);
                }
                if (app.SpriteIndexAt(cx, cy, vert) is int over && app.selSprites.Contains(over))
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }

            // Live lasso: sprites inside the band select immediately.
            if (app.dragStart is { } d0 && app.dragEnd is { } d1)
            {
                var (rx, ry, rw, rh) = Band(d0, d1);
                app.selSprites.Clear();
                for (int i = 0; i < app.sprites.Sprites.Count; i++)
                {
                    var (sx, sy) = app.sprites.Sprites[i].Cell(vert);
                    if (sx >= rx && sx < rx + rw && sy >= ry && sy < ry + rh) app.selSprites.Add(i);
                }
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) DrawBand(c, rx, ry, rw, rh, 0xFF00FFFF);
                else { app.dragStart = app.dragEnd = null; }
            }

            // Move: translucent ghost of the selection's pixels at the drop offset.
            if (app.moveDrag is { } sa && app.dragEnd is { } sc && app.selSprites.Count > 0)
            {
                int mdx = sc.x - sa.x, mdy = sc.y - sa.y;
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    if (app.sprGhostTex is not null)
                    {
                        float pz = cs / 16f;
                        var p0 = new Vector2(origin.X + (app.sprGhostX + mdx * 16) * pz, origin.Y + (app.sprGhostY + mdy * 16) * pz);
                        dl.AddImage(app.imgui!.GetTextureID(app.sprGhostTex),
                                    p0, new Vector2(p0.X + app.sprGhostW * pz, p0.Y + app.sprGhostH * pz),
                                    Vector2.Zero, Vector2.One, 0xC0FFFFFFu);
                    }
                    else
                        foreach (int si in app.selSprites)
                        {
                            var (sx, sy) = app.sprites.Sprites[si].Cell(vert);
                            var p0 = new Vector2(origin.X + (sx + mdx) * cs, origin.Y + (sy + mdy) * cs);
                            dl.AddRect(p0, new Vector2(p0.X + cs, p0.Y + cs), 0xFF00FF00, 0, 0, 1.5f);
                        }
                }
                else
                {
                    if (mdx != 0 || mdy != 0) { app.hiddenSprites = null; app.MoveSelectedSprites(mdx, mdy, vert); }
                    else app.ClearHiddenSprites();
                    app.moveDrag = null; app.dragEnd = null; app.DropSpriteGhost();
                }
            }

            if (app.selSprites.Count > 0 && !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Delete))
                app.DeleteSelectedSprites(vert);
        }
    }

    // ---- Objects: place from catalog, lasso-select, drag-move, duplicate, delete ----
    private sealed class ObjectTool(EditorApp app) : EditTool(app)
    {
        public override string Hint =>
            $"—  Objects:  left: select/drag-select   drag selection: move   right: {(app.selObjs.Count > 0 ? "duplicate" : app.selectedObjCat >= 0 ? $"place {app.selectedObjCat:X2}" : "place (pick in palette)")}   Del: delete   Esc: layer 1";

        public override void Frame(in CanvasCtx c)
        {
            var dl = c.Dl; var origin = c.Origin; float cs = c.Cs;
            if (app.objList is null) return;

            if (app.moveDrag is null)
                foreach (int oi in app.selObjs)
                {
                    if (oi >= app.objList.Count) continue;
                    var (ox, oy, ow, oh) = app.ObjRect(app.objList[oi]);
                    dl.AddRectFilled(new Vector2(origin.X + ox * cs, origin.Y + oy * cs),
                                     new Vector2(origin.X + (ox + ow) * cs, origin.Y + (oy + oh) * cs), 0x300080FFu);
                    dl.AddRect(new Vector2(origin.X + ox * cs, origin.Y + oy * cs),
                               new Vector2(origin.X + (ox + ow) * cs, origin.Y + (oy + oh) * cs), 0xFF0080FF, 0, 0, 2f);
                }

            if (c.Hovered)
            {
                int cx = c.Cx, cy = c.Cy;
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    if (app.ObjIndexAt(cx, cy) is int hit && app.selObjs.Contains(hit)) app.moveDrag = (cx, cy);
                    else app.dragStart = app.dragEnd = (cx, cy);
                }
                if (app.dragStart is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left)) app.dragEnd = (cx, cy);
                if (app.moveDrag is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left)) app.dragEnd = (cx, cy);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    if (app.selObjs.Count > 0) app.DuplicateSelectedObjects(cx, cy);
                    else if (app.selectedObjCat >= 0) app.PlaceObject(app.selectedObjCat, cx, cy);
                }
                if (app.ObjIndexAt(cx, cy) is int ov && app.selObjs.Contains(ov))
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }

            // Live lasso: objects whose footprint rect overlaps the band.
            if (app.dragStart is { } d0 && app.dragEnd is { } d1)
            {
                var (rx, ry, rw, rh) = Band(d0, d1);
                app.selObjs.Clear();
                for (int i = 0; i < app.objList.Count; i++)
                {
                    var (ox, oy, ow, oh) = app.ObjRect(app.objList[i]);
                    if (ox < rx + rw && ox + ow > rx && oy < ry + rh && oy + oh > ry) app.selObjs.Add(i);
                }
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) DrawBand(c, rx, ry, rw, rh, 0xFF00FFFF);
                else app.dragStart = app.dragEnd = null;
            }

            // Move: outline the footprints at the drag delta; on release shift the objects.
            if (app.moveDrag is { } oa && app.dragEnd is { } oc && app.selObjs.Count > 0)
            {
                int mdx = oc.x - oa.x, mdy = oc.y - oa.y;
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    foreach (int oi in app.selObjs)
                    {
                        var (ox, oy, ow, oh) = app.ObjRect(app.objList[oi]);
                        dl.AddRect(new Vector2(origin.X + (ox + mdx) * cs, origin.Y + (oy + mdy) * cs),
                                   new Vector2(origin.X + (ox + ow + mdx) * cs, origin.Y + (oy + oh + mdy) * cs),
                                   0xFF0080FF, 0, 0, 1.5f);
                    }
                }
                else
                {
                    if (mdx != 0 || mdy != 0) app.MoveSelectedObjects(mdx, mdy);
                    app.moveDrag = null; app.dragEnd = null;
                }
            }

            if (app.selObjs.Count > 0 && !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Delete))
                app.DeleteSelectedObjects();
        }
    }
}
