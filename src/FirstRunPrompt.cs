using ImGuiNET;

namespace PipeDream;

/// <summary>
/// First-run flow: until the global config knows where an unedited SMW ROM lives, a
/// blocking modal asks for it. The pick is hash-checked (headerless SHA-256 vs the known
/// vanilla US image — a mismatch warns but is allowed) and saved to the config.
/// </summary>
internal sealed class FirstRunPrompt(EditorApp app)
{
    private readonly EditorApp app = app;
    private string? pickedPath;
    private string status = "";

    internal void Draw()
    {
        if (app.config.VanillaRomPath is not null) return;
        if (!ImGuiCompat.BeginCenteredModal("Welcome to Pipe Dream")) return;
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24);
        ImGui.TextWrapped("Locate an unedited Super Mario World ROM (.smc/.sfc). Projects start " +
                          "from a private copy of it; the ROM itself is never part of shared files.");
        ImGui.Spacing();
        if (ImGui.Button("Browse…") && !FileDialog.Busy)
        {
            status = "Opening file dialog…";
            FileDialog.OpenFile("SNES ROM", "smc;sfc", app.SdlWindowHandle, OnPicked);
        }
        if (pickedPath is not null) { ImGui.SameLine(); ImGui.TextUnformatted(Path.GetFileName(pickedPath)); }
        if (status.Length > 0) ImGui.TextWrapped(status);
        ImGui.Spacing();
        ImGui.BeginDisabled(pickedPath is null);
        if (ImGui.Button("Save and continue"))
        {
            app.config.VanillaRomPath = pickedPath;
            app.config.Save();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.PopTextWrapPos();
        ImGui.EndPopup();
    }

    private void OnPicked(string? path)
    {
        if (path is null)
        {
            if (FileDialog.LastError is { } err) status = "Dialog error: " + err;
            return;
        }
        try
        {
            pickedPath = path;
            status = RomHash.HeaderlessSha256File(path) == RomHash.VanillaUsSha256
                ? "Verified: vanilla Super Mario World (U)."
                : "Warning: not the known vanilla US ROM. Projects pin the exact file you pick.";
        }
        catch (Exception e) { pickedPath = null; status = "Could not read file: " + e.Message; }
    }
}
