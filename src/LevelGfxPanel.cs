using System.Numerics;
using System.Runtime.InteropServices;
using Foster.Framework;
using ImGuiNET;

namespace PipeDream;

/// <summary>
/// Per-level VRAM GFX slot drawer (lives inline in the palette drawer's GFX tab). Owns
/// only its own decoded sheet textures + view state; the editor calls Draw each frame
/// with the current ROM/level.
/// </summary>
internal sealed class LevelGfxPanel(GraphicsDevice gd, ImGuiLayer imgui) : IDisposable
{
    private readonly GraphicsDevice gd = gd;
    private readonly ImGuiLayer imgui = imgui;

    // Per-level VRAM GFX slots. `File` is editable in the GFX tab: committing (Enter)
    // stores a session override in Rom.GfxSlotOverrides keyed by `BypWord`, so the level,
    // sprites and Map16 sheet all re-resolve it. `Status` carries decode info.
    private record struct GfxSlot(string Name, int PalRow, int BypWord, int File, string Status, Texture Tex, int W, int H);
    private readonly List<GfxSlot> levelGfx = new();
    private int levelGfxKey = -1;
    private Palette? levelGfxPal;
    private int refocusBin = -1;    // bin whose input regains focus after an arrow-key step
    private readonly int[] binGen = new int[16];   // per-bin widget generation: bumping it swaps
                                                   // the input's ID so its buffer re-seeds (ImGui.NET
                                                   // exposes no ClearActiveID to refresh in place)

    /// <summary>Level changed — drop the cached Level GFX sheets so they rebuild.</summary>
    public void InvalidateLevel() => levelGfxKey = -1;

    // The GFX files loaded into VRAM for the current level: 4 FG/BG + 4 sprite slots,
    // resolved through the tileset lists and the Super GFX Bypass, as decoded tile
    // sheets. Drawn inline (it lives in the palette drawer's GFX tab). Each bin header
    // is "[NAME]" + an editable GFXnn id; editing re-decodes that bin's sheet (a
    // session preview — nothing is written back to the ROM or the level render yet).
    public unsafe void Draw(Rom? rom, Level? level, int levelNum, Action? onOverride = null)
    {
        if (rom is null || level is null) { ImGui.TextDisabled("No level."); return; }
        if (levelGfxKey != levelNum) { levelGfxKey = levelNum; BuildLevelGfx(rom, level, levelNum); }
        for (int i = 0; i < levelGfx.Count; i++)
        {
            var s = levelGfx[i];
            ImGui.Text($"[{s.Name}] GFX");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(64);
            int v = s.File;
            if (refocusBin == i) { ImGui.SetKeyboardFocusHere(); refocusBin = -1; }
            // Commit on Enter (not per keystroke) — an override triggers a full recompose.
            if (ImGui.InputScalar($"##gfxbin{i}g{binGen[i]}", ImGuiDataType.S32, (IntPtr)(&v), IntPtr.Zero,
                                  IntPtr.Zero, "%03X", ImGuiInputTextFlags.CharsHexadecimal |
                                                       ImGuiInputTextFlags.EnterReturnsTrue)
                && v != s.File && v is >= 0 and <= 0xFFF)
            {
                rom.GfxSlotOverrides[(levelNum, s.BypWord)] = v;
                levelGfxKey = -1;         // re-resolve all bins through the shared bypass
                onOverride?.Invoke();     // editor recomposes level / sprites / Map16 sheet
            }
            // Up/Down while editing: step the id (repeats on hold) and commit immediately;
            // the refocus re-seeds the edit buffer with the stepped value next frame.
            if (ImGui.IsItemActive())
            {
                int step = ImGui.IsKeyPressed(ImGuiKey.UpArrow, true) ? 1
                         : ImGui.IsKeyPressed(ImGuiKey.DownArrow, true) ? -1 : 0;
                int nv = Math.Clamp(s.File + step, 0, 0xFFF);
                if (step != 0 && nv != s.File)
                {
                    rom.GfxSlotOverrides[(levelNum, s.BypWord)] = nv;
                    levelGfxKey = -1;
                    binGen[i]++;              // new widget ID → buffer re-seeds with nv
                    refocusBin = i;
                    onOverride?.Invoke();
                }
            }
            if (s.Status.Length > 0) { ImGui.SameLine(); ImGui.TextDisabled(s.Status); }
            ImGui.Image(imgui.GetTextureID(s.Tex), new Vector2(s.W * 2f, s.H * 2f));
            ImGui.Separator();
        }
    }

