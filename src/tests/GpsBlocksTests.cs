using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// GpsBlocks against a synthetic ROM laid out the way GPS 1.4.5's main.asm lays one out: the
/// contact stub at $06F690, block_execute with the bank table inline after it, the marker, the
/// pointer table and the blocks' code. No real GPS-patched ROM is checked in.
/// </summary>
public class GpsBlocksTests
{
    private const int Exec = 0x108000, Banks = 0x108100, Marker = 0x10C100;
    private const int Pointers = 0x118000, Pointers2 = 0x128000, Code = 0x138000;
    /// <summary>With pages 40+ the bank table is 32KB and has a freedata block of its own.</summary>
    private const int Banks40 = 0x148000;

    private static void Put(Rom rom, int snes, params byte[] bytes) => bytes.CopyTo(rom.Data, rom.FileOffset(snes));
    private static byte[] Long(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16)];
    private static byte[] Word(int v) => [(byte)v, (byte)(v >> 8)];

    /// <summary>A block at <paramref name="at"/>: the header, then a JMP per contact to an RTL,
    /// except the contacts in <paramref name="handled"/>, which JMP to code.</summary>
    private static void PutBlock(Rom rom, int at, byte header, params int[] handled)
    {
        int rtl = at + 0x40, code = at + 0x41;
        Put(rom, rtl, 0x6B);
        Put(rom, code, 0xA9, 0x01, 0x6B);
        Put(rom, at, header);
        for (int i = 0; i < (header == 0x37 ? 12 : 10); i++)
            Put(rom, at + 1 + i * 3, [0x4C, .. Word(handled.Contains(i) ? code : rtl)]);
    }

    private static void Map(Rom rom, int tile, int addr)
    {
        if (tile < 0x4000) Put(rom, Pointers + tile * 2, Word(addr));
        else Put(rom, Pointers2 + (tile - 0x4000) * 2, Word(addr));
    }

    /// <summary>GPS installed: the stub's JSL into block_execute, and the marker after it.</summary>
    private static Rom Installed(bool pages40)
    {
        var rom = TestRom.Create(size: 0x200000);
        Put(rom, GpsBlocks.Hook, [0x8B, 0xDA, 0xC2, 0x30, 0xA9, 0x01, 0x00, 0x22, .. Long(Exec)]);
        Put(rom, Exec, 0x85, 0x05);
        Put(rom, Marker, [.. "GPS_VeRsIoN"u8, 3, (byte)(pages40 ? 1 : 0),
                          .. Long(pages40 ? Banks40 : Banks), .. Long(Pointers), .. Word(pages40 ? 0x8000 : 0x4000), .. Word(0x8000),
                          .. pages40 ? [.. Long(Pointers2), .. Word(0x8000)] : Array.Empty<byte>()]);
        return rom;
    }

    [Fact]
    public void reads_each_inserted_tile_with_its_address_header_and_contacts()
    {
        var rom = Installed(pages40: false);
        PutBlock(rom, Code, 0x42, 1);                  // Mario above only
        PutBlock(rom, Code + 0x100, 0x37, 10, 11);     // wall feet + wall body
        PutBlock(rom, Code + 0x200, 0x42);             // every contact a bare RTL
        foreach (var (tile, addr) in new[] { (0x201, Code), (0x2FF, Code + 0x100), (0x3FFF, Code + 0x200) })
        {
            Put(rom, Banks + tile, (byte)(addr >> 16));
            Map(rom, tile, addr);
        }
        Map(rom, 0x202, Code);                        // a pointer with bank 0 is not inserted

        var gps = GpsBlocks.Read(rom)!;
        Assert.Equal(3, gps.AsmVersion);
        Assert.Equal([0x201, 0x2FF, 0x3FFF], gps.Blocks.Keys.Order());
        Assert.Equal(new GpsBlock(0x201, Code, 0x42, 1 << 1), gps.Blocks[0x201]);
        Assert.Equal(["Mario above"], gps.Blocks[0x201].HandledContacts);
        Assert.Equal(["wall feet", "wall body"], gps.Blocks[0x2FF].HandledContacts);
        Assert.Empty(gps.Blocks[0x3FFF].HandledContacts);
    }

    [Fact]
    public void pages_40_and_up_come_from_the_second_pointer_table()
    {
        var rom = Installed(pages40: true);
        PutBlock(rom, Code, 0x42, 0);
        Put(rom, Banks40 + 0x4005, Code >> 16);
        Map(rom, 0x4005, Code);

        Assert.Equal(Code, GpsBlocks.Read(rom)!.Blocks[0x4005].Addr);
    }

    [Fact]
    public void a_marker_with_no_hook_in_front_of_it_is_not_gps()
    {
        var rom = Installed(pages40: false);
        Put(rom, GpsBlocks.Hook, 0x22);               // the stub is gone: GPS was removed
        Assert.Null(GpsBlocks.Read(rom));
        Assert.Null(GpsBlocks.Read(TestRom.Create()));
    }
}
