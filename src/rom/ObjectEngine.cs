namespace PipeDream;

/// <summary>
/// Expands a parsed <see cref="Level"/> into a Map16 grid by replicating the SMW object
/// handlers (bank 0D). See reference/CONTRACT.md §4a/§4b. Currently implements the two
/// shared families (rectangle fill + single-tile lookup); other object families place a
/// marker tile (bit 0x8000 set) until their handlers are ported.
/// </summary>
public static class ObjectEngine
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
        catch { return PortedObjectEngine.Render(rom, level.Header, level.Objects); }
    }

    /// <summary>Layer-2 object stream via the same emulation ($1933 = 1).</summary>
    public static Map16Grid? RenderLayer2(Rom rom, LevelHeader header, int levelNum)
    {
        if (rom.Layer2IsBackground(levelNum)) return null;
        try { return RenderEmulated(rom, header, rom.Layer2Pointer(levelNum), layer: 1); }
        catch
        {
            var objs = LevelParser.ParseLayer2(rom, levelNum);
            return objs is null ? null : PortedObjectEngine.Render(rom, header, objs);
        }
    }

    public static Cpu65816? LastCpu;    // debug hook

    /// <summary>
    /// Render an edited object list by encoding it and running the ROM loader against the
    /// stream injected into RAM (bank $7F, below the $C800 high plane) — so edits use the
    /// same accurate emulated engine as the ROM's own data. `encoded` = LevelEncoder.Encode output
    /// (5-byte header + objects + 0xFF terminator).
    /// </summary>
    /// <summary>Instruction budget for one-object probe/preview renders — a solo object
    /// legitimately needs well under a million; a runaway handler (e.g. a DM16 number fed
    /// a stream without its tile bytes) should fail fast, not hang for seconds.</summary>
    public const int SoloBudget = 2_000_000;

    public static Map16Grid RenderEmulatedStream(Rom rom, LevelHeader header, byte[] encoded, int layer,
                                                 int maxInstructions = 30_000_000)
        => RenderEmulatedStream(rom, header, encoded, layer, null, out _, out _, maxInstructions);

    public static Map16Grid RenderEmulatedStream(Rom rom, LevelHeader header, byte[] encoded, int layer,
                                                 ushort[]? streamOwner, out Map16Grid? owners)
        => RenderEmulatedStream(rom, header, encoded, layer, streamOwner, out owners, out _);

    /// <summary>
    /// Same, additionally attributing every tile to the stream record(s) that wrote it.
    /// <paramref name="streamOwner"/> maps encoded byte offset → record id (0 = none);
    /// <paramref name="owners"/> gets each cell's LAST writer id (0/Empty = untouched),
    /// i.e. the topmost object — what click-selection wants. <paramref name="stacks"/>
    /// gets each written cell's FULL writer history bottom→top (key = y*Width+x) — the
    /// z-order under a cell; overlap isn't stored in the ROM, stream order is z-order.
    /// </summary>
    public static Map16Grid RenderEmulatedStream(Rom rom, LevelHeader header, byte[] encoded, int layer,
                                                 ushort[]? streamOwner, out Map16Grid? owners,
                                                 out Dictionary<int, ushort[]>? stacks,
                                                 int maxInstructions = 30_000_000)
    {
        var cpu = new Cpu65816(rom);
        LastCpu = cpu;
        Array.Fill(cpu.Ram7E, (byte)0x25, 0xC800, 0x3800);
        for (int i = 0; i < encoded.Length && i < 0xC000; i++) cpu.Ram7F[i] = encoded[i];
        if (streamOwner is not null)
        {
            cpu.StreamOwner = streamOwner;
            cpu.Owner7E = new ushort[0x10000];
            cpu.Owner7F = new ushort[0x10000];
            cpu.WriteLog = new();
        }
        return RenderEmulatedCore(rom, cpu, header, 0x7F0000, layer, out owners, out stacks, maxInstructions);
    }

    public static Map16Grid RenderEmulated(Rom rom, LevelHeader header, int dataPtrSnes, int layer)
    {
        var cpu = new Cpu65816(rom);
        LastCpu = cpu;
        // Tilemap init as at $058074: low planes 0x25, high planes 0x00.
        Array.Fill(cpu.Ram7E, (byte)0x25, 0xC800, 0x3800);
        return RenderEmulatedCore(rom, cpu, header, dataPtrSnes, layer, out _, out _, 30_000_000);
    }

    // Vanilla mode → per-screen plane-table address (bank 00), per layer. LM-saved ROMs
    // repoint some modes' entries into RAM tables that LM's init builds during level load;
    // we don't run that init, so we pre-fill that RAM with the vanilla tables (the game's
    // tilemap RAM layout is fixed — the rest of the engine depends on it). Dumped from a
    // clean ROM: $00BDA8/$00BDE8 (low plane, layer 0/1), $00BE28/$00BE68 (high plane).
    private static readonly ushort[][] VanillaLoMap =
    {
        new ushort[] { 0xBAD8,0xBAD8,0xBAD8,0xBB38,0xBB38,0xBB92,0xBB92,0xBBEC,0xBBEC,0x0000,0xBBEC,0x0000,0xBAD8,0xBBEC,0xBAD8,0xBAD8,
                       0x0000,0xBAD8,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0xBAD8,0xBAD8 },
        new ushort[] { 0xBB08,0xBB08,0xBB08,0xBB62,0xBB62,0xBBC2,0xBBC2,0xBC16,0xBC16,0x0000,0xBC16,0x0000,0xBB08,0xBC16,0xBB08,0xBB08,
                       0x0000,0xBB08,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0xBB08,0xBB08 },
    };
    private static readonly ushort[][] VanillaHiMap =
    {
        new ushort[] { 0xBC40,0xBC40,0xBC40,0xBCA0,0xBCA0,0xBCFA,0xBCFA,0xBD54,0xBD54,0x0000,0xBD54,0x0000,0xBC40,0xBD54,0xBC40,0xBC40,
                       0x0000,0xBC40,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0xBC40,0xBC40 },
        new ushort[] { 0xBC70,0xBC70,0xBC70,0xBCCA,0xBCCA,0xBD2A,0xBD2A,0xBD7E,0xBD7E,0x0000,0xBD7E,0x0000,0xBC70,0xBD7E,0xBC70,0xBC70,
                       0x0000,0xBC70,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0xBC70,0xBC70 },
    };

    private static Map16Grid RenderEmulatedCore(Rom rom, Cpu65816 cpu, LevelHeader header, int dataPtrSnes, int layer,
                                                out Map16Grid? owners, out Dictionary<int, ushort[]>? stacks,
                                                int maxInstructions)
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

        // LM-patched plane maps: mode entries repointed below $8000 reference RAM tables
        // that LM's init builds at level load. The emulated loader reads them via [$00],
        // so pre-fill that RAM from the vanilla tables (still present in the ROM).
        int hdrMode = header.LevelMode & 0x1F;
        for (int pl = 0; pl < 2; pl++)
        {
            int map = rom.ReadValue((pl == 0 ? 0x00BEA8 : 0x00BEAC) + layer * 2, 2);
            int scr = rom.ReadValue(map + hdrMode * 2, 2);
            int van = (pl == 0 ? VanillaLoMap : VanillaHiMap)[layer][hdrMode];
            if (scr == 0 || scr >= 0x8000 || van == 0) continue;   // ROM table: loader reads it fine
            for (int i = 0; i < 0x20 * 3; i++)
                cpu.Ram7E[(scr + i) & 0xFFFF] = rom.ReadByte(van + i);
        }

        // LM also patches the screen-step primitives (CODE_0DA95B / $0DA9D6 / $0DA9EF)
        // to add a stride from its RAM word $13D7 instead of the hardcoded vanilla +$1B0
        // (again built by LM's init). Seed the vanilla stride; harmless on clean ROMs.
        W(0x13D7, 0xB0); W(0x13D8, 0x01);

        if (RomOrRam(data) != 0xFF)                       // empty level: nothing to run
            cpu.CallNear(0x05_85FF, maxInstructions);

        bool vertical = rom.IsVerticalMode(header.LevelMode);
        // Full canvas (LM parity): content can sit past header.Screens. The loader filled
        // untouched RAM with blank sky ($25). Vertical caps at $1C bands ($C800+$1C*$200 = RAM end).
        int screens = vertical ? 0x1C : 0x20;

        // Per-screen plane-pointer tables (24-bit addresses, 3 bytes/screen), from the
        // static tables at $00BEA8/$00BEAC (vanilla). If those are dead (a LM-patched
        // loader relocates them), fall back to the pointers the emulated loader itself
        // left in scratch $00-$02 (low plane) / $0D-$0F (high) — set fresh for every
        // object (CODE_058676) — validated across all screens since handlers may clobber.
        int Mem24(int snes) => cpu.Read(snes >> 16, snes) | cpu.Read(snes >> 16, snes + 1) << 8
                                                          | (cpu.Read(snes >> 16, snes + 2) << 16);
        bool ValidTbl(int tbl, int entries)
        {
            if (tbl == 0) return false;
            for (int s = 0; s < entries; s++)
                if ((Mem24(tbl + s * 3) >> 16) is not (0x7E or 0x7F)) return false;
            return true;
        }
        int mode = header.LevelMode & 0x1F;
        int lowScr = rom.ReadValue(rom.ReadValue(0x00BEA8 + layer * 2, 2) + mode * 2, 2);
        int highScr = rom.ReadValue(rom.ReadValue(0x00BEAC + layer * 2, 2) + mode * 2, 2);
        if (!ValidTbl(lowScr, 1) || !ValidTbl(highScr, 1))
        {
            lowScr = cpu.Ram7E[0x00] | cpu.Ram7E[0x01] << 8 | (cpu.Ram7E[0x02] << 16);
            highScr = cpu.Ram7E[0x0D] | cpu.Ram7E[0x0E] << 8 | (cpu.Ram7E[0x0F] << 16);
            if (!ValidTbl(lowScr, screens) || !ValidTbl(highScr, screens))
                throw new InvalidOperationException("no valid plane tables (vanilla static + loader scratch both dead)");
        }
        var g = new Map16Grid(vertical ? 32 : screens * 16, vertical ? screens * 16 : 32);
        owners = cpu.Owner7E is not null ? new Map16Grid(g.Width, g.Height) : null;

        // Full writer history per low-plane address (consecutive duplicates collapsed).
        Dictionary<int, List<ushort>>? perAddr = null;
        if (cpu.WriteLog is { } wl)
        {
            perAddr = new Dictionary<int, List<ushort>>();
            foreach (var (bank, addr, id) in wl)
            {
                int key = (bank == 0x7F ? 0x10000 : 0) | addr;
                if (!perAddr.TryGetValue(key, out var l)) perAddr[key] = l = new();
                if (l.Count == 0 || l[^1] != id) l.Add(id);
            }
        }
        stacks = perAddr is not null ? new Dictionary<int, ushort[]>() : null;

        byte Ram(int bank, int addr) => bank == 0x7F ? cpu.Ram7F[addr & 0xFFFF] : cpu.Ram7E[addr & 0xFFFF];
        ushort Owner(int bank, int addr) => bank == 0x7F ? cpu.Owner7F![addr & 0xFFFF] : cpu.Owner7E![addr & 0xFFFF];
        for (int s = 0; s < screens; s++)
        {
            int lo = Mem24(lowScr + s * 3);        // tables may live in RAM on LM ROMs
            int hi = Mem24(highScr + s * 3);
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
                int loAddr = (lo + (half << 8) + pos) & 0xFFFF;
                owners?.Set(cx, cy, Owner(lo >> 16, loAddr));                    // low plane: always written
                if (perAddr is not null &&
                    perAddr.TryGetValue(((lo >> 16) == 0x7F ? 0x10000 : 0) | loAddr, out var hist))
                    stacks![cy * g.Width + cx] = hist.ToArray();
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

    // ---- resizability: which byte3 bits drive which axis ----

    /// <summary>Where an axis's size lives in byte3: a nibble, the whole byte (linear
    /// objects whose full byte is a length), or nowhere (fixed-size / subtype-only).</summary>
    public enum SizeSrc { None, Lo, Hi, Byte }
    public readonly record struct ObjResize(SizeSrc W, SizeSrc H);

    /// <summary>
    /// Learn empirically which byte3 nibble grows which axis: render the object solo at
    /// byte3 0x00 / 0x01 / 0x10 and compare footprint bboxes. Covers the real families —
    /// rect nibbles (most objects), full-byte length (both probes grow the same axis),
    /// diagonal slopes (one nibble grows both axes → both map to it), and subtype-only
    /// bytes (no growth → not resizable). Works unchanged for LM custom handlers since
    /// it runs the ROM's own code. Falls back to plain rect if emulation fails.
    /// </summary>
    public static ObjResize ProbeResize(Rom rom, Level level, int num)
    {
        if (SoloBBox(rom, level, num, 0x00) is not { } s0 ||
            SoloBBox(rom, level, num, 0x01) is not { } sl ||
            SoloBBox(rom, level, num, 0x10) is not { } sh)
            return new(SizeSrc.Lo, SizeSrc.Hi);
        bool loW = sl.w > s0.w, loH = sl.h > s0.h, hiW = sh.w > s0.w, hiH = sh.h > s0.h;
        return new(loW && hiW ? SizeSrc.Byte : loW ? SizeSrc.Lo : hiW ? SizeSrc.Hi : SizeSrc.None,
                   loH && hiH ? SizeSrc.Byte : loH ? SizeSrc.Lo : hiH ? SizeSrc.Hi : SizeSrc.None);
    }

    /// <summary>Footprint bbox of one object rendered alone (owner-tracked, no baseline diff needed).</summary>
    public static (int w, int h)? SoloBBox(Rom rom, Level level, int num, int b3)
    {
        var one = new List<LevelObject> { new(false, num, 0, 4, 10, b3, -1) };
        var offsets = new List<int>();
        byte[] enc = LevelEncoder.Encode(level, one, offsets);
        var so = new ushort[enc.Length];
        for (int b = offsets[0]; b < enc.Length - 1; b++) so[b] = 1;
        try
        {
            RenderEmulatedStream(rom, level.Header, enc, 0, so, out var owners, out _, SoloBudget);
            if (owners is null) return null;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            for (int y = 0; y < owners.Height; y++)
                for (int x = 0; x < owners.Width; x++)
                    if (owners.Get(x, y) == 1)
                    {
                        x0 = Math.Min(x0, x); y0 = Math.Min(y0, y);
                        x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                    }
            return x1 < 0 ? (0, 0) : (x1 - x0 + 1, y1 - y0 + 1);
        }
        catch { return null; }
    }

    public static int SizeOf(int b3, SizeSrc s) => s switch
    { SizeSrc.Lo => (b3 & 0x0F) + 1, SizeSrc.Hi => (b3 >> 4) + 1, SizeSrc.Byte => b3 + 1, _ => 1 };

    public static int MaxSize(SizeSrc s) => s == SizeSrc.Byte ? 256 : 16;

    public static int WithSize(int b3, SizeSrc s, int size) => s switch
    {
        SizeSrc.Lo => (b3 & 0xF0) | (Math.Clamp(size, 1, 16) - 1),
        SizeSrc.Hi => (b3 & 0x0F) | ((Math.Clamp(size, 1, 16) - 1) << 4),
        SizeSrc.Byte => Math.Clamp(size, 1, 256) - 1,
        _ => b3,
    };
}
