using System.Globalization;
using System.Text;

namespace PipeDream;

/// <summary>
/// Inserts a project's custom blocks — what gps.exe does, in one Asar patch instead of one per
/// block. The folder is used as GPS uses it: blocks/, routines/ and the user's own main.asm and
/// defines.asm, so whatever their driver does still happens. What gps.exe reads from list.txt
/// the project carries instead (<see cref="ProjectFile.Blocks"/>: tile → block file; acts-like
/// in <see cref="ProjectFile.Map16State.ActsAs"/>, written by the build after this runs), and a
/// list.txt is only ever read once, at import, to fill those.
///
/// The patch, generated in memory beside main.asm:
///   1. autoclean the last insertion (its blocks, tables and shared routines), when GPS is on the ROM;
///   2. incsrc main.asm, with __temp_settings.asm and the table .bins supplied in memory. The
///      pointer and bank tables are placeholders of the right size: the blocks' addresses are
///      only known once assembled, so they are written into the tables after the patch;
///   3. a macro per routines/*.asm, which puts the routine in freespace on first use and records
///      it in GPS's routine table ($0CB66F), as gps.exe's shared.asm does;
///   4. each distinct block file once, in freecode, in its own namespace (blocks reuse label
///      names like MarioAbove), between two labels that give its address and size.
///
/// Asar frees autocleaned space in its first pass, so the new insertion can reuse the old one's.
/// Known differences from gps.exe, all from sharing one patch: two block files that define the
/// same macro collide, and a define one block sets is visible to the blocks after it. No .dsc
/// file is written, the -O1/-O2 table trims are not offered, and BTSD is not removed.
/// </summary>
public static class GpsInsert
{
    public sealed record Result(bool Ok, string[] Errors, string[] Warnings, IReadOnlyList<Inserted> Blocks);

    /// <summary>One tile's block: its file, code address, and the block's prints (the first is
    /// its description; @pswitch= and the like follow, as gps.exe reads them).</summary>
    public sealed record Inserted(int Tile, string File, int Addr, string[] Prints);

    /// <summary>One list.txt entry per tile: the block file and, when the line gives one, acts-like.</summary>
    public readonly record struct Entry(string File, int ActsLike, int Line);

    private const int RoutineSlots = 0x0CB66F, MaxRoutines = 100;

    /// <summary>Insert <paramref name="blocks"/> (tile → file under gpsDir/blocks/) with the
    /// folder's driver and routines. The acts-like tables go back as they are on the ROM: the
    /// project's acts-as edits are the build's to write, after this.</summary>
    public static Result Insert(Rom rom, string gpsDir, IReadOnlyDictionary<int, string> blocks)
    {
        gpsDir = Path.GetFullPath(gpsDir);
        if (blocks.Keys.FirstOrDefault(t => t is < 0x200 or >= 0x8000, -1) is var bad and >= 0)
            return Fail($"tile {bad:X3} cannot be a custom block (pages 02-7F only)");
        if (rom.ReadValue(0x06F624, 3) == 0xFFFFFF)
            return Fail("Please save a level in Lunar Magic before using GPS");
        bool hi = blocks.Keys.Any(t => t >= 0x4000);
        bool lmHi = rom.ReadByte(0x06F63C) != 0xFF;
        if (hi && !lmHi)
            return Fail("Cannot insert custom blocks on pages 40+: edit at least one tile there in Lunar Magic first");

        // One asm label per distinct file, in tile order: the order gps.exe assembles them in.
        var files = blocks.OrderBy(kv => kv.Key).Select(kv => kv.Value).Distinct().ToList();
        var routines = Directory.GetFiles(Path.Combine(gpsDir, "routines"), "*.asm")
            .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToList();
        if (routines.Count > MaxRoutines) return Fail($"More than {MaxRoutines} routines in routines/");

        string root = Path.Combine(gpsDir, "__pipedream_gps.asm");
        var mem = new Dictionary<string, byte[]>
        {
            [root] = Encoding.UTF8.GetBytes(Source(rom, files, routines)),
            [Path.Combine(gpsDir, "__temp_settings.asm")] = Encoding.UTF8.GetBytes($"!__insert_pages_40_plus = {(hi ? 1 : 0)}"),
            [Path.Combine(gpsDir, "__banks.bin")] = new byte[hi ? 0x8000 : 0x4000],
            [Path.Combine(gpsDir, "__pointers_1.bin")] = new byte[0x8000],
            [Path.Combine(gpsDir, "__pointers_2.bin")] = new byte[0x8000],
        };
        var acts = ActsLikes(rom, lmHi);
        mem[Path.Combine(gpsDir, "__acts_likes_1.bin")] = acts[..0x8000];
        mem[Path.Combine(gpsDir, "__acts_likes_2.bin")] = acts[0x8000..];

        var before = rom.Data;
        var r = Asar.Patch(rom, root, mem);
        if (!r.Ok) return new Result(false, r.Errors, r.Warnings, []);

        var (addrs, errors) = Assembled(rom, r, files);
        if (errors.Count > 0) { rom.Data = before; RomPrep.ResetScanCaches(rom); return new Result(false, [.. errors], r.Warnings, []); }
        FillTables(rom, blocks, files, addrs);
        var prints = PrintsByBlock(r.Prints, files.Count);
        var inserted = blocks.OrderBy(kv => kv.Key).Select(kv =>
        {
            int i = files.IndexOf(kv.Value);
            return new Inserted(kv.Key, kv.Value, addrs[i], prints[i]);
        }).ToList();
        return new Result(true, [], r.Warnings, inserted);
    }

