using ImGuiNET;

namespace PipeDream;

/// <summary>
/// Choose which background image a level's layer 2 uses. Only the addresses the ROM already
/// uses are listed: a background's page byte comes from its address ($E8FE and up = page 1),
/// so an arbitrary address would recolour every tile in it, and there is nowhere to put a new
/// stream anyway (bank $0C is where the loader looks, and its free space is a few dozen bytes).
///
/// Each row names the levels that share the background, which is how you recognise one —
/// vanilla ships no names for them, and 17 addresses mean nothing on their own.
/// </summary>
internal sealed class BackgroundPicker(EditorApp app)
{
    private readonly EditorApp app = app;

    private bool show;

    internal void Open() => show = app.rom is not null;

    internal void Draw()
    {
        if (!show) return;
        if (app.rom is not { } rom) { show = false; return; }
        if (!ImGuiCompat.BeginCenteredModal("Layer 2 background")) return;

        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 36);
        ImGui.TextDisabled("Pointing layer 2 at a background replaces any layer-2 object " +
                           "stream — a level's layer 2 is one or the other. Only addresses the " +
                           "ROM already uses are listed, because a background's palette page " +
                           "comes from its address.");
        ImGui.PopTextWrapPos();
        ImGui.Separator();

        int current = rom.Layer2IsBackground(app.levelNum)
            ? rom.Layer2Pointer(app.levelNum) & 0xFFFF : -1;
        int chosen = -1;

        if (ImGui.BeginTable("bgs", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Address");
            ImGui.TableSetupColumn("Page");
            ImGui.TableSetupColumn("Used by");
            ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();
            foreach (var (lo16, page, levels) in BgImage.Catalog(rom))
            {
                ImGui.PushID(lo16);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool isCurrent = lo16 == current;
                if (isCurrent) ImGui.TextColored(new System.Numerics.Vector4(1f, 0.7f, 0.2f, 1f), $"${lo16:X4}");
                else ImGui.Text($"${lo16:X4}");
                ImGui.TableNextColumn();
                ImGui.TextDisabled(page.ToString());
                ImGui.TableNextColumn();
                // The first few sharers are enough to recognise which background this is.
                ImGui.TextDisabled(string.Join(" ", levels.Take(6).Select(l => $"{l:X3}")) +
                                   (levels.Count > 6 ? $" +{levels.Count - 6}" : ""));
                ImGui.TableNextColumn();
                ImGui.BeginDisabled(isCurrent);
                if (ImGui.SmallButton("Use")) chosen = lo16;
                ImGui.EndDisabled();
                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        ImGui.Separator();
        if (ImGui.Button("Cancel")) { show = false; ImGui.CloseCurrentPopup(); }
        if (chosen >= 0)
        {
            show = false;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            app.session.SetLayer2Background(chosen);   // reparses; do it outside the popup
            return;
        }
        ImGui.EndPopup();
    }
}
