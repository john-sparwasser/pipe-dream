using System.Text;
using System.Text.RegularExpressions;

namespace PipeDream;

/// <summary>
/// Inserts a project's UberASM at build (reference/UBERASM_PORT.md, phase 2), UberASM Tool's job done our way: the shared
/// routines the code calls go in once, each library file and each resource in freespace of its own (so an error names it,
/// and a resource's extra bytes sit in its bank), then our driver (data/uberdriver) with a routine per distinct list of calls
/// and a table per level/game mode/map label. Every build starts from the base, so nothing is ever cleaned up.
/// </summary>
public static partial class UberInsert
{
    /// <param name="Labels">The driver's labels (its hooks' code and each …Dispatch), for tests.</param>
    public sealed record Result(bool Ok, string[] Errors, string[] Warnings, Asar.WrittenBlock[] Written, IReadOnlyDictionary<string, int>? Labels = null);

    private static readonly string VirtualDir = Path.Combine(Path.GetTempPath(), "pipedream-uber");

    /// <summary>The sites the driver hooks and SMW's bytes there (-1: any byte); a base where one differs already has code of
    /// someone else's.</summary>
    private static readonly (int At, int[] Smw)[] Sites =
    [
        (0x008064, [0xA9, 0x03, 0x8D, 0x01, 0x21]), (0x008070, [0xE6, 0x13, 0x20, 0x22, 0x93, 0x64, 0x10]),
        (0x008174, [0xE2, 0x30, 0xAD, 0x10, 0x42]), (0x008E1A, [0xAD, 0x93, 0x14, 0x05, 0x9D]),
        (0x00A179, [0xA9, 0x02, 0x8D, 0x9B, 0x0D]), (0x00A1C3, [0x22, 0x00, 0x80, 0x7F, 0x22, 0x41, 0x82, 0x04, 0x4C, 0x94, 0x84]),
        (0x00A242, [0xAD, 0xD4, 0x13, 0xF0, 0x43]), (0x00A2EA, [0x68, 0x85, 0x1D, 0x68, 0x85, 0x1C, 0x4C, 0x94, 0x84]),
        (0x00A5E8, [0xAE, 0x01, 0x07, 0x8E, 0x03, 0x09]), (0x05808F, [0xE2, 0x30, 0xAD, 0x00, 0x01]),
        (0x0084CF, [0x6B]),                                       // the RTL the frame returns from SMW's game mode through
    ];

    /// <summary>The same sites after the SA-1 Pack (measured on v1.40): RAM operands in $0100-$1FFF moved to $6100-$7FFF, and
    /// the map's `JSL $048241` a JSL into the pack's own routine, which moves with the layout.</summary>
    private static readonly (int At, int[] Smw)[] Sa1Sites =
    [
        (0x008064, [0xA9, 0x03, 0x8D, 0x01, 0x21]), (0x008070, [0xE6, 0x13, 0x20, 0x22, 0x93, 0x64, 0x10]),
        (0x008174, [0xE2, 0x30, 0xAD, 0x10, 0x42]), (0x008E1A, [0xAD, 0x93, 0x74, 0x05, 0x9D]),
        (0x00A179, [0xA9, 0x02, 0x8D, 0x9B, 0x6D]), (0x00A1C3, [0x22, 0x00, 0x80, 0x7F, 0x22, -1, -1, -1, 0x4C, 0x94, 0x84]),
        (0x00A242, [0xAD, 0xD4, 0x73, 0xF0, 0x43]), (0x00A2EA, [0x68, 0x85, 0x1D, 0x68, 0x85, 0x1C, 0x4C, 0x94, 0x84]),
        (0x00A5E8, [0xAE, 0x01, 0x67, 0x8E, 0x03, 0x69]), (0x05808F, [0xE2, 0x30, 0xAD, 0x00, 0x61]),
        (0x0084CF, [0x6B]),
    ];

    private static readonly string[] Labels = ["load", "init", "main", "end", "nmi"];

    /// <summary>What a kind's dispatch reads its number from, in 16-bit A, and how many numbers it has. !BankA is SMW's RAM
    /// bank as a long address: $7E, or the SA-1 Pack's BW-RAM, $40.</summary>
    private static readonly (string Kind, string Name, int Count, string Number)[] Kinds =
    [
        ("level", "Level", 0x200, "LDA.l $010B|!BankA\n    AND #$01FF"),
        ("gamemode", "GameMode", 0x100, "LDA.l $0100|!BankA\n    AND #$00FF"),
        ("overworld", "Overworld", 7, "LDA.l $0DB3|!BankA\n    AND #$00FF\n    TAX\n    LDA.l $1F11|!BankA,x\n    AND #$00FF"),
    ];

