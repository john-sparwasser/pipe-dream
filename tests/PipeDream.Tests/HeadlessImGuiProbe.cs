using ImGuiNET;
using Xunit;

namespace PipeDream.Tests;

/// <summary>Feasibility probe: can ImGui lay out and hit-test with no GPU at all?</summary>
public class HeadlessImGuiProbe
{
    [Fact]
    public void imgui_lays_out_and_hit_tests_without_a_graphics_device()
    {
        var ctx = ImGui.CreateContext();
        ImGui.SetCurrentContext(ctx);
        try
        {
            var io = ImGui.GetIO();
            io.DisplaySize = new System.Numerics.Vector2(1280, 720);
            io.DeltaTime = 1f / 60f;
            // ImGui refuses to render without a font atlas; build one and hand it a fake id.
            io.Fonts.GetTexDataAsRGBA32(out nint _, out int w, out int h, out int _);
            io.Fonts.SetTexID(1);
            Assert.True(w > 0 && h > 0);

            bool clicked = false;
            // ImGui needs the cursor to hover for a frame before a press counts, and a button
            // fires on RELEASE — so a click is at minimum hover, press, release.
            for (int frame = 0; frame < 6; frame++)
            {
                io.AddMousePosEvent(60, 60);
                io.AddMouseButtonEvent(0, frame is 2 or 3);

                ImGui.NewFrame();
                ImGui.SetNextWindowPos(System.Numerics.Vector2.Zero);
                ImGui.SetNextWindowSize(new System.Numerics.Vector2(400, 300));
                ImGui.Begin("probe");
                ImGui.SetCursorPos(new System.Numerics.Vector2(40, 40));
                if (ImGui.Button("hit me", new System.Numerics.Vector2(80, 40))) clicked = true;
                ImGui.End();
                ImGui.Render();                       // builds draw data; nothing rasterizes it
            }

            Assert.True(clicked, "a synthetic click did not reach a button");
            Assert.True(ImGui.GetDrawData().CmdListsCount >= 0);
        }
        finally { ImGui.DestroyContext(ctx); }
    }
}
