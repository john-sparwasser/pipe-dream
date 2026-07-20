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

    /// <summary>
    /// LM extended Map16 defs (tiles 0x200-0xFFF), decoded from LM's Map16-lookup hijack:
    /// $00C17A = JSL $06F5D0 → piecewise pointer math at fixed $06F540, whose 0x200-0xFFF
    /// branch is `ADC #imm : LDY #bank<<8` at fixed $06F552 (CONTRACT §7a-rev).
    /// def(tile) = bank:(imm + tile*8). Returns (imm, bank), bank 0 = no extended defs.
    /// NOTE: the RATS pointer at $02C2E1 is NOT reliable (points at a stale block in ShaoBase).
    /// </summary>
    public (int Imm, int Bank) LmMap16Defs
    {
        get
        {
            if (ReadByte(0x00C17A) != 0x22 || ReadValue(0x00C17B, 3) != 0x06F5D0) return (0, 0);
            if (ReadByte(0x06F552) != 0x69 || ReadByte(0x06F555) != 0xA0) return (0, 0);
            return (ReadValue(0x06F553, 2), ReadValue(0x06F556, 2) >> 8);
        }
    }

    /// <summary>Kept for callers that just need a presence check: >= 0 when extended defs exist.</summary>
    public int LmMap16Base => LmMap16Defs.Bank == 0 ? -1 : (LmMap16Defs.Bank << 16) | LmMap16Defs.Imm;

    /// <summary>LM's acts-like table (2 bytes/tile, per-ROM location): the behavior tile a Map16 tile acts as.</summary>
    public int ActsAs(int tile) => LmActsAsBase < 0 ? tile : ReadValue(LmActsAsBase + tile * 2, 2);

    // --- LM expanded-table bases (CONTRACT §7d) -----------------------------
    // LM bakes the addresses of its expanded tables into its inserted ASM as LDA long,X
    // operands; the surrounding code bytes are stable across ROMs, the operands are not.
    // Each base is found once by signature scan and cached (-2 = not scanned yet).

    private int lmActsAsBase = -2, lmGfxBypassBase = -2, lmExGfxBase = -2, lmSpriteSizeBase = -2;

    /// <summary>
    /// Base of LM's sprite entry-size table (0x400 bytes, byte size per (extraBits&lt;&lt;8)|sprite#,
    /// includes the 3 base bytes), or -1 = vanilla 3-byte entries. Located via the LDA long,X
    /// operand in LM's sprite-advance hijack (CONTRACT §11).
    /// </summary>
    public int LmSpriteSizeBase => lmSpriteSizeBase != -2 ? lmSpriteSizeBase
        : lmSpriteSizeBase = ScanOperand([0x4A, 0x4A, 0x29, 0x03, 0xEB, 0xC8, 0xC8, 0xB7, 0xCE, 0x88, 0x88,
                                          0x08, 0xC2, 0x10, 0xDA, 0xAA, 0x98, 0x18, 0x7F], []);

    /// <summary>Base of LM's acts-like table, or -1 (from the remap reader in LM's $06F5D0 code).</summary>
    public int LmActsAsBase => lmActsAsBase != -2 ? lmActsAsBase
        : lmActsAsBase = ScanOperand([0xA8, 0x0A, 0xAA, 0x30, -1, 0xBF], [0xC9, 0x00, 0x02]);

    /// <summary>Base of LM's per-level GFX bypass table (0x20 bytes/level), or -1.</summary>
    public int LmGfxBypassBase => lmGfxBypassBase != -2 ? lmGfxBypassBase
        : lmGfxBypassBase = ScanOperand([0xA5, 0xFE, 0xF0, -1, 0x3A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0xAA, 0xBF], []);

    /// <summary>Base of LM's ExGFX 0x100+ pointer table (3 bytes/file), or -1.</summary>
    public int LmExGfxBase => lmExGfxBase != -2 ? lmExGfxBase
        : lmExGfxBase = ScanOperand([0x38, 0xE9, 0x00, 0x01, 0x85, 0x8A, 0x0A, 0x18, 0x65, 0x8A, 0xAA, 0xBF], []);

    /// <summary>
    /// Per-level Super GFX Bypass record (16 words), or null if the hack is absent or the
    /// record is disabled. w0=AN2 (bit15 = bypass enabled), w1=AN1, w2=BG3, w3=BG2, w4=FG3,
    /// w5=BG1, w6=FG2, w7=FG1, w8-11=SP4..SP1. Slot value &amp; 0xFFF = GFX/ExGFX file#,
    /// 0x7F = slot uses the tileset default.
    /// </summary>
    public ushort[]? LmGfxBypass(int level)
    {
        if (LmGfxBypassBase < 0) return null;
        int fo = FileOffset(LmGfxBypassBase + level * 0x20);
        if (fo < 0 || fo + 0x20 > Data.Length) return null;
        var w = new ushort[16];
        for (int i = 0; i < 16; i++) w[i] = (ushort)(Data[fo + i * 2] | (Data[fo + i * 2 + 1] << 8));
        return (w[0] & 0x8000) != 0 ? w : null;
    }

    /// <summary>
    /// Total Map16 tile count: 0x200 vanilla; with LM extended defs, up to 0x1000 (the hijack's
    /// 0x200-0xFFF region), clipped where imm + tile*8 would wrap past the bank.
    /// </summary>
    public int Map16TileCount
    {
        get
        {
            var (imm, bank) = LmMap16Defs;
            return bank == 0 ? 0x200 : Math.Min(0x1000, (0x10000 - imm) / 8);
        }
    }

    /// <summary>True if LM's palette engine is installed: a JML hook at $0095E9 replaces the
    /// vanilla JSR UploadSpriteGFX / JSR LoadPalette pair (CONTRACT §7e).</summary>
    public bool HasLmPaletteHook => ReadByte(0x0095E9) == 0x5C;

    /// <summary>LM per-level custom palette pointer table (3 bytes/level, fixed address §7e).</summary>
    public const int LmPaletteTable = 0x0EF600;

    /// <summary>
    /// LM custom palette for a level, or null if none. The pointer table entry (0/0xFFFFFF =
    /// none) leads to a RATS-tagged 0x202-byte blob: word 0 = back-area color, then 256
    /// BGR555 words — a full CGRAM image (each row's color 0 is stored as 0/transparent).
    /// </summary>
    public (ushort Back, ushort[] Colors)? LmCustomPalette(int level)
    {
        if (!HasLmPaletteHook) return null;      // vanilla ROMs have unrelated data at $0EF600
        int ptr = ReadValue(LmPaletteTable + level * 3, 3);
        if (ptr == 0 || ptr == 0xFFFFFF) return null;
        int fo = FileOffset(ptr);
        if (fo < 8 || fo + 0x202 > Data.Length) return null;
        if (Data[fo - 8] != 'S' || Data[fo - 7] != 'T' || Data[fo - 6] != 'A' || Data[fo - 5] != 'R')
            return null;
        ushort back = (ushort)(Data[fo] | (Data[fo + 1] << 8));
        var colors = new ushort[256];
        for (int i = 0; i < 256; i++)
            colors[i] = (ushort)(Data[fo + 2 + i * 2] | (Data[fo + 3 + i * 2] << 8));
        return (back, colors);
    }

    /// <summary>
    /// Find the little-endian 3-byte operand that sits between <paramref name="prefix"/> and
    /// <paramref name="suffix"/> code bytes (-1 in prefix = wildcard). Returns -1 if not found.
    /// </summary>
    private int ScanOperand(int[] prefix, byte[] suffix)
    {
        int end = Data.Length - prefix.Length - 3 - suffix.Length;
        for (int i = HeaderOffset; i <= end; i++)
        {
            bool ok = true;
            for (int j = 0; j < prefix.Length && ok; j++)
                ok = prefix[j] < 0 || Data[i + j] == prefix[j];
            for (int j = 0; j < suffix.Length && ok; j++)
                ok = Data[i + prefix.Length + 3 + j] == suffix[j];
            if (!ok) continue;
            int p = i + prefix.Length;
            return Data[p] | (Data[p + 1] << 8) | (Data[p + 2] << 16);
        }
        return -1;
    }

    /// <summary>True if a level mode is vertical (VerticalTable $058417, bit 0).</summary>
    public bool IsVerticalMode(int levelMode) => (ReadByte(0x058417 + (levelMode & 0x1F)) & 1) != 0;

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
