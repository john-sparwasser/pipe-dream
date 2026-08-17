using ImGuiNET;

namespace PipeDream;

/// <summary>
/// The level's screen exits — which screen leads where (pipes, doors, sublevels).
/// Exits are objects in the Layer-1 stream but draw no tiles, so they are invisible on
/// the canvas and only reachable through this list.
///
/// Rows are staged and committed on Apply as ONE object edit, so a session of retyping
/// destinations costs one undo step rather than one per keystroke.
/// </summary>
internal sealed class LevelExitsDialog(EditorApp app)
{
    private readonly EditorApp app = app;

    /// <summary>A staged exit. <see cref="Source"/> is the object it came from, kept so an
    /// untouched exit keeps its original stream position instead of being rewritten to sit
    /// on the screen it governs.</summary>
    private sealed class Row
    {
        internal LevelObject Source;
        internal bool IsNew;
        internal bool Lm;          // LM secondary-exit form (ext obj 0x02, 2-byte exit word)
        internal int Screen;       // the screen this exit governs (the object's Y field)
        internal int Dest;
        internal bool Water;
        internal bool Secondary;
    }

    private bool show;
    private readonly List<Row> rows = new();

    internal void Open()
    {
        if (app.objList is null) return;
        rows.Clear();
        foreach (var o in app.objList.Where(o => o.IsScreenExit || o.IsLmSecondaryExit))
            rows.Add(new Row
            {
                Source = o,
                Lm = o.IsLmSecondaryExit,
                Screen = o.ExitScreen,
                Dest = o.ExitDestination,
                Water = o.ExitIsWater,
                Secondary = o.ExitUsesSecondary,
            });
        show = true;
    }

    internal void Draw()
    {
        if (!show) return;
        if (app.objList is null) { show = false; return; }
        ImGui.OpenPopup("Screen Exits");
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing,
                               new System.Numerics.Vector2(0.5f, 0.5f));
        bool open;
        unsafe
        {
            var name = "Screen Exits\0"u8;
            fixed (byte* n = name)
                open = ImGuiNative.igBeginPopupModal(n, null, ImGuiWindowFlags.AlwaysAutoResize) != 0;
        }
        if (!open) return;

        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 34);
        ImGui.TextDisabled("Each row says where one screen of this level leads. " +
                           "A plain exit's destination is a level number; a secondary exit's " +
                           "is an index into the secondary entrance table.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (rows.Count == 0) ImGui.TextDisabled("This level has no screen exits.");
        else if (ImGui.BeginTable("exits", 6, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Screen");
            ImGui.TableSetupColumn("Destination");
            ImGui.TableSetupColumn("Water");
            ImGui.TableSetupColumn("Secondary");
            ImGui.TableSetupColumn("Kind");
            ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();

            int remove = -1;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                ImGui.PushID(i);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                HexField("##screen", ref r.Screen, 0x1F, "%02X");

                ImGui.TableNextColumn();
                HexField("##dest", ref r.Dest, r.Lm ? 0xFFFF : 0xFF, r.Lm ? "%04X" : "%02X");

                // The LM form packs its own flags into the exit word's high byte, so the
                // vanilla flag bits do not apply to it.
                ImGui.TableNextColumn();
                if (r.Lm) ImGui.TextDisabled("-");
                else { bool w = r.Water; if (ImGui.Checkbox("##water", ref w)) r.Water = w; }

                ImGui.TableNextColumn();
                if (r.Lm) ImGui.TextDisabled("-");
                else { bool s = r.Secondary; if (ImGui.Checkbox("##sec", ref s)) r.Secondary = s; }

                ImGui.TableNextColumn();
                ImGui.TextDisabled(r.Lm ? "LM word" : "vanilla");

                ImGui.TableNextColumn();
                if (ImGui.Button("Remove")) remove = i;
                ImGui.PopID();
            }
            ImGui.EndTable();
            if (remove >= 0) rows.RemoveAt(remove);
        }

        ImGui.Spacing();
        if (ImGui.Button("Add exit"))
            rows.Add(new Row { IsNew = true, Screen = rows.Count == 0 ? 0 : rows[^1].Screen + 1, Dest = 0 });
        ImGui.SameLine();
        if (ImGui.Button("Apply")) { Commit(); show = false; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { show = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    /// <summary>Hex entry with step buttons, matching the level field in the header row.</summary>
    private static void HexField(string id, ref int value, int max, string fmt)
    {
        int v = value, step = 1;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8);
        bool changed;
        unsafe
        {
            changed = ImGui.InputScalar(id, ImGuiDataType.S32, (IntPtr)(&v), (IntPtr)(&step),
                                        IntPtr.Zero, fmt, ImGuiInputTextFlags.CharsHexadecimal);
        }
        if (changed) value = Math.Clamp(v, 0, max);
    }

    /// <summary>Rewrite the level's exit objects from the staged rows, as one undoable edit.
    /// Non-exit objects keep their order; exits are replaced in place where they already
    /// existed and appended when new.</summary>
    private void Commit()
    {
        if (app.objList is null) return;
        var before = new List<LevelObject>(app.objList);
        app.objList.RemoveAll(o => o.IsScreenExit || o.IsLmSecondaryExit);
        foreach (var r in rows)
        {
            // An existing exit keeps its stream screen; a new one sits on the screen it
            // governs (what the vanilla data does in the common case).
            int streamScreen = r.IsNew ? r.Screen : r.Source.Screen;
            app.objList.Add(r.Lm
                ? new LevelObject(r.Source.NewScreen, 0, streamScreen, r.Source.XNibble,
                                  r.Screen & 0x1F, 0x02, r.Dest & 0xFFFF)
                : new LevelObject(r.IsNew ? false : r.Source.NewScreen, 0, streamScreen,
                                  (r.Secondary ? 2 : 0) | (r.Water ? 1 : 0),
                                  r.Screen & 0x1F, 0x00, r.Dest & 0xFF));
        }
        if (app.objList.SequenceEqual(before)) return;   // nothing actually changed
        app.objectEditor.PushObjectEdit(before);
    }
}
