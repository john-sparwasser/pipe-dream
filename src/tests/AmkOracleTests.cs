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
}
