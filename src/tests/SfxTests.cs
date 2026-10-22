using PipeDream.Music;
using Xunit;

namespace PipeDream.Tests;

/// <summary>Sound effects: AddmusicK's 93, compiled by SfxCompiler and played by our engine, against AddmusicK's engine playing
/// its own, the port poked as SMW's NMI does (the value for a frame, then 0).</summary>
public class SfxTests(Xunit.Abstractions.ITestOutputHelper log)
{
    /// <summary>AddmusicK's effect list, compiled: index = effect number; a '*' entry is the same array as the effect it names.</summary>
    internal static SoundEffects AmkEffects(byte[] instruments)
    {
        string root = AmkOracle.Root!;
        var (l9, lC) = AmkImport.ParseSfx(File.ReadAllText(Path.Combine(root, "Addmusic_sound effects.txt")), []);
        IReadOnlyList<byte[]?> Port(List<MusicState.SfxSlot> slots, string folder)
        {
            var byFile = new Dictionary<string, byte[]>();
            var list = new byte[]?[slots.Count == 0 ? 1 : slots.Max(s => s.Slot) + 1];
            foreach (var s in slots.Where(s => s.Flag != "*"))
            {
                var (bytes, problems) = SfxCompiler.Compile(File.ReadAllText(Path.Combine(root, folder, s.File)), s.Flag == "?");
                Assert.True(problems.Count == 0, s.File + ": " + string.Join("; ", problems));
                list[s.Slot] = byFile[s.File] = bytes;
            }
            foreach (var s in slots.Where(s => s.Flag == "*")) list[s.Slot] = byFile[s.File];
            return list;
        }
        return new SoundEffects(instruments, Port(l9, "1DF9"), Port(lC, "1DFC"));
    }

    private static byte[] Instruments(byte[] amkRam) => amkRam[0x18FB..0x19A6];     // AddmusicK's copy of SMW's (equal to the ROM's)

    /// <summary>Run <paramref name="cpu"/> 0.3 s, poke the port for a frame, then run on.</summary>
    internal static SpcTrace Poke(Spc700 cpu, int port, byte value, double seconds = 1.6)
    {
        var t = SpcTrace.Run(cpu, 307_200);
        cpu.FromCpu[port] = value;
        var a = SpcTrace.Run(cpu, 17_066);                    // one frame
        cpu.FromCpu[port] = 0;
        var b = SpcTrace.Run(cpu, (long)(seconds * 1_024_000));
        t.Append(a, 307_200); t.Append(b, 307_200 + 17_066);
        return t;
    }

    internal static Spc700 Ours(SoundEffects sfx)
    {
        var (code, labels) = MusicEngineTests.AssembleEngine();
        var image = MusicLayout.Build(code, MmlParser.Parse("#amk 2\n#0 r1"), MusicEngineTests.GroupSamples, MusicEngineTests.SampleFile, sfx);
        Assert.True(image.Ok, string.Join("; ", image.Problems));
        var cpu = new Spc700 { Pc = labels["Start"], Sp = 0xEF };
        Array.Copy(image.Ram, cpu.Ram, 0x10000);
        cpu.Run(20_000);
        cpu.FromCpu[2] = 1;
        return cpu;
    }

