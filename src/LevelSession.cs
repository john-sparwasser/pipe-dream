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
            // Project GFX overrides are session state on the Rom instance — refill them
            // for every recorded level so any level renders with its overrides on open.
            if (app.project is not null)
            {
                // Imported ExGFX files are session state on the Rom too — hydrate them
                // before the first parse so the initial render already resolves imports
                // through Gfx.Cached.
                foreach (var (id, b64) in app.project.Data.Gfx)
                    app.rom.ImportedGfx[Convert.ToInt32(id, 16)] = Convert.FromBase64String(b64);
                foreach (var (key, state) in app.project.Data.Levels)
                {
                    int lvl = Convert.ToInt32(key, 16);
                    foreach (var (word, file) in state.GfxOverrides)
                        app.rom.GfxSlotOverrides[(lvl, word)] = file;
                    if (state.Header is { } hx) app.rom.LevelHeaderOverrides[lvl] = Convert.FromHexString(hx);
                }
                // Replay the Map16/acts snapshot into the session ROM: same code Build
                // uses on a fresh base, so session and built ROM can't drift.
                if (RomBuilder.ReplayMap16(app.rom, app.project.Data) is { } replayErr)
                    app.saveStatus = replayErr;
            }
            ParseLevel();
        }
        catch (Exception e)
        {
            app.rom = null;
            app.loadedRomPath = $"{path}  (load failed: {e.Message})";
        }
    }

    /// <summary>Level navigation: stash the current level's edits into the project first,
    /// then parse (and hydrate) the new one. Reload deliberately skips the stash — it
    /// re-hydrates from the project, i.e. "revert to last autosave".</summary>
    internal void SwitchLevel(int newLevel)
    {
        if (newLevel == app.levelNum) return;
        if (app.project is not null && app.currentLevelTouched) StashCurrentLevel();
        app.levelNum = newLevel;
        ParseLevel();
    }

    /// <summary>Project.SyncBeforeSave hook: flush live editor state into Project.Data
    /// right before project.pdp is written.</summary>
    internal void SyncProject()
    {
        if (app.project is null) return;
        if (app.currentLevelTouched) StashCurrentLevel();
        RefreshCapturedMap16();
        if (app.rom is not null)
            app.project.Data.Gfx = app.rom.ImportedGfx
                .ToDictionary(kv => kv.Key.ToString("X3"), kv => Convert.ToBase64String(kv.Value));
    }

    // Write the current level's session state into the project snapshot.
    private void StashCurrentLevel()
    {
        if (app.project is null || app.rom is null) return;
        var s = app.project.Data.Level(app.levelNum);
        s.Objects = app.objList?.Select(ProjectFile.ObjectDto.From).ToList() ?? new();
        if (app.sprites is not null)
        {
            s.SpriteMemory = app.sprites.SpriteMemory;
            s.Buoyancy = app.sprites.Buoyancy;
            s.Sprites = app.sprites.Sprites.Select(ProjectFile.SpriteDto.From).ToList();
        }
        s.Palette = app.paletteEditor.palEdits.ToDictionary(kv => kv.Key, kv => (int)kv.Value);
        s.GfxOverrides = app.rom.GfxSlotOverrides.Where(kv => kv.Key.Level == app.levelNum)
                            .ToDictionary(kv => kv.Key.Word, kv => kv.Value);
        s.Header = app.rom.LevelHeaderOverrides.TryGetValue(app.levelNum, out var hb)
            ? Convert.ToHexString(hb) : null;
        app.project.MarkDirty();
    }

    /// <summary>Replace the current level's header. The override lives on the Rom (session
    /// state, like the GFX slot overrides), so a reparse re-renders everything the header
    /// drives — object dispatch, palettes, layer 2, sprite tiles.
    /// ponytail: reparsing costs the undo history, which a tileset change invalidates anyway.</summary>
    internal void ApplyHeader(LevelHeader header)
    {
        if (app.rom is null || app.level is null) return;
        if (app.project is not null && app.currentLevelTouched) StashCurrentLevel();
        app.rom.LevelHeaderOverrides[app.levelNum] = header.ToBytes();
        ParseLevel();
        app.currentLevelTouched = true;
        app.project?.MarkDirty();
    }

    /// <summary>Drop the header edit and go back to the base ROM's header.</summary>
    internal void RevertHeader()
    {
        if (app.rom is null) return;
        if (app.project is not null && app.currentLevelTouched) StashCurrentLevel();
        app.rom.LevelHeaderOverrides.Remove(app.levelNum);
        ParseLevel();
        app.currentLevelTouched = true;
        app.project?.MarkDirty();
    }

    // Re-read every captured Map16/acts slot's CURRENT bytes from the ROM. Values are
    // never tracked at edit time — re-reading at save makes undo/redo and the extended
    // region's relocation-on-allocation free.
    private void RefreshCapturedMap16()
    {
        if (app.rom is null || app.project is null) return;
        var m = app.project.Data.Map16;
        int tileset = app.level?.Header.Tileset ?? 1;
        foreach (var addr in m.Slots.Keys.ToArray())
        {
            int fo = app.rom.FileOffset(Convert.ToInt32(addr, 16));
            m.Slots[addr] = Convert.ToHexString(app.rom.Data.AsSpan(fo, 8));
        }
        foreach (var t in m.Ext.Keys.ToArray())
        {
            int fo = Map16.DefFileOffset(app.rom, tileset, Convert.ToInt32(t, 16));
            if (fo >= 0) m.Ext[t] = Convert.ToHexString(app.rom.Data.AsSpan(fo, 8));
        }
        if (app.rom.LmActsAsBase > 0)
            foreach (var t in m.ActsAs.Keys.ToArray())
            {
                int fo = app.rom.FileOffset(app.rom.LmActsAsBase + Convert.ToInt32(t, 16) * 2);
                m.ActsAs[t] = app.rom.Data[fo] | (app.rom.Data[fo + 1] << 8);
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
            // Project hydration: a level recorded in the project replaces the ROM-parsed
            // object/sprite state with the project's snapshot.
            var hydrated = app.level is not null ? app.project?.Data.LevelOrNull(app.levelNum) : null;
            if (hydrated is not null)
                app.objList = hydrated.Objects.Select(o => o.ToLevelObject()).ToList();
            var tracked = app.objectEditor.RenderObjectsTracked();        // owner attribution for hit-testing
            // Hydrated levels must render from the object list, not the parsed grid —
            // the parsed grid shows the base ROM's content. (Vanilla-parse keeps the
            // parsed grid: byte-identical, and it survives tracked-render failures.)
            if (hydrated is not null && tracked is not null) app.grid = tracked;
            app.history.Clear();                                          // new grid = new history
            app.selSprites.Clear(); app.selObjs.Clear(); app.dragStart = app.dragEnd = null; app.moveDrag = null; app.resizeDrag = null; app.spriteEditor.DropSpriteGhost(); app.spriteEditor.hiddenSprites = null;
            app.levelGfxPanel.InvalidateLevel();                          // refresh Level GFX window
            if (app.levelNum != app.paletteEditor.palEditsLevel) { app.paletteEditor.palEdits.Clear(); app.paletteEditor.palEditsLevel = app.levelNum; }
            if (hydrated is not null)
            {
                app.paletteEditor.palEdits.Clear();
                foreach (var (k, v) in hydrated.Palette) app.paletteEditor.palEdits[k] = (ushort)v;
                app.paletteEditor.palEditsLevel = app.levelNum;
            }
            // Layer 2: background image or object layer, drawn behind layer 1.
            bgImage = app.rom is not null && app.level is not null ? LevelParser.DecodeBgImage(app.rom, app.levelNum) : null;
            layer2Grid = app.rom is not null && app.level is not null
                ? ObjectEngine.RenderLayer2(app.rom, app.level.Header, app.levelNum) : null;
            app.sprites = app.rom is not null && app.level is not null ? SpriteData.Parse(app.rom, app.levelNum) : null;
            if (hydrated is not null && app.sprites is not null)
            {
                app.sprites = new SpriteData { SpriteMemory = hydrated.SpriteMemory, Buoyancy = hydrated.Buoyancy };
                app.sprites.Sprites.AddRange(hydrated.Sprites.Select(s => s.ToSprite()));
            }
            // Run the expensive OAM captures once per parse; repaints just re-blit.
            app.spriteOverlay = app.rom is not null && app.level is not null && app.sprites is not null
                ? SpriteOverlay.Build(app.rom, app.sprites, app.level.Header, app.levelNum) : null;
            app.canvasFull = true;
            RebuildGraphics();
            app.currentLevelTouched = false;   // freshly parsed/hydrated = in sync with project
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
        app.gfxEditor.InvalidateSheet();         // GFX editor sheet: bytes/palette may have changed
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
        app.levelGfxPanel.Draw(app.rom, app.level, app.levelNum, app.SdlWindowHandle,
                               s => app.saveStatus = s, () =>
        {
            // GFX overrides bypass the history stack — mark project state dirty directly.
            app.currentLevelTouched = true;
            app.project?.MarkDirty();
            app.canvasFull = true; app.levelDirty = true;
            RebuildGraphics();          // tile caches + Map16 sheet + catalogs + canvas
            // Swap the SP tile sheets only — the cached OAM captures stay valid.
            if (app.rom is not null && app.level is not null)
                app.spriteOverlay = app.spriteOverlay?.WithReloadedTiles(app.rom, app.level.Header, app.levelNum);
        },
        // Per-bin "Edit": open the file in the GFX tile editor (canvas mode 3).
        file => { app.gfxEditor.gfxFile = file; app.canvasView = EditorApp.CanvasView.Gfx; });
        ImGui.EndChild();
    }
}
