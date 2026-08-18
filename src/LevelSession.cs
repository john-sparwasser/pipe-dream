using ImGuiNET;

namespace PipeDream;

// ROM/level session management: loading the ROM, parsing the level into edit state,
// recomposing everything palette/GFX-dependent, and feeding the LevelCanvas compositor.
internal sealed class LevelSession(EditorApp app)
{
    private readonly EditorApp app = app;

    private uint backdropColor;
    private ushort[]? bgImage;       // layer-2 background image (BG def indices), else null

    // Both layers live here; app.objList/app.grid point at whichever app.editLayer selects,
    // so the object editor is layer-agnostic. Index 0 = layer 1, 1 = layer 2 (null when the
    // level's layer 2 is a background image and the project hasn't converted it).
    private readonly List<LevelObject>?[] layerObjects = new List<LevelObject>?[2];
    private readonly Map16Grid?[] layerGrid = new Map16Grid?[2];
    private List<LevelObject>? baseLayer2;   // the ROM's layer-2 stream, to diff against on save

    /// <summary>Layer 2 can be edited when the level has (or the project gave it) an object
    /// stream — a background-image layer 2 has no objects to edit.</summary>
    internal bool Layer2Editable => layerObjects[1] is not null;

    /// <summary>The layer-2 object stream exists only because the project converted a
    /// background-image level — so reverting is offered rather than a second conversion.</summary>
    internal bool Layer2FromProject => baseLayer2 is null && layerObjects[1] is not null;

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
            // Replaying the project onto the fresh base is UI-free (ProjectSession.Hydrate),
            // so the second front end and the tests use the very same code.
            if (app.project is not null && ProjectSession.Hydrate(app.rom, app.project.Data) is { } replayErr)
                app.saveStatus = replayErr;
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
        if (app.project is not null && app.currentLevelTouched)
        {
            StashCurrentLevel();
            app.project.Save();   // leaving a level is a natural commit point; don't sit on
        }                         // the debounce and lose the level's edits to a hard crash
        app.levelNum = newLevel;
        ParseLevel();
    }

    /// <summary>Project.SyncBeforeSave hook: flush live editor state into Project.Data
    /// right before project.pdp is written.</summary>
    internal void SyncProject()
    {
        if (app.project is null) return;
        if (app.currentLevelTouched) StashCurrentLevel();
        if (app.rom is null) return;
        LevelEditState.StashRomWide(app.project.Data, app.rom, app.level?.Header.Tileset ?? 1);
    }

    // Write the current level's session state into the project snapshot. The shape of that
    // state, and the rules for what gets recorded, live in LevelEditState — this only gathers
    // the editor's scattered fields into it.
    private void StashCurrentLevel()
    {
        if (app.project is null || app.rom is null) return;
        CurrentEditState().Stash(app.project.Data, app.rom, app.levelNum);
        app.project.MarkDirty();
    }

    /// <summary>The editor's live per-level state, as the UI-free shape the save path takes.</summary>
    internal LevelEditState CurrentEditState()
    {
        var st = new LevelEditState
        {
            Layer1 = layerObjects[0] ?? [],
            Layer2 = layerObjects[1],
            BaseLayer2 = baseLayer2,
            Sprites = app.sprites,
        };
        foreach (var (k, v) in app.paletteEditor.palEdits) st.PaletteEdits[k] = v;
        return st;
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

    /// <summary>Render one layer's grid from an object list — the untracked counterpart of
    /// ObjectEditor.RenderObjectsTracked, used for whichever layer isn't being edited.</summary>
    private Map16Grid? RenderLayerGrid(int layer, List<LevelObject> objs)
    {
        if (app.rom is null || app.level is null) return null;
        try
        {
            byte[] enc = LevelEncoder.Encode(app.level, LevelEncoder.NormalizeStream(objs));
            return ObjectEngine.RenderEmulatedStream(app.rom, app.level.Header, enc, layer);
        }
        catch { return null; }
    }

    /// <summary>Switch which layer the object editor edits. Both layers' objects already
    /// live here, so this only re-points app.objList/app.grid and re-renders.
    /// ponytail: clears undo — the history closures capture the other layer's list, and
    /// replaying one into the other would corrupt it.</summary>
    internal void SetEditLayer(int layer)
    {
        if (app.rom is null || app.level is null || layer == app.editLayer) return;
        if (layer == 1 && layerObjects[1] is null) return;
        layerGrid[app.editLayer] = app.grid;
        app.editLayer = layer;
        app.objList = layerObjects[layer];
        app.grid = layerGrid[layer];
        app.baseGrid = app.grid?.Clone();
        app.selObjs.Clear();
        app.dragStart = app.dragEnd = null; app.moveDrag = null; app.resizeDrag = null;
        app.history.Clear();
        if (app.objectEditor.RenderObjectsTracked() is { } g) { app.grid = g; layerGrid[layer] = g; }
        app.canvasFull = true;
        BuildLevelCanvas();
        // Which layer is active changes what every click does, so say so rather than relying
        // on the button tint alone.
        app.saveStatus = $"editing layer {layer + 1} ({app.objList?.Count ?? 0} objects)";
    }

    /// <summary>Give a BACKGROUND-IMAGE level an (empty) layer-2 object stream, or drop ours
    /// and go back to the base ROM's background. The mode IS the pointer's bank byte ($FF =
    /// background), so a non-null project list is the whole conversion — no mode flag needed.
    ///
    /// The reverse direction (turning a level that ships an object layer into a background
    /// one) is NOT offered: it needs a background-image id to point at, which this schema
    /// has nowhere to keep. Only "revert to the base ROM's layer 2" is possible there.</summary>
    /// <summary>Point this level's layer 2 at a background image, dropping any object stream —
    /// the two modes are exclusive. Only addresses the ROM already uses are offered, because a
    /// background's page byte comes from its address, so it cannot be moved without
    /// recolouring it. This is also the object-layer → background direction the layer-2
    /// object work had nowhere to store.</summary>
    internal void SetLayer2Background(int lo16)
    {
        if (app.rom is null || app.level is null || app.project is null) return;
        if (app.currentLevelTouched) StashCurrentLevel();
        var s = app.project.Data.Level(app.levelNum);
        s.Layer2Background = lo16 & 0xFFFF;
        s.Layer2Objects = null;
        // The session ROM has to agree with the project, or the canvas keeps showing the old
        // layer 2 until the next Build.
        app.rom.SetLayer2Pointer(app.levelNum, 0xFF0000 | (lo16 & 0xFFFF));
        if (app.editLayer == 1) app.editLayer = 0;
        app.project.MarkDirty();
        ParseLevel();
        app.currentLevelTouched = true;
        app.saveStatus = $"layer 2 ← background ${lo16 & 0xFFFF:X4} (page {BgImage.PageFor(lo16)})";
    }

    internal void SetLayer2ObjectMode(bool objectMode)
    {
        if (app.rom is null || app.level is null) return;
        if (objectMode == layerObjects[1] is not null) return;
        if (!objectMode && app.editLayer == 1) app.editLayer = 0;
        if (app.project is not null)
        {
            // Persist BEFORE reparsing — ParseLevel re-hydrates layer 2 from the project,
            // so setting the list here and reparsing after is what makes the change stick.
            if (app.currentLevelTouched) StashCurrentLevel();
            var st = app.project.Data.Level(app.levelNum);
            st.Layer2Objects = objectMode ? new List<ProjectFile.ObjectDto>() : null;
            // The two modes are exclusive, and Layer2Background wins in the builder — so
            // converting to an object layer has to drop any background selection.
            if (objectMode) st.Layer2Background = null;
            app.project.MarkDirty();
            ParseLevel();
            app.currentLevelTouched = true;
            return;
        }
        // No project open: session-only, nothing to hydrate from.
        layerObjects[1] = objectMode ? new List<LevelObject>() : null;
        app.objList = layerObjects[app.editLayer];
        app.canvasFull = true;
        RebuildGraphics();
    }

    internal void ParseLevel()
    {
        try
        {
            app.level = app.rom is null ? null : LevelParser.Parse(app.rom, app.levelNum);
            app.grid = app.rom is not null && app.level is not null ? ObjectEngine.Render(app.rom, app.level) : null;
            app.baseGrid = app.grid?.Clone();          // snapshot to diff edits against on save
            layerObjects[0] = app.level is not null ? new List<LevelObject>(app.level.Objects) : null;
            // Project hydration: a level recorded in the project replaces the ROM-parsed
            // object/sprite state with the project's snapshot.
            var hydrated = app.level is not null ? app.project?.Data.LevelOrNull(app.levelNum) : null;
            if (hydrated is not null)
                layerObjects[0] = hydrated.Objects.Select(o => o.ToLevelObject()).ToList();

            // Layer 2's object stream, when the level has one. The project's copy wins, and
            // a project list on a background-image level IS the conversion to object mode.
            baseLayer2 = app.rom is not null && app.level is not null
                ? LevelParser.ParseLayer2(app.rom, app.levelNum) : null;
            layerObjects[1] = hydrated?.Layer2Objects is { } pl2
                ? pl2.Select(o => o.ToLevelObject()).ToList()
                : baseLayer2 is not null ? new List<LevelObject>(baseLayer2) : null;
            if (app.editLayer == 1 && layerObjects[1] is null) app.editLayer = 0;

            app.objList = layerObjects[app.editLayer];
            var tracked = app.objectEditor.RenderObjectsTracked();        // owner attribution for hit-testing
            // Hydrated levels must render from the object list, not the parsed grid —
            // the parsed grid shows the base ROM's content. (Vanilla-parse keeps the
            // parsed grid: byte-identical, and it survives tracked-render failures.)
            if ((hydrated is not null || app.editLayer != 0) && tracked is not null) app.grid = tracked;
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
            // Layer 2: background image, or the object layer drawn behind layer 1 — never
            // both. A project that gave this level an object stream overrides a base ROM
            // pointer still saying "background", which the session ROM keeps until a build.
            bgImage = app.rom is not null && app.level is not null && layerObjects[1] is null
                ? LevelParser.DecodeBgImage(app.rom, app.levelNum) : null;
            layerGrid[app.editLayer] = app.grid;
            int other = 1 - app.editLayer;
            layerGrid[other] = layerObjects[other] is { } otherObjs
                ? RenderLayerGrid(other, otherObjs) : null;
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
        // The layer being edited draws in front; the other one sits behind it, so switching
        // to layer 2 shows it over layer 1 rather than buried under it.
        return new CanvasScene(app.tileCaches, backdropColor, app.grid, bgImage, app.bgCaches,
            layerGrid[1 - app.editLayer], visRows,
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
            app.gfxBrowser.Invalidate();   // an import added a file the browser should show
            RebuildGraphics();          // tile caches + Map16 sheet + catalogs + canvas
            // Swap the SP tile sheets only — the cached OAM captures stay valid.
            if (app.rom is not null && app.level is not null)
                app.spriteOverlay = app.spriteOverlay?.WithReloadedTiles(app.rom, app.level.Header, app.levelNum);
        },
        // Per-bin "Edit": open the file in the GFX tile editor (canvas mode 3).
        file => { app.gfxEditor.gfxFile = file; app.canvasView = EditorApp.CanvasView.Gfx; },
        // Per-bin "Browse…": pick a file visually, then assign it to that bin through the
        // same override path typing an id uses.
        bypWord => app.gfxBrowser.Open("Select GFX for this bin", picked =>
        {
            if (app.rom is null) return;
            app.rom.GfxSlotOverrides[(app.levelNum, bypWord)] = picked;
            app.levelGfxPanel.InvalidateLevel();
            app.currentLevelTouched = true;
            app.project?.MarkDirty();
            app.canvasFull = true; app.levelDirty = true;
            RebuildGraphics();
            if (app.level is not null)
                app.spriteOverlay = app.spriteOverlay?.WithReloadedTiles(app.rom, app.level.Header, app.levelNum);
            app.saveStatus = $"bin ← GFX{picked:X3}"
                           + (app.rom.GfxName(picked) is { Length: > 0 } n ? $" \"{n}\"" : "");
        }));
        ImGui.EndChild();
    }
}
