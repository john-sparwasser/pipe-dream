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

        if (ForeignSoundCode(rom) is { } at)
            return Fail([$"the ROM's sound code at ${at:X6} is not SMW's (AddmusicK or another music patch already in it?): build from a ROM without one"]);
        var (image, Group, Sample, songs, globals, errs) = Prepare(rom, m, slots, warnings);
        if (errs.Length > 0) return Fail(errs);

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
        Renumber(rom, asm, warnings);
        Put("music.asm", Encoding.UTF8.GetBytes(asm.ToString()));

        var r = Asar.Patch(rom, Path.Combine(VirtualDir, "music.asm"), mem);
        if (!r.Ok) return Fail([.. r.Errors.Select(e => "music: " + e)]);
        return new(true, [], [.. warnings], r.Blocks, songs.Count, r.Labels);
    }

    /// <summary>What every local song is laid out on: the engine, the effects, the global songs and the resident samples; and the
    /// project's songs in the list, parsed.</summary>
    private static (BaseImage Image, Func<string, IReadOnlyList<byte[]>?> Group, Func<string, byte[]?> Sample,
        Dictionary<int, MmlSong> Songs, List<(int Number, MmlSong Song)> Globals, string[] Errors)
        Prepare(Rom rom, MusicState m, List<MusicState.SongSlot> slots, List<string> warnings)
    {
        var (engine, _, asmErrors) = MusicEngine.Assemble();
        if (asmErrors.Length > 0) return (null!, null!, null!, [], [], ["the music engine: " + string.Join("; ", asmErrors)]);
        var stock = SmwSound.StockSamples(rom);
        var songs = new Dictionary<int, MmlSong>();
        foreach (var slot in slots)
        {
            if (!m.Songs.TryGetValue(slot.Path, out var text)) { warnings.Add($"song {slot.Number:X2} names {slot.Path}, which is not in the project"); continue; }
            songs[slot.Number] = MmlParser.Parse(text);
        }
        var globals = slots.Where(s => s.Global && songs.ContainsKey(s.Number)).Select(s => (s.Number, songs[s.Number])).ToList();
        var group = Groups(m, stock);
        var image = MusicLayout.Base(engine, Effects(rom, m, warnings), globals, group, Resident(m, stock));
        return (image, group, name => m.Samples.GetValueOrDefault(name), songs, globals, [.. image.Problems.Select(p => p.ToString())]);
    }

    /// <summary>How much of the ARAM a local song may use <paramref name="mml"/> takes, laid out as the build lays it out: from where
    /// local songs start to the echo buffer. The song stands in for any of the project's (a global one is counted as if local).</summary>
    public static (int Used, int Room, string? Error) Aram(Rom rom, ProjectFile data, string mml)
    {
        var m = data.Music;
        var slots = m.SongList.Where(s => s.Number is > 0 and < 0xFF).ToList();
        var (image, group, sample, _, _, errs) = Prepare(rom, m, slots, []);
        if (errs.Length > 0) return (0, 0, errs[0]);
        var local = MusicLayout.Local(image, MmlParser.Parse(mml), group, sample);
        if (!local.Ok) return (0, 0, local.Problems[0].Message);
        return (local.SamplesEnd - image.LocalBase, local.SamplesEnd + local.Free - image.LocalBase, null);
    }

    /// <summary>The first place data/musicdriver/snes.asm hooks that does not hold SMW's own code, or null. On an SA-1 build the
    /// pack has moved the NMI's `LDA $1DFB` to $7DFB, its home there.</summary>
    private static int? ForeignSoundCode(Rom rom)
    {
        byte songHigh = rom.Map is Sa1Map ? (byte)0x7D : (byte)0x1D;
        (int At, byte[] Smw)[] hooks =
        [
            (0x0080E8, [0xA9, 0x00, 0x8D]), (0x0080FD, [0xA9, 0x00, 0x8D]), (0x00810E, [0xA9, 0xB1, 0x8D]),
            (0x008148, [0xA9, 0xD6, 0x8D]), (0x008159, [0xA9, 0x00, 0x8D]), (0x008179, [0xAD, 0xFB, songHigh]),
        ];
        foreach (var (at, smw) in hooks)
            if (!rom.Data.AsSpan(rom.FileOffset(at), smw.Length).SequenceEqual(smw)) return at;
        return null;
    }

    // ---- SMW's song numbers as AddmusicK's ----

    /// <summary>SMW numbers its songs per music bank (the level's, which also holds the songs every level shares; the map's; the
    /// ending's); AddmusicK numbers them once, in its default list, and a project's songs keep those numbers. The pairs were found
    /// by playing each of SMW's songs in its own engine against AddmusicK's originals compiled by ours. SMW's $2E-$34 (the Bowser
    /// fight's pauses) start no song in SMW and are no song here either, so the music fades.</summary>
    public static readonly IReadOnlyDictionary<int, int> LevelSongs = new Dictionary<int, int>
    {
        [0x01] = 0x0A, [0x02] = 0x0B, [0x03] = 0x0C, [0x04] = 0x0D, [0x05] = 0x0E, [0x06] = 0x0F, [0x07] = 0x10, [0x08] = 0x11,
        [0x09] = 0x01, [0x0A] = 0x02, [0x0B] = 0x03, [0x0C] = 0x04, [0x0D] = 0x05, [0x0E] = 0x06, [0x0F] = 0x07, [0x10] = 0x07,
        [0x11] = 0x08, [0x12] = 0x12, [0x13] = 0x13, [0x14] = 0x09, [0x15] = 0x14, [0x16] = 0x0D, [0x17] = 0x24, [0x18] = 0x23,
        [0x19] = 0x1F, [0x1A] = 0x20, [0x1B] = 0x21, [0x1C] = 0x25, [0x1D] = 0x22,
    };
    private static readonly IReadOnlyDictionary<int, int> MapSongs = new Dictionary<int, int>
    {
        [0x01] = 0x15, [0x02] = 0x17, [0x03] = 0x18, [0x04] = 0x19, [0x05] = 0x1A, [0x06] = 0x1B, [0x07] = 0x1C, [0x08] = 0x16, [0x09] = 0x1D,
    };
    private static readonly IReadOnlyDictionary<int, int> EndingSongs = new Dictionary<int, int> { [0x09] = 0x26, [0x0A] = 0x27, [0x0B] = 0x28 };

    /// <summary>Every place SMW's code or tables name a song for $1DFB (reference/smw-disasm), with the number SMW has there.</summary>
    private static readonly (int At, int Vanilla, IReadOnlyDictionary<int, int> Bank)[] SongSites =
    [
        (0x0094B3, 0x15, LevelSongs), (0x009737, 0x16, LevelSongs), (0x00C526, 0x14, LevelSongs), (0x00C9BD, 0x11, LevelSongs),
        (0x00D0DE, 0x0A, LevelSongs), (0x00E304, 0x0E, LevelSongs), (0x00EEC3, 0x0C, LevelSongs), (0x00F60B, 0x09, LevelSongs),
        (0x018784, 0x0B, LevelSongs), (0x01AB08, 0x0E, LevelSongs), (0x01C0F0, 0x0C, LevelSongs), (0x01C586, 0x0D, LevelSongs),
        (0x01D04F, 0x0B, LevelSongs), (0x01E216, 0x10, LevelSongs), (0x01FB2E, 0x0B, LevelSongs), (0x028968, 0x0E, LevelSongs),
        (0x03809D, 0x0B, LevelSongs), (0x0398E7, 0x0B, LevelSongs), (0x03A702, 0x17, LevelSongs), (0x03A7A8, 0x1D, LevelSongs),
        (0x03A7C2, 0x18, LevelSongs), (0x03ABF4, 0x1B, LevelSongs), (0x03AC53, 0x1C, LevelSongs), (0x03CE9A, 0x0B, LevelSongs),
        (0x03A849, 0x19, LevelSongs), (0x03A84A, 0x1A, LevelSongs),                                   // BowserSoundMusic's songs
        (0x0584DB, 0x02, LevelSongs), (0x0584DC, 0x06, LevelSongs), (0x0584DD, 0x01, LevelSongs), (0x0584DE, 0x08, LevelSongs),
        (0x0584DF, 0x07, LevelSongs), (0x0584E0, 0x03, LevelSongs), (0x0584E1, 0x05, LevelSongs), (0x0584E2, 0x12, LevelSongs),
        (0x0096C7, 0x01, MapSongs), (0x0483D2, 0x08, MapSongs),                                       // the title; Bowser's valley
        .. new[] { 0x02, 0x03, 0x04, 0x06, 0x07, 0x09, 0x05 }.SelectMany((v, i) => new[] { (0x048D8A + i, v, MapSongs), (0x04DBC8 + i, v, MapSongs) }),
        (0x0C9447, 0x09, EndingSongs), (0x0CA40C, 0x0A, EndingSongs), (0x0CA5C2, 0x0B, EndingSongs),
    ];

    /// <summary>SMW's song numbers rewritten as AddmusicK's, where the ROM still has SMW's own (a patch that changed one keeps it).</summary>
    private static void Renumber(Rom rom, StringBuilder asm, List<string> warnings)
    {
        asm.Append('\n');
        foreach (var (at, vanilla, bank) in SongSites)
        {
            int now = rom.ReadByte(at);
            if (now == vanilla) asm.Append($"org ${at:X6}\n    db ${bank[vanilla]:X2}\n");
            else if (now != bank[vanilla]) warnings.Add($"${at:X6} asks for song {now:X2}, not SMW's {vanilla:X2}: left as it is");
        }
    }

    /// <summary>A sample group by name: the project's, or SMW's own from the ROM for default and optimized.</summary>
    private static Func<string, IReadOnlyList<byte[]>?> Groups(MusicState m, List<byte[]> stock) => name =>
    {
        var g = m.Groups.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (g is not null && g.Samples.All(e => m.Samples.ContainsKey(e.Path))) return g.Samples.Select(e => m.Samples[e.Path]).ToList();
        return name.ToLowerInvariant() is "default" or "optimized" ? stock : null;
    };

    /// <summary>A song as an .spc, laid out with the project's samples and effects as the build lays a local song out: the engine at its
    /// start and port 2 asking for the song, so a player boots it as the game would. ID666 tags from its #SPC block and #length.</summary>
    public static (SpcFile? Spc, string[] Errors) Spc(Rom rom, ProjectFile data, string name, string mml)
    {
        var m = data.Music;
        var (engine, labels, asmErrors) = MusicEngine.Assemble();
        if (asmErrors.Length > 0) return (null, asmErrors);
        var song = MmlParser.Parse(mml);
        var image = MusicLayout.Build(engine, song, Groups(m, SmwSound.StockSamples(rom)), s => m.Samples.GetValueOrDefault(s), Effects(rom, m, []));
        if (!image.Ok) return (null, [.. image.Problems.Select(p => p.ToString())]);
        var f = new SpcFile { Pc = labels["Start"], Sp = 0xEF, Dumper = "Pipe Dream" };
        image.Ram.CopyTo(f.Ram, 0);
        f.Ram[0xF6] = 1;                                                  // port 2: the local song
        f.Title = song.Spc.GetValueOrDefault("title") ?? Path.GetFileNameWithoutExtension(name);
        f.Game = song.Spc.GetValueOrDefault("game") ?? "";
        f.Artist = song.Spc.GetValueOrDefault("author") ?? "";
        f.Comment = song.Spc.GetValueOrDefault("comment") ?? "";
        if (song.Length?.Split(':') is [var min, var sec] && int.TryParse(min, out int mi) && int.TryParse(sec, out int se)) f.Seconds = mi * 60 + se;
        return (f, []);
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

    /// <summary>SMW's sound effects from the ROM, with the project's own ($1DF9 and $1DFC lists and their sources) in the slots it
    /// fills. Their instruments are SMW's either way.</summary>
    private static SoundEffects Effects(Rom rom, MusicState m, List<string> warnings)
    {
        var instruments = SoundEffects.SmwInstruments(rom);
        var smw = SmwSound.Effects(rom, instruments);
        if (m.Sfx1DF9.Count == 0 && m.Sfx1DFC.Count == 0) return smw;
        IReadOnlyList<byte[]?> Port(List<MusicState.SfxSlot> slots, string folder, IReadOnlyList<byte[]?> theirs)
        {
            var list = new byte[]?[Math.Max(theirs.Count, slots.Count == 0 ? 0 : slots.Max(s => s.Slot) + 1)];
            for (int i = 0; i < theirs.Count; i++) list[i] = theirs[i];
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
        return new SoundEffects(instruments, Port(m.Sfx1DF9, "1DF9", smw.Port1DF9), Port(m.Sfx1DFC, "1DFC", smw.Port1DFC));
    }
}
