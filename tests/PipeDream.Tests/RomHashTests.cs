using Xunit;

namespace PipeDream.Tests;

public class RomHashTests
{
    [Fact]
    public void headered_and_headerless_copies_hash_identically()
    {
        var payload = new byte[0x8000];
        new Random(1).NextBytes(payload);
        var headered = new byte[512 + payload.Length];       // copier header + payload
        payload.CopyTo(headered, 512);
        Assert.Equal(RomHash.HeaderlessSha256(payload), RomHash.HeaderlessSha256(headered));
    }

    [Fact]
    public void different_payloads_hash_differently()
    {
        var a = new byte[0x8000];
        var b = new byte[0x8000];
        b[123] = 1;
        Assert.NotEqual(RomHash.HeaderlessSha256(a), RomHash.HeaderlessSha256(b));
    }

    [RealRomFact]
    public void known_vanilla_rom_matches_the_pinned_constant()
    {
        Assert.Equal(RomHash.VanillaUsSha256,
                     RomHash.HeaderlessSha256File(TestRom.RealRomPath));
    }
}
