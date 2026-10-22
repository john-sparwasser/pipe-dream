using System.Text;
using PipeDream.Music;

namespace PipeDream;

/// <summary>
/// Inserts a project's music at build (reference/AMK_PORT.md, phase 5), AddmusicK's job done our way: our engine, the sound
/// effects and the global songs go to the SPC at power-on; each local song is laid out with its samples and uploaded when the
/// game asks for it in $1DFB. data/musicdriver/snes.asm is the main CPU's side; the block lists it reads are made here. SMW's own
/// samples and sound effects come from the base ROM unless the project has its own.
/// </summary>
public static class MusicInsert
{
    public sealed record Result(bool Ok, string[] Errors, string[] Warnings, Asar.WrittenBlock[] Written, int Songs,
        IReadOnlyDictionary<string, int> Labels);

    /// <summary>Where the in-memory files sit. Nothing is written there; see PatchInsert.</summary>
    private static readonly string VirtualDir = Path.Combine(Path.GetTempPath(), "pipedream-music");

    /// <summary>A block's bytes at most: well under a LoROM bank, with room for its RATS tag.</summary>
    private const int MaxBlock = 0x7000;

    public static Result Insert(Rom rom, ProjectFile data)
    {
        var m = data.Music;
        var warnings = new List<string>();
        Result Fail(params string[] e) => new(false, e, [.. warnings], [], 0, new Dictionary<string, int>());
        var slots = m.SongList.Where(s => s.Number is > 0 and < 0xFF).OrderBy(s => s.Number).ToList();
        if (slots.Count == 0) return new(true, [], [], [], 0, new Dictionary<string, int>());

        var (engine, _, asmErrors) = MusicEngine.Assemble();
        if (asmErrors.Length > 0) return Fail(["the music engine: " + string.Join("; ", asmErrors)]);

        var stock = SmwSound.StockSamples(rom);
        IReadOnlyList<byte[]>? Group(string name)
        {
            var g = m.Groups.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (g is not null && g.Samples.All(e => m.Samples.ContainsKey(e.Path))) return g.Samples.Select(e => m.Samples[e.Path]).ToList();
            return name.ToLowerInvariant() is "default" or "optimized" ? stock : null;     // SMW's own, from the ROM
        }
        byte[]? Sample(string name) => m.Samples.GetValueOrDefault(name);

        var sfx = Effects(rom, m, warnings);
        var songs = new Dictionary<int, MmlSong>();
        foreach (var slot in slots)
        {
            if (!m.Songs.TryGetValue(slot.Path, out var text)) { warnings.Add($"song {slot.Number:X2} names {slot.Path}, which is not in the project"); continue; }
            songs[slot.Number] = MmlParser.Parse(text);
        }
        var globals = slots.Where(s => s.Global && songs.ContainsKey(s.Number)).Select(s => (s.Number, songs[s.Number])).ToList();
        var image = MusicLayout.Base(engine, sfx, globals, Group, Resident(m, stock));
        if (!image.Ok) return Fail([.. image.Problems.Select(p => p.ToString())]);

        // Power-on: the base, and SMW's samples (with an empty song) so effects and global songs sound before a local song comes.
        var boot = MusicLayout.Local(image, MmlParser.Parse("#amk 2\n#0 r1"), Group, Sample);
        if (!boot.Ok) return Fail([.. boot.Problems.Select(p => "the power-on samples: " + p)]);
        var bootRam = boot.Ram;
        bootRam[MusicLayout.SongInfo] = 0; bootRam[MusicLayout.SongInfo + 1] = 0;          // no local song yet
        var bootBlocks = Blocks(bootRam, MusicLayout.EngineBase, boot.SamplesEnd).Append(Block(bootRam, MusicLayout.SongInfo, 0x10)).ToList();

        var lists = new Dictionary<int, List<byte[]>>();
        var errors = new List<string>();
        foreach (var slot in slots.Where(s => !s.Global && songs.ContainsKey(s.Number)))
        {
            var local = MusicLayout.Local(image, songs[slot.Number], Group, Sample);
            if (!local.Ok) { errors.AddRange(local.Problems.Select(p => $"song {slot.Number:X2} ({slot.Path}): {p}")); continue; }
            lists[slot.Number] = [.. Blocks(local.Ram, image.LocalBase, local.SamplesEnd), Block(local.Ram, MusicLayout.SongInfo, 5)];
        }
        if (errors.Count > 0) return Fail([.. errors]);

        // The patch, with every block an in-memory file of its own in its own free space.
        var mem = new Dictionary<string, byte[]>();
        void Put(string name, byte[] bytes) => mem[Path.Combine(VirtualDir, name)] = bytes;
        var asm = new StringBuilder(MusicEngine.Source("snes.asm"));
        void List(string label, List<byte[]> blocks, string prefix)
        {
            asm.Append($"\nfreedata\n{label}:\n    db {blocks.Count + 1}\n");
            for (int i = 0; i < blocks.Count; i++) asm.Append($"    dl {prefix}_{i}\n");
            asm.Append($"    dl {prefix}_jump\n");
            for (int i = 0; i < blocks.Count; i++)
            {
                Put($"{prefix}_{i}.bin", blocks[i]);
                asm.Append($"freedata\n{prefix}_{i}:\n    incbin \"{prefix}_{i}.bin\"\n");
            }
            asm.Append($"freedata\n{prefix}_jump:\n    dw $0000, ${MusicLayout.EngineBase:X4}\n");      // length 0: start the engine
        }
        List("BootList", bootBlocks, "Boot");
        foreach (var (n, blocks) in lists) List($"Song{n:X2}", blocks, $"S{n:X2}");
        asm.Append("\nfreedata\nSongLists:\n");
        for (int n = 0; n < 256; n++) asm.Append(lists.ContainsKey(n) ? $"    dl Song{n:X2}\n" : "    dl $000000\n");
        asm.Append("Global:\n");
        for (int n = 0; n < 256; n++) asm.Append($"    db {(globals.Any(g => g.Number == n) ? 1 : 0)}\n");
        Put("music.asm", Encoding.UTF8.GetBytes(asm.ToString()));

        var r = Asar.Patch(rom, Path.Combine(VirtualDir, "music.asm"), mem);
        if (!r.Ok) return Fail([.. r.Errors.Select(e => "music: " + e)]);
        return new(true, [], [.. warnings], r.Blocks, songs.Count, r.Labels);
    }

