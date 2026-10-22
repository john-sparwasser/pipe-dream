using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// Our SPC700 engine (src/data/musicdriver/main.asm, reference/AMK_PORT.md phase 5) assembled and run in the
/// interpreter, and compared with AddmusicK's engine playing the same song: the same notes at the same times with the same
/// pitch, volume and envelope (<see cref="SpcTrace"/>).
/// </summary>
public class MusicEngineTests
{
    /// <summary>The engine's source assembled onto a flat buffer: file offset 0 is ARAM $0400.</summary>
    internal static (byte[] Code, IReadOnlyDictionary<string, int> Labels) AssembleEngine()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "musicdriver", "main.asm");
        if (!File.Exists(path))
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "src", "data", "musicdriver", "main.asm"))) { path = Path.Combine(d.FullName, "src", "data", "musicdriver", "main.asm"); break; }
        var rom = TestRom.Create(size: 0x80000);
        var r = Asar.Patch(rom, path);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        int end = r.Labels["EngineEnd"];
        return (rom.Data[..(end - 0x0400)], r.Labels);
    }

    private const int Tick500 = 2048;          // cycles in one 500 Hz timer tick

    /// <summary>Our engine playing <paramref name="mml"/>, traced for <paramref name="seconds"/>.</summary>
    internal static SpcTrace PlayOurs(string mml, double seconds)
    {
        var (code, labels) = AssembleEngine();
        var song = Music.MmlCompiler.Compile(Music.MmlParser.Parse(mml), SongBase);
        Assert.True(song.Ok, string.Join("; ", song.Diagnostics));
        var cpu = new Spc700 { Pc = labels["Start"], Sp = 0xEF };
        Array.Copy(code, 0, cpu.Ram, 0x0400, code.Length);
        Array.Copy(song.Bytes, 0, cpu.Ram, SongBase, song.Bytes.Length);
        cpu.Ram[0x03F0] = (byte)song.InstrumentTable; cpu.Ram[0x03F1] = (byte)(song.InstrumentTable >> 8);
        cpu.FromCpu[2] = 1;                                           // play the local song
        return SpcTrace.Run(cpu, (long)(seconds * 1_024_000));
    }

    /// <summary>AddmusicK's engine playing the same text.</summary>
    internal static SpcTrace PlayTheirs(string mml, double seconds)
        => SpcTrace.Run(Spc700.FromSpc(AmkOracle.CompileText(AmkOracle.Root!, mml)), (long)(seconds * 1_024_000));

    private const int SongBase = 0x2400;

    /// <summary>What differs between two engines' key-ons: pitch, envelope, sample and volume (within one step) exactly, the time of each
    /// within <paramref name="slack"/> ticks of its start-relative position.</summary>
    internal static List<string> Differences(SpcTrace theirs, SpcTrace ours, double tickCycles, double slack = 1.5)
    {
        var d = new List<string>();
        long t0 = theirs.KeyOns.Count > 0 ? theirs.KeyOns[0].Cycle : 0, o0 = ours.KeyOns.Count > 0 ? ours.KeyOns[0].Cycle : 0;
        for (int v = 0; v < 8; v++)
        {
            var ta = theirs.KeyOns.Where(k => k.Voice == v).ToList();
            var tb = ours.KeyOns.Where(k => k.Voice == v).ToList();
            if (ta.Count != tb.Count) d.Add($"v{v} key-ons: theirs {ta.Count}, ours {tb.Count}");
            for (int i = 0; i < Math.Min(ta.Count, tb.Count); i++)
            {
                var (a, b) = (ta[i], tb[i]);
                string at = $"v{v} #{i}";
                if (a.Pitch != b.Pitch) d.Add($"{at}: pitch {a.Pitch:X4} vs {b.Pitch:X4}");
                if (a.Srcn != b.Srcn || a.Adsr1 != b.Adsr1 || a.Adsr2 != b.Adsr2 || a.Gain != b.Gain) d.Add($"{at}: instrument {a.Srcn:X2}/{a.Adsr1:X2}{a.Adsr2:X2}/{a.Gain:X2} vs {b.Srcn:X2}/{b.Adsr1:X2}{b.Adsr2:X2}/{b.Gain:X2}");
                if (Math.Abs(a.VolL - b.VolL) > 1 || Math.Abs(a.VolR - b.VolR) > 1) d.Add($"{at}: volume {a.VolL:X2}/{a.VolR:X2} vs {b.VolL:X2}/{b.VolR:X2}");
                double dt = ((a.Cycle - t0) - (b.Cycle - o0)) / tickCycles;
                if (Math.Abs(dt) > slack) d.Add($"{at}: {dt:F1} ticks apart");
            }
            // Key-offs of this voice (the startup $FF is left out): the same count, each within slack.
            var ka = theirs.KeyOffs.Where(k => k.Mask != 0xFF && (k.Mask >> v & 1) != 0).ToList();
            var kb = ours.KeyOffs.Where(k => k.Mask != 0xFF && (k.Mask >> v & 1) != 0).ToList();
            int n = Math.Min(ka.Count, kb.Count);
            for (int i = 0; i < n; i++)
            {
                double dt = ((ka[i].Cycle - t0) - (kb[i].Cycle - o0)) / tickCycles;
                if (Math.Abs(dt) > slack) d.Add($"v{v} key-off #{i}: {dt:F1} ticks apart");
            }
            if (Math.Abs(ka.Count - kb.Count) > 1) d.Add($"v{v} key-offs: theirs {ka.Count}, ours {kb.Count}");
        }
        return d;
    }

    /// <summary>Cycles in one song tick at tempo <paramref name="t"/>.</summary>
    internal static double TickCycles(int t) => 1_024_000.0 / (500.0 / 256 * (t + 1));

    [AmkTheory]
    [InlineData("#0 t40 c4 d4 e4")]
    [InlineData("#0 t60 @4 v200 y5 q5C o3 c8 d8 r8 e4 ^8 f8")]
    [InlineData("#0 t50 w120 [c8 e8 g8]3 o5 c2")]
    [InlineData("#0 t80 @2 c4 @9 d4 @13 e4 l16 c d e f g a b > c")]
    [InlineData("#0 t40 c4 d4\n#1 t40 @1 o3 e4 g4 b4")]
    public void a_song_plays_as_in_addmusick(string body)
    {
        string mml = "#amk 2\n" + body;
        var diff = Differences(PlayTheirs(mml, 2.5), PlayOurs(mml, 2.5), TickCycles(40));
        var (t, o) = (PlayTheirs(mml, 2.5), PlayOurs(mml, 2.5));
        string F(SpcTrace x) => string.Join(" ", x.KeyOffs.Take(8).Select(k => $"{k.Cycle - x.KeyOns[0].Cycle}:{k.Mask:X2}"));
        Assert.True(diff.Count == 0, string.Join("\n", diff) + "\ntheirs " + F(t) + "\nours   " + F(o));
    }

    [AsarFact]
    public void the_engine_assembles()
    {
        var (code, labels) = AssembleEngine();
        Assert.True(code.Length is > 200 and < 0x1F00, $"{code.Length} bytes");
        Assert.True(labels["EngineEnd"] <= 0x2400, "the engine must end before the song area");
    }
}
