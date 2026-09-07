namespace PipeDream;

/// <summary>
/// The two directions between a project snapshot and a live ROM, with no editor in sight.
///
/// A .pdp is a SNAPSHOT, not a diff log: opening a project replays it onto a fresh base
/// (<see cref="Hydrate"/>), and saving captures live state back into it
/// (<see cref="LevelEditState.Stash"/>). Both directions used to live inside the ImGui
/// editor's LevelSession, written against its EditorApp — which meant the save path could
/// only run if a window existed, could only be tested through the GUI, and had to be
/// reimplemented from scratch by any second front end. Getting that subtly wrong is not a
/// visible bug: it renders perfectly and silently drops edits on save.
/// </summary>
public static class ProjectSession
{
    /// <summary>
    /// Replay a project onto a freshly loaded ROM: imported GFX and their names, per-level
    /// GFX slot overrides and header overrides (all of which are session state carried on the
    /// Rom instance), then the Map16/acts snapshot and the entrance tables.
    ///
    /// Order matters. GFX imports come first so the very first level parse already resolves
    /// them through Gfx.Cached, and Map16 replay uses the same code the build does, so the
    /// session ROM and a built ROM cannot drift.
    ///
    /// Returns a user-facing warning, or null.
    /// </summary>
    public static string? Hydrate(Rom rom, ProjectFile data)
    {
        foreach (var (id, b64) in data.Gfx)
            rom.ImportedGfx[Convert.ToInt32(id, 16)] = Convert.FromBase64String(b64);
        foreach (var (id, name) in data.GfxNames)
            rom.ImportedGfxNames[Convert.ToInt32(id, 16)] = name;
        RomBuilder.ReplayExAnimation(rom, data, null);   // records + files 60-63 into the session ROM, as the build does

        foreach (var (key, state) in data.Levels)
        {
            int lvl = Convert.ToInt32(key, 16);
            foreach (var (word, file) in state.GfxOverrides)
                rom.GfxSlotOverrides[(lvl, word)] = file;
            if (state.Header is { } hx) rom.LevelHeaderOverrides[lvl] = Convert.FromHexString(hx);
            if (state.Layer3Tilemap is { } l3) rom.Layer3Tilemaps[lvl] = Convert.FromBase64String(l3);
            if (state.BgTilemap is { } bg)
                rom.BgTilemaps[lvl] = BgImage.Join(Convert.FromBase64String(bg),
                    state.BgTilemapPages is { } pg ? Convert.FromBase64String(pg) : BgImage.PagePlane(rom, lvl));
            if (state.Layer3Advanced is { } adv) rom.Layer3AdvancedOverrides[lvl] = adv;
            else if (state.Layer3AdvancedOff) rom.Layer3AdvancedOverrides[lvl] = null;
        }

        if (data.Overworld.Layer2 is { } ow) rom.OwLayer2 = WordsOf(Convert.FromBase64String(ow));
        if (data.Overworld.Layer1 is { } ow1) rom.OwLayer1 = WordsOf(Convert.FromBase64String(ow1));

        string? warn = RomBuilder.ReplayMap16(rom, data);
        RomBuilder.ReplayEntrances(rom, data);
        return warn;
    }

    /// <summary>Little-endian words from bytes, the shape a tilemap is saved in.</summary>
    public static ushort[] WordsOf(byte[] bytes)
    {
        var words = new ushort[bytes.Length / 2];
        for (int i = 0; i < words.Length; i++) words[i] = (ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
        return words;
    }

    public static byte[] BytesOf(ushort[] words)
    {
        var bytes = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++) { bytes[i * 2] = (byte)words[i]; bytes[i * 2 + 1] = (byte)(words[i] >> 8); }
        return bytes;
    }
}

/// <summary>
/// One level's live edit state, and how it becomes project data.
///
/// This is the part of an editing session that a .pdp actually remembers. Everything else a
/// UI holds — selections, zoom, undo stacks, composed pixels — is view state and is not
/// saved. Keeping the two apart is what lets the save path be exercised without a window.
/// </summary>
public sealed class LevelEditState
{
    /// <summary>Layer-1 object stream. THE level: the Map16 grid is a projection of this.</summary>
    public List<LevelObject> Layer1 { get; set; } = [];

