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

    /// <summary>Insert every slotted sprite onto <paramref name="rom"/>: the sizes their records
    /// take, the routines they call, each sprite, then the driver with their tables. On an error
    /// the result says which sprite; what was already written stays (the build is then discarded).</summary>
    public static Result Insert(Rom rom, ProjectFile data)
    {
        var warnings = new List<string>();
        var written = new List<Asar.WrittenBlock>();
        Result Fail(string e) => new(false, [e], [.. warnings], [.. written]);

        var (slots, perLevel, listed) = Slots(data, warnings);
        if (slots.Count == 0 && perLevel.Count == 0 && listed.Count == 0) return new(true, [], [.. warnings], []);
        if (ForeignDriver(rom) is { } foreign) return Fail(foreign);
        if (LoaderStep(rom, out int lmStep) is { } loader) return Fail(loader);

        var configs = new Dictionary<string, PixiConfig>();
        var names = slots.Values.Concat(perLevel.Values.SelectMany(l => l.Values)).Distinct().ToList();
        foreach (string name in names)
        {
            try { configs[name] = PixiConfig.Parse(data.Sprites[name].Config); }
            catch (FormatException e) { return Fail($"sprite {name}: config: {e.Message}"); }
        }
        if (SetRecordSizes(rom, slots, perLevel, configs) is { } sizeError) return Fail(sizeError);

        var mem = new Dictionary<string, byte[]>();
        void Put(string path, string text) => mem[Path.Combine(VirtualDir, path)] = Encoding.UTF8.GetBytes(text);
        Put("defines.asm", Driver("defines.asm"));
        // Our own routines stand in for any the project has not imported; an imported one wins,
        // since it is the one the user's sprites were written against.
        var files = BuiltinRoutines();
        foreach (var (path, text) in data.PixiFiles) files[path] = text;
        foreach (var (path, text) in files) Put(path, text);

        var sources = names.Where(n => configs[n].Type != 0).ToDictionary(n => n, n => data.Sprites[n]);
        var listedNames = listed.Values.SelectMany(l => l.Values).Distinct().ToList();
        var routines = InsertRoutines(rom, files, sources.Values.Concat(listedNames.Select(n => data.Sprites[n])), mem, written, out string? routineError);
        if (routineError is not null) return Fail(routineError);
        Put("routines.asm", string.Concat(routines.Select(r => $"macro {r.Key}()\n    JSL ${r.Value:X6}\nendmacro\n")));

        var entries = new Dictionary<string, (int Init, int Main, int[] Status)>();
        if (AssembleSprites(rom, data, sources, configs, mem, entries, warnings, written) is { } spriteError) return Fail(spriteError);
        var listedEntries = new Dictionary<string, (int Main, int Cape)>();
        if (AssembleListed(rom, data, listedNames, mem, listedEntries, warnings, written) is { } listedError) return Fail(listedError);

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
            freedata
            {string.Concat(PixiImport.AsmOnlyKinds.Select(k => $"{PixiImport.PixiName(k)}Table:\n{LongTable(listed.GetValueOrDefault(k), n => listedEntries[n].Main)}"))}
            ExtendedCapeTable:
            {LongTable(listed.GetValueOrDefault("extended"), n => listedEntries[n].Cape)}
            """);
        var d = Asar.Patch(rom, Path.Combine(VirtualDir, "_driver.asm"), mem);
        if (!d.Ok) return Fail("the sprite driver: " + string.Join("; ", Local(d.Errors)));
        written.AddRange(d.Blocks);
        return new(true, [], [.. warnings], [.. written]);
    }

    /// <summary>The slots to insert: global number → sprite, level → (B0-BF → sprite), and per
    /// ASM-only kind (cluster, extended, smoke…) its list number → sprite. A slot naming a sprite the
    /// library lacks, or holding the wrong kind, is skipped with a warning.</summary>
    private static (SortedDictionary<int, string> Global, SortedDictionary<int, SortedDictionary<int, string>> PerLevel,
                    Dictionary<string, SortedDictionary<int, string>> Listed)
        Slots(ProjectFile data, List<string> warnings)
    {
        var global = new SortedDictionary<int, string>();
        var perLevel = new SortedDictionary<int, SortedDictionary<int, string>>();
        var listed = new Dictionary<string, SortedDictionary<int, string>>();
        foreach (var (slot, name) in data.SpriteSlots)
        {
            if (!data.Sprites.TryGetValue(name, out var s)) { warnings.Add($"slot {slot} names {name}, which is not in the project"); continue; }
            if (PixiImport.SlotKind(slot) is not { } kind || kind != s.Kind) { warnings.Add($"slot {slot} cannot hold {name}, a {s.Kind}"); continue; }
            if (!PixiImport.HasConfig(kind))
            {
                if (!listed.TryGetValue(kind, out var list)) listed[kind] = list = [];
                list[Convert.ToInt32(slot.Split(' ')[1], 16)] = name;
                continue;
            }
            int colon = slot.IndexOf(':');
            if (colon < 0) { global[Convert.ToInt32(slot, 16)] = name; continue; }
            int level = Convert.ToInt32(slot[..colon], 16);
            if (!perLevel.TryGetValue(level, out var numbers)) perLevel[level] = numbers = [];
            numbers[Convert.ToInt32(slot[(colon + 1)..], 16)] = name;
        }
        return (global, perLevel, listed);
    }

    /// <summary>Extra bytes every B0-BF record carries once a project has per-level sprites.</summary>
    private const int PerLevelExtraBytes = 4;

    /// <summary>
    /// Custom placements have extra bits 2 (custom) or 3 (custom with the extra bit), and the
    /// loader steps each record by the table's length for those. A record's length is per
    /// number in every level, so with per-level sprites B0-BF are all 4 extra bytes, as PIXI has
    /// them, and a per-level sprite cannot ask for more. Returns an error naming it, or null.
    /// </summary>
    private static string? SetRecordSizes(Rom rom, SortedDictionary<int, string> slots,
        SortedDictionary<int, SortedDictionary<int, string>> perLevel, Dictionary<string, PixiConfig> configs)
    {
        foreach (var (n, name) in slots)
        {
            rom.SetSpriteEntrySize(2, n, 3 + configs[name].ExtraBytes);
            rom.SetSpriteEntrySize(3, n, 3 + configs[name].ExtraBytesSet);
        }
        if (perLevel.Count == 0) return null;
        foreach (var (level, numbers) in perLevel)
            foreach (var (n, name) in numbers)
                if (Math.Max(configs[name].ExtraBytes, configs[name].ExtraBytesSet) > PerLevelExtraBytes)
                    return $"sprite {name} in level {level:X3}'s slot {n:X2}: a per-level sprite has at most {PerLevelExtraBytes} extra bytes";
        for (int n = 0xB0; n <= 0xBF; n++)
        {
            rom.SetSpriteEntrySize(2, n, 3 + PerLevelExtraBytes);
            rom.SetSpriteEntrySize(3, n, 3 + PerLevelExtraBytes);
        }
        return null;
    }

    /// <summary>Assemble each sprite, shooter and generator on its own, with its folder's
    /// _header.asm, into <paramref name="entries"/>: INIT, MAIN and the status entry points it
    /// printed. Returns an error naming the sprite, or null.</summary>
    private static string? AssembleSprites(Rom rom, ProjectFile data, Dictionary<string, ProjectFile.SpriteEntry> sources,
        Dictionary<string, PixiConfig> configs, Dictionary<string, byte[]> mem, Dictionary<string, (int Init, int Main, int[] Status)> entries,
        List<string> warnings, List<Asar.WrittenBlock> written)
    {
        void Put(string path, string text) => mem[Path.Combine(VirtualDir, path)] = Encoding.UTF8.GetBytes(text);
        int i = 0;
        foreach (var (name, entry) in sources)
        {
            string dir = $"sprite{i++}";
            foreach (var (path, bytes) in entry.Files) mem[Path.Combine(VirtualDir, dir, path)] = bytes;
            string file = string.IsNullOrEmpty(configs[name].AsmFile) ? "sprite.asm" : Path.GetFileName(configs[name].AsmFile);
            Put(Path.Combine(dir, file), entry.Source);
            Put("_sprite.asm", Wrapper(data.PixiFiles, PixiImport.FolderOf(entry.Kind), $"{dir}/{file}"));
            var r = Asar.Patch(rom, Path.Combine(VirtualDir, "_sprite.asm"), mem);
            if (!r.Ok) return $"sprite {name}: " + string.Join("; ", Local(r.Errors));
            warnings.AddRange(Local(r.Warnings).Select(w => $"sprite {name}: {w}"));
            written.AddRange(r.Blocks);
            // A shooter or generator only runs MAIN; its INIT, if it prints one, is kept anyway.
            int? init = Entry(r, "INIT"), main = Entry(r, "MAIN");
            if (main is null || init is null && entry.Kind == "sprite")
                return $"sprite {name}: no {(main is null ? "MAIN" : "INIT")} — the sprite must print it (print \"MAIN \",pc)";
            entries[name] = (init ?? EmptyEntry, main.Value, [.. StatusEntries.Select(s => Entry(r, s) ?? 0)]);
        }
        return null;
    }

    /// <summary>
    /// Assemble each ASM-only sprite as the others are, with its folder's _header.asm,
    /// into <paramref name="entries"/>: its MAIN, which it must print, and for an extended sprite
    /// the CAPE it may (its cape contact; without one the cape passes through it). Returns an
    /// error naming the sprite, or null.
    /// </summary>
    private static string? AssembleListed(Rom rom, ProjectFile data, List<string> names, Dictionary<string, byte[]> mem,
        Dictionary<string, (int Main, int Cape)> entries, List<string> warnings, List<Asar.WrittenBlock> written)
    {
        int i = 0;
        foreach (string name in names)
        {
            var entry = data.Sprites[name];
            string dir = $"listed{i++}";
            foreach (var (path, bytes) in entry.Files) mem[Path.Combine(VirtualDir, dir, path)] = bytes;
            mem[Path.Combine(VirtualDir, dir, "sprite.asm")] = Encoding.UTF8.GetBytes(entry.Source);
            mem[Path.Combine(VirtualDir, "_sprite.asm")] = Encoding.UTF8.GetBytes(Wrapper(data.PixiFiles, PixiImport.FolderOf(entry.Kind), $"{dir}/sprite.asm"));
            var r = Asar.Patch(rom, Path.Combine(VirtualDir, "_sprite.asm"), mem);
            if (!r.Ok) return $"{entry.Kind} sprite {name}: " + string.Join("; ", Local(r.Errors));
            warnings.AddRange(Local(r.Warnings).Select(w => $"{entry.Kind} sprite {name}: {w}"));
            written.AddRange(r.Blocks);
            if (Entry(r, "MAIN") is not { } main) return $"{entry.Kind} sprite {name}: no MAIN — the sprite must print it (print \"MAIN \",pc)";
            entries[name] = (main, entry.Kind == "extended" ? Entry(r, "CAPE") ?? 0 : 0);
        }
        return null;
    }

    /// <summary>A list of 0x80 longs by list number (0: no sprite there), as db lines.</summary>
    private static string LongTable(SortedDictionary<int, string>? list, Func<string, int> pointer)
    {
        var sb = new StringBuilder();
        for (int n = 0; n < 0x80; n++)
        {
            int p = list?.GetValueOrDefault(n) is { } name ? pointer(name) : 0;
            sb.Append($"db ${p & 0xFF:X2},${p >> 8 & 0xFF:X2},${p >> 16:X2}\n");
        }
        return sb.ToString();
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
        var byName = RoutinesByName(files);
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var todo = new Stack<string>(sprites.SelectMany(s => s.Files.Values.Select(b => Encoding.UTF8.GetString(b)).Append(s.Source)));
        while (todo.TryPop(out var text))
            foreach (Match m in MacroCall().Matches(text))
                if (byName.ContainsKey(m.Groups[1].Value) && used.Add(m.Groups[1].Value)) todo.Push(files[byName[m.Groups[1].Value]]);
        if (used.Count == 0) return [];
        return AssembleRoutines(rom, files, byName, used, mem, written, out error);
    }

    /// <summary>Every routine in <paramref name="files"/> onto <paramref name="rom"/>, nothing else:
    /// name → address. What PixiRoutineTests runs ours and PIXI's from.</summary>
    internal static Dictionary<string, int> AssembleRoutines(Rom rom, Dictionary<string, string> files, out string? error)
    {
        var mem = new Dictionary<string, byte[]> { [Path.Combine(VirtualDir, "defines.asm")] = Encoding.UTF8.GetBytes(Driver("defines.asm")) };
        foreach (var (path, text) in files) mem[Path.Combine(VirtualDir, path)] = Encoding.UTF8.GetBytes(text);
        var byName = RoutinesByName(files);
        return AssembleRoutines(rom, files, byName, byName.Keys, mem, [], out error);
    }

    private static Dictionary<string, string> RoutinesByName(Dictionary<string, string> files)
        => files.Keys.Where(p => p.StartsWith("routines/") && p.EndsWith(".asm", StringComparison.OrdinalIgnoreCase))
                     .ToDictionary(p => p["routines/".Length..^4].Replace("/", ""), p => p);

    private static Dictionary<string, int> AssembleRoutines(Rom rom, Dictionary<string, string> files, Dictionary<string, string> byName,
        IEnumerable<string> used, Dictionary<string, byte[]> mem, List<Asar.WrittenBlock> written, out string? error)
    {
        error = null;
        var src = new StringBuilder("incsrc \"defines.asm\"\n");
        foreach (string extra in ExtraDefines(files)) src.Append($"incsrc \"{extra}\"\n");
        foreach (string r in used) src.Append($"macro {r}()\n    JSL PixiRoutine_{r}\nendmacro\n");
        // Inside a macro of its own: PIXI's routines use macro labels (?+, ?loop), which Asar
        // refuses anywhere else.
        foreach (string r in used)
            src.Append($"macro PixiRoutineBody_{r}()\nincsrc \"{byName[r]}\"\nendmacro\nfreecode\nPixiRoutine_{r}:\n%PixiRoutineBody_{r}()\n");
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

    /// <summary>The shared routines Pipe Dream ships (data/spritedriver/routines, MIT, written from
    /// SMW's code and reference/PIXI_ROUTINES.md), keyed as an imported PIXI folder's would be.</summary>
    internal static Dictionary<string, string> BuiltinRoutines()
        => typeof(SpriteInsert).Assembly.GetManifestResourceNames()
               .Where(n => n.StartsWith("spritedriver/routines/"))
               .ToDictionary(n => n["spritedriver/".Length..], n => Driver(n["spritedriver/".Length..]));

    private static string[] Local(string[] messages)
    {
        string folder = Path.GetFullPath(VirtualDir).Replace('\\', '/') + "/";
        return [.. messages.Select(m => m.Replace('\\', '/').IndexOf(folder, StringComparison.OrdinalIgnoreCase) is var i and >= 0
                                         ? m.Remove(i, folder.Length) : m)];
    }
}
