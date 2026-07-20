using System.Numerics;
using System.Runtime.InteropServices;
using Foster.Framework;
using ImGuiNET;

namespace PipeDream;

/// <summary>
/// Skeleton editor shell: a Foster window driving the ImGui backend. No ROM logic yet —
/// this exists to prove the framework layer (window + ImGui render chain) works, so the
/// ROM decode pipeline (see reference/CONTRACT.md) has something to render into.
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
    private uint[][]? bgCache;       // composed BG Map16 tiles for the background image
    private Map16Grid? layer2Grid;   // layer-2 object layer, else null
    private SpriteData? sprites;     // sprite list for the overlay
    private bool showSprites = true;

    // Undo/redo: each entry is one paint stroke (all cells changed during one mouse-down).
    private readonly List<List<(int x, int y, ushort before, ushort after)>> undoStack = new();
    private readonly List<List<(int x, int y, ushort before, ushort after)>> redoStack = new();
    private List<(int x, int y, ushort before, ushort after)> currentStroke = new();
    private string saveStatus = "";
    private const float Zoom = 2f;        // on-screen px per source px for the tile picker
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
        // Full-window dockspace so future panels (level canvas, Map16, palette, sprites) can dock.
        ImGui.DockSpaceOverViewport(ImGui.GetMainViewport(), ImGuiDockNodeFlags.PassthruCentralNode);

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
                if (ImGui.MenuItem("Sprite overlay", "", showSprites))
                { showSprites = !showSprites; levelDirty = true; }
                if (ImGui.MenuItem("Animate tiles", "", animateTiles))
                    animateTiles = !animateTiles;
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

        ImGui.Begin("ROM Info");
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

        DrawLevelPanel();
        DrawLevelCanvas();
        DrawMap16Sheet();
        DrawGfxViewer();
    }

    // The composed level: the Map16 grid rendered with real tile graphics.
    private void DrawLevelCanvas()
    {
        ImGui.Begin("Level Render");
        if (levelTexs[0] is null || grid is null) { ImGui.TextDisabled("No level rendered."); ImGui.End(); return; }
        ImGui.Text($"Level 0x{levelNum:X3} — left-click: place 0x{selectedMap16:X3}   right-click: erase");
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
            if (ImGui.IsItemHovered())
            {
                var m = ImGui.GetMousePos();
                int cx = (int)((m.X - origin.X) / cs), cy = (int)((m.Y - origin.Y) / cs);
                if (cx >= 0 && cx < grid.Width && cy >= 0 && cy < grid.Height)
                {
                    if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) PaintCell(cx, cy, selectedMap16);
                    else if (ImGui.IsMouseDown(ImGuiMouseButton.Right)) PaintCell(cx, cy, Map16Grid.Empty);
                    var tl = new Vector2(origin.X + cx * cs, origin.Y + cy * cs);
                    ImGui.GetWindowDrawList().AddRect(tl, new Vector2(tl.X + cs, tl.Y + cs), 0xFFFFFFFF, 0, 0, 1.5f);
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private void PaintCell(int x, int y, int tile)
    {
        int before = grid!.Get(x, y);
        if (before == tile) return;
        currentStroke.Add((x, y, (ushort)before, (ushort)tile));
        grid.Set(x, y, tile);
        levelDirty = true;
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
                var (img, W, H) = Map16.ComposeLevel(tileCaches[p], backdropColor, grid, bgImage, bgCache, layer2Grid, visRows);
                if (showSprites) sprites?.DrawOverlay(img, W, H);
                levelTexs[p] = new Texture(GraphicsDevice, W, H, MemoryMarshal.AsBytes(img.AsSpan()));
                levelPxW = W; levelPxH = H;
            }
        }
        catch { for (int p = 0; p < 4; p++) { levelTexs[p]?.Dispose(); levelTexs[p] = null; } }
    }

    // The composed Map16 tile sheet — real SNES graphics for this level's tileset.
    private void DrawMap16Sheet()
    {
        ImGui.Begin("Map16 Tiles");
        if (map16Texs[0] is null) { ImGui.TextDisabled("No level."); ImGui.End(); return; }
        ImGui.Text($"Selected: 0x{selectedMap16:X3}   (click a tile to pick it, then paint on the level)");
        if (ImGui.BeginChild("m16sheet", System.Numerics.Vector2.Zero, 0, ImGuiWindowFlags.HorizontalScrollbar))
        {
            SnapCursorToPixel();
            var origin = ImGui.GetCursorScreenPos();
            float pz = SnappedZoom(Zoom);
            ImGui.Image(imgui!.GetTextureID(map16Texs[AnimPhase] ?? map16Texs[0]!), new Vector2(map16W * pz, map16H * pz));
            float ts = 16 * pz;
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                var m = ImGui.GetMousePos();
                int idx = (int)((m.Y - origin.Y) / ts) * 16 + (int)((m.X - origin.X) / ts);
                if (idx >= 0 && idx < Map16.FgTiles) selectedMap16 = idx;
            }
            int sc = selectedMap16 & 0x1FF;
            var stl = new Vector2(origin.X + (sc % 16) * ts, origin.Y + (sc / 16) * ts);
            ImGui.GetWindowDrawList().AddRect(stl, new Vector2(stl.X + ts, stl.Y + ts), 0xFF00FFFF, 0, 0, 2f);
            ImGui.EndChild();
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
    private void DrawLevelPanel()
    {
        ImGui.Begin("Level");
        if (rom is null)
        {
            ImGui.TextDisabled("No ROM loaded.");
            ImGui.End();
            return;
        }

        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt($"Level (0x{levelNum:X3})", ref levelNum))
        {
            levelNum = Math.Clamp(levelNum, 0, Rom.LevelCount - 1);
            ParseLevel();
        }
        ImGui.SameLine();
        if (ImGui.Button("Reload")) ParseLevel();

        if (level is null) { ImGui.End(); return; }

        var h = level.Header;
        ImGui.Text($"Layer1 @ ${level.DataPointer:X6}   Layer 2 @ ${rom.Layer2Pointer(levelNum):X6}" +
                   (rom.Layer2IsBackground(levelNum) ? " (background)" : " (objects)") +
                   $"   Sprites @ ${rom.SpritePointer(levelNum):X6}");
        // Header fields, labeled like Lunar Magic's dialogs for side-by-side comparison.
        if (ImGui.CollapsingHeader("Level header (LM naming)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Text($"# of screens: {h.Screens:X2}    Level mode: {h.LevelMode:X2}    " +
                       $"FG/BG GFX (tileset): {h.Tileset:X}");
            ImGui.Text($"BG palette: {h.BgPalette}   FG palette: {h.FgPalette}   " +
                       $"Sprite palette: {h.SpritePalette}   Back area color: {h.BackAreaColor}");
            ImGui.Text($"Music: {h.Music}   Sprite GFX set: {h.SpriteSet:X}   Time: {h.Time}   " +
                       $"Item memory: {h.ItemMemory}   V-scroll: {h.ScrollSetting}   L3 prio: {h.Layer3Priority}");
            if (sprites is not null)
                ImGui.Text($"Sprite memory: {sprites.SpriteMemory:X2}   Buoyancy: {sprites.Buoyancy}");
            // Resolved GFX files per slot, like LM's "GFX index in header" dialog (FG1=14 …).
            var byp = rom.LmGfxBypass(levelNum);
            int[] slots = new int[4];
            for (int s = 0; s < 4; s++)
                slots[s] = rom.Data[rom.FileOffset(Gfx.ObjectGfxList) + h.Tileset * 4 + s];
            if (byp is not null)
            {
                int[] w = { 7, 6, 5, 4 };            // FG1, FG2, BG1, FG3 record words
                for (int s = 0; s < 4; s++)
                    if ((byp[w[s]] & 0xFFF) != 0x7F) slots[s] = byp[w[s]] & 0xFFF;
            }
            ImGui.Text($"GFX files: FG1={slots[0]:X2} FG2={slots[1]:X2} BG1={slots[2]:X2} FG3={slots[3]:X2}" +
                       (byp is not null ? "  (Super GFX Bypass ON)" : ""));
        }
        ImGui.Separator();
        ImGui.Text($"{level.Objects.Count} objects" + (level.Empty ? "  (empty level)" : ""));

        if (ImGui.BeginChild("objlist"))
        {
            if (ImGui.BeginTable("objs", 6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("#");
                ImGui.TableSetupColumn("obj");
                ImGui.TableSetupColumn("scr");
                ImGui.TableSetupColumn("x");
                ImGui.TableSetupColumn("y");
                ImGui.TableSetupColumn("info");
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();
                int i = 0;
                foreach (var o in level.Objects)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.Text((i++).ToString());
                    ImGui.TableNextColumn(); ImGui.Text(o.Extended ? "ext" : $"{o.Number:X2}");
                    ImGui.TableNextColumn(); ImGui.Text($"{o.Screen:X2}");
                    ImGui.TableNextColumn(); ImGui.Text($"{o.XNibble:X}");
                    ImGui.TableNextColumn(); ImGui.Text($"{o.Y:X2}");
                    ImGui.TableNextColumn();
                    string info = o.IsScreenExit ? $"screen exit → {o.ExtraByte:X2}"
                        : o.Extended ? $"ext obj {o.ExtendedNumber:X2}"
                        : $"{o.Width}x{o.Height} (b3={o.Byte3:X2})";
                    if (o.NewScreen) info = "[new screen] " + info;
                    ImGui.Text(info);
                }
                ImGui.EndTable();
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private void ParseLevel()
    {
        try
        {
            level = rom is null ? null : Level.Parse(rom, levelNum);
            grid = rom is not null && level is not null ? ObjectEngine.Render(rom, level) : null;
            baseGrid = grid?.Clone();          // snapshot to diff edits against on save
            undoStack.Clear(); redoStack.Clear(); currentStroke = new();   // new grid = new history
            if (rom is not null && level is not null)
            {
                tileCaches = new uint[4][][];
                for (int p = 0; p < 4; p++)
                    tileCaches[p] = Map16.ComposeAll(rom, level.Header, levelNum, p);
            }
            else tileCaches = null;
            backdropColor = rom is not null && level is not null ? Palette.Load(rom, level.Header, levelNum).Rgba[0] : 0;
            // Layer 2: background image or object layer, drawn behind layer 1.
            bgImage = rom is not null && level is not null ? Level.DecodeBgImage(rom, levelNum) : null;
            bgCache = bgImage is not null ? Map16.ComposeAllBg(rom!, level!.Header, levelNum) : null;
            var l2objs = rom is not null && level is not null ? Level.ParseLayer2(rom, levelNum) : null;
            layer2Grid = l2objs is not null ? ObjectEngine.Render(rom!, level!.Header, l2objs) : null;
            sprites = rom is not null && level is not null ? SpriteData.Parse(rom, levelNum) : null;
            BuildMap16Sheet();
            BuildLevelCanvas();
        }
        catch { level = null; grid = null; tileCaches = null; }
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
