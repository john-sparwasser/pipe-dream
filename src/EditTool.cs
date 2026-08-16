using System.Numerics;
using ImGuiNET;

namespace PipeDream;

// The canvas edit tools (one per EditMode). Each owns its whole per-frame interaction —
// selection highlights, hover, rubber-band select, drag-move, place/duplicate, delete —
// so the canvas loop just calls ActiveTool.Frame(ctx) with no mode conditionals. They
// hold a back-reference to EditorApp so they can touch its edit state directly
// (composition: EditorApp holds the active tool); the shared drag state lives on
// EditorApp and is cleared on mode switch.
internal abstract class EditTool(EditorApp app)
{
    // Per-frame canvas render context handed to the active tool.
    internal readonly record struct CanvasCtx(Vector2 Origin, float Cs, ImDrawListPtr Dl,
                                              int Cx, int Cy, int Px, int Py, bool Hovered, bool Vertical);

    protected readonly EditorApp app = app;
    public abstract void Frame(in CanvasCtx c);

    // Shared helpers for the cell rubber-band (lives on EditorApp; cleared on mode switch).
    protected static (int x, int y, int w, int h) Band((int x, int y) a, (int x, int y) b) =>
        (Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Abs(b.x - a.x) + 1, Math.Abs(b.y - a.y) + 1);
    protected void DrawBand(in CanvasCtx c, int rx, int ry, int rw, int rh, uint col) =>
        c.Dl.AddRect(new Vector2(c.Origin.X + rx * c.Cs, c.Origin.Y + ry * c.Cs),
                     new Vector2(c.Origin.X + (rx + rw) * c.Cs, c.Origin.Y + (ry + rh) * c.Cs),
                     col, 0, 0, 1.5f);

    // Screen rect of a sprite's whole pixel display (badge-only sprites fall back to their
    // cell), optionally shifted by a level-pixel delta (for move previews).
    protected (Vector2 p0, Vector2 p1) SpriteScreenRect(in CanvasCtx c, int si, bool vert, int shiftPx = 0, int shiftPy = 0)
    {
        float pz = c.Cs / 16f;
        if (app.spriteOverlay?.PixelBounds(si) is { } b)
            return (new Vector2(c.Origin.X + (b.MinX + shiftPx) * pz, c.Origin.Y + (b.MinY + shiftPy) * pz),
                    new Vector2(c.Origin.X + (b.MaxX + shiftPx) * pz, c.Origin.Y + (b.MaxY + shiftPy) * pz));
        var (sx, sy) = app.sprites!.Sprites[si].Cell(vert);
        return (new Vector2(c.Origin.X + sx * c.Cs + shiftPx * pz, c.Origin.Y + sy * c.Cs + shiftPy * pz),
                new Vector2(c.Origin.X + (sx + 1) * c.Cs + shiftPx * pz, c.Origin.Y + (sy + 1) * c.Cs + shiftPy * pz));
    }

    // Select every sprite whose pixel display overlaps a level-pixel rectangle.
    protected void SelectSpritesInPixelRect(int rx, int ry, int rw, int rh, bool vert)
    {
        app.selSprites.Clear();
        if (app.sprites is null) return;
        for (int i = 0; i < app.sprites.Sprites.Count; i++)
        {
            int bx, by, bx2, by2;
            if (app.spriteOverlay?.PixelBounds(i) is { } pb) (bx, by, bx2, by2) = (pb.MinX, pb.MinY, pb.MaxX, pb.MaxY);
            else { var (sx, sy) = app.sprites.Sprites[i].Cell(vert); (bx, by, bx2, by2) = (sx * 16, sy * 16, sx * 16 + 16, sy * 16 + 16); }
            if (bx < rx + rw && bx2 > rx && by < ry + rh && by2 > ry) app.selSprites.Add(i);
        }
    }
}
