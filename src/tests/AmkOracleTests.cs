using Xunit;

namespace PipeDream.Tests;

/// <summary>The oracle itself: AMK compiles a song with -norom and its .spc reads back as a snapshot
/// of ARAM, so later phases have something to compare against.</summary>
public class AmkOracleTests
{
    [AmkFact]
    public void the_oracle_compiles_a_song_to_an_spc_that_reads_back()
    {
        var spc = AmkOracle.Compile(AmkOracle.Root!, "click.mml")["click"];

        Assert.Equal("click", spc.Title);
        Assert.Equal("AddmusicK 1.0.5", spc.Dumper);          // AMK stamps its version here
        Assert.Equal(0xCF, spc.Sp);
        Assert.NotEqual(0, spc.Pc);
        Assert.Contains(spc.Ram, b => b != 0);
        Assert.Contains(spc.Dsp, b => b != 0);                      // the DSP is configured
    }

    [AmkFact]
    public void the_whole_corpus_compiles_to_spcs()
    {
        var all = AmkOracle.Compile(AmkOracle.Root!, AmkOracle.Corpus);

        Assert.Equal(AmkOracle.Corpus.Length, all.Count);
        Assert.All(all.Values, spc => Assert.Contains(spc.Ram, b => b != 0));
    }
}

/// <summary>The interpreter against the real thing: AddmusicK's own engine, from its own snapshot.</summary>
public class AmkEngineInInterpreterTests(Xunit.Abstractions.ITestOutputHelper log)
{
    [AmkFact]
    public void amks_engine_runs_in_the_interpreter_and_keys_on_in_time()
    {
        var spc = AmkOracle.Compile(AmkOracle.Root!, "click.mml")["click"];
        var cpu = Spc700.FromSpc(spc);
        cpu.Run(3 * 1_024_000);                                     // three seconds of SPC time

        var kon = cpu.DspWrites!.Where(w => w.Reg == 0x4C && w.Value != 0).ToList();
        log.WriteLine($"{cpu.DspWrites!.Count} DSP writes, key-ons at " + string.Join(", ", kon.Select(w => w.Cycle)));
        Assert.False(cpu.Halted);
        Assert.True(kon.Count >= 3, $"only {kon.Count} key-ons");
        Assert.All(kon, w => Assert.Equal(0x01, w.Value));          // channel 0 only, as the song says

        // "c16 r16 r8" repeated is a quarter note at this tempo: the key-ons are evenly spaced.
        var gaps = kon.Zip(kon.Skip(1), (a, b) => b.Cycle - a.Cycle).ToList();
        Assert.All(gaps, g => Assert.InRange(g, gaps[0] * 0.99, gaps[0] * 1.01));
    }
}
