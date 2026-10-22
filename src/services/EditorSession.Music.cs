using System.Text.RegularExpressions;

namespace PipeDream.Services;

// EditorSession — music: the project's AddmusicK-style library (ProjectFile.Music) — songs, the
// numbers they play under, samples and sound effects — and reading and writing an AMK folder
// (reference/AMK_PORT.md, phase 1). Like the block library, these edits mark the project dirty and are
// not undoable. Nothing here compiles or inserts music yet.
// The rest of the class: the other EditorSession.*.cs files.
public sealed partial class EditorSession
{
    /// <summary>One song as the Music window lists it: its path, the number and section it plays under
    /// ("global 01", "local 35", "" for none), and its size in bytes of MML.</summary>
    public sealed record SongInfo(string Path, string Number, int Bytes);

    /// <summary>One sample or bank as a list shows it.</summary>
    public sealed record SampleInfo(string Path, int Bytes);

    /// <summary>The project's songs, by path.</summary>
    public IReadOnlyList<SongInfo> MusicSongs
    {
        get
        {
            if (Project is null) return [];
            var m = Project.Data.Music;
            return m.Songs.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(s =>
            {
                var slots = m.SongList.Where(x => x.Path == s.Key).Select(x => $"{(x.Global ? "global" : "local")} {x.Number:X2}");
                return new SongInfo(s.Key, string.Join(", ", slots), s.Value.Length);
            }).ToList();
        }
    }

    /// <summary>What SMW's level song <paramref name="vanilla"/> plays in this project: the number AddmusicK gives it, and the
    /// project's song there (null when the project has no music: SMW's own plays).</summary>
    private string? LevelSongName(int vanilla)
    {
        if (Project?.Data.Music is not { SongList.Count: > 0 } m) return null;
        int n = MusicInsert.LevelSongs.GetValueOrDefault(vanilla, vanilla);
        var slot = m.SongList.FirstOrDefault(s => s.Number == n);
        return $"song {n:X2}, " + (slot is null ? "not in the project (the music fades)" : Path.GetFileNameWithoutExtension(slot.Path));
    }

    /// <summary>The project's samples and banks, by path.</summary>
    public IReadOnlyList<SampleInfo> MusicSamples
        => Project?.Data.Music.Samples.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                                      .Select(s => new SampleInfo(s.Key, s.Value.Length)).ToList() ?? [];

    /// <summary>A song's MML, or null.</summary>
    public string? SongText(string path) => Project?.Data.Music.Songs.GetValueOrDefault(path);

    /// <summary>Replace a song's MML. An unchanged text is not an edit.</summary>
    public void SetSongText(string path, string text)
    {
        if (Project is null || !Project.Data.Music.Songs.TryGetValue(path, out var old) || old == text) return;
        Project.Data.Music.Songs[path] = text;
        Project.MarkDirty();
    }

    /// <summary>Add a song from the template, in no slot yet. A name without an extension becomes ".mml".</summary>
    public string NewSong(string name, out string? made) => AddSong(name, null, out made);

    /// <summary>Add a song file from disk under its file name, with the samples it names from beside it
    /// (a quoted "x.brr" or "x.bnk" found there). The status names any it could not find.</summary>
    public string ImportSongFile(string path, out string? made)
    {
        made = null;
        string? text = null;
        if (!Guard("read the song", () => text = File.ReadAllText(path), path)) return Status;
        string status = AddSong(Path.GetFileName(path), text, out made);
        if (made is null) return status;
        string beside = Path.GetDirectoryName(path) ?? ".";
        var m = Project!.Data.Music;
        int copied = 0;
        List<string> missing = [];
        foreach (string sample in SampleNames(text!).Distinct())
        {
            if (m.Samples.ContainsKey(sample)) continue;
            string file = Path.Combine(beside, sample.Replace('/', Path.DirectorySeparatorChar));
            if (!AmkImport.SafePath(sample) || !File.Exists(file)) { missing.Add(sample); continue; }
            if (!Guard("read a sample", () => m.Samples[sample] = File.ReadAllBytes(file), file)) return Status;
            copied++;
        }
        if (copied > 0) status += $" with {copied} sample(s)";
        if (missing.Count > 0) status += "; not found beside it: " + string.Join(", ", missing);
        return Report2(status);
    }

