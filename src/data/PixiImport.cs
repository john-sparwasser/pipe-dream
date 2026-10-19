namespace PipeDream;

/// <summary>
/// Reading custom sprites in from disk (reference/PIXI_PORT.md, phase 1): one .json or .cfg with
/// its ASM, or a whole PIXI folder — list.txt's SPRITE: section into slots, each listed sprite into
/// the library, and the files every sprite assembles with (routines/, asm/ExtraDefines/, each
/// folder's _header.asm). What it reads is the user's own copy; nothing of PIXI's ships with us.
/// </summary>
internal static class PixiImport
{
    /// <summary>What a folder import found: the sprites by name, the slots that run them, the
    /// shared files, and what could not be read.</summary>
    public sealed record Folder(Dictionary<string, ProjectFile.SpriteEntry> Sprites, Dictionary<string, string> Slots,
                                Dictionary<string, string> Files, List<string> Problems);

    /// <summary>The kind of sprite a slot holds, or null for a slot that is not one: global 00-BF
    /// sprites, C0-CF shooters, D0-DF generators (PIXI runs nothing custom from E0); per-level
    /// "LLL:NN" only B0-BF of levels 000-1FF.</summary>
    public static string? SlotKind(string slot)
    {
        int colon = slot.IndexOf(':');
        if (!int.TryParse(slot[(colon + 1)..], System.Globalization.NumberStyles.HexNumber, null, out int n)) return null;
        if (colon >= 0)
            return int.TryParse(slot[..colon], System.Globalization.NumberStyles.HexNumber, null, out int level)
                   && level is >= 0 and < 0x200 && n is >= 0xB0 and <= 0xBF ? "sprite" : null;
        return n switch { < 0 => null, < 0xC0 => "sprite", < 0xD0 => "shooter", < 0xE0 => "generator", _ => null };
    }

    /// <summary>A slot as the project keys it: "1A", "105:B0". Null when it is not a slot.</summary>
    public static string? NormalSlot(string slot)
    {
        slot = slot.Trim();
        if (SlotKind(slot) is null) return null;
        int colon = slot.IndexOf(':');
        int n = Convert.ToInt32(slot[(colon + 1)..], 16);
        return colon < 0 ? $"{n:X2}" : $"{Convert.ToInt32(slot[..colon], 16):X3}:{n:X2}";
    }

    /// <summary>The folder PIXI reads a kind's sprites (and its _header.asm) from.</summary>
    public static string FolderOf(string kind) => kind switch { "shooter" => "shooters", "generator" => "generators", _ => "sprites" };

    /// <summary>
    /// Read one sprite: its config (a .cfg converts to JSON), the ASM the config names, beside it,
    /// and every file that ASM includes. <paramref name="missing"/> lists includes not found; an
    /// unreadable config or a missing ASM throws <see cref="FormatException"/>.
    /// </summary>
    public static ProjectFile.SpriteEntry ReadSprite(string configPath, string kind, out List<string> missing)
    {
        string name = Path.GetFileNameWithoutExtension(configPath);
        string text = File.ReadAllText(configPath);
        var config = Path.GetExtension(configPath).Equals(".cfg", StringComparison.OrdinalIgnoreCase)
            ? PixiConfig.FromCfg(text, name)
            : PixiConfig.Parse(text);
        missing = [];
        var entry = new ProjectFile.SpriteEntry { Kind = kind, Config = config.ToJson() };
        if (config.Type == 0) return entry;                  // a tweak has no code
        string asm = Path.Combine(Path.GetDirectoryName(configPath)!, config.AsmFile);
        if (!File.Exists(asm)) throw new FormatException($"its ASM file {config.AsmFile} is not beside it");
        entry.Source = File.ReadAllText(asm);
        entry.Files = PatchInsert.Includes(asm, out missing);
        return entry;
    }

