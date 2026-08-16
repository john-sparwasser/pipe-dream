using ImGuiNET;

namespace PipeDream;

/// <summary>
/// Read-only ROM info inspector window (File → ROM Info). Owns only its Show flag; the
/// editor calls Draw each frame with the current ROM and toggles Show from its menu.
/// </summary>
internal sealed class RomInfoPanel
{
    public bool Show;

    public void Draw(Rom? rom, string? romPath, int ratCount)
    {
        if (!Show) return;
        bool open = Show;
        if (!ImGui.Begin("ROM Info", ref open)) { Show = open; ImGui.End(); return; }
        Show = open;
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
}