    private void BuildLevelGfx(Rom rom, Level level, int levelNum)
    {
        foreach (var e in levelGfx) e.Tex.Dispose();
        levelGfx.Clear();
        var h = level.Header;
        var pal = Palette.Load(rom, h, levelNum);
        var byp = rom.LmGfxBypass(levelNum);

        // (name, GFXLIST base, list index, palette row for the preview, bypass record word)
        var slots = new (string name, int listBase, int idx, int palRow, int bypWord)[]
        {
            ("FG1", Gfx.ObjectGfxList, h.Tileset * 4 + 0, 2, 7),
            ("FG2", Gfx.ObjectGfxList, h.Tileset * 4 + 1, 2, 6),
            ("BG1", Gfx.ObjectGfxList, h.Tileset * 4 + 2, 0, 5),
            ("FG3", Gfx.ObjectGfxList, h.Tileset * 4 + 3, 2, 4),
            // BG2/BG3 have no vanilla list entry (listBase -1) — only via the bypass (LM VRAM patch).
            ("BG2", -1, 0, 0, 3),
            ("BG3", -1, 0, 0, 2),
            ("SP1", 0x00A8C3, h.SpriteSet * 4 + 0, 8, 11),
            ("SP2", 0x00A8C3, h.SpriteSet * 4 + 1, 8, 10),
            ("SP3", 0x00A8C3, h.SpriteSet * 4 + 2, 8, 9),
            ("SP4", 0x00A8C3, h.SpriteSet * 4 + 3, 8, 8),
        };
        levelGfxPal = pal;
        foreach (var s in slots)
        {
            // LM writes every slot of the record when bypass is on, including ones that
            // just restate the tileset default — only tag when the file actually differs.
            int def = s.listBase < 0 ? 0x7F : rom.Data[rom.FileOffset(s.listBase) + s.idx];
            bool bypassed = byp is not null && (byp[s.bypWord] & 0xFFF) != 0x7F;
            int file = bypassed ? byp![s.bypWord] & 0xFFF : def;
            bool overridden = rom.GfxSlotOverrides.ContainsKey((levelNum, s.bypWord));
            levelGfx.Add(DecodeSlot(rom, s.name, s.palRow, s.bypWord, file,
                                    overridden ? "(override)" : bypassed && file != def ? "(bypass)" : ""));
        }
    }

    /// <summary>Decode one GFX file into a bin's tile-sheet texture (blank on empty/failure).</summary>
    private GfxSlot DecodeSlot(Rom rom, string name, int palRow, int bypWord, int file, string note)
    {
        if (file == 0x7F || Gfx.Cached(rom, file) is not { } data)
            return new GfxSlot(name, palRow, bypWord, file, "(empty)", MakeBlank(), 8, 8);
        try
        {
            int bpp = Gfx.RomBpp(rom);                  // ROM-wide depth (vanilla 3 / LM 4)
            var (px, w, ht) = Gfx.TileSheet(data, bpp, levelGfxPal!, palRow);
            return new GfxSlot(name, palRow, bypWord, file, $"{note} {bpp}bpp".Trim(),
                               new Texture(gd, w, ht, MemoryMarshal.AsBytes(px.AsSpan())), w, ht);
        }
        catch { return new GfxSlot(name, palRow, bypWord, file, "(decode failed)", MakeBlank(), 8, 8); }
    }

    private Texture MakeBlank() => new(gd, 8, 8, new byte[8 * 8 * 4]);

    public void Dispose()
    {
        foreach (var e in levelGfx) e.Tex.Dispose();
        levelGfx.Clear();
    }
}
