using Xunit;

namespace PipeDream.Tests;

public class RatsWriterTests
{
    [Fact]
    public void allocate_writes_a_rats_block_that_enumerate_finds_with_the_payload_size()
    {
        var rom = TestRom.Create(size: 0x100000);   // 1MB so expanded space (pc >= 0x80000) exists
        var payload = Enumerable.Range(0, 300).Select(i => (byte)(i * 7 + 1)).ToArray();

        int snes = RatsWriter.Allocate(rom, payload);

        var rat = Assert.Single(RatsWriter.EnumerateRats(rom));
        Assert.Equal(payload.Length, rat.Size);
        Assert.Equal(snes, Rom.PcToSnes(rat.PcOffset + 8));   // data sits just past the 8-byte tag
        Assert.Equal(payload, rom.Data.AsSpan(rom.FileOffset(snes), payload.Length).ToArray());
    }

    [Fact]
    public void two_allocations_do_not_overlap_and_both_payloads_survive()
    {
        var rom = TestRom.Create(size: 0x100000);
        var a = new byte[100]; Array.Fill(a, (byte)0xAA);
        var b = new byte[200]; Array.Fill(b, (byte)0xBB);

        int s1 = RatsWriter.Allocate(rom, a);
        int s2 = RatsWriter.Allocate(rom, b);

        var rats = RatsWriter.EnumerateRats(rom).OrderBy(r => r.PcOffset).ToList();
        Assert.Equal(2, rats.Count);
        Assert.True(rats[0].PcOffset + 8 + rats[0].Size <= rats[1].PcOffset,
                    "second block starts inside the first (tag+data)");
        Assert.Equal(a, rom.Data.AsSpan(rom.FileOffset(s1), a.Length).ToArray());
        Assert.Equal(b, rom.Data.AsSpan(rom.FileOffset(s2), b.Length).ToArray());
    }

    [Fact]
    public void enumerate_ignores_a_fake_star_tag_whose_size_inverse_pair_is_wrong()
    {
        var rom = TestRom.Create(size: 0x100000);
        // "STAR" + size words that do NOT satisfy size ^ inverse == 0xFFFF.
        int fo = 0x80010;
        rom.Data[fo] = 0x53; rom.Data[fo + 1] = 0x54; rom.Data[fo + 2] = 0x41; rom.Data[fo + 3] = 0x52;
        rom.Data[fo + 4] = 0x10; rom.Data[fo + 5] = 0x00;
        rom.Data[fo + 6] = 0x10; rom.Data[fo + 7] = 0x00;
        Assert.Empty(RatsWriter.EnumerateRats(rom));
    }

    [Fact]
    public void fix_checksum_makes_checksum_xor_complement_ffff_and_checksum_equal_the_byte_sum()
    {
        var rom = TestRom.Create();
        rom.Data[0x1234] = 0x77;                      // dirty some content first
        rom.Data[0x91000 % rom.Data.Length] = 0x05;

        RatsWriter.FixChecksum(rom);

        int comp = rom.Data[0x7FDC] | (rom.Data[0x7FDD] << 8);
        int chk = rom.Data[0x7FDE] | (rom.Data[0x7FDF] << 8);
        Assert.Equal(0xFFFF, chk ^ comp);

        // SNES contract, recomputed independently: because the checksum and complement
        // bytes always sum to 0x1FE regardless of value, the stored checksum equals the
        // 16-bit sum of the FINAL image bytes.
        long sum = 0;
        foreach (byte x in rom.Data) sum += x;
        Assert.Equal(chk, (int)(sum & 0xFFFF));
    }
}