    /// <summary>The samples that stay in ARAM: every group's important ones (the "!" in AddmusicK's list), so a song change does not
    /// upload them again and sound effects always have theirs. SMW's own, read from the ROM when the project has no #default,
    /// are important but for $0D, $0F and $11, as AddmusicK's list marks them.</summary>
    private static List<byte[]> Resident(MusicState m, List<byte[]> stock)
    {
        var list = new List<byte[]>();
        foreach (var g in m.Groups)
            list.AddRange(g.Samples.Where(e => e.Important && m.Samples.ContainsKey(e.Path)).Select(e => m.Samples[e.Path]));
        if (!m.Groups.Any(g => g.Name.Equals("default", StringComparison.OrdinalIgnoreCase)))
            list.AddRange(stock.Where((_, i) => i is not (0x0D or 0x0F or 0x11)));
        return list;
    }

    /// <summary>ARAM [from, to) as upload blocks: a length, the address, the bytes, none longer than <see cref="MaxBlock"/>.</summary>
    private static IEnumerable<byte[]> Blocks(byte[] ram, int from, int to)
    {
        for (int at = from; at < to; at += MaxBlock) yield return Block(ram, at, Math.Min(MaxBlock, to - at));
    }

    private static byte[] Block(byte[] ram, int at, int length)
    {
        var b = new byte[4 + length];
        b[0] = (byte)length; b[1] = (byte)(length >> 8); b[2] = (byte)at; b[3] = (byte)(at >> 8);
        Array.Copy(ram, at, b, 4, length);
        return b;
    }

    /// <summary>The project's own sound effects if it has any ($1DF9 and $1DFC lists and their sources), else SMW's from the ROM.
    /// Their instruments are SMW's either way.</summary>
    private static SoundEffects Effects(Rom rom, MusicState m, List<string> warnings)
    {
        var instruments = SoundEffects.SmwInstruments(rom);
        if (m.Sfx1DF9.Count == 0 && m.Sfx1DFC.Count == 0) return SmwSound.Effects(rom, instruments);
        IReadOnlyList<byte[]?> Port(List<MusicState.SfxSlot> slots, string folder)
        {
            var list = new byte[]?[slots.Count == 0 ? 1 : slots.Max(s => s.Slot) + 1];
            var byFile = new Dictionary<string, byte[]>();
            foreach (var s in slots.Where(s => s.Flag != "*"))
            {
                if (!m.SfxSources.TryGetValue($"{folder}/{s.File}", out var text)) { warnings.Add($"sound effect {folder} {s.Slot:X2}: {s.File} is not in the project"); continue; }
                var (bytes, problems) = SfxCompiler.Compile(text, s.Flag == "?");
                warnings.AddRange(problems.Select(p => $"sound effect {folder} {s.Slot:X2}: {p}"));
                list[s.Slot] = byFile[s.File] = bytes;
            }
            foreach (var s in slots.Where(s => s.Flag == "*")) list[s.Slot] = byFile.GetValueOrDefault(s.File);
            return list;
        }
        return new SoundEffects(instruments, Port(m.Sfx1DF9, "1DF9"), Port(m.Sfx1DFC, "1DFC"));
    }
}
