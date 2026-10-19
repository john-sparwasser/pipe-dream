using System.Text;
using System.Text.RegularExpressions;

namespace PipeDream;

/// <summary>
/// Applies a project's Asar patches (reference/PATCHES.md). Every build starts from a fresh copy
/// of the base ROM, so a patch is applied, never re-applied: disabling or deleting one takes it
/// out at the next build with nothing to clean. Each patch is its own Asar run, so an error
/// names the patch it came from, and each sees only its own included files, laid out beside it
/// in memory as they were beside it on disk.
/// </summary>
public static class PatchInsert
{
    public sealed record Result(bool Ok, string[] Errors, string[] Warnings, Asar.WrittenBlock[] Written);

    /// <summary>Bytes someone already wrote: Pipe Dream's ROM hooks, the block driver, an earlier
    /// patch. <paramref name="Pc"/> is the headerless PC offset, so two SNES mirrors of
    /// one byte compare equal.</summary>
    public readonly record struct Owned(string Owner, int Pc, int Length);

    /// <summary>Where a patch's in-memory folder sits. Nothing is written there: Asar finds memory
    /// files before the disk, and this only gives their paths a folder.</summary>
    private static readonly string VirtualDir = Path.Combine(Path.GetTempPath(), "pipedream-patches");

    /// <summary>Assemble <paramref name="patch"/> onto <paramref name="rom"/>. On an error the ROM
    /// is unchanged. Messages name files relative to the patch, as they were on disk.</summary>
    public static Result Apply(Rom rom, ProjectFile.PatchEntry patch)
    {
        string dir = Path.Combine(VirtualDir, Path.GetFileNameWithoutExtension(patch.Name));
        string root = Path.Combine(dir, patch.Name);
        var mem = patch.Files.ToDictionary(kv => Path.Combine(dir, kv.Key), kv => kv.Value);
        mem[root] = Encoding.UTF8.GetBytes(patch.Source);
        var r = Asar.Patch(rom, root, mem);
        return new Result(r.Ok, Local(r.Errors, dir), Local(r.Warnings, dir), r.Blocks);
    }

    /// <summary>The messages with the in-memory folder taken off their paths. Asar writes them
    /// lowercased and with forward slashes on Windows, so the folder is found that way.</summary>
    private static string[] Local(string[] messages, string dir)
    {
        string folder = Path.GetFullPath(dir).Replace('\\', '/') + "/";
        return [.. messages.Select(m => m.Replace('\\', '/').IndexOf(folder, StringComparison.OrdinalIgnoreCase) is var i and >= 0
                                         ? m.Remove(i, folder.Length) : m)];
    }

    /// <summary>What a patch says it is: its first line, when that is a comment. A comment that is
    /// only the patch's own name (the template's) says nothing the name column doesn't.</summary>
    public static string? Description(string name, string asm)
        => asm.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } first
           && first.StartsWith(';') && first.TrimStart(';').Trim() is { Length: > 0 } d
           && !d.Equals(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)
           && !d.Equals(name, StringComparison.OrdinalIgnoreCase) ? d : null;

    /// <summary>
    /// The files a patch on disk pulls in — incsrc'd text and incbin'd data — by path relative to
    /// the patch, following includes of includes. The build assembles from memory, so whatever the
    /// patch found beside it on disk has to be carried. An include that leaves the patch's folder,
    /// or names a file that is not there (a path built from a define, say), is not carried; those
    /// come back in <paramref name="missing"/>, and the build will say so if Asar needs them.
    /// </summary>
    public static Dictionary<string, byte[]> Includes(string patchPath, out List<string> missing)
    {
        string full = Path.GetFullPath(patchPath), root = Path.GetDirectoryName(full)!, self = Path.GetFileName(full);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        missing = [];
        var work = new Queue<(string Rel, string Text)>([(self, File.ReadAllText(full))]);
        while (work.TryDequeue(out var file))
            foreach (Match m in Include.Matches(file.Text))
            {
                string named = (m.Groups[2].Success ? m.Groups[2] : m.Groups[3]).Value.Trim();
                string path = Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(file.Rel) ?? "", named));
                string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (rel.Equals(self, StringComparison.OrdinalIgnoreCase) || files.ContainsKey(rel)) continue;
                if (rel.StartsWith("..") || Path.IsPathRooted(rel) || !File.Exists(path))
                {
                    if (!missing.Contains(named)) missing.Add(named);
                    continue;
                }
                files[rel] = File.ReadAllBytes(path);
                if (m.Groups[1].Value.Equals("incsrc", StringComparison.OrdinalIgnoreCase))
                    work.Enqueue((rel, Encoding.UTF8.GetString(files[rel])));
            }
        return files;
    }

    /// <summary>incsrc / incbin and the file they name, quoted or bare. A bare name stops at a
    /// colon, which is incbin's range (<c>incbin data.bin:0-100</c>).</summary>
    private static readonly Regex Include = new(@"^[ \t]*(incsrc|incbin)[ \t]+(?:""([^""\r\n]+)""|([^\s;:""]+))",
                                                RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Where <paramref name="written"/> lands on bytes in <paramref name="owned"/>: one line per
    /// written block and owner, at the first byte they share. Overlaps are reported, not refused —
    /// some patches mean to replace a hook — so an accidental one has to be findable from here.
    /// </summary>
    public static List<string> Overlaps(IReadOnlyList<Owned> owned, Asar.WrittenBlock[] written)
    {
        var lines = new List<string>();
        foreach (var w in written)
        {
            int start = Rom.SnesToPc(w.Snes), end = start + w.Length;
            foreach (var g in owned.Where(o => o.Pc < end && start < o.Pc + o.Length).GroupBy(o => o.Owner))
                lines.Add($"writes ${Rom.PcToSnes(g.Min(o => Math.Max(o.Pc, start))):X6}, which {g.Key} also writes");
        }
        const int Shown = 5;
        return lines.Count <= Shown ? lines : [.. lines.Take(Shown), $"and {lines.Count - Shown} more overlap(s)"];
    }
}