    /// <summary>Layer-2 object stream, or null when layer 2 is a background image and the
    /// project has not converted it.</summary>
    public List<LevelObject>? Layer2 { get; set; }

    /// <summary>The BASE ROM's layer-2 stream, kept to diff against on save. Null means the
    /// base had no stream at all, which is how a conversion is recognised.</summary>
    public List<LevelObject>? BaseLayer2 { get; set; }

    public SpriteData? Sprites { get; set; }

    /// <summary>Custom palette entries, CGRAM index → BGR555.</summary>
    public Dictionary<int, ushort> PaletteEdits { get; } = [];

    /// <summary>
    /// Write this level's state into the project snapshot. Header and GFX overrides are read
    /// back off the Rom because that is where they live as session state.
    ///
    /// Layer 2 is only recorded once it DIFFERS from the base ROM's stream, or every touched
    /// level would pin its unedited layer 2 into the project. An empty list still counts when
    /// the base had no stream: that is the background-image → object-mode conversion, and
    /// dropping it would silently undo the conversion.
    /// </summary>
    public void Stash(ProjectFile data, Rom rom, int levelNum)
    {
        var s = data.Level(levelNum);
        s.Objects = Layer1.Select(ProjectFile.ObjectDto.From).ToList();

        bool converted = BaseLayer2 is null && Layer2 is not null;
        bool edited = BaseLayer2 is not null && Layer2 is { } l2 && !l2.SequenceEqual(BaseLayer2);
        s.Layer2Objects = converted || edited
            ? Layer2!.Select(ProjectFile.ObjectDto.From).ToList() : null;

        if (Sprites is not null)
        {
            s.SpriteMemory = Sprites.SpriteMemory;
            s.Buoyancy = Sprites.Buoyancy;
            s.Sprites = Sprites.Sprites.Select(ProjectFile.SpriteDto.From).ToList();
        }

        s.Palette = PaletteEdits.ToDictionary(kv => kv.Key, kv => (int)kv.Value);
        s.GfxOverrides = rom.GfxSlotOverrides.Where(kv => kv.Key.Level == levelNum)
                            .ToDictionary(kv => kv.Key.Word, kv => kv.Value);
        s.Header = rom.LevelHeaderOverrides.TryGetValue(levelNum, out var hb)
            ? Convert.ToHexString(hb) : null;
        s.Layer3Tilemap = rom.Layer3Tilemaps.TryGetValue(levelNum, out var l3b)
            ? Convert.ToBase64String(l3b) : null;
        if (rom.BgTilemaps.TryGetValue(levelNum, out var bgb))
        {
            var (low, page) = BgImage.Split(bgb);
            s.BgTilemap = Convert.ToBase64String(low);
            s.BgTilemapPages = Convert.ToBase64String(page);
        }
        else s.BgTilemap = s.BgTilemapPages = null;
        bool advEdit = rom.Layer3AdvancedOverrides.TryGetValue(levelNum, out var advo);
        s.Layer3Advanced = advEdit ? advo : null;
        s.Layer3AdvancedOff = advEdit && advo is null;
    }

    /// <summary>
    /// Capture the ROM-wide state a save needs: imported GFX blobs and their names, plus the
    /// Map16/acts/entrance re-read. The per-level half is <see cref="Stash"/>.
    ///
    /// Names are kept only for ids that still exist, so removing an import cannot leave a
    /// stray name behind.
    /// </summary>
    public static void StashRomWide(ProjectFile data, Rom rom, int tileset)
    {
        ProjectCapture.Refresh(rom, data, tileset);
        data.Gfx = rom.ImportedGfx.ToDictionary(kv => kv.Key.ToString("X3"),
                                                kv => Convert.ToBase64String(kv.Value));
        data.GfxNames = rom.ImportedGfxNames
            .Where(kv => kv.Value.Length > 0 && rom.ImportedGfx.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key.ToString("X3"), kv => kv.Value);
    }
}
