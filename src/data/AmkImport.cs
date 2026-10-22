using System.Text;
using System.Text.RegularExpressions;

namespace PipeDream;

/// <summary>
/// Reading AddmusicK's files in and writing them out (reference/AMK_PORT.md, phase 1): its three
/// manifests (Addmusic_list.txt, "Addmusic_sample groups.txt", "Addmusic_sound effects.txt"), its options file,
/// and the songs, samples and effects they name. What it reads is the user's own folder; nothing of
/// AMK's ships with us. Written files use AMK's own layout, so AMK reads them unchanged.
/// </summary>
internal static class AmkImport
{
    private const string ListFile = "Addmusic_list.txt", GroupsFile = "Addmusic_sample groups.txt",
                         SfxFile = "Addmusic_sound effects.txt", OptionsFile = "Addmusic_options.txt";

    public sealed record Folder(MusicState Music, List<string> Problems);

    // ---- the manifests ----

    private static readonly Regex ListLine = new(@"^([0-9A-Fa-f]{1,2})\s+(\S.*)$");
    private static readonly Regex SfxLine = new(@"^([0-9A-Fa-f]{1,2})(?:[ \t]+([*?]))?[ \t]+(\S.*)$");
    private static readonly Regex GroupEntry = new("^\"(.*)\"(!?)$");

    private static IEnumerable<(int Number, string Line)> Lines(string text)
    {
        int n = 0;
        foreach (string raw in text.Split('\n')) yield return (++n, raw.Trim());
    }

    /// <summary>Addmusic_list.txt: a "Globals:" section and a "Locals:" one, each line a hex number and a song path.</summary>
    public static List<MusicState.SongSlot> ParseList(string text, List<string> problems)
    {
        var slots = new List<MusicState.SongSlot>();
        bool? global = null;
        foreach (var (n, line) in Lines(text))
        {
            if (line.Length == 0) continue;
            if (line.Equals("Globals:", StringComparison.OrdinalIgnoreCase)) { global = true; continue; }
            if (line.Equals("Locals:", StringComparison.OrdinalIgnoreCase)) { global = false; continue; }
            var m = ListLine.Match(line);
            if (!m.Success || global is null) { problems.Add($"{ListFile} line {n}: \"{line}\" is not a song"); continue; }
            slots.Add(new MusicState.SongSlot { Number = Convert.ToInt32(m.Groups[1].Value, 16), Path = m.Groups[2].Value, Global = global.Value });
        }
        return slots;
    }

    public static string FormatList(IEnumerable<MusicState.SongSlot> slots)
    {
        var all = slots.ToList();
        var sb = new StringBuilder("Globals:\r\n");
        foreach (var s in all.Where(s => s.Global)) sb.Append($"{s.Number:X2} {s.Path}\r\n");
        sb.Append("\r\nLocals:\r\n");
        foreach (var s in all.Where(s => !s.Global)) sb.Append($"{s.Number:X2} {s.Path}\r\n");
        return sb.ToString();
    }

    /// <summary>"Addmusic_sample groups.txt": <c>#name { "path" "path"! }</c>, a trailing ! marking a sample always resident.</summary>
    public static List<MusicState.SampleGroup> ParseGroups(string text, List<string> problems)
    {
        var groups = new List<MusicState.SampleGroup>();
        MusicState.SampleGroup? open = null;
        bool inside = false;
        foreach (var (n, line) in Lines(text))
        {
            if (line.Length == 0) continue;
            if (!inside && line.StartsWith('#')) { open = new MusicState.SampleGroup { Name = line[1..].Trim() }; groups.Add(open); continue; }
            if (line == "{" && open is not null && !inside) { inside = true; continue; }
            if (line == "}" && inside) { inside = false; open = null; continue; }
            var m = GroupEntry.Match(line);
            if (inside && m.Success) { open!.Samples.Add(new MusicState.SampleEntry { Path = m.Groups[1].Value, Important = m.Groups[2].Value == "!" }); continue; }
            problems.Add($"{GroupsFile} line {n}: \"{line}\" is not a group, a brace or a sample");
        }
        return groups;
    }

