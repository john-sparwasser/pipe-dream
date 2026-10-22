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
        cpu.Run(20_000);                                              // boot first: AddmusicK's snapshot is taken after its own
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
            // AddmusicK keys an arpeggio's steps on twice a millisecond apart: one key-on to a listener.
            List<KeyOn> Once(IEnumerable<KeyOn> ks) { var l = new List<KeyOn>(); foreach (var k in ks) { if (l.Count > 0 && k.Cycle - l[^1].Cycle <= 2048) l[^1] = k; else l.Add(k); } return l; }
            var ta = Once(theirs.KeyOns.Where(k => k.Voice == v));
            var tb = Once(ours.KeyOns.Where(k => k.Voice == v));
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
                        .Select(c => (L: theirs.VolumeAt(v, c).L, R: theirs.VolumeAt(v, c).R, P: theirs.PitchAt(v, c))).ToList();
                    var ((lb, rb), pb) = (ours.VolumeAt(v, cb), ours.PitchAt(v, cb));
                    bool Near(Func<(int L, int R, int P), int> f, int ourValue, int tol) => ourValue >= around.Min(f) - tol && ourValue <= around.Max(f) + tol;
                    var mid = around[1];
                    if (!Near(a => a.L, lb, 1) || !Near(a => a.R, rb, 1)) { d.Add($"v{v} #{i} +{k} ticks: volume {mid.L:X2}/{mid.R:X2} vs {lb:X2}/{rb:X2}"); break; }
                    // Pitch within 1/128 of itself (a seventh of a semitone): AddmusicK moves vibrato on its own grid of timer ticks.
                    if (!Near(a => a.P, pb, Math.Max(2, mid.P / 128))) { d.Add($"v{v} #{i} +{k} ticks: pitch {mid.P:X4} vs {pb:X4}"); break; }
                }
            }
            // Key-offs before the voice's first note are silent either way, and AddmusicK's rests at the start of a song send some ours don't.
            long fa = ta.Count > 0 ? ta[0].Cycle : long.MaxValue, fb = tb.Count > 0 ? tb[0].Cycle : long.MaxValue;
            // (Two key-offs of a voice in one tick are one to a listener; AddmusicK writes some of them twice.)
            List<(long Cycle, int Mask)> OnceOff(IEnumerable<(long Cycle, int Mask)> ks) { var l = new List<(long Cycle, int Mask)>(); foreach (var k in ks) if (l.Count == 0 || k.Cycle - l[^1].Cycle > 2048) l.Add(k); return l; }
            var ka = OnceOff(theirs.KeyOffs.Where(k => (k.Mask >> v & 1) != 0 && k.Cycle > fa));
            var kb = OnceOff(ours.KeyOffs.Where(k => (k.Mask >> v & 1) != 0 && k.Cycle > fb));
            int n = Math.Min(ka.Count, kb.Count);
            for (int i = 0; i < n; i++)
            {
                if (i > 0 && Late(ka[i].Cycle - ka[i - 1].Cycle, kb[i].Cycle - kb[i - 1].Cycle) is { } late) d.Add($"v{v} key-off #{i}: {late:F1} ticks off from the one before");
            }
            if (Math.Abs(ka.Count - kb.Count) > Math.Max(2, ka.Count * 0.06)) d.Add($"v{v} key-offs: theirs {ka.Count}, ours {kb.Count}");
        }
        // What the engine tells the main CPU on ports 0 and 1 ($F9, $F4 $05): the values in order.
        foreach (int port in new[] { 0, 1 })
        {
            var (pa, pb) = (theirs.Ports.Where(p => p.Port == port).Select(p => p.Value).ToList(), ours.Ports.Where(p => p.Port == port).Select(p => p.Value).ToList());
            int n = Math.Min(pa.Count, pb.Count);
            if (!pa.Take(n).SequenceEqual(pb.Take(n)) || Math.Abs(pa.Count - pb.Count) > Math.Max(2, pa.Count / 16))
            {
                int at = Enumerable.Range(0, n).FirstOrDefault(i => pa[i] != pb[i], n);
                d.Add($"port {port} from #{at}: {string.Join(" ", pa.Skip(at).Take(10).Select(b => b.ToString("X2")))} vs {string.Join(" ", pb.Skip(at).Take(10).Select(b => b.ToString("X2")))} ({pa.Count} vs {pb.Count})");
            }
        }
        // The DSP's song-wide registers: each one's values in order (repeats and timing left out).
        foreach (int reg in new[] { 0x0C, 0x1C, 0x2C, 0x3C, 0x0D, 0x2D, 0x3D, 0x4D, 0x5D, 0x6D, 0x7D, 0x0F, 0x1F, 0x2F, 0x3F, 0x4F, 0x5F, 0x6F, 0x7F })
        {
            List<byte> Seq(SpcTrace x) { var l = new List<byte>(); foreach (var g in x.Global.Where(g => g.Reg == reg)) if (l.Count == 0 || l[^1] != g.Value) l.Add(g.Value); return l; }
            var (ga, gb) = (Seq(theirs), Seq(ours));
            if (!ga.SequenceEqual(gb)) d.Add($"DSP {reg:X2}: {string.Join(" ", ga.Take(12).Select(b => b.ToString("X2")))} vs {string.Join(" ", gb.Take(12).Select(b => b.ToString("X2")))}");
        }
        return d;
    }

    private static SpcTrace Trim(SpcTrace x, long start, long span)
    {
        var t = new SpcTrace();
        t.Voice.AddRange(x.Voice);
        t.Global.AddRange(x.Global.Where(g => g.Cycle - start < span));
        t.Ports.AddRange(x.Ports.Where(g => g.Cycle - start < span));
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
    [InlineData("#0 t120 @3 v255 o2a24 v159 ^=40 >c24 v159 ^=40 d24 v159 ^=40 <a24 v159 ^=88")]
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
    [InlineData("#0 t40 $E5 $04 $10 $80 c1 $E5 $00 $08 $C0 c2 $E5 $00 $00 $00 c4 $DE $00 $10 $60 $EA $10 c1 $EA $00 c2")]   // tremolo, vibrato fade
    [InlineData("#0 t40 $EB $04 $08 $0C c2 d2 $EB $00 $00 $00 c4 $EC $02 $06 $F4 c2 d2 $EC $01 $04 $03 e4 $EC $00 $00 $00 c4")]   // pitch envelopes
    [InlineData("#0 t40 c4 $EF $03 $28 $D8 c4 $F1 $04 $50 $01 c4 $F1 $02 $30 $00 c4 $F5 $11 $22 $33 $44 $55 $66 $77 $88 c4 $F1 $03 $20 $02 c4 $F0 c4")]   // echo
    [InlineData("#0 t40 $FA $04 $06 c4 $F1 $04 $50 $01 c4 c4 $F1 $06 $50 $00 c4 c4")]    // a reserved echo buffer
    [InlineData("#0 t40 $EF $01 $10 $20 $F2 $10 $40 $70 c1 $EF $03 $10 $20 c4 $F4 $03 c4 $F4 $03 c4")]    // echo fade, echo toggle
    [InlineData("#0 t40 c4 $F4 $01 d4 e4 f4 $F4 $01 g4 a4 r4 b4")]           // legato
    [InlineData("#0 t40 c4 $F4 $02 d4 e4 $F4 $02 f4 g4 c8 c16 c32")]       // light staccato
    [InlineData("#0 t40 v255 w255 q7F c4 q75 c4 $F4 $08 q7F c4 q75 c4")]   // velocity tables
    [InlineData("#amk 1\n#0 t40 v255 w255 q7F c4 q75 c4 q7C c4")]           // SMW's velocity table
    [InlineData("#amk 2\n#instruments { @0 $8F $E0 $B8 $03 $00 n1F $8F $E0 $B8 $03 $00 }\n#0 t40 @30 c4 @31 d4 @30 e4")]   // noise instruments
    [InlineData("#0 t40 c4 $F8 $1C d4 e4 @1 f4 $FA $00 $06 c4 $F6 $0C $40 d4")]   // noise, pitch modulation, DSP write
    [InlineData("#0 t40 $FB $02 $06 $04 $07 c2 d4 $FB $00 $00 e4")]        // arpeggio
    [InlineData("#0 t40 $FB $03 $04 $04 $80 $07 c2 e4")]                   // arpeggio with a loop point
    [InlineData("#0 t40 $FB $80 $06 $02 c2 $FB $81 $04 $01 d2 e4")]       // trill, glissando
    [InlineData("#0 t40 c4 $F9 $12 $34 d4 $F9 $56 $78 e4")]                 // data to the main CPU
    [InlineData("#0 t40 c4 $F4 $05 c1 c1 c1 c1 c1 c1")]                     // the tick count on the ports (one tick apart where a loop turns it off)
    [InlineData("#0 c4 d4 e8 f8")]                                         // the default tempo
    [InlineData("#0 t40 @21 c4 d4 @22 c4 @23 c8 c8 @24 c8 @25 c8 @26 c8 @27 c8 @28 c8 @29 c8 @0 e4")]   // percussion
    [InlineData("#0 t40 o3 @26 [c8]2 @29 d8 c16")]                         // a hit in a loop, then notes on its instrument
    [InlineData("#0 t80 [[c8 d8]]3 e4")]                                   // super loop
    [InlineData("#0 t60 [[c16 [d16 e16]2]]2 f8")]                           // super loop around a loop
    [InlineData("#0 t60 @4 v150 y4 q5C c4 / d4 e4")]                       // the loop phrase keeps @, v, y and q
    [InlineData("#0 t60 @19 c4 @20 d4 @18 e4")]                             // @19 and @20 play as @0
    public void a_song_plays_as_in_addmusick(string body)
    {
        string mml = body.StartsWith("#amk") ? body : "#amk 2\n" + body;
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
        var all = Differences(theirs, ours, tc, (long)(secs * 1_024_000));
        foreach (var d in all.Where(d => Environment.GetEnvironmentVariable("PIPEDREAM_NOTIMING") is null || !(d.Contains("ticks off") || d.Contains("key-ons:") || d.Contains("key-offs:"))).Take(20)) log.WriteLine(d);
        if (Environment.GetEnvironmentVariable("PIPEDREAM_WINDOW") is { } win)     // "from-to" in ticks: every KON/KOFF write, zeros too
        {
            var (from, to) = (double.Parse(win.Split('-')[0]), double.Parse(win.Split('-')[1]));
            foreach (var (who, x) in new[] { ("theirs", theirs), ("ours", ours) })
            {
                long s0 = x.KeyOns[0].Cycle;
                log.WriteLine($"{who} writes: " + string.Join(" ", x.Global.Concat(x.KeyOffs.Select(k => (k.Cycle, Reg: 0x5C, Value: (byte)k.Mask))).Concat(x.KeyOns.Select(k => (k.Cycle, Reg: 0x4C, Value: (byte)(1 << k.Voice))))
                    .Where(w => (w.Cycle - s0) / tc >= from && (w.Cycle - s0) / tc <= to && w.Reg is 0x4C or 0x5C or 0x6C or 0x7D)
                    .OrderBy(w => w.Cycle).Select(w => $"{(w.Cycle - s0) / tc:F2}:{w.Reg:X2}={w.Value:X2}")));
            }
        }
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
                    .OrderBy(e => e.Item1).Take(80).Select(e => $"{e.Item2}@{(e.Item1 - x.KeyOns[0].Cycle) / tc:F1}")));
    }

    /// <summary>Not a test: how much of the SPC's time our engine spends working, for the song in PIPEDREAM_MML.</summary>
    [AsarFact]
    public void load()
    {
        if (Environment.GetEnvironmentVariable("PIPEDREAM_MML") is not { } path) return;
        var (code, labels) = AssembleEngine();
        var song = Music.MmlCompiler.Compile(Music.MmlParser.Parse(File.ReadAllText(path)), SongBase);
        var cpu = new Spc700 { Pc = labels["Start"], Sp = 0xEF };
        Array.Copy(code, 0, cpu.Ram, 0x0400, code.Length);
        Array.Copy(song.Bytes, 0, cpu.Ram, SongBase, song.Bytes.Length);
        cpu.Ram[0x03F0] = (byte)song.InstrumentTable; cpu.Ram[0x03F1] = (byte)(song.InstrumentTable >> 8);
        cpu.Run(20_000);
        cpu.FromCpu[2] = 1;
        int main = labels["Main"];
        long idle = 0, start = cpu.Cycles, worst = 0, busySince = -1;
        var byPc = new long[0x10000];
        List<(int Pc, long C)> gapPcs = new(), worstPcs = new();
        while (cpu.Cycles - start < 4 * 1_024_000)
        {
            long before = cpu.Cycles;
            bool atMain = cpu.Pc == main;
            int pc = cpu.Pc;
            cpu.Run(1);
            byPc[pc] += cpu.Cycles - before;
            if (atMain && cpu.Ram[0xFD] == 0) { }                   // (the counter reads as 0 when there is nothing to do)
            if (!atMain) gapPcs.Add((pc, cpu.Cycles - before));
            if (atMain)
            {
                if (busySince >= 0 && before - busySince > worst) { worst = before - busySince; worstPcs = gapPcs; }
                busySince = before; gapPcs = new();
            }
        }
        log.WriteLine($"longest gap between visits to Main: {worst} cycles ({worst / 2048.0:F2} timer ticks)");
        var sorted = labels.Where(l => !l.Key.Contains('.') && !l.Key.Contains(':') && l.Value >= 0x400).OrderBy(l => l.Value).ToList();
        var share = new Dictionary<string, long>();
        for (int i = 0; i < sorted.Count; i++)
        {
            int end = i + 1 < sorted.Count ? sorted[i + 1].Value : 0x10000;
            long sum = 0; for (int a = sorted[i].Value; a < end; a++) sum += byPc[a];
            share[sorted[i].Key] = sum;
        }
        foreach (var (k, v) in share.OrderByDescending(x => x.Value).Take(14)) log.WriteLine($"  {k,-20} {v * 100.0 / (4 * 1_024_000):F1}%");
        string LabelOf(int pc) => sorted.LastOrDefault(l => l.Value <= pc).Key ?? "?";
        log.WriteLine("worst gap: " + string.Join(" ", worstPcs.GroupBy(p => LabelOf(p.Pc)).Select(g => (g.Key, S: g.Sum(x => x.C))).OrderByDescending(g => g.S).Take(10).Select(g => $"{g.Key}={g.S}")));
    }

    // Remote code: the AddmusicK build the oracle uses cannot compile it, so these check our engine against the readme.
    private static List<int> LeftVolumes(SpcTrace t, double tc, params double[] ticks)
        => ticks.Select(k => t.VolumeAt(0, t.KeyOns[0].Cycle + (long)(k * tc)).L).ToList();

    [AsarFact]
    public void remote_code_runs_some_ticks_after_each_key_on()
    {
        var t = PlayOurs("#amk 2\n(!1)[v100]\n(!2)[v255]\n#0 t40 (!2, -1) (!1, 1, 8) c2 d4 r4 e4 ^4", 4);
        double tc = TickCycles(40);
        // v255 at each key-on, v100 an eighth (24 ticks) in; the rest and the tie start nothing.
        Assert.Equal([0x2B, 0x2B, 0x06, 0x2B, 0x06, 0x2B, 0x06, 0x06], LeftVolumes(t, tc, 1, 23, 25, 97, 121, 193, 217, 250));
    }

    [AsarFact]
    public void remote_code_runs_before_the_release_or_instead_of_it()
    {
        double tc = TickCycles(40);
        var before = PlayOurs("#amk 2\n(!1)[v100]\n(!2)[v255]\n#0 t40 (!2, -1) (!1, 2, 16) c4 d4", 3);
        // c4 is released 47 ticks in; 12 before that, v100.
        Assert.Equal([0x2B, 0x06, 0x2B], LeftVolumes(before, tc, 33, 36, 49));
        var at = PlayOurs("#amk 2\n(!1)[v100]\n(!2)[v255]\n#0 t40 (!2, -1) (!1, 3) c4 d4", 3);
        Assert.Equal([0x2B, 0x06, 0x2B], LeftVolumes(at, tc, 46, 47.5, 49));
        Assert.Equal(1, at.KeyOffs.Count(k => k.Cycle > at.KeyOns[0].Cycle && k.Cycle < at.KeyOns[0].Cycle + 48 * tc));   // only the one two ticks before the end
    }

    [AsarFact]
    public void remote_code_runs_once_now_and_can_be_turned_off()
    {
        double tc = TickCycles(40);
        var t = PlayOurs("#amk 2\n(!1)[v100]\n(!2)[v255]\n#0 t40 c4 (!1, 4) d4 (!2, -1) e4 (!2, 0) v100 f4", 4);
        Assert.Equal([0x2B, 0x06, 0x2B, 0x06], LeftVolumes(t, tc, 10, 58, 106, 154));
    }

    [AsarFact]
    public void the_engine_assembles()
    {
        var (code, labels) = AssembleEngine();
        Assert.True(code.Length is > 200 and < 0x1F00, $"{code.Length} bytes");
        Assert.True(labels["EngineEnd"] <= 0x2400, "the engine must end before the song area");
    }
}
