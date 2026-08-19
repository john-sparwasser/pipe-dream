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
    // "Import…" brings a raw planar .bin in as a project ExGFX file and assigns it to
    // that bin through the same override path as typing an id.
    public unsafe void Draw(Rom? rom, Level? level, int levelNum, IntPtr sdlWindow,
                            Action<string>? setStatus = null, Action? onOverride = null,
                            Action<int>? onEdit = null, Action<int>? onBrowse = null)
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
            // Import a raw .bin into THIS bin (FileDialog is async; the callback lands on a
            // later frame via Pump, so it captures everything it needs).
            ImGui.SameLine();
            if (ImGui.SmallButton($"Import…##imp{i}") && !FileDialog.Busy)
            {
                int bypWord = s.BypWord;
                FileDialog.OpenFile("Raw GFX", "bin", sdlWindow, path =>
                {
                    if (path is not null)
                        setStatus?.Invoke(Import(rom, levelNum, bypWord, path, onOverride));
                });
            }
            // Pick a file by sight/name instead of typing a hex id into the field above.
            ImGui.SameLine();
            if (ImGui.SmallButton($"Browse…##brw{i}")) onBrowse?.Invoke(s.BypWord);
            // Open this bin's file in the GFX tile editor (canvas mode 3).
            ImGui.SameLine();
            if (ImGui.SmallButton($"Edit##ged{i}")) onEdit?.Invoke(s.File);
            if (s.Status.Length > 0) { ImGui.SameLine(); ImGui.TextDisabled(s.Status); }
            // A custom file's name is the point of naming them — show it next to the id.
            if (rom.GfxName(s.File) is { Length: > 0 } gname)
            { ImGui.SameLine(); ImGui.TextDisabled($"\"{gname}\""); }
            ImGui.Image(imgui.GetTextureID(s.Tex), new Vector2(s.W * 2f, s.H * 2f));
            ImGui.Separator();
        }
    }

    /// <summary>
    /// Import a raw planar .bin as a project ExGFX file: detect its bpp from the size,
    /// normalize to the ROM's depth, store under the next free id ≥ 0x100, and point this
    /// bin at it through the same override commit path as typing an id (so dirty-marking
    /// and the recompose ride onOverride). Returns the saveStatus line.
    /// </summary>
    private string Import(Rom rom, int levelNum, int bypWord, string path, Action? onOverride)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) { return $"import failed: {e.Message}"; }
        int bpp = Gfx.DetectBpp(bytes);
        if (bpp == 0)
            return $"import rejected: {Path.GetFileName(path)} is 0x{bytes.Length:X} bytes — not whole 3bpp (×24) or 4bpp (×32) planar tiles";
        int romBpp = Gfx.RomBpp(rom);
        bytes = Gfx.NormalizeBpp(bytes, bpp, romBpp, out bool plane3Dropped);

        // Next free ExGFX id: skip prior imports AND files the ROM itself resolves, so an
        // import can't shadow a real ExGFX file other levels may use. Invariant: the id is
        // always FRESH — if this ever overwrites an existing ImportedGfx id, clear the edit
        // history first (GFX stroke undo closures re-look-up the array by id, but Map16-style
        // offset closures into a replaced array would be meaningless).
        int id = 0x100;
        while (id <= 0xFFF && (rom.ImportedGfx.ContainsKey(id) || Gfx.SourceSnes(rom, id) >= 0)) id++;
        if (id > 0xFFF) return "import failed: no free ExGFX id (0x100-0xFFF all in use)";
        rom.ImportedGfx[id] = bytes;
        // The filename is the only human-meaningful label the import has; keep it as the
        // file's name instead of leaving the user with a bare hex id.
        rom.ImportedGfxNames[id] = Path.GetFileNameWithoutExtension(path);
        Gfx.InvalidateCache(rom);

        rom.GfxSlotOverrides[(levelNum, bypWord)] = id;
        levelGfxKey = -1;         // re-resolve all bins through the shared bypass
        onOverride?.Invoke();     // editor recomposes level / sprites / Map16 sheet
        return $"imported {Path.GetFileName(path)} as GFX{id:X3} ({bpp}bpp → {romBpp}bpp)"
             + (plane3Dropped ? " — nonzero plane 3 data discarded" : "");
    }

    private void BuildLevelGfx(Rom rom, Level level, int levelNum)
    {
        foreach (var e in levelGfx) e.Tex.Dispose();
        levelGfx.Clear();
        levelGfxPal = Palette.Load(rom, level.Header, levelNum);
        foreach (var s in Gfx.LevelSlots(rom, level.Header, levelNum))
        {
            // LM writes every slot of the record when bypass is on, including ones that
            // just restate the tileset default — only tag when the file actually differs.
            bool overridden = rom.GfxSlotOverrides.ContainsKey((levelNum, s.BypWord));
            levelGfx.Add(DecodeSlot(rom, s.Name, s.PalRow, s.BypWord, s.File,
                                    rom.ImportedGfx.ContainsKey(s.File) ? "(imported)"
                                    : overridden ? "(override)" : s.File != s.Def ? "(bypass)" : ""));
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
