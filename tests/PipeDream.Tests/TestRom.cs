using System.Text;

namespace PipeDream.Tests;

/// <summary>
/// Shared fixture: builds minimal in-memory LoROM images that Rom accepts, so every
/// ROM-dependent test runs without a real SMW file. Real-ROM checks live in RealRomTests.
/// </summary>
internal static class TestRom
{
    public const string RealRomPath = @"C:\SMW\Projects\.resources\SMW.smc";

    public const int TestLevel = 0x105;
    /// <summary>SNES address of the synthetic level's data (pc 0x70000 — empty area).</summary>
    public const int LevelDataSnes = 0x0E8000;

    /// <summary>The 5 header bytes CreateWithLevel writes at the level data pointer.</summary>
    public static readonly byte[] LevelHeaderBytes = { 0x21, 0x05, 0x30, 0x1D, 0x02 };

    /// <summary>
    /// Raw headerless LoROM image: zeros + SNES internal header at 0x7FC0 (title,
    /// LoROM map mode, size code). By default the vanilla Direct-Map16 placeholder
    /// pointer ($0DB3E3 at $0DA4BB) is present so HasDm16Hijack is false, like a clean
    /// ROM; dm16: true leaves it repointed (zeros), which reads as "LM DM16 installed".
    /// </summary>
    public static byte[] Image(int size = 0x80000, bool dm16 = false)
    {
        var d = new byte[size];
        WriteInternalHeader(d, 0, size);
        if (!dm16)
        {
            int fo = Rom.SnesToPc(0x0DA4BB);
            d[fo] = 0xE3; d[fo + 1] = 0xB3; d[fo + 2] = 0x0D;   // vanilla handler $0DB3E3
        }
        return d;
    }

    public static void WriteInternalHeader(byte[] d, int off, int romSize)
    {
        Encoding.ASCII.GetBytes("SUPER MARIOWORLD".PadRight(21)).CopyTo(d, off + 0x7FC0);
        d[off + 0x7FD5] = 0x20;                                 // map mode: LoROM
        int kb = romSize / 1024, code = 0;
        while ((1 << code) < kb) code++;
        d[off + 0x7FD7] = (byte)code;                           // size = 2^code KB
    }

    /// <summary>Synthetic Rom (headerless, valid checksum). 512KB default; pass
    /// size: 0x100000 when a test needs expanded space (pc >= 0x80000).</summary>
    public static Rom Create(int size = 0x80000, bool dm16 = false)
    {
        var rom = Rom.FromBytes(Image(size, dm16));
        RatsWriter.FixChecksum(rom);
        return rom;
    }

    /// <summary>Synthetic Rom plus an empty Level: 5 header bytes + 0xFF terminator at
    /// LevelDataSnes, layer-1 pointer table entry repointed there, then parsed.</summary>
    public static (Rom Rom, Level Level) CreateWithLevel(bool dm16 = false)
    {
        var rom = Create(dm16: dm16);
        int fo = rom.FileOffset(LevelDataSnes);
        LevelHeaderBytes.CopyTo(rom.Data, fo);
        rom.Data[fo + 5] = 0xFF;
        rom.SetLayer1Pointer(TestLevel, LevelDataSnes);
        return (rom, LevelParser.Parse(rom, TestLevel));
    }
}
