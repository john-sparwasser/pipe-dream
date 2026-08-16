using System.Text.Json;

namespace PipeDream;

/// <summary>
/// Global per-user editor state: %APPDATA%\PipeDream\config.json — the location of an
/// unedited SMW ROM (set by the first-run prompt) and the recent-projects list. Lives in
/// AppData so it survives wherever the exe sits; the app itself stays portable.
/// </summary>
internal sealed class Config
{
    public string? VanillaRomPath { get; set; }
    public List<string> RecentProjects { get; set; } = new();

    internal static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PipeDream");
    internal static string FilePath => Path.Combine(Dir, "config.json");

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

    /// <summary>Move <paramref name="path"/> to the front of the recents (max 8, no dupes).</summary>
    internal void TouchRecentProject(string path)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 8) RecentProjects.RemoveRange(8, RecentProjects.Count - 8);
        Save();
    }
}
