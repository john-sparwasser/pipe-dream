using System.Text;

namespace PipeDream;

/// <summary>
/// SMW ROM container + addressing foundation (CONTRACT §1, §3). Holds the raw bytes, the
/// parsed SNES internal header, the LoROM address math everything else builds on, and the
/// per-level pointer tables (where level data lives). The rest of the ROM surface lives in:
///
///   • RatsWriter.cs  — the write path: RATS free-space allocation, checksum, save
///   • LunarMagic.cs  — detecting LM hacks and locating/decoding LM's expanded tables
///
/// LoROM only (SMW is always LoROM). An address called "snes" is a 24-bit SNES address; "pc"
/// is the headerless ROM offset; a file offset = pc + HeaderOffset (the copier header, if any).
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

    // --- Per-level pointer tables (CONTRACT §3) ------------------------------
    // SMW keeps three parallel per-level pointer tables in bank $05, indexed by level number
    // (0x000-0x1FF). Each entry says where that level's raw data begins; the actual decoding
    // of that data lives elsewhere (LevelParser.Parse for the header + Layer-1/2 object
    // streams, SpriteData.Parse for the sprite stream). This only resolves the pointers.
    //
    //   Layer 1  $05E000  3 bytes/level  → header (5 bytes) + Layer-1 object stream
    //   Layer 2  $05E600  3 bytes/level  → Layer-2 object stream, OR a background image when
    //                                       the pointer's bank is $FF (a background-image id,
    //                                       not a real address)
    //   Sprites  $05EC00  2 bytes/level  → sprite stream; the data bank is fixed at $07

    public const int Layer1TableSnes = 0x05E000; // 3 bytes/level
    public const int Layer2TableSnes = 0x05E600; // 3 bytes/level
    public const int SpriteTableSnes = 0x05EC00; // 2 bytes/level, data bank fixed $07
    public const int LevelCount = 0x200;

    /// <summary>24-bit SNES pointer to a level's Layer 1 header+object data.</summary>
    public int Layer1Pointer(int level) => ReadValue(Layer1TableSnes + level * 3, 3);

    /// <summary>Repoint a level's Layer 1 table entry at a SNES address.</summary>
    public void SetLayer1Pointer(int level, int snes)
    {
        int fo = FileOffset(Layer1TableSnes + level * 3);
        Data[fo] = (byte)snes; Data[fo + 1] = (byte)(snes >> 8); Data[fo + 2] = (byte)(snes >> 16);
    }

    /// <summary>Layer 2 pointer. Bank $FF means "layer 2 is a background image", not object data.</summary>
    public int Layer2Pointer(int level) => ReadValue(Layer2TableSnes + level * 3, 3);
    public bool Layer2IsBackground(int level) => (Layer2Pointer(level) >> 16) == 0xFF;

    /// <summary>Sprite data pointer. Low 16 bits from the vanilla table; the bank is fixed
    /// $07 in clean ROMs, but LM relocates sprite data and keeps a per-level BANK table
    /// (<see cref="LunarMagic"/>'s LmSpriteBankTable) — reading bank $07 there yields stale data.</summary>
    public int SpritePointer(int level)
    {
        int bank = this.LmSpriteBankTable >= 0 ? ReadByte(this.LmSpriteBankTable + level) : 0x07;
        return (bank << 16) | ReadValue(SpriteTableSnes + level * 2, 2);
    }

    /// <summary>Repoint a level's sprite table entry (low 16 bits; the bank byte lives in
    /// LM's per-level bank table — the caller writes it when the table exists).</summary>
    public void SetSpritePointerWord(int level, int low16)
    {
        int fo = FileOffset(SpriteTableSnes + level * 2);
        Data[fo] = (byte)low16; Data[fo + 1] = (byte)(low16 >> 8);
    }

    /// <summary>True if a level mode is vertical (VerticalTable $058417, bit 0).</summary>
    public bool IsVerticalMode(int levelMode) => (ReadByte(0x058417 + (levelMode & 0x1F)) & 1) != 0;

    // --- Lunar Magic per-ROM state (logic in LunarMagic.cs) ------------------
    // Scan caches for LM's per-ROM table bases (-2 = not scanned yet; found once by
    // signature scan) plus session state. They live here because they are per-Rom-instance
    // state; everything that reads/writes them is in LunarMagic.cs.

    internal int lmActsAsBase = -2, lmGfxBypassBase = -2, lmExGfxBase = -2, lmSpriteSizeBase = -2, lmExAnimBase = -2;
    internal int lmGlobalExAnimPtr = -2;
    internal int lmExAnimSetupEntry = -2, lmExAnimProcEntry = -2;
    internal int pixiTable = -2;
    internal int lmSpriteBankTable = -2;
    internal int map16TileCount = -1;

    /// <summary>Session-only GFX slot overrides (the editor's GFX tab): (level, bypass word
    /// index) → GFX/ExGFX file. Overlaid on the bypass record in LunarMagic.LmGfxBypass so
    /// every consumer — Map16 compose, sprite tiles, the GFX tab — resolves the same files.
    /// Never saved.</summary>
    public readonly Dictionary<(int Level, int Word), int> GfxSlotOverrides = new();

    /// <summary>Decompressed GFX file cache for <see cref="Gfx.Cached"/> (file# → data).</summary>
    internal readonly Dictionary<int, byte[]?> GfxFileCache = new();
}
