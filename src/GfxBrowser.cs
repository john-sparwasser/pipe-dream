using System.Numerics;
using System.Runtime.InteropServices;
using Foster.Framework;
using ImGuiNET;

namespace PipeDream;

/// <summary>
/// Browse and pick a GFX file by sight and by name instead of by hex id. Lists the project's
/// imported ExGFX (id, editable name, tile count, a decoded thumbnail) with a name/id filter,
/// and optionally the ROM's stock files too. Picking calls back with the id, so the same
/// modal serves "assign this to a VRAM slot" and "open this in the tile editor".
///
/// Owns a thumbnail texture per listed file, built lazily and only ever added to during a
/// frame — disposal happens at the top of Draw when something asked for a rebuild, never
/// after an Image() for that texture has already been submitted.
/// </summary>
internal sealed class GfxBrowser(EditorApp app, GraphicsDevice gd, ImGuiLayer imgui) : IDisposable
{
    private readonly EditorApp app = app;
    private readonly GraphicsDevice gd = gd;
    private readonly ImGuiLayer imgui = imgui;

    private bool show;
    private bool showStock;
    private string filter = "";
    private Action<int>? onPick;
    private string title = "Select GFX";

    private readonly Dictionary<int, (Texture Tex, int W, int H)> thumbs = new();
    private bool rebuild;
    private int renaming = -1;
    private string renameBuf = "";
    private bool focusRename;    // IsWindowAppearing refers to the child, not "rename just started"

    /// <summary>Open the picker. <paramref name="onPicked"/> gets the chosen file id.</summary>
    internal void Open(string purpose, Action<int> onPicked)
    {
        title = purpose;
        onPick = onPicked;
        show = true;
        rebuild = true;      // consumed at the top of Draw, before any Image() this frame
        renaming = -1;
    }

    /// <summary>Thumbnails are stale (a file's pixels or the palette changed).</summary>
    internal void Invalidate() => rebuild = true;

    /// <summary>Files to list, in id order: imported ExGFX first, then stock when asked.
    /// Static and ROM-only so the filter rule is testable without a graphics device.</summary>
    internal static List<int> Candidates(Rom rom, bool includeStock, string filter)
    {
        var ids = new List<int>(rom.ImportedGfx.Keys);
        if (includeStock)
            for (int f = 0; f < 0x34; f++)
                if (!rom.ImportedGfx.ContainsKey(f)) ids.Add(f);
        ids.Sort();
        if (filter.Length == 0) return ids;
        return ids.Where(id => Matches(rom, id, filter)).ToList();
    }

    /// <summary>A file matches when the filter appears anywhere in its NAME, or PREFIXES its
    /// hex id — so "grass" finds it by name and "10" finds $100-$10F. Ids deliberately are not
    /// substring-matched: a one-letter filter like "a" would otherwise drag in $00A, $01A,
    /// $02A… by coincidence. Both spellings of the id are tried so "a" still finds $00A.</summary>
    internal static bool Matches(Rom rom, int id, string filter) =>
        (rom.GfxName(id) is { Length: > 0 } n &&
         n.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
        id.ToString("X").StartsWith(filter, StringComparison.OrdinalIgnoreCase) ||
        id.ToString("X3").StartsWith(filter, StringComparison.OrdinalIgnoreCase);

    internal void Draw()
    {
        if (!show) return;
        if (app.rom is not { } rom) { show = false; return; }
        if (rebuild) { DropThumbs(); rebuild = false; }
        if (!ImGuiCompat.BeginCenteredModal(title)) return;

        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 40);
        ImGui.TextDisabled("Imported files keep the name of the .bin they came from; click a " +
                           "name to rename. Filter matches names and hex ids.");
        ImGui.PopTextWrapPos();

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 16);
        ImGui.InputText("Filter", ref filter, 64);
        ImGui.SameLine();
        ImGui.Checkbox("include stock GFX", ref showStock);

        var ids = Candidates(rom, showStock, filter);
        ImGui.Separator();
        if (ids.Count == 0)
            ImGui.TextDisabled(rom.ImportedGfx.Count == 0
                ? "No custom GFX imported yet — use Import… in the GFX tab."
                : "Nothing matches that filter.");

