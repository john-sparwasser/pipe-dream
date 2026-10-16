namespace PipeDream.Services;

// EditorSession — custom blocks: the project's block library (ProjectFile.BlockLibrary: name →
// ASM), which tile runs which block (ProjectFile.Blocks), and importing a GPS folder into both.
// The rest of the class: the other EditorSession.*.cs files.
public sealed partial class EditorSession
{
    /// <summary>Import a GPS folder: its driver and routines into the project (replacing the
    /// last import's), its blocks into the library (a block of the same name is replaced), and
    /// its list.txt over the project's tiles — each tile's block into <see cref="ProjectFile.Blocks"/>,
    /// and each acts-like the list gives into the Map16 editor, where it is an ordinary acts-as
    /// edit (one undo step per value). Returns the status.</summary>
    public string ImportGps(string folder)
    {
        if (Project is null) return "no project open";
        Guard("import the GPS folder", () =>
        {
            string? problem = GpsInsert.Import(folder, out var list, out var files, out var library);
            if (problem is not null) { Report("GPS import: " + problem); return; }

            Project.Data.GpsFiles = files;
            foreach (var (name, asm) in library) Project.Data.BlockLibrary[name] = asm;
            foreach (var (tile, e) in list) Project.Data.Blocks[tile.ToString("X3")] = e.File;
            var acts = list.Where(kv => kv.Value.ActsLike >= 0).GroupBy(kv => kv.Value.ActsLike).ToList();
            string actsNote = acts.Count == 0 ? ""
                : Map16 is { HasActsAs: true }
                    ? $", {acts.Sum(g => g.Count())} acts-like value(s) into the Map16 editor"
                    : $"; {acts.Sum(g => g.Count())} acts-like value(s) NOT applied (the base has no acts-like table)";
            if (Map16 is { HasActsAs: true })
                foreach (var g in acts) Map16.SetActsAs(g.Select(kv => kv.Key), g.Key);
            Project.MarkDirty();
            Project.Save();
            Report($"imported GPS folder: {library.Count} block(s), {list.Count} block tile(s){actsNote}");
        }, folder);
        return Status;
    }

    // ---- the library ----

    /// <summary>Whether the project has GPS's driver (main.asm), which every block needs: it
    /// comes from an imported GPS folder, since GPS's files carry no licence we could ship under.</summary>
    public bool HasGpsDriver => Project?.Data.GpsFiles.ContainsKey("main.asm") == true;

    /// <summary>The library's block names, sorted.</summary>
    public IReadOnlyList<string> BlockNames =>
        Project?.Data.BlockLibrary.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [];

    /// <summary>A library block's ASM, or null.</summary>
    public string? BlockSource(string name) => Project?.Data.BlockLibrary.GetValueOrDefault(name);

    /// <summary>Replace a library block's ASM. An unchanged text is not an edit.</summary>
    public void SetBlockSource(string name, string asm)
    {
        if (Project is null || !Project.Data.BlockLibrary.TryGetValue(name, out var old) || old == asm) return;
        Project.Data.BlockLibrary[name] = asm;
        Project.MarkDirty();
    }

    /// <summary>Add a block to the library from the template and return its name through
    /// <paramref name="made"/>. Nothing is assigned: that is <see cref="AssignBlock"/>.</summary>
    public string NewBlock(string name, out string? made) => AddBlock(name, null, out made);

    /// <summary>Add an .asm file from disk to the library under its file name.</summary>
    public string ImportBlockFile(string path, out string? made)
    {
        made = null;
        string? asm = null;
        if (!Guard("read the block", () => asm = File.ReadAllText(path), path)) return Status;
        return AddBlock(Path.GetFileNameWithoutExtension(path), asm, out made);
    }

