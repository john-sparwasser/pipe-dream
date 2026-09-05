namespace PipeDream.Services;

// EditorSession — the sprite overlay: showing it, drawing an edited list over the scene, and
// the cheap repaints after a sprite edit or drag. The Sprites editor itself is declared with
// the rest of the state in EditorSession.cs.
public sealed partial class EditorSession
{
    /// <summary>Whether the sprite overlay is drawn. Off makes the terrain under a crowded
    /// level's sprites visible, which is why it is a toggle and not a preference.</summary>
    public bool ShowSprites
    {
        get => showSprites;
        set
        {
            if (showSprites == value) return;
            showSprites = value;
            ShowLevel(LevelNum);          // the overlay is composed in, so this is a re-parse
        }
    }
    private bool showSprites = true;

    /// <summary>How the composer should treat sprites: skip them entirely when an edited list
    /// will be drawn instead, else compose them unless the overlay is hidden — hidden still
    /// parses, because selection hit-tests against the overlay's pixel bounds.</summary>
    private LevelScene.SpriteDraw SpriteMode(bool haveEditedList)
        => haveEditedList ? LevelScene.SpriteDraw.Skip
         : showSprites ? LevelScene.SpriteDraw.Compose
         : LevelScene.SpriteDraw.ParseOnly;

    /// <summary>Capture a sprite list's OAM and draw it over every phase of the current scene.
    /// The capture is expensive, which is why it happens once per list change rather than per
    /// repaint — and it happens even when the overlay is hidden, since it is the hit target.</summary>
    private void DrawSprites(SpriteData sprites)
    {
        if (Rom is null || Scene is null) return;
        var overlay = SpriteOverlay.Build(Rom, sprites, Scene.Level.Header, LevelNum);
        Scene.Overlay = overlay;
        if (Sprites is not null) Sprites.Overlay = overlay;
        if (showSprites) Scene.RedrawOverlay();
    }

    /// <summary>
    /// Redraw the level after a sprite edit: a changed sprite leaves its old pixels behind, so
    /// the cells under every OLD sprite are recomposed and the overlay rebuilt from the edited
    /// list. A sprite edit cannot change the terrain, so the full scene rebuild this used to do
    /// — parse, objects, four composed phases — was a quarter second of pure cost per edit.
    /// </summary>
    public void RefreshSprites()
    {
        if (Rom is null || Scene is null || Sprites is not { } sp)
        { Rebuild("sprite recompose"); return; }
        if (showSprites && Scene.Overlay is { } old)
            foreach (var (x0, y0, x1, y1) in old.DrawnRects())
                for (int cy = y0 >> 4; cy <= (y1 - 1) >> 4; cy++)
                    for (int cx = x0 >> 4; cx <= (x1 - 1) >> 4; cx++)
                        Scene.RecomposeCell(cx, cy);
        DrawSprites(sp.Sprites);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One live sprite-drag step: recompose only the cells under the moved sprites' OLD pixels,
    /// shift their cached OAM, and re-blit the overlay. Routing a drag through RefreshSprites
    /// rebuilt the entire scene — parse, objects, four composed phases, a 65816 capture per
    /// sprite — per cell crossed, which made dragging a slideshow; a move changes nothing but
    /// where the overlay draws.
    /// </summary>
    public void MoveSprites(int dxCells, int dyCells)
    {
        if (Scene is not { Overlay: { } old } scene || Sprites is not { } sp)
        { RefreshSprites(); return; }

        if (showSprites)
            foreach (int i in sp.Selection)
            {
                if (i < 0 || i >= sp.Sprites.Sprites.Count) continue;
                // Bounds come from the PRE-move overlay (the records have already moved).
                // Badge-only sprites (null bounds) drew one cell at the spawn cell, whose old
                // position is the record's cell minus this step.
                var (x0, y0, x1, y1) = old.PixelBounds(i) is { } b
                    ? (b.MinX, b.MinY, b.MaxX, b.MaxY)
                    : OldBadgeRect(sp.Sprites.Sprites[i]);
                for (int cy = y0 >> 4; cy <= (y1 - 1) >> 4; cy++)
                    for (int cx = x0 >> 4; cx <= (x1 - 1) >> 4; cx++)
                        scene.RecomposeCell(cx, cy);
            }

        var moved = old.Moved(sp.Selection, dxCells * 16, dyCells * 16, sp.Sprites);
        scene.Overlay = moved;
        sp.Overlay = moved;
        if (showSprites) scene.RedrawOverlay();

        (int, int, int, int) OldBadgeRect(Sprite s)
        {
            var (cx, cy) = s.Cell(Vertical);
            int px = (cx - dxCells) * 16, py = (cy - dyCells) * 16;
            return (px, py, px + 16, py + 16);
        }
    }
}
