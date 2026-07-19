using System.Numerics;
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
                ImGui.Separator();
                if (ImGui.MenuItem("Exit")) Exit();
                ImGui.EndMenu();
            }
            ImGui.EndMainMenuBar();
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
        DrawMap16Panel();
        DrawLevelSchematic();
    }

    // Schematic view: each object drawn as a box at (screen*16+x, y), sized by width/height.
    // Not the real tiles (that needs the object engine + GFX) — a structural map of the parse.
    private void DrawLevelSchematic()
    {
        ImGui.Begin("Level Map (schematic)");
        if (level is null) { ImGui.TextDisabled("No level."); ImGui.End(); return; }

        const float S = 7f;                       // pixels per 16x16 tile
        float w = (level.Header.Screens * 16 + 1) * S;
        float hgt = 32 * S;
        ImGui.Text("standard objects = filled boxes (color by #); extended = yellow dots; screen exits = red");
        if (ImGui.BeginChild("canvas", System.Numerics.Vector2.Zero, 0,
                ImGuiWindowFlags.HorizontalScrollbar))
        {
            var dl = ImGui.GetWindowDrawList();
            var origin = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new System.Numerics.Vector2(w, hgt));

            // screen boundaries every 16 tiles
            for (int sx = 0; sx <= level.Header.Screens; sx++)
            {
                float x = origin.X + sx * 16 * S;
                dl.AddLine(new(x, origin.Y), new(x, origin.Y + hgt), 0x33FFFFFF);
            }

            foreach (var o in level.Objects)
            {
                float x = origin.X + o.AbsoluteX * S;
                float y = origin.Y + o.Y * S;
                if (o.IsScreenExit)
                {
                    dl.AddRectFilled(new(x, y), new(x + S, y + hgt), 0x400000FF);
                    dl.AddCircleFilled(new(x + S / 2, origin.Y + 4), 3, 0xFF0000FF);
                }
                else if (o.Extended)
                {
                    dl.AddCircleFilled(new(x + S / 2, y + S / 2), 2.5f, 0xFF00FFFF);
                }
                else
                {
                    uint col = ObjColor(o.Number);
                    dl.AddRectFilled(new(x, y), new(x + o.Width * S, y + o.Height * S), col);
                    dl.AddRect(new(x, y), new(x + o.Width * S, y + o.Height * S), 0x40000000);
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private static uint ObjColor(int n)
    {
        // deterministic pastel from object number
        float hf = (n * 0.6180339887f) % 1f;
        var (r, g, b) = HsvToRgb(hf, 0.55f, 0.85f);
        return 0xC0000000u | ((uint)b << 16) | ((uint)g << 8) | r;
    }

    private static (byte, byte, byte) HsvToRgb(float h, float s, float v)
    {
        float i = MathF.Floor(h * 6);
        float f = h * 6 - i;
        float p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        float r, g, b;
        switch (((int)i) % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

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
        ImGui.Text($"Layer1 @ ${level.DataPointer:X6}   tileset {h.Tileset}   mode {h.LevelMode}   " +
                   $"screens {h.Screens}");
        ImGui.Text($"palettes: FG {h.FgPalette}  BG {h.BgPalette}  sprite {h.SpritePalette}  " +
                   $"back {h.BackAreaColor}   music {h.Music}   sprite-set {h.SpriteSet}");
        ImGui.Text($"Layer 2 @ ${rom.Layer2Pointer(levelNum):X6}" +
                   (rom.Layer2IsBackground(levelNum) ? " (background)" : " (objects)") +
                   $"   Sprites @ ${rom.SpritePointer(levelNum):X6}");
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
        }
        catch { level = null; grid = null; }
    }

    // Map16 grid: each expanded tile drawn as a cell colored by its Map16 index.
    // Not real GFX yet — proves the object engine fills the tilemap. Markers = unported handlers.
    private void DrawMap16Panel()
    {
        ImGui.Begin("Level Map (Map16)");
        if (grid is null) { ImGui.TextDisabled("No level."); ImGui.End(); return; }
        int real = 0, mark = 0;
        foreach (var t in grid.Tiles)
            if (t != Map16Grid.Empty) { if ((t & ObjectEngine.Marker) != 0) mark++; else real++; }
        ImGui.Text($"{real} tiles placed, {mark} unimplemented (magenta)");
        const float S = 7f;
        if (ImGui.BeginChild("m16canvas", System.Numerics.Vector2.Zero, 0, ImGuiWindowFlags.HorizontalScrollbar))
        {
            var dl = ImGui.GetWindowDrawList();
            var o = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new System.Numerics.Vector2(grid.Width * S, grid.Height * S));
            for (int y = 0; y < grid.Height; y++)
                for (int x = 0; x < grid.Width; x++)
                {
                    int t = grid.Get(x, y);
                    if (t == Map16Grid.Empty) continue;
                    uint col = (t & ObjectEngine.Marker) != 0 ? 0xFFFF00FFu : TileColor(t & 0x3FFF);
                    dl.AddRectFilled(new(o.X + x * S, o.Y + y * S),
                                     new(o.X + x * S + S - 0.5f, o.Y + y * S + S - 0.5f), col);
                }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private static uint TileColor(int tile)
    {
        float hf = (tile * 0.6180339887f) % 1f;
        var (r, g, b) = HsvToRgb(hf, 0.5f, tile < 0x100 ? 0.9f : 0.6f);
        return 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r;
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
