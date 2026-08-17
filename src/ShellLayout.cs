using System.Numerics;
using ImGuiNET;

namespace PipeDream;

// Fixed shell: left palette (hideable, resizable) + main view fill the whole work area,
// plus the palette drawer's tab bar (which hosts the editors' picker tabs) and the Map16
// props drawer arrangement.
internal sealed class ShellLayout(EditorApp app)
{
    private readonly EditorApp app = app;

    // Layout state
    private bool paletteCollapsed;          // minimized to a thin strip (the < / > corner toggle)
    private float paletteWidth = 320f;      // remembered across collapse/expand

    // The left palette: pickers that feed the main view. Tabs per palette kind.
    // Selecting a tab switches the canvas edit mode (Sprites tab -> sprite mode,
    // Map16/Objects -> layer 1); Esc's mode toggle selects the matching tab back.
    internal int paletteTab;             // 0 Map16, 1 Sprites, 2 Objects, 3 Palette, 4 GFX
    internal int pendingTabSelect = -1;  // tab to force-select (mode changed via Esc)

    internal void DrawMainLayout()
    {
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.WorkPos);
        ImGui.SetNextWindowSize(vp.WorkSize);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);   // panels flush to the shell edges
        ImGui.Begin("##shell", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                               ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse |
                               ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus |
                               ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar(2);
        if (app.paletteVisible)
        {
            float bw = ImGui.GetFrameHeight();
            if (paletteCollapsed)
            {
                // Minimized: a thin strip holding just the expand toggle, flush at the top.
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
                // Distinct id so it doesn't clobber the resizable "palette" child's stored width.
                ImGui.BeginChild("palettemin", new Vector2(bw, 0), ImGuiChildFlags.None);
                ImGui.SetCursorPosY(7);                      // same top margin as the expand side
                if (ImGui.Button(">##palexpand", new Vector2(bw, bw))) paletteCollapsed = false;
                ImGui.EndChild();
                ImGui.PopStyleVar();
            }
            else
            {
                // No full border on the palette — just a 1px separator on its right edge
                // (AlwaysUseWindowPadding keeps the inner padding the Border flag provided).
                ImGui.BeginChild("palette", new Vector2(paletteWidth, 0),
                                 ImGuiChildFlags.ResizeX | ImGuiChildFlags.AlwaysUseWindowPadding);
                paletteWidth = ImGui.GetWindowWidth();      // keep the resized width across collapse
                DrawPalette();
                // Collapse toggle in the palette's top-right corner, inset by the same
                // 7px margin the level header row uses so it isn't clipped by the edge.
                ImGui.SetCursorPos(new Vector2(ImGui.GetWindowWidth() - bw - 7, 7));
                if (ImGui.Button("<##palcollapse", new Vector2(bw, bw))) paletteCollapsed = true;
                ImGui.EndChild();
            }
            var pMin = ImGui.GetItemRectMin();
            var pMax = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddLine(new Vector2(pMax.X - 1, pMin.Y),
                                              new Vector2(pMax.X - 1, pMax.Y), 0xFF404040u, 1f);
            // Level view flush against the palette panel (no item gap between the two).
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
            ImGui.SameLine();
            ImGui.PopStyleVar();
        }
        // Map16 edit mode gets a right drawer: properties of the selected tile(s).
        bool m16Props = app.canvasView == EditorApp.CanvasView.Map16 && app.rom is not null;
        ImGui.BeginChild("mainview", m16Props ? new Vector2(-210, 0) : Vector2.Zero);
        app.viewport.DrawLevelView();
        ImGui.EndChild();
        if (m16Props)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
            ImGui.SameLine();
            ImGui.PopStyleVar();
            ImGui.BeginChild("m16props", new Vector2(210, 0), ImGuiChildFlags.AlwaysUseWindowPadding);
            app.map16Editor.DrawMap16PropsDrawer();
            ImGui.EndChild();
            var rMin = ImGui.GetItemRectMin();
            ImGui.GetWindowDrawList().AddLine(rMin, new Vector2(rMin.X, ImGui.GetItemRectMax().Y), 0xFF404040u, 1f);
        }
        ImGui.End();
    }

    private void DrawPalette()
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 7);   // same top margin as the level header
        if (app.canvasView == EditorApp.CanvasView.Map16)
        {
            // Map16 edit mode: the drawer IS the 8x8 GFX palette (canvas = the Map16 sheet).
            app.map16Editor.DrawGfxPaletteDrawer();
            return;
        }
        if (app.canvasView == EditorApp.CanvasView.Gfx)
        {
            // GFX edit mode: the drawer is the color picker + the level's bin quick-list.
            app.gfxEditor.DrawDrawer();
            return;
        }
        if (ImGui.BeginTabBar("palettetabs"))
        {
            PaletteTabItem(0, "Map16", EditorApp.EditMode.Layer1, app.map16Editor.DrawMap16Tab);
            PaletteTabItem(1, "Sprites", EditorApp.EditMode.Sprites, app.spriteEditor.DrawSpritesTab);
            PaletteTabItem(2, "Objects", EditorApp.EditMode.Layer1, app.objectEditor.DrawObjectsTab);
            PaletteTabItem(3, "Palette", null, app.paletteEditor.DrawPaletteTab);
            PaletteTabItem(4, "GFX", null, app.session.DrawGfxTab);
            ImGui.EndTabBar();
        }
        pendingTabSelect = -1;
    }

    private void PaletteTabItem(int idx, string label, EditorApp.EditMode? mode, Action draw)
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
            if (mode is { } m && app.editMode != m)
            {
                app.editMode = m;
                app.dragStart = app.dragEnd = null; app.moveDrag = null; app.resizeDrag = null; app.selSprites.Clear();
                app.spriteEditor.DropSpriteGhost();
            }
        }
        draw();
        ImGui.EndTabItem();
    }
}
