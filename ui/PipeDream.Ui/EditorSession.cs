namespace PipeDream.Ui;

/// <summary>
/// Everything the editor knows that is not a control: the config, the open project, the live
/// ROM, and which level is being edited. The window observes it; it never observes the window.
///
/// This is the piece the ImGui editor never had — its equivalent state is ~65 fields on
/// EditorApp that every component reaches into, which is why its save path could not run
/// without a GUI. Keeping it here means the whole open → edit → save → build cycle is
/// exercisable headlessly.
/// </summary>
internal sealed class EditorSession
{
    public Config Config { get; } = Config.Load();
    public Project? Project { get; private set; }
    public Rom? Rom { get; private set; }
    public string? RomPath { get; private set; }
    public int LevelNum { get; private set; } = 0x105;
    public LevelScene? Scene { get; private set; }
    public LevelEdit? Edit { get; private set; }

    /// <summary>Levels whose edits have not been stashed into the project yet.</summary>
    private readonly HashSet<int> touched = [];

    public event EventHandler? Changed;
    public string Status { get; private set; } = "";

    private void Report(string s) { Status = s; Changed?.Invoke(this, EventArgs.Empty); }

    public bool HasUnsavedWork => Project is { Dirty: true } || touched.Count > 0
                                  || Edit is { Dirty: true };

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
        if (num != LevelNum && Edit is { Dirty: true }) { StashCurrent(); Project?.Save(); }
        LevelNum = num;
        try
        {
            Scene = LevelScene.Build(Rom, num);

            // PROJECT HYDRATION. A level recorded in the project replaces the ROM-parsed
            // object and sprite state with the project's snapshot — without this, reopening
            // a project shows the base ROM's level and the edits look lost (they are not;
            // they are in the .pdp, which is worse: the next save writes the stale view back).
            var saved = Project?.Data.LevelOrNull(num);
            var objects = saved is not null
                ? saved.Objects.Select(o => o.ToLevelObject()).ToList()
                : [.. Scene.Level.Objects];

            Edit = new LevelEdit(Rom, Scene, objects);
            // Always run the TRACKED render, as the ImGui editor does on every parse. It is
            // what gives each cell an owning object, and without it nothing on a freshly
            // opened level can be selected or hit-tested. It also puts a hydrated level's
            // pixels on screen from its OBJECT LIST rather than the base ROM's parsed grid;
            // for an unedited level the two are identical, and a render failure leaves the
            // parsed grid in place.
            Edit.Rerender();

            if (saved is not null && Scene.Sprites is not null)
            {
                var sp = new SpriteData { SpriteMemory = saved.SpriteMemory, Buoyancy = saved.Buoyancy };
                sp.Sprites.AddRange(saved.Sprites.Select(s => s.ToSprite()));
                Edit.HydratedSprites = sp;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { Report($"level ${num:X3}: {ex.Message}"); }
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

    private void StashCurrent()
    {
        if (Project is null || Rom is null || Edit is null) return;
        Edit.EditState().Stash(Project.Data, Rom, LevelNum);
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
