namespace PipeDream.Services;

// EditorSession — custom sprites: the project's PIXI-style sprite library (ProjectFile.Sprites),
// which slot runs which (ProjectFile.SpriteSlots), and the shared files they assemble with
// (ProjectFile.PixiFiles) — reference/PIXI_PORT.md. Like the block library, these edits mark the
// project dirty and are not undoable. Placing sprites in a level is EditorSession.Sprites.cs.
// The rest of the class: the other EditorSession.*.cs files.
public sealed partial class EditorSession
{
    /// <summary>One library sprite as a list shows it: its slots ("1A", "105:B0"), acts-like, and
    /// its first Lunar Magic list name. A config that does not read has its problem as the name.</summary>
    public sealed record CustomSpriteInfo(string Name, string Kind, IReadOnlyList<string> Slots, int ActLike, string Description);

    /// <summary>The project's custom sprites, by name.</summary>
    public IReadOnlyList<CustomSpriteInfo> CustomSprites
    {
        get
        {
            if (Project is null) return [];
            var slots = Project.Data.SpriteSlots.GroupBy(s => s.Value).ToDictionary(g => g.Key, g => g.Select(s => s.Key).Order().ToList());
            return Project.Data.Sprites.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(s =>
            {
                var (actLike, description) = (0, "");
                try { var c = PixiConfig.Parse(s.Value.Config); (actLike, description) = (c.ActLike, c.Collection.FirstOrDefault()?.Name ?? ""); }
                catch (FormatException e) { description = "config: " + e.Message; }
                return new CustomSpriteInfo(s.Key, s.Value.Kind, slots.GetValueOrDefault(s.Key) ?? [], actLike, description);
            }).ToList();
        }
    }

    /// <summary>A sprite's config JSON, or null.</summary>
    public string? CustomSpriteConfig(string name) => FindSprite(name)?.Config;

    /// <summary>Replace a sprite's config. Refused, with the reason, when PIXI could not read it.</summary>
    public string SetCustomSpriteConfig(string name, string json)
    {
        if (Project is null) return Report2("no project open");
        if (FindSprite(name) is not { } s) return Report2($"{name} is not in the project");
        try { PixiConfig.Parse(json); }
        catch (FormatException e) { return Report2($"{name}: config not saved: {e.Message}"); }
        if (s.Config == json) return Status;
        s.Config = json;
        Project.MarkDirty();
        return Report2($"{name} config saved");
    }

    /// <summary>A sprite's ASM, or null.</summary>
    public string? CustomSpriteSource(string name) => FindSprite(name)?.Source;

    /// <summary>Replace a sprite's ASM. An unchanged text is not an edit.</summary>
    public void SetCustomSpriteSource(string name, string asm)
    {
        if (Project is null || FindSprite(name) is not { } s || s.Source == asm) return;
        s.Source = asm;
        Project.MarkDirty();
    }

    /// <summary>Add a sprite of <paramref name="kind"/> from the template, in no slot yet.</summary>
    public string NewCustomSprite(string name, string kind, out string? made)
    {
        var config = new PixiConfig { Type = kind == "sprite" ? 1 : 2, AsmFile = name.Trim() + ".asm" };
        config.Collection.Add(new PixiConfig.CollectionEntry { Name = name.Trim() });
        return AddSprite(name, new ProjectFile.SpriteEntry { Kind = kind, Config = config.ToJson(), Source = SpriteTemplate(name.Trim(), kind) }, out made);
    }

