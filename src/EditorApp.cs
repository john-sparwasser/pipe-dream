using System.Numerics;
using System.Runtime.InteropServices;
using Foster.Framework;
using ImGuiNET;

namespace PipeDream;

/// <summary>
/// Editor shell. Layout paradigm: one MAIN VIEW fills the window (the level, by default),
/// with a hideable LEFT PALETTE that feeds it — what you pick in the palette is what you
/// paint in the main view. For the level view the palette tabs are Map16 (default),
/// Sprites, and Objects. Future main views (background editor, layer 3 editor, Map16
/// editor) will swap into the same main region. Auxiliary inspectors (ROM info, GFX
/// viewers) are floating windows behind the File/View menus.
/// </summary>
public partial class EditorApp : App
{
    private ImGuiLayer? imgui;

    // Loaded ROM (null until File → Open ROM).
    private string? loadedRomPath;
    private Rom? rom;
    private int ratCount;
    private int levelNum = 0x105;   // Yoshi's Island 2
    private Level? level;
    private Map16Grid? grid;

    // Read-only inspector windows (ROM info / GFX viewers), created in Startup.
    private DebugPanels panels = null!;

    // Composed Map16 sheet, one texture per animation phase (CONTRACT §12). The level
    // canvas itself is owned by the LevelCanvas compositor.
    private readonly Texture?[] map16Texs = new Texture?[4];
    private int map16W, map16H;
    private LevelCanvas canvas = null!;    // created in Startup (needs GraphicsDevice)
    private bool animateTiles = true;
    // Current animation phase: the game advances every 8 frames at 60fps (~133 ms).
    // Wall-clock based (NOT ImGui.GetTime) — this is read outside the ImGui layout
    // window, where no ImGui context is current and igGetTime crashes.
    private int AnimPhase => animateTiles ? (int)(Environment.TickCount64 / 133) & 3 : 0;

    // Edit state
    private uint[][][]? tileCaches;  // [phase][map16 tile] composed 16x16 tiles
    private uint backdropColor;
    private int selectedMap16 = 0x100;
    private bool levelDirty;
    private Map16Grid? baseGrid;     // object-engine output before edits, to diff against on save
    private ushort[]? bgImage;       // layer-2 background image (BG def indices), else null
    private uint[][][]? bgCaches;    // [phase] composed BG Map16 tiles for the background image
    private Map16Grid? layer2Grid;   // layer-2 object layer, else null
    private SpriteData? sprites;     // sprite list for the overlay
    private SpriteOverlay? spriteOverlay;   // cached OAM captures; Draw() is cheap blits
    private bool showSprites = true;
    // Sprite catalog (all insertable sprite numbers), LM-style "loaded only" filter.
    private Texture? catThumbTex;    // catalog thumbnail atlas
    private int[] catNumbers = Array.Empty<int>();
    private int[] levelSpFiles = new int[4];
    private bool catalogLoadedOnly = true;
    private int selectedCatalog = -1;

    // canvasFull forces the full compose path (vs incremental dirty-cell) on the next flush.
    private bool canvasFull = true;

    // Layout state
    private bool paletteVisible = true;
    private readonly HashSet<int> selSprites = new();   // selected sprite indices (sprite mode)
    private Texture? sprGhostTex;                       // drag ghost: the selected sprites' pixels
    private int sprGhostW, sprGhostH, sprGhostX, sprGhostY;   // ghost size + level-px origin
    private HashSet<int>? hiddenSprites;                // sprites hidden from the canvas mid-drag

    // Object editing (Objects mode): a mutable working copy of the level's objects, its
    // own render, and a placeable-object catalog.
    private List<LevelObject>? objList;
    private readonly HashSet<int> selObjs = new();      // selected indices into objList
    private Texture? objCatTex;                          // catalog thumbnail atlas
    private int[] objCatNums = Array.Empty<int>();
    private readonly Dictionary<int, (int cw, int ch, float u0, float v0, float u1, float v1)> objCatUV = new();
    private int objCatTileset = -1;                      // tileset the catalog was built for
    private int selectedObjCat = -1;                     // catalog object number to place
    private const int ObjDefaultSize = 0x22;             // default placed size: 3 wide x 3 tall

    // LM-style editing: left-click/drag selects (grabs the tiles under the cursor as the
    // brush), right-click stamps the brush, Delete erases the selection, Esc toggles
    // between Layer 1 and sprite selection modes.
    private enum EditMode { Layer1, Sprites, Objects }
    private EditMode editMode = EditMode.Layer1;
    private EditTool tileTool = null!, spriteTool = null!, objectTool = null!;   // set in Startup
    private EditTool ActiveTool => editMode switch
    { EditMode.Sprites => spriteTool, EditMode.Objects => objectTool, _ => tileTool };
    private ushort[] brushTiles = { 0x100 };
    private int brushW = 1, brushH = 1;
    private (int x, int y, int w, int h)? selRect;   // last grab, for highlight + Delete
    private (int x, int y)? dragStart, dragEnd;      // left-drag rubber band (cells)
    private (int x, int y)? moveDrag;                // anchor cell: dragging the selection moves it

    // Palette editor: CGRAM index -> edited BGR555, applied over the ROM palette while
    // rendering. In-session only for now (no ROM save path yet); cleared on level change.
    private readonly Dictionary<int, ushort> palEdits = new();
    private int palEditsLevel = -1;
    private int palDirtyRebuild = -1;   // swatch whose picker changed; rebuild when its popup closes
    private Dictionary<int, ushort>? palBeforePicker;   // palEdits snapshot at picker open (undo)

    // Undo/redo via a generic command stack; each Push* below captures the domain-specific
    // undo/redo closures. `currentStroke` buffers a tile paint until the mouse releases.
    private readonly EditHistory history = new();
    private List<(int x, int y, ushort before, ushort after)> currentStroke = new();
    private string saveStatus = "";
    private const float Map16Zoom = 1f;   // tile picker (16px tiles at native size)
    private const float CanvasZoom = 1f;  // level canvas (native size)

    // The UI runs at ImGuiLayer.Scale (BaseScale x display scale), so a zoom of 1 unit may be
    // a fractional number of physical pixels (e.g. 2.5 at 125% Windows scaling) — that
    // resampling is visible on pixel art. Snap so texel -> physical pixels is an integer,
    // and anchor draws on whole physical pixels.
    private float SnappedZoom(float desired)
    {
        float s = imgui!.Scale;
        return MathF.Max(1f, MathF.Round(desired * s)) / s;
    }

