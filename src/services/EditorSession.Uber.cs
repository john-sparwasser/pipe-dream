using System.Text;

namespace PipeDream.Services;

// EditorSession — UberASM: the project's resources, library and list (ProjectFile.Uber, reference/UBERASM_PORT.md). Each
// operation returns a status line; edits mark the project dirty and are not undoable. Inserting is RomBuilder's.
public sealed partial class EditorSession
{
    /// <summary>One file as the UberASM window lists it, and where the list runs it ("level 105, *").</summary>
    public sealed record UberFileInfo(string Path, int Bytes, string RunsIn);

    public IReadOnlyList<UberFileInfo> UberFiles
    {
        get
        {
            if (Project is null) return [];
            var u = Project.Data.Uber;
            return [.. u.Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(f => new UberFileInfo(f.Key, f.Value.Length, RunsIn(u, f.Key)))];
        }
    }

    private static string RunsIn(UberState u, string path)
    {
        var where = new List<string>();
        foreach (var (kind, entries) in new[] { ("level", u.Levels), ("game mode", u.GameModes), ("map", u.Overworlds) })
            foreach (var e in entries.Where(e => e.Calls.Any(c => c.File == path && !c.Skip)))
                where.Add($"{kind} {(e.Number == UberEntry.All ? "*" : e.Number.ToString("X"))}");
        return string.Join(", ", where);
    }

    /// <summary>The resources a level, game mode or map runs (kind "level", "gamemode", "overworld"); <see cref="UberEntry.All"/>
    /// for <c>*</c>. Empty when it has none.</summary>
    public IReadOnlyList<UberCall> UberCalls(string kind, int number)
        => Project is null ? [] : Section(Project.Data.Uber, kind)?.Find(e => e.Number == number)?.Calls ?? [];

    /// <summary>Set what a level, game mode or map runs, in order; an empty list takes it out of the list.</summary>
    public string SetUberCalls(string kind, int number, IEnumerable<UberCall> calls)
    {
        if (Project is null) return Report2("no project open");
        if (Section(Project.Data.Uber, kind) is not { } section) return Report2($"\"{kind}\" is not level, gamemode or overworld");
        var list = calls.ToList();
        if (list.Find(c => !Project.Data.Uber.Files.ContainsKey(c.File)) is { } missing) return Report2($"{missing.File} is not in the project");
        section.RemoveAll(e => e.Number == number);
        if (list.Count > 0) section.Add(new UberEntry { Number = number, Calls = list });
        section.Sort((a, b) => a.Number.CompareTo(b.Number));
        string where = number == UberEntry.All ? "*" : number.ToString("X");
        return Report2(Dirty2(list.Count == 0 ? $"{kind} {where} runs nothing" : $"{kind} {where} runs {string.Join(", ", list.Select(c => (c.Skip ? "-" : "") + c.File))}"));
    }

    /// <summary>The numbers the list has a line for, in order (<see cref="UberEntry.All"/> first).</summary>
    public IReadOnlyList<int> UberNumbers(string kind)
        => Project is null ? [] : Section(Project.Data.Uber, kind)?.Select(e => e.Number).Order().ToList() ?? [];

    /// <summary>What a level, game mode or map runs as a list line without its number: "a.asm : 12 34, star.asm".</summary>
    public string UberLine(string kind, int number) => UberImport.FormatCalls(kind, UberCalls(kind, number));

    /// <summary>Set what a level, game mode or map runs from a list line without its number, read as list.txt reads it.</summary>
    public string SetUberLine(string kind, int number, string line)
    {
        if (Project is null) return Report2("no project open");
        if (line.Trim().Length == 0) return SetUberCalls(kind, number, []);
        var parsed = new UberState();
        var problems = new List<string>();
        UberImport.ParseList($"{kind}:\n{(number == UberEntry.All ? "*" : number.ToString("X"))} {line}", parsed, problems);
        if (problems.Count > 0) return Report2(problems[0].Replace($"{UberImport.ListFile} line 2: ", ""));
        return SetUberCalls(kind, number, Section(parsed, kind)!.SelectMany(e => e.Calls));
    }

    private static List<UberEntry>? Section(UberState u, string kind)
        => kind switch { "level" => u.Levels, "gamemode" => u.GameModes, "overworld" => u.Overworlds, _ => null };

    public string? UberText(string path)
        => Project?.Data.Uber.Files.TryGetValue(path, out var b) == true ? Encoding.UTF8.GetString(b) : null;

