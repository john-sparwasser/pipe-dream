using ImGuiNET;

namespace PipeDream;

// ROM/level session management: loading the ROM, parsing the level into edit state,
// recomposing everything palette/GFX-dependent, and feeding the LevelCanvas compositor.
internal sealed class LevelSession(EditorApp app)
{
    private readonly EditorApp app = app;

    private uint backdropColor;
    private ushort[]? bgImage;       // layer-2 background image (BG def indices), else null
    private Map16Grid? layer2Grid;   // layer-2 object layer, else null

    internal void LoadRom(string path)
    {
        try
        {
            app.rom = Rom.Load(path);
            app.loadedRomPath = path;
            app.ratCount = RatsWriter.EnumerateRats(app.rom).Count();
            app.objectEditor.objCatTileset = -1;   // force object catalog rebuild for the new ROM
            ParseLevel();
        }
        catch (Exception e)
        {
            app.rom = null;
            app.loadedRomPath = $"{path}  (load failed: {e.Message})";
        }
    }

    internal void ParseLevel()
    {
        try
        {
            app.level = app.rom is null ? null : LevelParser.Parse(app.rom, app.levelNum);
            app.grid = app.rom is not null && app.level is not null ? ObjectEngine.Render(app.rom, app.level) : null;
            app.baseGrid = app.grid?.Clone();          // snapshot to diff edits against on save
            app.objList = app.level is not null ? new List<LevelObject>(app.level.Objects) : null;
            app.objectEditor.RenderObjectsTracked();                      // owner attribution for hit-testing (grid stays as parsed)
            app.history.Clear();                                          // new grid = new history
            app.selSprites.Clear(); app.selObjs.Clear(); app.dragStart = app.dragEnd = null; app.moveDrag = null; app.resizeDrag = null; app.spriteEditor.DropSpriteGhost(); app.spriteEditor.hiddenSprites = null;
            app.levelGfxPanel.InvalidateLevel();                          // refresh Level GFX window
            if (app.levelNum != app.paletteEditor.palEditsLevel) { app.paletteEditor.palEdits.Clear(); app.paletteEditor.palEditsLevel = app.levelNum; }
            // Layer 2: background image or object layer, drawn behind layer 1.
            bgImage = app.rom is not null && app.level is not null ? LevelParser.DecodeBgImage(app.rom, app.levelNum) : null;
            layer2Grid = app.rom is not null && app.level is not null
                ? ObjectEngine.RenderLayer2(app.rom, app.level.Header, app.levelNum) : null;
            app.sprites = app.rom is not null && app.level is not null ? SpriteData.Parse(app.rom, app.levelNum) : null;
            // Run the expensive OAM captures once per parse; repaints just re-blit.
            app.spriteOverlay = app.rom is not null && app.level is not null && app.sprites is not null
                ? SpriteOverlay.Build(app.rom, app.sprites, app.level.Header, app.levelNum) : null;
            app.canvasFull = true;
            RebuildGraphics();
        }
        catch { app.level = null; app.grid = null; app.tileCaches = null; }
    }

    // Recompose everything palette-dependent (tile caches, sheet, canvas) without
    // reparsing the level — so palette edits don't reset the grid or undo history.
    internal void RebuildGraphics()
    {
        if (app.rom is null || app.level is null) { app.tileCaches = null; app.bgCaches = null; return; }
        // The four animation phases are independent — compose them on parallel workers
        // (all ROM/palette access below is read-only; Gfx.Cached locks its dictionary).
        // BG tiles compose unconditionally: the Map16 picker's bank 2 shows them even in
        // levels whose layer 2 is an object layer.
        var fg = new uint[4][][];
        var bg = new uint[4][][];
        var (r, lv, ln) = (app.rom, app.level, app.levelNum);
        System.Threading.Tasks.Parallel.For(0, 4, p =>
        {
            fg[p] = Map16.ComposeAll(r, lv.Header, ln, p, app.paletteEditor.EditedPalette(p));
            bg[p] = Map16.ComposeAllBg(r, lv.Header, ln, p, app.paletteEditor.EditedPalette(p));
        });
        app.tileCaches = fg;
        app.bgCaches = bg;
        backdropColor = app.paletteEditor.EditedPalette(0)!.Rgba[0];
        app.map16Editor.m16ChrPal = -1;          // 8x8 picker sheet: recompose on next draw
        app.map16Editor.BuildMap16Sheet();
        app.spriteEditor.BuildSpriteCatalog();
        app.objectEditor.objCatTex?.Dispose(); app.objectEditor.objCatTex = null;   // stale: Objects tab rebuilds it lazily
        BuildLevelCanvas();
    }

    // Assemble the compositor inputs from current edit state, or null when nothing to draw.
    private CanvasScene? Scene()
    {
        if (app.tileCaches is null || app.grid is null) return null;
        int visRows = app.rom is not null && app.level is not null && app.rom.IsVerticalMode(app.level.Header.LevelMode)
            ? app.grid.Height : 27;
        return new CanvasScene(app.tileCaches, backdropColor, app.grid, bgImage, app.bgCaches, layer2Grid, visRows,
            (img, W, H, p) => { if (app.showSprites) app.spriteOverlay?.Draw(img, W, H, app.paletteEditor.EditedPalette(p)!, app.spriteEditor.hiddenSprites); });
    }

    internal void BuildLevelCanvas()
    {
        app.canvasFull = false;
        if (Scene() is { } s) app.canvas.Rebuild(s, app.AnimPhase); else app.canvas.Drop();
    }

    internal void ApplyDirtyCells()
    {
        if (Scene() is { } s) app.canvas.ApplyDirty(s, app.AnimPhase); else app.canvas.Drop();
    }

    // GFX tab: the level's loaded VRAM tile sheets (FG/BG/SP slots), scrollable. Editing
    // a bin id stores a session override (Rom.GfxSlotOverrides) and recomposes everything
    // that resolves GFX — level canvas, sprite overlay/catalog, Map16 sheet.
    internal void DrawGfxTab()
    {
        ImGui.BeginChild("gfxtab");
        app.levelGfxPanel.Draw(app.rom, app.level, app.levelNum, () =>
        {
            app.canvasFull = true; app.levelDirty = true;
            RebuildGraphics();          // tile caches + Map16 sheet + catalogs + canvas
            // Swap the SP tile sheets only — the cached OAM captures stay valid.
            if (app.rom is not null && app.level is not null)
                app.spriteOverlay = app.spriteOverlay?.WithReloadedTiles(app.rom, app.level.Header, app.levelNum);
        });
        ImGui.EndChild();
    }
}
