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

    /// <summary>Config directory for a platform. Split out from <see cref="Dir"/> so the
    /// per-OS choice can be tested from any host. .NET maps ApplicationData to %APPDATA% on
    /// Windows and $XDG_CONFIG_HOME (or ~/.config) on Linux — both already conventional —
    /// but on macOS it also gives ~/.config, where the convention is Application Support.</summary>
    internal static string DirFor(bool isMacOS, string appData, string home) =>
        isMacOS ? Path.Combine(home, "Library", "Application Support", "PipeDream")
                : Path.Combine(appData, "PipeDream");

    internal static string Dir => DirFor(
        OperatingSystem.IsMacOS(),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string FilePath => Path.Combine(Dir, "config.json");

    /// <summary>How to compare paths on the host filesystem. Windows and macOS are
    /// case-insensitive in practice; Linux is not, and treating "/a/X" and "/a/x" as the
    /// same file there would silently drop a distinct project from the recents.</summary>
    internal static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    internal static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt config falls back to defaults; the next Save rewrites it */ }
        return new();
    }

    internal void Save()
    {
        Directory.CreateDirectory(Dir);
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

        // Atomic: write a temp file and swap, so a crash mid-write cannot destroy the config.
        //
        // The temp name is UNIQUE per write. A fixed one (config.json.tmp) is a race the moment
        // two things save at once — a second editor window, or a test run with parallel classes:
        // one writer's file is moved out from under the other, and the loser throws an IOException
        // that surfaces as "could not open project" for no reason the user can act on.
        string tmp = $"{FilePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
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
                try { File.Move(tmp, FilePath, overwrite: true); return; }
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
