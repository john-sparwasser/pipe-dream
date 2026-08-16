using System.Numerics;
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
///
/// This class is lifecycle + wiring only: it owns the component classes (menu bar, shell
/// layout, viewport, editors, session, inspector panels) and the state they share.
/// </summary>
public class EditorApp : App
{
    internal ImGuiLayer? imgui;

    // Loaded ROM (null until File → Open ROM).
    internal string? loadedRomPath;
    internal Rom? rom;
    internal int ratCount;
    internal int levelNum = 0x105;   // Yoshi's Island 2
    internal Level? level;
    internal Map16Grid? grid;

    // ---- Map16 edit mode (canvas view toggle: the canvas becomes the Map16 sheet, the
    // left drawer becomes the 8x8 GFX palette; same grammar as level editing) ----
    internal enum CanvasView { Level, Map16 }
    internal CanvasView canvasView;

    internal LevelCanvas canvas = null!;    // created in Startup (needs GraphicsDevice)
    internal bool animateTiles = true;
    // Current animation phase: the game advances every 8 frames at 60fps (~133 ms).
    // Wall-clock based (NOT ImGui.GetTime) — this is read outside the ImGui layout
    // window, where no ImGui context is current and igGetTime crashes.
    internal int AnimPhase => animateTiles ? (int)(Environment.TickCount64 / 133) & 3 : 0;

    // Edit state
    internal uint[][][]? tileCaches;  // [phase][map16 tile] composed 16x16 tiles
    internal int selectedMap16 = 0x100;
    internal bool levelDirty;
    internal Map16Grid? baseGrid;     // object-engine output before edits, to diff against on save
    internal uint[][][]? bgCaches;    // [phase] composed BG Map16 tiles for the background image
    internal SpriteData? sprites;     // sprite list for the overlay
    internal SpriteOverlay? spriteOverlay;   // cached OAM captures; Draw() is cheap blits
    internal bool showSprites = true;
    internal int selectedCatalog = -1;

    // canvasFull forces the full compose path (vs incremental dirty-cell) on the next flush.
    internal bool canvasFull = true;

    internal bool paletteVisible = true;
    internal readonly HashSet<int> selSprites = new();   // selected sprite indices (sprite mode)

    // Object editing (Objects mode): a mutable working copy of the level's objects.
    internal List<LevelObject>? objList;
    internal readonly HashSet<int> selObjs = new();      // selected indices into objList
    internal int selectedObjCat = -1;                    // catalog object number to place

    // LM-style editing: everything on Layer 1 is an object — placed tiles are Direct
    // Map16 objects, manipulated exactly like standard objects (select/move/resize).
    // Left-click/lasso selects, right-click duplicates / places catalog object / stamps
    // the tile brush, Delete removes, Esc toggles Layer 1 <-> sprite mode.
    internal enum EditMode { Layer1, Sprites }
    internal EditMode editMode = EditMode.Layer1;
    internal EditTool spriteTool = null!, objectTool = null!;   // set in Startup
    internal EditTool ActiveTool => editMode == EditMode.Sprites ? spriteTool : objectTool;
    internal ushort[] brushTiles = { 0x100 };
    internal int brushW = 1, brushH = 1;
    internal (int x, int y)? dragStart, dragEnd;      // left-drag rubber band (cells)
    internal (int x, int y)? moveDrag;                // anchor cell: dragging the selection moves it
    // Object resize in progress: which object, which edges (bit 1=L 2=R 4=T 8=B), the
    // object as it was at mouse-down, and the anchor cell the drag started from.
    internal (int obj, int edges, LevelObject orig, int cx, int cy)? resizeDrag;

    // Undo/redo via a generic command stack; each editor's Push* captures the
    // domain-specific undo/redo closures.
    internal readonly EditHistory history = new();
    internal string saveStatus = "";

    // Components (created in Startup): each owns one responsibility and holds a
    // back-reference here for the shared state above.
    internal MenuBar menuBar = null!;
    internal ShellLayout shell = null!;
    internal LevelViewport viewport = null!;
    internal Map16Editor map16Editor = null!;
    internal SpriteEditor spriteEditor = null!;
    internal ObjectEditor objectEditor = null!;
    internal PaletteEditor paletteEditor = null!;
    internal LevelSession session = null!;

