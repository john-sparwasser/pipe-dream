using System.Text;

namespace PipeDream;

/// <summary>
/// SMW ROM container + addressing foundation. See reference/CONTRACT.md §1–3.
/// LoROM only (SMW is always LoROM). All addresses called "snes" are 24-bit SNES
/// addresses; "pc" is the headerless ROM offset; file offset = pc + HeaderOffset.
/// </summary>
public sealed class Rom
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

    /// <summary>
    /// True if LM's Direct Map16 ASM is installed. LM repurposes the reserved object slots
    /// 0x23/0x27 by repointing their handlers away from the vanilla placeholder $0DB3E3
    /// (handler table entry for obj 0x23 is at $0DA4BB).
    /// </summary>
    public bool HasDm16Hijack => ReadValue(0x0DA4BB, 3) != 0x0DB3E3;

    // Level pointer tables (CONTRACT §3), all in bank $05.
    public const int Layer1TableSnes = 0x05E000; // 3 bytes/level
    public const int Layer2TableSnes = 0x05E600; // 3 bytes/level
    public const int SpriteTableSnes = 0x05EC00; // 2 bytes/level, data bank fixed $07
    public const int LevelCount = 0x200;

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

    // --- Level pointer tables (CONTRACT §3) ---------------------------------

    /// <summary>24-bit SNES pointer to a level's Layer 1 header+object data.</summary>
    public int Layer1Pointer(int level) => ReadValue(Layer1TableSnes + level * 3, 3);

    /// <summary>Layer 2 pointer. Bank $FF means "layer 2 is a background image", not object data.</summary>
    public int Layer2Pointer(int level) => ReadValue(Layer2TableSnes + level * 3, 3);
    public bool Layer2IsBackground(int level) => (Layer2Pointer(level) >> 16) == 0xFF;

    /// <summary>Sprite data pointer (16-bit; bank is fixed $07).</summary>
    public int SpritePointer(int level) => 0x070000 | ReadValue(SpriteTableSnes + level * 2, 2);

    // --- RATS (CONTRACT §2) -------------------------------------------------

    public readonly record struct Rat(int PcOffset, int Size);

    /// <summary>
    /// Enumerate valid RATS-protected regions in the expanded area (pc ≥ 0x80000).
    /// A tag is valid only when (size-1) XOR (~(size-1)) == 0xFFFF — required, because
    /// random data contains the ASCII bytes "STAR".
    /// </summary>
    public IEnumerable<Rat> EnumerateRats()
    {
        int end = Data.Length - 8;
        for (int pc = 0x80000; pc <= end - HeaderOffset; )
        {
            int fo = pc + HeaderOffset;
            if (Data[fo] == 0x53 && Data[fo + 1] == 0x54 && Data[fo + 2] == 0x41 && Data[fo + 3] == 0x52) // "STAR"
            {
                int sizeField = Data[fo + 4] | (Data[fo + 5] << 8);
                int invField = Data[fo + 6] | (Data[fo + 7] << 8);
                if ((sizeField ^ invField) == 0xFFFF)
                {
                    int size = sizeField + 1; // stored value is size-1
                    yield return new Rat(pc, size);
                    pc += 8 + size;
                    continue;
                }
            }
            pc++;
        }
    }

    // --- Writing (save path) ------------------------------------------------

    /// <summary>Grow the ROM to <paramref name="romBytes"/> (zero-filled) and update the size code.</summary>
    public void ExpandTo(int romBytes)
    {
        int want = romBytes + HeaderOffset;
        if (Data.Length >= want) return;
        var n = new byte[want];
        Array.Copy(Data, n, Data.Length);
        Data = n;
        int kb = romBytes / 1024, code = 0;
        while ((1 << code) < kb) code++;
        Data[0x7FD7 + HeaderOffset] = (byte)code;
    }

    /// <summary>First free run of <paramref name="need"/> bytes in expanded space (PC ≥ 0x80000), skipping valid RATs.</summary>
    public int FindFreeSpace(int need)
    {
        int end = Data.Length - HeaderOffset;
        for (int p = 0x80000; p + need <= end;)
        {
            int fo = p + HeaderOffset;
            if (Data[fo] == 0x53 && Data[fo + 1] == 0x54 && Data[fo + 2] == 0x41 && Data[fo + 3] == 0x52)
            {
                int sz = Data[fo + 4] | (Data[fo + 5] << 8), inv = Data[fo + 6] | (Data[fo + 7] << 8);
                if ((sz ^ inv) == 0xFFFF) { p += 8 + sz + 1; continue; }
            }
            bool ok = true;
            for (int i = 0; i < need; i++)
                if (Data[fo + i] != 0) { ok = false; p += i + 1; break; }
            if (ok) return p;
        }
        throw new InvalidOperationException("no free space (expand the ROM first)");
    }

    /// <summary>Write a RATS-protected block and return the SNES address of the data (after the tag).</summary>
    public int AllocateRats(byte[] data)
    {
        int pc = FindFreeSpace(8 + data.Length), fo = pc + HeaderOffset;
        Data[fo] = 0x53; Data[fo + 1] = 0x54; Data[fo + 2] = 0x41; Data[fo + 3] = 0x52;   // "STAR"
        int sm1 = data.Length - 1;
        Data[fo + 4] = (byte)sm1; Data[fo + 5] = (byte)(sm1 >> 8);
        int invv = sm1 ^ 0xFFFF;
        Data[fo + 6] = (byte)invv; Data[fo + 7] = (byte)(invv >> 8);
        Array.Copy(data, 0, Data, fo + 8, data.Length);
        return PcToSnes(pc + 8);
    }

    public void SetLayer1Pointer(int level, int snes)
    {
        int fo = FileOffset(Layer1TableSnes + level * 3);
        Data[fo] = (byte)snes; Data[fo + 1] = (byte)(snes >> 8); Data[fo + 2] = (byte)(snes >> 16);
    }

    /// <summary>
    /// Recompute and write the SNES checksum ($FFDE) + complement ($FFDC). Assumes a
    /// power-of-two ROM size (our expanded ROMs are 1/2/4 MB). The placeholder-invariant
    /// trick: checksum computed with checksum=0/complement=0xFFFF equals the final sum.
    /// </summary>
    public void FixChecksum()
    {
        int h = HeaderOffset, size = ActualRomSize;
        Data[0x7FDC + h] = 0xFF; Data[0x7FDD + h] = 0xFF;   // complement placeholder
        Data[0x7FDE + h] = 0x00; Data[0x7FDF + h] = 0x00;   // checksum placeholder
        long sum = 0;
        for (int i = 0; i < size; i++) sum += Data[h + i];
        int chk = (int)(sum & 0xFFFF), comp = chk ^ 0xFFFF;
        Data[0x7FDE + h] = (byte)chk; Data[0x7FDF + h] = (byte)(chk >> 8);
        Data[0x7FDC + h] = (byte)comp; Data[0x7FDD + h] = (byte)(comp >> 8);
    }

    public void SaveAs(string path) { FixChecksum(); File.WriteAllBytes(path, Data); }

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
