using Xunit;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The acts-as descriptions in the Map16 gutter. SMW has no behaviour table — every line in
/// Map16ActsAs.json was read out of the disassembly by hand — so what matters here is that the
/// file parses, that an exact code beats the range it sits inside, and above all that an
/// untraced code says NOTHING rather than borrowing a neighbour's meaning.
/// </summary>
public class ActsAsTests
{
    [Fact]
    public void the_shipped_table_describes_the_codes_it_claims_to()
    {
        Assert.Equal("Coin", ActsAs.Describe(0x2B));                  // exact
        Assert.Contains("Muncher", ActsAs.Describe(0x2F));
        Assert.Contains("spikes", ActsAs.Describe(0x5A));             // inside the 59..5B range
        Assert.Equal("Solid", ActsAs.Describe(0x100));
        Assert.Equal("Solid", ActsAs.Describe(0x1FF));
    }

    /// <summary>0x1E is a turn block and 0x11-0x2D are hittable blocks generally: the code has to
    /// win, or every one of them reads as the band it belongs to.</summary>
    [Fact]
    public void an_exact_code_beats_the_range_around_it()
    {
        Assert.Contains("Turn block", ActsAs.Describe(0x1E));
        Assert.Contains("Hittable", ActsAs.Describe(0x1F));
    }

    [Fact]
    public void an_untraced_code_describes_as_nothing()
    {
        Assert.Equal("", ActsAs.Describe(0x40));
        Assert.Equal("", ActsAs.Describe(0x00));
    }
}
