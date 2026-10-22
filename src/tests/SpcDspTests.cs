using System.Runtime.InteropServices;
using PipeDream.Music;
using Xunit;

namespace PipeDream.Tests;

/// <summary>The S-DSP (<see cref="SpcDsp"/>): what a note sounds like, and, where libgme is installed, the same audio as its
/// emulator (blargg's snes_spc) gives for the same .spc.</summary>
public class SpcDspTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private static Spc700 Exported(string mml)
    {
        var (spc, errors) = MusicInsert.Spc(Rom.Load(ReferenceRoms.Vanilla), new ProjectFile(), "t", mml);
        Assert.True(spc is not null, string.Join("; ", errors));
        return Spc700.FromSpc(SpcFile.Parse(spc.ToBytes()));
    }

    [AsarFact]
    public void a_note_sounds_at_its_pitch_and_mute_silences_it()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        int Crossings(string octave, out Spc700 cpu)
        {
            cpu = Exported($"#amk 2\n#0 t40 @0 v255 {octave} a1^1");
            var pcm = SpcDsp.Render(cpu, 32000);
            var left = Enumerable.Range(16000, 16000).Select(i => (int)pcm[2 * i]).ToArray();
            Assert.True(left.Max() > 1000);
            return left.Zip(left.Skip(1)).Count(p => p.First < 0 && p.Second >= 0);
        }
        int high = Crossings("o4", out var cpu), low = Crossings("o3", out _);
        log.WriteLine($"rising zero crossings in 0.5 s: o4 {high}, o3 {low}");
        Assert.InRange(high, 435, 445);                          // SMW's @0 at o4 a: 880 Hz
        Assert.InRange(low, 215, 225);                           // an octave down
        cpu.Dsp[0x6C] |= 0x40;                                   // FLG: mute
        Assert.All(SpcDsp.Render(cpu, 100), s => Assert.Equal(0, s));
    }

    [AsarFact]
    public void a_key_off_stands_long_enough_for_the_dsp_to_see_it()
    {
        // The DSP reads KOFF every 64 cycles; one cleared sooner (for a key-on in the same tick) can be missed and the note ring on.
        var (code, labels) = MusicEngineTests.AssembleEngine();
        var image = MusicLayout.Build(code, MmlParser.Parse("#amk 2\n#0 t60 @0 o4 q7f [c=24]8\n#1 @1 o3 r=22 [c=24]8"),
            MusicEngineTests.GroupSamples, MusicEngineTests.SampleFile);
        var cpu = new Spc700 { Pc = labels["Start"], Sp = 0xEF };
        Array.Copy(image.Ram, cpu.Ram, 0x10000);
        cpu.Run(20_000);
        cpu.FromCpu[2] = 1;
        cpu.Run(2 * 1_024_000);
        var koff = cpu.DspWrites!.Where(w => w.Reg == 0x5C).ToList();
        var offs = koff.Zip(koff.Skip(1)).Where(p => p.First.Value is not (0 or 0xFF)).ToList();
        Assert.NotEmpty(offs);
        Assert.All(offs, p => Assert.True(p.Second.Cycle - p.First.Cycle >= 64, $"KOFF {p.First.Value:X2} cleared {p.Second.Cycle - p.First.Cycle} cycles later"));
    }

    [AsarFact]
    public void the_player_renders_the_songs_length_as_a_wav()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var (spc, _) = MusicInsert.Spc(Rom.Load(ReferenceRoms.Vanilla), new ProjectFile(), "t",
            "#amk 2\n#SPC\n{\n#length \"0:03\"\n}\n#0 t40 @0 v255 o4 c4 d4");
        string path = Services.SongPlayer.Render(spc!);
        var wav = File.ReadAllBytes(path);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(44 + 3 * 32000 * 4, wav.Length);                // three seconds of 16-bit stereo at 32 kHz
        Assert.Equal(path, Services.SongPlayer.Render(spc!));         // nothing changed: the same render again
    }

    [GmeFact]
    public void songs_sound_as_libgme_plays_them()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string[] songs =
        [
            "#amk 2\n#0 t40 @0 v255 o4 c4 d4 e4 f4 g4 a4\n#1 @1 v200 y5 o3 g1 $DD $10 $20 $B0 c1",
            "#amk 2\n#0 t40 @0 v255 n10 c2 o4 p10,40 c2",
            "#amk 2\n#0 t50 $EF $03 $30 $30 $F1 $04 $50 $00 @0 v255 o4 c8 e8 g8 r2\n#1 @1 v200 o3 c4 r2",
        ];
        foreach (var mml in songs)
        {
            var (spc, _) = MusicInsert.Spc(Rom.Load(ReferenceRoms.Vanilla), new ProjectFile(), "t", mml);
            string file = Path.Combine(Path.GetTempPath(), $"pd-{Guid.NewGuid():N}.spc");
            File.WriteAllBytes(file, spc!.ToBytes());
            var theirs = Gme.Play(file, 64000);
            var ours = SpcDsp.Render(Spc700.FromSpc(SpcFile.Parse(File.ReadAllBytes(file))), 64000);
            // libgme plays 1.4 times louder, and starts a few samples apart: the best of small offsets.
            double best = Enumerable.Range(-16, 33).Max(lag => Correlation(theirs, ours, 16000, 32000, lag));
            log.WriteLine($"{mml.Split('\n')[1]}: correlation {best:F4}");
            Assert.True(best > 0.99, $"{mml}: correlation {best:F4}");
        }
    }

    private static double Correlation(short[] a, short[] b, int from, int count, int lag)
    {
        double sa = 0, sb = 0, sab = 0, saa = 0, sbb = 0;
        for (int i = from; i < from + count; i++)
        {
            double x = a[2 * i], y = b[2 * (i + lag)];
            sa += x; sb += y; sab += x * y; saa += x * x; sbb += y * y;
        }
        double cov = sab - sa * sb / count, va = saa - sa * sa / count, vb = sbb - sb * sb / count;
        return va > 0 && vb > 0 ? cov / Math.Sqrt(va * vb) : 0;
    }
}