    private string AddBlock(string name, string? asm, out string? made)
    {
        made = null;
        if (Project is null) return Report2("no project open");
        name = name.Trim();
        if (name.EndsWith(".asm", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-')))
            return Report2("a block name is letters, digits, _ and -");
        if (Project.Data.BlockLibrary.ContainsKey(name + ".asm")) return Report2($"{name}.asm is already in the library");
        made = name + ".asm";
        Project.Data.BlockLibrary[made] = asm ?? BlockTemplate(name);
        Project.MarkDirty();
        return Report2($"added {made} to the library");
    }

    /// <summary>Take a block out of the library. One a tile still runs stays, and the status
    /// says how many tiles.</summary>
    public string DeleteBlock(string name)
    {
        if (Project is null) return Report2("no project open");
        int used = Project.Data.Blocks.Values.Count(v => v == name);
        if (used > 0) return Report2($"{name} runs on {used} tile(s) — take it off them first");
        if (!Project.Data.BlockLibrary.Remove(name)) return Report2($"{name} is not in the library");
        Project.MarkDirty();
        return Report2($"deleted {name}");
    }

    // ---- which tile runs what ----

    /// <summary>One tile's block. Acts-like is the Map16 editor's (the live ROM, which is what
    /// the build writes); the handled contacts are read off the LAST BUILD, because a block only
    /// exists on a ROM once it has been assembled into one — empty until then, or when the build
    /// did not include this tile.</summary>
    public sealed record BlockEntry(int Tile, string File, bool InLibrary, int? ActsLike, bool Built, string[] Handled);

    /// <summary>The project's blocks by tile, with what the editor and the last build know about each.</summary>
    public IReadOnlyList<BlockEntry> BlockEntries
    {
        get
        {
            if (Project is null) return [];
            var built = LastBuildBlocks();
            return GpsInsert.BlocksOf(Project.Data).OrderBy(kv => kv.Key).Select(kv =>
            {
                string? key = kv.Key.ToString("X3");
                int? acts = Map16?.ActsAs(kv.Key)
                            ?? (Project.Data.Map16.ActsAs.TryGetValue(key, out int v) ? v : null);
                var onRom = built?.Blocks.GetValueOrDefault(kv.Key);
                return new BlockEntry(kv.Key, kv.Value, Project.Data.BlockLibrary.ContainsKey(kv.Value), acts,
                                      onRom is not null, onRom?.HandledContacts.ToArray() ?? []);
            }).ToList();
        }
    }

    /// <summary>The block a tile runs, or null.</summary>
    public string? BlockFileOf(int tile) => Project?.Data.Blocks.GetValueOrDefault(tile.ToString("X3"));

    /// <summary>Have a Map16 tile run library block <paramref name="name"/>, or with null or
    /// empty take the tile's block away. Pages 02-7F only, as GPS has it. Returns the status.</summary>
    public string AssignBlock(int tile, string? name)
    {
        if (Project is null) return Report2("no project open");
        if (tile is < 0x200 or >= 0x8000) return Report2($"tile {tile:X3} cannot be a custom block (pages 02-7F only)");
        string key = tile.ToString("X3");
        if (string.IsNullOrEmpty(name))
        {
            if (!Project.Data.Blocks.Remove(key)) return Report2($"tile {key} has no block");
            Project.MarkDirty();
            return Report2($"tile {key}: block removed");
        }
        if (!Project.Data.BlockLibrary.ContainsKey(name)) return Report2($"{name} is not in the library");
        Project.Data.Blocks[key] = name;
        Project.MarkDirty();
        return Report2($"tile {key}: {name}");
    }

    private string Report2(string s) { Report(s); return s; }

    /// <summary>A block that does nothing on any contact, laid out as GPS reads one: the header
    /// byte, then one JMP per contact in GPS's order, each landing on RTL. Ours, not GPS's
    /// template.asm (no licence); the layout is GPS's documented format.</summary>
    internal static string BlockTemplate(string name) => $"""
        ; {name} — a custom block. Pipe Dream inserts it with GPS's main.asm at build.
        ;
        ; The header byte picks the contact list: $42 is the ten contacts below; $37 adds
        ; "wall feet" and "wall body" (two more JMPs) for wall-running. One JMP per contact,
        ; in this order; a contact the block ignores lands on RTL.
        ; Routines from the imported GPS routines/ are called as macros: %erase_block(), %create_smoke(), ...
        db $42
        JMP MarioBelow : JMP MarioAbove : JMP MarioSide
        JMP SpriteV : JMP SpriteH : JMP MarioCape : JMP MarioFireball
        JMP TopCorner : JMP BodyInside : JMP HeadInside

        MarioBelow:
        MarioAbove:
        MarioSide:
        SpriteV:
        SpriteH:
        MarioCape:
        MarioFireball:
        TopCorner:
        BodyInside:
        HeadInside:
            RTL

        print "{name}"

        """;

    // ---- the last build ----

    private (string Path, DateTime Written, GpsBlocks? Blocks)? lastBuildBlocks;

    /// <summary>GPS's tables as the last build left them, re-read when the build output changes.</summary>
    private GpsBlocks? LastBuildBlocks()
    {
        if (Project is null) return null;
        string path = Path.Combine(Project.Folder, "build", Project.Name + ".smc");
        if (!File.Exists(path)) return null;
        var written = File.GetLastWriteTimeUtc(path);
        if (lastBuildBlocks is { } c && c.Path == path && c.Written == written) return c.Blocks;
        GpsBlocks? blocks = null;
        try { blocks = GpsBlocks.Read(Rom.Load(path)); } catch (Exception e) when (FileProblem.IsFile(e)) { }
        lastBuildBlocks = (path, written, blocks);
        return blocks;
    }
}