    public static string FormatGroups(IEnumerable<MusicState.SampleGroup> groups)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var g in groups)
        {
            if (!first) sb.Append("\r\n");
            first = false;
            sb.Append($"#{g.Name}\r\n{{\r\n");
            foreach (var s in g.Samples) sb.Append($"\t\"{s.Path}\"{(s.Important ? "!" : "")}\r\n");
            sb.Append("}\r\n");
        }
        return sb.ToString();
    }

    /// <summary>"Addmusic_sound effects.txt": "SFX1DF9:" and "SFX1DFC:" sections of <c>slot [*|?] TAB file</c>.</summary>
    public static (List<MusicState.SfxSlot> Port1DF9, List<MusicState.SfxSlot> Port1DFC) ParseSfx(string text, List<string> problems)
    {
        List<MusicState.SfxSlot> a = [], b = [], into = null!;
        foreach (var (n, line) in Lines(text))
        {
            if (line.Length == 0) continue;
            if (line.Equals("SFX1DF9:", StringComparison.OrdinalIgnoreCase)) { into = a; continue; }
            if (line.Equals("SFX1DFC:", StringComparison.OrdinalIgnoreCase)) { into = b; continue; }
            var m = SfxLine.Match(line);
            if (!m.Success || into is null) { problems.Add($"{SfxFile} line {n}: \"{line}\" is not a sound effect"); continue; }
            into.Add(new MusicState.SfxSlot { Slot = Convert.ToInt32(m.Groups[1].Value, 16), Flag = m.Groups[2].Value, File = m.Groups[3].Value });
        }
        return (a, b);
    }

    public static string FormatSfx(IEnumerable<MusicState.SfxSlot> port1DF9, IEnumerable<MusicState.SfxSlot> port1DFC)
    {
        var sb = new StringBuilder("SFX1DF9:\r\n");
        foreach (var s in port1DF9) sb.Append(Sfx(s));
        sb.Append("\r\nSFX1DFC:\r\n");
        foreach (var s in port1DFC) sb.Append(Sfx(s));
        return sb.ToString();

        static string Sfx(MusicState.SfxSlot s) => $"{s.Slot:X}{(s.Flag.Length > 0 ? " " + s.Flag : "")}\t{s.File}\r\n";
    }

    // ---- a folder ----

    /// <summary>Read an AMK folder: its manifests, options and every song, sample and effect file in it
    /// (not only the ones the manifests name, so a folder of songs imports whole). Throws when there is no
    /// Addmusic_list.txt. Anything that does not read, and every name a manifest gives that has no file,
    /// goes in the problems.</summary>
    public static Folder ReadFolder(string dir)
    {
        var problems = new List<string>();
        var music = new MusicState();
        string? list = FindFile(dir, ListFile);
        if (list is null) throw new FileNotFoundException($"no {ListFile} in {dir}");
        music.SongList = ParseList(File.ReadAllText(list), problems);
        if (FindFile(dir, GroupsFile) is { } g) music.Groups = ParseGroups(File.ReadAllText(g), problems);
        if (FindFile(dir, SfxFile) is { } f) (music.Sfx1DF9, music.Sfx1DFC) = ParseSfx(File.ReadAllText(f), problems);
        if (FindFile(dir, OptionsFile) is { } o)
            music.Options = File.ReadAllLines(o).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        foreach (var (key, path) in Tree(dir, "music"))
            if (key.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".mml", StringComparison.OrdinalIgnoreCase))
                music.Songs[key] = File.ReadAllText(path);
        foreach (var (key, path) in Tree(dir, "samples")) music.Samples[key] = File.ReadAllBytes(path);
        foreach (string port in new[] { "1DF9", "1DFC" })
            foreach (var (key, path) in Tree(dir, port)) music.SfxSources[$"{port}/{key}"] = File.ReadAllText(path);

        foreach (var s in music.SongList.Where(s => !music.Songs.ContainsKey(s.Path)))
            problems.Add($"song {s.Number:X2} names {s.Path}, which is not in music/");
        foreach (var e in music.Groups.SelectMany(gr => gr.Samples.Select(s => (gr.Name, s))).Where(x => !music.Samples.ContainsKey(x.s.Path)))
            problems.Add($"group #{e.Name} names {e.s.Path}, which is not in samples/");
        foreach (var (port, slots) in new[] { ("1DF9", music.Sfx1DF9), ("1DFC", music.Sfx1DFC) })
            foreach (var s in slots.Where(s => !music.SfxSources.ContainsKey($"{port}/{s.File}")))
                problems.Add($"effect {s.Slot:X} of {port} names {s.File}, which is not in {port}/");
        return new Folder(music, problems);
    }

    /// <summary>Write <paramref name="music"/> as an AMK folder: the manifests, options (when there are
    /// any) and the files, under music/, samples/, 1DF9/ and 1DFC/. Existing files are overwritten.</summary>
    public static void WriteFolder(MusicState music, string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ListFile), FormatList(music.SongList));
        File.WriteAllText(Path.Combine(dir, GroupsFile), FormatGroups(music.Groups));
        File.WriteAllText(Path.Combine(dir, SfxFile), FormatSfx(music.Sfx1DF9, music.Sfx1DFC));
        if (music.Options.Count > 0) File.WriteAllText(Path.Combine(dir, OptionsFile), string.Join("\r\n", music.Options) + "\r\n");
        var utf8 = new UTF8Encoding(false);
        foreach (var (key, text) in music.Songs) File.WriteAllText(Place(dir, "music", key), text, utf8);
        foreach (var (key, bytes) in music.Samples) File.WriteAllBytes(Place(dir, "samples", key), bytes);
        foreach (var (key, text) in music.SfxSources) File.WriteAllText(Place(dir, "", key), text, utf8);
    }

    /// <summary>Whether a name is a path a folder can hold: relative, forward slashes, no "..".</summary>
    public static bool SafePath(string key)
        => key.Length > 0 && !key.StartsWith('/') && !key.Contains('\\') && !key.Contains(':')
           && !key.Split('/').Any(part => part is "" or "." or "..");

    private static string Place(string dir, string folder, string key)
    {
        if (!SafePath(key)) throw new InvalidDataException($"\"{key}\" is not a path inside the folder");
        string path = Path.Combine(dir, folder, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    internal static IEnumerable<(string Key, string Path)> Tree(string dir, string folder)
    {
        string root = Path.Combine(dir, folder);
        if (!Directory.Exists(root)) yield break;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            yield return (Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'), file);
    }

    /// <summary>A file by name, whatever its case: AMK is Windows-born and Linux is not.</summary>
    internal static string? FindFile(string dir, string name)
        => Directory.Exists(dir)
           ? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
}