    public static Result Insert(Rom rom, ProjectFile data)
    {
        var u = data.Uber;
        var warnings = new List<string>();
        var written = new List<Asar.WrittenBlock>();
        Result Fail(string e) => new(false, [e], [.. warnings], [.. written]);

        var sections = new[] { ("level", u.Levels), ("gamemode", u.GameModes), ("overworld", u.Overworlds) };
        var used = sections.SelectMany(s => s.Item2).SelectMany(e => e.Calls).Where(c => !c.Skip).Select(c => c.File).Distinct().ToList();
        bool hasGlobal = Has(u, u.Global, "init", "main", "nmi", "load"), hasStatus = Has(u, u.StatusBar, "main");
        if (used.Count == 0 && !hasGlobal && !hasStatus) return new(true, [], [], []);
        if (used.Find(f => !u.Files.ContainsKey(f)) is { } missing) return Fail($"{missing} is in the list but not in the project");
        foreach (var (at, smw) in rom.Map is Sa1Map ? Sa1Sites : Sites)
            if (smw.Where((b, i) => b >= 0 && rom.ReadByte(at + i) != b).Any())
                return Fail($"${at:X6} is not SMW's code (UberASM Tool, or another patch, already hooks it): build from a base without one");

        var mem = new Dictionary<string, byte[]>();
        foreach (var (path, bytes) in u.Files) mem[Full(path)] = bytes;
        void Put(string path, string text) => mem[Full(path)] = Encoding.UTF8.GetBytes(text);
        string Text(string path) => u.Files.TryGetValue(path, out var b) ? Encoding.UTF8.GetString(b) : "";

        // The defines and macros every file gets: SMW's names (the sprite driver's), ours, the user's macro library, and as each
        // step is done the routines' and the library's labels.
        Put("_defines.asm", Defines(data, Text(u.MacroLib)));
        // Each resource, library file and routine is its own patch, referenced only by a later one (the driver), which Asar
        // calls a freespace leak.
        var head = new StringBuilder("warnings disable Wfreespace_leaked\nincsrc \"_defines.asm\"\n");
        if (u.Files.ContainsKey(u.MacroLib)) head.Append($"incsrc \"{u.MacroLib}\"\n");

        var sources = used.Select(Text).Concat(u.Files.Where(f => f.Key.StartsWith("library/") && f.Key.EndsWith(".asm")).Select(f => Text(f.Key)))
            .Append(Text(u.Global)).Append(Text(u.StatusBar)).Append(Text(u.MacroLib));
        if (InsertRoutines(rom, u, sources, head.ToString(), mem, written, out var routines) is { } routineError) return Fail(routineError);
        head.Append(string.Concat(routines.Select(r => $"UberRoutine_{r.Key} = ${r.Value:X6}\n")));

        if (InsertLibrary(rom, u, head.ToString(), mem, warnings, written, out var library) is { } libraryError) return Fail(libraryError);
        head.Append(string.Concat(library.Select(l => $"{l.Key} = ${l.Value:X6}\n")));
        Put("_head.asm", head.ToString());

        var labels = new Dictionary<string, Dictionary<string, int>>();               // resource → label → address
        var extras = new Dictionary<(string File, string Bytes), int>();               // a call's extra bytes (as listed) → where they sit, in its resource's bank
        var dbrOff = new HashSet<string>();
        foreach (string file in used)
        {
            string text = Text(file);
            var calls = sections.SelectMany(s => s.Item2).SelectMany(e => e.Calls).Where(c => c.File == file && !c.Skip).ToList();
            if (ExtraBytes(file, text, calls) is { } byteError) return Fail(byteError);
            if (Regex.IsMatch(text, @"(?m)^;>dbr off\s*$")) dbrOff.Add(file);
            var arrays = calls.Select(c => c.Bytes).DistinctBy(Convert.ToHexString).ToList();
            var src = new StringBuilder("incsrc \"_head.asm\"\nnamespace nested on\nfreecode\n");
            src.Append($"incsrc \"{file}\"\n");
            for (int i = 0; i < arrays.Count; i++)
                src.Append($"UberExtraBytes_{i}:\n").Append(Arrayed(text, arrays[i]) is { Length: > 0 } a ? $"    db {string.Join(",", a.Select(b => $"${b:X2}"))}\n" : "");
            Put("_resource.asm", src.ToString());
            var r = Asar.Patch(rom, Full("_resource.asm"), mem);
            if (!r.Ok) return Fail($"{file}: " + string.Join("; ", Local(r.Errors)));
            warnings.AddRange(Local(r.Warnings).Select(w => $"{file}: {w}"));
            written.AddRange(r.Blocks);
            labels[file] = Labels.Where(r.Labels.ContainsKey).ToDictionary(l => l, l => r.Labels[l]);
            if (labels[file].Count == 0)
                warnings.Add($"{file} has none of load:, init:, main:, end:, nmi: (lower case), so it never runs");
            for (int i = 0; i < arrays.Count; i++) extras[(file, Convert.ToHexString(arrays[i]))] = r.Labels[$"UberExtraBytes_{i}"];
        }

        // The driver: its hooks, the global and status bar files inside it, and the dispatch the list makes.
        var drv = new StringBuilder("incsrc \"_head.asm\"\n");
        drv.Append($"!FreeRam = ${u.FreeRam:X6}\n");
        drv.Append($"!OverworldMain = ${rom.ReadValue(0x00A1C8, 3):X6}\n");      // what the map's hook displaces calls: SMW's, or the SA-1 Pack's
        foreach (var (define, path, label) in new[] { ("UberGlobalInit", u.Global, "init"), ("UberGlobalMain", u.Global, "main"),
                     ("UberGlobalNmi", u.Global, "nmi"), ("UberGlobalLoad", u.Global, "load"), ("UberStatusMain", u.StatusBar, "main") })
            drv.Append($"!{define} = {(Has(u, path, label) ? 1 : 0)}\n");
        drv.Append("incsrc \"_main.asm\"\n");
        if (hasGlobal) drv.Append($"namespace UberGlobal\nincsrc \"{u.Global}\"\nnamespace off\n");
        if (hasStatus) drv.Append($"namespace UberStatus\nincsrc \"{u.StatusBar}\"\nnamespace off\n");
        drv.Append(Dispatch(sections, labels, extras, dbrOff));
        Put("_main.asm", Driver("main.asm"));
        Put("_driver.asm", drv.ToString());
        var d = Asar.Patch(rom, Full("_driver.asm"), mem);
        if (!d.Ok) return Fail("the UberASM driver: " + string.Join("; ", Local(d.Errors)));
        warnings.AddRange(Local(d.Warnings).Select(w => $"the UberASM driver: {w}"));
        written.AddRange(d.Blocks);
        return new(true, [], [.. warnings], [.. written], d.Labels);
    }

