using System.Text;
using System.Text.RegularExpressions;

namespace PipeDream;

/// <summary>
/// Inserts the project's overworld sprites at build, BOWSIE's job (https://github.com/arinsuwu/bowsie, v1.21) done with its
/// own ASM (data/owspritedriver, BSD-2-Clause, its LICENSE beside it), for the custom sprites of the "overworld" kind in
/// "overworld NN" slots (<see cref="PixiImport.Overworld"/>): the shared routines go in first and become
/// <c>%name()</c> macros, then each sprite in freespace of its own (so an error names it), then BOWSIE's vldc9 system with the
/// init/main pointer tables and the extra-byte size table, which Lunar Magic's custom sprite list ($0EF55D) is read by.
/// Every build starts from the base, so nothing is ever cleaned up. Only the vldc9 method: no Overworld Revolution, no custom
/// method; MaxTile off (see <see cref="Defines"/>).
/// </summary>
public static partial class OwSpriteInsert
{
    public sealed record Result(bool Ok, string[] Errors, string[] Warnings, Asar.WrittenBlock[] Written);

    private static readonly string VirtualDir = Path.Combine(Path.GetTempPath(), "pipedream-owsprites");

    /// <summary>Where BOWSIE keeps its signature, then the pointer table's address and each shared routine's: a later BOWSIE
    /// run on the built ROM finds them and cleans our insertion up as its own.</summary>
    public const int Used = 0x04EF3E;
    private static readonly byte[] Signature = [0x00, 0xCA, 0xC7, 0x05];

    /// <summary>BOWSIE's limit, the sprite tables' size: $14C8 and on, 24 words each.</summary>
    private const int Slots = 24;

    public static Result Insert(Rom rom, ProjectFile data)
    {
        var listed = Listed(data);
        var warnings = new List<string>();
        var written = new List<Asar.WrittenBlock>();
        Result Fail(string e) => new(false, [e], [.. warnings], [.. written]);
        if (listed.Count == 0) return new(true, [], [], []);
        if (Enumerable.Range(0, 4).All(i => rom.ReadByte(0x048000 + i) == "YFOR"[i]))
            return Fail("this base has Overworld Revolution, which Pipe Dream does not insert BOWSIE for");
        if (Signature.Select((b, i) => rom.ReadByte(Used + i) == b).All(x => x))
            return Fail("this base already has BOWSIE in it: build from a base without it");
        if (data.SpriteSlots.FirstOrDefault(p => PixiImport.SlotKind(p.Key) == PixiImport.Overworld && !listed.ContainsKey(Number(p.Key))) is { Key: { } bad })
            return Fail($"slot {bad} names {data.SpriteSlots[bad]}, which is not an overworld sprite in the project");

        var mem = new Dictionary<string, byte[]>();
        void Put(string path, string text) => mem[Full(path)] = Encoding.UTF8.GetBytes(text);
        foreach (string name in typeof(OwSpriteInsert).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("owspritedriver/")))
            Put(name["owspritedriver/".Length..], Resource(name));
        // Each sprite in a folder of its own, its included files beside it as they were beside it on disk.
        foreach (var (number, (_, entry)) in listed)
        {
            Put($"sprites/{number:X2}/sprite.asm", entry.Source);
            foreach (var (path, bytes) in entry.Files) mem[Full($"sprites/{number:X2}/{path}")] = bytes;
        }
        int lmver = LmVersion(rom);
        Put("bowsie_defines.asm", Defines(lmver));
        const string head = "warnings disable Wfreespace_leaked\nincsrc \"vldc9_defines.asm\"\nincsrc \"maxtile_defines.asm\"\n";

