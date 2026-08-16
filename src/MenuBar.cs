using ImGuiNET;

namespace PipeDream;

// The main menu bar: File (open/save/inspectors/exit), Edit (undo/redo), View (toggles).
internal sealed class MenuBar(EditorApp app)
{
    private readonly EditorApp app = app;

    internal void Draw()
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Open ROM…"))
                {
                    // ponytail: hardcoded to the known test ROM until a file dialog exists.
                    app.session.LoadRom(@"C:\SMW\Projects\.resources\SMW.smc");
                }
                if (ImGui.MenuItem("Open ROM (expanded test)…"))
                {
                    app.session.LoadRom(@"C:\SMW\Projects\ShaoBase\base.smc");
                }
                if (ImGui.MenuItem("Open DM16 test ROM…"))
                {
                    app.session.LoadRom(@"C:\SMW\Projects\.resources\after.smc");
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Save DM16 edits to ROM copy", app.rom is not null && app.level is not null))
                    app.objectEditor.SaveEdits();
                if (ImGui.MenuItem("Save palette to ROM copy", app.rom is not null && app.level is not null))
                    app.paletteEditor.SavePalette();
                ImGui.Separator();
                if (ImGui.MenuItem("ROM Info", "", app.romInfoPanel.Show)) app.romInfoPanel.Show = !app.romInfoPanel.Show;
                ImGui.Separator();
                if (ImGui.MenuItem("Exit")) app.Exit();
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Edit"))
            {
                if (ImGui.MenuItem("Undo", "Ctrl+Z", false, app.history.CanUndo)) app.Undo();
                if (ImGui.MenuItem("Redo", "Ctrl+Shift+Z", false, app.history.CanRedo)) app.Redo();
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("View"))
            {
                if (ImGui.MenuItem("Palette", "", app.paletteVisible)) app.paletteVisible = !app.paletteVisible;
                ImGui.Separator();
                if (ImGui.MenuItem("Sprite overlay", "", app.showSprites))
                { app.showSprites = !app.showSprites; app.canvasFull = true; app.levelDirty = true; }
                if (ImGui.MenuItem("Animate tiles", "", app.animateTiles))
                    app.animateTiles = !app.animateTiles;
                ImGui.Separator();
                if (ImGui.MenuItem("GFX Viewer", "", app.gfxViewerPanel.Show)) app.gfxViewerPanel.Show = !app.gfxViewerPanel.Show;
                ImGui.EndMenu();
            }
            ImGui.EndMainMenuBar();
        }
    }
}