    /// <summary>Whether the global or status bar file has one of <paramref name="labels"/> (at the start of a line, lower case).</summary>
    private static bool Has(UberState u, string path, params string[] labels)
        => u.Files.TryGetValue(path, out var b) && labels.Any(l => Regex.IsMatch(Encoding.UTF8.GetString(b), $@"(?m)^\s*{l}:"));

    /// <summary>A resource's <c>;>bytes n</c> (or <c>any</c>) against what each call gives it. Returns an error, or null.</summary>
    private static string? ExtraBytes(string file, string text, List<UberCall> calls)
    {
        var m = Regex.Match(text, @"(?m)^;>bytes\s+(\S+)");
        if (m.Success && m.Groups[1].Value == "any") return calls.Any(c => c.Bytes.Length > 255) ? $"{file}: at most 255 extra bytes" : null;
        int want = m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : 0;
        if (calls.Find(c => c.Bytes.Length != want) is { } bad)
            return $"{file} takes {want} extra byte{(want == 1 ? "" : "s")} (;>bytes), the list gives {bad.Bytes.Length}";
        return null;
    }

    /// <summary>A call's extra bytes as they sit in ROM: with <c>;>bytes any</c>, the count first.</summary>
    private static byte[] Arrayed(string text, byte[] bytes)
        => Regex.IsMatch(text, @"(?m)^;>bytes\s+any\b") ? [(byte)bytes.Length, .. bytes] : bytes;

