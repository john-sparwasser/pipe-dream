using Xunit;

namespace PipeDream.Tests;

/// <summary>LevelHeader decode ↔ encode. LevelEncoder emits the header from these fields
/// on every save, so an inexact inverse would silently corrupt the header of any level a
/// project touches — including levels whose header nobody edited.</summary>
public class LevelHeaderTests
{
    [Fact]
    public void every_possible_header_survives_decode_then_encode()
    {
        // The 5 bytes are independent bitfields, so covering all 256 values of each byte
        // against a couple of neighbour patterns exhausts the field packing without
        // walking all 2^40 combinations.
        foreach (byte fill in new byte[] { 0x00, 0xFF, 0x5A })
            for (int b = 0; b < 5; b++)
                for (int v = 0; v < 256; v++)
                {
                    byte[] bytes = [fill, fill, fill, fill, fill];
                    bytes[b] = (byte)v;
                    Assert.Equal(bytes, new LevelHeader(bytes).ToBytes());
                }
    }

    [Fact]
    public void fields_decode_to_their_documented_bit_positions()
    {
        var h = new LevelHeader([0x00, 0x00, 0x00, 0x00, 0x00]) with
        {
            Screens = 32, BgPalette = 5, LevelMode = 0x1F, BackAreaColor = 3,
            SpriteSet = 9, Music = 6, Layer3Priority = 1,
            Time = 2, SpritePalette = 4, FgPalette = 1,
            Tileset = 7, ItemMemory = 3, ScrollSetting = 2,
        };
        Assert.Equal(h, new LevelHeader(h.ToBytes()));      // fields survive the trip
        Assert.Equal([0xBF, 0x7F, 0xE9, 0xA1, 0xE7], h.ToBytes());
    }

    [Fact]
    public void out_of_range_field_values_truncate_instead_of_corrupting_neighbours()
    {
        var h = new LevelHeader([0, 0, 0, 0, 0]) with { Tileset = 0x1F, ItemMemory = 0 };
        Assert.Equal(0x0F, h.ToBytes()[4]);                 // 0x1F masked to 4 bits, no spill
    }

    [RealRomFact]
    public void every_level_in_the_rom_round_trips_its_header()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        for (int lvl = 0; lvl < Rom.LevelCount; lvl++)
        {
            int fo = rom.FileOffset(rom.Layer1Pointer(lvl));
            byte[] original = rom.Data.AsSpan(fo, 5).ToArray();
            Assert.Equal(original, new LevelHeader(original).ToBytes());
        }
    }

    [RealRomFact]
    public void a_header_override_reaches_the_parsed_level_and_the_encoded_bytes()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var before = LevelParser.Parse(rom, 0x105);
        var edited = before.Header with { Tileset = (before.Header.Tileset + 1) & 0x0F };

        rom.LevelHeaderOverrides[0x105] = edited.ToBytes();
        var after = LevelParser.Parse(rom, 0x105);
        Assert.Equal(edited, after.Header);
        Assert.Equal(edited.ToBytes(), LevelEncoder.Encode(after, rom, after.Objects)[..5]);
        Assert.Equal(before.Objects.Count, after.Objects.Count);   // objects still come from ROM

        rom.LevelHeaderOverrides.Remove(0x105);
        Assert.Equal(before.Header, LevelParser.Parse(rom, 0x105).Header);
    }
}
