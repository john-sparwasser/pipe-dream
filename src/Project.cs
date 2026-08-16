namespace PipeDream;

/// <summary>
/// An open project on disk: folder layout, base-ROM hash pinning, and the debounced
/// atomic autosave of project.pdp. The base ROM is copied into the folder at creation
/// so a project is self-contained from t0; the copy is private (never shared) — only
/// project.pdp travels between users.
/// </summary>
internal sealed class Project
{
    internal const string RomName = "base.smc";
    internal const string FileName = "project.pdp";
    private const long AutosaveDebounceMs = 1000;

    internal string Folder { get; }
    internal string BaseRomPath => Path.Combine(Folder, RomName);
    internal string FilePath => Path.Combine(Folder, FileName);
    internal string Name => Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar));
    internal ProjectFile Data { get; }

    /// <summary>Set when an edit made the in-memory state newer than project.pdp;
    /// the sync callback (set by the session layer) flushes editor state into Data
    /// right before a save.</summary>
    private bool dirty;
    private long dirtyAtMs;
    internal Action? SyncBeforeSave;

    private Project(string folder, ProjectFile data) { Folder = folder; Data = data; }

    /// <summary>Create a new project: folder, private base-ROM copy, initial project.pdp.</summary>
    internal static Project Create(string folder, string baseRomSource)
    {
        Directory.CreateDirectory(folder);
        string dest = Path.Combine(folder, RomName);
        File.Copy(baseRomSource, dest, overwrite: false);
        byte[] bytes = File.ReadAllBytes(dest);
        var data = new ProjectFile
        {
            BaseRom = new ProjectFile.BaseRomInfo
            {
                Sha256 = RomHash.HeaderlessSha256(bytes),
                Size = bytes.Length,
                Title = Rom.Load(dest).Title,
            },
        };
        var p = new Project(folder, data);
        p.Save();
        return p;
    }

    /// <summary>Open an existing project by its .pdp path. Throws with a user-facing
    /// message when the base copy is missing or doesn't match the pinned hash — the
    /// caller offers "locate base ROM" recovery via <see cref="AdoptBase"/>.</summary>
    internal static Project Open(string pdpPath)
    {
        var data = ProjectFile.FromJson(File.ReadAllText(pdpPath));
        if (data.SchemaVersion > 1)
            throw new InvalidDataException($"project schema v{data.SchemaVersion} is newer than this editor");
        return new Project(Path.GetDirectoryName(Path.GetFullPath(pdpPath))!, data);
    }

    /// <summary>null = base copy present and matching; otherwise a user-facing problem.</summary>
    internal string? ValidateBase()
    {
        if (!File.Exists(BaseRomPath))
            return "base.smc is missing from the project folder — locate the base ROM to restore it.";
        return RomHash.HeaderlessSha256File(BaseRomPath) == Data.BaseRom.Sha256 ? null
            : "base.smc does not match the project's pinned base ROM hash.";
    }

    /// <summary>Recovery for a shared bare .pdp: verify a user-located ROM against the
    /// pinned hash and copy it in as base.smc. Returns a problem string or null.</summary>
    internal string? AdoptBase(string romPath)
    {
        if (RomHash.HeaderlessSha256File(romPath) != Data.BaseRom.Sha256)
            return "that ROM's hash does not match this project's pinned base.";
        File.Copy(romPath, BaseRomPath, overwrite: true);
        return null;
    }

    internal void MarkDirty()
    {
        dirty = true;
        dirtyAtMs = Environment.TickCount64;
    }

    /// <summary>Called once per frame: autosave when edits settled (debounced).</summary>
    internal void Tick()
    {
        if (dirty && Environment.TickCount64 - dirtyAtMs >= AutosaveDebounceMs) Save();
    }

    /// <summary>Atomic write (temp + replace): a crash mid-write must never destroy
    /// the only copy of the project.</summary>
    internal void Save()
    {
        SyncBeforeSave?.Invoke();
        dirty = false;
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, Data.ToJson());
        File.Move(tmp, FilePath, overwrite: true);
    }
}
