namespace PipeDream.Services;

// EditorSession — custom blocks: importing a GPS folder, and the project's block list
// (ProjectFile.Blocks: tile → file under gps/blocks/). The rest of the class: the other
// EditorSession.*.cs files.
public sealed partial class EditorSession
{
    /// <summary>Import a GPS folder: its assembly into the project's gps/ (GpsInsert.Import), its
    /// list.txt into the project — each tile's block into <see cref="ProjectFile.Blocks"/>,
    /// replacing what was there, and each acts-like the list gives into the Map16 editor, where
    /// it is an ordinary acts-as edit (one undo step per value). Returns the status.</summary>
    public string ImportGps(string folder)
    {
        if (Project is null) return "no project open";
        Guard("import the GPS folder", () =>
        {
            string? problem = GpsInsert.Import(folder, Project.GpsFolder, out var list);
            if (problem is not null) { Report("GPS import: " + problem); return; }

            Project.Data.Blocks = list.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString("X3"), kv => kv.Value.File);
            var acts = list.Where(kv => kv.Value.ActsLike >= 0).GroupBy(kv => kv.Value.ActsLike).ToList();
            string actsNote = acts.Count == 0 ? ""
                : Map16 is { HasActsAs: true }
                    ? $", {acts.Sum(g => g.Count())} acts-like value(s) into the Map16 editor"
                    : $"; {acts.Sum(g => g.Count())} acts-like value(s) NOT applied (the base has no acts-like table)";
            if (Map16 is { HasActsAs: true })
                foreach (var g in acts) Map16.SetActsAs(g.Select(kv => kv.Key), g.Key);
            Project.MarkDirty();
            Project.Save();
            Report($"imported GPS folder: {list.Count} block tile(s){actsNote}");
        }, Project.GpsFolder);
        return Status;
    }

    // ---- the block list ----

    /// <summary>One row of the project's block list. Acts-like is the Map16 editor's (the live
    /// ROM, which is what the build writes); the handled contacts are read off the LAST BUILD,
    /// because a block only exists on a ROM once it has been assembled into one — empty until
    /// then, or when the build did not include this tile.</summary>
    public sealed record BlockEntry(int Tile, string File, bool FileExists, int? ActsLike, bool Built, string[] Handled);

    /// <summary>Whether the project has GPS's driver (gps/main.asm), which every block needs: it
    /// comes from an imported GPS folder, since GPS's files carry no licence we could ship under.</summary>
    public bool HasGpsDriver => Project is not null && File.Exists(Path.Combine(Project.GpsFolder, "main.asm"));

    /// <summary>The block files a tile can be assigned: every .asm under gps/blocks/, as the
    /// project names them (relative, forward slashes), sorted.</summary>
    public IReadOnlyList<string> BlockFiles
    {
        get
        {
            if (Project is null) return [];
            string dir = Path.Combine(Project.GpsFolder, "blocks");
            if (!Directory.Exists(dir)) return [];
            return Directory.GetFiles(dir, "*.asm", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

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
                return new BlockEntry(kv.Key, kv.Value, File.Exists(Path.Combine(Project.GpsFolder, "blocks", kv.Value)), acts,
                                      onRom is not null, onRom?.HandledContacts.ToArray() ?? []);
            }).ToList();
        }
    }

    /// <summary>The block file a tile is assigned, or null.</summary>
    public string? BlockFileOf(int tile) => Project?.Data.Blocks.GetValueOrDefault(tile.ToString("X3"));

    /// <summary>Assign <paramref name="file"/> (under gps/blocks/) to a Map16 tile, or with null
    /// or empty take the tile's block away. Pages 02-7F only, as GPS has it. Returns the status.</summary>
    public string AssignBlock(int tile, string? file)
    {
        if (Project is null) return Report2("no project open");
        if (tile is < 0x200 or >= 0x8000) return Report2($"tile {tile:X3} cannot be a custom block (pages 02-7F only)");
        string key = tile.ToString("X3");
        file = file?.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(file))
        {
            if (!Project.Data.Blocks.Remove(key)) return Report2($"tile {key} has no block");
            Project.MarkDirty();
            return Report2($"tile {key}: block removed");
        }
        if (!File.Exists(Path.Combine(Project.GpsFolder, "blocks", file))) return Report2($"gps/blocks/{file} does not exist");
        Project.Data.Blocks[key] = file;
        Project.MarkDirty();
        return Report2($"tile {key}: {file}");
    }

    /// <summary>Write a new block file from the template into gps/blocks/ and return its project
    /// name through <paramref name="file"/>. Nothing is assigned: that is <see cref="AssignBlock"/>.
    /// Needs the driver, because a block with nothing to insert it is a file going nowhere.</summary>
    public string NewBlock(string name, out string? file)
    {
        file = null;
        if (Project is null) return Report2("no project open");
        if (!HasGpsDriver) return Report2("import a GPS folder first — its main.asm is what inserts blocks");
        name = name.Trim();
        if (name.EndsWith(".asm", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-')))
            return Report2("a block name is letters, digits, _ and -");
        string path = Path.Combine(Project.GpsFolder, "blocks", name + ".asm");
        if (File.Exists(path)) return Report2($"gps/blocks/{name}.asm already exists");
        string made = name + ".asm";
        if (!Guard("create the block", () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BlockTemplate(name));
        }, path)) return Status;
        file = made;
        return Report2($"created gps/blocks/{made}");
    }

    /// <summary>Open a block's .asm in whatever the OS opens .asm files with. Returns a problem, or null.</summary>
    public string? OpenBlockFile(string file)
    {
        if (Project is null) return "no project open";
        string path = Path.Combine(Project.GpsFolder, "blocks", file);
        if (!File.Exists(path)) return $"gps/blocks/{file} does not exist";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { return $"could not open {file}: {ex.Message}"; }
        return null;
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
        ; Routines from gps/routines/ are called as macros: %erase_block(), %create_smoke(), ...
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