        int picked = -1;
        ImGui.BeginChild("gfxlist", new Vector2(ImGui.GetFontSize() * 40,
                                               Math.Min(ImGui.GetMainViewport().WorkSize.Y * 0.55f, 520)));
        foreach (int id in ids)
        {
            ImGui.PushID(id);
            bool imported = rom.ImportedGfx.ContainsKey(id);
            var t = Thumb(rom, id);

            // The thumbnail is the pick target: clicking the picture is the obvious gesture.
            // A full sheet is 128x64 (16 cols of 8px), so scale to fit 128 wide and keep the
            // aspect — a short partial file stays short instead of being stretched.
            float scale = t.W > 0 ? Math.Min(2f, 128f / t.W) : 1f;
            if (ImGui.ImageButton($"##pick{id}", imgui.GetTextureID(t.Tex),
                                  new Vector2(t.W * scale, t.H * scale)))
                picked = id;
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.Text($"GFX{id:X3}");
            ImGui.SameLine();
            ImGui.TextDisabled(imported ? "custom" : "stock");

            if (imported && renaming == id)
            {
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 14);
                if (focusRename) { ImGui.SetKeyboardFocusHere(); focusRename = false; }
                if (ImGui.InputText("##rename", ref renameBuf, 48,
                                    ImGuiInputTextFlags.EnterReturnsTrue) || ImGui.IsItemDeactivated())
                {
                    rom.ImportedGfxNames[id] = renameBuf.Trim();
                    app.project?.MarkDirty();
                    renaming = -1;
                }
            }
            else
            {
                string name = rom.GfxName(id);
                if (imported)
                {
                    if (ImGui.SmallButton(name.Length > 0 ? name : "(unnamed)"))
                    { renaming = id; renameBuf = name; focusRename = true; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("rename");
                }
                // Stock files have no name to show beyond their id — vanilla ships no label
                // table, and inventing one would be guesswork.
            }
            ImGui.TextDisabled(Describe(rom, id));
            ImGui.EndGroup();
            ImGui.Separator();
            ImGui.PopID();
        }
        ImGui.EndChild();

        if (ImGui.Button("Cancel")) { show = false; ImGui.CloseCurrentPopup(); }
        if (picked >= 0)
        {
            var cb = onPick;
            show = false;
            onPick = null;            // don't retain the closure past its one use
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            cb?.Invoke(picked);       // after EndPopup: the callback recomposes graphics
            return;
        }
        ImGui.EndPopup();
    }

    private static string Describe(Rom rom, int id)
    {
        int bpp = Gfx.RomBpp(rom);
        if (Gfx.Cached(rom, id) is not { } d) return "(empty)";
        return $"{d.Length / Gfx.TileBytes(bpp)} tiles, {bpp}bpp, 0x{d.Length:X} bytes";
    }

    /// <summary>Cached thumbnail, decoded on first sight. With stock files included this is
    /// ~50 sheet decodes and texture uploads the first frame the list is shown — a one-off
    /// hitch on open, not per frame, and Gfx.Cached already memoizes the decompression.</summary>
    private (Texture Tex, int W, int H) Thumb(Rom rom, int id)
    {
        if (thumbs.TryGetValue(id, out var got)) return got;
        var made = Decode(rom, id);
        thumbs[id] = made;
        return made;
    }

    private (Texture, int, int) Decode(Rom rom, int id)
    {
        // Palette row 2 (the FG row) is the least misleading single choice for a preview;
        // the real row depends on which slot the file ends up in.
        try
        {
            if (Gfx.Cached(rom, id) is { } data && app.level is { } lv)
            {
                var pal = Palette.Load(rom, lv.Header, app.levelNum);
                var (px, w, h) = Gfx.TileSheet(data, Gfx.RomBpp(rom), pal, 2);
                return (new Texture(gd, w, h, MemoryMarshal.AsBytes(px.AsSpan())), w, h);
            }
        }
        catch { /* fall through to a blank chip */ }
        return (new Texture(gd, 8, 8, new byte[8 * 8 * 4]), 8, 8);
    }

    private void DropThumbs()
    {
        foreach (var (_, v) in thumbs) v.Tex.Dispose();
        thumbs.Clear();
    }

    public void Dispose() => DropThumbs();
}