    /// <summary>Read a PIXI folder: list.txt, the sprites it lists, and the shared files. A line
    /// that cannot be read is a problem, not a failure, so one bad sprite does not lose the rest;
    /// a missing list.txt throws <see cref="FileNotFoundException"/>.</summary>
    public static Folder ReadFolder(string dir)
    {
        string list = Path.Combine(dir, "list.txt");
        if (!File.Exists(list)) throw new FileNotFoundException("no list.txt in that folder", list);
        var result = new Folder([], [], [], []);
        // The same file in two slots is one sprite; keyed by where it lives, named by its file.
        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool sprites = true;
        int skipped = 0, lineNo = 0;
        foreach (string raw in File.ReadAllLines(list))
        {
            lineNo++;
            string line = (raw.IndexOf(';') is int sc and >= 0 ? raw[..sc] : raw).Trim();
            if (line.Length == 0) continue;
            if (line.EndsWith(':')) { sprites = line == "SPRITE:"; continue; }
            if (!sprites) { skipped++; continue; }
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            // A trailing "display"/"nodisplay" (PIXI 1.43, .cfg only) is about LM's lists: dropped.
            string file = parts.Length > 1 ? parts[1].Split(' ')[0] : "";
            if (parts.Length < 2 || NormalSlot(parts[0]) is not { } slot)
            { result.Problems.Add($"list.txt line {lineNo}: \"{line}\" is not a slot and a file"); continue; }
            string kind = SlotKind(slot)!;
            string path = Path.Combine(dir, FolderOf(kind), file);
            if (!byPath.TryGetValue(path, out var name))
            {
                if (Path.GetExtension(file).ToLowerInvariant() is not (".json" or ".cfg"))
                { result.Problems.Add($"list.txt line {lineNo}: {file} is not a .json or .cfg"); continue; }
                ProjectFile.SpriteEntry entry;
                List<string> missing;
                try { entry = ReadSprite(path, kind, out missing); }
                catch (Exception e) when (e is FormatException or IOException)
                { result.Problems.Add($"{FolderOf(kind)}/{file}: {e.Message}"); continue; }
                name = UniqueName(result.Sprites, Path.ChangeExtension(file, null).Replace('\\', '/'), kind);
                result.Sprites[name] = entry;
                byPath[path] = name;
                if (missing.Count > 0) result.Problems.Add($"{name}: not found beside it: {string.Join(", ", missing)}");
            }
            result.Slots[slot] = name;
        }
        if (skipped > 0) result.Problems.Add($"{skipped} cluster, extended or other sprite(s) skipped: Pipe Dream inserts normal sprites, shooters and generators");
        ReadShared(dir, result.Files);
        return result;
    }

    /// <summary>A file's name, unless a sprite of another kind already has it (a shooter and a
    /// sprite can share a file name in their two folders).</summary>
    private static string UniqueName(Dictionary<string, ProjectFile.SpriteEntry> sprites, string name, string kind)
        => sprites.ContainsKey(name) ? $"{name} ({kind})" : name;

    /// <summary>routines/ (subfolders and all), asm/ExtraDefines/, and the three folders' _header.asm.</summary>
    private static void ReadShared(string dir, Dictionary<string, string> files)
    {
        void Add(string path, string key) { if (File.Exists(path)) files[key] = File.ReadAllText(path); }
        void AddTree(string root, string prefix)
        {
            if (!Directory.Exists(root)) return;
            foreach (string f in Directory.EnumerateFiles(root, "*.asm", SearchOption.AllDirectories))
                Add(f, prefix + Path.GetRelativePath(root, f).Replace('\\', '/'));
        }
        AddTree(Path.Combine(dir, "routines"), "routines/");
        AddTree(Path.Combine(dir, "asm", "ExtraDefines"), "ExtraDefines/");
        foreach (string kind in (string[])["sprite", "shooter", "generator"])
            Add(Path.Combine(dir, FolderOf(kind), "_header.asm"), FolderOf(kind) + "/_header.asm");
    }
}
