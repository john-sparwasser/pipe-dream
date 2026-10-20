using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PipeDream;

/// <summary>
/// Inserts a project's custom sprites at build (reference/PIXI_PORT.md, phase 2), PIXI's job done
/// our way: the size table gets each slotted sprite's record lengths, the shared routines the
/// sprites call go in once, each sprite is assembled on its own (so an error names it) and hands
/// back INIT and MAIN as Asar prints, and our driver (data/spritedriver) goes in last with the
/// table those make. Every build starts from the base, so nothing is ever cleaned up.
/// </summary>
public static partial class SpriteInsert
{
    public sealed record Result(bool Ok, string[] Errors, string[] Warnings, Asar.WrittenBlock[] Written);

    /// <summary>Where the in-memory files sit. Nothing is written there; see PatchInsert.</summary>
    private static readonly string VirtualDir = Path.Combine(Path.GetTempPath(), "pipedream-sprites");

    /// <summary>An empty table entry's INIT and MAIN: an RTL in bank 01, as PIXI fills them.</summary>
    private const int EmptyEntry = 0x018021;

    /// <summary>Insert every sprite in a global slot onto <paramref name="rom"/>. On an error the
    /// result says which sprite; what was already written stays (the build is then discarded).</summary>
    public static Result Insert(Rom rom, ProjectFile data)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var written = new List<Asar.WrittenBlock>();
        Result Fail(string e) => new(false, [e], [.. warnings], [.. written]);

        var (slots, perLevel) = Slots(data, warnings);
        if (slots.Count == 0 && perLevel.Count == 0) return new(true, [], [.. warnings], []);
        if (ForeignDriver(rom) is { } foreign) return Fail(foreign);
        if (LoaderStep(rom, out int lmStep) is { } loader) return Fail(loader);

        var configs = new Dictionary<string, PixiConfig>();
        var names = slots.Values.Concat(perLevel.Values.SelectMany(l => l.Values)).Distinct().ToList();
        foreach (string name in names)
        {
            try { configs[name] = PixiConfig.Parse(data.Sprites[name].Config); }
            catch (FormatException e) { return Fail($"sprite {name}: config: {e.Message}"); }
        }
        // Custom placements have extra bits 2 (custom) or 3 (custom with the extra bit), and the
        // loader steps each record by the table's length for those. A record's length is per
        // number in every level, so with per-level sprites B0-BF are all 4 extra bytes, as PIXI has
        // them, and a per-level sprite cannot ask for more.
        foreach (var (n, name) in slots)
        {
            rom.SetSpriteEntrySize(2, n, 3 + configs[name].ExtraBytes);
            rom.SetSpriteEntrySize(3, n, 3 + configs[name].ExtraBytesSet);
        }
        if (perLevel.Count > 0)
        {
            foreach (var (level, numbers) in perLevel)
                foreach (var (n, name) in numbers)
                    if (Math.Max(configs[name].ExtraBytes, configs[name].ExtraBytesSet) > PerLevelExtraBytes)
                        return Fail($"sprite {name} in level {level:X3}'s slot {n:X2}: a per-level sprite has at most {PerLevelExtraBytes} extra bytes");
            for (int n = 0xB0; n <= 0xBF; n++)
            {
                rom.SetSpriteEntrySize(2, n, 3 + PerLevelExtraBytes);
                rom.SetSpriteEntrySize(3, n, 3 + PerLevelExtraBytes);
            }
        }

        var mem = new Dictionary<string, byte[]>();
        void Put(string path, string text) => mem[Path.Combine(VirtualDir, path)] = Encoding.UTF8.GetBytes(text);
        Put("defines.asm", Driver("defines.asm"));
        foreach (var (path, text) in data.PixiFiles) Put(path, text);

        var sources = names.Where(n => configs[n].Type != 0).ToDictionary(n => n, n => data.Sprites[n]);
        var routines = InsertRoutines(rom, data.PixiFiles, sources.Values, mem, written, out string? routineError);
        if (routineError is not null) return Fail(routineError);
        Put("routines.asm", string.Concat(routines.Select(r => $"macro {r.Key}()\n    JSL ${r.Value:X6}\nendmacro\n")));