    /// <summary>
    /// Each kind's label tables and the routines they point at. A number runs the <c>*</c> calls it does not skip, then its
    /// own, keeping those whose resource has the label; numbers with the same calls share a routine, and a label nothing has
    /// is a bare RTL. A call sets $00-01 to its extra bytes and the DBR to its bank (not for nmi:, nor with <c>;>dbr off</c>).
    /// </summary>
    private static string Dispatch((string Kind, List<UberEntry> Entries)[] sections, Dictionary<string, Dictionary<string, int>> labels,
        Dictionary<(string, string), int> extras, HashSet<string> dbrOff)
    {
        var s = new StringBuilder();
        var routines = new Dictionary<string, string>();                     // a call list's code → its routine's name
        foreach (var (kind, name, count, number) in Kinds)
        {
            var entries = sections.First(x => x.Kind == kind).Entries;
            var star = entries.Find(e => e.Number == UberEntry.All);
            foreach (string label in Labels.Where(l => l != "load" || kind == "level"))
            {
                string entry = $"{name}{char.ToUpperInvariant(label[0])}{label[1..]}Dispatch";
                var lists = new string[count];
                for (int n = 0; n < count; n++)
                {
                    var own = entries.Find(e => e.Number == n);
                    var skipped = own?.Calls.Where(c => c.Skip).Select(c => c.File).ToHashSet() ?? [];
                    var calls = (star?.Calls ?? []).Where(c => !c.Skip && !skipped.Contains(c.File))
                        .Concat(own?.Calls.Where(c => !c.Skip) ?? []).Where(c => labels.GetValueOrDefault(c.File)?.ContainsKey(label) == true);
                    var code = new StringBuilder();
                    foreach (var c in calls)
                    {
                        int at = labels[c.File][label], extra = extras[(c.File, Convert.ToHexString(c.Bytes))];
                        bool dbr = label != "nmi" && !dbrOff.Contains(c.File);
                        code.Append($"    SEP #$30\n    REP #$20\n    LDA.w #${extra & 0xFFFF:X4}\n    STA $00\n    SEP #$20\n");
                        if (dbr) code.Append($"    PHB\n    LDA.b #${at >> 16:X2}\n    PHA\n    PLB\n");
                        code.Append($"    JSL ${at:X6}\n");
                        if (dbr) code.Append("    PLB\n");
                    }
                    lists[n] = code.ToString();
                }
                if (lists.All(l => l.Length == 0)) { s.Append($"{entry}:\n    RTL\n"); continue; }
                foreach (string code in lists.Where(l => l.Length > 0).Distinct())
                    if (!routines.ContainsKey(code))
                    {
                        routines[code] = $"UberCalls_{routines.Count}";
                        s.Append($"{routines[code]}:\n{code}    SEP #$30\n    RTL\n");
                    }
                // The number × 3 into the table; the routine is entered with JML [$00] and returns to the JSL into this. JML's
                // operand is an absolute address, so it names the direct page's $00 through !dp: $3000 on SA-1.
                s.Append($"{entry}:\n    REP #$30\n    {number}\n    STA $00\n    ASL\n    ADC $00\n    TAX\n    SEP #$20\n");
                s.Append($"    LDA.l {entry}_Table,x\n    STA $00\n    LDA.l {entry}_Table+1,x\n    STA $01\n    LDA.l {entry}_Table+2,x\n    STA $02\n");
                s.Append("    SEP #$30\n    JML [$0000|!dp]\n");
                s.Append($"{entry}_Table:\n");
                for (int n = 0; n < count; n++) s.Append($"    dl {(lists[n].Length > 0 ? routines[lists[n]] : "UberNothing")}\n");
            }
        }
        s.Append("UberNothing:\n    RTL\n");
        return s.ToString();
    }

    /// <summary>
    /// The shared routines anything calls (<c>%UberRoutine(name)</c>, and what those call), each from routines/name.asm inside a
    /// macro of its own (2.x's rule: a routine's labels are macro labels) and in its own freecode, all in one assembly so they can
    /// call each other. Returns an error, or null.
    /// </summary>
    private static string? InsertRoutines(Rom rom, UberState u, IEnumerable<string> sources, string head, Dictionary<string, byte[]> mem,
        List<Asar.WrittenBlock> written, out Dictionary<string, int> addresses)
    {
        addresses = [];
        var byName = u.Files.Keys.Where(p => Regex.IsMatch(p, @"^routines/[^/]+\.asm$", RegexOptions.IgnoreCase))
            .ToDictionary(p => p["routines/".Length..^4], p => p, StringComparer.OrdinalIgnoreCase);
        var needed = new SortedSet<string>(StringComparer.Ordinal);
        var todo = new Stack<string>(sources);
        while (todo.TryPop(out var text))
            foreach (Match m in RoutineCall().Matches(text))
            {
                string name = m.Groups[1].Value;
                if (!byName.ContainsKey(name)) return $"%UberRoutine({name}): routines/{name}.asm is not in the project";
                if (needed.Add(name)) todo.Push(Encoding.UTF8.GetString(u.Files[byName[name]]));
            }
        if (needed.Count == 0) return null;
        var src = new StringBuilder(head);
        foreach (string r in needed)
            src.Append($"macro UberRoutineBody_{r}()\nincsrc \"{byName[r]}\"\nendmacro\nfreecode\nUberRoutine_{r}:\n%UberRoutineBody_{r}()\n");
        mem[Full("_routines.asm")] = Encoding.UTF8.GetBytes(src.ToString());
        var res = Asar.Patch(rom, Full("_routines.asm"), mem);
        if (!res.Ok) return "shared routines: " + string.Join("; ", Local(res.Errors));
        written.AddRange(res.Blocks);
        addresses = needed.ToDictionary(r => r, r => res.Labels[$"UberRoutine_{r}"]);
        return null;
    }

