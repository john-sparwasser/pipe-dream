using System.Text.Json;

namespace PipeDream;

/// <summary>
/// Global per-user editor state: config.json holding the location of an unedited SMW ROM
/// (set by the first-run prompt) and the recent-projects list. Lives in the platform's
/// per-user config area so it survives wherever the exe sits; the app itself stays portable.
/// </summary>
internal sealed class Config
{
    public string? VanillaRomPath { get; set; }
    public List<string> RecentProjects { get; set; } = new();

    /// <summary>Emulator for File → Run in emulator (F4). Null = whatever the OS opens .smc
    /// files with, so the command works before anyone has set anything.</summary>
    public string? EmulatorPath { get; set; }

    /// <summary>Whether to ask GitHub about newer releases on startup. On by default; the only
    /// thing it sends is the request itself, and a build that never checks is a build that
    /// stays old.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>A release the user said no to, so it is not offered again. Stored as the
    /// version string rather than a bool, because skipping 0.1.9 must not also skip 0.1.10.</summary>
    public string? SkippedUpdate { get; set; }

    /// <summary>When the last automatic check went out, so startup does not ask on every
    /// launch.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>How the GFX browser lists files: "names", "list" or "cards".</summary>
    public string GfxBrowserView { get; set; } = "list";

    /// <summary>Config directory for a platform. Split out from <see cref="Dir"/> so the
    /// per-OS choice can be tested from any host. .NET maps ApplicationData to %APPDATA% on
    /// Windows and $XDG_CONFIG_HOME (or ~/.config) on Linux — both already conventional —
    /// but on macOS it also gives ~/.config, where the convention is Application Support.</summary>
    internal static string DirFor(bool isMacOS, string appData, string home) =>
        isMacOS ? Path.Combine(home, "Library", "Application Support", "PipeDream")
                : Path.Combine(appData, "PipeDream");

    /// <summary>Where config.json actually lives. PIPEDREAM_CONFIG_DIR overrides it, the way
    /// PIPEDREAM_SMW_ROOT overrides the reference-ROM root: the test suite points it at a temp
    /// folder, because <see cref="Save"/> serialises whatever instance it is called on and a
    /// test holding a fresh <c>new Config()</c> would otherwise write DEFAULTS over the real
    /// user's vanilla ROM path, emulator and recents. Nothing else sets it.</summary>
    internal static string Dir =>
        Environment.GetEnvironmentVariable("PIPEDREAM_CONFIG_DIR") is { Length: > 0 } d
            ? d
            : DirFor(OperatingSystem.IsMacOS(),
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string FilePath => Path.Combine(Dir, "config.json");

    /// <summary>How to compare paths on the host filesystem. Windows and macOS are
    /// case-insensitive in practice; Linux is not, and treating "/a/X" and "/a/x" as the
    /// same file there would silently drop a distinct project from the recents.</summary>
    internal static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>What this instance loaded, so <see cref="Save(string)"/> can tell the fields it
    /// changed from the ones it merely carried. Null on an instance made with <c>new</c>, which
    /// then saves whole — the test suite's fresh instances, against its own redirected file.</summary>
    private Config? loaded;

    internal static Config Load() => Load(FilePath);

    /// <summary>The file at <paramref name="path"/>, or defaults when it is absent or unreadable.
    /// Path-parameterised so a test can work a file of its own rather than the one the run shares.</summary>
    internal static Config Load(string path)
    {
        var cfg = Read(path) ?? new();
        cfg.loaded = cfg.Snapshot();
        return cfg;
    }

    /// <summary>
    /// The file parsed, or null when there is nothing usable to parse. Two failures, told apart
    /// because they need opposite handling.
    ///
    /// A read that fails with an IO error is most likely another instance mid-swap — Save's own
    /// retry exists for the same race from the other side — so it is retried. If it still fails,
    /// defaults are used, and what stops those defaults from being SAVED over a perfectly good
    /// file is the merge in Save, which re-reads the file before writing.
    ///
    /// A file that exists but does not parse is set aside as config.json.corrupt. Left in place it
    /// would be overwritten by the defaults on the next action — the recents getter alone saves —
    /// and the user's settings would be gone without a trace.
    /// </summary>
    private static Config? Read(string path)
    {
        if (!File.Exists(path)) return null;
        for (int attempt = 0; ; attempt++)
        {
            try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt < 3) { Thread.Sleep(5); continue; }
                return null;
            }
            catch (JsonException)
            {
                try { File.Move(path, path + ".corrupt", overwrite: true); } catch { /* defaults still apply */ }
                return null;
            }
        }
    }

    /// <summary>A deep copy by the same round trip the file makes; the private state stays behind.</summary>
    private Config Snapshot() => JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(this))!;

    internal void Save() => Save(FilePath);

    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Merge before writing. This file has as many writers as the user has editor instances
        // — there is no single-instance guard, so two projects open side by side are two
        // processes — and each used to write its whole in-memory copy. Set the vanilla ROM in
        // one window, touch the recents in the other, and the second wrote its stale null over
        // the first. Now a field this instance did not change takes whatever is on disk, so a
        // writer only ever moves its own fields. It also heals a load that fell back to defaults
        // on a transient read error: at save time the file reads fine, and the defaults yield.
        if (loaded is { } was && Read(path) is { } disk)
        {
            if (VanillaRomPath == was.VanillaRomPath) VanillaRomPath = disk.VanillaRomPath;
            if (RecentProjects.SequenceEqual(was.RecentProjects)) RecentProjects = disk.RecentProjects;
            if (EmulatorPath == was.EmulatorPath) EmulatorPath = disk.EmulatorPath;
            if (CheckForUpdates == was.CheckForUpdates) CheckForUpdates = disk.CheckForUpdates;
            if (SkippedUpdate == was.SkippedUpdate) SkippedUpdate = disk.SkippedUpdate;
            if (LastUpdateCheckUtc == was.LastUpdateCheckUtc) LastUpdateCheckUtc = disk.LastUpdateCheckUtc;
            if (GfxBrowserView == was.GfxBrowserView) GfxBrowserView = disk.GfxBrowserView;
        }
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

        // Atomic: write a temp file and swap, so a crash mid-write cannot destroy the config.
        //
        // The temp name is UNIQUE per write. A fixed one (config.json.tmp) is a race the moment
        // two things save at once — a second editor window, or a test run with parallel classes:
        // one writer's file is moved out from under the other, and the loser throws an IOException
        // that surfaces as "could not open project" for no reason the user can act on.
        string tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, json);
            // The replace itself can still lose a race with a reader holding the file open, so
            // give it a couple of tries before giving up.
            //
            // BOTH exception types, and that is not belt-and-braces: Windows reports a losing
            // File.Move(overwrite) as UnauthorizedAccessException ("Access to the path is
            // denied"), not IOException. Catching only the latter meant the retry never ran on
            // the platform it was written for, and the loser surfaced the failure as "could not
            // open project" — with nothing the user could do about it.
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); loaded = Snapshot(); return; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                          && attempt < 3) { Thread.Sleep(5); }
            }
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    /// <summary>Move <paramref name="path"/> to the front of the recents (max 8, no dupes).
    /// Normalized first so the same project reached two ways collapses to one entry.</summary>
    internal void TouchRecentProject(string path)
    {
        try { path = Path.GetFullPath(path); } catch { /* keep the caller's string */ }
        RecentProjects.RemoveAll(p => string.Equals(p, path, PathComparison));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 8) RecentProjects.RemoveRange(8, RecentProjects.Count - 8);
        Save();
    }
}
