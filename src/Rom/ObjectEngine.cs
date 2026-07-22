namespace PipeDream;

/// <summary>
/// Expands a parsed <see cref="Level"/> into a Map16 grid by replicating the SMW object
/// handlers (bank 0D). See reference/CONTRACT.md §4a/§4b. Currently implements the two
/// shared families (rectangle fill + single-tile lookup); other object families place a
/// marker tile (bit 0x8000 set) until their handlers are ported.
/// </summary>
public static partial class ObjectEngine
{
    // Object-number bit distinguishing a not-yet-implemented placeholder from a real tile.
    public const int Marker = 0x8000;

    /// <summary>
    /// Expand a level's object stream by EXECUTING the ROM's own loader + handlers
    /// (`LoadLevelData` $0585FF) in a small 65816 interpreter, then reading back the
    /// tilemap planes. Tiles are correct by construction for every object, every tileset,
    /// and LM's custom handlers. Falls back to the hand-ported engine on emulation failure.
    /// </summary>
    public static Map16Grid Render(Rom rom, Level level)
    {
        try { return RenderEmulated(rom, level.Header, level.DataPointer, layer: 0); }
        catch { return RenderPorted(rom, level.Header, level.Objects); }
    }

    /// <summary>Layer-2 object stream via the same emulation ($1933 = 1).</summary>
    public static Map16Grid? RenderLayer2(Rom rom, LevelHeader header, int levelNum)
    {
        if (rom.Layer2IsBackground(levelNum)) return null;
        try { return RenderEmulated(rom, header, rom.Layer2Pointer(levelNum), layer: 1); }
        catch
        {
            var objs = Level.ParseLayer2(rom, levelNum);
            return objs is null ? null : RenderPorted(rom, header, objs);
        }
    }

    public static Cpu65816? LastCpu;    // debug hook

    /// <summary>
    /// Render an edited object list by encoding it and running the ROM loader against the
    /// stream injected into RAM (bank $7F, below the $C800 high plane) — so edits use the
    /// same accurate emulated engine as the ROM's own data. `encoded` = Level.Encode output
    /// (5-byte header + objects + 0xFF terminator).
    /// </summary>
    public static Map16Grid RenderEmulatedStream(Rom rom, LevelHeader header, byte[] encoded, int layer)
    {
        var cpu = new Cpu65816(rom);
        LastCpu = cpu;
        Array.Fill(cpu.Ram7E, (byte)0x25, 0xC800, 0x3800);
        for (int i = 0; i < encoded.Length && i < 0xC000; i++) cpu.Ram7F[i] = encoded[i];
        return RenderEmulatedCore(rom, cpu, header, 0x7F0000, layer);
    }

    public static Map16Grid RenderEmulated(Rom rom, LevelHeader header, int dataPtrSnes, int layer)
    {
        var cpu = new Cpu65816(rom);
        LastCpu = cpu;
        // Tilemap init as at $058074: low planes 0x25, high planes 0x00.
        Array.Fill(cpu.Ram7E, (byte)0x25, 0xC800, 0x3800);
        return RenderEmulatedCore(rom, cpu, header, dataPtrSnes, layer);
    }

    private static Map16Grid RenderEmulatedCore(Rom rom, Cpu65816 cpu, LevelHeader header, int dataPtrSnes, int layer)
    {
        byte RomOrRam(int snes)     // $65 pointer may target ROM (real level) or $7F RAM (edited stream)
            => (snes >> 16) == 0x7F ? cpu.Ram7F[snes & 0xFFFF] : rom.ReadByte(snes);

        void W(int addr, byte v) => cpu.Ram7E[addr] = v;
        int data = dataPtrSnes + 5;                       // past the 5-byte header copy
        W(0x65, (byte)data); W(0x66, (byte)(data >> 8)); W(0x67, (byte)(data >> 16));
        W(0x1925, (byte)header.LevelMode);
        W(0x1931, (byte)header.Tileset);
        W(0x1930, (byte)header.BgPalette);
        W(0x192B, (byte)header.SpriteSet);
        W(0x5B, rom.ReadByte(0x058417 + (header.LevelMode & 0x1F)));   // VerticalTable
        W(0x1933, (byte)layer);

        if (RomOrRam(data) != 0xFF)                       // empty level: nothing to run
            cpu.CallNear(0x05_85FF);

        // Plane bases come from the same tables the loader used ($00BEA8/$00BEAC → per-mode
        // screen tables of 24-bit addresses, 3 bytes per screen).
        int mode = header.LevelMode & 0x1F;
        int lowTbl = rom.ReadValue(0x00BEA8 + layer * 2, 2);
        int highTbl = rom.ReadValue(0x00BEAC + layer * 2, 2);
        int lowScr = rom.ReadValue(lowTbl + mode * 2, 2);
        int highScr = rom.ReadValue(highTbl + mode * 2, 2);
        // LM-saved ROMs patch the loader and rebuild plane pointers at runtime — these
        // static tables are dead there. Bail to the ported engine until we capture the
        // pointers from the emulation itself (see CONTRACT §13 TODO).
        if ((rom.ReadValue(lowScr, 3) >> 16) is not (0x7E or 0x7F))
            throw new InvalidOperationException("plane tables not vanilla (LM-patched loader)");

        bool vertical = rom.IsVerticalMode(header.LevelMode);
        int screens = Math.Max(1, header.Screens);
        var g = new Map16Grid(vertical ? 32 : screens * 16, vertical ? screens * 16 : 32);

        byte Ram(int bank, int addr) => bank == 0x7F ? cpu.Ram7F[addr & 0xFFFF] : cpu.Ram7E[addr & 0xFFFF];
        for (int s = 0; s < screens; s++)
        {
            int lo = rom.ReadValue(lowScr + s * 3, 3);
            int hi = rom.ReadValue(highScr + s * 3, 3);
            for (int i = 0; i < 0x200; i++)
            {
                int half = i >> 8, pos = i & 0xFF;
                int rx = pos & 0x0F, ry = pos >> 4;
                int cx, cy;
                if (vertical)
                {
                    // Vertical: screen = a 16-row band; +0x100 = the RIGHT 16 columns
                    // (loader: "high coordinate" INC $6C = right half, $0585BD swap).
                    cx = half * 16 + rx;
                    cy = s * 16 + ry;
                }
                else
                {
                    int y = half * 16 + ry;
                    if (y >= 27) continue;                 // screens are 16x27
                    cx = s * 16 + rx; cy = y;
                }
                int tile = Ram(lo >> 16, lo + (half << 8) + pos)
                         | (Ram(hi >> 16, hi + (half << 8) + pos) << 8);
                g.Set(cx, cy, tile);
            }
        }
        return g;
    }

    /// <summary>Handler address for an object in a tileset, from the ROM's dispatch tables.</summary>
    public static int Handler(Rom rom, int tileset, int obj)
    {
        int dispatcher = rom.ReadValue(0x0DA41E + (tileset & 0x0F) * 3, 3);
        return rom.ReadValue(dispatcher + 0x0A + (obj - 1) * 3, 3);
    }

}