    /// <summary>
    /// Every library file, each in its own freespace: a binary is one label (its name), an .asm's labels go out with its name
    /// before them ("math.asm"'s sqrt is math_sqrt); folders and spaces in the path become underscores. Returns an error, or null.
    /// </summary>
    private static string? InsertLibrary(Rom rom, UberState u, string head, Dictionary<string, byte[]> mem, List<string> warnings,
        List<Asar.WrittenBlock> written, out Dictionary<string, int> labels)
    {
        labels = [];
        foreach (var path in u.Files.Keys.Where(p => p.StartsWith("library/")).Order(StringComparer.Ordinal))
        {
            string name = Regex.Replace(Path.ChangeExtension(path["library/".Length..], null), "[/ ]", "_");
            bool asm = path.EndsWith(".asm", StringComparison.OrdinalIgnoreCase);
            string src = asm ? $"{head}namespace nested on\nnamespace {name}\nfreecode\nincsrc \"{path}\"\nnamespace off\n"
                             : $"{head}freedata\n{name}:\nincbin \"{path}\"\n";
            mem[Full("_library.asm")] = Encoding.UTF8.GetBytes(src);
            var r = Asar.Patch(rom, Full("_library.asm"), mem);
            if (!r.Ok) return $"{path}: " + string.Join("; ", Local(r.Errors));
            warnings.AddRange(Local(r.Warnings).Select(w => $"{path}: {w}"));
            written.AddRange(r.Blocks);
            foreach (var (label, at) in r.Labels)
                if (asm ? label.StartsWith(name + "_") && !label.Contains('.') : label == name) labels[label] = at;
        }
        return null;
    }

    /// <summary>The defines and macros a resource sees: the sprite driver's names for SMW's RAM, UberASM Tool's own defines, and
    /// our macros under UberASM Tool's names, but for any the user's macro library defines itself (theirs wins).</summary>
    private static string Defines(ProjectFile data, string macroLib)
    {
        var s = new StringBuilder(Sprite("defines.asm"));
        bool remapped = data.SpriteSlots.Count > 0;                          // our sprite driver moves the load table, as PIXI does
        s.Append(Driver("defines.asm"));
        s.Append($"!Has255SpritesPerLevel = {(remapped ? 1 : 0)}\n");
        // Without our sprite driver the table stays SMW's — on LoROM; the SA-1 Pack moves it to $418A00 either way.
        if (!remapped) s.Append("if !sa1 == 0\n!1938 = $1938\n!sprite_load_table = $1938\nendif\n");
        // Not again what the sprite driver's defines or the user's library already define (Asar refuses a macro twice).
        var theirs = Regex.Matches(Sprite("defines.asm") + "\n" + macroLib, @"(?m)^\s*macro\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(")
            .Select(m => m.Groups[1].Value).ToHashSet();
        foreach (Match m in Regex.Matches(Driver("macros.asm"), @"(?ms)^macro\s+([A-Za-z_][A-Za-z0-9_]*)\(.*?^endmacro\s*$"))
            if (!theirs.Contains(m.Groups[1].Value)) s.Append(m.Value).Append('\n');
        return s.ToString();
    }

    [GeneratedRegex(@"%UberRoutine\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)")]
    private static partial Regex RoutineCall();

    private static string Full(string path) => Path.Combine(VirtualDir, path);

    private static string Driver(string name) => Resource("uberdriver/" + name);
    private static string Sprite(string name) => Resource("spritedriver/" + name);

    private static string Resource(string name)
    {
        using var s = typeof(UberInsert).Assembly.GetManifestResourceStream(name)!;
        return new StreamReader(s).ReadToEnd();
    }

    private static string[] Local(string[] messages)
    {
        string folder = Path.GetFullPath(VirtualDir).Replace('\\', '/') + "/";
        return [.. messages.Select(m => m.Replace('\\', '/').IndexOf(folder, StringComparison.OrdinalIgnoreCase) is var i and >= 0
                                         ? m.Remove(i, folder.Length) : m)];
    }
}
