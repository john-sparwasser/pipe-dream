using ImGuiNET;

namespace PipeDream;

/// <summary>
/// New Project / Open Project flows. New: pick a folder + base ROM (defaults to the
/// configured vanilla; any ROM allowed with a warning — the hash is pinned either way),
/// then the base is copied in and project.pdp written. Open: load a .pdp; when its
/// base.smc is missing or mismatched (a shared bare .pdp), a locate-base recovery modal
/// verifies a user-located ROM against the pinned hash and copies it in.
/// </summary>
internal sealed class ProjectWizard(EditorApp app)
{
    private readonly EditorApp app = app;

    // New Project modal state
    private bool showNew;
    private string folder = "", baseRom = "", status = "";

    // Locate-base recovery state (Open Project found no valid base.smc)
    private Project? pendingOpen;
    private string recoverStatus = "";

    internal void BeginNew()
    {
        showNew = true;
        folder = "";
        baseRom = app.config.VanillaRomPath ?? "";
        status = baseRom.Length > 0 ? DescribeBase(baseRom) : "";
    }

    internal void BeginOpen()
    {
        if (FileDialog.Busy) return;
        FileDialog.OpenFile("Pipe Dream project", "pdp", app.SdlWindowHandle,
                            p => { if (p is not null) OpenPath(p); });
    }

    /// <summary>Open a project by .pdp path (dialog pick or recents entry).</summary>
    internal void OpenPath(string pdpPath)
    {
        try
        {
            var p = Project.Open(pdpPath);
            if (p.ValidateBase() is { } problem)
            {
                pendingOpen = p;
                recoverStatus = problem;
                return;
            }
            Adopt(p);
        }
        catch (Exception e) { app.saveStatus = "open project failed: " + e.Message; }
    }

    private void Adopt(Project p)
    {
        app.project = p;
        p.SyncBeforeSave = app.session.SyncProject;
        app.config.TouchRecentProject(p.FilePath);
        app.session.LoadRom(p.BaseRomPath);
        int pv = p.Data.BaseRom.PrepVersion;
        // Name what the OLD base actually lacks, so the notice stays true as versions land.
        string missing = pv < 2 ? "GFX overrides are editor-preview only"
                                : "Map16 pages past 0x0F cannot be created";
        app.saveStatus = pv is >= 1 && pv < RomPrep.Version
            ? $"project '{p.Name}' opened — base uses prep v{pv}: {missing} (File → Upgrade base to prep v{RomPrep.Version})"
            : $"project '{p.Name}' opened";
    }

    internal void Draw()
    {
        DrawNewModal();
        DrawRecoverModal();
    }

    private void DrawNewModal()
    {
        if (!showNew) return;
        if (!ImGuiCompat.BeginCenteredModal("New Project")) return;
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);

        ImGui.TextUnformatted("Project folder (holds project.pdp + a private base ROM copy):");
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 24);
        ImGui.InputText("##folder", ref folder, 512);
        ImGui.SameLine();
        if (ImGui.Button("Browse…##f") && !FileDialog.Busy)
            FileDialog.OpenFolder(app.SdlWindowHandle, p => { if (p is not null) folder = p; });

        ImGui.Spacing();
        ImGui.TextUnformatted("Base ROM (unedited SMW recommended):");
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 24);
        if (ImGui.InputText("##base", ref baseRom, 512)) status = DescribeBase(baseRom);
        ImGui.SameLine();
        if (ImGui.Button("Browse…##b") && !FileDialog.Busy)
            FileDialog.OpenFile("SNES ROM", "smc;sfc", app.SdlWindowHandle,
                                p => { if (p is not null) { baseRom = p; status = DescribeBase(p); } });
        if (status.Length > 0) ImGui.TextWrapped(status);

        ImGui.Spacing();
        bool ready = folder.Length > 0 && File.Exists(baseRom) &&
                     !File.Exists(Path.Combine(folder, Project.FileName));
        if (folder.Length > 0 && File.Exists(Path.Combine(folder, Project.FileName)))
            ImGui.TextWrapped("That folder already contains a project — use Open Project instead.");
        ImGui.BeginDisabled(!ready);
        if (ImGui.Button("Create"))
        {
            try
            {
                Adopt(Project.Create(folder, baseRom));
                showNew = false;
                ImGui.CloseCurrentPopup();
            }
            catch (Exception e) { status = "create failed: " + e.Message; }
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { showNew = false; ImGui.CloseCurrentPopup(); }
        ImGui.PopTextWrapPos();
        ImGui.EndPopup();
    }

    private void DrawRecoverModal()
    {
        if (pendingOpen is null) return;
        if (!ImGuiCompat.BeginCenteredModal("Locate base ROM")) return;
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
        ImGui.TextWrapped($"'{pendingOpen.Name}': {recoverStatus}");
        ImGui.TextWrapped($"Pinned base: {pendingOpen.Data.BaseRom.Title} " +
                          $"({pendingOpen.Data.BaseRom.Size / 1024} KB, sha256 {pendingOpen.Data.BaseRom.Sha256[..12]}…)");
        ImGui.Spacing();
        if (ImGui.Button("Browse…") && !FileDialog.Busy)
            FileDialog.OpenFile("SNES ROM", "smc;sfc", app.SdlWindowHandle, p =>
            {
                if (p is null || pendingOpen is null) return;
                if (pendingOpen.AdoptBase(p) is { } problem) { recoverStatus = problem; return; }
                var done = pendingOpen;
                pendingOpen = null;
                Adopt(done);
            });
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { pendingOpen = null; ImGui.CloseCurrentPopup(); }
        ImGui.PopTextWrapPos();
        ImGui.EndPopup();
    }

    private static string DescribeBase(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            return RomHash.HeaderlessSha256File(path) == RomHash.VanillaUsSha256
                ? "Verified: vanilla Super Mario World (U). The base copy will be prepared " +
                  "automatically for full editing (Map16, tile placement, palettes, sprites)."
                : "Warning: not the known vanilla US ROM — it is used as-is (LM-prepared bases " +
                  "work fully), and collaborators will need this exact file.";
        }
        catch (Exception e) { return "could not read: " + e.Message; }
    }
}
