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
public class EditorApp : App
{
    private ImGuiLayer? imgui;

    // Loaded ROM (null until File → Open ROM).
    private string? loadedRomPath;
    private Rom? rom;
    private int ratCount;
    private int levelNum = 0x105;   // Yoshi's Island 2
    private Level? level;
    private Map16Grid? grid;

    // GFX viewer state
    private Texture? gfxTex;
    private int gfxW, gfxH, gfxFile, gfxBpp = 3, gfxPalRow = 2;
    private (int, int, int, int) gfxKey = (-1, -1, -1, -1);

    // "Level GFX" popup: the 8 GFX files this level loads into VRAM, as tile sheets.
    private bool showLevelGfx;
    private readonly List<(string label, Texture tex, int w, int h)> levelGfx = new();
    private int levelGfxKey = -1;

    // Composed Map16 sheet + level canvas, one texture per animation phase (CONTRACT §12).
    private readonly Texture?[] map16Texs = new Texture?[4];
    private int map16W, map16H;
    private readonly Texture?[] levelTexs = new Texture?[4];
    private int levelPxW, levelPxH;
    private bool animateTiles = true;
    // Current animation phase: the game advances every 8 frames at 60fps (~133 ms).
    private int AnimPhase => animateTiles ? (int)(ImGui.GetTime() / 0.1333) & 3 : 0;

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
    private bool showSprites = true;

    // Layout state
    private bool paletteVisible = true;
    private bool showRomInfo;
    private bool showGfxViewer;
    private int selectedSprite = -1;
    private int selectedObject = -1;

    // LM-style editing: left-click/drag selects (grabs the tiles under the cursor as the
    // brush), right-click stamps the brush, Delete erases the selection, Esc toggles
    // between Layer 1 and sprite selection modes.
    private enum EditMode { Layer1, Sprites }
    private EditMode editMode = EditMode.Layer1;
    private ushort[] brushTiles = { 0x100 };
    private int brushW = 1, brushH = 1;
    private (int x, int y, int w, int h)? selRect;   // last grab, for highlight + Delete
    private (int x, int y)? dragStart, dragEnd;      // left-drag rubber band (cells)

    // Palette editor: CGRAM index -> edited BGR555, applied over the ROM palette while
    // rendering. In-session only for now (no ROM save path yet); cleared on level change.
    private readonly Dictionary<int, ushort> palEdits = new();
    private int palEditsLevel = -1;
    private int palDirtyRebuild = -1;   // swatch whose picker changed; rebuild when its popup closes

