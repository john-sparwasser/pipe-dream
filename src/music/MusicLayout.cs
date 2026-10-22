namespace PipeDream.Music;

/// <summary>One song laid out in ARAM with our engine: what the SNES side uploads and the engine reads.</summary>
/// <param name="Ram">The whole 64 KB as it is to be: engine, song info, sample directory, song, samples.</param>
/// <param name="EchoDelay">The longest echo delay the song asks for ($F1 or $FA $04): its buffer is the top 2 KB x delay.</param>
/// <param name="Free">Bytes left between the last sample and the echo buffer.</param>
public sealed record AramImage(byte[] Ram, int SongBase, int InstrumentTable, int DirPage, int SamplesEnd, int EchoDelay, int Free,
    IReadOnlyList<MmlDiagnostic> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Our ARAM map, chosen here rather than copied from AddmusicK (reference/AMK_ENGINE.md):
/// <code>
/// $0000-$00EF  direct page          $0100-$01FF  stack
/// $0200-$03EF  per-channel state    $03F0        song info: song (word), instrument table (word), directory page
/// $0400        the engine           next page    the sample directory, four bytes a sample (start, loop)
///              the song             then         its samples, one after another
/// top          the echo buffer: 2 KB a unit of the song's longest delay ($FF00 for none)
/// </code>
/// A song's samples are its groups' (in order) then its own, which is how the compiler numbers them.
/// </summary>
public static class MusicLayout
{
    public const int EngineBase = 0x0400, SongInfo = 0x03F0;

    /// <param name="engine">The assembled engine, loaded at $0400.</param>
    /// <param name="groupSamples">A sample group's samples by name ("default" etc.), or null if there is no such group.</param>
    /// <param name="sample">A sample file by the name a song gives it, or null.</param>
    public static AramImage Build(byte[] engine, MmlSong song, Func<string, IReadOnlyList<byte[]>?> groupSamples, Func<string, byte[]?> sample)
    {
        var problems = new List<MmlDiagnostic>(song.Diagnostics);
        var empty = new AramImage([], 0, 0, 0, 0, 0, 0, problems);
        if (problems.Count > 0) return empty;

        // The samples, in the order the compiler numbers them.
        var groups = song.SampleGroups.Count > 0 || song.SamplesDeclared ? song.SampleGroups : ["default"];
        var brrs = new List<(string Name, byte[] Data)>();
        foreach (var g in groups)
        {
            if (groupSamples(g) is not { } list) { problems.Add(new MmlDiagnostic(1, 1, $"There is no sample group \"{g}\".")); continue; }
            brrs.AddRange(list.Select((b, i) => ($"{g} {i}", b)));
        }
        foreach (var name in song.Samples)
        {
            var data = (song.Path is { Length: > 0 } p ? sample(p.TrimEnd('/') + "/" + name) : null) ?? sample(name);
            if (data is null) { problems.Add(new MmlDiagnostic(1, 1, $"The sample \"{name}\" is not in the project.")); continue; }
            brrs.Add((name, data));
        }
        foreach (var (name, data) in brrs)
            if (data.Length < 11 || (data.Length - 2) % 9 != 0) problems.Add(new MmlDiagnostic(1, 1, $"\"{name}\" is not a BRR sample (its length is not 2 + a multiple of 9)."));
        if (problems.Count > 0) return empty;

        var ram = new byte[0x10000];
        int engineEnd = EngineBase + engine.Length;
        Array.Copy(engine, 0, ram, EngineBase, engine.Length);
        int dir = (engineEnd + 0xFF) & ~0xFF;
        int songBase = dir + 4 * brrs.Count;

        var compiled = MmlCompiler.Compile(song, songBase, g => groupSamples(g)?.Count ?? 0);
        if (!compiled.Ok) return empty with { Problems = compiled.Diagnostics };
        if (songBase + compiled.Bytes.Length > 0x10000) return empty with { Problems = [new MmlDiagnostic(1, 1, "The song does not fit in ARAM.")] };
        Array.Copy(compiled.Bytes, 0, ram, songBase, compiled.Bytes.Length);

        int at = songBase + compiled.Bytes.Length;
        for (int i = 0; i < brrs.Count; i++)
        {
            var data = brrs[i].Data;
            int start = at, loop = start + (data[0] | data[1] << 8);
            int len = data.Length - 2;
            if (start + len <= 0x10000) Array.Copy(data, 2, ram, start, len);
            ram[dir + 4 * i] = (byte)start; ram[dir + 4 * i + 1] = (byte)(start >> 8);
            ram[dir + 4 * i + 2] = (byte)loop; ram[dir + 4 * i + 3] = (byte)(loop >> 8);
            at += len;
        }

        int delay = EchoDelay(song);
        int echoStart = delay == 0 ? 0xFF00 : 0x10000 - delay * 0x800;
        if (at > echoStart)
            return empty with { Problems = [new MmlDiagnostic(1, 1, $"Echo buffer exceeded total space in ARAM. (The song and its samples end at ${at:X4}; the echo buffer starts at ${echoStart:X4}.)")] };

        ram[SongInfo] = (byte)songBase; ram[SongInfo + 1] = (byte)(songBase >> 8);
        ram[SongInfo + 2] = (byte)compiled.InstrumentTable; ram[SongInfo + 3] = (byte)(compiled.InstrumentTable >> 8);
        ram[SongInfo + 4] = (byte)(dir >> 8);
        return new AramImage(ram, songBase, compiled.InstrumentTable, dir >> 8, at, delay, echoStart - at, []);
    }

    /// <summary>The longest echo delay anything in the song sets: $F1's first parameter or $FA $04's.</summary>
    public static int EchoDelay(MmlSong song)
    {
        int max = 0;
        void Walk(IEnumerable<Cmd> cmds)
        {
            foreach (var c in cmds)
                switch (c)
                {
                    case HexCmd { Bytes: [0xF1, var d, ..] }: max = Math.Max(max, d); break;
                    case HexCmd { Bytes: [0xFA, 0x04, var d] }: max = Math.Max(max, d); break;
                    case LoopCmd l: Walk(l.Body); break;
                    case LabelLoopCmd l: Walk(l.Body); break;
                    case SuperLoopCmd l: Walk(l.Body); break;
                    case TripletCmd t: Walk(t.Body); break;
                    case RemoteDefCmd r: Walk(r.Body); break;
                }
        }
        foreach (var ch in song.Channels) if (ch is not null) Walk(ch);
        foreach (var r in song.RemoteDefs) Walk(r.Body);
        return max & 0x0F;
    }
}
