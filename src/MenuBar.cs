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
                if (ImGui.MenuItem("New Project…")) app.projectWizard.BeginNew();
                if (ImGui.MenuItem("Open Project…")) app.projectWizard.BeginOpen();
                if (ImGui.BeginMenu("Recent Projects", app.config.RecentProjects.Count > 0))
                {
                    // Snapshot: opening a project reorders the recents list mid-iteration.
                    foreach (var r in app.config.RecentProjects.ToArray())
                        if (ImGui.MenuItem(r))
                        {
                            if (File.Exists(r)) app.projectWizard.OpenPath(r);
                            else { app.config.RecentProjects.Remove(r); app.config.Save(); }
                        }
                    ImGui.EndMenu();
                }
                ImGui.Separator();
                // Raw-ROM inspection (no project): view/debug any ROM without edit persistence.
                if (ImGui.MenuItem("Open ROM…") && !FileDialog.Busy)
                    FileDialog.OpenFile("SNES ROM", "smc;sfc", app.SdlWindowHandle,
                        p => { if (p is not null) { app.project = null; app.session.LoadRom(p); } });
                ImGui.Separator();
                // Build applies the project snapshot to a FRESH base copy; the session
                // ROM is never mutated by saving (the project file is the save).
                if (ImGui.MenuItem("Build ROM", app.project is not null && app.rom is not null))
                {
                    app.project!.Save();   // flush current edits into the snapshot first
                    var (status, _) = RomBuilder.Build(app.project);
                    app.saveStatus = status;
                }
                if (ImGui.MenuItem("Export BPS Patch", app.project is not null && app.rom is not null))
                {
                    app.project!.Save();
                    var (status, _) = RomBuilder.ExportBps(app.project, app.config.VanillaRomPath);
                    app.saveStatus = status;
                }
                // Deliberate base migration: old prepped projects keep their frozen prep
                // until the user opts in (new stamps change the pinned base hash).
                if (ImGui.MenuItem($"Upgrade base to prep v{RomPrep.Version}",
                        app.project?.CanUpgradeBasePrep == true))
                {
                    app.project!.Save();       // flush edits before the base swap
                    if (app.project.UpgradeBasePrep(app.config.VanillaRomPath) is { } prob)
                        app.saveStatus = "upgrade failed: " + prob;
                    else
                    {
                        app.saveStatus = $"base upgraded to prep v{RomPrep.Version}";
                        app.projectWizard.OpenPath(app.project.FilePath);   // reload on the new base
                    }
                }
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