    private static Result Fail(string message) => new(false, [message], [], []);

    /// <summary>A project's blocks as Insert takes them: the tile keys parsed from hex.</summary>
    public static Dictionary<int, string> BlocksOf(ProjectFile data)
        => data.Blocks.ToDictionary(kv => Convert.ToInt32(kv.Key, 16), kv => kv.Value);

    // ---- import ----

    /// <summary>Copy what Insert reads from a GPS folder into <paramref name="dest"/>, replacing
    /// whatever was there: main.asm, defines.asm, blocks/ and routines/. list.txt is not copied —
    /// it comes back parsed in <paramref name="list"/> for the project to keep as its own
    /// (<see cref="ProjectFile.Blocks"/> and acts-as) — and gps.exe, its dll and readme stay
    /// behind. Returns the problem, or null. The folder is checked first (list.txt parses, every
    /// block it names exists) and copied beside dest before the swap, so a bad import leaves the
    /// last good one in place.</summary>
    public static string? Import(string source, string dest, out Dictionary<int, Entry> list)
    {
        list = new();
        source = Path.GetFullPath(source);
        dest = Path.GetFullPath(dest);
        if (!File.Exists(Path.Combine(source, "list.txt"))) return "not a GPS folder: no list.txt";
        if (!File.Exists(Path.Combine(source, "main.asm"))) return "not a GPS folder: no main.asm";
        Dictionary<int, Entry> parsed;
        try { parsed = ParseList(File.ReadAllText(Path.Combine(source, "list.txt"))); }
        catch (FormatException e) { return e.Message; }
        foreach (var e in parsed.Values.DistinctBy(e => e.File).OrderBy(e => e.Line))
            if (!File.Exists(Path.Combine(source, "blocks", e.File)))
                return $"list.txt line {e.Line}: blocks/{e.File} not found";
        list = parsed;                                          // only a folder that imports hands its list over
        if (source == dest) return null;

        string staged = dest + ".importing";
        if (Directory.Exists(staged)) Directory.Delete(staged, true);
        Directory.CreateDirectory(staged);
        foreach (var f in new[] { "main.asm", "defines.asm" })
            if (File.Exists(Path.Combine(source, f))) File.Copy(Path.Combine(source, f), Path.Combine(staged, f));
        foreach (var d in new[] { "blocks", "routines" })
            CopyTree(Path.Combine(source, d), Path.Combine(staged, d));
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.Move(staged, dest);
        return null;
    }