    /// <summary>Give a song a number and a section, replacing any it had. The number is hex 01-FF, the one
    /// Lunar Magic and the game use, and must not be another song's.</summary>
    public string SetSongNumber(string path, int number, bool global)
    {
        if (Project is null) return Report2("no project open");
        var m = Project.Data.Music;
        if (!m.Songs.ContainsKey(path)) return Report2($"{path} is not in the project");
        if (number is < 1 or > 0xFF) return Report2("a song number is 01-FF");
        if (m.SongList.Find(s => s.Number == number && s.Path != path) is { } other)
            return Report2($"number {number:X2} is {other.Path}'s");
        var mine = m.SongList.Where(s => s.Path == path).ToList();
        if (mine.Count == 1 && mine[0].Number == number && mine[0].Global == global) return Status;
        m.SongList.RemoveAll(s => s.Path == path);
        m.SongList.Add(new MusicState.SongSlot { Number = number, Path = path, Global = global });
        m.SongList.Sort((a, b) => a.Number.CompareTo(b.Number));
        return Report2(Dirty2($"{path} is {(global ? "global" : "local")} {number:X2}"));
    }

    /// <summary>Take a song out of the numbered list. It stays in the project and is not inserted.</summary>
    public string ClearSongNumber(string path)
    {
        if (Project is null) return Report2("no project open");
        var m = Project.Data.Music;
        if (!m.Songs.ContainsKey(path)) return Report2($"{path} is not in the project");
        if (m.SongList.RemoveAll(s => s.Path == path) == 0) return Report2($"{path} has no number");
        return Report2(Dirty2($"{path} is in no slot"));
    }

    /// <summary>Take a song, and its numbers, out of the project.</summary>
    public string DeleteSong(string path)
    {
        if (Project is null) return Report2("no project open");
        var m = Project.Data.Music;
        if (!m.Songs.Remove(path)) return Report2($"{path} is not in the project");
        m.SongList.RemoveAll(s => s.Path == path);
        return Report2(Dirty2($"deleted {path}"));
    }