    /// <summary>What differs on one voice: key-ons (pitch, sample, envelope, volume), key-off count, and pitch and volume a few effect
    /// ticks into each note, all timed from the first key-on.</summary>
    internal static List<string> Compare(SpcTrace theirs, SpcTrace ours, int voice)
    {
        var d = new List<string>();
        var ta = theirs.KeyOns.Where(k => k.Voice == voice).ToList();
        var tb = ours.KeyOns.Where(k => k.Voice == voice).ToList();
        if (ta.Count != tb.Count) d.Add($"key-ons: theirs {ta.Count}, ours {tb.Count}");
        const double tick = 256.0 / 56 * 2048;                // one effect tick
        for (int i = 0; i < Math.Min(ta.Count, tb.Count); i++)
        {
            var (a, b) = (ta[i], tb[i]);
            if (a.Pitch != b.Pitch) d.Add($"#{i}: pitch {a.Pitch:X4} vs {b.Pitch:X4}");
            if (a.Srcn != b.Srcn || a.Adsr1 != b.Adsr1 || a.Adsr2 != b.Adsr2 || a.Gain != b.Gain) d.Add($"#{i}: instrument {a.Srcn:X2}/{a.Adsr1:X2}{a.Adsr2:X2}/{a.Gain:X2} vs {b.Srcn:X2}/{b.Adsr1:X2}{b.Adsr2:X2}/{b.Gain:X2}");
            if (a.VolL != b.VolL || a.VolR != b.VolR) d.Add($"#{i}: volume {a.VolL:X2}/{a.VolR:X2} vs {b.VolL:X2}/{b.VolR:X2}");
            double dt = ((a.Cycle - ta[0].Cycle) - (b.Cycle - tb[0].Cycle)) / tick;
            if (Math.Abs(dt) > 1.2) d.Add($"#{i}: {dt:F1} ticks apart");
            for (int k = 1; k < 6; k++)
            {
                long ca = a.Cycle + (long)(k * tick), cb = b.Cycle + (long)(k * tick);
                if (i + 1 < ta.Count && ca >= ta[i + 1].Cycle) break;
                int pa = theirs.PitchAt(voice, ca), pb = ours.PitchAt(voice, cb);
                int pa2 = theirs.PitchAt(voice, ca + (long)(tick / 2)), pa0 = theirs.PitchAt(voice, ca - (long)(tick / 2));
                int tol = Math.Max(2, pa / 128);                 // a slide's steps may round a little differently (as music's do)
                if (pb < Math.Min(pa0, Math.Min(pa, pa2)) - tol || pb > Math.Max(pa0, Math.Max(pa, pa2)) + tol) { d.Add($"#{i} +{k}: pitch {pa:X4} vs {pb:X4}"); break; }
            }
        }
        int kOffs(SpcTrace t, long from) => t.KeyOffs.Count(k => (k.Mask >> voice & 1) != 0 && k.Cycle > from);
        if (ta.Count > 0 && tb.Count > 0 && Math.Abs(kOffs(theirs, ta[0].Cycle) - kOffs(ours, tb[0].Cycle)) > 0)
            d.Add($"key-offs: theirs {kOffs(theirs, ta[0].Cycle)}, ours {kOffs(ours, tb[0].Cycle)}");
        return d;
    }

    /// <summary>Run with the ports set at times (seconds), each for a frame, as SMW's NMI writes them.</summary>
    internal static SpcTrace WithPokes(Spc700 cpu, double seconds, params (double At, int Port, byte Value)[] pokes)
    {
        var t = new SpcTrace();
        long done = 0;
        foreach (var (at, port, value) in pokes.OrderBy(p => p.At))
        {
            long c = (long)(at * 1_024_000);
            t.Append(SpcTrace.Run(cpu, c - done), done); done = c;
            cpu.FromCpu[port] = value;
            t.Append(SpcTrace.Run(cpu, 17_066), done); done += 17_066;
            cpu.FromCpu[port] = 0;
        }
        t.Append(SpcTrace.Run(cpu, (long)(seconds * 1_024_000) - done), done);
        return t;
    }

    private static Spc700 OursPlaying(string mml, SoundEffects sfx)
    {
        var (code, labels) = MusicEngineTests.AssembleEngine();
        var image = MusicLayout.Build(code, MmlParser.Parse(mml), MusicEngineTests.GroupSamples, MusicEngineTests.SampleFile, sfx);
        Assert.True(image.Ok, string.Join("; ", image.Problems));
        var cpu = new Spc700 { Pc = labels["Start"], Sp = 0xEF };
        Array.Copy(image.Ram, cpu.Ram, 0x10000);
        cpu.Run(20_000);
        cpu.FromCpu[2] = 1;
        return cpu;
    }