    /// <summary>Copies a tree; an absent one becomes an empty folder (Insert lists routines/).</summary>
    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from)) return;
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    // ---- list.txt ----

    /// <summary>list.txt by tile, as README.txt describes it: `200 file.asm`, `200:0130 file.asm`,
    /// ranges `200-20F`, rectangles `R200-21F` (the box between two corners), hex throughout.
    /// A later line for the same tile wins, keeping an earlier acts-like when it gives none.
    /// `;` starts a comment line; `@dsc` ends the list (what follows is for LM's .dsc).</summary>
    public static Dictionary<int, Entry> ParseList(string text)
    {
        var tiles = new Dictionary<int, Entry>();
        var lines = text.Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n].Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line == "@dsc") break;
            int split = line.IndexOfAny([' ', '\t']);
            if (split < 0) throw new FormatException($"list.txt line {n + 1}: missing space before the file name");
            string spec = line[..split], file = line[split..].Trim();
            int acts = -1;
            if (spec.IndexOf(':') is var colon and >= 0)
            {
                acts = Hex(spec[(colon + 1)..], n);
                if (acts >= 0x8000) throw new FormatException($"list.txt line {n + 1}: acts-like out of bounds");
                spec = spec[..colon];
            }
            foreach (int tile in Tiles(spec, n))
                tiles[tile] = new Entry(file, acts >= 0 ? acts : tiles.TryGetValue(tile, out var old) ? old.ActsLike : -1, n + 1);
        }
        return tiles;
    }

    private static IEnumerable<int> Tiles(string spec, int n)
    {
        bool rect = spec.StartsWith('R');
        var ends = (rect ? spec[1..] : spec).Split('-');
        int a = Hex(ends[0], n), b = ends.Length > 1 ? Hex(ends[1], n) : a;
        IEnumerable<int> tiles = !rect
            ? Enumerable.Range(Math.Min(a, b), Math.Abs(b - a) + 1)
            : from row in Enumerable.Range(Math.Min(a, b) >> 4, Math.Abs((b >> 4) - (a >> 4)) + 1)
              from col in Enumerable.Range(Math.Min(a & 0xF, b & 0xF), Math.Abs((b & 0xF) - (a & 0xF)) + 1)
              select row << 4 | col;
        var list = tiles.ToList();
        if (list.Any(t => t is < 0x200 or >= 0x8000)) throw new FormatException($"list.txt line {n + 1}: block id out of bounds");
        return list;
    }

    private static int Hex(string s, int n) => int.TryParse(s, NumberStyles.HexNumber, null, out int v) ? v
        : throw new FormatException($"list.txt line {n + 1}: '{s}' is not hex");

    // ---- the patch ----

    private static string Source(Rom rom, List<string> files, List<string> routines)
    {
        var s = new StringBuilder();
        var old = GpsBlocks.Read(rom);
        var oldSlots = Enumerable.Range(0, MaxRoutines).Select(i => rom.ReadValue(RoutineSlots + i * 3, 3)).ToArray();
        if (old is not null)
        {
            // gps.exe's clean_hack. Before asm version 3 the routine table started a byte earlier.
            foreach (int addr in old.Blocks.Values.Select(b => b.Addr).Distinct()) s.AppendLine($"autoclean ${addr:X6}");
            s.AppendLine($"autoclean ${old.Tables.Pointers:X6}");
            if (old.Tables.Pointers2 >= 0) s.AppendLine($"autoclean ${old.Tables.Pointers2:X6}\nautoclean ${old.Tables.Banks:X6}");
            int slots = old.AsmVersion >= 3 ? RoutineSlots : RoutineSlots - 1;
            for (int i = 0; i < MaxRoutines; i++)
                if (rom.ReadValue(slots + i * 3, 3) is var p and not 0xFFFFFF) s.AppendLine($"autoclean ${p:X6}");
        }
        s.AppendLine("incsrc \"main.asm\"");

        // gps.exe's shared.asm, less its read3 of the routine table: the table is rewritten here.
        s.AppendLine("""
            macro include_once(target, base, offset)
                if !<base> != 1
                    !<base> = 1
                    pushpc
                    namespace off
                    freecode cleaned
                    #<base>:
                    incsrc "<target>"
                    ORG <offset>*3+$0CB66F
                    dl <base>
                    namespace !__pd_gps_ns
                    pullpc
                endif
            endmacro
            """);
        for (int i = 0; i < routines.Count; i++)
            s.AppendLine($"!{routines[i]} = 0\nmacro {routines[i]}()\n\t%include_once(\"routines/{routines[i]}.asm\", {routines[i]}, ${i:X2})\n\tJSL {routines[i]}\nendmacro");

        for (int i = 0; i < files.Count; i++)
            s.AppendLine($"""
                print "@@pd_gps {i}"
                freecode cleaned
                __pd_gps_{i}:
                !__pd_gps_ns = __pd_gps_{i}_ns
                namespace __pd_gps_{i}_ns
                incsrc "blocks/{files[i]}"
                namespace off
                __pd_gps_{i}_end:
                """);

        // A routine no block used leaves its slot empty, and so does a slot the old run filled.
        for (int i = 0; i < MaxRoutines; i++)
            if (i < routines.Count) s.AppendLine($"if !{routines[i]} == 0\n\torg ${RoutineSlots + i * 3:X6}\n\tdl $FFFFFF\nendif");
            else if (old is not null && oldSlots[i] != 0xFFFFFF) s.AppendLine($"org ${RoutineSlots + i * 3:X6}\ndl $FFFFFF");
        return s.ToString();
    }

    /// <summary>The acts-like tables main.asm puts back: LM's current ones (0x8000 tiles, the
    /// second half only when LM made pages 40+), unchanged — the project's acts-as edits are
    /// written by the build after the blocks, so the table has one owner.</summary>
    private static byte[] ActsLikes(Rom rom, bool lmHi)
    {
        var acts = new byte[0x10000];
        Array.Copy(rom.Data, rom.FileOffset(rom.ReadValue(0x06F624, 3)), acts, 0, 0x8000);
        if (lmHi) Array.Copy(rom.Data, rom.FileOffset(rom.ReadValue(0x06F63A, 3) + 0x8000), acts, 0x8000, 0x8000);
        return acts;
    }

    // ---- after the patch ----

    /// <summary>Each file's address, checked the way gps.exe checks it.</summary>
    private static (int[] Addrs, List<string> Errors) Assembled(Rom rom, Asar.Result r, List<string> files)
    {
        var addrs = new int[files.Count];
        var errors = new List<string>();
        for (int i = 0; i < files.Count; i++)
        {
            int entry = r.Labels[$"__pd_gps_{i}"], size = r.Labels[$"__pd_gps_{i}_end"] - entry;
            addrs[i] = entry;
            if (size is < 0 or > 0x8000) errors.Add($"{files[i]}: block is {size} bytes. Is it a patch?");
            else if (rom.ReadByte(entry) is not (0x42 or 0x37)) errors.Add($"{files[i]}: lacks a db $42 or db $37 header");
        }
        return (addrs, errors);
    }

    private static void FillTables(Rom rom, IReadOnlyDictionary<int, string> blocks, List<string> files, int[] addrs)
    {
        var t = GpsBlocks.Read(rom)!.Tables;
        foreach (var (tile, file) in blocks)
        {
            int addr = addrs[files.IndexOf(file)];
            rom.Data[rom.FileOffset(t.Banks) + tile] = (byte)(addr >> 16);
            int ptr = tile < 0x4000 ? rom.FileOffset(t.Pointers) + tile * 2 : rom.FileOffset(t.Pointers2) + (tile - 0x4000) * 2;
            rom.Data[ptr] = (byte)addr;
            rom.Data[ptr + 1] = (byte)(addr >> 8);
        }
    }

    /// <summary>The prints each block made, split on the markers the patch prints before each.</summary>
    private static string[][] PrintsByBlock(string[] prints, int count)
    {
        var by = Enumerable.Range(0, count).Select(_ => new List<string>()).ToArray();
        int at = -1;
        foreach (var p in prints)
            if (p.StartsWith("@@pd_gps ")) at = int.Parse(p[9..]);
            else if (at >= 0) by[at].Add(p);
        return [.. by.Select(l => l.ToArray())];
    }
}