        var entries = new Dictionary<string, (int Init, int Main, int[] Status)>();
        int i = 0;
        foreach (var (name, entry) in sources)
        {
            string dir = $"sprite{i++}";
            foreach (var (path, bytes) in entry.Files) mem[Path.Combine(VirtualDir, dir, path)] = bytes;
            string file = string.IsNullOrEmpty(configs[name].AsmFile) ? "sprite.asm" : Path.GetFileName(configs[name].AsmFile);
            Put(Path.Combine(dir, file), entry.Source);
            Put("_sprite.asm", Wrapper(data.PixiFiles, PixiImport.FolderOf(entry.Kind), $"{dir}/{file}"));
            var r = Asar.Patch(rom, Path.Combine(VirtualDir, "_sprite.asm"), mem);
            if (!r.Ok) return Fail($"sprite {name}: " + string.Join("; ", Local(r.Errors)));
            warnings.AddRange(Local(r.Warnings).Select(w => $"sprite {name}: {w}"));
            written.AddRange(r.Blocks);
            // A shooter or generator only runs MAIN; its INIT, if it prints one, is kept anyway.
            int? init = Entry(r, "INIT"), main = Entry(r, "MAIN");
            if (main is null || init is null && entry.Kind == "sprite")
                return Fail($"sprite {name}: no {(main is null ? "MAIN" : "INIT")} — the sprite must print it (print \"MAIN \",pc)");
            entries[name] = (init ?? EmptyEntry, main.Value, [.. StatusEntries.Select(s => Entry(r, s) ?? 0)]);
        }

