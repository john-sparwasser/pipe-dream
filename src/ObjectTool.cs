using System.Numerics;
using ImGuiNET;
using static PipeDream.ObjectEngine;   // SizeSrc, SizeOf/MaxSize/WithSize (resize byte3 math)

namespace PipeDream;

// ---- Objects: place from catalog, lasso-select, drag-move, duplicate, delete ----
internal sealed class ObjectTool(EditorApp app) : EditTool(app)
{
    public override void Frame(in CanvasCtx c)
    {
        var dl = c.Dl; var origin = c.Origin; float cs = c.Cs;
        if (app.objList is null) return;

        if (app.moveDrag is null && app.resizeDrag is null)
            foreach (int oi in app.selObjs)
            {
                if (oi >= app.objList.Count) continue;
                DrawFootprint(c, oi, 0x300080FFu, 0xFF0080FF, 2f);
            }

        // Resize handles: single selection, idle, on a resizable axis. Hovering an edge
        // or corner shows the resize cursor; clicking starts the resize drag.
        int hoverEdges = 0;
        if (app.resizeDrag is null && app.moveDrag is null && app.dragStart is null &&
            app.selObjs.Count == 1 && app.selObjs.First() is int sel && sel < app.objList.Count)
        {
            var rz = app.objectEditor.ResizeInfo(app.objList[sel]);
            bool wOk = rz.W != SizeSrc.None, hOk = rz.H != SizeSrc.None;
            if (wOk || hOk)
            {
                var (bx, by, bw, bh) = app.objectEditor.ObjBBox(sel);
                float x0 = origin.X + bx * cs, y0 = origin.Y + by * cs;
                float x1 = origin.X + (bx + bw) * cs, y1 = origin.Y + (by + bh) * cs;
                DrawHandles(c, x0, y0, x1, y1, wOk, hOk);
                if (c.Hovered)
                {
                    hoverEdges = HandleEdges(ImGui.GetMousePos(), x0, y0, x1, y1, wOk, hOk);
                    if (hoverEdges != 0)
                    {
                        ImGui.SetMouseCursor(CursorFor(hoverEdges));
                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        {
                            app.resizeDrag = (sel, hoverEdges, app.objList[sel], c.Cx, c.Cy);
                            app.dragEnd = (c.Cx, c.Cy);
                        }
                    }
                }
            }
        }

        if (c.Hovered)
        {
            int cx = c.Cx, cy = c.Cy;
            if (app.resizeDrag is null && hoverEdges == 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                if (app.objectEditor.ObjIndexAt(cx, cy) is int hit && app.selObjs.Contains(hit)) app.moveDrag = (cx, cy);
                else app.dragStart = app.dragEnd = (cx, cy);
            }
            if (app.dragStart is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left)) app.dragEnd = (cx, cy);
            if (app.moveDrag is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left)) app.dragEnd = (cx, cy);
            if (app.resizeDrag is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left)) app.dragEnd = (cx, cy);
            if (app.resizeDrag is null && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                if (app.selObjs.Count > 0) app.objectEditor.DuplicateSelectedObjects(cx, cy);
                else if (app.selectedObjCat >= 0) app.objectEditor.PlaceObject(app.selectedObjCat, cx, cy);
                else app.objectEditor.StampBrushObjects(cx, cy);   // tile brush → DM16 objects (LM parity)
            }
            if (hoverEdges == 0 && app.objectEditor.ObjIndexAt(cx, cy) is int ov && app.selObjs.Contains(ov))
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        // Resize: preview the adjusted bbox while dragging; on release rewrite the
        // object's size bits (and anchor, for left/top edges) in one undoable edit.
        if (app.resizeDrag is { } rd && app.dragEnd is { } rc)
        {
            // DM16 tiles have their own size model (nibbles or LM's extended Form B,
            // up to 128x256); standard objects use the probed byte3 sources.
            bool dm = rd.orig.IsDm16;
            var rz = app.objectEditor.ResizeInfo(rd.orig);
            var (w0, h0) = dm ? rd.orig.Dm16Size()
                              : (SizeOf(rd.orig.Byte3, rz.W), SizeOf(rd.orig.Byte3, rz.H));
            int maxW = dm ? 128 : MaxSize(rz.W), maxH = dm ? 256 : MaxSize(rz.H);
            int dx = rc.x - rd.cx, dy = rc.y - rd.cy;
            int nx = rd.orig.AbsoluteX, ny = rd.orig.Y, nw = w0, nh = h0;
            if ((rd.edges & 2) != 0) nw = Math.Clamp(w0 + dx, 1, maxW);
            if ((rd.edges & 1) != 0) { nw = Math.Clamp(w0 - dx, 1, maxW); nx = Math.Max(0, nx + (w0 - nw)); }
            if ((rd.edges & 8) != 0) nh = Math.Clamp(h0 + dy, 1, maxH);
            if ((rd.edges & 4) != 0) { nh = Math.Clamp(h0 - dy, 1, maxH); ny = Math.Clamp(ny + (h0 - nh), 0, 0x1F); }
            // Clamp at the level bottom (LM parity): the engine happily writes past the
            // last row, bleeding into the next screen's RAM — don't let a drag do that.
            int maxRows = c.Vertical ? (app.grid?.Height ?? 27) : 27;
            nh = Math.Max(1, Math.Min(nh, maxRows - ny));
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                ImGui.SetMouseCursor(CursorFor(rd.edges));
                var (bx, by, bw, bh) = app.objectEditor.ObjBBox(rd.obj);
                DrawBand(c, bx + nx - rd.orig.AbsoluteX, by + ny - rd.orig.Y,
                         bw + nw - w0, bh + nh - h0, 0xFF0080FF);
            }
            else
            {
                if (nx != rd.orig.AbsoluteX || ny != rd.orig.Y || nw != w0 || nh != h0)
                {
                    LevelObject moved = ObjectEditor.ObjAt(rd.orig, nx, ny);
                    if (dm) app.objectEditor.ReplaceObject(rd.obj, moved.Dm16Resized(nw, nh));
                    else
                    {
                        // Same source on both axes (diagonal slopes): one nibble drives both,
                        // apply whichever the drag changed. ponytail: corner drags pick width.
                        int b3 = rz.W == rz.H ? WithSize(rd.orig.Byte3, rz.W, nw != w0 ? nw : nh)
                                              : WithSize(WithSize(rd.orig.Byte3, rz.W, nw), rz.H, nh);
                        app.objectEditor.ReplaceObject(rd.obj, new LevelObject(false, rd.orig.Number, (nx >> 4) & 0x1F, nx & 15,
                            ny, b3, rd.orig.ExtraByte, rd.orig.Dm16Tile,
                            rd.orig.Dm16Page, rd.orig.Dm16ExtX, rd.orig.Dm16ExtH));
                    }
                }
                app.resizeDrag = null; app.dragEnd = null;
            }
        }

        // Live lasso once the band covers >1 cell; a stationary click instead selects
        // the topmost object at that cell (or cycles down the overlap stack, LM-style).
        // Ctrl+lasso grabs the covered tiles as the stamp brush instead of selecting.
        if (app.dragStart is { } d0 && app.dragEnd is { } d1)
        {
            bool grab = ImGui.GetIO().KeyCtrl;
            if (d0 != d1)
            {
                var (rx, ry, rw, rh) = Band(d0, d1);
                if (!grab)
                {
                    app.selObjs.Clear();
                    for (int i = 0; i < app.objList.Count; i++)
                    {
                        var (ox, oy, ow, oh) = app.objectEditor.ObjBBox(i);
                        if (ox < rx + rw && ox + ow > rx && oy < ry + rh && oy + oh > ry) app.selObjs.Add(i);
                    }
                }
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    DrawBand(c, rx, ry, rw, rh, grab ? 0xFF00FF00 : 0xFF00FFFF);
            }
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (d0 == d1) app.objectEditor.CycleSelectionAt(d0.x, d0.y);
                else if (grab)
                {
                    var (rx, ry, rw, rh) = Band(d0, d1);
                    app.objectEditor.GrabSelection(rx, ry, rw, rh);
                }
                app.dragStart = app.dragEnd = null;
            }
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
                    var (ox, oy, ow, oh) = app.objectEditor.ObjBBox(oi);
                    dl.AddRect(new Vector2(origin.X + (ox + mdx) * cs, origin.Y + (oy + mdy) * cs),
                               new Vector2(origin.X + (ox + ow + mdx) * cs, origin.Y + (oy + oh + mdy) * cs),
                               0xFF0080FF, 0, 0, 1.5f);
                }
            }
            else
            {
                if (mdx != 0 || mdy != 0) app.objectEditor.MoveSelectedObjects(mdx, mdy);
                else app.objectEditor.CycleSelectionAt(oa.x, oa.y);   // stationary click on selection: cycle the stack
                app.moveDrag = null; app.dragEnd = null;
            }
        }

        if (app.resizeDrag is null && app.selObjs.Count > 0 &&
            !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Delete))
            app.objectEditor.DeleteSelectedObjects();
    }

    // Selection highlight hugging the object's real tiles: cells it currently shows
    // get the full fill; cells another object covers (it wrote them, but a later
    // stream object overwrote — LM's overlap view) get a dimmed fill. The outline
    // follows the object's full written extent. Declared rect when owner data is
    // unavailable.
    private void DrawFootprint(in CanvasCtx c, int oi, uint fill, uint line, float th)
    {
        var own = app.objectEditor.objOwners;
        var (bx, by, bw, bh) = app.objectEditor.ObjBBox(oi);
        if (own is null || oi >= app.objectEditor.objBounds.Length || app.objectEditor.objBounds[oi] is null)
        {
            c.Dl.AddRectFilled(new Vector2(c.Origin.X + bx * c.Cs, c.Origin.Y + by * c.Cs),
                               new Vector2(c.Origin.X + (bx + bw) * c.Cs, c.Origin.Y + (by + bh) * c.Cs), fill);
            DrawBand(c, bx, by, bw, bh, line);
            return;
        }
        ushort id = (ushort)(oi + 1);
        var stacks = app.objectEditor.objStacks;
        uint dim = (fill & 0x00FFFFFF) | (fill >> 24) / 2 << 24;     // buried: half alpha
        bool Mine(int x, int y)
            => (uint)x < (uint)own.Width && (uint)y < (uint)own.Height &&
               (own.Get(x, y) == id ||
                (stacks is not null && stacks.TryGetValue(y * own.Width + x, out var s) &&
                 Array.IndexOf(s, id) >= 0));
        for (int y = by; y < by + bh; y++)
            for (int x = bx; x < bx + bw; x++)
            {
                if (!Mine(x, y)) continue;
                var p0 = new Vector2(c.Origin.X + x * c.Cs, c.Origin.Y + y * c.Cs);
                var p1 = new Vector2(p0.X + c.Cs, p0.Y + c.Cs);
                c.Dl.AddRectFilled(p0, p1, own.Get(x, y) == id ? fill : dim);
                if (!Mine(x - 1, y)) c.Dl.AddLine(p0, new Vector2(p0.X, p1.Y), line, th);
                if (!Mine(x + 1, y)) c.Dl.AddLine(new Vector2(p1.X, p0.Y), p1, line, th);
                if (!Mine(x, y - 1)) c.Dl.AddLine(p0, new Vector2(p1.X, p0.Y), line, th);
                if (!Mine(x, y + 1)) c.Dl.AddLine(new Vector2(p0.X, p1.Y), p1, line, th);
            }
    }

    // Which bbox edges (1=L 2=R 4=T 8=B) the mouse is within grab range of. Corners are
    // two flags at once; on tiny objects the nearer edge wins over its opposite.
    private static int HandleEdges(Vector2 m, float x0, float y0, float x1, float y1, bool wOk, bool hOk)
    {
        const float t = 6f;
        bool inX = m.X > x0 - t && m.X < x1 + t, inY = m.Y > y0 - t && m.Y < y1 + t;
        int e = 0;
        if (wOk && inY && Math.Abs(m.X - x0) <= t) e |= 1;
        if (wOk && inY && Math.Abs(m.X - x1) <= t) e |= 2;
        if (hOk && inX && Math.Abs(m.Y - y0) <= t) e |= 4;
        if (hOk && inX && Math.Abs(m.Y - y1) <= t) e |= 8;
        if ((e & 3) == 3) e &= Math.Abs(m.X - x0) < Math.Abs(m.X - x1) ? ~2 : ~1;
        if ((e & 12) == 12) e &= Math.Abs(m.Y - y0) < Math.Abs(m.Y - y1) ? ~8 : ~4;
        return e;
    }

    private static ImGuiMouseCursor CursorFor(int e) => e switch
    {
        1 or 2 => ImGuiMouseCursor.ResizeEW,
        4 or 8 => ImGuiMouseCursor.ResizeNS,
        5 or 10 => ImGuiMouseCursor.ResizeNWSE,   // TL / BR
        6 or 9 => ImGuiMouseCursor.ResizeNESW,    // TR / BL
        _ => ImGuiMouseCursor.Arrow,
    };

    // Knobs on the enabled edges' midpoints + all corners (corners resize whichever
    // axes are enabled), vector-editor style.
    private static void DrawHandles(in CanvasCtx c, float x0, float y0, float x1, float y1, bool wOk, bool hOk)
    {
        var dl = c.Dl;      // local functions can't capture `in` params
        void Knob(float x, float y)
        {
            dl.AddRectFilled(new Vector2(x - 3, y - 3), new Vector2(x + 3, y + 3), 0xFF0080FF);
            dl.AddRect(new Vector2(x - 3, y - 3), new Vector2(x + 3, y + 3), 0xFF000000);
        }
        float mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
        if (wOk) { Knob(x0, my); Knob(x1, my); }
        if (hOk) { Knob(mx, y0); Knob(mx, y1); }
        Knob(x0, y0); Knob(x1, y0); Knob(x0, y1); Knob(x1, y1);
    }
}
