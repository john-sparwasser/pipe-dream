namespace PipeDream.Services;

/// <summary>
/// Everything the editor knows that is not a control: the config, the open project, the live
/// ROM, and which level is being edited. The window observes it; it never observes the window.
///
/// This is the SERVICE the UI talks to. The ROM, the project file and the config are storage
/// handles and stay internal — the layer above asks this class questions ("is this level
/// vertical?", "give me the tile sheet") instead of reaching through it to the bytes. That is
/// the whole point of the split: the open → edit → save → build cycle runs headlessly, and
/// nothing in the UI can quietly grow a dependency on the file format.
/// </summary>
public sealed class EditorSession
{
    internal Config Config { get; } = Config.Load();
    internal Project? Project { get; private set; }
    internal Rom? Rom { get; private set; }

    public string? RomPath { get; private set; }
    public int LevelNum { get; private set; } = 0x105;
    public LevelScene? Scene { get; private set; }
    public LevelEdit? Edit { get; private set; }

    /// <summary>Map16 definition editing for the current level's tileset. Rebuilt with the
    /// level, because a new tileset means new definition offsets.</summary>
    public Map16Edit? Map16 { get; private set; }

    /// <summary>Committed Map16 bytes changed: tile caches, the level and the sheets are all
    /// stale. Raised for whichever Map16Edit is current, so the UI subscribes once.</summary>
    public event EventHandler? Map16Committed;

    /// <summary>Levels whose edits have not been stashed into the project yet.</summary>
    private readonly HashSet<int> touched = [];

    public event EventHandler? Changed;
    public string Status { get; private set; } = "";

    private void Report(string s) { Status = s; Changed?.Invoke(this, EventArgs.Empty); }

    public bool HasUnsavedWork => Project is { Dirty: true } || touched.Count > 0 || LevelDirty;

    /// <summary>Whether the CURRENT level holds work the project snapshot does not have yet.
    /// Palette edits count as dirty even when they were hydrated from the project — stashing
    /// them again writes back the same values, which is cheaper than a rule that can lose them.</summary>
    private bool LevelDirty => Edit is { Dirty: true } || Sprites is { Dirty: true }
                               || paletteEdits.Count > 0;

    /// <summary>Sprite editing for the current level.
    ///
    /// Owned HERE rather than created per repaint, for two reasons that both bit: an object
    /// re-render replaces the LevelEdit, so a SpriteEdit hanging off that would lose its undo
    /// history and selection on every edit; and a level loaded from a project must edit the
    /// PROJECT's sprite list, not the ROM's parse of the same level.</summary>
    public SpriteEdit? Sprites { get; private set; }

    // ---- what the UI asks about the open ROM ----
    // Narrow questions rather than a Rom handle: the window has no business knowing that a
    // level's orientation is a bit in a header byte.

    public bool HasRom => Rom is not null;
    public bool HasProject => Project is not null;
    public string? ProjectName => Project?.Name;
    public string? RomFileName => RomPath is null ? null : Path.GetFileName(RomPath);
    public string? VanillaRomPath => Config.VanillaRomPath;

    /// <summary>How many levels there are to choose from.</summary>
    public static int LevelCount => Rom.LevelCount;

    public LevelHeader? Header => Scene?.Level.Header;
    public bool HasLevel => Scene is not null;

    /// <summary>The composed level image, one buffer per animation phase, and its size.</summary>
    public uint[]?[] Phases => Scene?.Phases ?? [];
    public int PxW => Scene?.Width ?? 0;
    public int PxH => Scene?.Height ?? 0;

    /// <summary>How many objects the level currently holds — the edited list, not the parse.</summary>
    public int ObjectCount => Edit?.Objects.Count ?? 0;

    /// <summary>
    /// Fold pending edits into the level image, returning true when the pixels changed.
    /// Sprites are drawn last and straddle cell boundaries, so the overlay is re-blitted rather
    /// than recomposed per cell — that rule is composition's business, not the window's.
    /// </summary>
    public bool RefreshPixels()
    {
        if (Scene is null || Edit is null) return false;
        if (Edit.TakeDirty().Count == 0) return false;
        Scene.RedrawOverlay();
        return true;
    }
    public int Tileset => Scene?.Level.Header.Tileset ?? 0;
    public int Map16TileCount => Rom?.Map16TileCount ?? 0;