/// <summary>libgme (game-music-emu), used only as a second opinion on the DSP's audio.</summary>
internal static class Gme
{
    public static string? Library => new[] { Environment.GetEnvironmentVariable("PIPEDREAM_GME"), "/opt/homebrew/lib/libgme.dylib", "/usr/lib/x86_64-linux-gnu/libgme.so.0" }
        .FirstOrDefault(p => p is not null && File.Exists(p));

    private delegate IntPtr Open(string path, out IntPtr emu, int rate);
    private delegate IntPtr Start(IntPtr emu, int track);
    private delegate void Ignore(IntPtr emu, int ignore);
    private delegate IntPtr PlayFn(IntPtr emu, int count, short[] buf);
    private delegate void Delete(IntPtr emu);

    /// <summary><paramref name="samples"/> stereo samples of the file at 32 kHz, leading silence kept.</summary>
    public static short[] Play(string file, int samples)
    {
        var lib = NativeLibrary.Load(Library!);
        T F<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
        Assert.Equal(IntPtr.Zero, F<Open>("gme_open_file")(file, out var emu, 32000));
        F<Ignore>("gme_ignore_silence")(emu, 1);
        F<Start>("gme_start_track")(emu, 0);
        var buf = new short[samples * 2];
        F<PlayFn>("gme_play")(emu, buf.Length, buf);
        F<Delete>("gme_delete")(emu);
        return buf;
    }
}

public sealed class GmeFactAttribute : FactAttribute
{
    public GmeFactAttribute()
    {
        if (Gme.Library is null) Skip = "libgme not present (set PIPEDREAM_GME)";
        else if (!Asar.Available) Skip = "Asar library not found";
    }
}
