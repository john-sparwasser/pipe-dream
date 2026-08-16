using System.Numerics;
using ImGuiNET;

namespace PipeDream;

// ---- Sprites: place from catalog, lasso-select, drag-move, duplicate, delete ----
internal sealed class SpriteTool(EditorApp app) : EditTool(app)
{
    public override void Frame(in CanvasCtx c)
    {
        var dl = c.Dl; var origin = c.Origin; float cs = c.Cs; bool vert = c.Vertical;
        if (app.sprites is null) return;

        float pz = cs / 16f;   // level-pixel -> screen
        if (app.moveDrag is null)
            foreach (int si in app.selSprites)
            {
                if (si >= app.sprites.Sprites.Count) continue;
                var (p0, p1) = SpriteScreenRect(c, si, vert);   // whole pixel display, not the spawn cell
                dl.AddRectFilled(p0, p1, 0x3000FF00u);
                dl.AddRect(p0, p1, 0xFF00FF00, 0, 0, 2f);
            }

        if (c.Hovered)
        {
            int cx = c.Cx, cy = c.Cy;
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                // Click on a selected sprite drags the selection; else rubber-band.
                if (app.spriteEditor.SpriteIndexAt(cx, cy, vert) is int hit && app.selSprites.Contains(hit))
                {
                    app.moveDrag = (cx, cy);
                    app.spriteEditor.BuildSpriteGhost();
                    app.spriteEditor.hiddenSprites = new HashSet<int>(app.selSprites);   // hide originals; only the ghost shows
                    foreach (int si in app.selSprites)
                    {
                        var (hx, hy) = app.sprites.Sprites[si].Cell(vert);
                        app.spriteEditor.MarkSpriteCells(hx, hy);
                    }
                    app.levelDirty = true;
                }
                else app.dragStart = app.dragEnd = (c.Px, c.Py);   // lasso in level-pixels (no grid snap)
            }
            if (app.dragStart is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left)) app.dragEnd = (c.Px, c.Py);
            if (app.moveDrag is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left)) app.dragEnd = (cx, cy);   // move in cells
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                if (app.selSprites.Count > 0) app.spriteEditor.DuplicateSelection(cx, cy, vert);
                else if (app.selectedCatalog >= 0) app.spriteEditor.PlaceSprite(app.selectedCatalog, cx, cy, vert);
            }
            if (app.spriteEditor.SpriteIndexAt(cx, cy, vert) is int over && app.selSprites.Contains(over))
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        // Live pixel lasso: any sprite whose pixel display overlaps the band selects immediately.
        if (app.dragStart is { } d0 && app.dragEnd is { } d1)
        {
            int rx = Math.Min(d0.x, d1.x), ry = Math.Min(d0.y, d1.y);
            int rw = Math.Abs(d1.x - d0.x), rh = Math.Abs(d1.y - d0.y);
            SelectSpritesInPixelRect(rx, ry, rw, rh, vert);
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                dl.AddRect(new Vector2(origin.X + rx * pz, origin.Y + ry * pz),
                           new Vector2(origin.X + (rx + rw) * pz, origin.Y + (ry + rh) * pz), 0xFF00FFFF, 0, 0, 1.5f);
            else { app.dragStart = app.dragEnd = null; }
        }

        // Move: translucent ghost of the selection's pixels at the drop offset.
        if (app.moveDrag is { } sa && app.dragEnd is { } sc && app.selSprites.Count > 0)
        {
            int mdx = sc.x - sa.x, mdy = sc.y - sa.y;
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (app.spriteEditor.sprGhostTex is not null)
                {
                    var p0 = new Vector2(origin.X + (app.spriteEditor.sprGhostX + mdx * 16) * pz, origin.Y + (app.spriteEditor.sprGhostY + mdy * 16) * pz);
                    dl.AddImage(app.imgui!.GetTextureID(app.spriteEditor.sprGhostTex),
                                p0, new Vector2(p0.X + app.spriteEditor.sprGhostW * pz, p0.Y + app.spriteEditor.sprGhostH * pz),
                                Vector2.Zero, Vector2.One, 0xC0FFFFFFu);
                }
                else
                    foreach (int si in app.selSprites)
                    {
                        if (si >= app.sprites.Sprites.Count) continue;
                        var (p0, p1) = SpriteScreenRect(c, si, vert, mdx * 16, mdy * 16);   // full display, shifted
                        dl.AddRect(p0, p1, 0xFF00FF00, 0, 0, 1.5f);
                    }
            }
            else
            {
                if (mdx != 0 || mdy != 0) { app.spriteEditor.hiddenSprites = null; app.spriteEditor.MoveSelectedSprites(mdx, mdy, vert); }
                else app.spriteEditor.ClearHiddenSprites();
                app.moveDrag = null; app.dragEnd = null; app.spriteEditor.DropSpriteGhost();
            }
        }

        if (app.selSprites.Count > 0 && !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Delete))
            app.spriteEditor.DeleteSelectedSprites(vert);
    }
}
