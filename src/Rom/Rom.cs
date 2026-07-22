using System.Text;

namespace PipeDream;

/// <summary>
/// SMW ROM container + addressing foundation (CONTRACT §1). This core partial holds the raw
/// bytes, the parsed SNES internal header, and the LoROM address math every other partial
/// builds on. The rest of the ROM surface is split by concern:
///
///   • Rom.LevelData.cs  — reading the per-level pointer tables (where level data lives)
///   • Rom.Save.cs       — the write path: RATS free-space allocation, expand, checksum, save
///   • Rom.LunarMagic.cs — detecting LM hacks and locating/decoding LM's expanded tables
///
/// LoROM only (SMW is always LoROM). An address called "snes" is a 24-bit SNES address; "pc"
/// is the headerless ROM offset; a file offset = pc + HeaderOffset (the copier header, if any).
/// </summary>
public sealed partial class Rom
{
    public byte[] Data;               // raw file bytes (includes copier header if present)
    public readonly int HeaderOffset; // 0x200 if a 512-byte copier header is present, else 0

    // Parsed SNES internal header (CONTRACT §1).
    public readonly string Title;
    public readonly byte MapMode;         // $FFD5 — 0x20 = LoROM
    public readonly byte CartType;        // $FFD6
    public readonly byte RomSizeCode;     // $FFD7 — log2(KB)
    public readonly byte SramSizeCode;    // $FFD8
    public readonly ushort Checksum;      // $FFDE
    public readonly ushort ChecksumComplement; // $FFDC

    /// <summary>Header-declared ROM size in bytes (2^code KB).</summary>
    public int DeclaredRomSize => (1 << RomSizeCode) * 1024;
    /// <summary>Actual ROM size on disk, excluding any copier header.</summary>
    public int ActualRomSize => Data.Length - HeaderOffset;
    public bool IsLoRom => (MapMode & 0x01) == 0; // 0x20 LoROM / 0x21 HiROM

    private Rom(byte[] data, int headerOffset)
    {
        Data = data;
        HeaderOffset = headerOffset;

        // SNES internal header at $00FFC0 → pc 0x7FC0.
        int h = 0x7FC0 + HeaderOffset;
        Title = Encoding.ASCII.GetString(Data, h, 21).TrimEnd();
        MapMode = Data[h + 0x15];
        CartType = Data[h + 0x16];
        RomSizeCode = Data[h + 0x17];
        SramSizeCode = Data[h + 0x18];
        ChecksumComplement = (ushort)(Data[h + 0x1C] | (Data[h + 0x1D] << 8));
        Checksum = (ushort)(Data[h + 0x1E] | (Data[h + 0x1F] << 8));
    }

    public static Rom Load(string path) => FromBytes(File.ReadAllBytes(path));

    public static Rom FromBytes(byte[] data)
    {
        // Copier header: present when file size mod 0x8000 == 0x200 (CONTRACT §1).
        int headerOffset = (data.Length % 0x8000) == 0x200 ? 0x200 : 0;
        return new Rom(data, headerOffset);
    }

    // --- Addressing (LoROM) -------------------------------------------------

    /// <summary>LoROM SNES address → headerless PC offset.</summary>
    public static int SnesToPc(int snes)
        => ((snes & 0x7F0000) >> 1) | (snes & 0x7FFF);

    /// <summary>Headerless PC offset → LoROM SNES address (bank $00-$3F, high half).</summary>
    public static int PcToSnes(int pc)
        => ((pc << 1) & 0x7F0000) | (pc & 0x7FFF) | 0x8000;

    public int FileOffset(int snes) => SnesToPc(snes) + HeaderOffset;

    public byte ReadByte(int snes) => Data[FileOffset(snes)];

    /// <summary>Read a little-endian multi-byte value (1–4 bytes) at a SNES address.</summary>
    public int ReadValue(int snes, int bytes)
    {
        int fo = FileOffset(snes), v = 0;
        for (int i = 0; i < bytes; i++) v |= Data[fo + i] << (8 * i);
        return v;
    }

    /// <summary>Human-readable map-mode name for the UI.</summary>
    public string MapModeName => MapMode switch
    {
        0x20 => "LoROM",
        0x21 => "HiROM",
        0x22 => "LoROM/SA-1?",
        0x23 => "SA-1",
        0x30 => "LoROM+FastROM",
        0x31 => "HiROM+FastROM",
        _ => $"0x{MapMode:X2}",
    };
}