    // Undo/redo: each entry is one paint stroke (all cells changed during one mouse-down).
    private readonly List<List<(int x, int y, ushort before, ushort after)>> undoStack = new();
    private readonly List<List<(int x, int y, ushort before, ushort after)>> redoStack = new();
    private List<(int x, int y, ushort before, ushort after)> currentStroke = new();
    private string saveStatus = "";
    private const float Zoom = 2f;        // on-screen px per source px (GFX viewer)
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
    }

    protected override void Shutdown()
    {
        imgui?.Dispose();
    }

    protected override void Update()
    {
        if (imgui is null) return;

        // Apply pending edits before layout so we never dispose a texture mid-frame.
        if (levelDirty) { BuildLevelCanvas(); levelDirty = false; }

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
                if (ImGui.MenuItem("ROM Info", "", showRomInfo)) showRomInfo = !showRomInfo;
                ImGui.Separator();
                if (ImGui.MenuItem("Exit")) Exit();
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Edit"))
            {
                if (ImGui.MenuItem("Undo", "Ctrl+Z", false, undoStack.Count > 0 || currentStroke.Count > 0)) Undo();
                if (ImGui.MenuItem("Redo", "Ctrl+Shift+Z", false, redoStack.Count > 0)) Redo();
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("View"))
            {
                if (ImGui.MenuItem("Palette", "", paletteVisible)) paletteVisible = !paletteVisible;
                ImGui.Separator();
                if (ImGui.MenuItem("Sprite overlay", "", showSprites))
                { showSprites = !showSprites; levelDirty = true; }
                if (ImGui.MenuItem("Animate tiles", "", animateTiles))
                    animateTiles = !animateTiles;
                ImGui.Separator();
                if (ImGui.MenuItem("GFX Viewer", "", showGfxViewer)) showGfxViewer = !showGfxViewer;
                if (ImGui.MenuItem("Level GFX", "", showLevelGfx)) showLevelGfx = !showLevelGfx;
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
            editMode = editMode == EditMode.Layer1 ? EditMode.Sprites : EditMode.Layer1;
            dragStart = null;
        }

        DrawMainLayout();
        if (showRomInfo) DrawRomInfo();
        if (showGfxViewer) DrawGfxViewer();
        DrawLevelGfx();
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
    private void DrawPalette()
    {
        if (ImGui.BeginTabBar("palettetabs"))
        {
            if (ImGui.BeginTabItem("Map16")) { DrawMap16Tab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Sprites")) { DrawSpritesTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Objects")) { DrawObjectsTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Palette")) { DrawPaletteTab(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
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
        if (ImGui.Button("GFX")) showLevelGfx = !showLevelGfx;
        if (levelTexs[0] is null || grid is null) { ImGui.TextDisabled("No level rendered."); return; }
        ImGui.SameLine();
        ImGui.Text(editMode == EditMode.Layer1
            ? $"—  Layer 1:  left: select/grab ({brushW}x{brushH} brush)   right: stamp   Del: erase   Esc: sprites"
            : "—  Sprites:  left: select sprite   Esc: layer 1");
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
            ImGui.Image(imgui!.GetTextureID(levelTexs[AnimPhase] ?? levelTexs[0]!), new Vector2(levelPxW * z, levelPxH * z));
            float cs = 16 * z;
            var dl = ImGui.GetWindowDrawList();

            // Persistent highlights: last selection (yellow) / selected sprite (green).
            if (editMode == EditMode.Layer1 && selRect is { } sr)
                dl.AddRect(new Vector2(origin.X + sr.x * cs, origin.Y + sr.y * cs),
                           new Vector2(origin.X + (sr.x + sr.w) * cs, origin.Y + (sr.y + sr.h) * cs),
                           0xFF00FFFF, 0, 0, 1.5f);
            if (editMode == EditMode.Sprites && sprites is not null &&
                selectedSprite >= 0 && selectedSprite < sprites.Sprites.Count)
            {
                var (sx, sy) = sprites.Sprites[selectedSprite].Cell(verticalLvl);
                dl.AddRect(new Vector2(origin.X + sx * cs, origin.Y + sy * cs),
                           new Vector2(origin.X + (sx + 1) * cs, origin.Y + (sy + 1) * cs),
                           0xFF00FF00, 0, 0, 2f);
            }

            if (ImGui.IsItemHovered())
            {
                var m = ImGui.GetMousePos();
                int cx = (int)((m.X - origin.X) / cs), cy = (int)((m.Y - origin.Y) / cs);
                if (cx >= 0 && cx < grid.Width && cy >= 0 && cy < grid.Height)
                {
                    if (editMode == EditMode.Layer1)
                    {
                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) dragStart = dragEnd = (cx, cy);
                        if (dragStart is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left)) dragEnd = (cx, cy);
                        if (ImGui.IsMouseDown(ImGuiMouseButton.Right)) StampBrush(cx, cy);
                    }
                    else if (sprites is not null && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        selectedSprite = -1;
                        for (int i = 0; i < sprites.Sprites.Count; i++)
                            if (sprites.Sprites[i].Cell(verticalLvl) == (cx, cy)) { selectedSprite = i; break; }
                    }
                    var tl = new Vector2(origin.X + cx * cs, origin.Y + cy * cs);
                    dl.AddRect(tl, new Vector2(tl.X + cs, tl.Y + cs), 0xFFFFFFFF, 0, 0, 1.5f);
                }
            }

            // Rubber band while dragging; grab the region as the brush on release.
            if (dragStart is { } d0 && dragEnd is { } d1)
            {
                var (rx, ry, rw, rh) = (Math.Min(d0.x, d1.x), Math.Min(d0.y, d1.y),
                                        Math.Abs(d1.x - d0.x) + 1, Math.Abs(d1.y - d0.y) + 1);
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    dl.AddRect(new Vector2(origin.X + rx * cs, origin.Y + ry * cs),
                               new Vector2(origin.X + (rx + rw) * cs, origin.Y + (ry + rh) * cs),
                               0xFF00FFFF, 0, 0, 1.5f);
                else
                {
                    GrabSelection(rx, ry, rw, rh);
                    dragStart = dragEnd = null;
                }
            }

            // Delete erases the selected region (one undo step).
            if (editMode == EditMode.Layer1 && selRect is { } er &&
                !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Delete))
            {
                for (int y = er.y; y < er.y + er.h; y++)
                    for (int x = er.x; x < er.x + er.w; x++)
                        PaintCell(x, y, Map16Grid.Empty);
                CommitStroke();
            }
            ImGui.EndChild();
        }
    }

    private void PaintCell(int x, int y, int tile)
    {
        int before = grid!.Get(x, y);
        if (before == tile) return;
        currentStroke.Add((x, y, (ushort)before, (ushort)tile));
        grid.Set(x, y, tile);
        levelDirty = true;
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
        undoStack.Add(currentStroke);
        if (undoStack.Count > 256) undoStack.RemoveAt(0);
        currentStroke = new();
        redoStack.Clear();
    }

    private void Undo()
    {
        CommitStroke();
        if (undoStack.Count == 0 || grid is null) return;
        var s = undoStack[^1];
        undoStack.RemoveAt(undoStack.Count - 1);
        for (int i = s.Count - 1; i >= 0; i--) grid.Set(s[i].x, s[i].y, s[i].before);
        redoStack.Add(s);
        levelDirty = true;
    }

    private void Redo()
    {
        if (redoStack.Count == 0 || grid is null) return;
        var s = redoStack[^1];
        redoStack.RemoveAt(redoStack.Count - 1);
        foreach (var (x, y, _, after) in s) grid.Set(x, y, after);
        undoStack.Add(s);
        levelDirty = true;
    }

    // Write the current grid edits back to a ROM copy as Direct Map16 objects.
    private void SaveEdits()
    {
        if (rom is null || level is null || grid is null || baseGrid is null) return;
        if (!rom.HasDm16Hijack) { saveStatus = "ROM lacks LM Direct Map16 ASM — open a ROM saved by LM."; return; }

        // Collect changed, non-empty cells as DM16 objects grouped by screen.
        var byScreen = new Dictionary<int, List<LevelObject>>();
        int edits = 0;
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
            {
                int t = grid.Get(x, y);
                if (t == baseGrid.Get(x, y)) continue;
                if ((t & ObjectEngine.Marker) != 0) continue;             // marker: skip
                int place = t == Map16Grid.Empty ? 0x025 : t;             // erase = blank sky tile
                int screen = x / 16;
                var o = LevelObject.MakeDm16(place, screen, x % 16, y);
                if (!byScreen.TryGetValue(screen, out var lst)) byScreen[screen] = lst = new();
                lst.Add(o);
                edits++;
            }
        if (edits == 0) { saveStatus = "no edits to save"; return; }

        // Merge into the original object list: insert each screen's DM16 objects right after
        // that screen's last original object (keeps the original new-screen flags valid).
        var merged = new List<LevelObject>();
        var placed = new HashSet<int>();
        var objs = level.Objects;
        for (int i = 0; i < objs.Count; i++)
        {
            merged.Add(objs[i]);
            int next = i + 1 < objs.Count ? objs[i + 1].Screen : -1;
            // Screens can repeat (screen jumps): only insert at a screen's first boundary.
            if (objs[i].Screen != next && !placed.Contains(objs[i].Screen) &&
                byScreen.TryGetValue(objs[i].Screen, out var lst))
            { merged.AddRange(lst); placed.Add(objs[i].Screen); }
        }
        int skipped = byScreen.Where(kv => !placed.Contains(kv.Key)).Sum(kv => kv.Value.Count);

        try
        {
            byte[] data = level.Encode(rom, merged);
            int addr;
            try { addr = rom.AllocateRats(data); }
            catch { rom.ExpandTo(Math.Min(0x400000, Math.Max(0x200000, rom.ActualRomSize * 2))); addr = rom.AllocateRats(data); }
            rom.SetLayer1Pointer(levelNum, addr);
            string outp = System.IO.Path.ChangeExtension(loadedRomPath, ".edited.smc");
            rom.SaveAs(outp);
            baseGrid = grid.Clone();   // committed: new baseline
            saveStatus = $"saved {edits} edits -> {System.IO.Path.GetFileName(outp)}" +
                         (skipped > 0 ? $"  ({skipped} on empty screens skipped)" : "");
        }
        catch (Exception e) { saveStatus = "save failed: " + e.Message; }
    }

    private void BuildLevelCanvas()
    {
        for (int p = 0; p < 4; p++) { levelTexs[p]?.Dispose(); levelTexs[p] = null; }
        if (tileCaches is null || grid is null) return;
        try
        {
            int visRows = rom is not null && level is not null && rom.IsVerticalMode(level.Header.LevelMode)
                ? grid.Height : 27;
            for (int p = 0; p < 4; p++)
            {
                var (img, W, H) = Map16.ComposeLevel(tileCaches[p], backdropColor, grid, bgImage, bgCaches?[p], layer2Grid, visRows);
                if (showSprites && rom is not null && level is not null)
                    sprites?.DrawOverlay(img, W, H, rom, level.Header, levelNum, EditedPalette(p));
                levelTexs[p] = new Texture(GraphicsDevice, W, H, MemoryMarshal.AsBytes(img.AsSpan()));
                levelPxW = W; levelPxH = H;
            }
        }
        catch { for (int p = 0; p < 4; p++) { levelTexs[p]?.Dispose(); levelTexs[p] = null; } }
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
    private void DrawSpritesTab()
    {
        if (sprites is null) { ImGui.TextDisabled("No level."); return; }
        ImGui.Text($"{sprites.Sprites.Count} sprites   memory {sprites.SpriteMemory}  buoyancy {sprites.Buoyancy}");
        if (ImGui.BeginChild("sprlist"))
        {
            bool vert = rom is not null && level is not null && rom.IsVerticalMode(level.Header.LevelMode);
            for (int i = 0; i < sprites.Sprites.Count; i++)
            {
                var s = sprites.Sprites[i];
                var (cx, cy) = s.Cell(vert);
                string kind = s.IsScrollCommand ? " (scroll cmd)" : s.Number >= 0xC9 ? " (special)" : "";
                if (ImGui.Selectable($"{s.Number:X2}  at ({cx,3},{cy,2})  extra {s.Extra}{kind}###spr{i}",
                                     selectedSprite == i))
                    selectedSprite = i;
            }
            ImGui.EndChild();
        }
    }

    // Objects palette tab: the level's parsed object list. Selection is groundwork for
    // object editing later; today it's an inspector.
    private void DrawObjectsTab()
    {
        if (level is null) { ImGui.TextDisabled("No level."); return; }
        ImGui.Text($"{level.Objects.Count} objects   tileset {level.Header.Tileset}");
        if (ImGui.BeginChild("objlist"))
        {
            for (int i = 0; i < level.Objects.Count; i++)
            {
                var o = level.Objects[i];
                string label = o.IsScreenExit ? $"exit -> {(o.ExtraByte >= 0 ? $"{o.ExtraByte:X2}" : "?")}"
                    : o.IsDm16 ? $"DM16 0x{o.Dm16Tile:X3}"
                    : o.Extended ? $"ext {o.ExtendedNumber:X2}"
                    : $"obj {o.Number:X2}";
                if (ImGui.Selectable($"{label}  scr {o.Screen:X2} at ({o.AbsoluteX,3},{o.Y,2})###obj{i}",
                                     selectedObject == i))
                    selectedObject = i;
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
            if (ImGui.SmallButton("Reset")) { palEdits.Clear(); RebuildGraphics(); }
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
                ImGui.OpenPopup($"palpick{i}");
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
    private void DrawRomInfo()
    {
        if (!ImGui.Begin("ROM Info", ref showRomInfo)) { ImGui.End(); return; }
        if (rom is null)
        {
            ImGui.TextDisabled("No ROM loaded.");
            ImGui.TextDisabled("File → Open ROM to begin.");
        }
        else
        {
            ImGui.Text($"File: {loadedRomPath}");
            ImGui.Text($"Copier header: {(rom.HeaderOffset != 0 ? "yes (0x200)" : "no")}");
            ImGui.Text($"Title: '{rom.Title}'");
            ImGui.Text($"Map mode: {rom.MapModeName} (0x{rom.MapMode:X2})");
            ImGui.Text($"ROM size: {rom.ActualRomSize / 1024} KB on disk, {rom.DeclaredRomSize / 1024} KB declared");
            ImGui.Text($"Checksum: 0x{rom.Checksum:X4} (compl 0x{rom.ChecksumComplement:X4})");
            ImGui.Separator();
            ImGui.Text($"Valid RATS tags: {ratCount}");
        }
        ImGui.End();
    }

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

    // Renders a GFX file as a palette-colored 8x8 tile sheet — real SNES pixels via the
    // decompress → decode → palette → Foster texture path.
    private void DrawGfxViewer()
    {
        ImGui.Begin("GFX Viewer");
        if (rom is null) { ImGui.TextDisabled("No ROM."); ImGui.End(); return; }
        ImGui.SetNextItemWidth(90);
        ImGui.InputInt($"file (0x{gfxFile:X2})", ref gfxFile); gfxFile = Math.Clamp(gfxFile, 0, Gfx.Count - 1);
        ImGui.SameLine(); ImGui.SetNextItemWidth(80); ImGui.InputInt("bpp", ref gfxBpp); gfxBpp = Math.Clamp(gfxBpp, 2, 4);
        ImGui.SameLine(); ImGui.SetNextItemWidth(80); ImGui.InputInt("pal", ref gfxPalRow); gfxPalRow = Math.Clamp(gfxPalRow, 0, 15);

        var key = (gfxFile, gfxBpp, gfxPalRow, levelNum);
        if (key != gfxKey) { gfxKey = key; BuildGfxTexture(); }
        if (gfxTex is not null)
            ImGui.Image(imgui!.GetTextureID(gfxTex), new Vector2(gfxW * 3f, gfxH * 3f));
        ImGui.End();
    }

    // The GFX files loaded into VRAM for the current level: 4 FG/BG + 4 sprite slots,
    // resolved through the tileset lists and the Super GFX Bypass, as decoded tile sheets.
    private void DrawLevelGfx()
    {
        if (!showLevelGfx) return;
        if (!ImGui.Begin("Level GFX", ref showLevelGfx)) { ImGui.End(); return; }
        if (rom is null || level is null) { ImGui.TextDisabled("No level."); ImGui.End(); return; }

        if (levelGfxKey != levelNum) { levelGfxKey = levelNum; BuildLevelGfx(); }
        foreach (var (label, tex, w, h) in levelGfx)
        {
            ImGui.Text(label);
            ImGui.Image(imgui!.GetTextureID(tex), new Vector2(w * 2f, h * 2f));
            ImGui.Separator();
        }
        ImGui.End();
    }

    private void BuildLevelGfx()
    {
        foreach (var e in levelGfx) e.tex.Dispose();
        levelGfx.Clear();
        if (rom is null || level is null) return;
        var h = level.Header;
        var pal = Palette.Load(rom, h, levelNum);
        var byp = rom.LmGfxBypass(levelNum);

        // (name, GFXLIST base, list index, palette row for the preview, bypass record word)
        var slots = new (string name, int listBase, int idx, int palRow, int bypWord)[]
        {
            ("FG1", Gfx.ObjectGfxList, h.Tileset * 4 + 0, 2, 7),
            ("FG2", Gfx.ObjectGfxList, h.Tileset * 4 + 1, 2, 6),
            ("BG1", Gfx.ObjectGfxList, h.Tileset * 4 + 2, 0, 5),
            ("FG3", Gfx.ObjectGfxList, h.Tileset * 4 + 3, 2, 4),
            ("SP1", 0x00A8C3, h.SpriteSet * 4 + 0, 8, 11),
            ("SP2", 0x00A8C3, h.SpriteSet * 4 + 1, 8, 10),
            ("SP3", 0x00A8C3, h.SpriteSet * 4 + 2, 8, 9),
            ("SP4", 0x00A8C3, h.SpriteSet * 4 + 3, 8, 8),
        };
        foreach (var s in slots)
        {
            int file = rom.Data[rom.FileOffset(s.listBase) + s.idx];
            bool bypassed = byp is not null && (byp[s.bypWord] & 0xFFF) != 0x7F;
            if (bypassed) file = byp![s.bypWord] & 0xFFF;
            int src = Gfx.SourceSnes(rom, file);
            if (src < 0) { levelGfx.Add(($"{s.name} = {file:X2} (empty)", MakeBlank(), 8, 8)); continue; }
            try
            {
                var data = Gfx.Lz2Decompress(rom.Data, rom.FileOffset(src));
                int bpp = data.Length >= 0x1000 ? 4 : 3;
                var (px, w, ht) = Gfx.TileSheet(data, bpp, pal, s.palRow);
                levelGfx.Add(($"{s.name} = GFX{file:X2}{(bypassed ? " (bypass)" : "")}, {bpp}bpp",
                              new Texture(GraphicsDevice, w, ht, MemoryMarshal.AsBytes(px.AsSpan())), w, ht));
            }
            catch { levelGfx.Add(($"{s.name} = {file:X2} (decode failed)", MakeBlank(), 8, 8)); }
        }
    }

    private Texture MakeBlank() => new(GraphicsDevice, 8, 8, new byte[8 * 8 * 4]);

    private void BuildGfxTexture()
    {
        try
        {
            var gfx = Gfx.DecompressFile(rom!, gfxFile);
            var pal = Palette.Load(rom!, level?.Header ?? default);
            var (px, w, h) = Gfx.TileSheet(gfx, gfxBpp, pal, gfxPalRow);
            gfxTex?.Dispose();
            gfxTex = new Texture(GraphicsDevice, w, h, MemoryMarshal.AsBytes(px.AsSpan()));
            gfxW = w; gfxH = h;
        }
        catch { gfxTex?.Dispose(); gfxTex = null; }
    }

    // Schematic view: each object drawn as a box at (screen*16+x, y), sized by width/height.
    // Not the real tiles (that needs the object engine + GFX) — a structural map of the parse.
    private void ParseLevel()
    {
        try
        {
            level = rom is null ? null : Level.Parse(rom, levelNum);
            grid = rom is not null && level is not null ? ObjectEngine.Render(rom, level) : null;
            baseGrid = grid?.Clone();          // snapshot to diff edits against on save
            undoStack.Clear(); redoStack.Clear(); currentStroke = new();   // new grid = new history
            levelGfxKey = -1;                                              // refresh Level GFX window
            if (levelNum != palEditsLevel) { palEdits.Clear(); palEditsLevel = levelNum; }
            // Layer 2: background image or object layer, drawn behind layer 1.
            bgImage = rom is not null && level is not null ? Level.DecodeBgImage(rom, levelNum) : null;
            layer2Grid = rom is not null && level is not null
                ? ObjectEngine.RenderLayer2(rom, level.Header, levelNum) : null;
            sprites = rom is not null && level is not null ? SpriteData.Parse(rom, levelNum) : null;
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
        BuildLevelCanvas();
    }

    private void LoadRom(string path)
    {
        try
        {
            rom = Rom.Load(path);
            loadedRomPath = path;
            ratCount = rom.EnumerateRats().Count();
            ParseLevel();
        }
        catch (Exception e)
        {
            rom = null;
            loadedRomPath = $"{path}  (load failed: {e.Message})";
        }
    }
}
