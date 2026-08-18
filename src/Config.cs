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
        // Atomic: write a temp file and swap, so a crash mid-write can't destroy the config.
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
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
