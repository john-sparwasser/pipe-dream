using ImGuiNET;

namespace PipeDream;

/// <summary>
/// Everything that is a property OF a level rather than of its contents: the 5 header
/// bytes (CONTRACT §4) and the main entrance / entry settings, which live in their own
/// bank-05 tables rather than the header (see <see cref="MainEntrance"/>).
///
/// Edits are staged locally and only committed on Apply — every header field forces a full
/// reparse (tileset drives object dispatch, the palette fields drive every tile cache),
/// which is far too expensive to run per slider tick.
/// </summary>
internal sealed class LevelPropertiesDialog(EditorApp app)
{
    private readonly EditorApp app = app;

    private bool show;
    private LevelHeader edit;
    private MainEntrance entry;

    internal void Open()
    {
        if (app.level is null || app.rom is null) return;
        edit = app.level.Header;
        entry = app.rom.ReadMainEntrance(app.levelNum);
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

        // Header + entry settings together are taller than a small window, so the fields
        // scroll and the buttons stay pinned below.
        ImGui.BeginChild("fields", new System.Numerics.Vector2(
            ImGui.GetFontSize() * 26,
            Math.Min(ImGui.GetMainViewport().WorkSize.Y * 0.62f, ImGui.GetFontSize() * 42)));
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
        ImGui.TextDisabled("header bytes " + Convert.ToHexString(edit.ToBytes()));

        // Entry settings live in their own bank-05 tables, not the header. The spawn
        // position only applies when the level is entered from the overworld — a secondary
        // exit places Mario itself — but the vertical and entrance-walk bits always apply.
        ImGui.Spacing();
        ImGui.SeparatorText("Main entrance");
        EntryField("Mario X", entry.MarioX, 0, 7, v => entry with { MarioX = v });
        EntryField("Mario Y", entry.MarioY, 0, 15, v => entry with { MarioY = v });
        EntryField("Entrance action", entry.EntranceAction, 0, 7, v => entry with { EntranceAction = v });
        EntryField("Screen boundary Y", entry.ScreenBoundaryY, 0, 3, v => entry with { ScreenBoundaryY = v });
        EntryField("Entry vert. scroll", entry.VerticalScroll, 0, 3, v => entry with { VerticalScroll = v });
        EntryField("Layer 2 scroll", entry.Layer2Scroll, 0, 15, v => entry with { Layer2Scroll = v });
        EntryField("Layer 2 BG setting", entry.Layer2Setting, 0, 3, v => entry with { Layer2Setting = v });
        EntryField("Vertical level", entry.VerticalLevel, 0, 3, v => entry with { VerticalLevel = v });
        EntryField("Skip entrance walk", entry.SkipEntranceWalk, 0, 1, v => entry with { SkipEntranceWalk = v });
        ImGui.TextDisabled("entrance bytes " + Convert.ToHexString(entry.ToBytes()));
        ImGui.EndChild();

        ImGui.Separator();
        if (ImGui.Button("Apply"))
        {
            CommitEntry();
            if (edit != app.level.Header) app.session.ApplyHeader(edit);
            show = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { show = false; ImGui.CloseCurrentPopup(); }
        // Header only — the entry settings are written into the ROM in place, so undo is
        // what takes those back. Only meaningful once this level carries a header edit.
        bool edited = app.rom?.LevelHeaderOverrides.ContainsKey(app.levelNum) == true;
        ImGui.SameLine();
        ImGui.BeginDisabled(!edited);
        if (ImGui.Button("Revert header"))
        {
            app.session.RevertHeader();
            show = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    /// <summary>Write the staged entry settings into the session ROM, undoably. The record
    /// is per level but lives outside the level's data, so like Map16 it is written straight
    /// to the ROM and re-read from it at save time.</summary>
    private void CommitEntry()
    {
        if (app.rom is null || app.rom.ReadMainEntrance(app.levelNum) == entry) return;
        var before = app.rom.ReadMainEntrance(app.levelNum);
        int level = app.levelNum;
        void Apply(MainEntrance e)
        {
            app.rom!.WriteMainEntrance(level, e);
            if (app.project is not null)
            {
                app.project.Data.Level(level).MainEntrance = Convert.ToHexString(e.ToBytes());
                app.project.MarkDirty();
            }
        }
        Apply(entry);
        var after = entry;
        app.history.Push(() => Apply(before), () => Apply(after));
    }

    private void Field(string label, int value, int min, int max, Func<int, LevelHeader> set)
    {
        int v = value;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
        if (ImGui.SliderInt(label, ref v, min, max)) edit = set(v);
    }

    private void EntryField(string label, int value, int min, int max, Func<int, MainEntrance> set)
    {
        int v = value;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
        if (ImGui.SliderInt(label, ref v, min, max)) entry = set(v);
    }
}