    // Read-only inspector windows (ROM info / GFX viewers), created in Startup.
    internal RomInfoPanel romInfoPanel = null!;
    internal GfxViewerPanel gfxViewerPanel = null!;
    internal LevelGfxPanel levelGfxPanel = null!;

    // The UI runs at ImGuiLayer.Scale (BaseScale x display scale), so a zoom of 1 unit may be
    // a fractional number of physical pixels (e.g. 2.5 at 125% Windows scaling) — that
    // resampling is visible on pixel art. Snap so texel -> physical pixels is an integer,
    // and anchor draws on whole physical pixels.
    internal float SnappedZoom(float desired)
    {
        float s = imgui!.Scale;
        return MathF.Max(1f, MathF.Round(desired * s)) / s;
    }

    internal void SnapCursorToPixel()
    {
        float s = imgui!.Scale;
        var p = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(MathF.Floor(p.X * s) / s, MathF.Floor(p.Y * s) / s));
    }

    private readonly string? startupRomPath;
    private readonly int startupLevel;

    public EditorApp(string? romPath = null, int startLevel = -1) : base(new AppConfig
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
        startupRomPath = romPath;
        startupLevel = startLevel;
    }

    protected override void Startup()
    {
        // SDL3 delivers no text-input events until this is called — without it every
        // ImGui text field is focusable but type-dead (Keyboard.Text stays empty).
        Window.StartTextInput();
        imgui = new ImGuiLayer(this) { BaseScale = 1.5f };
        canvas = new LevelCanvas(GraphicsDevice);
        spriteTool = new SpriteTool(this); objectTool = new ObjectTool(this);
        menuBar = new MenuBar(this);
        shell = new ShellLayout(this);
        viewport = new LevelViewport(this);
        map16Editor = new Map16Editor(this);
        spriteEditor = new SpriteEditor(this);
        objectEditor = new ObjectEditor(this);
        paletteEditor = new PaletteEditor(this);
        session = new LevelSession(this);
        romInfoPanel = new RomInfoPanel();
        gfxViewerPanel = new GfxViewerPanel(GraphicsDevice, imgui);
        levelGfxPanel = new LevelGfxPanel(GraphicsDevice, imgui);
        SetWindowIcon();
        if (startupRomPath is not null)
        {
            if (startupLevel >= 0) levelNum = startupLevel;
            session.LoadRom(startupRomPath);
        }
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
        gfxViewerPanel?.Dispose();
        levelGfxPanel?.Dispose();
    }

    protected override void Update()
    {
        if (imgui is null) return;

        // Apply pending edits before layout so we never dispose a texture mid-frame.
        if (levelDirty)
        {
            if (!canvasFull && canvas.DirtyCount > 0 && canvas.HasImages) session.ApplyDirtyCells();
            else session.BuildLevelCanvas();
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
        menuBar.Draw();

        // Map16 paint strokes commit when the right button is up — at frame start, so the
        // graphics rebuild never disposes textures already submitted to this frame.
        map16Editor.CommitStrokeOnRelease();

        // Deferred Map16 page allocation (from a click on an empty page in the picker) —
        // runs before any drawing so texture rebuilds never race the frame's draw data.
        map16Editor.RunPendingAlloc();

        var io = ImGui.GetIO();
        if (io.KeyCtrl && !io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Z, true))
        {
            if (io.KeyShift) Redo(); else Undo();
        }
        // Esc cycles Layer 1 <-> sprite selection (unless it's closing a popup).
        if (!io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Escape) &&
            !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
        {
            if (canvasView == CanvasView.Map16)
                canvasView = CanvasView.Level;             // Esc leaves Map16 edit mode first
            else
            {
                editMode = editMode == EditMode.Layer1 ? EditMode.Sprites : EditMode.Layer1;
                // Bring the matching palette tab along (Objects tab also drives Layer1 mode).
                shell.pendingTabSelect = shell.paletteTab = editMode == EditMode.Sprites ? 1 : 0;
            }
            dragStart = null; moveDrag = null; resizeDrag = null; selSprites.Clear();
            spriteEditor.DropSpriteGhost();
            spriteEditor.ClearHiddenSprites();
        }

        shell.DrawMainLayout();
        romInfoPanel.Draw(rom, loadedRomPath, ratCount);
        gfxViewerPanel.Draw(rom, level?.Header, levelNum);
    }

    internal void MarkAllDirty() { canvasFull = true; levelDirty = true; }

    internal void Undo() { history.Undo(); }
    internal void Redo() { history.Redo(); }
}