    /// <summary>Add one sprite from disk: a .json or .cfg, the ASM it names and what that includes.
    /// Its kind comes from the folder it sits in, as PIXI's would (shooters/, generators/).</summary>
    public string ImportCustomSpriteFile(string path, out string? made)
    {
        made = null;
        string folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "").ToLowerInvariant();
        string kind = folder switch { "shooters" => "shooter", "generators" => "generator", _ => "sprite" };
        ProjectFile.SpriteEntry? entry = null;
        List<string> missing = [];
        try
        {
            if (!Guard("read the sprite", () => entry = PixiImport.ReadSprite(path, kind, out missing), path)) return Status;
        }
        catch (FormatException e) { return Report2($"{Path.GetFileName(path)}: {e.Message}"); }
        string status = AddSprite(Path.GetFileNameWithoutExtension(path), entry!, out made);
        if (made is null) return status;
        if (entry!.Files.Count > 0) status += $" with {entry.Files.Count} included file(s)";
        if (missing.Count > 0) status += "; not found beside it: " + string.Join(", ", missing);
        return Report2(status);
    }

    /// <summary>
    /// Import a PIXI folder: every sprite list.txt names (replacing a library sprite of the same
    /// name), its slots, and the shared files. The status counts what came in and names what did not.
    /// </summary>
    public string ImportPixiFolder(string dir)
    {
        if (Project is null) return Report2("no project open");
        if (!File.Exists(Path.Combine(dir, "list.txt"))) return Report2("that folder has no list.txt — pick PIXI's own folder");
        PixiImport.Folder? f = null;
        if (!Guard("read the PIXI folder", () => f = PixiImport.ReadFolder(dir), dir)) return Status;
        var d = Project.Data;
        foreach (var (name, entry) in f!.Sprites) d.Sprites[name] = entry;
        foreach (var (slot, name) in f.Slots) d.SpriteSlots[slot] = name;
        foreach (var (path, text) in f.Files) d.PixiFiles[path] = text;
        Project.MarkDirty();
        string status = $"imported {f.Sprites.Count} sprite(s) into {f.Slots.Count} slot(s), {f.Files.Count} shared file(s)";
        if (f.Problems.Count > 0) status += "; " + string.Join("; ", f.Problems);
        return Report2(status);
    }

    /// <summary>Put a sprite in a slot ("1A", "105:B0"), or empty the slot with a null name. The
    /// slot must suit the sprite: 00-BF a sprite, C0-CF a shooter, D0-DF a generator.</summary>
    public string SetSpriteSlot(string slot, string? name)
    {
        if (Project is null) return Report2("no project open");
        if (PixiImport.NormalSlot(slot) is not { } key) return Report2($"\"{slot}\" is not a sprite slot (00-DF, or level:B0-BF)");
        if (name is null)
            return Report2(Project.Data.SpriteSlots.Remove(key) ? Dirty($"slot {key} emptied") : $"slot {key} was empty");
        if (FindSprite(name) is not { } s) return Report2($"{name} is not in the project");
        if (PixiImport.SlotKind(key) != s.Kind) return Report2($"slot {key} holds a {PixiImport.SlotKind(key)}, and {name} is a {s.Kind}");
        if (Project.Data.SpriteSlots.GetValueOrDefault(key) == name) return Status;
        Project.Data.SpriteSlots[key] = name;
        return Report2(Dirty($"{name} in slot {key}"));
    }

    /// <summary>
    /// Give a sprite exactly the slots in <paramref name="list"/> ("1A, 105:B0", commas or spaces):
    /// slots it had and the list leaves out are emptied, the listed ones taken (from any sprite in
    /// them). Nothing changes when one listed slot is not a slot or does not suit the sprite.
    /// </summary>
    public string SetCustomSpriteSlots(string name, string list)
    {
        if (Project is null) return Report2("no project open");
        if (FindSprite(name) is not { } s) return Report2($"{name} is not in the project");
        var wanted = new List<string>();
        foreach (string raw in list.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (PixiImport.NormalSlot(raw) is not { } key) return Report2($"\"{raw}\" is not a sprite slot (00-DF, or level:B0-BF)");
            if (PixiImport.SlotKind(key) != s.Kind) return Report2($"slot {key} holds a {PixiImport.SlotKind(key)}, and {name} is a {s.Kind}");
            wanted.Add(key);
        }
        var slots = Project.Data.SpriteSlots;
        var before = slots.Where(p => p.Value == name).Select(p => p.Key).Order().ToList();
        if (before.SequenceEqual(wanted.Distinct().Order())) return Status;
        foreach (string old in before) slots.Remove(old);
        foreach (string key in wanted) slots[key] = name;
        return Report2(Dirty(wanted.Count == 0 ? $"{name} is in no slot" : $"{name} in slot(s) {string.Join(", ", wanted.Distinct().Order())}"));
    }

    /// <summary>Open a sprite's ASM in the user's text editor, as a block's opens: a temp copy,
    /// saves read back on the next <see cref="Tick"/>.</summary>
    public string OpenCustomSpriteExternally(string name)
    {
        if (Project is null) return Report2("no project open");
        if (CustomSpriteSource(name) is not { } asm) return Report2($"{name} is not in the project");
        // The temp file is named for the sprite; one from a subfolder ("sub/thing") gets no folder.
        return OpenExternally("sprite:" + name, name.Replace('/', '_') + ".asm", asm, text => SetCustomSpriteSource(name, text));
    }

    /// <summary>Take a sprite, its files and its slots out of the project.</summary>
    public string DeleteCustomSprite(string name)
    {
        if (Project is null) return Report2("no project open");
        if (!Project.Data.Sprites.Remove(name)) return Report2($"{name} is not in the project");
        foreach (var slot in Project.Data.SpriteSlots.Where(s => s.Value == name).Select(s => s.Key).ToList())
            Project.Data.SpriteSlots.Remove(slot);
        return Report2(Dirty($"deleted {name}"));
    }

    private ProjectFile.SpriteEntry? FindSprite(string name) => Project?.Data.Sprites.GetValueOrDefault(name);

    private string Dirty(string status) { Project!.MarkDirty(); return status; }

    private string AddSprite(string name, ProjectFile.SpriteEntry entry, out string? made)
    {
        made = null;
        if (Project is null) return Report2("no project open");
        name = name.Trim();
        if (name.Length == 0 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or ' ')))
            return Report2("a sprite name is letters, digits, spaces, _ and -");
        if (Project.Data.Sprites.ContainsKey(name)) return Report2($"{name} is already in the project");
        Project.Data.Sprites[name] = entry;
        made = name;
        return Report2(Dirty($"added {name}"));
    }

    /// <summary>A sprite that does nothing yet: the two entry points PIXI's format asks for, as
    /// prints. A shooter or generator has only MAIN that matters, but INIT is still read.</summary>
    internal static string SpriteTemplate(string name, string kind) => $"""
        ; {name}
        ;
        ; A custom {kind}. INIT runs once when it spawns, MAIN every frame; both end in RTL and are
        ; entered with the data bank unset, so set it yourself (PHB : PHK : PLB ... PLB).

        print "INIT ",pc
            RTL

        print "MAIN ",pc
            PHB : PHK : PLB
            PLB
            RTL

        """;
}
