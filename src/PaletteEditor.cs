using System.Numerics;
using ImGuiNET;

namespace PipeDream;

// Palette editor: CGRAM index -> edited BGR555, applied over the ROM palette while
// rendering. In-session only for now (no ROM save path yet); cleared on level change.
// Owns the Palette tab, the palette undo snapshots, and the LM custom-palette save path.
internal sealed class PaletteEditor(EditorApp app)
{
    private readonly EditorApp app = app;

    internal readonly Dictionary<int, ushort> palEdits = new();
    internal int palEditsLevel = -1;
    private int palDirtyRebuild = -1;   // swatch whose picker changed; rebuild when its popup closes
    private Dictionary<int, ushort>? palBeforePicker;   // palEdits snapshot at picker open (undo)

    // `mutate` (e.g. Reset's clear) runs before the after-snapshot; a no-op (picker closed
    // back on the original color) records nothing.
    private void PushPaletteEdit(Dictionary<int, ushort> before, Action? mutate)
    {
        mutate?.Invoke();
        if (before.Count == palEdits.Count &&
            before.All(kv => palEdits.TryGetValue(kv.Key, out var v) && v == kv.Value))
            return;
        var after = new Dictionary<int, ushort>(palEdits);
        app.history.Push(() => RestorePalEdits(before), () => RestorePalEdits(after));
    }

    private void RestorePalEdits(Dictionary<int, ushort> state)
    {
        palEdits.Clear();
        foreach (var (k, c) in state) palEdits[k] = c;
        app.session.RebuildGraphics();
    }

    // Palette tab: the level's 256-color CGRAM as a 16x16 swatch grid. Click a swatch to
    // edit the color (quantized to SNES BGR555); the level re-renders when the picker
    // closes. Edits are session-only until a save path exists (LM custom palette, §7e).
    internal void DrawPaletteTab()
    {
        if (app.rom is null || app.level is null) { ImGui.TextDisabled("No level."); return; }
        var pal = EditedPalette(0)!;
        ImGui.Text($"CGRAM — rows 0-7 BG/FG, 8-F sprites.  {palEdits.Count} edit(s)");
        ImGui.TextDisabled(app.rom.LmCustomPalette(app.levelNum) is not null
            ? "source: LM custom palette"
            : "source: vanilla (header-assembled)");
        if (palEdits.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset"))
            {
                PushPaletteEdit(new Dictionary<int, ushort>(palEdits), () => palEdits.Clear());
                app.session.RebuildGraphics();
            }
        }
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2, 2));
        float sw = MathF.Max(10, MathF.Floor((ImGui.GetContentRegionAvail().X - 15 * 2) / 16));
        for (int i = 0; i < 256; i++)
        {
            if ((i & 15) != 0) ImGui.SameLine();
            uint c = pal.Rgba[i];
            var v = new Vector4((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, 1f);
            if (ImGui.ColorButton($"##pal{i}", v,
                    ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoTooltip, new Vector2(sw, sw)))
            {
                palBeforePicker = new Dictionary<int, ushort>(palEdits);   // undo baseline
                ImGui.OpenPopup($"palpick{i}");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"0x{i:X2}  row {i >> 4} color {i & 15}  BGR555 {pal.Bgr[i]:X4}" +
                                 (palEdits.ContainsKey(i) ? "  (edited)" : ""));
            if (ImGui.BeginPopup($"palpick{i}"))
            {
                var v3 = new Vector3(v.X, v.Y, v.Z);
                if (ImGui.ColorPicker3($"0x{i:X2}##pick{i}", ref v3,
                        ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.NoSmallPreview))
                {
                    ushort bgr = (ushort)(((int)(v3.Z * 31 + .5f) << 10) |
                                          ((int)(v3.Y * 31 + .5f) << 5) |
                                           (int)(v3.X * 31 + .5f));
                    if (pal.Bgr[i] != bgr) { palEdits[i] = bgr; palDirtyRebuild = i; }
                }
                ImGui.EndPopup();
            }
            else if (palDirtyRebuild == i)
            {
                palDirtyRebuild = -1;
                PushPaletteEdit(palBeforePicker ?? new Dictionary<int, ushort>(palEdits), null);
                palBeforePicker = null;
                app.session.RebuildGraphics();      // picker closed: re-render with the edited palette
            }
        }
        ImGui.PopStyleVar();
    }

    // Save the edited palette as an LM custom palette (§7e) into a ROM copy. After a
    // successful save the edits ARE the level's palette, so the edit list resets.
    internal void SavePalette()
    {
        if (app.rom is null || app.level is null) return;
        if (!app.rom.HasLmPaletteHook)
        { app.saveStatus = "ROM lacks LM's palette ASM — open/save it in Lunar Magic once first."; return; }
        try
        {
            var pal = EditedPalette(0)!;
            try { app.rom.WriteLmCustomPalette(app.levelNum, pal.Bgr[0], pal.Bgr); }
            catch (InvalidOperationException) { throw; }
            catch
            {
                app.rom.ExpandTo(Math.Min(0x400000, Math.Max(0x200000, app.rom.ActualRomSize * 2)));
                app.rom.WriteLmCustomPalette(app.levelNum, pal.Bgr[0], pal.Bgr);
            }
            string outp = System.IO.Path.ChangeExtension(app.loadedRomPath, ".edited.smc");
            RatsWriter.SaveAs(app.rom, outp);
            palEdits.Clear();               // the ROM now holds these colors
            app.session.RebuildGraphics();
            app.saveStatus = $"palette saved -> {System.IO.Path.GetFileName(outp)} (level 0x{app.levelNum:X3} custom palette)";
        }
        catch (Exception e) { app.saveStatus = "palette save failed: " + e.Message; }
    }

    // The level palette with the editor tab's session edits applied on top.
    internal Palette? EditedPalette(int phase)
    {
        if (app.rom is null || app.level is null) return null;
        var p = Palette.Load(app.rom, app.level.Header, app.levelNum, phase);
        foreach (var (i, c) in palEdits) { p.Bgr[i] = c; p.Rgba[i] = Palette.ToRgba(c); }
        return p;
    }
}