    [AmkTheory]
    [InlineData("#0 t50 [c8 e8 g8]8\n#6 t50 o3 [c4 e4]6\n#7 t50 o4 [g8 b8]12", "0.5:0=01 1.2:3=02 2.0:3=01 2.3:0=0A")]   // effects over channels 6 and 7
    [InlineData("#0 t50 [c8]40\n#5 t50 $F4 $00 o3 [g8]40\n#3 t50 $F4 $06 o2 [c4]20", "1.0:1=02 2.0:1=03")]         // Yoshi's drums
    [InlineData("#0 t50 [c8 d8]20\n#6 t50 [e4]10", "0.8:1=07 1.6:1=08")]                                           // pause and unpause
    public void songs_and_effects_share_the_voices_as_in_addmusick(string body, string pokes)
    {
        string mml = "#amk 2\n" + body;
        var p = pokes.Split(' ').Select(x => { var a = x.Split(':', '='); return (double.Parse(a[0]), int.Parse(a[1]), Convert.ToByte(a[2], 16)); }).ToArray();
        var amkSpc = AmkOracle.CompileText(AmkOracle.Root!, mml);
        var sfx = AmkEffects(Instruments(AmkOracle.CompileText(AmkOracle.Root!, "#amk 2\n#0 r1").Ram));
        var theirs = WithPokes(Spc700.FromSpc(amkSpc), 3.5, p);
        // The pokes at the same point of the song in both: ours starts it sooner after boot than AddmusicK's snapshot does.
        double shift = (SpcTrace.Run(OursPlaying(mml, sfx), 1_024_000).KeyOns[0].Cycle - theirs.KeyOns[0].Cycle) / 1_024_000.0;
        var ours = WithPokes(OursPlaying(mml, sfx), 3.5, p.Select(x => (x.Item1 + shift, x.Item2, x.Item3)).ToArray());
        var d = MusicEngineTests.Differences(theirs, ours, MusicEngineTests.TickCycles(50), (long)(3.5 * 1_024_000));
        string Ev(SpcTrace x, int v) => string.Join(" ", x.KeyOns.Where(k => k.Voice == v).Select(k => (k.Cycle, $"N{k.Pitch:X4}"))
            .Concat(x.KeyOffs.Where(k => (k.Mask >> v & 1) != 0).Select(k => (k.Cycle, "o"))).OrderBy(e => e.Item1).Where(e => e.Item1 / 2048 > 850).Take(30).Select(e => $"{e.Item2}@{e.Item1 / 2048}"));
        Assert.True(d.Count == 0, string.Join("\n", d.Take(12)) + "\n" + string.Join("\n", new[] { 3, 5, 6, 7 }.Select(v => $"theirs v{v}: {Ev(theirs, v)}\nours   v{v}: {Ev(ours, v)}")));
    }