    /// <summary>Vertical levels swap the scroll and placement axes, which the canvas needs to
    /// know and cannot work out from pixels.</summary>
    public bool Vertical => Rom is not null && Scene is not null
                            && Rom.IsVerticalMode(Scene.Level.Header.LevelMode);

    /// <summary>The Map16 tile at a cell, or null when the cell is empty. Keeps the level's
    /// grid representation out of the window's hover readout.</summary>
    public int? TileAt(int cx, int cy)
    {
        if (Scene is not { } s || cx < 0 || cy < 0 || cx >= s.Grid.Width || cy >= s.Grid.Height) return null;
        int t = s.Grid.Get(cx, cy);
        return t == Map16Grid.Empty ? null : t;
    }

    /// <summary>The Map16 tile sheet the drawer picks from.</summary>
    public (uint[] Px, int W, int H) Sheet() => Scene?.Sheet() ?? ([], 0, 0);

    /// <summary>The level's 8x8 GFX sheet in one palette row, for the Map16 editor's picker.</summary>
    public (uint[] Px, int W, int H) ChrSheet(int palRow)
        => Rom is { } r && Scene is { } s && s.Palettes[0] is { } pal
            ? GfxSheets.Chr(r, s.Level.Header, LevelNum, 0, pal, palRow)
            : ([], 0, 0);

    public MainEntrance? MainEntrance => Rom?.ReadMainEntrance(LevelNum);

    /// <summary>Remember the user's verified vanilla ROM (used to prep new project bases).</summary>
    public void SetVanillaRom(string path)
    {
        Config.VanillaRomPath = path;
        Config.Save();
    }

