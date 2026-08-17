using ImGuiNET;

namespace PipeDream;

/// <summary>
/// The level header editor (CONTRACT §4): the 5 header bytes as named fields. Edits are
/// staged locally and only committed on Apply — every field the header carries forces a
/// full reparse (tileset drives object dispatch, the palette fields drive every tile
/// cache), which is far too expensive to run per slider tick.
/// </summary>
internal sealed class LevelPropertiesDialog(EditorApp app)
{
    private readonly EditorApp app = app;

    private bool show;
    private LevelHeader edit;

    internal void Open()
    {
        if (app.level is null) return;
        edit = app.level.Header;
        show = true;
    }

    internal void Draw()
    {
        if (!show) return;
        if (app.level is null) { show = false; return; }
        ImGui.OpenPopup("Level Properties");
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing,
                               new System.Numerics.Vector2(0.5f, 0.5f));
        bool open;
        unsafe
        {
            var name = "Level Properties\0"u8;
            fixed (byte* n = name)
                open = ImGuiNative.igBeginPopupModal(n, null, ImGuiWindowFlags.AlwaysAutoResize) != 0;
        }
        if (!open) return;

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
        Field("Screens", edit.Screens, 1, 32, v => edit with { Screens = v });
        Field("Level mode", edit.LevelMode, 0, 31, v => edit with { LevelMode = v });
        Field("Tileset", edit.Tileset, 0, 15, v => edit with { Tileset = v });
        Field("Sprite set", edit.SpriteSet, 0, 15, v => edit with { SpriteSet = v });
        ImGui.Separator();
        Field("FG palette", edit.FgPalette, 0, 7, v => edit with { FgPalette = v });
        Field("BG palette", edit.BgPalette, 0, 7, v => edit with { BgPalette = v });
        Field("Sprite palette", edit.SpritePalette, 0, 7, v => edit with { SpritePalette = v });
        Field("Back area color", edit.BackAreaColor, 0, 7, v => edit with { BackAreaColor = v });
        ImGui.Separator();
        Field("Music", edit.Music, 0, 7, v => edit with { Music = v });
        Field("Time", edit.Time, 0, 3, v => edit with { Time = v });
        Field("Item memory", edit.ItemMemory, 0, 3, v => edit with { ItemMemory = v });
        Field("Vertical scroll", edit.ScrollSetting, 0, 3, v => edit with { ScrollSetting = v });
        Field("Layer 3 priority", edit.Layer3Priority, 0, 1, v => edit with { Layer3Priority = v });

        ImGui.Separator();
        ImGui.TextDisabled("bytes " + Convert.ToHexString(edit.ToBytes()));

        if (ImGui.Button("Apply"))
        {
            if (edit != app.level.Header) app.session.ApplyHeader(edit);
            show = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { show = false; ImGui.CloseCurrentPopup(); }
        // Only meaningful once this level carries an edit — otherwise there is nothing to drop.
        bool edited = app.rom?.LevelHeaderOverrides.ContainsKey(app.levelNum) == true;
        ImGui.SameLine();
        ImGui.BeginDisabled(!edited);
        if (ImGui.Button("Revert to ROM"))
        {
            app.session.RevertHeader();
            show = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    private void Field(string label, int value, int min, int max, Func<int, LevelHeader> set)
    {
        int v = value;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
        if (ImGui.SliderInt(label, ref v, min, max)) edit = set(v);
    }
}
