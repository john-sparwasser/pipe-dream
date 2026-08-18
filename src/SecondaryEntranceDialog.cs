using ImGuiNET;

namespace PipeDream;

/// <summary>
/// Editor for one secondary entrance — the destination side of a secondary screen exit.
/// There are 512 of them and they are global (any level's exit may point at any index), so
/// this edits one index at a time rather than listing them all.
///
/// Edits write straight into the session ROM and record the index in the project, matching
/// how Map16 slots work: the bytes are re-read from the ROM at save time, which makes
/// undo/redo fall out for free.
/// </summary>
internal sealed class SecondaryEntranceDialog(EditorApp app)
{
    private readonly EditorApp app = app;

    private bool show;
    private int index;
    private SecondaryEntrance edit;

    internal void Open(int entranceIndex)
    {
        if (app.rom is null) return;
        index = Math.Clamp(entranceIndex, 0, Rom.SecondaryEntranceCount - 1);
        edit = app.rom.ReadSecondaryEntrance(index);
        show = true;
    }

    internal void Draw()
    {
        if (!show) return;
        if (app.rom is null) { show = false; return; }
        if (!ImGuiCompat.BeginCenteredModal("Secondary Entrance")) return;

        // Switching index abandons unapplied edits — reload from the ROM so the fields
        // always describe the entrance named above them.
        int idx = index;
        if (HexField("Entrance", ref idx, Rom.SecondaryEntranceCount - 1, "%03X") && idx != index)
        {
            index = idx;
            edit = app.rom.ReadSecondaryEntrance(index);
        }
        // The index is 9 bits: an exit supplies the low byte, bit 8 comes from the player's
        // submap flag. So exit byte $BB reaches record $0BB from the main map and $1BB from
        // a submap — edit the wrong half and the exit appears to do nothing.
        ImGui.TextDisabled(index < 0x100
            ? $"reached from the main map (a submap exit with the same byte uses ${index + 0x100:X3})"
            : $"reached from a submap (from the main map the same byte uses ${index - 0x100:X3})");
        ImGui.SameLine();
        if (ImGui.SmallButton("go to pair")) { index ^= 0x100; edit = app.rom.ReadSecondaryEntrance(index); }
        ImGui.Separator();

        int dest = edit.DestinationLevel;
        if (HexField("Destination level", ref dest, 0xFF, "%02X")) edit = edit with { DestinationLevel = dest };
        ImGui.TextDisabled("levels $000-$0FF only — the high bit also comes from the submap state");
        ImGui.Separator();

        Field("Mario X", edit.MarioX, 0, 7, v => edit with { MarioX = v });
        Field("Mario Y", edit.MarioY, 0, 15, v => edit with { MarioY = v });
        Field("Screen boundary Y", edit.ScreenBoundaryY, 0, 3, v => edit with { ScreenBoundaryY = v });
        Field("Vertical scroll", edit.VerticalScroll, 0, 3, v => edit with { VerticalScroll = v });
        Field("Entrance action", edit.EntranceAction, 0, 7, v => edit with { EntranceAction = v });

        ImGui.Separator();
        ImGui.TextDisabled("bytes " + Convert.ToHexString(edit.ToBytes()));

        if (ImGui.Button("Apply")) { Commit(); show = false; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { show = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    private void Commit()
    {
        if (app.rom is null || app.rom.ReadSecondaryEntrance(index) == edit) return;
        var before = app.rom.ReadSecondaryEntrance(index);
        int at = index;
        void Apply(SecondaryEntrance e)
        {
            app.rom!.WriteSecondaryEntrance(at, e);
            app.project?.Data.Entrances.TryAdd(at.ToString("X3"), "");   // captured; bytes re-read at save
            app.project?.MarkDirty();
        }
        Apply(edit);
        // Entrances are GLOBAL like Map16 defs, so this marks the project dirty via
        // history.Changed and never sets currentLevelTouched.
        var after = edit;
        app.history.Push(() => Apply(before), () => Apply(after));
    }

    private void Field(string label, int value, int min, int max, Func<int, SecondaryEntrance> set)
    {
        if (ImGuiCompat.Slider(label, value, min, max, out int v)) edit = set(v);
    }

    private static bool HexField(string label, ref int value, int max, string fmt)
    {
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8);
        return ImGuiCompat.HexInput(label, ref value, max, fmt);
    }
}