    /// <summary>
    /// Where to look for a ROM at startup: the one asked for, else the conventional location.
    /// The filesystem is storage, so the probing lives here and the window just gets a path.
    /// </summary>
    public static string? FindStartupRom(string? requested)
    {
        if (requested is not null && File.Exists(requested)) return requested;
        string fallback = Path.Combine(
            Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
            ".resources", "SMW.smc");
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>
    /// A free folder for a new project inside <paramref name="parent"/>, named after the base
    /// ROM. Project.Create refuses to overwrite an existing base, so the name steps until one
    /// is free rather than failing on the user's second project.
    /// </summary>
    public static string ProjectFolderFor(string parent, string baseRomPath)
    {
        string stem = Path.GetFileNameWithoutExtension(baseRomPath) + "-project";
        string target = Path.Combine(parent, stem);
        for (int n = 2; Directory.Exists(target); n++) target = Path.Combine(parent, $"{stem}-{n}");
        return target;
    }

    /// <summary>Whether a path is worth trying to open (the picker can hand back anything).</summary>
    public static bool FileExists(string? path) => path is not null && File.Exists(path);

    // ---- palette editing ----
    // CGRAM index → BGR555. Held per level and applied on every compose, which is why it lives
    // here and not in a control: the tile CACHES are built from the palette, so an edited colour
    // has to be in place before composition rather than tinted afterwards.

    private readonly Dictionary<int, ushort> paletteEdits = [];

    public int PaletteEditCount => paletteEdits.Count;

    /// <summary>True when this level's colours come from an LM custom palette rather than being
    /// assembled from the header's palette fields — worth showing, because it changes what an
    /// edit will eventually be saved as.</summary>
    public bool HasCustomPalette => Rom?.LmCustomPalette(LevelNum) is not null;

    /// <summary>The level's 256 CGRAM colours as RGBA, edits included.</summary>
    public uint[] PaletteRgba => Scene?.Palettes[0]?.Rgba ?? new uint[256];

    /// <summary>One colour as the SNES stores it, BGR555.</summary>
    public ushort PaletteBgr(int index)
        => Scene?.Palettes[0] is { } p && index is >= 0 and < 256 ? p.Bgr[index] : (ushort)0;

    public bool IsPaletteEdited(int index) => paletteEdits.ContainsKey(index);

    /// <summary>A BGR555 colour as screen RGBA, so a swatch can be previewed without paying for
    /// a recompose.</summary>
    public static uint Rgba(ushort bgr) => Palette.ToRgba(bgr);

    /// <summary>
    /// Change one CGRAM colour and recompose. Returns false when nothing changed, so dragging a
    /// slider across a value it already has does not pay for a full recompose.
    ///
    /// Session-only until the LM custom-palette save path lands (CONTRACT §7e); the edit IS
    /// recorded in the project, so it survives save and reopen.
    /// </summary>
    public bool SetPaletteColor(int index, ushort bgr)
    {
        if (index is < 0 or > 255 || PaletteBgr(index) == bgr) return false;
        paletteEdits[index] = bgr;
        Project?.MarkDirty();
        touched.Add(LevelNum);
        Rebuild("palette");
        return true;
    }

    /// <summary>Drop every palette edit on this level and go back to the ROM's colours.</summary>
    public bool ResetPalette()
    {
        if (paletteEdits.Count == 0) return false;
        paletteEdits.Clear();
        Project?.MarkDirty();
        touched.Add(LevelNum);
        Rebuild("palette");
        return true;
    }

    // ---- catalogs ----
    // Cached here rather than in the window: what invalidates them is a LEVEL or TILESET
    // change, which is this class's business, and the ImGui editor's habit of rebuilding them
    // from UI callbacks is exactly how they went stale.

    private List<CatalogItem>? spriteCatalog, objectCatalog;
    private int[] spriteFiles = [];
    private int objectCatalogTileset = -1;

    /// <summary>The sprite catalog for this level, built on first use.</summary>
    public (IReadOnlyList<CatalogItem> Items, int[] SpFiles) SpriteCatalog()
    {
        if (Rom is null || Scene is null) return ([], []);
        if (spriteCatalog is null)
            spriteCatalog = Catalog.Sprites(Rom, Scene, LevelNum, out spriteFiles);
        return (spriteCatalog, spriteFiles);
    }

    /// <summary>The object catalog for this TILESET, built on first use. Footprints are a
    /// property of the tileset, so switching levels within one costs nothing.</summary>
    public IReadOnlyList<CatalogItem> ObjectCatalog()
    {
        if (Rom is null || Scene is null) return [];
        if (objectCatalog is null || objectCatalogTileset != Tileset)
        {
            objectCatalog = Catalog.Objects(Rom, Scene);
            objectCatalogTileset = Tileset;
        }
        return objectCatalog;
    }

    // ---- opening ----

    /// <summary>Open a bare ROM with no project. Editing still works; saving does not, which
    /// the caller surfaces — a ROM is not a project.</summary>
    public bool OpenRom(string path)
    {
        try
        {
            Rom = Rom.Load(path);
            RomPath = path;
            Project = null;
            touched.Clear();
            ShowLevel(LevelNum);
            Report($"{Path.GetFileName(path)} — {Rom.Title.Trim()} (no project: File ▸ New Project to save edits)");
            return true;
        }
        catch (Exception ex) { Report("could not open: " + ex.Message); return false; }
    }

    public bool OpenProject(string pdpPath)
    {
        try
        {
            var p = Project.Open(pdpPath);
            // Bring an old base up to date before anything reads it, exactly as the ImGui
            // editor does on open — a stale base makes features refuse for invisible reasons.
            string? prepNote = p.PrepareBaseOnOpen(Config.VanillaRomPath);
            if (p.ValidateBase() is { } bad) { Report($"{p.Name}: {bad}"); return false; }

            Rom = Rom.Load(p.BaseRomPath);
            RomPath = p.BaseRomPath;
            Project = p;
            touched.Clear();
            p.SyncBeforeSave = Sync;
            Config.TouchRecentProject(p.FilePath);
            Config.Save();

            string? warn = ProjectSession.Hydrate(Rom, p.Data);
            ShowLevel(LevelNum);
            Report($"project '{p.Name}' opened" + (warn is null ? "" : " — " + warn)
                   + (prepNote is null ? "" : " — base not updated: " + prepNote));
            return true;
        }
        catch (Exception ex) { Report("could not open project: " + ex.Message); return false; }
    }

    public bool NewProject(string folder, string baseRomSource)
    {
        try
        {
            var p = Project.Create(folder, baseRomSource);
            return OpenProject(p.FilePath);
        }
        catch (Exception ex) { Report("could not create project: " + ex.Message); return false; }
    }

    // ---- level navigation ----

    public void ShowLevel(int num)
    {
        if (Rom is null) return;
        // Leaving a level commits its edits: a crash should cost the current level at worst,
        // not everything since the last manual save.
        //
        // EVERY kind of edit counts, not just objects. Gating this on the object list alone lost
        // sprite and palette work silently — you would place a sprite, switch level, come back,
        // and find the base ROM's sprites.
        if (num != LevelNum && LevelDirty) { StashCurrent(); Project?.Save(); }
        LevelNum = num;
        try
        {
            // PROJECT HYDRATION. A level recorded in the project replaces the ROM-parsed
            // object and sprite state with the project's snapshot — without this, reopening
            // a project shows the base ROM's level and the edits look lost (they are not;
            // they are in the .pdp, which is worse: the next save writes the stale view back).
            var saved = Project?.Data.LevelOrNull(num);
            SpriteData? live = null;
            if (saved is not null)
            {
                live = new SpriteData { SpriteMemory = saved.SpriteMemory, Buoyancy = saved.Buoyancy };
                live.Sprites.AddRange(saved.Sprites.Select(s => s.ToSprite()));
            }

            // Palette edits belong to the level, so they are dropped and re-hydrated here —
            // before the compose, since the tile caches are built through them.
            paletteEdits.Clear();
            if (saved is not null)
                foreach (var (k, v) in saved.Palette) paletteEdits[k] = (ushort)v;

            // Built without sprites when the project has its own list, so the ROM's parse is
            // not composed in and then painted over.
            Scene = LevelScene.Build(Rom, num, live is null, paletteEdits);
            var objects = saved is not null
                ? saved.Objects.Select(o => o.ToLevelObject()).ToList()
                : [.. Scene.Level.Objects];
            if (live is not null) DrawSprites(live);

            Sprites = (live ?? Scene.Sprites) is { } sd
                ? new SpriteEdit(sd, Scene.Overlay, Vertical) : null;

            Edit = new LevelEdit(Rom, Scene, objects);
            // Always run the TRACKED render, as the ImGui editor does on every parse. It is
            // what gives each cell an owning object, and without it nothing on a freshly
            // opened level can be selected or hit-tested. It also puts a hydrated level's
            // pixels on screen from its OBJECT LIST rather than the base ROM's parsed grid;
            // for an unedited level the two are identical, and a render failure leaves the
            // parsed grid in place.
            Edit.Rerender();

            // Map16 definitions are per tileset, and the catalogs are rendered with this
            // level's own graphics — both belong to the level that was just loaded.
            NewMap16Edit();
            spriteCatalog = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { Report($"level ${num:X3}: {ex.Message}"); }
    }

    /// <summary>Capture a sprite list's OAM and draw it over every phase of the current scene.
    /// The capture is expensive, which is why it happens once per list change rather than per
    /// repaint.</summary>
    private void DrawSprites(SpriteData sprites)
    {
        if (Rom is null || Scene is null) return;
        var overlay = SpriteOverlay.Build(Rom, sprites, Scene.Level.Header, LevelNum);
        Scene.Overlay = overlay;
        if (Sprites is not null) Sprites.Overlay = overlay;
        Scene.RedrawOverlay();
    }

    /// <summary>One Map16 editor per level, re-raising its commits under this class's event so
    /// the UI subscribes once instead of re-subscribing to a new object each level.</summary>
    private void NewMap16Edit()
    {
        if (Rom is null || Scene is null) return;
        Map16 = new Map16Edit(Rom, Scene.Level.Header.Tileset, Project);
        Map16.Committed += () => Map16Committed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Recompose the current level from the ROM, keeping the object edits. Needed after a
    /// Map16 definition changes: the tile caches are built from the defs, so every tile that
    /// uses the edited one has to be redrawn — in the level, in the picker, and in the sheet.
    /// </summary>
    public void RecomposeScene()
    {
        if (Edit is null) return;
        // A definition change moves the tiles the thumbnails are drawn from, so both catalogs
        // are stale — not only the sprite one.
        spriteCatalog = null;
        objectCatalog = null;
        Rebuild("recompose");
    }

    /// <summary>
    /// Redraw the level after a sprite edit: a moved sprite leaves its old pixels behind, so the
    /// image is recomposed and the overlay re-captured from the edited list.
    /// </summary>
    public void RefreshSprites() => Rebuild("sprite recompose");

    /// <summary>
    /// Recompose the level from the ROM and re-render the object list over it, keeping the
    /// sprite list being edited. Built without sprites whenever there IS an edited list, since
    /// LevelScene.Build would otherwise compose the ROM's own parse underneath it.
    /// </summary>
    private void Rebuild(string what)
    {
        if (Rom is null || Edit is null) return;
        var objects = Edit.Objects.ToList();
        try
        {
            Scene = LevelScene.Build(Rom, LevelNum, Sprites is null, paletteEdits);
            if (Sprites is { } sp) DrawSprites(sp.Sprites);
            var next = new LevelEdit(Rom, Scene, objects);
            next.Rerender();
            Edit = next;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { Report($"{what} failed: {ex.Message}"); }
    }

    /// <summary>
    /// Apply a level-header edit. The header is session state on the Rom (an override keyed by
    /// level), and every field of it changes how the level parses — the tileset drives object
    /// dispatch, the palette fields drive every tile cache — so this stashes the current level
    /// first and then reparses from scratch.
    /// </summary>
    public void ApplyHeader(LevelHeader header)
    {
        if (Rom is null || Scene is null) return;
        StashCurrent();
        Rom.LevelHeaderOverrides[LevelNum] = header.ToBytes();
        ShowLevel(LevelNum);
        Project?.MarkDirty();
    }

    /// <summary>Drop the header edit and go back to the base ROM's header.</summary>
    public void RevertHeader()
    {
        if (Rom is null) return;
        StashCurrent();
        Rom.LevelHeaderOverrides.Remove(LevelNum);
        ShowLevel(LevelNum);
        Project?.MarkDirty();
    }

    /// <summary>
    /// Write the main-entrance record. It is per level but lives OUTSIDE the level's data, in
    /// its own bank-05 tables, so like Map16 it is written straight into the session ROM and
    /// re-read from there at save time rather than being carried in the level state.
    /// </summary>
    public void ApplyEntry(MainEntrance entry)
    {
        if (Rom is null || Rom.ReadMainEntrance(LevelNum) == entry) return;
        Rom.WriteMainEntrance(LevelNum, entry);
        if (Project is not null)
        {
            Project.Data.Level(LevelNum).MainEntrance = Convert.ToHexString(entry.ToBytes());
            Project.MarkDirty();
        }
        touched.Add(LevelNum);
    }

    public bool HasHeaderOverride => Rom?.LevelHeaderOverrides.ContainsKey(LevelNum) == true;

    // ---- saving ----

    /// <summary>Fold the live level into the project snapshot. Called before every write, and
    /// wired to Project.SyncBeforeSave so an autosave cannot miss the current level.</summary>
    private void Sync()
    {
        if (Project is null || Rom is null) return;
        StashCurrent();
        LevelEditState.StashRomWide(Project.Data, Rom, Scene?.Level.Header.Tileset ?? 1);
    }

    private void StashCurrent()
    {
        if (Project is null || Rom is null || Edit is null) return;
        var state = Edit.EditState();
        state.Sprites = Sprites?.Sprites ?? Scene?.Sprites;   // the live list, not the ROM's parse
        foreach (var (k, v) in paletteEdits) state.PaletteEdits[k] = v;
        state.Stash(Project.Data, Rom, LevelNum);
        Project.MarkDirty();
        touched.Add(LevelNum);
    }

    public string Save()
    {
        if (Project is null) return "no project open — File ▸ New Project first";
        Project.Save();                       // SyncBeforeSave folds the live level in
        touched.Clear();
        Report($"saved {Project.Name}");
        return Status;
    }

    public string Build()
    {
        if (Project is null) return "no project open";
        Project.Save();
        var (status, path) = RomBuilder.Build(Project);
        Report(path is null ? status : $"built {Path.GetFileName(path)} — {status}");
        return Status;
    }

    public string ExportBps()
    {
        if (Project is null) return "no project open";
        Project.Save();
        var (status, _) = RomBuilder.ExportBps(Project, Config.VanillaRomPath);
        Report(status);
        return Status;
    }

    /// <summary>Autosave tick — the project debounces its own writes.</summary>
    public void Tick() => Project?.Tick();
}