    public void SetUberText(string path, string text)
    {
        var files = Project?.Data.Uber.Files;
        if (files is null || !files.TryGetValue(path, out var old) || Encoding.UTF8.GetString(old) == text) return;
        files[path] = Encoding.UTF8.GetBytes(text);
        Report(Dirty2($"saved {path}"));
    }

    /// <summary>A new resource or library file (a path under one of UberASM Tool's folders, "level/waves.asm") with a main: that
    /// returns, ready to edit.</summary>
    public string NewUberFile(string path, out string? made)
    {
        made = null;
        if (Project is null) return Report2("no project open");
        path = path.Trim().Replace('\\', '/');
        if (!path.EndsWith(".asm", StringComparison.OrdinalIgnoreCase)) path += ".asm";
        string folder = path.Split('/')[0];
        if (!AmkImport.SafePath(path) || !path.Contains('/') || folder is not ("level" or "gamemode" or "overworld" or "library" or "routines" or "other"))
            return Report2("a file is in level/, gamemode/, overworld/, library/, routines/ or other/ (\"level/waves.asm\")");
        if (Project.Data.Uber.Files.ContainsKey(path)) return Report2($"{path} is already in the project");
        string body = folder is "library" or "routines" ? "    RTL\n" : "init:\n    RTL\n\nmain:\n    RTL\n";
        Project.Data.Uber.Files[path] = Encoding.UTF8.GetBytes(body);
        made = path;
        return Report2(Dirty2($"added {path}"));
    }

    /// <summary>Add a file from disk under a folder of UberASM Tool's ("level", "library" …), replacing one of that name.</summary>
    public string ImportUberFile(string file, string folder, out string? made)
    {
        made = null;
        if (Project is null) return Report2("no project open");
        if (folder is not ("level" or "gamemode" or "overworld" or "library" or "routines" or "other")) return Report2($"\"{folder}\" is not one of UberASM's folders");
        byte[]? bytes = null;
        if (!Guard("read the file", () => bytes = File.ReadAllBytes(file), file)) return Status;
        made = $"{folder}/{Path.GetFileName(file)}";
        Project.Data.Uber.Files[made] = bytes!;
        return Report2(Dirty2($"added {made}"));
    }

    /// <summary>Take a file out of the project, and out of every list entry that ran it.</summary>
    public string DeleteUberFile(string path)
    {
        if (Project is null) return Report2("no project open");
        var u = Project.Data.Uber;
        if (!u.Files.Remove(path)) return Report2($"{path} is not in the project");
        foreach (var section in new[] { u.Levels, u.GameModes, u.Overworlds })
        {
            foreach (var e in section) e.Calls.RemoveAll(c => c.File == path);
            section.RemoveAll(e => e.Calls.Count == 0);
        }
        return Report2(Dirty2($"deleted {path}"));
    }

    /// <summary>Read an UberASM Tool folder into the project: its files (not the tool's own asm/), and the list, whose entries
    /// replace the project's for the same numbers. Files of the same path are replaced.</summary>
    public string ImportUberFolder(string dir)
    {
        if (Project is null) return Report2("no project open");
        UberImport.Folder? f;
        try { f = UberImport.ReadFolder(dir); }
        catch (FileNotFoundException) { return Report2("that folder has no list.txt — pick UberASM Tool's own folder"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Report2($"could not read the UberASM folder: {e.Message}"); }
        var (src, dst) = (f.Uber, Project.Data.Uber);
        foreach (var (k, v) in src.Files) dst.Files[k] = v;
        Merge(dst.Levels, src.Levels);
        Merge(dst.GameModes, src.GameModes);
        Merge(dst.Overworlds, src.Overworlds);
        (dst.Global, dst.StatusBar, dst.MacroLib, dst.FreeRam) = (src.Global, src.StatusBar, src.MacroLib, src.FreeRam);
        int entries = src.Levels.Count + src.GameModes.Count + src.Overworlds.Count;
        string status = $"imported {src.Files.Count} file(s) and {entries} list entr{(entries == 1 ? "y" : "ies")}";
        if (f.Problems.Count > 0) status += "; " + string.Join("; ", f.Problems.Take(5)) + (f.Problems.Count > 5 ? $"; and {f.Problems.Count - 5} more" : "");
        return Report2(Dirty2(status));

        static void Merge(List<UberEntry> into, List<UberEntry> from)
        {
            into.RemoveAll(e => from.Any(n => n.Number == e.Number));
            into.AddRange(from);
            into.Sort((a, b) => a.Number.CompareTo(b.Number));
        }
    }
}