        // The shared routines, all in one assembly, each in a namespace of its own as BOWSIE puts them.
        var names = mem.Keys.Select(k => Path.GetRelativePath(VirtualDir, k).Replace('\\', '/'))
            .Where(k => k.StartsWith("routines/")).Select(k => k[9..^4]).Order(StringComparer.Ordinal).ToList();
        var src = new StringBuilder(head);
        foreach (string r in names) src.Append($"macro {r}()\n    JSL {r}_{r}\nendmacro\n");      // routines call each other
        src.Append("freecode cleaned\n");
        foreach (string r in names) src.Append($"namespace {r}\n{r}:\nincsrc \"routines/{r}.asm\"\nnamespace off\n");
        Put("_routines.asm", src.ToString());
        var rr = Asar.Patch(rom, Full("_routines.asm"), mem);
        if (!rr.Ok) return Fail("shared routines: " + string.Join("; ", Local(rr.Errors)));
        written.AddRange(rr.Blocks);
        var routines = names.Select(r => rr.Labels[$"{r}_{r}"]).ToList();
        Put("ssr.asm", string.Concat(names.Select((r, i) => $"macro {r}()\n    JSL ${routines[i]:X6}\nendmacro\n")));

        // Each sprite: its init and main into the pointer tables, its !extra (2 unless it says) into the size table.
        var init = Enumerable.Repeat(0x048414, 0x7E).ToArray();                    // an RTL in bank 4, as BOWSIE fills unused numbers
        var main = (int[])init.Clone();
        var sizes = Enumerable.Repeat((byte)3, 0x7F).ToArray();
        foreach (var (number, (file, entry)) in listed)
        {
            string ns = $"OwSprite{number:X2}";
            Put("_sprite.asm", head + "incsrc \"ssr.asm\"\nincsrc \"macro.asm\"\n"
                + $"namespace {ns}\nfreecode cleaned\nincsrc \"sprites/{number:X2}/sprite.asm\"\n!extra ?= 2\nnamespace off\nOwSpriteExtra = !extra\n");
            var r = Asar.Patch(rom, Full("_sprite.asm"), mem);
            if (!r.Ok) return Fail($"{file}: " + string.Join("; ", Local(r.Errors)));
            warnings.AddRange(Local(r.Warnings).Select(w => $"{file}: {w}"));
            written.AddRange(r.Blocks);
            if (!r.Labels.TryGetValue($"{ns}_init", out init[number - 1]) || !r.Labels.TryGetValue($"{ns}_main", out main[number - 1]))
                return Fail($"{file} needs both init: and main: labels");
            int extra = r.Labels["OwSpriteExtra"];
            if (extra is < 0 or > 8) return Fail($"{file}: !extra is {extra}, BOWSIE takes 0-8");
            sizes[number - 1] = (byte)(3 + extra);
            if (lmver > 350 && extra != ExtraOf(entry.Source))
                warnings.Add($"{file}: its !extra came out {extra}, not what the editor read from it, so its sprites' extra bytes were cut or padded");
        }
        mem[Full("extra_size.bin")] = sizes;

