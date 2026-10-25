using System.Text;

namespace PipeDream;

/// <summary>
/// The SA-1 compatibility report over a whole project (reference/SA1_PORT.md, phase 4): every
/// resource the build assembles, scanned by <see cref="Sa1Compat"/> with its own files and the CPU its
/// code runs on under the pack — sprites, blocks and the status bar on the SA-1 CPU, UberASM's code
/// points on the SNES CPU, a patch on whichever calls it. Songs run on the sound chip and are not
/// in it. The confirmation for ASM ▸ SA-1, each build's warnings and the windows' badges all read this.
/// </summary>
public static class Sa1Report
{
    /// <summary>One resource: its kind ("patch", "block", "sprite", "UberASM"), its name, and what its code holds.</summary>
    public sealed record Item(string Kind, string Name, Sa1Compat.Result Result);

    public static IReadOnlyList<Item> Of(ProjectFile data)
    {
        var items = new List<Item>();
        foreach (var p in data.Patches.Where(p => p.Enabled))
            items.Add(new("patch", p.Name, Patch(p)));
        foreach (string name in data.Blocks.Values.Distinct().Order(StringComparer.Ordinal))
            if (data.BlockLibrary.TryGetValue(name, out var asm))
                items.Add(new("block", name, Block(asm, name)));
        if (data.GpsFiles.Count > 0)
            items.Add(new("block", "GPS files", Sa1Compat.Scan(data.GpsFiles.Where(f => IsAsm(f.Key)).Select(f => (f.Key, f.Value)), Sa1Compat.Cpu.Sa1)));
        foreach (string name in data.SpriteSlots.Values.Distinct().Order(StringComparer.Ordinal))
            if (data.Sprites.TryGetValue(name, out var s))
                items.Add(new("sprite", name, Sprite(s, name)));
        if (data.PixiFiles.Count > 0)
            items.Add(new("sprite", "PIXI files", Sa1Compat.Scan(data.PixiFiles.Where(f => IsAsm(f.Key)).Select(f => (f.Key, f.Value)), Sa1Compat.Cpu.Sa1)));
        foreach (string path in data.Uber.Files.Keys.Where(IsAsm).Order(StringComparer.Ordinal))
            items.Add(new("UberASM", path, Uber(data.Uber, path)));
        return items;
    }

    /// <summary>One UberASM file, as its badge reads it: the status bar's code runs on the SA-1 CPU, the rest on the SNES CPU.</summary>
    public static Sa1Compat.Result Uber(UberState uber, string path)
        => Sa1Compat.Scan([(path, Encoding.UTF8.GetString(uber.Files.GetValueOrDefault(path) ?? []))],
                          path == uber.StatusBar ? Sa1Compat.Cpu.Sa1 : Sa1Compat.Cpu.Snes);

    public static Sa1Compat.Result Patch(ProjectFile.PatchEntry p) => Sa1Compat.Scan(WithFiles(p.Name, p.Source, p.Files), Sa1Compat.Cpu.Either);
    public static Sa1Compat.Result Block(string asm, string name) => Sa1Compat.Scan([(name, asm)], Sa1Compat.Cpu.Sa1);
    public static Sa1Compat.Result Sprite(ProjectFile.SpriteEntry s, string name) => Sa1Compat.Scan(WithFiles(name + ".asm", s.Source, s.Files), Sa1Compat.Cpu.Sa1);

    /// <summary>A resource's badge: with SA-1 on, how it fares there; off, only "needs SA-1" — a LoROM
    /// project's lists stay quiet, and what would break is the switch's confirmation to say.</summary>
    public static string? Badge(Sa1Compat.Result r, bool sa1On)
        => !sa1On ? (r.NeedsSa1Build ? "needs SA-1" : null)
         : r.OnSa1 switch
         {
             Sa1Compat.Fit.Breaks => "breaks on SA-1",
             Sa1Compat.Fit.Check => "SA-1: check",
             _ => "SA-1 ready",
         };

