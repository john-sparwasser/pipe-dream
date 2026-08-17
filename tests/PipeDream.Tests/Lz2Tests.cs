using Xunit;

namespace PipeDream.Tests;

/// <summary>Gfx.Lz2Compress: round-trips against the managed decompressor and, for the
/// real consumer, against the ROM's own $00B8DE core run under Cpu65816.</summary>
public class Lz2Tests
{
    private static byte[] RoundTrip(byte[] data)
        => Gfx.Lz2Decompress(Gfx.Lz2Compress(data), 0, cap: Math.Max(0x10000, data.Length));

    [Fact]
    public void round_trips_edge_shapes()
    {
        Assert.Equal(Array.Empty<byte>(), RoundTrip([]));
        Assert.Equal(new byte[] { 0x42 }, RoundTrip([0x42]));
        Assert.Equal(new byte[0xC00], RoundTrip(new byte[0xC00]));                 // all-zero file

        var words = new byte[0x800];                                               // AB AB… word fill
        for (int i = 0; i < words.Length; i++) words[i] = (byte)(i % 2 == 0 ? 0xAA : 0xBB);
        Assert.Equal(words, RoundTrip(words));

        var runBoundary = new byte[0x401 + 5];                                     // byte run > one chunk
        Array.Fill(runBoundary, (byte)0x7E, 0, 0x401);
        for (int i = 0; i < 5; i++) runBoundary[0x401 + i] = (byte)i;
        Assert.Equal(runBoundary, RoundTrip(runBoundary));

        var litBoundary = new byte[0x403];                                         // literal > one chunk
        for (int i = 0; i < litBoundary.Length; i++) litBoundary[i] = (byte)(i * 37 + (i >> 3));
        Assert.Equal(litBoundary, RoundTrip(litBoundary));

        var rng = new Random(1234);
        var random = new byte[0x1000];
        rng.NextBytes(random);
        Assert.Equal(random, RoundTrip(random));

        var mixed = new byte[0x600];                                               // runs + words + noise
        Array.Fill(mixed, (byte)3, 0x40, 0x80);
        for (int i = 0x100; i < 0x180; i++) mixed[i] = (byte)(i % 2);
        for (int i = 0x200; i < 0x600; i++) mixed[i] = (byte)(i * 11);
        Assert.Equal(mixed, RoundTrip(mixed));
    }

    [Fact]
    public void compression_is_deterministic()
    {
        var data = new byte[0x900];
        new Random(7).NextBytes(data);
        Array.Fill(data, (byte)0, 0x100, 0x300);
        Assert.Equal(Gfx.Lz2Compress(data), Gfx.Lz2Compress(data));
    }

    [RealRomFact]
    public void real_gfx_file_round_trips_and_zero_fill_compresses_small()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        byte[] gfx0 = Gfx.DecompressFile(rom, 0);
        Assert.Equal(gfx0, RoundTrip(gfx0));

        var mostlyZero = new byte[0xC00];                       // padded-import shape
        gfx0.AsSpan(0, 0x180).CopyTo(mostlyZero);
        Assert.True(Gfx.Lz2Compress(mostlyZero).Length < 0x300);
    }

    /// <summary>The ROM's own decompressor ($00B8DE core: $8A-$8C source, [$00] dest) must
    /// reproduce our compressor's input — including a blob straddling a LoROM bank boundary
    /// (the core's source reads wrap banks; GFX blobs are allocated bank-cross OK).</summary>
    [RealRomFact]
    public void emulated_rom_decompressor_agrees_with_lz2_compress()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        rom.ExpandTo(0x100000);
        var data = new byte[0x600];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 13 + 1);
        Array.Fill(data, (byte)9, 0x80, 0x100);
        byte[] comp = Gfx.Lz2Compress(data);

        foreach (int pc in new[] { 0x80100, 0x87FD0 })          // in-bank and bank-straddling
        {
            comp.CopyTo(rom.Data, pc + rom.HeaderOffset);
            int snes = Rom.PcToSnes(pc);
            var cpu = new Cpu65816(rom);
            cpu.Ram7E[0x8A] = (byte)snes; cpu.Ram7E[0x8B] = (byte)(snes >> 8); cpu.Ram7E[0x8C] = (byte)(snes >> 16);
            cpu.Ram7E[0x00] = 0x00; cpu.Ram7E[0x01] = 0xAD; cpu.Ram7E[0x02] = 0x7E;
            cpu.CallNear(0x00B8DE, 5_000_000);
            for (int i = 0; i < data.Length; i++)
                if (cpu.Ram7E[0xAD00 + i] != data[i])
                    Assert.Fail($"pc {pc:X}: diverges at +{i:X}: {cpu.Ram7E[0xAD00 + i]:X2} != {data[i]:X2}");
            Array.Clear(rom.Data, pc + rom.HeaderOffset, comp.Length);
        }
    }
}
