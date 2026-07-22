using System.Numerics;
using System.Runtime.InteropServices;
using Foster.Framework;
using ImGuiNET;

namespace PipeDream;

/// <summary>
/// Read-only inspector windows (ROM info, raw GFX file viewer, per-level VRAM GFX slots).
/// Owns only its own preview textures + view state; the editor calls Draw* each frame with
/// the current ROM/level and toggles the Show* flags from its menus.
/// </summary>
public sealed class DebugPanels : IDisposable
{
    private readonly GraphicsDevice gd;
    private readonly ImGuiLayer imgui;

    public bool ShowRomInfo, ShowGfxViewer, ShowLevelGfx;

    // GFX file viewer.
    private Texture? gfxTex;
    private int gfxW, gfxH, gfxFile, gfxBpp = 3, gfxPalRow = 2;
    private (int, int, int, int) gfxKey = (-1, -1, -1, -1);
    // Per-level VRAM GFX slots.
    private readonly List<(string label, Texture tex, int w, int h)> levelGfx = new();
    private int levelGfxKey = -1;

    public DebugPanels(GraphicsDevice gd, ImGuiLayer imgui) { this.gd = gd; this.imgui = imgui; }

    /// <summary>Level changed — drop the cached Level GFX sheets so they rebuild.</summary>
    public void InvalidateLevel() => levelGfxKey = -1;

    public void DrawRomInfo(Rom? rom, string? romPath, int ratCount)
    {
        if (!ShowRomInfo) return;
        bool open = ShowRomInfo;
        if (!ImGui.Begin("ROM Info", ref open)) { ShowRomInfo = open; ImGui.End(); return; }
        ShowRomInfo = open;
        if (rom is null)
        {
            ImGui.TextDisabled("No ROM loaded.");
            ImGui.TextDisabled("File → Open ROM to begin.");
        }
        else
        {
            ImGui.Text($"File: {romPath}");
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

    // Renders a GFX file as a palette-colored 8x8 tile sheet — decompress → decode →
    // palette → Foster texture.
    public void DrawGfxViewer(Rom? rom, LevelHeader? header, int levelNum)
    {
        if (!ShowGfxViewer) return;
        bool open = ShowGfxViewer;
        if (!ImGui.Begin("GFX Viewer", ref open)) { ShowGfxViewer = open; ImGui.End(); return; }
        ShowGfxViewer = open;
        if (rom is null) { ImGui.TextDisabled("No ROM."); ImGui.End(); return; }
        ImGui.SetNextItemWidth(90);
        ImGui.InputInt($"file (0x{gfxFile:X2})", ref gfxFile); gfxFile = Math.Clamp(gfxFile, 0, Gfx.Count - 1);
        ImGui.SameLine(); ImGui.SetNextItemWidth(80); ImGui.InputInt("bpp", ref gfxBpp); gfxBpp = Math.Clamp(gfxBpp, 2, 4);
        ImGui.SameLine(); ImGui.SetNextItemWidth(80); ImGui.InputInt("pal", ref gfxPalRow); gfxPalRow = Math.Clamp(gfxPalRow, 0, 15);

        var key = (gfxFile, gfxBpp, gfxPalRow, levelNum);
        if (key != gfxKey) { gfxKey = key; BuildGfxTexture(rom, header); }
        if (gfxTex is not null)
            ImGui.Image(imgui.GetTextureID(gfxTex), new Vector2(gfxW * 3f, gfxH * 3f));
        ImGui.End();
    }

    // The GFX files loaded into VRAM for the current level: 4 FG/BG + 4 sprite slots,
    // resolved through the tileset lists and the Super GFX Bypass, as decoded tile sheets.
    public void DrawLevelGfx(Rom? rom, Level? level, int levelNum)
    {
        if (!ShowLevelGfx) return;
        bool open = ShowLevelGfx;
        if (!ImGui.Begin("Level GFX", ref open)) { ShowLevelGfx = open; ImGui.End(); return; }
        ShowLevelGfx = open;
        if (rom is null || level is null) { ImGui.TextDisabled("No level."); ImGui.End(); return; }

        if (levelGfxKey != levelNum) { levelGfxKey = levelNum; BuildLevelGfx(rom, level, levelNum); }
        foreach (var (label, tex, w, h) in levelGfx)
        {
            ImGui.Text(label);
            ImGui.Image(imgui.GetTextureID(tex), new Vector2(w * 2f, h * 2f));
            ImGui.Separator();
        }
        ImGui.End();
    }

    private void BuildLevelGfx(Rom rom, Level level, int levelNum)
    {
        foreach (var e in levelGfx) e.tex.Dispose();
        levelGfx.Clear();
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
                              new Texture(gd, w, ht, MemoryMarshal.AsBytes(px.AsSpan())), w, ht));
            }
            catch { levelGfx.Add(($"{s.name} = {file:X2} (decode failed)", MakeBlank(), 8, 8)); }
        }
    }

    private Texture MakeBlank() => new(gd, 8, 8, new byte[8 * 8 * 4]);

    private void BuildGfxTexture(Rom rom, LevelHeader? header)
    {
        try
        {
            var gfx = Gfx.DecompressFile(rom, gfxFile);
            var pal = Palette.Load(rom, header ?? default);
            var (px, w, h) = Gfx.TileSheet(gfx, gfxBpp, pal, gfxPalRow);
            gfxTex?.Dispose();
            gfxTex = new Texture(gd, w, h, MemoryMarshal.AsBytes(px.AsSpan()));
            gfxW = w; gfxH = h;
        }
        catch { gfxTex?.Dispose(); gfxTex = null; }
    }

    public void Dispose()
    {
        gfxTex?.Dispose();
        foreach (var e in levelGfx) e.tex.Dispose();
        levelGfx.Clear();
    }
}