    /// <summary>The badge's tooltip: what was found, a line each, at most <paramref name="max"/>.</summary>
    public static string Details(Sa1Compat.Result r, bool sa1On, int max = 8)
    {
        var lines = (sa1On || !r.NeedsSa1Build ? r.Breaks.Concat(r.Check) : r.NeedsSa1).Select(f => f.ToString()).ToList();
        if (lines.Count == 0) return sa1On ? "Nothing in it addresses what SA-1 moves." : "";
        return string.Join("\n", lines.Take(max)) + (lines.Count > max ? $"\nand {lines.Count - max} more" : "");
    }

    /// <summary>What the report says for the switch going to <paramref name="on"/>: a line per resource
    /// that breaks or may (on), or that needs SA-1 (off), at most <paramref name="maxItems"/> a section;
    /// empty when there is nothing to say.</summary>
    public static string Summary(IReadOnlyList<Item> items, bool on, int perItem = 3, int maxItems = int.MaxValue)
    {
        var sb = new StringBuilder();
        void Section(string title, IEnumerable<Item> which, Func<Item, IReadOnlyList<Sa1Compat.Finding>> findings)
        {
            var all = which.ToList();
            if (all.Count == 0) return;
            sb.Append(title).Append('\n');
            int more = Math.Max(0, all.Count - maxItems);
            foreach (var it in all.Take(maxItems))
            {
                var f = findings(it);
                sb.Append($"• {it.Kind} {it.Name}: ").Append(string.Join("; ", f.Take(perItem).Select(x => $"line {x.Line}: {x.Code} — {x.Why}")));
                if (f.Count > perItem) sb.Append($"; and {f.Count - perItem} more");
                sb.Append('\n');
            }
            if (more > 0) sb.Append($"and {more} more (the build lists every one)\n");
        }
        if (on)
        {
            Section("Breaks on SA-1:", items.Where(i => i.Result.OnSa1 == Sa1Compat.Fit.Breaks), i => i.Result.Breaks);
            Section("May not work on SA-1 (check):", items.Where(i => i.Result.OnSa1 == Sa1Compat.Fit.Check), i => i.Result.Check);
        }
        else Section("Needs SA-1:", items.Where(i => i.Result.NeedsSa1Build), i => i.Result.NeedsSa1);
        return sb.ToString().TrimEnd();
    }

    /// <summary>The build's warnings from the report: one line per resource, for the switch as it is.</summary>
    public static IEnumerable<string> Warnings(IReadOnlyList<Item> items, bool on)
        => on
            ? items.Where(i => i.Result.OnSa1 != Sa1Compat.Fit.Ready).Select(i =>
                $"SA-1: {i.Kind} {i.Name} {(i.Result.OnSa1 == Sa1Compat.Fit.Breaks ? "breaks" : "may not work")} ({First(i.Result.OnSa1 == Sa1Compat.Fit.Breaks ? i.Result.Breaks : i.Result.Check)})")
            : items.Where(i => i.Result.NeedsSa1Build).Select(i => $"{i.Kind} {i.Name} needs SA-1 ({First(i.Result.NeedsSa1)})");

    private static string First(IReadOnlyList<Sa1Compat.Finding> f)
        => $"{f[0].File}:{f[0].Line}: {f[0].Code} — {f[0].Why}" + (f.Count > 1 ? $"; {f.Count - 1} more" : "");

    private static bool IsAsm(string path) => path.EndsWith(".asm", StringComparison.OrdinalIgnoreCase);

    /// <summary>A source and its included files that are ASM, as the scanner takes them.</summary>
    private static IEnumerable<(string, string)> WithFiles(string name, string source, Dictionary<string, byte[]> files)
        => files.Where(f => IsAsm(f.Key)).Select(f => (f.Key, Encoding.UTF8.GetString(f.Value))).Prepend((name, source));
}
