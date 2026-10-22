using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// Our SPC700 engine (src/data/musicdriver/main.asm, reference/AMK_PORT.md phase 5) assembled and run in the
/// interpreter, and compared with AddmusicK's engine playing the same song: the same notes at the same times with the same
/// pitch, volume and envelope (<see cref="SpcTrace"/>).
/// </summary>
public class MusicEngineTests(Xunit.Abstractions.ITestOutputHelper log)
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
    /// within <paramref name="slack"/> ticks of its start-relative position.
    /// <paramref name="window"/> is how long both ran: events are compared only over the time both traces cover from their first key-on.</summary>
    internal static List<string> Differences(SpcTrace theirs, SpcTrace ours, double tickCycles, long window, double slack = 1.5)
    {
        var d = new List<string>();
        long t0 = theirs.KeyOns.Count > 0 ? theirs.KeyOns[0].Cycle : 0, o0 = ours.KeyOns.Count > 0 ? ours.KeyOns[0].Cycle : 0;
        long span = window - Math.Max(t0, o0) - (long)tickCycles;
        theirs = Trim(theirs, t0, span); ours = Trim(ours, o0, span);
        // Each event is timed from the one before on its voice, not from the start: AddmusicK's engine falls behind the timer when
        // many channels run effects and loses ticks (5% on dv.mml, and blargg's snes_spc agrees, so hardware would too); ours keeps time.
        double? Late(long theirGap, long ourGap)
        {
            double dt = (theirGap - ourGap) / tickCycles;
            return Math.Abs(dt) > slack + 0.06 * theirGap / tickCycles ? dt : null;
        }
        for (int v = 0; v < 8; v++)
        {
            var ta = theirs.KeyOns.Where(k => k.Voice == v).ToList();
            var tb = ours.KeyOns.Where(k => k.Voice == v).ToList();
            if (Math.Abs(ta.Count - tb.Count) > Math.Max(1, ta.Count * 0.06)) d.Add($"v{v} key-ons: theirs {ta.Count}, ours {tb.Count}");   // lag: see Late
            for (int i = 0; i < Math.Min(ta.Count, tb.Count); i++)
            {
                var (a, b) = (ta[i], tb[i]);
                string at = $"v{v} #{i}";
                if (a.Pitch != b.Pitch) d.Add($"{at}: pitch {a.Pitch:X4} vs {b.Pitch:X4}");
                if (a.Srcn != b.Srcn || a.Adsr1 != b.Adsr1 || a.Adsr2 != b.Adsr2 || a.Gain != b.Gain) d.Add($"{at}: instrument {a.Srcn:X2}/{a.Adsr1:X2}{a.Adsr2:X2}/{a.Gain:X2} vs {b.Srcn:X2}/{b.Adsr1:X2}{b.Adsr2:X2}/{b.Gain:X2}");
                if (Math.Abs(a.VolL - b.VolL) > 1 || Math.Abs(a.VolR - b.VolR) > 1) d.Add($"{at}: volume {a.VolL:X2}/{a.VolR:X2} vs {b.VolL:X2}/{b.VolR:X2}");
                if (i > 0 && Late(a.Cycle - ta[i - 1].Cycle, b.Cycle - tb[i - 1].Cycle) is { } late) d.Add($"{at}: {late:F1} ticks off from the one before");
            }
            // Key-offs of this voice (the startup $FF is left out): the same count, each within slack.
            // During each note: volume and pitch a few ticks in, the same number of ticks after each engine's key-on (both engines
            // update their registers on the tick, so half a tick later both have).
            for (int i = 0; i < Math.Min(ta.Count, tb.Count) - 1; i++)
            {
                double len = (ta[i + 1].Cycle - ta[i].Cycle) / tickCycles;
                foreach (int k in new[] { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128 })
                {
                    if (k + 2 > len) break;              // not on the tick the next note starts
                    long ca = ta[i].Cycle + (long)((k + 0.5) * tickCycles), cb = tb[i].Cycle + (long)((k + 0.5) * tickCycles);
                    int b = v * 16;
                    // Theirs over a quarter tick either side: where in its tick an engine keys on moves a fast bend or fade that much.
                    var around = new[] { -0.25, 0, 0.25 }.Select(f => ca + (long)(f * tickCycles))
                        .Select(c => (L: theirs.At(b, c), R: theirs.At(b + 1, c), P: theirs.PitchAt(v, c))).ToList();
                    var (lb, rb, pb) = (ours.At(b, cb), ours.At(b + 1, cb), ours.PitchAt(v, cb));
                    bool Near(Func<(int L, int R, int P), int> f, int ourValue, int tol) => ourValue >= around.Min(f) - tol && ourValue <= around.Max(f) + tol;
                    var mid = around[1];
                    if (!Near(a => a.L, lb, 1) || !Near(a => a.R, rb, 1)) { d.Add($"v{v} #{i} +{k} ticks: volume {mid.L:X2}/{mid.R:X2} vs {lb:X2}/{rb:X2}"); break; }
                    // Pitch within 1/128 of itself (a seventh of a semitone): AddmusicK moves vibrato on its own grid of timer ticks.
                    if (!Near(a => a.P, pb, Math.Max(2, mid.P / 128))) { d.Add($"v{v} #{i} +{k} ticks: pitch {mid.P:X4} vs {pb:X4}"); break; }
                }
            }
            // Key-offs before the voice's first note are silent either way, and AddmusicK's rests at the start of a song send some ours don't.
            long fa = ta.Count > 0 ? ta[0].Cycle : long.MaxValue, fb = tb.Count > 0 ? tb[0].Cycle : long.MaxValue;
            var ka = theirs.KeyOffs.Where(k => k.Mask != 0xFF && (k.Mask >> v & 1) != 0 && k.Cycle > fa).ToList();
            var kb = ours.KeyOffs.Where(k => k.Mask != 0xFF && (k.Mask >> v & 1) != 0 && k.Cycle > fb).ToList();
            int n = Math.Min(ka.Count, kb.Count);
            for (int i = 0; i < n; i++)
            {
                if (i > 0 && Late(ka[i].Cycle - ka[i - 1].Cycle, kb[i].Cycle - kb[i - 1].Cycle) is { } late) d.Add($"v{v} key-off #{i}: {late:F1} ticks off from the one before");
            }
            if (Math.Abs(ka.Count - kb.Count) > Math.Max(2, ka.Count * 0.06)) d.Add($"v{v} key-offs: theirs {ka.Count}, ours {kb.Count}");
        }
        return d;
    }

    private static SpcTrace Trim(SpcTrace x, long start, long span)
    {
        var t = new SpcTrace();
        t.Voice.AddRange(x.Voice);
        t.KeyOns.AddRange(x.KeyOns.Where(k => k.Cycle - start < span));
        t.KeyOffs.AddRange(x.KeyOffs.Where(k => k.Cycle - start < span));
        return t;
    }

    /// <summary>Cycles in one song tick at tempo <paramref name="t"/>.</summary>
    internal static double TickCycles(int t) => 1_024_000.0 / (500.0 / 256 * (t + 1));

    [AmkTheory]
    [InlineData("#0 t40 c4 d4 e4")]
    [InlineData("#0 t60 @4 v200 y5 q5C o3 c8 d8 r8 e4 ^8 f8")]
    [InlineData("#0 t50 w120 [c8 e8 g8]3 o5 c2")]
    [InlineData("#0 t80 @2 c4 @9 d4 @13 e4 l16 c d e f g a b > c")]
    [InlineData("#0 t40 c4 d4\n#1 t40 @1 o3 e4 g4 b4")]
    [InlineData("#0 t32 @0 v200 o4 c4 d4 e4 f4 r4 g2")]
    [InlineData("#0 t96 r6 / t77 c=384 d=384 e2")]
    [InlineData("#0 t96 r6 / v168 $DE $00 $04 $1B t77 o3a+=384 g+=384 a+=384")]
    [InlineData("#0 t192 @3 v255 o2a24 v159 ^=40 >c24 v159 ^=40 d24 v159 ^=40 <a24 v159 ^=88")]
    [InlineData("#0 w138 t58 $EF$BF$28$28$F1$0B$50$01 @0 y10 v255 [q7F o4 a16 r48 a16 r48 g16 r48 a8 r24]4")]
    [InlineData("#0 t58 @0 [q7F o4 a16 r48 a16 r48 g16 r48 a8 r24]4")]
    [InlineData("#0 t40 $EE $80 c4 $EE $40 c4 $EE $FF c4 @5 $EE $80 c4 $EE $00 b4")]          // fine tune
    [InlineData("#0 t40 $FA $02 $7B c4 $FA $02 $05 c4 $E4 $03 c4 $E4 $FD c4 $FA $02 $00 c4")]  // transposition, both kinds
    [InlineData("#0 t40 $FA $02 $FB c4 $FA $02 $85 c4 $FA $02 $40 c4 $FA $02 $74 c4 $E4 $85 c4 $FA $02 $7B @21 c4")] // seven-bit wrap
    [InlineData("#0 t40 $FA $03 $C4 c4 $FA $03 $80 c4 $FA $03 $FF c4 $FA $03 $01 c4 v100 y3 c4")] // amplification
    [InlineData("#0 t40 $FA $01 $AD c4 @1 c4 $ED $7A $E0 c4 $ED $80 $9F c4 $ED $7A $E0 c4 $FA $01 $AD c4 $F4 $09 c4")] // envelopes
    [InlineData("#0 t40 c4 v100 ^4 y4 ^4 w100 ^4 $FA $03 $40 ^4 c4")]                       // volume changes mid-note
    [InlineData("#0 t40 $DE $00 $04 $1B c1 $DE $08 $10 $40 c1 $DE $02 $30 $C0 o3 e2 $DF c2 $DE $00 $20 $FF o5 b2")]   // vibrato
    [InlineData("#0 t40 c2 $DD $04 $08 $B0 d2 $DD $00 $10 $98 o3 c2 $DD $02 $20 $C0 e4 $DE $00 $10 $40 c2 $DD $04 $08 $A8")]   // pitch bends
    [InlineData("#0 t40 $E8 $10 $40 c1 v40 $E8 $08 $F0 c2 $E8 $00 $80 c2 y3 $DC $10 $14 c1 $DC $08 $05 c2 $E1 $10 $40 c1 $E1 $20 $FF c1")]   // fades
    [InlineData("#0 t40 c4 $E3 $20 $80 c4 c4 c4 c4 c4 c4 $E3 $10 $20 c4 c4 c4 c4")]   // tempo fade
    [InlineData("#0 c4 d4 e8 f8")]                                         // the default tempo
    [InlineData("#0 t40 @21 c4 d4 @22 c4 @23 c8 c8 @24 c8 @25 c8 @26 c8 @27 c8 @28 c8 @29 c8 @0 e4")]   // percussion
    [InlineData("#0 t40 o3 @26 [c8]2 @29 d8 c16")]                         // a hit in a loop, then notes on its instrument
    [InlineData("#0 t80 [[c8 d8]]3 e4")]                                   // super loop
    [InlineData("#0 t60 [[c16 [d16 e16]2]]2 f8")]                           // super loop around a loop
    [InlineData("#0 t60 @4 v150 y4 q5C c4 / d4 e4")]                       // the loop phrase keeps @, v, y and q
    [InlineData("#0 t60 @19 c4 @20 d4 @18 e4")]                             // @19 and @20 play as @0
    public void a_song_plays_as_in_addmusick(string body)
    {
        string mml = "#amk 2\n" + body;
        var tm = System.Text.RegularExpressions.Regex.Match(body, @"\bt(\d+)");
        int tempo = tm.Success ? int.Parse(tm.Groups[1].Value) : 53;
        var (t, o) = (PlayTheirs(mml, 8), PlayOurs(mml, 8));
        var diff = Differences(t, o, TickCycles(tempo), 8 * 1_024_000);
        double tc = TickCycles(tempo);
        string F(SpcTrace x) => string.Join(" ", x.KeyOns.Select(k => (k.Cycle, $"N{k.Voice}")).Concat(x.KeyOffs.Where(k => k.Mask != 0xFF).Select(k => (k.Cycle, $"o{k.Mask:X}")))
            .OrderBy(e => e.Item1).Take(40).Select(e => $"{e.Item2}@{(e.Item1 - x.KeyOns[0].Cycle) / tc:F1}"));
        Assert.True(diff.Count == 0, string.Join("\n", diff) + "\ntheirs " + F(t) + "\nours   " + F(o));
    }

    /// <summary>Not a pass/fail test: every corpus song played by both engines, and what differs. Run with PIPEDREAM_ENGINE_REPORT=1.</summary>
    [AmkFact]
    public void corpus_report()
    {
        if (Environment.GetEnvironmentVariable("PIPEDREAM_ENGINE_REPORT") is null) return;
        var spcs = AmkOracle.Compile(AmkOracle.Root!, AmkOracle.Corpus);
        foreach (var name in AmkOracle.Corpus)
        {
            string text = File.ReadAllText(Path.Combine(AmkOracle.Root!, "music", name));
            var theirs = SpcTrace.Run(Spc700.FromSpc(spcs[Path.GetFileNameWithoutExtension(name)]), 20 * 1_024_000);
            SpcTrace ours;
            try { ours = PlayOurs(text, 20); } catch (Exception e) { log.WriteLine($"{name}: ours failed: {e.Message}"); continue; }
            var tm = System.Text.RegularExpressions.Regex.Match(text, @"\bt(\d+)");
            var diff = Differences(theirs, ours, TickCycles(tm.Success ? int.Parse(tm.Groups[1].Value) : 53), 20 * 1_024_000);
            log.WriteLine($"{name}: {theirs.KeyOns.Count} key-ons, {diff.Count} differences");
            foreach (var d in diff.Take(6)) log.WriteLine("    " + d);
            if (Environment.GetEnvironmentVariable("PIPEDREAM_ENGINE_REPORT") == name)
            {
                double tc = TickCycles(tm.Success ? int.Parse(tm.Groups[1].Value) : 53);
                foreach (var (who, x) in new[] { ("theirs", theirs), ("ours", ours) })
                    for (int v = 0; v < 8; v++)
                        log.WriteLine($"  {who} v{v}: " + string.Join(" ", x.KeyOns.Where(k => k.Voice == v).Select(k => (k.Cycle, $"N{k.Pitch:X4}"))
                            .Concat(x.KeyOffs.Where(k => k.Mask != 0xFF && (k.Mask >> v & 1) != 0).Select(k => (k.Cycle, "o")))
                            .OrderBy(e => e.Item1).Take(60).Select(e => $"{e.Item2}@{(e.Item1 - x.KeyOns[0].Cycle) / tc:F1}")));
            }
        }
    }

    /// <summary>Not a test: compare the song in PIPEDREAM_MML (a file) and print both engines' events per voice.</summary>
    [AmkFact]
    public void adhoc()
    {
        if (Environment.GetEnvironmentVariable("PIPEDREAM_MML") is not { } path) return;
        string text = File.ReadAllText(path);
        double secs = double.Parse(Environment.GetEnvironmentVariable("PIPEDREAM_SECONDS") ?? "8");
        var (theirs, ours) = (PlayTheirs(text, secs), PlayOurs(text, secs));
        var tm = System.Text.RegularExpressions.Regex.Match(text, @"\bt(\d+)");
        double tc = TickCycles(tm.Success ? int.Parse(tm.Groups[1].Value) : 53);
        foreach (var d in Differences(theirs, ours, tc, (long)(secs * 1_024_000)).Take(20)) log.WriteLine(d);
        if (Environment.GetEnvironmentVariable("PIPEDREAM_PITCH") is { } pv)          // voice n's pitch half a tick into each tick
        {
            int v = int.Parse(pv);
            foreach (var (who, x) in new[] { ("theirs", theirs), ("ours", ours) })
            {
                long s0 = x.KeyOns.First(k => k.Voice == v).Cycle;
                int lastP = -1;
                log.WriteLine($"{who} raw v{v}: " + string.Join(" ", x.Voice.Where(w => w.Reg == v * 16 + 3 || w.Reg == v * 16 + 2).Select(w => (w.Cycle, P: x.At(v * 16 + 2, w.Cycle) | x.At(v * 16 + 3, w.Cycle) << 8))
                    .Where(w => w.Cycle >= s0 && w.P != lastP && (lastP = w.P) >= 0).Take(600).Select(w => $"{(w.Cycle - s0) / tc:F2}:{w.P:X4}")));
                int lastV = -1;
                log.WriteLine($"{who} rawvol v{v}: " + string.Join(" ", x.Voice.Where(w => w.Reg == v * 16 || w.Reg == v * 16 + 1).Select(w => (w.Cycle, V: x.At(v * 16, w.Cycle) | x.At(v * 16 + 1, w.Cycle) << 8))
                    .Where(w => w.Cycle >= s0 && w.V != lastV && (lastV = w.V) >= 0).Take(60).Select(w => $"{(w.Cycle - s0) / tc:F2}:{w.V & 0xFF:X2}/{w.V >> 8:X2}")));
                log.WriteLine($"{who} pitch v{v}: " + string.Join(" ", Enumerable.Range(0, 48).Select(k =>
                    { long c = s0 + (long)((k + 0.5) * tc); return $"{x.At(v * 16 + 2, c) | x.At(v * 16 + 3, c) << 8:X4}/{x.At(v * 16, c):X2}"; })));
            }
        }
        foreach (var (who, x) in new[] { ("theirs", theirs), ("ours", ours) })
            for (int v = 0; v < 8; v++)
                log.WriteLine($"{who} v{v}: " + string.Join(" ", x.KeyOns.Where(k => k.Voice == v).Select(k => (k.Cycle, $"N{k.Pitch:X4}"))
                    .Concat(x.KeyOffs.Where(k => k.Mask != 0xFF && (k.Mask >> v & 1) != 0).Select(k => (k.Cycle, "o")))
                    .OrderBy(e => e.Item1).Take(40).Select(e => $"{e.Item2}@{(e.Item1 - x.KeyOns[0].Cycle) / tc:F1}")));
    }

    [AsarFact]
    public void an_arpeggio_is_stepped_over_whole()
    {
        // $FB carries its notes after the count and length; reading only two bytes would take $04 as a length and $07 as a q byte.
        var t = PlayOurs("#amk 2\n#0 t40 c4 $FB $02 $0C $04 $07 d4 e4", 2);
        Assert.Equal([0x0C8A, 0x0E16, 0x0FD2], t.KeyOns.Take(3).Select(k => (int)k.Pitch));
    }

    [AsarFact]
    public void the_engine_assembles()
    {
        var (code, labels) = AssembleEngine();
        Assert.True(code.Length is > 200 and < 0x1F00, $"{code.Length} bytes");
        Assert.True(labels["EngineEnd"] <= 0x2400, "the engine must end before the song area");
    }
}
