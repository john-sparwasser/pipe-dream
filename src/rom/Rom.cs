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

    // Secondary entrances: four parallel 512-entry tables, one byte each (see
    // SecondaryEntrance for the bit layout). Vanilla addresses — LM extends the index range
    // but leaves the tables where they are.
    public const int SecondaryEntranceCount = 0x200;
    private static readonly int[] SecondaryEntranceTables =
        [0x05F800, 0x05FA00, 0x05FC00, 0x05FE00];

    /// <summary>The fifth byte (LM's Y high) lives wherever the reader at $05DC85 points, and
    /// only where that reader exists — a base without it reads the byte as zero and drops it
    /// on write, so the record round-trips on any ROM.</summary>
    public SecondaryEntrance ReadSecondaryEntrance(int index)
    {
        Span<byte> b = stackalloc byte[6];
        for (int t = 0; t < 4; t++) b[t] = Data[FileOffset(SecondaryEntranceTables[t] + index)];
        if (this.HasFreeSecondaryPositions)
        {
            b[4] = Data[FileOffset(this.LmSecondaryYHighTable + index)];
            b[5] = Data[FileOffset(this.LmSecondaryFgBgTable + index)];
        }
        return new SecondaryEntrance(b);
    }

    public void WriteSecondaryEntrance(int index, SecondaryEntrance e)
    {
        byte[] b = e.ToBytes();
        for (int t = 0; t < 4; t++) Data[FileOffset(SecondaryEntranceTables[t] + index)] = b[t];
        if (!this.HasFreeSecondaryPositions) return;
        Data[FileOffset(this.LmSecondaryYHighTable + index)] = b[4];
        Data[FileOffset(this.LmSecondaryFgBgTable + index)] = b[5];
    }

    // Main entrance / entry settings: the sibling tables, indexed by LEVEL (see MainEntrance).
    // The last two are Lunar Magic's method-2 bytes, at fixed addresses but only meaningful
    // where its routine is installed.
    private static readonly int[] MainEntranceTables =
        [0x05F000, 0x05F200, 0x05F400, 0x05F600, LmEntranceFlags, LmEntranceYHigh, LmEntranceFgBg];
    public const int LmEntranceFlags = 0x05DE00, LmEntranceYHigh = 0x06FC00, LmEntranceFgBg = 0x06FE00;

    /// <summary>LM's registration of the sprite size table (help: "Custom Sprite List Sizes", PC
    /// 0x7750C/0x7750F on a headered ROM): the table's SNES address, and 0x42 to enable it. LM's
    /// level engine (block C, transplanted by prep v10) reads both — DogsOfWar and ShaoBase
    /// register theirs here, so this comes before the signature scan for older PIXI code.</summary>
    public const int LmSpriteSizePtr = 0x0EF30C, LmSpriteSizeFlag = 0x0EF30F;
    /// <summary>LM's 4x3-byte pointer table to the uncompressed ExAnimation source files 60-63.</summary>
    public const int LmAltExGfxTable = 0x03BCC0;
    public const int LmEntranceLayer2 = 0x06FA00;

    /// <summary>Bytes 6-9 are LM's midway tables, wherever this ROM keeps them (per-ROM, 0x200
    /// apart); like the method-2 bytes they are read as zero and dropped on write where the
    /// routine that consumes them is absent.</summary>
    /// <summary>Bytes 0-3 always; 4, 5 and 10 ($05DE00, $06FC00, $06FE00) where LM's method-2
    /// routine is; 6-9 where its midway routine is; 11 where its level-height engine is. -1 = not on
    /// this base.</summary>
    private int MainEntranceByteAddr(int t, int level) => t switch
    {
        < 4 => MainEntranceTables[t] + level,
        4 or 5 or 10 => this.HasFreeEntrancePositions ? MainEntranceTables[t == 10 ? 6 : t] + level : -1,
        11 => this.HasLmLevelHeight ? this.LmLevelHeightTable + level : -1,
        _ => this.HasFreeMidwayPosition ? this.LmMidwayTable + (t - 6) * 0x200 + level : -1,
    };

    public MainEntrance ReadMainEntrance(int level)
    {
        Span<byte> b = stackalloc byte[12];
        for (int t = 0; t < 12; t++)
            if (MainEntranceByteAddr(t, level) is var at and >= 0) b[t] = Data[FileOffset(at)];
        return new MainEntrance(b);
    }

    public void WriteMainEntrance(int level, MainEntrance e)
    {
        byte[] b = e.ToBytes();
        for (int t = 0; t < 12; t++)
            if (MainEntranceByteAddr(t, level) is var at and >= 0) Data[FileOffset(at)] = b[t];
    }

    /// <summary>Layer 2 pointer. Bank $FF means "layer 2 is a background image", not object data.</summary>
    public int Layer2Pointer(int level) => ReadValue(Layer2TableSnes + level * 3, 3);
    public bool Layer2IsBackground(int level) => (Layer2Pointer(level) >> 16) == 0xFF;

    /// <summary>Repoint a level's Layer 2 table entry at a SNES address. Writing a real bank
    /// here also converts a background-image level to object mode, since the mode IS the
    /// bank byte ($FF = background).</summary>
    public void SetLayer2Pointer(int level, int snes)
    {
        int fo = FileOffset(Layer2TableSnes + level * 3);
        Data[fo] = (byte)snes; Data[fo + 1] = (byte)(snes >> 8); Data[fo + 2] = (byte)(snes >> 16);
    }

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

    /// <summary>Whether the game loads layer-2 OBJECT data for a level mode (CONTRACT §10).
    /// The other modes use layer 2 for a background image or not at all, so an object stream
    /// written for them is simply never read.</summary>
    public static bool LoadsLayer2Objects(int levelMode) =>
        (levelMode & 0x1F) is not (0x00 or 0x0A or 0x0C or 0x0D or 0x0E or 0x11 or 0x1E);

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

    /// <summary>Session-only level header overrides: level → the 5 replacement header bytes.
    /// Applied by LevelParser.Parse, so a header edit reaches everything the header drives
    /// (object dispatch, palettes, layer 2, sprite tiles) through the normal parse path.
    /// Hydrated from / stashed to ProjectFile.LevelState.Header by LevelSession.</summary>
    public readonly Dictionary<int, byte[]> LevelHeaderOverrides = new();

    /// <summary>Edited layer-2 backgrounds: level → the 0x400 BG Map16 def indices the RLE
    /// stream decodes to (page NOT applied — it comes from the stream's address, §10a).
    /// LevelParser.DecodeBgImage prefers these, so an edit reaches the level canvas, the
    /// Background tab and the built ROM through the one path the base ROM already uses.</summary>
    public readonly Dictionary<int, byte[]> BgTilemaps = new();

    /// <summary>Imported layer-3 tilemaps: level → a flat 16-bit map, LM's LT3 file shape
    /// (0x800/0x1000/0x2000 bytes). Replaces vanilla's (level mode, option) pick for that level
    /// wherever <see cref="Layer3.LevelTilemap"/> is asked. Hydrated from / stashed to
    /// ProjectFile.LevelState.Layer3Tilemap. The build inserts it as an ExGFX file and points
    /// the record's LT3 slot at it (CONTRACT §12b).</summary>
    public readonly Dictionary<int, byte[]> Layer3Tilemaps = new();

    /// <summary>Session edits to LM's advanced layer-3 bypass: level → the settings, or null
    /// for "this level has none". A present key always wins over the base ROM's record, which
    /// is what lets an edit turn the group OFF as well as on. Hydrated from / stashed to
    /// ProjectFile.LevelState.Layer3Advanced.</summary>
    public readonly Dictionary<int, Layer3.Advanced?> Layer3AdvancedOverrides = new();

    /// <summary>Imported ExGFX files (the project's GFX store): file id → raw planar bytes
    /// at the ROM's bit depth. Consulted first by Gfx.Cached, so imports render everywhere
    /// a GFX id resolves. Hydrated from / stashed to ProjectFile.Gfx by LevelSession;
    /// mutate via Gfx.InvalidateCache so no stale decode survives.</summary>
    public readonly Dictionary<int, byte[]> ImportedGfx = new();

    /// <summary>Display names for imported ExGFX (file id → name), so a custom file is
    /// identifiable as something other than a hex number. Defaulted from the imported
    /// filename. Pure metadata — nothing in the ROM read/write path consults it. Hydrated
    /// from / stashed to ProjectFile.GfxNames alongside <see cref="ImportedGfx"/>.</summary>
    public readonly Dictionary<int, string> ImportedGfxNames = new();

    /// <summary>An imported file's name, or "" when it has none.</summary>
    public string GfxName(int file) => ImportedGfxNames.GetValueOrDefault(file, "");

    /// <summary>Decompressed GFX file cache for <see cref="Gfx.Cached"/> (file# → data).</summary>
    internal readonly Dictionary<int, byte[]?> GfxFileCache = new();

    /// <summary>Memoized <see cref="Gfx.RomBpp"/> (-1 = not probed yet); cleared with the
    /// GFX cache. Probing decompresses GFX00, and the GFX editor asks once per frame.</summary>
    internal int RomBppCache = -1;
}
