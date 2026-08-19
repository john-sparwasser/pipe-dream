using Xunit;

namespace PipeDream.Tests;

public class RomAddressingTests
{
    [Theory]
    [InlineData(0x000000)]
    [InlineData(0x007FFF)]  // last byte of bank $00
    [InlineData(0x008000)]  // first byte of bank $01
    [InlineData(0x00FFFF)]
    [InlineData(0x010000)]
    [InlineData(0x02E000)]  // layer-1 pointer table
    [InlineData(0x07FFFF)]  // last byte of a 512KB ROM
    [InlineData(0x080000)]  // first expanded byte
    [InlineData(0x0F8000)]
    [InlineData(0x3FFFFF)]  // last byte of a 4MB ROM
    public void pc_to_snes_and_back_round_trips_across_bank_boundaries(int pc)
        => Assert.Equal(pc, Rom.SnesToPc(Rom.PcToSnes(pc)));

    [Theory]  // high-half ($8000+) LoROM addresses, the only ones that map to ROM
    [InlineData(0x008000)]
    [InlineData(0x00FFFF)]
    [InlineData(0x018000)]
    [InlineData(0x05E000)]
    [InlineData(0x0DA4BB)]
    [InlineData(0x10FFFF)]
    [InlineData(0x7F8000)]
    public void snes_to_pc_and_back_round_trips_for_rom_addresses(int snes)
        => Assert.Equal(snes, Rom.PcToSnes(Rom.SnesToPc(snes)));

    [Fact]
    public void known_lorom_mappings_hold()
    {
        // Hand-computed LoROM facts: bank N high half = Nth 32KB chunk of the file.
        Assert.Equal(0x000000, Rom.SnesToPc(0x008000));
        Assert.Equal(0x007FFF, Rom.SnesToPc(0x00FFFF));
        Assert.Equal(0x008000, Rom.SnesToPc(0x018000));
        Assert.Equal(0x02E000, Rom.SnesToPc(0x05E000));   // layer-1 pointer table
    }

    [Fact]
    public void load_parses_the_internal_header_of_a_headerless_rom()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pd-test-{Guid.NewGuid():N}.smc");
        try
        {
            File.WriteAllBytes(path, TestRom.Image());
            var rom = Rom.Load(path);
            Assert.Equal(0, rom.HeaderOffset);
            Assert.Equal("SUPER MARIOWORLD", rom.Title);
            Assert.True(rom.IsLoRom);
            Assert.Equal(0x80000, rom.DeclaredRomSize);
            Assert.Equal(0x80000, rom.ActualRomSize);
            Assert.Equal(Rom.SnesToPc(0x05E000), rom.FileOffset(0x05E000));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void copier_header_is_detected_and_offsets_all_reads_by_0x200()
    {
        var img = new byte[0x200 + 0x80000];              // 512-byte copier header + 512KB
        TestRom.WriteInternalHeader(img, 0x200, 0x80000);
        img[0x200 + Rom.SnesToPc(0x0DBB56)] = 0xAB;       // a marker byte, addressed via SNES
        var rom = Rom.FromBytes(img);
        Assert.Equal(0x200, rom.HeaderOffset);
        Assert.Equal("SUPER MARIOWORLD", rom.Title);      // header found despite the offset
        Assert.Equal(0x80000, rom.ActualRomSize);
        Assert.Equal(Rom.SnesToPc(0x008000) + 0x200, rom.FileOffset(0x008000));
        Assert.Equal(0xAB, rom.ReadByte(0x0DBB56));
    }

    [Fact]
    public void read_value_reads_little_endian_multibyte_values()
    {
        var rom = TestRom.Create();
        int fo = rom.FileOffset(0x0E8000);
        rom.Data[fo] = 0x34; rom.Data[fo + 1] = 0x12; rom.Data[fo + 2] = 0x7E;
        Assert.Equal(0x34, rom.ReadValue(0x0E8000, 1));
        Assert.Equal(0x1234, rom.ReadValue(0x0E8000, 2));
        Assert.Equal(0x7E1234, rom.ReadValue(0x0E8000, 3));
    }

    [Fact]
    public void layer1_pointer_set_then_get_round_trips()
    {
        var rom = TestRom.Create();
        rom.SetLayer1Pointer(0x1FF, 0x0E93C2);            // last level slot
        Assert.Equal(0x0E93C2, rom.Layer1Pointer(0x1FF));
        Assert.Equal(0, rom.Layer1Pointer(0x1FE));        // neighbors untouched
    }
}
