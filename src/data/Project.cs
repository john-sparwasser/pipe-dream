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
    internal ProjectFile Data { get; private set; }

    /// <summary>Set when an edit made the in-memory state newer than project.pdp;
    /// the sync callback (set by the session layer) flushes editor state into Data
    /// right before a save.</summary>
    private bool dirty;
    private long dirtyAtMs;
    internal Action? SyncBeforeSave;

    /// <summary>Edits are in memory but not yet in project.pdp (autosave is debounced).</summary>
    internal bool Dirty => dirty;

    private Project(string folder, ProjectFile data) { Folder = folder; Data = data; }

    /// <summary>Create a new project: folder, private base-ROM copy, initial project.pdp.
    /// A verified-vanilla base is automatically PREPPED (RomPrep: LM-equivalent structures
    /// for the full editing feature set); the pinned hash is then of the prepped image —
    /// deterministic, so a shared .pdp can be reproduced from any vanilla copy.</summary>
    internal static Project Create(string folder, string baseRomSource)
    {
        Directory.CreateDirectory(folder);
        string dest = Path.Combine(folder, RomName);
        File.Copy(baseRomSource, dest, overwrite: false);
        int prepVersion = 0;
        if (RomHash.HeaderlessSha256File(dest) == RomHash.VanillaUsSha256 &&
            RomPrep.PrepInPlace(dest) is null)
            prepVersion = RomPrep.Version;
        byte[] bytes = File.ReadAllBytes(dest);
        var data = new ProjectFile
        {
            BaseRom = new ProjectFile.BaseRomInfo
            {
                Sha256 = RomHash.HeaderlessSha256(bytes),
                Size = bytes.Length,
                Title = Rom.Load(dest).Title,
                PrepVersion = prepVersion,
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
    /// pinned hash and copy it in as base.smc. For prepped projects a raw VANILLA ROM
    /// also works — it's prepped deterministically and must then match the pin.
    /// Returns a problem string or null.</summary>
    internal string? AdoptBase(string romPath)
    {
        string hash = RomHash.HeaderlessSha256File(romPath);
        if (hash == Data.BaseRom.Sha256)
        {
            File.Copy(romPath, BaseRomPath, overwrite: true);
            return null;
        }
        if (Data.BaseRom.PrepVersion > 0 && hash == RomHash.VanillaUsSha256)
        {
            if (Data.BaseRom.PrepVersion > RomPrep.Version)
                return $"this project's base was prepared by a newer editor (prep v{Data.BaseRom.PrepVersion}) — update pipe-dream.";
            File.Copy(romPath, BaseRomPath, overwrite: true);
            // Prep with the PROJECT'S version — released stamp lists are byte-frozen, so
            // a v1 pin reproduces exactly even after the editor moves to v2.
            if (RomPrep.PrepInPlace(BaseRomPath, Data.BaseRom.PrepVersion) is { } err) { File.Delete(BaseRomPath); return err; }
            if (RomHash.HeaderlessSha256File(BaseRomPath) != Data.BaseRom.Sha256)
            {
                File.Delete(BaseRomPath);
                return "prep of that vanilla ROM did not reproduce the pinned base — the project may need a newer editor.";
            }
            return null;
        }
        return "that ROM's hash does not match this project's pinned base.";
    }

    /// <summary>Whether "upgrade base" applies: a project already on an older prep, or one
    /// whose base is an UNPREPPED verified vanilla. The latter exists because projects created
    /// before prep landed pinned a raw vanilla image — with no LM structures at all, every
    /// feature that needs them (Map16 pages, acts-like, custom palettes, DM16 objects, in-game
    /// GFX) reports "save it in Lunar Magic once first" and there was no way out from the UI.
    /// A PrepVersion-0 base that is NOT vanilla is a foreign/LM ROM: prepping would replace it
    /// with vanilla and throw the hack away, so those are excluded.</summary>
    internal bool CanUpgradeBasePrep =>
        Data.BaseRom.PrepVersion is >= 1 && Data.BaseRom.PrepVersion < RomPrep.Version
        || Data.BaseRom.PrepVersion == 0 && File.Exists(BaseRomPath)
           && RomHash.HeaderlessSha256File(BaseRomPath) == RomHash.VanillaUsSha256;

    /// <summary>
    /// Bring the base up to the current prep on open. Called for every project the editor
    /// opens, so the answer to "why won't this feature work" is never an invisible stale
    /// base — a project pinned to a raw vanilla (PrepVersion 0) has no LM structures at all
    /// and refuses Map16 pages, acts-like, palettes, DM16 objects and in-game GFX.
    ///
    /// Returns null when nothing needed doing or it succeeded, else a reason the base was
    /// left alone. A failure is not fatal: the project keeps working on its old base, which
    /// is exactly what it did before.
    /// </summary>
    internal string? PrepareBaseOnOpen(string? vanillaRomPath)
        => CanUpgradeBasePrep ? UpgradeBasePrep(vanillaRomPath) : null;

    /// <summary>Upgrade a project's base to the current prep version: prep a fresh copy of the
    /// user's verified vanilla ROM to a temp file, swap it in as base.smc, and re-pin.
    /// Returns a problem or null.</summary>
    internal string? UpgradeBasePrep(string? vanillaRomPath)
    {
        if (!CanUpgradeBasePrep)
            return Data.BaseRom.PrepVersion == 0
                ? "this project's base is not a verified vanilla ROM — prep would discard it."
                : "base is already at the current prep version.";
        // A v0 base IS the vanilla image, so it can seed its own prep — no configured ROM
        // needed for the case that most needs fixing.
        string? source = vanillaRomPath is not null && File.Exists(vanillaRomPath) &&
                         RomHash.HeaderlessSha256File(vanillaRomPath) == RomHash.VanillaUsSha256
            ? vanillaRomPath
            : Data.BaseRom.PrepVersion == 0 ? BaseRomPath : null;
        if (source is null)
            return "no verified vanilla SMW ROM configured — set one first (first-run prompt / config).";
        string tmp = BaseRomPath + ".upgrade";
        File.Copy(source, tmp, overwrite: true);
        if (RomPrep.PrepInPlace(tmp) is { } err) { File.Delete(tmp); return err; }
        File.Move(tmp, BaseRomPath, overwrite: true);
        byte[] bytes = File.ReadAllBytes(BaseRomPath);
        Data.BaseRom.Sha256 = RomHash.HeaderlessSha256(bytes);
        Data.BaseRom.Size = bytes.Length;
        Data.BaseRom.PrepVersion = RomPrep.Version;
        Save();
        return null;
    }

    /// <summary>Debug helper: drop every edit, keeping only the base-ROM pin. Sync is unhooked
    /// first — the whole point is NOT to flush the live editor state back into the file — so the
    /// caller must reopen the project to get a session that matches what is now on disk.</summary>
    internal void ClearEdits()
    {
        SyncBeforeSave = null;
        Data = new ProjectFile { BaseRom = Data.BaseRom };
        Save();
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
