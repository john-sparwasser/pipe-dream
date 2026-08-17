using System.Numerics;
using ImGuiNET;

namespace PipeDream;

// The main view: the composed level (Map16 grid with real tile graphics) — level number
// header, the Level ⇄ Map16 canvas toggle, scroll/zoom/wheel handling, and handing each
// frame to the active edit tool.
internal sealed class LevelViewport(EditorApp app)
{
    private readonly EditorApp app = app;

    private float viewSlideT;                      // toggle knob animation (0 Level → 1 Map16)
    private const float CanvasZoom = 1f;  // level canvas (native size)

    internal void DrawLevelView()
    {
        if (app.rom is null)
        {
            // The shell has zero padding, so give the placeholder its own margin.
            ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(12, 12));
            ImGui.TextDisabled("No ROM loaded.  File → Open ROM to begin.");
            return;
        }
        ImGui.Dummy(new Vector2(0, 1));   // breathing room above the header row
                                          // (total = 1 + ItemSpacing.Y 6 ≈ 7px)
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 7);   // matching left margin
        ImGui.SetNextItemWidth(120);
        unsafe
        {
            int v = app.levelNum, step = 1;
            if (ImGui.InputScalar("Level", ImGuiDataType.S32, (IntPtr)(&v), (IntPtr)(&step),
                                  IntPtr.Zero, "%03X", ImGuiInputTextFlags.CharsHexadecimal))
                app.session.SwitchLevel(Math.Clamp(v, 0, Rom.LevelCount - 1));   // stashes project edits first
        }
        ImGui.SameLine();
        if (ImGui.Button("Reload")) app.session.ParseLevel();
        ImGui.SameLine();
        ImGui.BeginDisabled(app.level is null);
        if (ImGui.Button("Properties")) app.levelProps.Open();
        ImGui.SameLine();
        if (ImGui.Button("Exits")) app.levelExits.Open();
        ImGui.EndDisabled();
        ImGui.SameLine();
        DrawViewToggle();
        if (app.canvas.TexFor(0) is null || app.grid is null) { ImGui.TextDisabled("No level rendered."); return; }
        if (app.saveStatus.Length > 0) ImGui.TextDisabled(app.saveStatus);
        if (app.canvasView == EditorApp.CanvasView.Map16) { app.map16Editor.DrawMap16Canvas(); return; }
        if (app.canvasView == EditorApp.CanvasView.Gfx) { app.gfxEditor.DrawCanvas(); return; }
        // Horizontal levels scroll left/right with the wheel (Shift+wheel = vertical);
        // vertical levels keep the default up/down wheel.
        bool verticalLvl = app.rom is not null && app.level is not null && app.rom.IsVerticalMode(app.level.Header.LevelMode);
        var canvasFlags = ImGuiWindowFlags.HorizontalScrollbar |
                          (verticalLvl ? 0 : ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 1);   // +1px under the header row
        if (ImGui.BeginChild("lvlcanvas", System.Numerics.Vector2.Zero, 0, canvasFlags))
        {
            float z = app.SnappedZoom(CanvasZoom);
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
            DrawDeskBackdrop();

            // Center the level in the viewport when it's smaller than the view.
            var imgSize = new Vector2(app.canvas.PxW * z, app.canvas.PxH * z);
            var avail0 = ImGui.GetContentRegionAvail();
            if (imgSize.Y < avail0.Y) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (avail0.Y - imgSize.Y) / 2);
            if (imgSize.X < avail0.X) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail0.X - imgSize.X) / 2);

            app.SnapCursorToPixel();
            var origin = ImGui.GetCursorScreenPos();
            ImGui.Image(app.imgui!.GetTextureID(app.canvas.TexFor(app.AnimPhase)!), imgSize);
            float cs = 16 * z;
            var dl = ImGui.GetWindowDrawList();

            // Hand the frame to the active tool: it owns highlights + all interaction.
            int hcx = 0, hcy = 0, hpx = 0, hpy = 0;
            bool hovered = false;
            if (ImGui.IsItemHovered())
            {
                var m = ImGui.GetMousePos();
                hcx = (int)((m.X - origin.X) / cs); hcy = (int)((m.Y - origin.Y) / cs);
                hpx = (int)((m.X - origin.X) / z); hpy = (int)((m.Y - origin.Y) / z);   // level-pixel (lasso, no grid snap)
                hovered = hcx >= 0 && hcx < app.grid!.Width && hcy >= 0 && hcy < app.grid.Height;
            }
            app.ActiveTool.Frame(new EditTool.CanvasCtx(origin, cs, dl, hcx, hcy, hpx, hpy, hovered, verticalLvl));
            ImGui.EndChild();
        }
    }

    // Desk backdrop: 45°-rotated black/dark-grey checkerboard, static in view space
    // (doesn't scroll with the content); the canvas content draws over it.
    internal static void DrawDeskBackdrop()
    {
        var wdl = ImGui.GetWindowDrawList();
        var wp0 = ImGui.GetWindowPos();
        var wsz = ImGui.GetWindowSize();
        wdl.AddRectFilled(wp0, wp0 + wsz, 0xFF101010u);
        const float dh = 16f;                          // diamond half-diagonal
        for (int rw = 1; rw * dh <= wsz.Y + dh; rw += 2)
            for (int cl = 1; cl * dh <= wsz.X + dh; cl += 2)
            {
                var dc = new Vector2(wp0.X + cl * dh, wp0.Y + rw * dh);
                wdl.AddQuadFilled(dc with { Y = dc.Y - dh }, dc with { X = dc.X + dh },
                                  dc with { Y = dc.Y + dh }, dc with { X = dc.X - dh }, 0xFF1B1B1Bu);
            }
    }

    // Sliding three-state Level | Map16 | GFX switch in the canvas header: a pill with a
    // knob that animates between the thirds; clicking a segment selects that canvas view.
    private static readonly string[] ViewLabels = { "Level", "Map16", "GFX" };

    private void DrawViewToggle()
    {
        float h = ImGui.GetFrameHeight();
        var size = new Vector2(210, h);
        float seg = size.X / 3;
        var p0 = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##viewtoggle", size))
        {
            var clicked = (EditorApp.CanvasView)Math.Clamp((int)((ImGui.GetMousePos().X - p0.X) / seg), 0, 2);
            if (clicked != app.canvasView)
            {
                app.canvasView = clicked;
                // Any switch drops EVERY mode's in-flight drag/stroke state.
                app.dragStart = app.dragEnd = null; app.moveDrag = null; app.resizeDrag = null;   // level
                app.map16Editor.m16Lasso = null; app.map16Editor.m16Move = null;                  // Map16
                app.gfxEditor.AbortStroke();       // GFX: uncommitted write-through bytes reverted
            }
        }
        bool hovered = ImGui.IsItemHovered();
        float target = (int)app.canvasView;
        float dt = ImGui.GetIO().DeltaTime;
        viewSlideT = Math.Clamp(viewSlideT + Math.Sign(target - viewSlideT) * dt * 8f, 0f, 2f);
        if (Math.Abs(target - viewSlideT) < dt * 8f) viewSlideT = target;

        var dl = ImGui.GetWindowDrawList();
        var p1 = p0 + size;
        dl.AddRectFilled(p0, p1, 0xFF262626u, h / 2);
        dl.AddRect(p0, p1, hovered ? 0xFF666666u : 0xFF404040u, h / 2);
        var k0 = new Vector2(p0.X + viewSlideT * seg + 2, p0.Y + 2);
        dl.AddRectFilled(k0, new Vector2(k0.X + seg - 4, p1.Y - 2), 0xFF884400u, (h - 4) / 2);
        for (int i = 0; i < ViewLabels.Length; i++)
        {
            var sz = ImGui.CalcTextSize(ViewLabels[i]);
            dl.AddText(new Vector2(p0.X + i * seg + (seg - sz.X) / 2, p0.Y + (h - sz.Y) / 2),
                       Math.Abs(viewSlideT - i) < 0.5f ? 0xFFFFFFFFu : 0xFF808080u, ViewLabels[i]);
        }
    }
}
