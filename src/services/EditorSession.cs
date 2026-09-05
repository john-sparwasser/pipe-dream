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
///
/// This file is the spine: what the session owns, how a ROM or project is opened, how a level
/// is loaded (<see cref="ShowLevel"/>), how the picture is rebuilt after an edit, and how the
/// live level is folded back into the project and saved. Each editing concern has its own
/// partial beside it — EditorSession.Background / .Map16 / .Gfx / .Palette / .Sprites / .ExAnim /
/// .Entrances / .CourseBot / .Build / .Config.cs — headed by a comment saying what lives there.
/// </summary>
public sealed partial class EditorSession
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

    /// <summary>A file could not be read or written. The status line gets the one-line form; the
    /// UI subscribes here to put the whole thing — file, reason, way forward — in front of the
    /// user, because a failed save cannot be a line in the corner.</summary>
    public event EventHandler<FileProblem>? Problem;

    private void Fail(FileProblem p) { Report(p.OneLine); Problem?.Invoke(this, p); }

    /// <summary>Run a file operation; a file failure becomes a <see cref="Problem"/> rather than
    /// an exception. Every save, build and open routes through here, so a read-only folder, a
    /// moved file or a ROM an emulator still holds is reported once, the same way, instead of
    /// ending the process from whichever handler happened to be running.</summary>
    private bool Guard(string doing, Action act, string? path = null)
    {
        try { act(); return true; }
        catch (Exception e) when (FileProblem.IsFile(e)) { Fail(FileProblem.From(e, doing, path)); return false; }
    }

    private void SaveConfig() => Guard("save your settings", Config.Save, Config.FilePath);

    public bool HasUnsavedWork => Project is { Dirty: true } || touched.Count > 0 || LevelDirty;

    /// <summary>Whether the CURRENT level holds work the project snapshot does not have yet.
    /// The palette has its own flag rather than "any edit at all": counting hydrated edits as
    /// dirty meant a level with a custom colour could never read as saved — Ctrl+S wrote the
    /// file and the title kept its marker, which looked like the save had not happened.</summary>
    private bool LevelDirty => Edit is { Dirty: true } || Sprites is { Dirty: true } || paletteDirty;

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
        catch (Exception ex) when (FileProblem.IsFile(ex)) { Fail(FileProblem.From(ex, "open the ROM", path)); return false; }
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
            Guard("save your settings", () => Config.TouchRecentProject(p.FilePath), Config.FilePath);

            NewGfxEdit();
            string? warn = ProjectSession.Hydrate(Rom, p.Data);
            ShowLevel(LevelNum);
            Report($"project '{p.Name}' opened" + (warn is null ? "" : " — " + warn)
                   + (prepNote is null ? "" : " — base not updated: " + prepNote));
            return true;
        }
        catch (Exception ex) when (FileProblem.IsFile(ex)) { Fail(FileProblem.From(ex, "open the project", pdpPath)); return false; }
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
            if (FileProblem.IsFile(ex)) Fail(FileProblem.From(ex, "create the project", folder));
            else Report($"could not create project: {ex.Message} ({ex.GetType().Name})");
            return false;
        }
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

    // ---- recent projects ----

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
                SaveConfig();
            }
            return Config.RecentProjects;
        }
    }

    // ---- the open ROM: facts and base prep ----

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

    // ---- level navigation ----

    /// <summary>Make <paramref name="num"/> the current level: stash the one being left, then
    /// build the scene and every per-level editor from the ROM plus the project's snapshot.</summary>
    public void ShowLevel(int num)
    {
        if (Rom is null) return;
        StashBeforeLeaving(num);
        LevelNum = num;
        try
        {
            // PROJECT HYDRATION. A level recorded in the project replaces the ROM-parsed
            // object and sprite state with the project's snapshot — without this, reopening
            // a project shows the base ROM's level and the edits look lost (they are not;
            // they are in the .pdp, which is worse: the next save writes the stale view back).
            var saved = Project?.Data.LevelOrNull(num);
            var live = HydratedSprites(saved);
            HydratePalette(saved);

            // Built without sprites when the project has its own list, so the ROM's parse is
            // not composed in and then painted over.
            Scene = LevelScene.Build(Rom, num, SpriteMode(live is not null), paletteEdits, PreviewLayer3);
            if (live is not null) DrawSprites(live);

            Sprites = (live ?? Scene.Sprites) is { } sd
                ? new SpriteEdit(sd, Scene.Overlay, Vertical) { EntrySize = Rom.SpriteEntrySize } : null;

            OpenLayer1(saved);
            OpenLayer2(num, saved);

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

    /// <summary>Commit the current level's edits before switching away from it.</summary>
    private void StashBeforeLeaving(int num)
    {
        // Leaving a level commits its edits: a crash should cost the current level at worst,
        // not everything since the last manual save.
        //
        // EVERY kind of edit counts, not just objects. Gating this on the object list alone lost
        // sprite and palette work silently — you would place a sprite, switch level, come back,
        // and find the base ROM's sprites.
        if (num != LevelNum && LevelDirty)
        {
            StashCurrent();
            if (Project is { } pr) Guard("save the project", pr.Save, pr.FilePath);
        }
    }

    /// <summary>The project's sprite list for the level, or null when the ROM's parse is it.</summary>
    private static SpriteData? HydratedSprites(ProjectFile.LevelState? saved)
    {
        SpriteData? live = null;
        if (saved is not null)
        {
            live = new SpriteData { SpriteMemory = saved.SpriteMemory, Buoyancy = saved.Buoyancy };
            live.Sprites.AddRange(saved.Sprites.Select(s => s.ToSprite()));
        }
        return live;
    }

    /// <summary>Replace the palette edits and their history with the level's own.</summary>
    private void HydratePalette(ProjectFile.LevelState? saved)
    {
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
        paletteDirty = false;                        // what is here now IS the snapshot
    }

    /// <summary>The layer-1 object editor over the scene just built, rendered so cells own objects.</summary>
    private void OpenLayer1(ProjectFile.LevelState? saved)
    {
        var objects = saved is not null
            ? saved.Objects.Select(o => o.ToLevelObject()).ToList()
            : [.. Scene!.Level.Objects];
        layer1 = new LevelEdit(Rom!, Scene!, objects);
        // Always run the TRACKED render, as the ImGui editor does on every parse. It is
        // what gives each cell an owning object, and without it nothing on a freshly
        // opened level can be selected or hit-tested. It also puts a hydrated level's
        // pixels on screen from its OBJECT LIST rather than the base ROM's parsed grid;
        // for an unedited level the two are identical, and a render failure leaves the
        // parsed grid in place.
        layer1.Rerender();
    }

    /// <summary>The layer-2 object editor, when the level has (or the project gave it) a stream.</summary>
    private void OpenLayer2(int num, ProjectFile.LevelState? saved)
    {
        // Layer 2 is the same object stream format, so it gets its own editor on the same
        // class. The project's copy wins, and a project list on a background-image level IS
        // the conversion to object mode — the pointer's bank byte is the only mode flag
        // there is (CONTRACT §10), so there is nothing else to record.
        baseLayer2 = LevelParser.ParseLayer2(Rom!, num);
        var l2 = saved?.Layer2Objects is { } pl2
            ? pl2.Select(o => o.ToLevelObject()).ToList()
            : baseLayer2 is not null ? new List<LevelObject>(baseLayer2) : null;
        layer2 = l2 is null ? null : new LevelEdit(Rom!, Scene!, l2, layer: 1);
        if (layer2 is not null)
        {
            // A converted level has no layer-2 grid yet: the scene was composed from a
            // background image, so give it one before the render replaces it.
            Scene!.Layer2 ??= new Map16Grid(Scene.Grid.Width, Scene.Grid.Height);
            layer2.Rerender();
        }
        if (EditLayer == 1 && layer2 is null) EditLayer = 0;
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

    // ---- level header ----
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

    public bool HasHeaderOverride => Rom?.LevelHeaderOverrides.ContainsKey(LevelNum) == true;

    // ---- recomposing ----
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
            Scene = LevelScene.Build(Rom, LevelNum, SpriteMode(Sprites is not null), paletteEdits, PreviewLayer3);
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
        paletteDirty = false;                            // the snapshot has them; Project.Dirty says whether disk does
        state.Stash(Project.Data, Rom, LevelNum);
        Project.MarkDirty();
        touched.Add(LevelNum);
    }

    public string Save()
    {
        if (Project is null) return "no project open — File ▸ New Project first";
        if (!Guard("save the project", Project.Save, Project.FilePath)) return Status;   // SyncBeforeSave folds the live level in
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

    /// <summary>Autosave tick — the project debounces its own writes.</summary>
    public void Tick() => Project?.Tick();
}
