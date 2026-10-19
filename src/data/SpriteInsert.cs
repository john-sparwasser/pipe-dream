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

        var slots = Slots(data, warnings);
        if (slots.Count == 0) return new(true, [], [.. warnings], []);
        if (ForeignDriver(rom) is { } foreign) return Fail(foreign);
        if (LoaderStep(rom, out int lmStep) is { } loader) return Fail(loader);

        var configs = new Dictionary<string, PixiConfig>();
        foreach (string name in slots.Values.Distinct())
        {
            try { configs[name] = PixiConfig.Parse(data.Sprites[name].Config); }
            catch (FormatException e) { return Fail($"sprite {name}: config: {e.Message}"); }
        }
        // Custom placements have extra bits 2 (custom) or 3 (custom with the extra bit), and the
        // loader steps each record by the table's length for those.
        foreach (var (n, name) in slots)
        {
            rom.SetSpriteEntrySize(2, n, 3 + configs[name].ExtraBytes);
            rom.SetSpriteEntrySize(3, n, 3 + configs[name].ExtraBytesSet);
        }

        var mem = new Dictionary<string, byte[]>();
        void Put(string path, string text) => mem[Path.Combine(VirtualDir, path)] = Encoding.UTF8.GetBytes(text);
        Put("defines.asm", Driver("defines.asm"));
        foreach (var (path, text) in data.PixiFiles) Put(path, text);

        var sources = slots.Values.Distinct().Where(n => configs[n].Type != 0).ToDictionary(n => n, n => data.Sprites[n]);
        var routines = InsertRoutines(rom, data.PixiFiles, sources.Values, mem, written, out string? routineError);
        if (routineError is not null) return Fail(routineError);
        Put("routines.asm", string.Concat(routines.Select(r => $"macro {r.Key}()\n    JSL ${r.Value:X6}\nendmacro\n")));

        var entries = new Dictionary<string, (int Init, int Main)>();
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
            int? init = Entry(r, "INIT"), main = Entry(r, "MAIN");
            if (init is null || main is null)
                return Fail($"sprite {name}: no {(init is null ? "INIT" : "MAIN")} — the sprite must print it (print \"INIT \",pc)");
            entries[name] = (init.Value, main.Value);
        }

        Put("main.asm", Driver("main.asm"));
        Put("_driver.asm", $"""
            incsrc "defines.asm"
            !SizeTable = ${rom.LmSpriteSizeBase:X6}
            !LmStep = ${lmStep:X6}
            incsrc "main.asm"
            freedata
            SpriteTable:
            {Table(rom, slots, configs, entries)}
            """);
        var d = Asar.Patch(rom, Path.Combine(VirtualDir, "_driver.asm"), mem);
        if (!d.Ok) return Fail("the sprite driver: " + string.Join("; ", Local(d.Errors)));
        written.AddRange(d.Blocks);
        return new(true, [], [.. warnings], [.. written]);
    }

    /// <summary>The global slots to insert, number → sprite. Per-level slots, shooters and
    /// generators are not inserted yet; a slot naming a sprite the library lacks is skipped. Each
    /// is said once in the warnings.</summary>
    private static SortedDictionary<int, string> Slots(ProjectFile data, List<string> warnings)
    {
        var slots = new SortedDictionary<int, string>();
        int perLevel = 0, others = 0;
        foreach (var (slot, name) in data.SpriteSlots)
        {
            if (slot.Contains(':')) { perLevel++; continue; }
            if (PixiImport.SlotKind(slot) != "sprite") { others++; continue; }
            if (!data.Sprites.ContainsKey(name)) { warnings.Add($"slot {slot} names {name}, which is not in the project"); continue; }
            slots[Convert.ToInt32(slot, 16)] = name;
        }
        if (perLevel > 0) warnings.Add($"{perLevel} per-level sprite slot(s) not inserted (not supported yet)");
        if (others > 0) warnings.Add($"{others} shooter/generator slot(s) not inserted (not supported yet)");
        return slots;
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

    /// <summary>The 0x100 table entries as db lines. A number with no sprite is a tweak of itself
    /// with SMW's own tweakers, so a stray custom placement acts as the vanilla sprite would.</summary>
    private static string Table(Rom rom, SortedDictionary<int, string> slots, Dictionary<string, PixiConfig> configs,
                                Dictionary<string, (int Init, int Main)> entries)
    {
        var sb = new StringBuilder();
        for (int n = 0; n < 0x100; n++)
        {
            byte[] e;
            if (slots.TryGetValue(n, out var name))
            {
                var c = configs[name];
                var (init, main) = entries.TryGetValue(name, out var p) ? p : (EmptyEntry, EmptyEntry);
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
            sb.Append("db ").AppendJoin(',', e.Select(b => $"${b:X2}")).Append($" ; {n:X2}\n");
        }
        return sb.ToString();
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