    [AmkFact]
    public void the_1dfa_sounds_play_as_in_addmusick()
    {
        var amkSpc = AmkOracle.CompileText(AmkOracle.Root!, "#amk 2\n#0 r1");
        var sfx = AmkEffects(Instruments(amkSpc.Ram));
        var failures = new List<string>();
        foreach (var (value, voice) in new[] { (1, 7), (4, 7), (7, 6), (8, 6) })
        {
            var d = Compare(Poke(Spc700.FromSpc(amkSpc), 1, (byte)value), Poke(Ours(sfx), 1, (byte)value), voice);
            if (d.Count > 0) failures.Add($"1DFA {value:X2}: " + string.Join("; ", d.Take(4)));
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [AmkFact]
    public void every_addmusick_sound_effect_plays_as_in_addmusick()
    {
        var amkSpc = AmkOracle.CompileText(AmkOracle.Root!, "#amk 2\n#0 r1");
        var sfx = AmkEffects(Instruments(amkSpc.Ram));
        var failures = new List<string>();
        foreach (var (port, list, voice) in new[] { (0, sfx.Port1DF9, 6), (3, sfx.Port1DFC, 7) })
            for (int n = 1; n < list.Count; n++)
            {
                if (list[n] is null) continue;
                var theirs = Poke(Spc700.FromSpc(amkSpc), port, (byte)n);
                var ours = Poke(Ours(sfx), port, (byte)n);
                var d = Compare(theirs, ours, voice);
                if (d.Count > 0) failures.Add($"{(port == 0 ? "1DF9" : "1DFC")} {n:X2}: " + string.Join("; ", d.Take(4)));
            }
        foreach (var f in failures) log.WriteLine(f);
        Assert.True(failures.Count == 0, $"{failures.Count} effects differ:\n" + string.Join("\n", failures.Take(25)));
    }
}

public class SfxDebug(Xunit.Abstractions.ITestOutputHelper log)
{
    [AmkFact]
    public void debug()
    {
        if (Environment.GetEnvironmentVariable("PIPEDREAM_SFXDEBUG") is null) return;
        var amkSpc = AmkOracle.CompileText(AmkOracle.Root!, "#amk 2\n#0 r1");
        var sfx = SfxTests.AmkEffects(amkSpc.Ram[0x18FB..0x19A6]);
        var cpu = SfxTests.Ours(sfx);
        log.WriteLine($"info {string.Join(" ", cpu.Ram[0x03F0..0x0400].Select(b => b.ToString("X2")))}");
        int t9 = cpu.Ram[0x3F7] | cpu.Ram[0x3F8] << 8, tc = cpu.Ram[0x3F9] | cpu.Ram[0x3FA] << 8;
        log.WriteLine($"t9 {string.Join(" ", cpu.Ram[t9..(t9 + 12)].Select(b => b.ToString("X2")))} tC {string.Join(" ", cpu.Ram[tc..(tc + 12)].Select(b => b.ToString("X2")))} n9 {sfx.Port1DF9.Count} nulls {sfx.Port1DF9.Count(x => x is null)}");
        int port = int.Parse(Environment.GetEnvironmentVariable("PIPEDREAM_SFXDEBUG")!);
        SpcTrace.Run(cpu, 307_200);
        cpu.FromCpu[port] = 1;
        var (_, labels) = MusicEngineTests.AssembleEngine();
        int start = labels["SfxStart"], ports = labels["Ports"];
        long until = cpu.Cycles + 5000; int hits = 0, portHits = 0;
        bool tracing = false; int n = 0;
        while (cpu.Cycles < until)
        {
            if (cpu.Pc == start) { hits++; tracing = true; }
            if (tracing && n++ < 40) log.WriteLine($"  pc {cpu.Pc:X4} op {cpu.Ram[cpu.Pc]:X2} {cpu.Ram[cpu.Pc + 1]:X2} {cpu.Ram[cpu.Pc + 2]:X2} A={cpu.A:X2} X={cpu.X:X2} Y={cpu.Y:X2} psw={cpu.Psw:X2}");
            if (cpu.Pc == ports) portHits++;
            cpu.Run(1);
        }
        log.WriteLine($"SfxStart hits {hits}, Ports {portHits}");
        log.WriteLine($"mid: lastP0 {cpu.Ram[0x3B]:X2} s_wait {cpu.Ram[0x108]:X2} ptr {cpu.Ram[0x100]:X2}{cpu.Ram[0x102]:X2}");
        var t = SfxTests.Poke(cpu, port, 1, 0.5);
        log.WriteLine("kon " + string.Join(" ", t.KeyOns.Select(k => k.ToString())));
        log.WriteLine("s_ " + string.Join(" ", cpu.Ram[0x0100..0x0120].Select(b => b.ToString("X2"))));
        log.WriteLine($"lastP0 {cpu.Ram[0x3B]:X2} lastP1 {cpu.Ram[0x3C]:X2} F4in {cpu.FromCpu[0]:X2} busy {cpu.Ram[0x3A]:X2} lastP3 {cpu.Ram[0x3D]:X2} acc {cpu.Ram[0x39]:X2} pc {cpu.Pc:X4} sp {cpu.Sp:X2}");
    }
}
