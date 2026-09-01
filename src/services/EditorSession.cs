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

    /// <summary>
    /// The ACTIVE layer's object editor — what the canvas edits. Layer 2 uses the same object
    /// stream format, so the same class drives both; which one is live is <see cref="EditLayer"/>.
    /// </summary>
    public LevelEdit? Edit => EditLayer == 1 ? layer2 : layer1;

    private LevelEdit? layer1, layer2;

    /// <summary>The base ROM's layer-2 stream, kept to diff against on save. Null means the base
    /// had no stream at all, which is how a background→objects conversion is recognised.</summary>
    private List<LevelObject>? baseLayer2;

    /// <summary>0 or 1. Which layer is active changes what every click does.</summary>
    public int EditLayer { get; private set; }

    /// <summary>Whether layer 2 has an object stream to edit at all. A background-image layer 2
    /// has none until it is converted, and "no layer-2 objects" and "an empty layer 2" are
    /// genuinely different in the ROM — so the conversion is explicit rather than implied by
    /// clicking L2.</summary>
    public bool Layer2Editable => layer2 is not null;

    /// <summary>True when the layer-2 stream is the PROJECT's rather than the base ROM's — the
    /// only case where dropping it back to a background image is possible.</summary>
    public bool Layer2FromProject => baseLayer2 is null && layer2 is not null;

    /// <summary>Whether this level's mode ever loads layer-2 objects. It is possible to build an
    /// object layer a level mode simply never reads, and that silence is the loudest failure
    /// mode here.</summary>
    public bool LevelModeReadsLayer2
        => Scene is { } s && Rom.LoadsLayer2Objects(s.Level.Header.LevelMode);

    /// <summary>Map16 definition editing for the current level's tileset. Rebuilt with the
    /// level, because a new tileset means new definition offsets.</summary>
    public Map16Edit? Map16 { get; private set; }

    /// <summary>Committed Map16 bytes changed: tile caches, the level and the sheets are all
    /// stale. Raised for whichever Map16Edit is current, so the UI subscribes once.</summary>
    public event EventHandler? Map16Committed;

    /// <summary>Levels whose edits have not been stashed into the project yet.</summary>
    private readonly HashSet<int> touched = [];

    public event EventHandler? Changed;

    /// <summary>The scene and BOTH layers' editors were replaced (see <see cref="Rebuild"/>).
    /// Anything holding one has to let go: an edit made through a discarded LevelEdit renders
    /// into a discarded scene, so it looks like it worked and changes nothing on screen.</summary>
    public event EventHandler? SceneRebuilt;

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

    /// <summary>Whether a screen exit on this base can name a level above $0FF. False on a raw
    /// vanilla or pre-v7 base, where the destination is one byte and its ninth bit comes from
    /// the submap the player entered from.</summary>
    public bool ExitsReachHighLevels => Rom?.HasExitLevelHighBit == true;

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

    /// <summary>The Map16 tile sheet the drawer picks from, one image per animation phase — a
    /// tile made of animated graphics has to animate wherever it is DRAWN, not only in the
    /// level.</summary>
    public (uint[]?[] Px, int W, int H) SheetPhases()
    {
        if (Scene is not { } s) return (new uint[4][], 0, 0);
        var px = new uint[4][];
        int w = 0, h = 0;
        for (int p = 0; p < 4; p++) (px[p], w, h) = s.Sheet(p);
        return (px, w, h);
    }

    /// <summary>The empty-page tile per phase, for the picker to tile over unallocated pages.</summary>
    public uint[]?[] PlaceholderPhases()
    {
        var px = new uint[4][];
        if (Rom is { } r && Scene is { } s)
            for (int p = 0; p < 4; p++) px[p] = s.Placeholder(r, LevelNum, p);
        return px;
    }

    /// <summary>The level's 8x8 GFX sheet in one palette row, for the Map16 editor's picker —
    /// again one per phase, off the scene's own per-phase graphics.</summary>
    // ---- Background (layer 2), reference/CONTRACT.md §10 / §10b ----

    /// <summary>True when this level's layer 2 is a background IMAGE, which is the only case the
    /// Background tab can edit. An object-mode layer 2 is edited in the Level tab instead — the
    /// same split LM draws ("if a level is set to have level data on layer 2 instead of an image,
    /// there will be nothing to edit here").</summary>
    public bool Layer2IsBackgroundImage => Scene?.BgImage is not null;

    /// <summary>Two screens side by side, which is how the background repeats horizontally
    /// (<c>within = cx &amp; 0x1F</c>), and vanilla's 27 rows. LM's custom backgrounds carry 32
    /// (§10b) — that arrives with the writer, not before.</summary>
    public const int BgCols = 32, BgRows = 27;

    /// <summary>BG Map16 defs: the fixed 0x200 at $0D9100, which is the whole picker.</summary>
    public const int BgSheetTiles = 0x200;

    /// <summary>The level's background drawn as pixels, one image per animation phase (BG tiles
    /// animate like any other). Empty when layer 2 is not a background image.</summary>
    public (uint[]?[] Px, int W, int H) BgPhases()
    {
        var px = new uint[4][];
        int w = 0, h = 0;
        if (Scene is not { } s) return (px, 0, 0);
        for (int p = 0; p < 4; p++)
        {
            var (img, iw, ih) = s.BgSurface(BgCols, BgRows, p);
            if (img.Length == 0) continue;
            px[p] = img; w = iw; h = ih;
        }
        return (px, w, h);
    }

    /// <summary>The BG Map16 defs as a picker sheet, one per phase. These are the fixed 0x200
    /// defs at $0D9100 — LM's BG pages 80-81, our virtual tiles 0x4000-0x41FF.</summary>
    public (uint[]?[] Px, int W, int H) BgSheetPhases()
    {
        var px = new uint[4][];
        int w = 0, h = 0;
        if (Scene is not { } s) return (px, 0, 0);
        for (int p = 0; p < 4; p++)
        {
            var (img, iw, ih) = s.BgSheet(p);
            if (img.Length == 0) continue;
            px[p] = img; w = iw; h = ih;
        }
        return (px, w, h);
    }

    // ---- Layer 3, reference/CONTRACT.md §12b ----

    /// <summary>The level's Layer 3 Options value, 0-3 — 0 means the level has no layer 3.
    /// Name it with <see cref="Layer3.OptionNames"/>.</summary>
    public int Layer3Option => Rom is { } r && HasLevel ? Layer3.Option(r, LevelNum) : 0;

    // ---- background tilemap EDITING ----
    // Both layers paint through TilemapEdit; what differs is where a (column, row) lands in the
    // level's own buffer, and what a cell's number means. Created per level in ShowLevel, and
    // each commit writes straight back into the session ROM's override store — the same store
    // the import path fills, so an edited map and an imported one are the same thing downstream.

    /// <summary>The layer-2 background as a paintable 32x27 grid of BG Map16 tiles, or null when
    /// this level's layer 2 is an object stream (nothing to paint) or no level is open.</summary>
    public TilemapEdit? BgMap { get; private set; }

    /// <summary>The layer-3 tilemap as a paintable 64x64 grid, or null when the level has no
    /// layer 3. Cells are whole BG3 words, so a stamp carries the brush's palette and flips.</summary>
    public TilemapEdit? Layer3Map { get; private set; }

    /// <summary>Build both editors for the level now open. An edit to either is a per-level
    /// override, so a level that has never been painted has no buffer of its own until it is.</summary>
    private void OpenBackgroundEdits()
    {
        BgMap = null; Layer3Map = null;
        if (Rom is not { } rom || Scene is not { } s) return;

        if (s.BgImage is not null)
        {
            // The page bits come from the stream's ADDRESS, not the data, so the editable value
            // is the low def index and the page rides along unchanged (CONTRACT §10a).
            byte[] low = rom.BgTilemaps.TryGetValue(LevelNum, out var edited)
                ? edited : [.. s.BgImage.Select(t => (byte)(t & 0xFF))];
            var cells = new int[low.Length];
            for (int i = 0; i < low.Length; i++) cells[i] = low[i];
            BgMap = new TilemapEdit(cells, BgCols, BgRows, 16,
                                    (c, r) => (c / 16) * 0x1B0 + r * 16 + (c % 16));
            BgMap.Committed += () =>
            {
                rom.BgTilemaps[LevelNum] = [.. BgMap.Cells.Select(v => (byte)v)];
                if (Project is not null)
                {
                    Project.Data.Level(LevelNum).BgTilemap =
                        Convert.ToBase64String(rom.BgTilemaps[LevelNum]);
                    Project.MarkDirty();
                }
                touched.Add(LevelNum);
                RecomposeScene();
            };
        }

        if (Layer3.LevelTilemap(rom, LevelNum, s.Level.Header.LevelMode, Layer3.Option(rom, LevelNum))
            is { } map)
        {
            Layer3Map = new TilemapEdit(map, Layer3.Cols, Layer3.Rows, 8, Layer3.CellIndex);
            Layer3Map.Committed += () =>
            {
                // The first stroke turns a level using vanilla's shared (mode, option) tilemap
                // into one with a map of its own — the same move LM makes when you edit a shared
                // background, and the only way to avoid editing every level that shares it.
                rom.Layer3Tilemaps[LevelNum] = Layer3.ToBytes(Layer3Map.Cells);
                if (Project is not null)
                {
                    Project.Data.Level(LevelNum).Layer3Tilemap =
                        Convert.ToBase64String(rom.Layer3Tilemaps[LevelNum]);
                    Project.MarkDirty();
                }
                touched.Add(LevelNum);
            };
        }
    }

    /// <summary>Pixels for one BG Map16 tile, for the Background canvas and its drawer.</summary>
    public uint[]? BgCellPixels(int tile, int phase = 0)
        => Scene?.BgCaches[phase & 3] is { } cache && (uint)(tile & 0x1FF) < cache.Length
           ? cache[tile & 0x1FF] : null;

    /// <summary>Pixels for one layer-3 tilemap WORD — tile, palette group and both flips, which
    /// is why it is keyed by the word rather than by the tile number. Cached per distinct word:
    /// a 64x64 map redraws on every stamp and only ever names a handful of them.</summary>
    public uint[]? Layer3CellPixels(int word)
    {
        if (Rom is not { } r || Scene?.Palettes[0] is not { } pal) return null;
        if (word < 0 || (word & 0x3FF) >= Layer3.TileCount) return null;
        if (layer3Cells.TryGetValue(word, out var hit)) return hit;
        layer3Tiles ??= Layer3.Tiles(r, LevelNum);
        return layer3Cells[word] = Layer3.CellPixels(word, layer3Tiles, pal);
    }

    private readonly Dictionary<int, uint[]?> layer3Cells = [];
    private byte[]?[]? layer3Tiles;

    /// <summary>Drop the layer-3 pixel caches — a repointed LG slot or a palette edit changes
    /// what every word draws as.</summary>
    private void InvalidateLayer3Cells() { layer3Cells.Clear(); layer3Tiles = null; }

    /// <summary>True when this level draws an imported tilemap rather than vanilla's.</summary>
    public bool Layer3TilemapImported => Rom is { } r && HasLevel && r.Layer3Tilemaps.ContainsKey(LevelNum);

    /// <summary>True when this level's background is one the project edited.</summary>
    public bool BgTilemapEdited => Rom is { } r && HasLevel && r.BgTilemaps.ContainsKey(LevelNum);

    /// <summary>
    /// Import a raw layer-3 tilemap for this level — LM's LT3 file shape, a flat little-endian
    /// 16-bit map of 0x800, 0x1000 or 0x2000 bytes (0x2000 being the whole 64x64 window).
    ///
    /// EDITOR-ONLY so far, and the build says so: LM's tilemap-bypass slot in the per-level
    /// record is not decoded (CONTRACT §12b), so there is nowhere to write it that the game
    /// would read. It renders, it persists in the project, and it will ship the day that slot
    /// is pinned — which is a better answer than refusing the import.
    /// </summary>
    public bool ImportLayer3Tilemap(string path)
    {
        if (Rom is null || !HasLevel) { Report("no level open"); return false; }
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) { Report($"import failed: {e.Message}"); return false; }

        if (!Layer3.IsTilemapSize(bytes.Length))
        {
            Report($"import rejected: {Path.GetFileName(path)} is 0x{bytes.Length:X} bytes — "
                 + "a layer-3 tilemap is 0x800, 0x1000 or 0x2000");
            return false;
        }
        Rom.Layer3Tilemaps[LevelNum] = bytes;
        if (Project is not null)
        {
            Project.Data.Level(LevelNum).Layer3Tilemap = Convert.ToBase64String(bytes);
            Project.MarkDirty();
        }
        touched.Add(LevelNum);
        Report($"layer 3 tilemap ← {Path.GetFileName(path)} (0x{bytes.Length:X} bytes)");
        return true;
    }

    /// <summary>Drop an imported tilemap, back to vanilla's (level mode, option) pick.</summary>
    public bool ClearLayer3Tilemap()
    {
        if (Rom is null || !Rom.Layer3Tilemaps.Remove(LevelNum)) return false;
        if (Project is not null)
        {
            Project.Data.Level(LevelNum).Layer3Tilemap = null;
            Project.MarkDirty();
        }
        touched.Add(LevelNum);
        Report("layer 3 tilemap ← the base ROM's");
        return true;
    }

    /// <summary>Whether an option value would actually reach a tilemap on THIS level — the
    /// pointer table is indexed by (level mode, option) and only covers modes 0-14, so a legal
    /// option can still land on nothing (CONTRACT §12b).</summary>
    public bool Layer3HasTilemap(int option)
        => Rom is { } r && Scene is { } s
           && Layer3.LevelTilemap(r, LevelNum, s.Level.Header.LevelMode, option) is not null;

    /// <summary>The level's layer 3 drawn as one 512x512 image, empty when it has none. Not
    /// per-phase: its GFX are the fixed 2bpp files and its colours sit outside the animated
    /// ones, so unlike the background it does not move between phases.</summary>
    public (uint[] Px, int W, int H) Layer3Image()
    {
        if (Rom is not { } r || Scene is not { } s || s.Palettes[0] is not { } pal) return ([], 0, 0);
        return Layer3.LevelTilemap(r, LevelNum, s.Level.Header.LevelMode, Layer3.Option(r, LevelNum)) is { } map
            ? Layer3.Render(map, Layer3.Tiles(r, LevelNum), pal) : ([], 0, 0);
    }

    /// <summary>The 512 layer-3 8x8s as a picker sheet, in BG palette 2 (CGRAM 08-0B) — the
    /// first of the four groups the layer-3 colours occupy.</summary>
    public (uint[] Px, int W, int H) Layer3Sheet()
        => Rom is { } r && Scene?.Palettes[0] is { } pal
           ? GfxSheets.Tiles(Layer3.Tiles(r, LevelNum), pal, 0, colorOffset: 8) : ([], 0, 0);

    /// <summary>The level's composed sprite 8x8s (SP1-SP4, bypass honored) as one sheet,
    /// for the destination picker's sprite range (LM dest tiles 400-5FF).</summary>
    public (uint[] Px, int W, int H) SpriteSheet(int palRow)
    {
        if (Rom is not { } r || Scene is not { } s || s.Palettes[0] is not { } pal) return ([], 0, 0);
        return GfxSheets.Tiles(SpriteRender.LoadSpTiles(r, s.Level.Header, LevelNum), pal, palRow);
    }

    public (uint[]?[] Px, int W, int H) ChrPhases(int palRow)
    {
        if (Rom is not { } r || Scene is not { } s) return (new uint[4][], 0, 0);
        var px = new uint[4][];
        int w = 0, h = 0;
        for (int p = 0; p < 4; p++)
        {
            if (s.Palettes[p] is not { } pal) continue;
            (px[p], w, h) = GfxSheets.Chr(s.Fg(r, LevelNum, p), pal, palRow);
        }
        return (px, w, h);
    }

    public MainEntrance? MainEntrance => Rom?.ReadMainEntrance(LevelNum);

    /// <summary>Remember the user's verified vanilla ROM (used to prep new project bases).</summary>
    public void SetVanillaRom(string path)
    {
        Config.VanillaRomPath = path;
        Config.Save();
    }

    /// <summary>
    /// Whether a ROM is the known-good vanilla image, and what that means for the user. The hash
    /// is taken headerless, so a copier-header copy of the same ROM still verifies. A mismatch is
    /// a warning rather than a refusal: an LM-prepared base works fully, it just has to be the
    /// exact file collaborators use, because the project pins its hash either way.
    /// </summary>
    public static string DescribeRom(string? path)
    {
        if (path is null || !File.Exists(path)) return "";
        try
        {
            return RomHash.HeaderlessSha256File(path) == RomHash.VanillaUsSha256
                ? "Verified: vanilla Super Mario World (U). A base copy is prepared automatically "
                + "for full editing — Map16, tile placement, palettes and sprites."
                : "Warning: not the known vanilla US ROM. It is used as-is (an LM-prepared base "
                + "works fully), and collaborators will need this exact file.";
        }
        catch (Exception e) { return "Could not read file: " + e.Message; }
    }

    // ---- updates ----
    // The UI cannot reach the config or the filesystem (ArchitectureTests enforces it), so the
    // whole update path is exposed here: settings, the check, the download, the install.

    /// <summary>Whether startup asks GitHub about newer releases.</summary>
    public bool CheckForUpdates
    {
        get => Config.CheckForUpdates;
        set { Config.CheckForUpdates = value; Config.Save(); }
    }

    /// <summary>
    /// A newer release, or null for nothing to offer. <paramref name="userAsked"/> forces the
    /// request; otherwise it is rate-limited to once a day and honours the setting, so calling
    /// this on every startup is free.
    /// </summary>
    public async Task<UpdateInfo?> FindUpdate(bool userAsked, CancellationToken ct = default)
    {
        if (!UpdateCheck.Due(userAsked, Config.CheckForUpdates, Config.LastUpdateCheckUtc, DateTime.UtcNow))
            return null;

        // Stamped before the result is known: a check that went out counts, or a machine that is
        // offline for a week would retry on every single launch.
        Config.LastUpdateCheckUtc = DateTime.UtcNow;
        Config.Save();

        return await UpdateCheck.Latest(UpdateCheck.Current, Config.SkippedUpdate,
                                        OperatingSystem.IsWindows(), ct);
    }

    /// <summary>Never offer this version again. A later one still gets through.</summary>
    public void SkipUpdate(UpdateInfo u)
    {
        Config.SkippedUpdate = u.Display;
        Config.Save();
    }

    /// <summary>The running build's version, for the about/update dialog.</summary>
    public string CurrentVersion => UpdateCheck.Current.ToString(3);

    public Task<string> DownloadUpdate(UpdateInfo u, IProgress<double>? progress = null,
                                       CancellationToken ct = default)
        => UpdateCheck.Download(u, progress, ct);

    /// <summary>Start the install. The caller must close the app immediately after a null
    /// return — see <see cref="UpdateCheck.Apply"/>.</summary>
    public string? ApplyUpdate(string downloadedFile) => UpdateCheck.Apply(downloadedFile);

    /// <summary>True on the very first run, before the config knows where a vanilla ROM lives.</summary>
    public bool NeedsVanillaRom => Config.VanillaRomPath is null;

    /// <summary>How the GFX browser lists files ("names", "list" or "cards"), remembered
    /// per user like the update-check switch above.</summary>
    public string GfxBrowserView
    {
        get => Config.GfxBrowserView;
        set { Config.GfxBrowserView = value; Config.Save(); }
    }

    // ---- opening a project whose base ROM is missing ----
    // A .pdp is shareable on its own; the base ROM copy beside it deliberately is not. So opening
    // someone else's project usually means locating a ROM and having it verified against the hash
    // the project pinned.

    private Project? pendingOpen;

    /// <summary>What is wrong with the pending project's base, or null when nothing is.</summary>
    public string? PendingBaseProblem { get; private set; }

    /// <summary>The base ROM the pending project pins, described for the recovery prompt.</summary>
    public string PendingBaseDescription => pendingOpen is { } p
        ? $"{p.Data.BaseRom.Title} ({p.Data.BaseRom.Size / 1024} KB, sha256 {p.Data.BaseRom.Sha256[..12]}…)"
        : "";

    public string? PendingProjectName => pendingOpen?.Name;

    /// <summary>
    /// Adopt a located ROM as the pending project's base. Refuses one whose hash does not match
    /// what the project pinned — a base that only looks right would corrupt every offset the
    /// project recorded against it.
    /// </summary>
    public string? AdoptPendingBase(string romPath)
    {
        if (pendingOpen is not { } p) return "nothing waiting for a base ROM";
        if (p.AdoptBase(romPath) is { } problem) { PendingBaseProblem = problem; return problem; }
        string path = p.FilePath;
        pendingOpen = null;
        PendingBaseProblem = null;
        OpenProject(path);
        return null;
    }

    public void CancelPendingOpen()
    {
        pendingOpen = null;
        PendingBaseProblem = null;
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

    // ---- command line ----
    // The ROM tools run in the same executable as the editor. They are storage-layer work, so
    // they are reached through here rather than from the window: the presentation layer stays
    // unable to call storage, and the process entry point stays a composition root.

    /// <summary>The switch that forces command-line mode with no command to run — it prints the
    /// available ones. A recognised command implies it, so both spellings work.</summary>
    public const string HeadlessSwitch = "--headless";

    /// <summary>Whether these arguments mean "do not open a window".</summary>
    public static bool IsCommandLine(string[] args)
        => args.Contains(HeadlessSwitch) || DebugCommands.Names.Any(args.Contains);

    /// <summary>Run the ROM command in <paramref name="args"/> and return its exit code, or
    /// print what is available when only the switch was given.</summary>
    public static int RunCommandLine(string[] args)
    {
        if (DebugCommands.TryDispatch(args) is { } code) return code;
        Console.Error.WriteLine("pipe-dream — ROM tools\n");
        Console.Error.WriteLine("Commands:");
        foreach (string name in DebugCommands.Names) Console.Error.WriteLine("  " + name);
        Console.Error.WriteLine($"\nWithout {HeadlessSwitch} or one of these, the editor opens.");
        return 1;
    }

    /// <summary>Recently opened projects, most recent first, with any that have been moved or
    /// deleted pruned — offering a menu entry that cannot open is worse than a short list.</summary>
    public IReadOnlyList<string> RecentProjects
    {
        get
        {
            var gone = Config.RecentProjects.Where(p => !File.Exists(p)).ToList();
            if (gone.Count > 0)
            {
                foreach (string p in gone) Config.RecentProjects.Remove(p);
                Config.Save();
            }
            return Config.RecentProjects;
        }
    }

    /// <summary>Read-only facts about the open ROM, for the info window.</summary>
    public IEnumerable<(string Label, string Value)> RomInfo()
    {
        if (Rom is not { } r) yield break;
        yield return ("File", RomPath ?? "");
        yield return ("Copier header", r.HeaderOffset != 0 ? "yes (0x200)" : "no");
        yield return ("Title", $"'{r.Title}'");
        yield return ("Map mode", $"{r.MapModeName} (0x{r.MapMode:X2})");
        yield return ("ROM size", $"{r.ActualRomSize / 1024} KB on disk, "
                                + $"{r.DeclaredRomSize / 1024} KB declared");
        yield return ("Checksum", $"0x{r.Checksum:X4} (complement 0x{r.ChecksumComplement:X4})");
        yield return ("Valid RATS tags", RatsWriter.EnumerateRats(r).Count().ToString());
        yield return ("Map16 tiles", $"0x{r.Map16TileCount:X}");
        yield return ("Direct Map16", r.HasDm16Hijack ? "yes" : "no (tile placement unavailable)");
    }

    /// <summary>The prep version this build inserts. A project pinned to an older one keeps
    /// working; upgrading is deliberate because it changes the base ROM's hash.</summary>
    public static int PrepVersion => RomPrep.Version;

    public bool CanUpgradeBasePrep => Project?.CanUpgradeBasePrep == true;

    /// <summary>
    /// Re-prep the project's base ROM to the current version. Edits are flushed first, because
    /// the base is replaced on disk, and the project is reopened on the new base afterwards so
    /// nothing is left reading the old one.
    /// </summary>
    public string UpgradeBasePrep()
    {
        if (Project is not { } p) return "no project open";
        p.Save();
        if (p.UpgradeBasePrep(Config.VanillaRomPath) is { } problem) return "upgrade failed: " + problem;
        string path = p.FilePath;
        return OpenProject(path) ? $"base upgraded to prep v{RomPrep.Version}" : Status;
    }

    /// <summary>
    /// Re-read the current level from the ROM and the project, throwing away anything not yet
    /// stashed. The point of a Reload is to get back to the recorded state, so it deliberately
    /// does NOT stash on the way out — which is exactly what ShowLevel does for the level it is
    /// already on.
    ///
    /// This used to force a sentinel level number first, to stop ShowLevel treating the call as
    /// "staying put". That made ShowLevel stash under the SENTINEL, writing a level entry keyed
    /// -1 into the project, and the builder then tried to parse -1 as a level number and died.
    /// The stash it was trying to avoid is the one ShowLevel already skips.
    /// </summary>
    public void ReloadLevel() => ShowLevel(LevelNum);

    /// <summary>Whether the sprite overlay is drawn. Off makes the terrain under a crowded
    /// level's sprites visible, which is why it is a toggle and not a preference.</summary>
    public bool ShowSprites
    {
        get => showSprites;
        set
        {
            if (showSprites == value) return;
            showSprites = value;
            ShowLevel(LevelNum);          // the overlay is composed in, so this is a re-parse
        }
    }
    private bool showSprites = true;

    // ---- graphics ----

    /// <summary>Pixel editing for one GFX file. ROM-wide rather than per level, so it survives
    /// level switches — unlike the object, sprite and palette state above it.</summary>
    public GfxEdit? GfxPixels { get; private set; }

    /// <summary>The level's VRAM GFX bins in drawer order: the ten FG/BG/SP ones, then LG1-LG4
    /// (the layer-3 window), then the animation slots.</summary>
    public (string Name, int PalRow, int BypWord, int Def, int File, int ColorOffset, int Bpp)[] GfxBins
        => Rom is { } r && Scene is { } s ? Gfx.LevelSlots(r, s.Level.Header, LevelNum) : [];

    /// <summary>The 16 tileset / sprite-set choices for the graphics-header dialog: the setting
    /// number plus the GFX files it loads, straight from the ROM's own lists — the lists have
    /// no prose names, and the files are what actually distinguishes the settings.</summary>
    public (IReadOnlyList<string> Layer1, IReadOnlyList<string> Sprites) GfxHeaderChoices()
    {
        if (Rom is not { } rom) return ([], []);
        List<string> Items(int listBase) => [.. Enumerable.Range(0, 16).Select(i =>
            $"{i:X} — GFX " + string.Join(" ", Enumerable.Range(0, 4)
                .Select(s => $"{rom.Data[rom.FileOffset(listBase) + i * 4 + s]:X2}")))];
        return (Items(Gfx.ObjectGfxList), Items(Gfx.SpriteGfxList));
    }

    /// <summary>How a bin's current file got there, for the drawer's badge. A base file — fork or
    /// not — says nothing: it is the normal case, and a badge on all ten bins is not a badge.</summary>
    public string GfxBinNote(int bypWord, int file, int def)
        => Rom is not { } r ? ""
         : Gfx.SourceSnes(r, file) < 0 && r.ImportedGfx.ContainsKey(file) ? "custom"
         : r.GfxSlotOverrides.ContainsKey((LevelNum, bypWord)) ? "override"
         : file != def ? "bypass" : "";

    public string? GfxName(int file)
        => Rom?.GfxName(file) is { Length: > 0 } n ? n : null;

    /// <summary>One GFX file decoded as a tile sheet, for a preview. Empty when the id resolves
    /// nowhere or will not decode — a bin pointing at nothing is normal (0x7F means "unused").</summary>
    public (uint[] Px, int W, int H) GfxFileSheet(int file, int palRow, int colorOffset = 0, int bpp = 0)
    {
        if (Rom is null || file == 0x7F || Scene?.Palettes[0] is not { } pal) return ([], 0, 0);
        if (Gfx.Cached(Rom, file) is not { } data) return ([], 0, 0);
        try { return Gfx.TileSheet(data, bpp > 0 ? bpp : Gfx.FileBpp(Rom, file), pal, palRow, colorOffset: colorOffset); }
        catch { return ([], 0, 0); }
    }

    /// <summary>
    /// Point one VRAM bin at a different GFX file. This is a SESSION override recorded in the
    /// project (CONTRACT §7d's Super GFX Bypass), so it re-resolves everything that reads the
    /// bin: the level's tiles, the sprite graphics and the Map16 sheet alike.
    /// </summary>
    public string SetGfxSlot(int bypWord, int file)
    {
        if (Rom is null) return "no ROM open";
        if (file is < 0 or > 0xFFF) return "GFX ids run 000-FFF";
        Rom.GfxSlotOverrides[(LevelNum, bypWord)] = file;
        Project?.MarkDirty();
        touched.Add(LevelNum);
        RecomposeScene();
        return $"bin ← GFX{file:X3}" + (GfxName(file) is { } n ? $" \"{n}\"" : "");
    }

    /// <summary>
    /// Import a raw planar .bin as a custom ExGFX file: detect its depth from the size, normalise
    /// to the ROM's depth, and store it under the next FREE id ≥ 0x100. Returns that id, or -1 with
    /// the reason in the status.
    ///
    /// The id must be fresh — skipping both prior imports and ids the ROM itself resolves — or the
    /// import would shadow a real ExGFX file other levels use. Pointing a bin at the result is a
    /// separate step (<see cref="SetGfxSlot"/>): importing and assigning are different decisions.
    /// </summary>
    public (int Id, string Status) ImportGfx(string path)
    {
        if (Rom is null) return (-1, "no ROM open");
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) { return (-1, $"import failed: {e.Message}"); }

        int bpp = Gfx.DetectBpp(bytes);
        if (bpp == 0)
            return (-1, $"import rejected: {Path.GetFileName(path)} is 0x{bytes.Length:X} bytes — "
                      + "not whole 3bpp (x24) or 4bpp (x32) planar tiles");
        int romBpp = Gfx.RomBpp(Rom);
        bytes = Gfx.NormalizeBpp(bytes, bpp, romBpp, out bool plane3Dropped);

        // A file named by the ExGFX### convention carries its own id — honour it when it is a
        // usable custom id (0x100+) that nothing here resolves yet. Anything else auto-assigns.
        string stem = Path.GetFileNameWithoutExtension(path);
        var m = System.Text.RegularExpressions.Regex.Match(stem, "^ExGFX([0-9A-Fa-f]{3})$",
                                                           System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        int id = m.Success
              && int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber) is >= 0x100 and <= 0xFFF and var wanted
              && !Rom.ImportedGfx.ContainsKey(wanted) && Gfx.SourceSnes(Rom, wanted) < 0
            ? wanted : 0x100;
        while (id <= 0xFFF && (Rom.ImportedGfx.ContainsKey(id) || Gfx.SourceSnes(Rom, id) >= 0)) id++;
        if (id > 0xFFF) return (-1, "import failed: no free ExGFX id (0x100-0xFFF all in use)");

        Rom.ImportedGfx[id] = bytes;
        // The filename is the only human-meaningful label an import has; keeping it beats
        // leaving the user with a bare hex id.
        Rom.ImportedGfxNames[id] = stem;
        Gfx.InvalidateCache(Rom);
        Project?.MarkDirty();

        return (id, $"imported {Path.GetFileName(path)} as GFX{id:X3} ({bpp}bpp → {romBpp}bpp)"
                  + (plane3Dropped ? " — nonzero plane 3 data discarded" : ""));
    }

    /// <summary>The sheet for the file currently open in the pixel editor.</summary>
    public (uint[] Px, int W, int H) GfxSheet()
        => GfxPixels is { } g && Scene?.Palettes[0] is { } pal ? g.Sheet(pal) : ([], 0, 0);

    /// <summary>
    /// Files a picker should offer — the project's custom ExGFX or the ROM's base files, filtered.
    /// <paramref name="filter"/> matches names anywhere and hex ids by prefix, so "grass" finds it
    /// by name and "10" finds $100-$10F.
    /// </summary>
    public List<GfxFileInfo> GfxFiles(bool custom, string filter)
    {
        if (Rom is null) return [];
        return Gfx.Candidates(Rom, custom, filter).Select(id => new GfxFileInfo
        {
            Id = id,
            Custom = Gfx.SourceSnes(Rom, id) < 0,
            Name = GfxName(id),
            Description = Gfx.Describe(Rom, id),
            // Palette row 2 (the FG row) is the least misleading single choice for a preview; the
            // real row depends on which bin the file ends up in.
            Sheet = GfxFileSheet(id, 2),
        }).ToList();
    }

    /// <summary>Whether the open GFX file is one of the ROM's own — including a copy-on-write fork
    /// of one, which has no ExGFX id of its own yet. False means a custom ExGFX file. This is what
    /// makes a save ask for a name, and what the mode's badge shows.</summary>
    public bool GfxIsStock
        => Rom is { } r && GfxPixels is { } g && Gfx.SourceSnes(r, g.File) >= 0;

    /// <summary>Committed GFX pixel edits that are not in project.pdp yet.</summary>
    public bool GfxDirty => GfxPixels?.Dirty == true;

    /// <summary>The name a new custom file derived from <paramref name="from"/> gets when the
    /// user offers none: the source's own label plus "copy". Custom files go by name in the UI,
    /// so leaving one nameless would strand it behind a bare hex id.</summary>
    public string DefaultGfxName(int from)
        => (GfxName(from) is { Length: > 0 } n ? n : $"GFX{from:X3}") + " copy";

    /// <summary>
    /// Save the open GFX file into the project as a custom ExGFX.
    ///
    /// An already-custom file is just written under its own id. A STOCK file MOVES to the next
    /// free id ≥ 0x100 under <paramref name="name"/>: the stock file is restored for everyone
    /// else, and this level's bins that pointed at it are repointed to the new file — the same
    /// shape <see cref="ImportGfx"/> gives an imported .bin, so the edit travels with the level
    /// instead of shadowing stock graphics ROM-wide.
    /// </summary>
    public string SaveGfx(string name)
    {
        if (Rom is null || GfxPixels is not { } g) return "no GFX open";
        if (Project is null) return "no project open — File ▸ New Project first";
        g.EndStroke();
        if (Gfx.EditableBytes(Rom, g.File, out _) is not { } bytes) return $"GFX{g.File:X3} is empty";

        if (Gfx.SourceSnes(Rom, g.File) >= 0)
        {
            int id = 0x100;
            while (id <= 0xFFF && (Rom.ImportedGfx.ContainsKey(id) || Gfx.SourceSnes(Rom, id) >= 0)) id++;
            if (id > 0xFFF) return "save failed: no free ExGFX id (0x100-0xFFF all in use)";
            int from = g.File;
            Rom.ImportedGfx[id] = bytes;
            Rom.ImportedGfx.Remove(from);        // the stock file comes back for every other user
            Rom.ImportedGfxNames[id] = name.Trim().Length > 0 ? name.Trim() : DefaultGfxName(from);
            Gfx.InvalidateCache(Rom);
            g.Retarget(from, id);
            foreach (var bin in GfxBins)
                if (bin.File == from) SetGfxSlot(bin.BypWord, id);
        }
        else if (name.Trim().Length > 0) Rom.ImportedGfxNames[g.File] = name.Trim();

        // An ExAnimation source file lives in the ROM uncompressed: push the edited bytes into
        // its block too, so the animation overlay draws what was just painted.
        if (g.File is >= 0x60 and <= 0x63 && Rom.LmExAnimBase >= 0)
        {
            Rom.SetLmAltExGfx(g.File - 0x60, bytes);
            Gfx.InvalidateCache(Rom);
            Scene?.InvalidateGfx();
            RecomposeScene();
        }

        Save();
        return GfxName(g.File) is { Length: > 0 } n ? $"saved \"{n}\"" : $"saved GFX{g.File:X3}";
    }

    /// <summary>
    /// Save a COPY of the open GFX file as a new custom ExGFX under <paramref name="name"/>.
    ///
    /// The source keeps its bytes: a custom source stays as it is, and a stock source drops its
    /// copy-on-write fork so the stock file is restored for everyone else. The editor and this
    /// level's bins that pointed at the source move to the copy.
    /// </summary>
    public string SaveGfxAs(string name)
    {
        if (Rom is null || GfxPixels is not { } g) return "no GFX open";
        if (Project is null) return "no project open — File ▸ New Project first";
        g.EndStroke();
        if (Gfx.EditableBytes(Rom, g.File, out _) is not { } bytes) return $"GFX{g.File:X3} is empty";

        int id = 0x100;
        while (id <= 0xFFF && (Rom.ImportedGfx.ContainsKey(id) || Gfx.SourceSnes(Rom, id) >= 0)) id++;
        if (id > 0xFFF) return "save failed: no free ExGFX id (0x100-0xFFF all in use)";
        int from = g.File;
        Rom.ImportedGfx[id] = (byte[])bytes.Clone();   // its own array: edits must not alias the source
        if (Gfx.SourceSnes(Rom, from) >= 0)
            Rom.ImportedGfx.Remove(from);              // the stock file comes back for every other user
        Rom.ImportedGfxNames[id] = name.Trim().Length > 0 ? name.Trim() : DefaultGfxName(from);
        Gfx.InvalidateCache(Rom);
        g.Retarget(from, id);
        foreach (var bin in GfxBins)
            if (bin.File == from) SetGfxSlot(bin.BypWord, id);

        Save();
        return GfxName(id) is { Length: > 0 } n ? $"saved \"{n}\"" : $"saved GFX{id:X3}";
    }

    /// <summary>Rename an imported file. Stock files have no name to change — vanilla ships no
    /// label table, and inventing one would be guesswork.</summary>
    public bool RenameGfx(int id, string name)
    {
        if (Rom is null || !Rom.ImportedGfx.ContainsKey(id)) return false;
        Rom.ImportedGfxNames[id] = name.Trim();
        Project?.MarkDirty();
        return true;
    }

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
        // Inside a stroke the history entry is deferred to EndPaletteStroke, so a whole session
        // with the picker open is ONE undo rather than one per colour the drag passed through.
        // Putting a colour back to what the ROM has there REMOVES the edit rather than recording
        // one that happens to match: otherwise the swatch keeps its edited marker, the level
        // stays dirty, and a picker opened and closed on the same colour leaves a history entry
        // that changes nothing.
        ushort? after = bgr == RomPaletteBgr(index) ? null : bgr;
        if (stroke is { } s) s.TryAdd(index, Edited(index));
        else PushPalette([(index, Edited(index), after)]);
        if (after is { } v) paletteEdits[index] = v; else paletteEdits.Remove(index);
        Project?.MarkDirty();
        touched.Add(LevelNum);
        Recolour(livePhaseOnly: InPaletteStroke);
        return true;
    }

    /// <summary>
    /// Repaint for the current palette edits WITHOUT rebuilding the scene. A colour cannot have
    /// changed the level's objects, sprites or graphics, so re-parsing them is pure cost —
    /// see <see cref="LevelScene.Repalette"/>. This is what makes dragging a colour live.
    /// </summary>
    private void Recolour(bool livePhaseOnly = false)
    {
        if (Rom is null || Scene is null) return;
        Scene.Repalette(Rom, LevelNum, paletteEdits, livePhaseOnly ? LivePhase : null);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Which animation phase the canvas is showing. Mid-drag only that one is worth
    /// recomposing; the rest catch up when the stroke ends.</summary>
    public int LivePhase { get; set; }

    // ---- palette strokes ----
    // A drag through the colour picker fires many colour changes and must land as ONE undo, the
    // same bargain every other editor here makes with its stroke. Open/close of the picker is
    // the boundary, which is exactly where the ImGui editor snapshotted too.

    /// <summary>Index → the value it had BEFORE the stroke started (null = no edit).</summary>
    private Dictionary<int, ushort?>? stroke;

    public bool InPaletteStroke => stroke is not null;

    public void BeginPaletteStroke() => stroke ??= [];

    /// <summary>Close the stroke and record its net effect as one history entry. A stroke that
    /// ended back on the colour it started from records nothing.</summary>
    public void EndPaletteStroke()
    {
        if (stroke is not { } s) return;
        stroke = null;
        var entry = s.Where(kv => Edited(kv.Key) != kv.Value)
                     .Select(kv => (kv.Key, kv.Value, Edited(kv.Key)))
                     .ToArray();
        if (entry.Length == 0) return;
        PushPalette(entry);
        Recolour();                    // the phases the drag skipped catch up here
    }

    /// <summary>Drop every palette edit on this level and go back to the ROM's colours.</summary>
    public bool ResetPalette()
    {
        if (paletteEdits.Count == 0) return false;
        // Reset is one history entry, so it is undoable rather than a cliff.
        PushPalette([.. paletteEdits.Select(kv => (kv.Key, (ushort?)kv.Value, (ushort?)null))]);
        paletteEdits.Clear();
        Project?.MarkDirty();
        touched.Add(LevelNum);
        Recolour();
        return true;
    }

    // ---- palette history ----
    // Same shape as Map16Edit and GfxEdit: an array of (where, before, after) per entry, applied
    // forwards for redo and backwards for undo. A colour is a scalar write, so it fits that
    // model exactly. `null` means "no edit at this index" — undoing back past a colour's FIRST
    // edit has to REMOVE the entry, not write the ROM's own colour in as an edit, or the swatch
    // would keep its edited marker and the level would count as touched forever.

    private readonly Stack<(int Index, ushort? Before, ushort? After)[]> palUndo = new();
    private readonly Stack<(int Index, ushort? Before, ushort? After)[]> palRedo = new();

    public int PaletteUndoDepth => palUndo.Count;
    public bool CanUndoPalette => palUndo.Count > 0;
    public bool CanRedoPalette => palRedo.Count > 0;

    private ushort? Edited(int index) => paletteEdits.TryGetValue(index, out var v) ? v : null;

    /// <summary>What the ROM itself has at this CGRAM index — the level's colours with no editor
    /// edits on top, including any LM custom palette.</summary>
    private ushort RomPaletteBgr(int index)
        => Rom is { } r && Scene is { } s && index is >= 0 and < 256
            ? Palette.Load(r, s.Level.Header, LevelNum).Bgr[index]
            : (ushort)0;

    private void PushPalette((int Index, ushort? Before, ushort? After)[] entry)
    {
        palUndo.Push(entry);
        palRedo.Clear();
    }

    public bool PaletteUndo() => StepPalette(palUndo, palRedo, redo: false);
    public bool PaletteRedo() => StepPalette(palRedo, palUndo, redo: true);

    private bool StepPalette(Stack<(int Index, ushort? Before, ushort? After)[]> from,
                             Stack<(int Index, ushort? Before, ushort? After)[]> to, bool redo)
    {
        if (from.Count == 0) return false;
        var entry = from.Pop();
        foreach (var (i, before, after) in entry)
        {
            if ((redo ? after : before) is { } c) paletteEdits[i] = c;
            else paletteEdits.Remove(i);
        }
        to.Push(entry);
        Project?.MarkDirty();
        touched.Add(LevelNum);
        Recolour();
        return true;
    }

    /// <summary>
    /// The CGRAM index a composed pixel came from — the eyedropper. Goes through the Map16 tile
    /// rather than matching RGB alone: the same colour appears in several palette rows (black is
    /// in all of them), so "which entry is this tile actually using" is the only answer worth
    /// giving. The tile's quadrant word carries the palette row, and the colour is matched
    /// inside that row.
    ///
    /// Falls back to a search of all 256 when the pixel belongs to something drawn OVER the
    /// tile — a sprite, an overlay — whose row the layer-1 tile cannot know.
    /// </summary>
    public int? SampleCgramIndex(int px, int py)
    {
        if (Scene is not { } sc || sc.Palettes[0] is not { } pal) return null;
        if (px < 0 || py < 0 || px >= PxW || py >= sc.Height) return null;
        if (Phases.Length == 0 || Phases[0] is not { } pixels) return null;
        uint want = pixels[py * PxW + px];

        int tile = sc.Grid.Get(px / 16, py / 16);
        if (tile != Map16Grid.Empty && Map16?.ReadDef(tile) is { } def)
        {
            // Quadrants are stored in visual order: TL, TR, BL, BR.
            int quad = (py % 16 / 8) * 2 + (px % 16 / 8);
            int base16 = def[quad].Palette * 16;
            for (int c = 0; c < 16; c++)
                if (pal.Rgba[base16 + c] == want) return base16 + c;
        }
        for (int i = 0; i < 256; i++) if (pal.Rgba[i] == want) return i;
        return null;
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
            NewGfxEdit();
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
            // A missing or mismatched base is RECOVERABLE, not a failure: a .pdp is shareable on
            // its own, so this is the normal way someone else's project opens. Hold it and let
            // the caller ask for a ROM.
            if (p.ValidateBase() is { } bad)
            {
                pendingOpen = p;
                PendingBaseProblem = bad;
                Report($"{p.Name}: {bad}");
                return false;
            }
            // Bring an old base up to date before anything reads it, exactly as the ImGui
            // editor does on open — a stale base makes features refuse for invisible reasons.
            string? prepNote = p.PrepareBaseOnOpen(Config.VanillaRomPath);

            Rom = Rom.Load(p.BaseRomPath);
            RomPath = p.BaseRomPath;
            Project = p;
            touched.Clear();
            p.SyncBeforeSave = Sync;
            Config.TouchRecentProject(p.FilePath);
            Config.Save();

            NewGfxEdit();
            string? warn = ProjectSession.Hydrate(Rom, p.Data);
            ShowLevel(LevelNum);
            Report($"project '{p.Name}' opened" + (warn is null ? "" : " — " + warn)
                   + (prepNote is null ? "" : " — base not updated: " + prepNote));
            return true;
        }
        catch (Exception ex) { Report("could not open project: " + ex.Message); return false; }
    }

    /// <summary>Debug ▸ Clear project edits: wipe the .pdp back to its base-ROM pin and reopen
    /// it, so every level, Map16, GFX, palette and entrance edit is gone in one step. Returns
    /// false when no project is open.</summary>
    public bool ClearProjectEdits()
    {
        if (Project is not { } p) return false;
        p.ClearEdits();
        return OpenProject(p.FilePath);
    }

    public bool NewProject(string folder, string baseRomSource)
    {
        bool fresh = !Directory.Exists(folder);
        try
        {
            var p = Project.Create(folder, baseRomSource);
            return OpenProject(p.FilePath);
        }
        catch (Exception ex)
        {
            // A folder with base.smc and no project.pdp is a puzzle to find later, and blocks the
            // name for the retry (ProjectFolderFor steps to "-2"). Take back only what we made.
            if (fresh) try { Directory.Delete(folder, recursive: true); } catch { }
            Report($"could not create project: {ex.Message} ({ex.GetType().Name})");
            return false;
        }
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
            // before the compose, since the tile caches are built through them. The history goes
            // with them: undoing after a level switch would write these colours into the wrong
            // level's CGRAM.
            paletteEdits.Clear();
            palUndo.Clear();
            palRedo.Clear();
            stroke = null;                 // an open picker belongs to the level being left
            if (saved is not null)
                foreach (var (k, v) in saved.Palette) paletteEdits[k] = (ushort)v;

            // Built without sprites when the project has its own list, so the ROM's parse is
            // not composed in and then painted over.
            Scene = LevelScene.Build(Rom, num, SpriteMode(live is not null), paletteEdits);
            var objects = saved is not null
                ? saved.Objects.Select(o => o.ToLevelObject()).ToList()
                : [.. Scene.Level.Objects];
            if (live is not null) DrawSprites(live);

            Sprites = (live ?? Scene.Sprites) is { } sd
                ? new SpriteEdit(sd, Scene.Overlay, Vertical) { EntrySize = Rom.SpriteEntrySize } : null;

            layer1 = new LevelEdit(Rom, Scene, objects);
            // Always run the TRACKED render, as the ImGui editor does on every parse. It is
            // what gives each cell an owning object, and without it nothing on a freshly
            // opened level can be selected or hit-tested. It also puts a hydrated level's
            // pixels on screen from its OBJECT LIST rather than the base ROM's parsed grid;
            // for an unedited level the two are identical, and a render failure leaves the
            // parsed grid in place.
            layer1.Rerender();

            // Layer 2 is the same object stream format, so it gets its own editor on the same
            // class. The project's copy wins, and a project list on a background-image level IS
            // the conversion to object mode — the pointer's bank byte is the only mode flag
            // there is (CONTRACT §10), so there is nothing else to record.
            baseLayer2 = LevelParser.ParseLayer2(Rom, num);
            var l2 = saved?.Layer2Objects is { } pl2
                ? pl2.Select(o => o.ToLevelObject()).ToList()
                : baseLayer2 is not null ? new List<LevelObject>(baseLayer2) : null;
            layer2 = l2 is null ? null : new LevelEdit(Rom, Scene, l2, layer: 1);
            if (layer2 is not null)
            {
                // A converted level has no layer-2 grid yet: the scene was composed from a
                // background image, so give it one before the render replaces it.
                Scene.Layer2 ??= new Map16Grid(Scene.Grid.Width, Scene.Grid.Height);
                layer2.Rerender();
            }
            if (EditLayer == 1 && layer2 is null) EditLayer = 0;

            // Map16 definitions are per tileset, and the catalogs are rendered with this
            // level's own graphics — both belong to the level that was just loaded.
            NewMap16Edit();
            spriteCatalog = null;
            InvalidateLayer3Cells();
            OpenBackgroundEdits();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { Report($"level ${num:X3}: {ex.Message}"); }
    }

    // ---- layer 2 ----

    /// <summary>
    /// Switch which layer the canvas edits. Both layers' editors stay alive, so switching keeps
    /// each one's undo history and selection — unlike the ImGui editor, which had to clear the
    /// history because its undo closures captured whichever list was current.
    /// </summary>
    public string SetEditLayer(int layer)
    {
        if (layer == EditLayer) return "";
        if (layer == 1 && layer2 is null)
            return "layer 2 is a background image — use +L2 to give it an object layer";
        EditLayer = layer;
        Changed?.Invoke(this, EventArgs.Empty);
        string note = $"editing layer {layer + 1} ({Edit?.Objects.Count ?? 0} objects)";
        if (layer == 1 && !LevelModeReadsLayer2)
            note += $" — warning: level mode {Scene?.Level.Header.LevelMode:X2} never loads layer-2 objects";
        Report(note);
        return note;
    }

    /// <summary>
    /// Give a background-image level an empty layer-2 object stream, or drop the project's stream
    /// and go back to the base ROM's background.
    ///
    /// Only those two directions exist. Turning a level that SHIPS an object layer into a
    /// background one needs a background-image address to point at, which is what
    /// <see cref="SetLayer2Background"/> is for.
    /// </summary>
    public string SetLayer2ObjectMode(bool objectMode)
    {
        if (Rom is null || Scene is null) return "no level open";
        if (objectMode == (layer2 is not null)) return "";
        if (!objectMode && EditLayer == 1) EditLayer = 0;

        if (Project is not null)
        {
            // Persisted BEFORE the reparse: ShowLevel re-hydrates layer 2 from the project, so
            // setting the list here and reparsing after is what makes the change stick.
            StashCurrent();
            var st = Project.Data.Level(LevelNum);
            st.Layer2Objects = objectMode ? [] : null;
            // The two modes are exclusive and a background selection wins in the builder, so
            // converting to an object layer has to drop it.
            if (objectMode) st.Layer2Background = null;
            Project.MarkDirty();
            ReloadLevel();
        }
        else
        {
            // No project open: session-only, with nothing to hydrate from.
            layer2 = objectMode ? new LevelEdit(Rom, Scene, [], layer: 1) : null;
            if (objectMode)
            {
                Scene.Layer2 ??= new Map16Grid(Scene.Grid.Width, Scene.Grid.Height);
                layer2!.Rerender();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        string note = objectMode
            ? "layer 2 is now an editable object layer"
              + (LevelModeReadsLayer2 ? "" : " — the level mode never loads layer-2 objects, "
                                           + "so change the mode in Properties too")
            : "layer 2 restored to the base ROM's background image";
        Report(note);
        return note;
    }

    /// <summary>
    /// Point layer 2 at a background image, dropping any object stream — a level's layer 2 is one
    /// or the other.
    /// </summary>
    public string SetLayer2Background(int lo16)
    {
        if (Rom is null || Project is null) return "backgrounds can only be changed in a project";
        StashCurrent();
        var s = Project.Data.Level(LevelNum);
        s.Layer2Background = lo16 & 0xFFFF;
        s.Layer2Objects = null;
        // The session ROM has to agree with the project, or the canvas keeps showing the old
        // layer 2 until the next build.
        Rom.SetLayer2Pointer(LevelNum, 0xFF0000 | (lo16 & 0xFFFF));
        if (EditLayer == 1) EditLayer = 0;
        Project.MarkDirty();
        ReloadLevel();
        string note = $"layer 2 ← background ${lo16 & 0xFFFF:X4} (page {BgImage.PageFor(lo16)})";
        Report(note);
        return note;
    }

    /// <summary>
    /// The background images this ROM has, with the levels that share each one. Only addresses
    /// already in use are offered: a background's palette page comes from its ADDRESS, so
    /// pointing at an arbitrary one would recolour every tile in it, and bank $0C — where the
    /// loader looks — has a few dozen bytes free anyway.
    /// </summary>
    public IReadOnlyList<(int Lo16, int Page, IReadOnlyList<int> Levels)> Backgrounds()
        => Rom is null ? []
         : BgImage.Catalog(Rom).Select(c => (c.Lo16, c.Page, (IReadOnlyList<int>)c.Levels)).ToList();

    /// <summary>The background layer 2 currently points at, or null when it is an object layer.</summary>
    public int? CurrentBackground
        => Rom is { } r && r.Layer2IsBackground(LevelNum) ? r.Layer2Pointer(LevelNum) & 0xFFFF : null;

    /// <summary>How the composer should treat sprites: skip them entirely when an edited list
    /// will be drawn instead, else compose them unless the overlay is hidden — hidden still
    /// parses, because selection hit-tests against the overlay's pixel bounds.</summary>
    private LevelScene.SpriteDraw SpriteMode(bool haveEditedList)
        => haveEditedList ? LevelScene.SpriteDraw.Skip
         : showSprites ? LevelScene.SpriteDraw.Compose
         : LevelScene.SpriteDraw.ParseOnly;

    /// <summary>Capture a sprite list's OAM and draw it over every phase of the current scene.
    /// The capture is expensive, which is why it happens once per list change rather than per
    /// repaint — and it happens even when the overlay is hidden, since it is the hit target.</summary>
    private void DrawSprites(SpriteData sprites)
    {
        if (Rom is null || Scene is null) return;
        var overlay = SpriteOverlay.Build(Rom, sprites, Scene.Level.Header, LevelNum);
        Scene.Overlay = overlay;
        if (Sprites is not null) Sprites.Overlay = overlay;
        if (showSprites) Scene.RedrawOverlay();
    }

    /// <summary>One GFX pixel editor per ROM — the bytes are ROM-wide, so unlike Map16 defs it
    /// outlives a level switch. A committed stroke changes what every level draws with, hence the
    /// full recompose.</summary>
    private void NewGfxEdit()
    {
        if (Rom is null) return;
        GfxPixels = new GfxEdit(Rom);
        GfxPixels.Committed += (_, _) =>
        {
            Project?.MarkDirty();
            RecomposeScene();
        };
    }

    /// <summary>One Map16 editor per level, re-raising its commits under this class's event so
    /// the UI subscribes once instead of re-subscribing to a new object each level.</summary>
    private void NewMap16Edit()
    {
        if (Rom is null || Scene is null) return;
        Map16 = new Map16Edit(Rom, Scene.Level.Header.Tileset, Project);
        Map16.Committed += () => Map16Committed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Delete on a Map16 selection: put the tiles back to the base ROM's definitions.
    /// The pristine base is its on-disk copy — the session ROM is that plus the hydrated edits —
    /// so this needs a project. Undoable as one stroke, like any other Map16 edit.</summary>
    public bool ResetMap16Tiles(IEnumerable<int> tiles)
    {
        if (Map16 is not { } m) return false;
        if (Project is not { } p) { Report("no project — nothing to reset to"); return false; }
        m.Reset(tiles, Rom.Load(p.BaseRomPath));
        return true;
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
        // The GFX itself may be what changed here (a repointed bin, an edited pixel), so the
        // cached graphics have to go before anything recomposes from them.
        Scene?.InvalidateGfx();
        InvalidateLayer3Cells();
        Rebuild("recompose");
    }

    /// <summary>
    /// Repaint after a committed Map16 edit, touching only the tiles it actually changed.
    /// A definition edit used to cost a full scene rebuild — a quarter of a second before the
    /// stamped tile appeared — for a change to 256 pixels of artwork.
    ///
    /// Falls back to the full in-place recompose when the editor cannot say which tiles moved
    /// (undo and redo replay byte offsets, not tiles).
    /// </summary>
    public void RecomposeAfterMap16()
    {
        if (Rom is null || Scene is null) return;
        spriteCatalog = null;
        objectCatalog = null;
        if (Map16?.CommittedTiles is { } tiles) Scene.RecomposeTiles(Rom, LevelNum, tiles, paletteEdits);
        else Recolour();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Redraw the level after a sprite edit: a changed sprite leaves its old pixels behind, so
    /// the cells under every OLD sprite are recomposed and the overlay rebuilt from the edited
    /// list. A sprite edit cannot change the terrain, so the full scene rebuild this used to do
    /// — parse, objects, four composed phases — was a quarter second of pure cost per edit.
    /// </summary>
    public void RefreshSprites()
    {
        if (Rom is null || Scene is null || Sprites is not { } sp)
        { Rebuild("sprite recompose"); return; }
        if (showSprites && Scene.Overlay is { } old)
            foreach (var (x0, y0, x1, y1) in old.DrawnRects())
                for (int cy = y0 >> 4; cy <= (y1 - 1) >> 4; cy++)
                    for (int cx = x0 >> 4; cx <= (x1 - 1) >> 4; cx++)
                        Scene.RecomposeCell(cx, cy);
        DrawSprites(sp.Sprites);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One live sprite-drag step: recompose only the cells under the moved sprites' OLD pixels,
    /// shift their cached OAM, and re-blit the overlay. Routing a drag through RefreshSprites
    /// rebuilt the entire scene — parse, objects, four composed phases, a 65816 capture per
    /// sprite — per cell crossed, which made dragging a slideshow; a move changes nothing but
    /// where the overlay draws.
    /// </summary>
    public void MoveSprites(int dxCells, int dyCells)
    {
        if (Scene is not { Overlay: { } old } scene || Sprites is not { } sp)
        { RefreshSprites(); return; }

        if (showSprites)
            foreach (int i in sp.Selection)
            {
                if (i < 0 || i >= sp.Sprites.Sprites.Count) continue;
                // Bounds come from the PRE-move overlay (the records have already moved).
                // Badge-only sprites (null bounds) drew one cell at the spawn cell, whose old
                // position is the record's cell minus this step.
                var (x0, y0, x1, y1) = old.PixelBounds(i) is { } b
                    ? (b.MinX, b.MinY, b.MaxX, b.MaxY)
                    : OldBadgeRect(sp.Sprites.Sprites[i]);
                for (int cy = y0 >> 4; cy <= (y1 - 1) >> 4; cy++)
                    for (int cx = x0 >> 4; cx <= (x1 - 1) >> 4; cx++)
                        scene.RecomposeCell(cx, cy);
            }

        var moved = old.Moved(sp.Selection, dxCells * 16, dyCells * 16, sp.Sprites);
        scene.Overlay = moved;
        sp.Overlay = moved;
        if (showSprites) scene.RedrawOverlay();

        (int, int, int, int) OldBadgeRect(Sprite s)
        {
            var (cx, cy) = s.Cell(Vertical);
            int px = (cx - dxCells) * 16, py = (cy - dyCells) * 16;
            return (px, py, px + 16, py + 16);
        }
    }

    /// <summary>
    /// Recompose the level from the ROM and re-render the object list over it, keeping the
    /// sprite list being edited. Built without sprites whenever there IS an edited list, since
    /// LevelScene.Build would otherwise compose the ROM's own parse underneath it.
    /// </summary>
    private void Rebuild(string what)
    {
        if (Rom is null || layer1 is null) return;
        // Both layers' streams are carried across: a recompose is about the PIXELS, and losing
        // layer 2's objects because a Map16 definition changed would be a silent data loss.
        var objects = layer1.Objects.ToList();
        var l2 = layer2?.Objects.ToList();
        try
        {
            Scene = LevelScene.Build(Rom, LevelNum, SpriteMode(Sprites is not null), paletteEdits);
            if (Sprites is { } sp) DrawSprites(sp.Sprites);
            layer1 = new LevelEdit(Rom, Scene, objects);
            layer1.Rerender();
            if (l2 is not null)
            {
                Scene.Layer2 ??= new Map16Grid(Scene.Grid.Width, Scene.Grid.Height);
                layer2 = new LevelEdit(Rom, Scene, l2, layer: 1);
                layer2.Rerender();
            }
            else layer2 = null;
            Changed?.Invoke(this, EventArgs.Empty);
            SceneRebuilt?.Invoke(this, EventArgs.Empty);
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
    // ---- ExAnimation (reference/EXANIMATION.md) ----

    /// <summary>The current level's slots, or the global list's, as the ROM has them.</summary>
    public IReadOnlyList<ExAnimation.Slot> ExAnimSlots(bool global)
        => Rom is null ? [] : global ? ExAnimation.ReadGlobal(Rom) : ExAnimation.ReadLevel(Rom, LevelNum);

    /// <summary>
    /// One frame of a tile slot as pixels, in the slot's shape (a line of N tiles, or the
    /// stacked / 16x16 / 32x16 block), coloured with the level's palette row. The source is
    /// whatever the frame word names: a byte offset into the list's alternate file, or a $7E
    /// address in AN1 ($7D00, GFX33), AN2 ($AD00, the level's bypass file) or Mario's sheet
    /// ($2000, GFX32). Empty when the source is not loaded (no AN2 file, no alt file yet).
    /// </summary>
    public (uint[] Px, int W, int H) ExAnimFramePixels(ExAnimation.Slot s, int frame, int palRow)
    {
        if (Rom is null || Scene?.Palettes[0] is not { } pal || s.IsPalette || s.TileCount == 0 || frame >= s.Frames.Length)
            return ([], 0, 0);
        int word = s.Frames[frame];
        byte[]? src; int off, bpp;
        if (s.AltFile)
        {
            src = Gfx.Cached(Rom, 0x60 + s.AltFileIndex); off = word; bpp = s.Type == ExAnimation.Type2bpp ? 2 : 4;
        }
        else if (word >= 0xAD00)
        {
            int an2 = GfxBins.FirstOrDefault(b => b.Name == "AN2").File;
            src = an2 is 0 or 0x7F ? null : Gfx.Cached(Rom, an2); bpp = an2 is 0 or 0x7F ? 4 : Gfx.FileBpp(Rom, an2);
            off = (word - 0xAD00) / 0x20 * Gfx.TileBytes(bpp);
        }
        else if (word >= 0x7D00)
        {
            src = Gfx.Cached(Rom, 0x33); bpp = Gfx.FileBpp(Rom, 0x33); off = (word - 0x7D00) / 0x20 * Gfx.TileBytes(bpp);
        }
        else
        {
            src = Gfx.Cached(Rom, 0x32); bpp = 4; off = (word - 0x2000) / 0x20 * 0x20;
        }
        if (src is null) return ([], 0, 0);

        int cols = s.Type switch { ExAnimation.TypeStacked => 1, ExAnimation.Type16x16 => 2, ExAnimation.Type32x16 => 4, _ => s.TileCount };
        int rows = (s.TileCount + cols - 1) / cols;
        int w = cols * 8, h = rows * 8, tb = Gfx.TileBytes(bpp), baseColor = (palRow & 0x0F) * 16;
        var px = new uint[w * h];
        for (int k = 0; k < s.TileCount; k++)
        {
            if (off + (k + 1) * tb > src.Length) break;
            var tile = Gfx.DecodeTile(src, off + k * tb, bpp);
            int ox = (k % cols) * 8, oy = (k / cols) * 8;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int idx = tile[y * 8 + x];
                    px[(oy + y) * w + ox + x] = idx == 0 ? 0xFF303030u : pal.Rgba[baseColor + idx];
                }
        }
        return (px, w, h);
    }

    /// <summary>Add a working slot with nothing decided yet — the next free slot number, one 8x8,
    /// no trigger, one frame of AN1 tile 600, destination tile 000 — so the decisions can be made
    /// on the timeline afterwards. Null when the list is full or the base cannot hold it.</summary>
    public ExAnimation.Slot? AddExAnimSlot(bool global)
    {
        var have = ExAnimSlots(global);
        int index = Enumerable.Range(0, 0x20).FirstOrDefault(i => have.All(s => s.Index != i), -1);
        if (index < 0) { Report("all 32 slots of this list are used"); return null; }
        var slot = new ExAnimation.Slot(index, 1, ExAnimation.TriggerNone, 1, 0x0000, [0x7D00], ExAnimAltFile(global));
        return SetExAnimSlot(global, slot) ? slot : null;
    }

    /// <summary>Move a slot to a free slot number, keeping everything else about it.</summary>
    public bool ReassignExAnimSlot(bool global, int from, int to)
    {
        var list = ExAnimSlots(global).ToList();
        int i = list.FindIndex(s => s.Index == from);
        if (i < 0 || from == to || to is < 0 or >= 0x20) return false;
        if (list.Any(s => s.Index == to)) { Report($"slot {to:X2} is already used"); return false; }
        list[i] = list[i] with { Index = to };
        return SetExAnim(global, list, ExAnimAltFile(global));
    }

    /// <summary>Replace (or add) one slot in a list, keeping the list's source file.</summary>
    public bool SetExAnimSlot(bool global, ExAnimation.Slot slot)
    {
        var list = ExAnimSlots(global).Where(x => x.Index != slot.Index).ToList();
        list.Add(slot);
        return SetExAnim(global, list, ExAnimAltFile(global));
    }

    /// <summary>What currently sits at a tile slot's destination in the level's VRAM, in the slot's
    /// shape — the thing the animation will overwrite. Empty for palette slots.</summary>
    public (uint[] Px, int W, int H) ExAnimDestPixels(ExAnimation.Slot s, int palRow)
    {
        if (Rom is null || Scene?.Palettes[0] is not { } pal || s.IsPalette || s.TileCount == 0) return ([], 0, 0);
        var fg = Scene.Fg(Rom, LevelNum, 0);
        int cols = s.Type switch { ExAnimation.TypeStacked => 1, ExAnimation.Type16x16 => 2, ExAnimation.Type32x16 => 4, _ => s.TileCount };
        int rows = (s.TileCount + cols - 1) / cols, w = cols * 8, h = rows * 8, baseColor = (palRow & 0x0F) * 16;
        var px = new uint[w * h];
        byte[][]? sp = null;        // loaded on first sprite-range dest tile
        byte[]?[]? l3 = null;       // and likewise the layer-3 window
        for (int k = 0; k < s.TileCount; k++)
        {
            int tile = s.DestTileAt(k);
            byte[]? t;
            if (tile is >= 0x400 and < 0x600)
                t = (sp ??= SpriteRender.LoadSpTiles(Rom, Scene.Level.Header, LevelNum))[tile - 0x400];
            else if (tile is >= 0x1C00 and < 0x1C00 + Layer3.TileCount)
                t = (l3 ??= Layer3.Tiles(Rom, LevelNum))[tile - 0x1C00];
            else if (tile is < 0 or >= 0x400) continue;
            else t = fg.Fetch(tile);
            if (t is null) continue;
            int ox = (k % cols) * 8, oy = (k / cols) * 8;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int idx = t[y * 8 + x];
                    px[(oy + y) * w + ox + x] = idx == 0 ? 0xFF303030u : pal.Rgba[baseColor + idx];
                }
        }
        return (px, w, h);
    }

    /// <summary>Which of files 60-63 a list reads (its record header), 0 when it has no record.</summary>
    public int ExAnimAltFile(bool global)
    {
        if (Rom is null) return 0;
        int ptr = global ? Rom.LmGlobalExAnimPtr : Rom.LmExAnimBase < 0 ? -1 : Rom.ReadValue(Rom.LmExAnimBase + LevelNum * 3, 3);
        return ptr > 0xFFFF ? Rom.ReadByte(ptr + 1) & 3 : 0;
    }

    /// <summary>Replace the level's (or the global) slot list: written to the session ROM so the
    /// canvas animates it, recorded in the project as the encoded record, and the graphics
    /// recomposed. False with a report when the base cannot hold it.</summary>
    public bool SetExAnim(bool global, IReadOnlyList<ExAnimation.Slot> slots, int altFileIndex)
    {
        if (Rom is null) return false;
        string? err = global ? Rom.WriteGlobalExAnim(slots, altFileIndex) : Rom.WriteLevelExAnim(LevelNum, slots, altFileIndex);
        if (err is not null) { Report(err); return false; }
        if (Project is not null)
        {
            string? hex = slots.Count == 0 ? null : Convert.ToHexString(ExAnimation.Encode(slots, altFileIndex));
            if (global) Project.Data.ExAnimation.Global = hex;
            else if (hex is null) Project.Data.ExAnimation.Levels.Remove(LevelNum.ToString("X3"));
            else Project.Data.ExAnimation.Levels[LevelNum.ToString("X3")] = hex;
            Project.MarkDirty();
        }
        Scene?.InvalidateGfx();
        Rebuild("exanimation");
        return true;
    }

    /// <summary>Install raw 4bpp tile data as ExAnimation source file 60+<paramref name="index"/>
    /// (≤ 32KB): into the session ROM for the overlay, and into the project under its id.</summary>
    /// <summary>The same from a file on disk (a raw 4bpp .bin, as LM's ExGraphics/ExGFX6x.bin).</summary>
    public bool ImportExAnimSource(int index, string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Report($"could not read {path}: {e.Message}"); return false; }
        return SetExAnimSource(index, data);
    }

    public bool SetExAnimSource(int index, byte[] data)
    {
        if (Rom is null || Rom.LmExAnimBase < 0) { Report("this base has no ExAnimation engine — File → Upgrade base"); return false; }
        if (data.Length is 0 or > 0x8000) { Report("an ExAnimation source file is 1..32768 bytes"); return false; }
        Rom.SetLmAltExGfx(index, data);
        Rom.ImportedGfx[0x60 + index] = data;
        Project?.MarkDirty();
        Scene?.InvalidateGfx();
        Rebuild("exanimation source");
        return true;
    }

    public void ApplyEntry(MainEntrance entry)
    {
        if (Rom is null) return;
        var had = Rom.ReadMainEntrance(LevelNum);
        if (had == entry) return;
        // LM's level height trades width for height: W columns of LUT[H] bytes must fit the
        // 0x3800-byte tilemap, or the engine writes past RAM. Refuse rather than build a crash.
        if (entry.HeightIndex != had.HeightIndex && Header is { } hdr)
        {
            int px = Rom.HasLmLevelHeight
                ? Rom.ReadValue(Rom.LmLevelHeightTable + 0x200 + entry.HeightIndex * 2, 2) : 0x1B0;
            if (hdr.Screens * px > 0x3800)
            {
                Report($"{hdr.Screens} screens x {px:X} px does not fit the tilemap (max 0x3800) — height not changed");
                return;
            }
        }
        Rom.WriteMainEntrance(LevelNum, entry);
        if (Project is not null)
        {
            Project.Data.Level(LevelNum).MainEntrance = Convert.ToHexString(entry.ToBytes());
            Project.MarkDirty();
        }
        touched.Add(LevelNum);
        // A new height is a new canvas: the engine sizes its grid to it, so reparse like a header.
        if (entry.HeightIndex != had.HeightIndex) { StashCurrent(); ShowLevel(LevelNum); }
    }

    public bool HasHeaderOverride => Rom?.LevelHeaderOverrides.ContainsKey(LevelNum) == true;

    // ---- secondary entrances ----
    // The destination side of a secondary screen exit. There are 512, they are GLOBAL (any
    // level's exit may point at any index), and like Map16 definitions they are written straight
    // into the session ROM with the index recorded in the project — the bytes are re-read at save
    // time, so nothing has to be carried in the level state.

    public static int SecondaryEntranceCount => Rom.SecondaryEntranceCount;

    public SecondaryEntrance? ReadEntrance(int index)
        => Rom is { } r && index >= 0 && index < Rom.SecondaryEntranceCount
            ? r.ReadSecondaryEntrance(index) : null;

    /// <summary>
    /// Every entrance that lands in THIS level, as positions on the canvas: the main entrance,
    /// the midway one, and every secondary record pointing here.
    ///
    /// "Pointing here" is the low byte only on a vanilla base — a record's destination is 8 bits
    /// and its ninth comes from the submap the player crossed ($05F800's own doc), so $005 and
    /// $105 share a set. With Lunar Magic's secondary routine in, bit 3 of $05FE00 is that ninth
    /// bit and the match is exact.
    /// </summary>
    public IReadOnlyList<LevelEntrance> Entrances()
    {
        if (Rom is not { } rom || !HasLevel || MainEntrance is not { } main) return [];
        // Method 2 (LM's, prep v10's) reinterprets the record's two index nibbles as 16px steps;
        // otherwise they index vanilla's tables. Same record either way — the flag decides.
        var mainAt = main.Method2 != 0
            ? (EntrancePlacement.Method2X(main.ReservedMode, main.MarioX, main.XHigh),
               EntrancePlacement.Method2Y(main.MarioY, main.YHigh))
            : (EntrancePlacement.X(rom, main.ReservedMode, main.MarioX),
               EntrancePlacement.Y(rom, main.MarioY));
        // The midway carries only a screen and borrows the main's spot inside it — unless LM's
        // separate midway settings are on for this level, which give it a 16px position of its own.
        int midScreen = main.ReservedBoundary | (main.MidwayScreenHigh << 4);
        var midAt = main.MidwaySeparate != 0
            ? ((midScreen << 8) | (main.MidwayX << 4),                    // one nibble = X bits 4-7
               EntrancePlacement.Method2Y(main.MidwayY, main.MidwayYHigh))
            : main.Method2 != 0
            ? (EntrancePlacement.Method2X(midScreen, main.MarioX, main.XHigh), mainAt.Item2)
            : (EntrancePlacement.X(rom, midScreen, main.MarioX), mainAt.Item2);
        var list = new List<LevelEntrance>
        {
            new(EntranceKind.Main, LevelNum, mainAt.Item1, mainAt.Item2) { Free = rom.HasFreeEntrancePositions },
            new(EntranceKind.Midway, LevelNum, midAt.Item1, midAt.Item2) { Free = rom.HasFreeMidwayPosition },
        };
        bool secFree = rom.HasFreeSecondaryPositions;
        for (int i = 0; i < Rom.SecondaryEntranceCount; i++)
        {
            var e = rom.ReadSecondaryEntrance(i);
            if (e.DestinationLevel != (LevelNum & 0xFF)) continue;
            if (secFree && e.DestinationHigh != (LevelNum >> 8)) continue;
            var at = e.Method2 != 0
                ? (EntrancePlacement.Method2X(e.ReservedX, e.MarioX, e.XHigh), EntrancePlacement.Method2Y(e.MarioY, e.YHigh))
                : (EntrancePlacement.X(rom, e.ReservedX, e.MarioX), EntrancePlacement.Y(rom, e.MarioY));
            list.Add(new LevelEntrance(EntranceKind.Secondary, i, at.Item1, at.Item2) { Free = secFree });
        }
        return list;
    }

    /// <summary>
    /// Move an entrance to the nearest position the ROM can express: a 16px step with method 2,
    /// one of vanilla's 8 x 16 table spots without. Returns false when nothing changed —
    /// including a midway dragged within its own screen, which has nowhere to store the move
    /// (see <see cref="LevelEntrance.ScreenOnly"/>).
    /// </summary>
    public bool MoveEntrance(EntranceKind kind, int index, int px, int py)
    {
        if (Rom is not { } rom) return false;

        if (kind == EntranceKind.Secondary)
        {
            if (ReadEntrance(index) is not { } e) return false;
            if (rom.HasFreeSecondaryPositions)
            {
                var f = EntrancePlacement.Method2Fields(px, py);
                return WriteEntrance(index, e with { Method2 = 1, ReservedX = f.Screen, MarioX = f.XIndex,
                                                     XHigh = f.XHigh, MarioY = f.YIndex, YHigh = f.YHigh });
            }
            var (sScreen, sX) = EntrancePlacement.NearestX(rom, px);
            return WriteEntrance(index, e with { ReservedX = sScreen, MarioX = sX, MarioY = EntrancePlacement.NearestY(rom, py) });
        }

        if (MainEntrance is not { } main) return false;
        MainEntrance moved;
        if (kind == EntranceKind.Midway && rom.HasFreeMidwayPosition)
        {
            var f = EntrancePlacement.Method2Fields(px, py);
            // First opt-in: the separate record starts as a copy of what the midway had been
            // using — the main's action and FG/BG settings — so only the position changes.
            // MidwayYHigh bit 6 is what LM writes on every separate record; kept for parity.
            bool first = main.MidwaySeparate == 0;
            moved = main with
            {
                MidwaySeparate = 1, ReservedBoundary = f.Screen & 0x0F, MidwayScreenHigh = f.Screen >> 4,
                MidwayX = (px >> 4) & 0x0F, MidwayY = f.YIndex,
                MidwayYHigh = 0x40 | f.YHigh,
                MidwayAction = first ? main.EntranceAction : main.MidwayAction,
                MidwayFgBg = first ? main.VerticalScroll | (main.ScreenBoundaryY << 2) : main.MidwayFgBg,
            };
        }
        else if (kind == EntranceKind.Midway)
        {
            // A screen is all the midway record holds without LM's separate settings.
            moved = main with { ReservedBoundary = Math.Clamp(px, 0, 0x0FFF) >> 8 };
            if (moved == main)
            {
                Report("the midway entrance moves a screen at a time; it shares the main entrance's spot");
                return false;
            }
        }
        else if (rom.HasFreeEntrancePositions)
        {
            var f = EntrancePlacement.Method2Fields(px, py);
            moved = main with { Method2 = 1, ReservedMode = f.Screen, MarioX = f.XIndex, XHigh = f.XHigh,
                                MarioY = f.YIndex, YHigh = f.YHigh };
        }
        else
        {
            var (screen, xIndex) = EntrancePlacement.NearestX(rom, px);
            moved = main with { ReservedMode = screen, MarioX = xIndex, MarioY = EntrancePlacement.NearestY(rom, py) };
        }
        if (moved == main) return false;
        ApplyEntry(moved);
        return true;
    }

    /// <summary>Write one secondary entrance. Returns false when it already said that.</summary>
    public bool WriteEntrance(int index, SecondaryEntrance entrance)
    {
        if (Rom is not { } r || index < 0 || index >= Rom.SecondaryEntranceCount) return false;
        if (r.ReadSecondaryEntrance(index) == entrance) return false;
        r.WriteSecondaryEntrance(index, entrance);
        Project?.Data.Entrances.TryAdd(index.ToString("X3"), "");   // captured; bytes re-read at save
        Project?.MarkDirty();
        return true;
    }

    // ---- course bot ----
    // Named handles on level slots, so courses are organized by name instead of number. An
    // entry is an ordinary project level whose slot was auto-picked and seeded by copying a
    // base level; only the name (ProjectFile.CourseBot) is new state.

    /// <summary>Overworld-enterable level slots — the pool Course Bot assigns from and
    /// offers as bases.</summary>
    public static IEnumerable<int> EnterableLevels()
    {
        for (int l = 0x001; l <= 0x024; l++) yield return l;
        for (int l = 0x101; l <= 0x13B; l++) yield return l;
    }

    /// <summary>Course Bot entries, sorted by name.</summary>
    public IReadOnlyList<(int Level, string Name)> CourseBotEntries =>
        Project is null ? []
        : Project.Data.CourseBot
            .Select(kv => (Level: Convert.ToInt32(kv.Key, 16), kv.Value))
            .OrderBy(e => e.Value, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Level)
            .ToList();

    /// <summary>The course name a level slot carries, or null.</summary>
    public string? CourseBotName(int level) =>
        Project?.Data.CourseBot.GetValueOrDefault(level.ToString("X3"));

    /// <summary>
    /// Whether a slot can take a new course. A project entry does not by itself mean "used":
    /// every save stashes whichever level is being shown, so a merely-visited level carries an
    /// entry identical to its base ROM parse — that slot is still free.
    /// </summary>
    private bool SlotIsFree(int level)
    {
        var data = Project!.Data;
        if (data.CourseBot.ContainsKey(level.ToString("X3"))) return false;
        if (data.LevelOrNull(level) is not { } s) return true;
        if (s.Header is not null || s.MainEntrance is not null || s.Layer2Objects is not null
            || s.Layer2Background is not null || s.Palette.Count > 0 || s.GfxOverrides.Count > 0)
            return false;
        if (!s.Objects.Select(o => o.ToLevelObject())
                      .SequenceEqual(LevelParser.Parse(Rom!, level).Objects)) return false;
        var sd = SpriteData.Parse(Rom!, level);
        // Sprite.ExtraBytes is an array, so records carrying them never compare equal — that
        // only ever calls a free slot "used", never the reverse.
        return s.SpriteMemory == sd.SpriteMemory && s.Buoyancy == sd.Buoyancy
            && s.Sprites.Select(x => x.ToSprite()).SequenceEqual(sd.Sprites);
    }

    /// <summary>
    /// Create a Course Bot level: the first free enterable slot, seeded with a FULL copy of
    /// <paramref name="baseLevel"/> — header, main entrance, both layers, sprites, palette and
    /// GFX bins — so the slot's build output is determined entirely by its project entry.
    /// Returns the new slot, or -1 with the reason in Status.
    /// </summary>
    public int CreateCourseBotLevel(string name, int baseLevel)
    {
        if (Project is null || Rom is null) { Report("no project open"); return -1; }
        name = name.Trim();
        if (name.Length == 0) { Report("a course needs a name"); return -1; }
        StashCurrent();                       // a copy of the shown level must be fresh
        // The shown level is never the slot: its next stash would overwrite the copy with
        // whatever is on screen.
        int slot = EnterableLevels().Where(l => l != LevelNum && SlotIsFree(l))
                                    .DefaultIfEmpty(-1).First();
        if (slot < 0) { Report("no free enterable level slot left"); return -1; }

        var data = Project.Data;
        // Start from the base's project entry when it has one (its object/sprite edits live
        // only there), then fill the rest from the session ROM, whose reads already merge the
        // session edits (header overrides, replayed entrance tables).
        var state = data.LevelOrNull(baseLevel)?.Clone() ?? new ProjectFile.LevelState();
        var parsed = LevelParser.Parse(Rom, baseLevel);
        state.Header = Convert.ToHexString(parsed.Header.ToBytes());
        if (data.LevelOrNull(baseLevel) is null)
            state.Objects = parsed.Objects.Select(ProjectFile.ObjectDto.From).ToList();
        // Layer 2 is recorded EXPLICITLY either way: null in the base's entry means "keep the
        // base ROM's layer 2", and the new slot's own base layer 2 is a different one.
        if (state.Layer2Objects is null && state.Layer2Background is null)
        {
            if (Rom.Layer2IsBackground(baseLevel))
                state.Layer2Background = Rom.Layer2Pointer(baseLevel) & 0xFFFF;
            else
                state.Layer2Objects = LevelParser.ParseLayer2(Rom, baseLevel)!
                    .Select(ProjectFile.ObjectDto.From).ToList();
        }
        if (state.Sprites.Count == 0 && state.SpriteMemory == 0 && state.Buoyancy == 0)
        {
            // ponytail: all-defaults reads as "never stashed"; a base deliberately emptied of
            // sprites AT memory setting 0 copies the ROM's list instead — harmless to re-delete.
            var sd = SpriteData.Parse(Rom, baseLevel);
            state.SpriteMemory = sd.SpriteMemory;
            state.Buoyancy = sd.Buoyancy;
            state.Sprites = sd.Sprites.Select(ProjectFile.SpriteDto.From).ToList();
        }
        state.MainEntrance = Convert.ToHexString(Rom.ReadMainEntrance(baseLevel).ToBytes());
        state.GfxOverrides = Rom.GfxSlotOverrides.Where(kv => kv.Key.Level == baseLevel)
                                .ToDictionary(kv => kv.Key.Word, kv => kv.Value);

        string key = slot.ToString("X3");
        data.Levels[key] = state;
        data.CourseBot[key] = name;

        // Seed the session ROM the way Hydrate would on reopen, so the slot shows the copy
        // right away — and so the save-time entrance re-read (ProjectCapture) captures the
        // copied bytes rather than the slot's base ones.
        Rom.LevelHeaderOverrides[slot] = Convert.FromHexString(state.Header);
        foreach (var (word, file) in state.GfxOverrides) Rom.GfxSlotOverrides[(slot, word)] = file;
        Rom.WriteMainEntrance(slot, new MainEntrance(Convert.FromHexString(state.MainEntrance)));
        if (state.Layer2Background is { } bg) Rom.SetLayer2Pointer(slot, 0xFF0000 | bg);

        Project.MarkDirty();
        Report($"course \"{name}\" created in level ${slot:X3} from ${baseLevel:X3}");
        return slot;
    }

    /// <summary>
    /// Delete a Course Bot level: the name goes and the slot's project entry goes with it, so
    /// the slot reverts to the base ROM. The per-slot bytes create wrote into the session ROM
    /// (entrance table, layer-2 pointer) are restored from the base copy — a build replays
    /// onto a fresh base anyway, this just keeps what is on screen honest.
    /// </summary>
    public string DeleteCourseBotLevel(int level)
    {
        if (Project is null || Rom is null) { Report("no project open"); return Status; }
        string key = level.ToString("X3");
        if (!Project.Data.CourseBot.Remove(key))
        {
            Report($"${level:X3} is not a Course Bot level");
            return Status;
        }
        Project.Data.Levels.Remove(key);
        Rom.LevelHeaderOverrides.Remove(level);
        foreach (var k in Rom.GfxSlotOverrides.Keys.Where(k => k.Level == level).ToArray())
            Rom.GfxSlotOverrides.Remove(k);
        var baseRom = Rom.Load(Project.BaseRomPath);
        Rom.WriteMainEntrance(level, baseRom.ReadMainEntrance(level));
        Rom.SetLayer2Pointer(level, baseRom.Layer2Pointer(level));
        Project.MarkDirty();
        touched.Remove(level);
        // Same number, so ShowLevel does not stash the dying state on the way out.
        if (level == LevelNum) ShowLevel(level);
        Report($"course level ${level:X3} deleted — slot reverted to the base ROM");
        return Status;
    }

    // ---- saving ----

    /// <summary>Fold the live level into the project snapshot. Called before every write, and
    /// wired to Project.SyncBeforeSave so an autosave cannot miss the current level.</summary>
    private void Sync()
    {
        if (Project is null || Rom is null) return;
        StashCurrent();
        LevelEditState.StashRomWide(Project.Data, Rom, Scene?.Level.Header.Tileset ?? 1);
    }

    /// <summary>
    /// Fold the live level into the project snapshot. Assembled HERE rather than by the object
    /// editor, because it spans things no single editor owns: both layers' streams, the base
    /// layer-2 stream to diff against, the sprite list and the palette edits.
    /// </summary>
    private void StashCurrent()
    {
        if (Project is null || Rom is null || layer1 is null) return;
        // A level entry is created by KEY, so a nonsense level number silently poisons the
        // project file: nothing reads it back until a build tries to parse it as a level and
        // dies. Guarding the one place every stash goes through makes that unrepresentable.
        if (LevelNum < 0 || LevelNum >= Rom.LevelCount) return;
        var state = new LevelEditState
        {
            Layer1 = [.. layer1.Objects],
            // Recorded only when it DIFFERS from the base, or every touched level would pin its
            // unedited layer 2 into the project. A null base with a non-null live list is what
            // marks the background→objects conversion, so both are reported honestly.
            Layer2 = layer2 is { } l2 ? [.. l2.Objects] : null,
            BaseLayer2 = baseLayer2,
            Sprites = Sprites?.Sprites ?? Scene?.Sprites,   // the live list, not the ROM's parse
        };
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
        // Everything that meant "the project snapshot does not have this yet" is now on disk,
        // so the live editors stop claiming it. Without this the title kept its unsaved marker
        // for the rest of the session, which reads as "the save did not happen".
        if (GfxPixels is { } g) g.Dirty = false;
        if (Edit is { } le) le.Dirty = false;
        if (Sprites is { } se) se.Dirty = false;
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

    /// <summary>
    /// Dev only: build the project and open the result in Lunar Magic. Prep's stated
    /// requirement is that what we stamp is what LM reads (CONTRACT §0 tracks the divergences),
    /// and the loop for checking that was "build, find the file, drag it onto LM" — this is the
    /// same thing in one click. Returns a problem, or null when LM was launched.
    ///
    /// The path comes from the reference-ROM root (PIPEDREAM_SMW_ROOT), which is where the
    /// gated tests already expect Lunar Magic to live — no new setting to keep in sync.
    /// </summary>
    public string? OpenInLunarMagic()
    {
        if (Project is null) return "no project open";
        Build();
        string rom = Path.Combine(Project.Folder, "build", Project.Name + ".smc");
        if (!File.Exists(rom)) return Status;                  // the build already said why
        string exe = ReferenceRoms.Resource(Path.Combine("Lunar Magic", "Lunar Magic.exe"));
        if (!File.Exists(exe)) return $"Lunar Magic is not at {exe}";
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(exe, rom) { UseShellExecute = true });
        }
        catch (Exception ex) { return $"could not start Lunar Magic: {ex.Message}"; }
        Report($"opened {Path.GetFileName(rom)} in Lunar Magic");
        return null;
    }

    public string? EmulatorPath => Config.EmulatorPath;
    /// <summary>The emulator's name for the menu ("Mesen", "snes9x"), null until one is set.</summary>
    public string? EmulatorName => Config.EmulatorPath is { } p ? Path.GetFileNameWithoutExtension(p) : null;

    public void SetEmulator(string? path)
    {
        Config.EmulatorPath = path;
        Config.Save();
    }

    /// <summary>File → Run in emulator (F4), Lunar Magic's habit: build, then launch the ROM in
    /// Mesen — the configured one, or the first Mesen.exe found in the usual places (remembered
    /// once found). Not the OS's .smc association: on an LM user's machine that IS Lunar Magic.
    /// Returns a problem, or null when the emulator was launched.</summary>
    public string? RunInEmulator()
    {
        if (Project is null) return "no project open";
        Build();
        string rom = Path.Combine(Project.Folder, "build", Project.Name + ".smc");
        if (!File.Exists(rom)) return Status;                  // the build already said why
        string? emu = Config.EmulatorPath;
        if (emu is not null && !File.Exists(emu)) return $"emulator not found at {emu} — File → Set emulator…";
        if (emu is null && FindMesen() is { } found) { emu = found; SetEmulator(found); }
        if (emu is null) return "no emulator found — File → Set emulator… (Mesen.exe)";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(emu, $"\"{rom}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { return $"could not start {Path.GetFileName(emu)}: {ex.Message}"; }
        Report($"running {Path.GetFileName(rom)} in {Path.GetFileNameWithoutExtension(emu)}");
        return null;
    }

    /// <summary>Mesen.exe where people keep it: next to the user's home, its installer's
    /// %LOCALAPPDATA% folder, Program Files, or anywhere on PATH.</summary>
    private static string? FindMesen()
    {
        string exe = OperatingSystem.IsWindows() ? "Mesen.exe" : "Mesen";
        var dirs = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mesen"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mesen"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Mesen"),
        };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        return dirs.Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
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
