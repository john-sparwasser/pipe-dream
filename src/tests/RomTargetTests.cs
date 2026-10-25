using Xunit;

namespace PipeDream.Tests;

/// <summary>What a build is for (RomTarget, RomMap): SA-1 addressing past 2MB, and the LoROM target
/// saying every routine as it always has.</summary>
public class RomTargetTests
{
    [Theory]
    [InlineData(0x000000, 0x008000)]
    [InlineData(0x02E000, 0x05E000)]   // below 2MB, the same as LoROM
    [InlineData(0x1FFFFF, 0x3FFFFF)]   // the last byte of the first 2MB
    [InlineData(0x200000, 0x808000)]   // the next 2MB start in bank $80, not LoROM's $40
    [InlineData(0x3FFFFF, 0xBFFFFF)]
    public void sa1_maps_4mb_through_banks_00_3f_then_80_bf(int pc, int snes)
    {
        Assert.Equal(snes, RomMap.Sa1.PcToSnes(pc));
        Assert.Equal(pc, RomMap.Sa1.SnesToPc(snes));
    }

    [Theory]
    [InlineData(0xC00000, 0x000000)]
    [InlineData(0xC08000, 0x008000)]
    [InlineData(0xE12345, 0x212345)]
    public void sa1_banks_c0_ff_are_hirom_over_the_whole_rom(int snes, int pc)
        => Assert.Equal(pc, RomMap.Sa1.SnesToPc(snes));

    [Fact]
    public void sa1_banks_40_7f_are_not_rom()
        => Assert.Throws<ArgumentOutOfRangeException>(() => RomMap.Sa1.SnesToPc(0x408000));

    [Fact]
    public void a_rom_addresses_by_its_header_as_it_is_now()
    {
        var rom = Rom.FromBytes(new byte[0x400000]);
        Assert.Same(RomMap.LoRom, rom.Map);
        Assert.Equal(0x200000, rom.FileOffset(0x408000));
        rom.Data[0x7FD5] = 0x23;                                  // what the SA-1 Pack writes
        Assert.Same(RomMap.Sa1, rom.Map);
        Assert.Equal(0x200000, rom.FileOffset(0x808000));
    }

    [Fact]
    public void the_lorom_target_keeps_ram_where_vanilla_has_it()
    {
        Assert.Equal(0x1931, RomTarget.LoRom.Ram(0x1931));
        Assert.Equal(0x7EC800, RomTarget.LoRom.RamLong(0x7EC800));
        Assert.Null(RomTarget.LoRom.ExAnimTrampoline);
        var (errors, written) = RomTarget.LoRom.Install(Rom.FromBytes(new byte[0x80000]), RomPrep.Version);
        Assert.Empty(errors);
        Assert.Empty(written);
    }
}