        // The system: BOWSIE's signature and pointers, then vldc9.asm and the tables it names.
        var sys = new StringBuilder(head).Append("incsrc \"ssr.asm\"\nincsrc \"macro.asm\"\n");
        sys.Append($"org ${Used:X6}\n    dd $05C7CA00\n    dl ow_sprite_init_ptrs\n");
        foreach (int at in routines) sys.Append($"    dl ${at:X6}\n");
        sys.Append("    dl $FFFFFF\n");
        sys.Append(Resource("owspritedriver/vldc9.asm"));
        sys.Append("\now_sprite_init_ptrs:\n").Append(string.Concat(init.Select(a => $"    dl ${a:X6}\n")));
        sys.Append("ow_sprite_main_ptrs:\n").Append(string.Concat(main.Select(a => $"    dl ${a:X6}\n")));
        Put("_system.asm", sys.ToString());
        var d = Asar.Patch(rom, Full("_system.asm"), mem);
        if (!d.Ok) return Fail("the overworld sprite system: " + string.Join("; ", Local(d.Errors)));
        warnings.AddRange(Local(d.Warnings).Select(w => $"the overworld sprite system: {w}"));
        written.AddRange(d.Blocks);
        return new(true, [], [.. warnings], [.. written]);
    }

    /// <summary>The Lunar Magic version BOWSIE's ASM is told (3.33 is 333): what the ROM says, or 3.60 for a ROM without
    /// Lunar Magic's string, as <see cref="Overworld.SpriteExtraCount"/> reads one.</summary>
    public static int LmVersion(Rom rom)
    {
        int v = rom.FileOffset(0x0FF0B4);
        var d = rom.Data;
        return d[v] == '3' && d[v + 1] == '.' && char.IsAsciiDigit((char)d[v + 2]) && char.IsAsciiDigit((char)d[v + 3])
            ? 300 + 10 * (d[v + 2] - '0') + (d[v + 3] - '0') : 360;
    }

    /// <summary>The extra bytes each number's entries will carry once built, for the editor, which places sprites before the
    /// build has assembled anything: a listed sprite's <c>!extra</c> as its source sets it (2 if not), the rest none. Null with
    /// no sprites listed, or a Lunar Magic before 3.51, whose entries are always four bytes.</summary>
    public static int[]? ExtraCounts(Rom rom, ProjectFile data)
    {
        var listed = Listed(data);
        if (listed.Count == 0 || LmVersion(rom) < 351) return null;
        var counts = new int[0x80];
        foreach (var (n, (_, entry)) in listed) counts[n] = ExtraOf(entry.Source);
        return counts;
    }

    /// <summary>The overworld sprites the project runs: each "overworld NN" slot's number → the library sprite in it.
    /// A slot naming a sprite that is missing or not an overworld one is left out (<see cref="Insert"/> refuses it).</summary>
    public static SortedDictionary<int, (string Name, ProjectFile.SpriteEntry Entry)> Listed(ProjectFile data)
    {
        var listed = new SortedDictionary<int, (string, ProjectFile.SpriteEntry)>();
        foreach (var (slot, name) in data.SpriteSlots)
            if (PixiImport.SlotKind(slot) == PixiImport.Overworld && data.Sprites.TryGetValue(name, out var e) && e.Kind == PixiImport.Overworld)
                listed[Number(slot)] = (name, e);
        return listed;
    }

    private static int Number(string slot) => Convert.ToInt32(slot.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], 16);

    // ponytail: a plain number only; a computed !extra is caught by the build's warning, not read here.
    private static int ExtraOf(string source)
        => ExtraDefine().Match(source) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 2;

    [GeneratedRegex(@"(?m)^\s*!extra\s*=\s*(\d+)\s*(;|$)")]
    private static partial Regex ExtraDefine();

    /// <summary>bowsie_defines.asm as BOWSIE writes it. MaxTile is off: its LoROM code (maxtile.asm, not shipped) builds on
    /// Vitor Vilela's SA-1 MaxTile, which declares no licence. Sprites draw straight into OAM instead, a setting BOWSIE has.</summary>
    private static string Defines(int lmver) => $$"""
        !bowsie_ow_slots = {{Slots}}
        !bowsie_version = 1
        !bowsie_subversion = 21
        !bowsie_contiguous = 0
        !bowsie_maxtile = 0
        !bowsie_lmver = {{lmver}}
        if read1($00FFD5) == $23
            if read1($00FFD7) == $0D
                fullsa1rom
            else
                sa1rom
            endif
            !sa1 = 1
            !dp = $3000
            !addr = $6000
            !bank = $000000
        else
            lorom
            !sa1 = 0
            !dp = $0000
            !addr = $0000
            !bank = $800000
        endif
        !bowsie_owrev = 0
        !bowsie_widescreen_ow = equal(read1($04FB2A), $5C)
        optimize dp always
        dpbase !dp
        optimize address mirrors
        bank auto

        """;

    private static string Full(string path) => Path.Combine(VirtualDir, path);

    private static string Resource(string name)
    {
        using var s = typeof(OwSpriteInsert).Assembly.GetManifestResourceStream(name)!;
        return new StreamReader(s).ReadToEnd();
    }

    private static string[] Local(string[] messages)
    {
        string folder = Path.GetFullPath(VirtualDir).Replace('\\', '/') + "/";
        return [.. messages.Select(m => m.Replace('\\', '/').IndexOf(folder, StringComparison.OrdinalIgnoreCase) is var i and >= 0
                                         ? m.Remove(i, folder.Length) : m)];
    }
}
