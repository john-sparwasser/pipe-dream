using System.Numerics;
using System.Runtime.InteropServices;
using Foster.Framework;
using ImGuiNET;

namespace PipeDream;

/// <summary>
/// Raw GFX file viewer window (View → GFX Viewer). Owns only its own preview texture +
/// view state; the editor calls Draw each frame with the current ROM/level and toggles
/// the Show flag from its menu.
/// </summary>
internal sealed class GfxViewerPanel(GraphicsDevice gd, ImGuiLayer imgui) : IDisposable
{
    private readonly GraphicsDevice gd = gd;
    private readonly ImGuiLayer imgui = imgui;

    public bool Show;

    // GFX file viewer.
    private Texture? gfxTex;
    private int gfxW, gfxH, gfxFile, gfxBpp = 3, gfxPalRow = 2;
    private (int, int, int, int) gfxKey = (-1, -1, -1, -1);

    // Renders a GFX file as a palette-colored 8x8 tile sheet — decompress → decode →
    // palette → Foster texture.
    public void Draw(Rom? rom, LevelHeader? header, int levelNum)
    {
        if (!Show) return;
        bool open = Show;
        if (!ImGui.Begin("GFX Viewer", ref open)) { Show = open; ImGui.End(); return; }
        Show = open;
        if (rom is null) { ImGui.TextDisabled("No ROM."); ImGui.End(); return; }
        ImGui.SetNextItemWidth(90);
        ImGui.InputInt($"file (0x{gfxFile:X2})", ref gfxFile); gfxFile = Math.Clamp(gfxFile, 0, 0xFFF);
        ImGui.SameLine(); ImGui.SetNextItemWidth(80); ImGui.InputInt("bpp", ref gfxBpp); gfxBpp = Math.Clamp(gfxBpp, 2, 4);
        ImGui.SameLine(); ImGui.SetNextItemWidth(80); ImGui.InputInt("pal", ref gfxPalRow); gfxPalRow = Math.Clamp(gfxPalRow, 0, 15);

        var key = (gfxFile, gfxBpp, gfxPalRow, levelNum);
        if (key != gfxKey) { gfxKey = key; BuildGfxTexture(rom, header); }
        if (gfxTex is not null)
            ImGui.Image(imgui.GetTextureID(gfxTex), new Vector2(gfxW * 3f, gfxH * 3f));
        ImGui.End();
    }

    private void BuildGfxTexture(Rom rom, LevelHeader? header)
    {
        try
        {
            // Cached (not DecompressFile) so ExGFX ids and project imports resolve too;
            // missing ids show as no texture.
            var gfx = Gfx.Cached(rom, gfxFile) ?? throw new InvalidDataException("no file");
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
    }
}