    /// <summary>Add a .brr sample or .bnk bank from disk under its file name. Refused when one of that
    /// name is already in the project.</summary>
    public string ImportSampleFile(string path, out string? made)
    {
        made = null;
        if (Project is null) return Report2("no project open");
        string name = Path.GetFileName(path);
        if (!name.EndsWith(".brr", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
            return Report2("a sample is a .brr file, or a .bnk bank");
        if (Project.Data.Music.Samples.ContainsKey(name)) return Report2($"{name} is already in the project");
        byte[]? bytes = null;
        if (!Guard("read the sample", () => bytes = File.ReadAllBytes(path), path)) return Status;
        Project.Data.Music.Samples[name] = bytes!;
        made = name;
        return Report2(Dirty2($"added {name}"));
    }

    /// <summary>Take a sample out of the project and out of every sample group that listed it.</summary>
    public string DeleteSample(string path)
    {
        if (Project is null) return Report2("no project open");
        var m = Project.Data.Music;
        if (!m.Samples.Remove(path)) return Report2($"{path} is not in the project");
        foreach (var g in m.Groups) g.Samples.RemoveAll(s => s.Path == path);
        return Report2(Dirty2($"deleted {path}"));
    }

    /// <summary>Read an AddmusicK folder into the project: its songs, samples, sound effects, sample
    /// groups, the numbered list and its options. Files with the same name are replaced, and a number or
    /// group the folder names replaces the project's; everything else stays.</summary>
    public string ImportAmkFolder(string dir)
    {
        if (Project is null) return Report2("no project open");
        AmkImport.Folder? f = null;
        try { f = AmkImport.ReadFolder(dir); }
        catch (FileNotFoundException) { return Report2("that folder has no Addmusic_list.txt — pick AddmusicK's own folder"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Report2($"could not read the AddmusicK folder: {e.Message}"); }
        var (src, dst) = (f.Music, Project.Data.Music);
        foreach (var (k, v) in src.Songs) dst.Songs[k] = v;
        foreach (var (k, v) in src.Samples) dst.Samples[k] = v;
        foreach (var (k, v) in src.SfxSources) dst.SfxSources[k] = v;
        dst.SongList.RemoveAll(s => src.SongList.Any(n => n.Number == s.Number));
        dst.SongList.AddRange(src.SongList);
        dst.SongList.Sort((a, b) => a.Number.CompareTo(b.Number));
        foreach (var g in src.Groups) { dst.Groups.RemoveAll(x => x.Name == g.Name); dst.Groups.Add(g); }
        Merge(dst.Sfx1DF9, src.Sfx1DF9);
        Merge(dst.Sfx1DFC, src.Sfx1DFC);
        if (src.Options.Count > 0) dst.Options = src.Options;
        string status = $"imported {src.Songs.Count} song(s) ({src.SongList.Count} numbered), {src.Samples.Count} sample(s), "
                      + $"{src.Groups.Count} group(s), {src.Sfx1DF9.Count + src.Sfx1DFC.Count} effect(s)";
        if (f.Problems.Count > 0) status += "; " + string.Join("; ", f.Problems.Take(5)) + (f.Problems.Count > 5 ? $"; and {f.Problems.Count - 5} more" : "");
        return Report2(Dirty2(status));

        static void Merge(List<MusicState.SfxSlot> into, List<MusicState.SfxSlot> from)
        {
            into.RemoveAll(s => from.Any(n => n.Slot == s.Slot));
            into.AddRange(from);
            into.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        }
    }

    /// <summary>Write the project's music as an AddmusicK folder, for AMK itself or to share. Files already
    /// there are overwritten.</summary>
    public string ExportAmkFolder(string dir)
    {
        if (Project is null) return Report2("no project open");
        var m = Project.Data.Music;
        if (m.IsEmpty) return Report2("the project has no music to export");
        if (!Guard("write the AddmusicK folder", () => AmkImport.WriteFolder(m, dir), dir)) return Status;
        return Report2($"wrote {m.Songs.Count} song(s), {m.Samples.Count} sample(s) and {m.SfxSources.Count} effect(s) to {dir}");
    }

    private string AddSong(string name, string? text, out string? made)
    {
        made = null;
        if (Project is null) return Report2("no project open");
        name = name.Trim();
        if (!name.Contains('.', StringComparison.Ordinal) || name.EndsWith('.')) name = name.TrimEnd('.') + ".mml";
        if (!AmkImport.SafePath(name)) return Report2("a song name is a file name, with folders written as a/b.mml");
        var songs = Project.Data.Music.Songs;
        if (songs.Keys.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase))) return Report2($"{name} is already in the project");
        songs[name] = text ?? SongTemplate(name);
        made = name;
        return Report2(Dirty2($"added {name}"));
    }

    /// <summary>Every .brr or .bnk a song names in quotes.</summary>
    private static IEnumerable<string> SampleNames(string mml)
        => Regex.Matches(mml, "\"([^\"\\r\\n]+\\.(?:brr|bnk))\"", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value);

    private string Dirty2(string status) { Project!.MarkDirty(); return status; }

    /// <summary>A song that plays four notes, saying what Pipe Dream does with it.</summary>
    internal static string SongTemplate(string name) => $"""
        #amk 2

        ; {name}
        ;
        ; AddmusicK's MML. Pipe Dream keeps it in the project and inserts it on build once it has a number
        ; (Music window). Channels are #0-#7; 6 and 7 are shared with sound effects.

        #0 t40 @0 v255 o5 l8
        c d e f g a b > c

        """;
}
