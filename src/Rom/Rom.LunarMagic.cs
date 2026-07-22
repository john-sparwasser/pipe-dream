namespace PipeDream;

/// <summary>
/// Rom — LUNAR MAGIC hack detection + expanded-table location/decode (CONTRACT §7).
///
/// A ROM saved by Lunar Magic carries LM's inserted ASM plus expanded data tables (extended
/// Map16 defs, per-level GFX bypass, custom palettes, sprite entry sizes...). LM places most
/// of those tables at per-ROM addresses, but the *code* that reads them is byte-stable — so
/// we locate each table by scanning for that code signature and reading the address baked in
/// as its operand (ScanOperand). Fixed-address hooks/tables are read directly. All of this is
/// detection + read/write of LM's structures; vanilla ROMs return "absent" from every probe.
/// </summary>
public sealed partial class Rom
{
    /// <summary>
    /// True if LM's Direct Map16 ASM is installed. LM repurposes the reserved object slots
    /// 0x23/0x27 by repointing their handlers away from the vanilla placeholder $0DB3E3
    /// (handler table entry for obj 0x23 is at $0DA4BB).
    /// </summary>
    public bool HasDm16Hijack => ReadValue(0x0DA4BB, 3) != 0x0DB3E3;

    /// <summary>
    /// LM extended Map16 defs (tiles 0x200-0xFFF), decoded from LM's Map16-lookup hijack:
    /// $00C17A = JSL $06F5D0 → piecewise pointer math at fixed $06F540, whose 0x200-0xFFF
    /// branch is `ADC #imm : LDY #bank&lt;&lt;8` at fixed $06F552 (CONTRACT §7a-rev).
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
    /// Write a level's LM custom palette (§7e): the same 0x202 blob LmCustomPalette reads
    /// (word 0 = back color, 256 CGRAM words with row color-0 slots zeroed). Overwrites an
    /// existing blob in place (the size is fixed); otherwise allocates a RATS block and
    /// points the $0EF600 table entry at it. Requires LM's palette hook — without the
    /// $0095E9 JML the game would never read the table.
    /// </summary>
    public void WriteLmCustomPalette(int level, ushort back, ushort[] colors)
    {
        if (!HasLmPaletteHook)
            throw new InvalidOperationException("ROM lacks LM's palette ASM — save it in Lunar Magic once first.");
        var blob = new byte[0x202];
        blob[0] = (byte)back; blob[1] = (byte)(back >> 8);
        for (int i = 0; i < 256; i++)
        {
            ushort c = (i & 15) == 0 ? (ushort)0 : colors[i];
            blob[2 + i * 2] = (byte)c; blob[3 + i * 2] = (byte)(c >> 8);
        }

        int ptr = ReadValue(LmPaletteTable + level * 3, 3);
        if (ptr != 0 && ptr != 0xFFFFFF)
        {
            int fo = FileOffset(ptr);
            if (fo >= 8 && fo + 0x202 <= Data.Length &&
                Data[fo - 8] == 'S' && Data[fo - 7] == 'T' && Data[fo - 6] == 'A' && Data[fo - 5] == 'R')
            {
                Array.Copy(blob, 0, Data, fo, 0x202);
                return;
            }
        }
        int addr = AllocateRats(blob);
        int tfo = FileOffset(LmPaletteTable + level * 3);
        Data[tfo] = (byte)addr; Data[tfo + 1] = (byte)(addr >> 8); Data[tfo + 2] = (byte)(addr >> 16);
    }

    /// <summary>
    /// Find the little-endian 3-byte operand that sits between <paramref name="prefix"/> and
    /// <paramref name="suffix"/> code bytes (-1 in prefix = wildcard). Returns -1 if not found.
    /// This is how LM's per-ROM table addresses are recovered: the surrounding opcodes are
    /// byte-stable, only the baked-in operand address varies.
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
}
