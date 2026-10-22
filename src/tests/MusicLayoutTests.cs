using PipeDream.Music;
using Xunit;

namespace PipeDream.Tests;

/// <summary>Songs laid out in ARAM with their samples (music/MusicLayout.cs), and SMW's own samples read from a ROM.</summary>
public class MusicLayoutTests
{
    private static (byte[] Code, IReadOnlyDictionary<string, int> Labels) Engine() => MusicEngineTests.AssembleEngine();

    /// <summary>A sample's bytes in an ARAM image: the directory entry's start, up to the block marked last, and its loop offset.</summary>
    private static (byte[] Data, int Loop) SampleAt(byte[] ram, int dirPage, int index)
    {
        int e = dirPage * 0x100 + 4 * index;
        int start = ram[e] | ram[e + 1] << 8, loop = ram[e + 2] | ram[e + 3] << 8, end = start;
        while ((ram[end] & 1) == 0) end += 9;
        return (ram[start..(end + 9)], loop - start);
    }

    [Fact]
    public void smws_samples_come_from_the_rom_as_addmusick_ships_them()
    {
        if (!File.Exists(ReferenceRoms.Vanilla) || AmkOracle.Root is not { } root) return;
        var stock = SmwSound.StockSamples(Rom.Load(ReferenceRoms.Vanilla));
        var files = Directory.GetFiles(Path.Combine(root, "samples", "default"), "*.brr").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(files.Count, stock.Count);
        for (int i = 0; i < stock.Count; i++) Assert.Equal(File.ReadAllBytes(files[i]), stock[i]);
    }

    [AsarFact]
    public void the_map_is_engine_directory_song_samples_with_room_for_the_echo()
    {
        var (code, _) = Engine();
        var image = MusicLayout.Build(code, MmlParser.Parse("#amk 2\n#0 $F1 $04 $40 $01 c4"), MusicEngineTests.GroupSamples, MusicEngineTests.SampleFile);
        Assert.True(image.Ok, string.Join("; ", image.Problems));
        Assert.Equal((MusicLayout.EngineBase + code.Length + 0xFF) >> 8, image.DirPage);
        Assert.Equal(image.DirPage * 0x100 + 4 * 20, image.SongBase);          // twenty #default samples, then the song
        Assert.Equal(4, image.EchoDelay);
        Assert.Equal(0x10000 - 4 * 0x800, image.SamplesEnd + image.Free);
        Assert.Equal(image.SongBase, image.Ram[MusicLayout.SongInfo] | image.Ram[MusicLayout.SongInfo + 1] << 8);
        Assert.Equal(image.DirPage, image.Ram[MusicLayout.SongInfo + 4]);
    }

    [AsarFact]
    public void a_song_and_samples_too_big_for_its_echo_are_refused_as_addmusick_does()
    {
        var (code, _) = Engine();
        var big = Enumerable.Range(0, 20).Select(_ => new byte[2 + 9 * 300]).ToList();     // 54 KB of samples
        big.ForEach(b => b[^9] = 1);
        var image = MusicLayout.Build(code, MmlParser.Parse("#amk 2\n#0 $F1 $0F $40 $01 c4"), g => big, _ => null);
        Assert.False(image.Ok);
        Assert.StartsWith("Echo buffer exceeded total space in ARAM.", image.Problems[0].Message);
    }

    [AsarFact]
    public void a_missing_sample_or_group_is_named()
    {
        var (code, _) = Engine();
        var image = MusicLayout.Build(code, MmlParser.Parse("#amk 2\n#samples { #nosuch \"gone.brr\" }\n#0 c4"), _ => null, _ => null);
        Assert.Contains(image.Problems, p => p.Message.Contains("\"nosuch\""));
        Assert.Contains(image.Problems, p => p.Message.Contains("\"gone.brr\""));
    }

    /// <summary>The numbering, end to end: every sample a corpus song's instruments can name is, in our ARAM, the same bytes with the
    /// same loop point as in AddmusicK's.</summary>
    [AmkFact]
    public void every_sample_index_holds_the_sample_addmusick_puts_there()
    {
        var (code, _) = Engine();
        var spcs = AmkOracle.Compile(AmkOracle.Root!, "dv.mml", "ek.mml", "ct_fanfare_2.opt.mml", "click.mml");
        int compared = 0;
        foreach (var (name, spc) in spcs)
        {
            var song = MmlParser.Parse(File.ReadAllText(Path.Combine(AmkOracle.Root!, "music", name + (name.EndsWith(".opt") ? ".mml" : ".mml"))));
            var image = MusicLayout.Build(code, song, MusicEngineTests.GroupSamples, MusicEngineTests.SampleFile);
            Assert.True(image.Ok, name + ": " + string.Join("; ", image.Problems));
            // The samples its custom instruments name (AddmusicK leaves unused ones out, so their entries mean nothing).
            var used = Enumerable.Range(0, song.Instruments.Count).Select(k => (int)image.Ram[image.InstrumentTable + 6 * k])
                .Where(srcn => srcn < 0x80).Distinct().ToList();
            compared += used.Count;
            foreach (int i in used)
            {
                var ours = SampleAt(image.Ram, image.DirPage, i);
                var theirs = SampleAt(spc.Ram, spc.Dsp[0x5D], i);
                Assert.True(ours.Data.SequenceEqual(theirs.Data) && ours.Loop == theirs.Loop, $"{name}: sample {i} differs ({ours.Data.Length} bytes vs {theirs.Data.Length})");
            }
        }
        Assert.True(compared > 20, $"only {compared} samples compared");
    }
}
