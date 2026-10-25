namespace PipeDream.Services;

// EditorSession — patches: the project's Asar patches (ProjectFile.Patches), in the order the
// build applies them, each enabled or not (reference/PATCHES.md). Like the block library, these
// edits mark the project dirty and are not undoable.
// The rest of the class: the other EditorSession.*.cs files.
public sealed partial class EditorSession
{
    /// <summary>One patch as the Patches window lists it, with its SA-1 badge and why (EditorSession.Sa1).</summary>
    public sealed record PatchInfo(string Name, bool Enabled, string? Description, int Files, string? Sa1 = null, string? Sa1Tip = null);

    /// <summary>The project's patches, in build order.</summary>
    public IReadOnlyList<PatchInfo> Patches =>
        Project?.Data.Patches.Select(p =>
        {
            var (sa1, tip) = Sa1Badge(Sa1Report.Patch(p));
            return new PatchInfo(p.Name, p.Enabled, PatchInsert.Description(p.Name, p.Source), p.Files.Count, sa1, tip);
        }).ToList() ?? [];

    /// <summary>A patch's ASM, or null.</summary>
    public string? PatchSource(string name) => FindPatch(name)?.Source;

    /// <summary>Replace a patch's ASM. An unchanged text is not an edit.</summary>
    public void SetPatchSource(string name, string asm)
    {
        if (Project is null || FindPatch(name) is not { } p || p.Source == asm) return;
        p.Source = asm;
        Project.MarkDirty();
    }

    /// <summary>Add a patch from the template, enabled, at the end of the list.</summary>
    public string NewPatch(string name, out string? made) => AddPatch(name, null, [], out made);

    /// <summary>Add an .asm file from disk under its file name, with every file it includes from
    /// beside it (<see cref="PatchInsert.Includes"/>). Characters a patch name cannot have become
    /// _, since downloaded patches are often "name v1.2.asm". The status names any include not
    /// found.</summary>
    public string ImportPatchFile(string path, out string? made)
    {
        made = null;
        string? asm = null;
        Dictionary<string, byte[]> files = [];
        List<string> missing = [];
        if (!Guard("read the patch", () => { asm = File.ReadAllText(path); files = PatchInsert.Includes(path, out missing); }, path))
            return Status;
        string name = string.Concat(Path.GetFileNameWithoutExtension(path).Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
        string status = AddPatch(name, asm, files, out made);
        if (made is null) return status;
        if (files.Count > 0) status += $" with {files.Count} included file(s)";
        if (missing.Count > 0) status += "; not found beside it: " + string.Join(", ", missing);
        return Report2(status);
    }

    /// <summary>Open a patch in the user's text editor, as a block opens
    /// (<see cref="OpenBlockExternally"/>): a temp copy, saves read back on the next <see cref="Tick"/>.</summary>
    public string OpenPatchExternally(string name)
    {
        if (Project is null) return Report2("no project open");
        if (PatchSource(name) is not { } asm) return Report2($"{name} is not in the project");
        return OpenExternally("patch:" + name, name, asm, text => SetPatchSource(name, text));
    }

    /// <summary>Turn a patch on or off. Off keeps it in the project and out of the build.</summary>
    public string SetPatchEnabled(string name, bool enabled)
    {
        if (Project is null) return Report2("no project open");
        if (FindPatch(name) is not { } p) return Report2($"{name} is not in the project");
        if (p.Enabled == enabled) return Status;
        p.Enabled = enabled;
        Project.MarkDirty();
        return Report2($"{name} {(enabled ? "enabled" : "disabled")}");
    }

    /// <summary>Move a patch <paramref name="by"/> places in the build order (negative = earlier),
    /// stopping at either end.</summary>
    public string MovePatch(string name, int by)
    {
        if (Project is null) return Report2("no project open");
        var list = Project.Data.Patches;
        int from = list.FindIndex(p => p.Name == name);
        if (from < 0) return Report2($"{name} is not in the project");
        int to = Math.Clamp(from + by, 0, list.Count - 1);
        if (to == from) return Status;
        var p = list[from];
        list.RemoveAt(from);
        list.Insert(to, p);
        Project.MarkDirty();
        return Report2($"{name} moved to {to + 1} of {list.Count}");
    }

    /// <summary>Take a patch, and the files it includes, out of the project.</summary>
    public string DeletePatch(string name)
    {
        if (Project is null) return Report2("no project open");
        if (Project.Data.Patches.RemoveAll(p => p.Name == name) == 0) return Report2($"{name} is not in the project");
        Project.MarkDirty();
        return Report2($"deleted {name}");
    }

    private ProjectFile.PatchEntry? FindPatch(string name) => Project?.Data.Patches.Find(p => p.Name == name);

    private string AddPatch(string name, string? asm, Dictionary<string, byte[]> files, out string? made)
    {
        made = null;
        if (Project is null) return Report2("no project open");
        name = name.Trim();
        if (name.EndsWith(".asm", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-')))
            return Report2("a patch name is letters, digits, _ and -");
        if (FindPatch(name + ".asm") is not null) return Report2($"{name}.asm is already in the project");
        made = name + ".asm";
        Project.Data.Patches.Add(new ProjectFile.PatchEntry { Name = made, Source = asm ?? PatchTemplate(name), Files = files });
        Project.MarkDirty();
        return Report2($"added {made}");
    }

    /// <summary>A patch that does nothing yet, saying how Pipe Dream applies it.</summary>
    internal static string PatchTemplate(string name) => $"""
        ; {name}
        ;
        ; An Asar patch. Pipe Dream applies it at build, onto a fresh copy of the base ROM, after
        ; custom blocks and before the levels and everything else the editor writes. Patches apply
        ; in list order. There is no cleanup to write: every build starts from the base again.
        ;
        ; Hijack with org, and put new code in freecode:
        ;
        ;   org $xxxxxx
        ;       autoclean JSL Hook
        ;   freecode
        ;   Hook:
        ;       RTL

        """;
}