    private void SnapCursorToPixel()
    {
        float s = imgui!.Scale;
        var p = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(MathF.Floor(p.X * s) / s, MathF.Floor(p.Y * s) / s));
    }

    public EditorApp() : base(new AppConfig
    {
        ApplicationName = "PipeDream",
        WindowTitle = "Pipe Dream — SMW Editor",
        Width = 1280,
        Height = 720,
        Resizable = true,
        // Windows: D3D12 backend rejects the ImGui material's uniform buffer
        // (Foster 0.3.0 / SDL3 limitation). Vulkan works. Mac/Linux keep default.
        PreferredGraphicsDriver = OperatingSystem.IsWindows()
            ? GraphicsDriver.Vulkan
            : GraphicsDriver.None,
    })
    {
    }

    protected override void Startup()
    {
        imgui = new ImGuiLayer(this) { BaseScale = 1.5f };
        canvas = new LevelCanvas(GraphicsDevice);
        tileTool = new TileTool(this); spriteTool = new SpriteTool(this); objectTool = new ObjectTool(this);
        panels = new DebugPanels(GraphicsDevice, imgui);
        SetWindowIcon();
    }

    // Foster 0.3.0 has no icon API; go straight to SDL with the embedded pipe icon.
    // (The exe/taskbar icon comes from assets/pipe-dream.ico via <ApplicationIcon>.)
    private void SetWindowIcon()
    {
        try
        {
            using var s = GetType().Assembly.GetManifestResourceStream("pipe-icon.png");
            if (s is null) return;
            using var img = new Image(s);
            // Window.Handle (the SDL_Window*) is internal in Foster 0.3.0.
            var handleProp = typeof(Window).GetProperty("Handle",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            if (handleProp?.GetValue(Window) is not IntPtr win || win == IntPtr.Zero) return;
            unsafe
            {
                var surf = SDL3.SDL.SDL_CreateSurfaceFrom(img.Width, img.Height,
                    SDL3.SDL.SDL_PixelFormat.SDL_PIXELFORMAT_RGBA32, img.Pointer, img.Width * 4);
                if (surf is null) return;
                SDL3.SDL.SDL_SetWindowIcon(win, (IntPtr)surf);
                SDL3.SDL.SDL_DestroySurface((IntPtr)surf);
            }
        }
        catch { /* cosmetic only */ }
    }

    protected override void Shutdown()
    {
        imgui?.Dispose();
        canvas?.Dispose();
        panels?.Dispose();
    }

    protected override void Update()
    {
        if (imgui is null) return;

        // Apply pending edits before layout so we never dispose a texture mid-frame.
        if (levelDirty)
        {
            if (!canvasFull && canvas.DirtyCount > 0 && canvas.HasImages) ApplyDirtyCells();
            else BuildLevelCanvas();
            levelDirty = false;
        }
        canvas.RefreshPhase(AnimPhase);   // lazy re-upload when the animation reaches a stale phase

        imgui.BeginLayout();
        DrawUI();
        imgui.EndLayout();
    }

    protected override void Render()
    {
        Window.Clear(0x1e1e1eff);
        imgui?.Render();
    }

    private void DrawUI()
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Open ROM…"))
                {
                    // ponytail: hardcoded to the known test ROM until a file dialog exists.
                    LoadRom(@"C:\SMW\Projects\.resources\SMW.smc");
                }
                if (ImGui.MenuItem("Open ROM (expanded test)…"))
                {
                    LoadRom(@"C:\SMW\Projects\ShaoBase\base.smc");
                }
                if (ImGui.MenuItem("Open DM16 test ROM…"))
                {
                    LoadRom(@"C:\SMW\Projects\.resources\after.smc");
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Save DM16 edits to ROM copy", rom is not null && level is not null))
                    SaveEdits();
                if (ImGui.MenuItem("Save palette to ROM copy", rom is not null && level is not null))
                    SavePalette();
                ImGui.Separator();
                if (ImGui.MenuItem("ROM Info", "", panels.ShowRomInfo)) panels.ShowRomInfo = !panels.ShowRomInfo;
                ImGui.Separator();
                if (ImGui.MenuItem("Exit")) Exit();
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Edit"))
            {
                if (ImGui.MenuItem("Undo", "Ctrl+Z", false, history.CanUndo || currentStroke.Count > 0)) Undo();
                if (ImGui.MenuItem("Redo", "Ctrl+Shift+Z", false, history.CanRedo)) Redo();
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("View"))
            {
                if (ImGui.MenuItem("Palette", "", paletteVisible)) paletteVisible = !paletteVisible;
                ImGui.Separator();
                if (ImGui.MenuItem("Sprite overlay", "", showSprites))
                { showSprites = !showSprites; canvasFull = true; levelDirty = true; }
                if (ImGui.MenuItem("Animate tiles", "", animateTiles))
                    animateTiles = !animateTiles;
                ImGui.Separator();
                if (ImGui.MenuItem("GFX Viewer", "", panels.ShowGfxViewer)) panels.ShowGfxViewer = !panels.ShowGfxViewer;
                if (ImGui.MenuItem("Level GFX", "", panels.ShowLevelGfx)) panels.ShowLevelGfx = !panels.ShowLevelGfx;
                ImGui.EndMenu();
            }
            ImGui.EndMainMenuBar();
        }

        // A paint stroke ends when neither paint button is held.
        if (currentStroke.Count > 0 &&
            !ImGui.IsMouseDown(ImGuiMouseButton.Left) && !ImGui.IsMouseDown(ImGuiMouseButton.Right))
            CommitStroke();

        var io = ImGui.GetIO();
        if (io.KeyCtrl && !io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Z, true))
        {
            if (io.KeyShift) Redo(); else Undo();
        }
        // Esc cycles Layer 1 <-> sprite selection (unless it's closing a popup).
        if (!io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Escape) &&
            !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
        {
            editMode = editMode switch
            {
                EditMode.Layer1 => EditMode.Sprites,
                EditMode.Sprites => EditMode.Objects,
                _ => EditMode.Layer1,
            };
            dragStart = null; moveDrag = null;
            DropSpriteGhost();
            ClearHiddenSprites();
            // Bring the matching palette tab along.
            pendingTabSelect = paletteTab = editMode switch
            { EditMode.Sprites => 1, EditMode.Objects => 2, _ => 0 };
        }

        DrawMainLayout();
        panels.DrawRomInfo(rom, loadedRomPath, ratCount);
        panels.DrawGfxViewer(rom, level?.Header, levelNum);
        panels.DrawLevelGfx(rom, level, levelNum);
    }

    // Fixed shell: left palette (hideable, resizable) + main view fill the whole work area.
    private void DrawMainLayout()
    {
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.WorkPos);
        ImGui.SetNextWindowSize(vp.WorkSize);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4, 4));
        ImGui.Begin("##shell", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                               ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse |
                               ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus |
                               ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar(2);
        if (paletteVisible)
        {
            ImGui.BeginChild("palette", new Vector2(320, 0),
                             ImGuiChildFlags.ResizeX | ImGuiChildFlags.Border);
            DrawPalette();
            ImGui.EndChild();
            ImGui.SameLine();
        }
        ImGui.BeginChild("mainview");
        DrawLevelView();
        ImGui.EndChild();
        ImGui.End();
    }

    // The left palette: pickers that feed the main view. Tabs per palette kind.
    // Selecting a tab switches the canvas edit mode (Sprites tab -> sprite mode,
    // Map16/Objects -> layer 1); Esc's mode toggle selects the matching tab back.
    private int paletteTab;             // 0 Map16, 1 Sprites, 2 Objects, 3 Palette
    private int pendingTabSelect = -1;  // tab to force-select (mode changed via Esc)

    private void DrawPalette()
    {
        if (ImGui.BeginTabBar("palettetabs"))
        {
            PaletteTabItem(0, "Map16", EditMode.Layer1, DrawMap16Tab);
            PaletteTabItem(1, "Sprites", EditMode.Sprites, DrawSpritesTab);
            PaletteTabItem(2, "Objects", EditMode.Objects, DrawObjectsTab);
            PaletteTabItem(3, "Palette", null, DrawPaletteTab);
            ImGui.EndTabBar();
        }
        pendingTabSelect = -1;
    }

    private void PaletteTabItem(int idx, string label, EditMode? mode, Action draw)
    {
        var flags = pendingTabSelect == idx ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        // ImGui.NET has no BeginTabItem(label, flags) overload (only ref-bool with a
        // close button) — call the native one with p_open = null.
        bool open;
        unsafe
        {
            int len = System.Text.Encoding.UTF8.GetByteCount(label);
            Span<byte> buf = stackalloc byte[len + 1];
            System.Text.Encoding.UTF8.GetBytes(label, buf);
            buf[len] = 0;
            fixed (byte* p = buf) open = ImGuiNative.igBeginTabItem(p, null, flags) != 0;
        }
        if (!open) return;
        if (paletteTab != idx)
        {
            paletteTab = idx;
            if (mode is { } m && editMode != m)
            {
                editMode = m;
                dragStart = dragEnd = null; moveDrag = null;
                DropSpriteGhost();
            }
        }
        draw();
        ImGui.EndTabItem();
    }

    // The main view: the composed level (Map16 grid with real tile graphics).
    private void DrawLevelView()
    {
        if (rom is null) { ImGui.TextDisabled("No ROM loaded.  File → Open ROM to begin."); return; }
        ImGui.SetNextItemWidth(120);
        unsafe
        {
            int v = levelNum, step = 1;
            if (ImGui.InputScalar("Level", ImGuiDataType.S32, (IntPtr)(&v), (IntPtr)(&step),
                                  IntPtr.Zero, "%03X", ImGuiInputTextFlags.CharsHexadecimal))
            {
                levelNum = Math.Clamp(v, 0, Rom.LevelCount - 1);
                ParseLevel();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Reload")) ParseLevel();
        ImGui.SameLine();
        if (ImGui.Button("GFX")) panels.ShowLevelGfx = !panels.ShowLevelGfx;
        if (canvas.TexFor(0) is null || grid is null) { ImGui.TextDisabled("No level rendered."); return; }
        ImGui.SameLine();
        ImGui.Text(ActiveTool.Hint);
        if (saveStatus.Length > 0) ImGui.TextDisabled(saveStatus);
        // Horizontal levels scroll left/right with the wheel (Shift+wheel = vertical);
        // vertical levels keep the default up/down wheel.
        bool verticalLvl = rom is not null && level is not null && rom.IsVerticalMode(level.Header.LevelMode);
        var canvasFlags = ImGuiWindowFlags.HorizontalScrollbar |
                          (verticalLvl ? 0 : ImGuiWindowFlags.NoScrollWithMouse);
        if (ImGui.BeginChild("lvlcanvas", System.Numerics.Vector2.Zero, 0, canvasFlags))
        {
            float z = SnappedZoom(CanvasZoom);
            if (!verticalLvl && ImGui.IsWindowHovered())
            {
                float wheel = ImGui.GetIO().MouseWheel;
                if (wheel != 0)
                {
                    float step = wheel * 64 * z;
                    if (ImGui.GetIO().KeyShift) ImGui.SetScrollY(ImGui.GetScrollY() - step);
                    else ImGui.SetScrollX(ImGui.GetScrollX() - step);
                }
            }
            SnapCursorToPixel();
            var origin = ImGui.GetCursorScreenPos();
            ImGui.Image(imgui!.GetTextureID(canvas.TexFor(AnimPhase)!), new Vector2(canvas.PxW * z, canvas.PxH * z));
            float cs = 16 * z;
            var dl = ImGui.GetWindowDrawList();

            // Hand the frame to the active tool: it owns highlights + all interaction.
            int hcx = 0, hcy = 0;
            bool hovered = false;
            if (ImGui.IsItemHovered())
            {
                var m = ImGui.GetMousePos();
                hcx = (int)((m.X - origin.X) / cs); hcy = (int)((m.Y - origin.Y) / cs);
                hovered = hcx >= 0 && hcx < grid.Width && hcy >= 0 && hcy < grid.Height;
            }
            ActiveTool.Frame(new CanvasCtx(origin, cs, dl, hcx, hcy, hovered, verticalLvl));
            ImGui.EndChild();
        }
    }

    private void PaintCell(int x, int y, int tile)
    {
        int before = grid!.Get(x, y);
        if (before == tile) return;
        currentStroke.Add((x, y, (ushort)before, (ushort)tile));
        grid.Set(x, y, tile);
        canvas.MarkDirty(x, y);
        levelDirty = true;
    }

    // ---- sprite editing (sprite mode) ----

    /// <summary>Construct a sprite at a display cell (inverse of Sprite.Cell).</summary>
    private static Sprite SpriteAt(int number, int extra, int cx, int cy, bool vert, byte[]? extraBytes = null)
    {
        int abs = vert ? cy : cx, y = vert ? cx : cy;
        return new Sprite(Screen: (abs >> 4) & 0x1F, XNibble: abs & 15, Y: y & 0x1F,
                          Extra: extra, Number: number, ExtraBytes: extraBytes);
    }

    private int? SpriteIndexAt(int cx, int cy, bool vert)
    {
        if (sprites is null) return null;
        for (int i = 0; i < sprites.Sprites.Count; i++)
            if (sprites.Sprites[i].Cell(vert) == (cx, cy)) return i;
        return null;
    }

    // A sprite changed at (around) this cell: recompose its neighborhood on next flush.
    private void MarkSpriteCells(int cx, int cy)
    {
        for (int dy = -2; dy <= 4; dy++)
            for (int dx = -2; dx <= 4; dx++)
                canvas.MarkDirty(cx + dx, cy + dy);
    }

    private void RebuildSpriteOverlay()
    {
        spriteOverlay = rom is not null && level is not null && sprites is not null
            ? SpriteOverlay.Build(rom, sprites, level.Header, levelNum) : null;
        levelDirty = true;
    }

    // Compose the selected sprites into one texture for the drag ghost (built when the
    // move starts, disposed when it ends).
    private void BuildSpriteGhost()
    {
        DropSpriteGhost();
        if (spriteOverlay is null || selSprites.Count == 0) return;
        try
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (int i in selSprites)
                if (spriteOverlay.PixelBounds(i) is { } b)
                {
                    minX = Math.Min(minX, b.MinX); minY = Math.Min(minY, b.MinY);
                    maxX = Math.Max(maxX, b.MaxX); maxY = Math.Max(maxY, b.MaxY);
                }
            if (minX > maxX) return;                      // badge-only selection: no ghost
            int W = maxX - minX, H = maxY - minY;
            var img = new uint[W * H];
            var pal = EditedPalette(0)!;
            foreach (int i in selSprites) spriteOverlay.DrawOne(i, img, W, H, pal, -minX, -minY);
            sprGhostTex = new Texture(GraphicsDevice, W, H, MemoryMarshal.AsBytes(img.AsSpan()));
            sprGhostW = W; sprGhostH = H; sprGhostX = minX; sprGhostY = minY;
        }
        catch { DropSpriteGhost(); }
    }

    private void DropSpriteGhost()
    {
        sprGhostTex?.Dispose(); sprGhostTex = null;
    }

    // Un-hide sprites that were suppressed during a drag (their cells recompose).
    private void ClearHiddenSprites()
    {
        if (hiddenSprites is null) return;
        if (sprites is not null)
        {
            bool vert = rom is not null && level is not null && rom.IsVerticalMode(level.Header.LevelMode);
            foreach (int i in hiddenSprites)
                if (i < sprites.Sprites.Count)
                {
                    var (cx, cy) = sprites.Sprites[i].Cell(vert);
                    MarkSpriteCells(cx, cy);
                }
        }
        hiddenSprites = null;
        levelDirty = true;
    }

    // ---- object editing (Objects mode) ----

    // Re-render the level grid from the edited object list, preserving any DM16 tile-paint
    // overlay (the cells where the display grid differs from the object-render baseline).
    private void RenderObjects()
    {
        if (rom is null || level is null || objList is null || grid is null || baseGrid is null) return;
        var overlay = new List<(int x, int y, ushort t)>();
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
            {
                int t = grid.Get(x, y);
                if (t != baseGrid.Get(x, y)) overlay.Add((x, y, (ushort)t));
            }
        try
        {
            var g = ObjectEngine.RenderEmulatedStream(rom, level.Header, level.Encode(rom, Level.NormalizeStream(objList)), 0);
            baseGrid = g;
            grid = g.Clone();
            foreach (var (x, y, t) in overlay) grid.Set(x, y, t);
        }
        catch { return; }
        canvasFull = true;
        levelDirty = true;
    }

    // A rough footprint for hit-testing/selection: the object's declared W×H rect
    // (extended objects are single-cell). Not pixel-exact for irregular objects.
    private (int x, int y, int w, int h) ObjRect(LevelObject o)
    {
        int w = o.Extended || o.IsScreenExit ? 1 : Math.Clamp(o.Width, 1, 32);
        int h = o.Extended || o.IsScreenExit ? 1 : Math.Clamp(o.Height, 1, 32);
        return (o.AbsoluteX, o.Y, w, h);
    }

    private int? ObjIndexAt(int cx, int cy)
    {
        if (objList is null) return null;
        // Topmost (last-drawn) object whose rect contains the cell.
        for (int i = objList.Count - 1; i >= 0; i--)
        {
            var (x, y, w, h) = ObjRect(objList[i]);
            if (cx >= x && cx < x + w && cy >= y && cy < y + h) return i;
        }
        return null;
    }

    private static LevelObject ObjAt(LevelObject src, int cx, int cy)
        => new(src.NewScreen, src.Number, (cx >> 4) & 0x1F, cx & 15, cy & 0x1F,
               src.Byte3, src.ExtraByte, src.Dm16Tile, src.Dm16Page, src.Dm16ExtX, src.Dm16ExtH);

    private void MarkAllDirty() { canvasFull = true; levelDirty = true; }

    private void PlaceObject(int number, int cx, int cy)
    {
        if (objList is null) return;
        var before = new List<LevelObject>(objList);
        objList.Add(new LevelObject(false, number, (cx >> 4) & 0x1F, cx & 15, cy & 0x1F, ObjDefaultSize, -1));
        PushObjectEdit(before);
        RenderObjects();
    }

    private void MoveSelectedObjects(int dx, int dy)
    {
        if (objList is null) return;
        var before = new List<LevelObject>(objList);
        foreach (int i in selObjs)
        {
            var o = objList[i];
            objList[i] = ObjAt(o, Math.Max(0, o.AbsoluteX + dx), Math.Clamp(o.Y + dy, 0, 0x1F));
        }
        PushObjectEdit(before);
        RenderObjects();
    }

    private void DuplicateSelectedObjects(int cx, int cy)
    {
        if (objList is null || selObjs.Count == 0) return;
        var before = new List<LevelObject>(objList);
        int ax = selObjs.Min(i => objList[i].AbsoluteX), ay = selObjs.Min(i => objList[i].Y);
        var added = new List<int>();
        foreach (int i in selObjs.OrderBy(i => i))
        {
            var o = objList[i];
            added.Add(objList.Count);
            objList.Add(ObjAt(o, Math.Max(0, cx + o.AbsoluteX - ax), Math.Clamp(cy + o.Y - ay, 0, 0x1F)));
        }
        selObjs.Clear();
        foreach (int i in added) selObjs.Add(i);
        PushObjectEdit(before);
        RenderObjects();
    }

    private void DeleteSelectedObjects()
    {
        if (objList is null || selObjs.Count == 0) return;
        var before = new List<LevelObject>(objList);
        foreach (int i in selObjs.OrderByDescending(i => i)) objList.RemoveAt(i);
        selObjs.Clear();
        PushObjectEdit(before);
        RenderObjects();
    }

    private void PlaceSprite(int number, int cx, int cy, bool vert)
    {
        if (sprites is null) return;
        var before = new List<Sprite>(sprites.Sprites);
        sprites.Sprites.Add(SpriteAt(number, 0, cx, cy, vert));
        MarkSpriteCells(cx, cy);
        PushSpriteEdit(before);
        RebuildSpriteOverlay();
    }

    private void MoveSelectedSprites(int dx, int dy, bool vert)
    {
        if (sprites is null) return;
        var before = new List<Sprite>(sprites.Sprites);
        foreach (int i in selSprites)
        {
            var s = sprites.Sprites[i];
            var (cx, cy) = s.Cell(vert);
            MarkSpriteCells(cx, cy);
            sprites.Sprites[i] = SpriteAt(s.Number, s.Extra, cx + dx, cy + dy, vert, s.ExtraBytes);
            MarkSpriteCells(cx + dx, cy + dy);
        }
        PushSpriteEdit(before);
        RebuildSpriteOverlay();
    }

    // Duplicate the selection with its top-left-most cell at the cursor; the copies
    // become the new selection (LM-style stamp-and-continue).
    private void DuplicateSelection(int cx, int cy, bool vert)
    {
        if (sprites is null || selSprites.Count == 0) return;
        var before = new List<Sprite>(sprites.Sprites);
        var cells = selSprites.Select(i => (i, cell: sprites.Sprites[i].Cell(vert))).ToList();
        int ax = cells.Min(c => c.cell.X), ay = cells.Min(c => c.cell.Y);
        var added = new List<int>();
        foreach (var (i, cell) in cells)
        {
            var s = sprites.Sprites[i];
            int nx = cx + cell.X - ax, ny = cy + cell.Y - ay;
            added.Add(sprites.Sprites.Count);
            sprites.Sprites.Add(SpriteAt(s.Number, s.Extra, nx, ny, vert, s.ExtraBytes));
            MarkSpriteCells(nx, ny);
        }
        selSprites.Clear();
        foreach (int i in added) selSprites.Add(i);
        PushSpriteEdit(before);
        RebuildSpriteOverlay();
    }

    private void DeleteSelectedSprites(bool vert)
    {
        if (sprites is null || selSprites.Count == 0) return;
        var before = new List<Sprite>(sprites.Sprites);
        foreach (int i in selSprites.OrderByDescending(i => i))
        {
            var (cx, cy) = sprites.Sprites[i].Cell(vert);
            MarkSpriteCells(cx, cy);
            sprites.Sprites.RemoveAt(i);
        }
        selSprites.Clear();
        PushSpriteEdit(before);
        RebuildSpriteOverlay();
    }

    // Copy a level region into the brush (LM-style: what you select is what you stamp).
    // A 1x1 grab also syncs the Map16 palette selection.
    private void GrabSelection(int x, int y, int w, int h)
    {
        if (grid is null) return;
        selRect = (x, y, w, h);
        brushW = w; brushH = h;
        brushTiles = new ushort[w * h];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                brushTiles[j * w + i] = (ushort)grid.Get(x + i, y + j);
        if (w == 1 && h == 1 && brushTiles[0] != Map16Grid.Empty && (brushTiles[0] & ObjectEngine.Marker) == 0)
            selectedMap16 = brushTiles[0];
    }

    // Stamp the brush with its top-left at the given cell (empty brush cells erase,
    // faithful to a copied region). Runs on right-drag; PaintCell dedups no-ops and the
    // stroke system groups the whole drag into one undo step.
    private void StampBrush(int cx, int cy)
    {
        if (grid is null) return;
        for (int j = 0; j < brushH; j++)
            for (int i = 0; i < brushW; i++)
            {
                int x = cx + i, y = cy + j;
                if (x < grid.Width && y < grid.Height) PaintCell(x, y, brushTiles[j * brushW + i]);
            }
    }

    // A stroke ends when no paint button is held; committing makes it one undo step.
    private void CommitStroke()
    {
        if (currentStroke.Count == 0) return;
        var cells = currentStroke;
        currentStroke = new();
        history.Push(
            undoAction: () => { if (grid is null) return;
                for (int i = cells.Count - 1; i >= 0; i--)
                { grid.Set(cells[i].x, cells[i].y, cells[i].before); canvas.MarkDirty(cells[i].x, cells[i].y); }
                levelDirty = true; },
            redoAction: () => { if (grid is null) return;
                foreach (var (x, y, _, after) in cells) { grid.Set(x, y, after); canvas.MarkDirty(x, y); }
                levelDirty = true; });
    }

    // Sprite/object/palette edits are before/after snapshots (the lists/dicts are small).
    private void PushSpriteEdit(List<Sprite> before)
    {
        if (sprites is null) return;
        CommitStroke();
        var after = new List<Sprite>(sprites.Sprites);
        history.Push(() => RestoreSprites(before), () => RestoreSprites(after));
    }

    // `mutate` (e.g. Reset's clear) runs before the after-snapshot; a no-op (picker closed
    // back on the original color) records nothing.
    private void PushPaletteEdit(Dictionary<int, ushort> before, Action? mutate)
    {
        mutate?.Invoke();
        if (before.Count == palEdits.Count &&
            before.All(kv => palEdits.TryGetValue(kv.Key, out var v) && v == kv.Value))
            return;
        CommitStroke();
        var after = new Dictionary<int, ushort>(palEdits);
        history.Push(() => RestorePalEdits(before), () => RestorePalEdits(after));
    }

    private void PushObjectEdit(List<LevelObject> before)
    {
        if (objList is null) return;
        CommitStroke();
        var after = new List<LevelObject>(objList);
        history.Push(() => RestoreObjects(before), () => RestoreObjects(after));
    }

    private void RestorePalEdits(Dictionary<int, ushort> state)
    {
        palEdits.Clear();
        foreach (var (k, c) in state) palEdits[k] = c;
        RebuildGraphics();
    }

    private void RestoreObjects(List<LevelObject> list)
    {
        if (objList is null) return;
        objList.Clear();
        objList.AddRange(list);
        selObjs.Clear();
        RenderObjects();
    }

    private void RestoreSprites(List<Sprite> list)
    {
        if (sprites is null) return;
        bool vert = rom is not null && level is not null && rom.IsVerticalMode(level.Header.LevelMode);
        foreach (var s in sprites.Sprites.Concat(list))
        {
            var (cx, cy) = s.Cell(vert);
            MarkSpriteCells(cx, cy);
        }
        sprites.Sprites.Clear();
        sprites.Sprites.AddRange(list);
        selSprites.Clear();
        DropSpriteGhost();
        hiddenSprites = null;
        RebuildSpriteOverlay();
    }

    private void Undo() { CommitStroke(); history.Undo(); }
    private void Redo() { history.Redo(); }

    // Write the current grid edits back to a ROM copy as Direct Map16 objects.
    private void SaveEdits()
    {
        if (rom is null || level is null || grid is null || baseGrid is null) return;
        var (status, committed) = Dm16Saver.Save(rom, level, levelNum, grid, baseGrid, loadedRomPath);
        saveStatus = status;
        if (committed) baseGrid = grid.Clone();   // committed: new baseline
    }

    // Assemble the compositor inputs from current edit state, or null when nothing to draw.
    private CanvasScene? Scene()
    {
        if (tileCaches is null || grid is null) return null;
        int visRows = rom is not null && level is not null && rom.IsVerticalMode(level.Header.LevelMode)
            ? grid.Height : 27;
        return new CanvasScene(tileCaches, backdropColor, grid, bgImage, bgCaches, layer2Grid, visRows,
            (img, W, H, p) => { if (showSprites) spriteOverlay?.Draw(img, W, H, EditedPalette(p)!, hiddenSprites); });
    }

    private void BuildLevelCanvas()
    {
        canvasFull = false;
        if (Scene() is { } s) canvas.Rebuild(s); else canvas.Drop();
    }

    private void ApplyDirtyCells()
    {
        if (Scene() is { } s) canvas.ApplyDirty(s, AnimPhase); else canvas.Drop();
    }

    // Map16 palette tab: the composed tile sheet; click a tile to pick the paint brush.
    private void DrawMap16Tab()
    {
        if (map16Texs[0] is null) { ImGui.TextDisabled("No level."); return; }
        ImGui.Text($"Selected: 0x{selectedMap16:X3}");
        if (ImGui.BeginChild("m16sheet", System.Numerics.Vector2.Zero, 0, ImGuiWindowFlags.HorizontalScrollbar))
        {
            SnapCursorToPixel();
            var origin = ImGui.GetCursorScreenPos();
            float pz = SnappedZoom(Map16Zoom);
            ImGui.Image(imgui!.GetTextureID(map16Texs[AnimPhase] ?? map16Texs[0]!), new Vector2(map16W * pz, map16H * pz));
            float ts = 16 * pz;
            int tileCount = tileCaches?[0].Length ?? Map16.FgTiles;
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                var m = ImGui.GetMousePos();
                int idx = (int)((m.Y - origin.Y) / ts) * 16 + (int)((m.X - origin.X) / ts);
                if (idx >= 0 && idx < tileCount)
                {
                    selectedMap16 = idx;
                    brushTiles = new[] { (ushort)idx };    // palette pick = 1x1 brush
                    brushW = brushH = 1;
                    selRect = null;
                }
            }
            var stl = new Vector2(origin.X + (selectedMap16 % 16) * ts, origin.Y + (selectedMap16 / 16) * ts);
            ImGui.GetWindowDrawList().AddRect(stl, new Vector2(stl.X + ts, stl.Y + ts), 0xFF00FFFF, 0, 0, 2f);
            ImGui.EndChild();
        }
    }

    // Sprites palette tab: the level's sprite list. Selection is groundwork for sprite
    // placement editing later; today it's an inspector.
    // Sprites available to place in this level; "Loaded only" = LM's "sprites available
    // with the current sprite GFX" filter, from the table's per-slot file requirements.
    private void DrawSpritesTab()
    {
        if (rom is null || level is null) { ImGui.TextDisabled("No level."); return; }
        ImGui.Checkbox("Loaded only", ref catalogLoadedOnly);
        ImGui.SameLine();
        ImGui.TextDisabled($"SP {string.Join(" ", levelSpFiles.Select(f => f.ToString("X2")))}");
        if (ImGui.BeginChild("sprcat"))
        {
            for (int i = 0; i < catNumbers.Length; i++)
            {
                int num = catNumbers[i];
                bool loaded = SpriteDisplay.IsLoaded(num, levelSpFiles);
                if (catalogLoadedOnly && !loaded) continue;
                if (catThumbTex is not null)
                {
                    ImGui.Image(imgui!.GetTextureID(catThumbTex), new Vector2(32, 32),
                                new Vector2(0, (float)i / catNumbers.Length),
                                new Vector2(1, (float)(i + 1) / catNumbers.Length));
                    ImGui.SameLine();
                }
                if (ImGui.Selectable($"{num:X2}  {SpriteDisplay.NameOf(num)}{(loaded ? "" : "  (GFX not loaded)")}###cat{num}",
                                     selectedCatalog == num, ImGuiSelectableFlags.None, new Vector2(0, 32)))
                    selectedCatalog = num;
            }
            ImGui.EndChild();
        }
    }

    // Objects palette tab: the level's parsed object list. Selection is groundwork for
    // object editing later; today it's an inspector.
    // Objects tab: the placeable-object catalog (thumbnails from this tileset), right-click
    // the level to place the selected one. Names from the SMW source dispatch comments.
    private void DrawObjectsTab()
    {
        if (level is null) { ImGui.TextDisabled("No level."); return; }
        ImGui.TextDisabled($"tileset {level.Header.Tileset}  —  select, then right-click the level to place");
        if (objCatTex is null) BuildObjectCatalog();   // lazy: first view of the tab (per tileset)
        if (ImGui.BeginChild("objcat"))
        {
            for (int i = 0; i < objCatNums.Length; i++)
            {
                int num = objCatNums[i];
                if (objCatTex is not null && objCatUV.TryGetValue(num, out var uv))
                {
                    ImGui.Image(imgui!.GetTextureID(objCatTex), new Vector2(48, 48),
                                new Vector2(uv.u0, uv.v0), new Vector2(uv.u1, uv.v1));
                    ImGui.SameLine();
                }
                if (ImGui.Selectable($"{num:X2}  {ObjectNames.Standard(num)}###objcat{num}",
                                     selectedObjCat == num, ImGuiSelectableFlags.None, new Vector2(0, 48)))
                    selectedObjCat = num;
            }
            ImGui.EndChild();
        }
    }

    // Palette tab: the level's 256-color CGRAM as a 16x16 swatch grid. Click a swatch to
    // edit the color (quantized to SNES BGR555); the level re-renders when the picker
    // closes. Edits are session-only until a save path exists (LM custom palette, §7e).
    private void DrawPaletteTab()
    {
        if (rom is null || level is null) { ImGui.TextDisabled("No level."); return; }
        var pal = EditedPalette(0)!;
        ImGui.Text($"CGRAM — rows 0-7 BG/FG, 8-F sprites.  {palEdits.Count} edit(s)");
        ImGui.TextDisabled(rom.LmCustomPalette(levelNum) is not null
            ? "source: LM custom palette"
            : "source: vanilla (header-assembled)");
        if (palEdits.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset"))
            {
                PushPaletteEdit(new Dictionary<int, ushort>(palEdits), () => palEdits.Clear());
                RebuildGraphics();
            }
        }
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2, 2));
        float sw = MathF.Max(10, MathF.Floor((ImGui.GetContentRegionAvail().X - 15 * 2) / 16));
        for (int i = 0; i < 256; i++)
        {
            if ((i & 15) != 0) ImGui.SameLine();
            uint c = pal.Rgba[i];
            var v = new Vector4((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, 1f);
            if (ImGui.ColorButton($"##pal{i}", v,
                    ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoTooltip, new Vector2(sw, sw)))
            {
                palBeforePicker = new Dictionary<int, ushort>(palEdits);   // undo baseline
                ImGui.OpenPopup($"palpick{i}");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"0x{i:X2}  row {i >> 4} color {i & 15}  BGR555 {pal.Bgr[i]:X4}" +
                                 (palEdits.ContainsKey(i) ? "  (edited)" : ""));
            if (ImGui.BeginPopup($"palpick{i}"))
            {
                var v3 = new Vector3(v.X, v.Y, v.Z);
                if (ImGui.ColorPicker3($"0x{i:X2}##pick{i}", ref v3,
                        ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.NoSmallPreview))
                {
                    ushort bgr = (ushort)(((int)(v3.Z * 31 + .5f) << 10) |
                                          ((int)(v3.Y * 31 + .5f) << 5) |
                                           (int)(v3.X * 31 + .5f));
                    if (pal.Bgr[i] != bgr) { palEdits[i] = bgr; palDirtyRebuild = i; }
                }
                ImGui.EndPopup();
            }
            else if (palDirtyRebuild == i)
            {
                palDirtyRebuild = -1;
                PushPaletteEdit(palBeforePicker ?? new Dictionary<int, ushort>(palEdits), null);
                palBeforePicker = null;
                RebuildGraphics();      // picker closed: re-render with the edited palette
            }
        }
        ImGui.PopStyleVar();
    }

    // Save the edited palette as an LM custom palette (§7e) into a ROM copy. After a
    // successful save the edits ARE the level's palette, so the edit list resets.
    private void SavePalette()
    {
        if (rom is null || level is null) return;
        if (!rom.HasLmPaletteHook)
        { saveStatus = "ROM lacks LM's palette ASM — open/save it in Lunar Magic once first."; return; }
        try
        {
            var pal = EditedPalette(0)!;
            try { rom.WriteLmCustomPalette(levelNum, pal.Bgr[0], pal.Bgr); }
            catch (InvalidOperationException) { throw; }
            catch
            {
                rom.ExpandTo(Math.Min(0x400000, Math.Max(0x200000, rom.ActualRomSize * 2)));
                rom.WriteLmCustomPalette(levelNum, pal.Bgr[0], pal.Bgr);
            }
            string outp = System.IO.Path.ChangeExtension(loadedRomPath, ".edited.smc");
            rom.SaveAs(outp);
            palEdits.Clear();               // the ROM now holds these colors
            RebuildGraphics();
            saveStatus = $"palette saved -> {System.IO.Path.GetFileName(outp)} (level 0x{levelNum:X3} custom palette)";
        }
        catch (Exception e) { saveStatus = "palette save failed: " + e.Message; }
    }

    // The level palette with the editor tab's session edits applied on top.
    private Palette? EditedPalette(int phase)
    {
        if (rom is null || level is null) return null;
        var p = Palette.Load(rom, level.Header, levelNum, phase);
        foreach (var (i, c) in palEdits) { p.Bgr[i] = c; p.Rgba[i] = Palette.ToRgba(c); }
        return p;
    }

    // ROM inspector, reachable from File → ROM Info.
    private void BuildMap16Sheet()
    {
        for (int p = 0; p < 4; p++) { map16Texs[p]?.Dispose(); map16Texs[p] = null; }
        if (tileCaches is null) return;
        try
        {
            for (int p = 0; p < 4; p++)
            {
                var (px, w, h) = Map16.ComposeSheet(tileCaches[p]);
                map16Texs[p] = new Texture(GraphicsDevice, w, h, MemoryMarshal.AsBytes(px.AsSpan()));
                map16W = w; map16H = h;
            }
        }
        catch { for (int p = 0; p < 4; p++) { map16Texs[p]?.Dispose(); map16Texs[p] = null; } }
    }

    private void ParseLevel()
    {
        try
        {
            level = rom is null ? null : Level.Parse(rom, levelNum);
            grid = rom is not null && level is not null ? ObjectEngine.Render(rom, level) : null;
            baseGrid = grid?.Clone();          // snapshot to diff edits against on save
            objList = level is not null ? new List<LevelObject>(level.Objects) : null;
            history.Clear(); currentStroke = new();                       // new grid = new history
            selSprites.Clear(); selObjs.Clear(); dragStart = dragEnd = null; moveDrag = null; DropSpriteGhost(); hiddenSprites = null;
            panels.InvalidateLevel();                                     // refresh Level GFX window
            if (levelNum != palEditsLevel) { palEdits.Clear(); palEditsLevel = levelNum; }
            // Layer 2: background image or object layer, drawn behind layer 1.
            bgImage = rom is not null && level is not null ? Level.DecodeBgImage(rom, levelNum) : null;
            layer2Grid = rom is not null && level is not null
                ? ObjectEngine.RenderLayer2(rom, level.Header, levelNum) : null;
            sprites = rom is not null && level is not null ? SpriteData.Parse(rom, levelNum) : null;
            // Run the expensive OAM captures once per parse; repaints just re-blit.
            spriteOverlay = rom is not null && level is not null && sprites is not null
                ? SpriteOverlay.Build(rom, sprites, level.Header, levelNum) : null;
            canvasFull = true;
            RebuildGraphics();
        }
        catch { level = null; grid = null; tileCaches = null; }
    }

    // Recompose everything palette-dependent (tile caches, sheet, canvas) without
    // reparsing the level — so palette edits don't reset the grid or undo history.
    private void RebuildGraphics()
    {
        if (rom is null || level is null) { tileCaches = null; bgCaches = null; return; }
        tileCaches = new uint[4][][];
        for (int p = 0; p < 4; p++)
            tileCaches[p] = Map16.ComposeAll(rom, level.Header, levelNum, p, EditedPalette(p));
        backdropColor = EditedPalette(0)!.Rgba[0];
        if (bgImage is not null)
        {
            bgCaches = new uint[4][][];
            for (int p = 0; p < 4; p++)
                bgCaches[p] = Map16.ComposeAllBg(rom, level.Header, levelNum, p, EditedPalette(p));
        }
        else bgCaches = null;
        BuildMap16Sheet();
        BuildSpriteCatalog();
        objCatTex?.Dispose(); objCatTex = null;   // stale: Objects tab rebuilds it lazily
        BuildLevelCanvas();
    }

    // Per-tileset object footprint geometry (changed cells vs an empty render), cached so
    // the object engine runs only on tileset change; thumbnails recompose per palette.
    private readonly Dictionary<int, (int bx, int by, int bw, int bh, (int cx, int cy, ushort t)[] cells)> objCatCells = new();

    private void BuildObjectFootprints()
    {
        objCatCells.Clear();
        if (rom is null || level is null) return;
        var empty = new List<LevelObject>();
        Map16Grid baseG;
        try { baseG = ObjectEngine.RenderEmulatedStream(rom, level.Header, level.Encode(rom, empty), 0); }
        catch { return; }
        for (int num = 1; num <= 0x3F; num++)
        {
            var one = new List<LevelObject> { new(false, num, 0, 4, 10, ObjDefaultSize, -1) };
            Map16Grid g;
            try { g = ObjectEngine.RenderEmulatedStream(rom, level.Header, level.Encode(rom, one), 0); }
            catch { continue; }
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            var cells = new List<(int, int, ushort)>();
            for (int y = 0; y < g.Height; y++)
                for (int x = 0; x < g.Width; x++)
                {
                    int t = g.Get(x, y);
                    if (t == baseG.Get(x, y) || t == Map16Grid.Empty) continue;
                    cells.Add((x, y, (ushort)t));
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
            if (cells.Count == 0) continue;
            objCatCells[num] = (minX, minY, maxX - minX + 1, maxY - minY + 1, cells.ToArray());
        }
    }

    // Catalog atlas: one 48x48 thumbnail per placeable object, composed from the cached
    // footprint geometry with the current tileset's Map16 tiles (phase 0).
    private void BuildObjectCatalog()
    {
        objCatTex?.Dispose(); objCatTex = null;
        objCatNums = Array.Empty<int>();
        objCatUV.Clear();
        if (rom is null || level is null || tileCaches is null) return;
        if (objCatTileset != level.Header.Tileset) { BuildObjectFootprints(); objCatTileset = level.Header.Tileset; }
        var nums = objCatCells.Keys.OrderBy(n => n).ToArray();
        if (nums.Length == 0) return;
        const int cell = 48;
        var cache = tileCaches[0];
        var img = new uint[cell * cell * nums.Length];
        for (int i = 0; i < nums.Length; i++)
        {
            var fp = objCatCells[nums[i]];
            int srcW = fp.bw * 16, srcH = fp.bh * 16;
            // Nearest-neighbour fit into the cell, preserving aspect.
            int dw = srcW, dh = srcH;
            float scale = Math.Min(1f, (float)cell / Math.Max(srcW, srcH));
            dw = Math.Max(1, (int)(srcW * scale)); dh = Math.Max(1, (int)(srcH * scale));
            int ox = (cell - dw) / 2, oy = (cell - dh) / 2, rowBase = i * cell;
            foreach (var (cx, cy, t) in fp.cells)
            {
                uint[]? tile = (t & ObjectEngine.Marker) != 0 || t >= cache.Length ? null : cache[t];
                if (tile is null) continue;
                for (int py = 0; py < 16; py++)
                    for (int px = 0; px < 16; px++)
                    {
                        uint c = tile[py * 16 + px];
                        if (c == 0) continue;
                        int sx = (cx - fp.bx) * 16 + px, sy = (cy - fp.by) * 16 + py;
                        int dx = ox + (int)(sx * scale), dy = oy + (int)(sy * scale);
                        if (dx >= 0 && dx < cell && dy >= 0 && dy < cell) img[(rowBase + dy) * cell + dx] = c;
                    }
            }
            objCatUV[nums[i]] = (fp.bw, fp.bh, 0, (float)rowBase / (cell * nums.Length),
                                 1, (float)(rowBase + cell) / (cell * nums.Length));
        }
        objCatTex = new Texture(GraphicsDevice, cell, cell * nums.Length, MemoryMarshal.AsBytes(img.AsSpan()));
        objCatNums = nums;
    }

    // Catalog atlas: one thumbnail per table sprite, drawn with THIS level's GFX/palette.
    private void BuildSpriteCatalog()
    {
        catThumbTex?.Dispose(); catThumbTex = null;
        catNumbers = Array.Empty<int>();
        if (rom is null || level is null) return;
        try
        {
            levelSpFiles = SpriteRender.ResolveSpFiles(rom, level.Header, levelNum);
            var sp = SpriteRender.LoadSpTiles(rom, level.Header, levelNum);
            var pal = EditedPalette(0)!;
            catNumbers = SpriteDisplay.Numbers.ToArray();
            const int cell = 32;
            int n = catNumbers.Length;
            if (n == 0) return;
            var img = new uint[cell * cell * n];
            for (int i = 0; i < n; i++)
                if (SpriteDisplay.TryGet(catNumbers[i], out var rel))
                    SpriteRender.Draw(img, cell, cell * n,
                        rel.Select(o => o with { X = o.X + 8, Y = o.Y + i * cell + 16 }).ToList(), sp, pal);
            catThumbTex = new Texture(GraphicsDevice, cell, cell * n, MemoryMarshal.AsBytes(img.AsSpan()));
        }
        catch { catThumbTex?.Dispose(); catThumbTex = null; catNumbers = Array.Empty<int>(); }
    }

    private void LoadRom(string path)
    {
        try
        {
            rom = Rom.Load(path);
            loadedRomPath = path;
            ratCount = rom.EnumerateRats().Count();
            objCatTileset = -1;                 // force object catalog rebuild for the new ROM
            ParseLevel();
        }
        catch (Exception e)
        {
            rom = null;
            loadedRomPath = $"{path}  (load failed: {e.Message})";
        }
    }
}