        Put("main.asm", Driver("main.asm"));
        Put("_driver.asm", $"""
            incsrc "defines.asm"
            !SizeTable = ${rom.LmSpriteSizeBase:X6}
            !LmStep = ${lmStep:X6}
            incsrc "main.asm"
            freedata
            SpriteTable:
            {Table(rom, slots, perLevel, configs, entries)}
            freedata
            StatusTable:
            {StatusTable(slots, perLevel, entries)}
            freedata
            LevelBlocks:
            {LevelBlocks(perLevel)}
            """);
        var d = Asar.Patch(rom, Path.Combine(VirtualDir, "_driver.asm"), mem);
        if (!d.Ok) return Fail("the sprite driver: " + string.Join("; ", Local(d.Errors)));
        written.AddRange(d.Blocks);
        return new(true, [], [.. warnings], [.. written]);
    }

    /// <summary>Extra bytes every B0-BF record carries once a project has per-level sprites.</summary>
    private const int PerLevelExtraBytes = 4;

    /// <summary>The slots to insert: global number → sprite, and level → (B0-BF → sprite). A slot
    /// naming a sprite the library lacks, or holding the wrong kind, is skipped with a warning.</summary>
    private static (SortedDictionary<int, string> Global, SortedDictionary<int, SortedDictionary<int, string>> PerLevel)
        Slots(ProjectFile data, List<string> warnings)
    {
        var global = new SortedDictionary<int, string>();
        var perLevel = new SortedDictionary<int, SortedDictionary<int, string>>();
        foreach (var (slot, name) in data.SpriteSlots)
        {
            if (!data.Sprites.TryGetValue(name, out var s)) { warnings.Add($"slot {slot} names {name}, which is not in the project"); continue; }
            if (PixiImport.SlotKind(slot) is not { } kind || kind != s.Kind) { warnings.Add($"slot {slot} cannot hold {name}, a {s.Kind}"); continue; }
            int colon = slot.IndexOf(':');
            if (colon < 0) { global[Convert.ToInt32(slot, 16)] = name; continue; }
            int level = Convert.ToInt32(slot[..colon], 16);
            if (!perLevel.TryGetValue(level, out var numbers)) perLevel[level] = numbers = [];
            numbers[Convert.ToInt32(slot[(colon + 1)..], 16)] = name;
        }
        return (global, perLevel);
    }

    /// <summary>A base that already carries PIXI (or our driver) has its own table and hooks;
    /// building ours over them would leave two drivers half-installed.</summary>
    private static string? ForeignDriver(Rom rom)
        => rom.Data.AsSpan(rom.FileOffset(0x02FFE2), 4).SequenceEqual("STSD"u8)
            ? "the base ROM already has PIXI's sprite driver — custom sprites need a base without it" : null;

    /// <summary>What $02A846 does in this base: SMW's own step (<paramref name="lmStep"/> 0), or a
    /// JML to Lunar Magic's, which our step chains into past its first three bytes.</summary>
    private static string? LoaderStep(Rom rom, out int lmStep)
    {
        lmStep = 0;
        var at = rom.Data.AsSpan(rom.FileOffset(0x02A846), 5);
        if (at.SequenceEqual((byte[])[0xC8, 0xC8, 0xE8, 0x80, 0xE3])) return null;      // INY : INY : INX : BRA
        if (at[0] == 0x5C)
        {
            int target = at[1] | at[2] << 8 | at[3] << 16;
            if (rom.Data.AsSpan(rom.FileOffset(target), 3).SequenceEqual((byte[])[0xE8, 0xC8, 0xC8]))   // INX : INY : INY
            { lmStep = target; return null; }
        }
        return "the base ROM's sprite loader at $02A846 is not SMW's or Lunar Magic's, so custom sprites cannot hook it";
    }

    /// <summary>
    /// Put every routine the sprites call (%Name(), and the routines those call) into the ROM once,
    /// in one patch, and return name → address. A routine is a file under routines/; its macro is
    /// its path with the folders run together, as PIXI names it: routines/Spawn/Smoke.asm is
    /// %SpawnSmoke().
    /// </summary>
    private static Dictionary<string, int> InsertRoutines(Rom rom, Dictionary<string, string> files,
        IEnumerable<ProjectFile.SpriteEntry> sprites, Dictionary<string, byte[]> mem, List<Asar.WrittenBlock> written, out string? error)
    {
        error = null;
        var byName = files.Keys.Where(p => p.StartsWith("routines/") && p.EndsWith(".asm", StringComparison.OrdinalIgnoreCase))
                               .ToDictionary(p => p["routines/".Length..^4].Replace("/", ""), p => p);
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var todo = new Stack<string>(sprites.SelectMany(s => s.Files.Values.Select(b => Encoding.UTF8.GetString(b)).Append(s.Source)));
        while (todo.TryPop(out var text))
            foreach (Match m in MacroCall().Matches(text))
                if (byName.ContainsKey(m.Groups[1].Value) && used.Add(m.Groups[1].Value)) todo.Push(files[byName[m.Groups[1].Value]]);
        if (used.Count == 0) return [];

        var src = new StringBuilder("incsrc \"defines.asm\"\n");
        foreach (string extra in ExtraDefines(files)) src.Append($"incsrc \"{extra}\"\n");
        foreach (string r in used) src.Append($"macro {r}()\n    JSL PixiRoutine_{r}\nendmacro\n");
        foreach (string r in used) src.Append($"freecode\nPixiRoutine_{r}:\nincsrc \"{byName[r]}\"\n");
        mem[Path.Combine(VirtualDir, "_routines.asm")] = Encoding.UTF8.GetBytes(src.ToString());
        var res = Asar.Patch(rom, Path.Combine(VirtualDir, "_routines.asm"), mem);
        if (!res.Ok) { error = "shared routines: " + string.Join("; ", Local(res.Errors)); return []; }
        written.AddRange(res.Blocks);
        return used.ToDictionary(r => r, r => res.Labels[$"PixiRoutine_{r}"]);
    }

    [GeneratedRegex(@"%([A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex MacroCall();

    private static IEnumerable<string> ExtraDefines(Dictionary<string, string> files)
        => files.Keys.Where(p => p.StartsWith("ExtraDefines/")).Order(StringComparer.Ordinal);

    /// <summary>What a sprite is assembled inside: the defines, the user's extra defines, the
    /// routine macros, its folder's _header.asm, then the sprite in freespace.</summary>
    private static string Wrapper(Dictionary<string, string> files, string folder, string sprite)
    {
        var s = new StringBuilder("incsrc \"defines.asm\"\n");
        foreach (string extra in ExtraDefines(files)) s.Append($"incsrc \"{extra}\"\n");
        s.Append("incsrc \"routines.asm\"\n");
        if (files.ContainsKey($"{folder}/_header.asm")) s.Append($"incsrc \"{folder}/_header.asm\"\n");
        s.Append($"freecode\nincsrc \"{sprite}\"\n");
        return s.ToString();
    }

    /// <summary>An entry point the sprite printed ("INIT 1A8000", as PIXI reads it), else a label
    /// of that name.</summary>
    private static int? Entry(Asar.Result r, string name)
    {
        foreach (string p in r.Prints)
        {
            string t = p.Trim();
            if (t.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(t[name.Length..].Trim().TrimStart('$'), NumberStyles.HexNumber, null, out int v)) return v;
        }
        return r.Labels.FirstOrDefault(l => l.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } l ? l.Value : null;
    }

    /// <summary>The table as db lines: 0x100 global entries, then 0x10 (B0-BF) per level with
    /// per-level sprites, in level order. A number with no sprite is a tweak of itself with SMW's
    /// own tweakers, so a stray custom placement acts as the vanilla sprite would. A per-level
    /// number a level does not fill falls back to the global entry.</summary>
    private static string Table(Rom rom, SortedDictionary<int, string> slots, SortedDictionary<int, SortedDictionary<int, string>> perLevel,
                                Dictionary<string, PixiConfig> configs, Dictionary<string, (int Init, int Main, int[] Status)> entries)
    {
        var sb = new StringBuilder();
        for (int n = 0; n < 0x100; n++) sb.Append(EntryLine(rom, n, slots.GetValueOrDefault(n), configs, entries));
        foreach (var (level, numbers) in perLevel)
            for (int n = 0xB0; n <= 0xBF; n++)
                sb.Append(EntryLine(rom, n, numbers.GetValueOrDefault(n) ?? slots.GetValueOrDefault(n), configs, entries, $"level {level:X3}"));
        return sb.ToString();
    }

    /// <summary>The status entry points a sprite may print, in StatusTable's order (the driver's
    /// StatusSlot): states 09, 0A, 0B, 07 and 0C, which then run its code in place of SMW's.</summary>
    private static readonly string[] StatusEntries = ["CARRIABLE", "KICKED", "CARRIED", "MOUTH", "GOAL"];

    /// <summary>StatusTable as db lines, laid out as the sprite table: per entry the five status
    /// entry points (0 where the sprite has none), then a pad byte.</summary>
    private static string StatusTable(SortedDictionary<int, string> slots, SortedDictionary<int, SortedDictionary<int, string>> perLevel,
                                      Dictionary<string, (int Init, int Main, int[] Status)> entries)
    {
        string Line(string? name)
        {
            int[] s = name is not null && entries.TryGetValue(name, out var e) ? e.Status : new int[StatusEntries.Length];
            return "db " + string.Join(',', s.SelectMany(p => new[] { p & 0xFF, p >> 8 & 0xFF, p >> 16 }).Append(0).Select(b => $"${b:X2}")) + "\n";
        }
        var sb = new StringBuilder();
        for (int n = 0; n < 0x100; n++) sb.Append(Line(slots.GetValueOrDefault(n)));
        foreach (var (_, numbers) in perLevel)
            for (int n = 0xB0; n <= 0xBF; n++) sb.Append(Line(numbers.GetValueOrDefault(n) ?? slots.GetValueOrDefault(n)));
        return sb.ToString();
    }

    /// <summary>Per level, where its block starts, less B0's offset within a block (0 = none), so
    /// the driver adds number * 16 as it does for the global table.</summary>
    private static string LevelBlocks(SortedDictionary<int, SortedDictionary<int, string>> perLevel)
    {
        var at = new int[0x200];
        int k = 0;
        foreach (int level in perLevel.Keys) at[level] = 0x1000 + k++ * 0x100 - 0xB00;
        return string.Join('\n', at.Chunk(16).Select(row => "dw " + string.Join(',', row.Select(v => $"${v:X4}"))));
    }

    private static string EntryLine(Rom rom, int n, string? name, Dictionary<string, PixiConfig> configs,
                                    Dictionary<string, (int Init, int Main, int[] Status)> entries, string? note = null)
    {
        byte[] e;
        if (name is not null)
        {
            var c = configs[name];
            var (init, main, _) = entries.TryGetValue(name, out var p) ? p : (EmptyEntry, EmptyEntry, []);
            e = [(byte)c.Type, (byte)c.ActLike, .. c.Tweak, (byte)init, (byte)(init >> 8), (byte)(init >> 16),
                 (byte)main, (byte)(main >> 8), (byte)(main >> 16), c.Prop1, c.Prop2];
        }
        else
        {
            e = new byte[16];
            e[1] = (byte)n;
            if (n < VanillaSprites)
                for (int t = 0; t < 6; t++) e[2 + t] = rom.ReadByte(VanillaTweakers[t] + n);
            e[8] = e[11] = EmptyEntry & 0xFF; e[9] = e[12] = EmptyEntry >> 8 & 0xFF; e[10] = e[13] = EmptyEntry >> 16;
        }
        return $"db {string.Join(',', e.Select(b => $"${b:X2}"))} ; {(note is null ? "" : note + " ")}{n:X2}\n";
    }

    /// <summary>SMW's sprite numbers, 00-C8, and its six tweaker tables ($07F7A0 reads them).</summary>
    private const int VanillaSprites = 0xC9;
    private static readonly int[] VanillaTweakers = [0x07F26C, 0x07F335, 0x07F3FE, 0x07F4C7, 0x07F590, 0x07F659];

    private static string Driver(string name)
    {
        using var s = typeof(SpriteInsert).Assembly.GetManifestResourceStream("spritedriver/" + name)!;
        return new StreamReader(s).ReadToEnd();
    }

    private static string[] Local(string[] messages)
    {
        string folder = Path.GetFullPath(VirtualDir).Replace('\\', '/') + "/";
        return [.. messages.Select(m => m.Replace('\\', '/').IndexOf(folder, StringComparison.OrdinalIgnoreCase) is var i and >= 0
                                         ? m.Remove(i, folder.Length) : m)];
    }
}
