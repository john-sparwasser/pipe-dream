namespace PipeDream;

/// <summary>
/// Lunar Magic's ExAnimation data (CONTRACT §12e/§12f, reference/EXANIMATION.md), read-only.
/// The per-level table (<see cref="Rom.LmExAnimBase"/>, 3 bytes/level, FF 00 00 = none) and
/// the global list (<see cref="Rom.LmGlobalExAnimPtr"/>) point at RECORDS of one shape:
///
///   +0 word   low byte = slot-entry count, high byte = alt-ExGFX file index (0-3 → files 60-63,
///             pointer table $03BCC0)
///   +2 word   AND mask, +4 word OR mask (→ $7FC0FC, DMA enable)
///   +6 word   16-bit selector; one trailing byte per set bit (→ $7FC070 manual-frame inits)
///   section   `count` 16-bit offsets, one per slot number, relative to the section start
///             (0x0000 = slot unused), then the packed slot blocks:
///
///   slot+0 byte  type      (1..0x0E = that many 8x8s in a line; 0x0F = 1 8x8 2bpp; 0x10 = 2
///                          stacked; 0x11 = 4 as 16x16; 0x12 = 8 as 32x16 [assumed]; 0x13 = palette)
///   slot+1 byte  trigger   (0 none, 1 POW, 3 ON/OFF, 0x10+n Manual n, 0x30+n One-Shot n;
///                          the rest of LM's list sits in between and is not yet pinned)
///   slot+2 byte  frames-1
///   slot+3 word  destination: VRAM word (tile = word/16) or palette colour index; bit 15 =
///                          "use alternate ExGFX file" — then frame words are BYTE OFFSETS into
///                          that file instead of $7E RAM addresses
///   slot+5 ..    frame words — `frames` of them, or 2×frames for a stateful trigger (the second
///                          half is the triggered animation; LM zero-fills what you don't set)
///
/// RAM-source frame word = $7D00 + (LM tile − 0x600) × 0x20 (AN1 0x600-, AN2 0x780- at $AD00,
/// Mario 0x900- at $2000). Pinned by the exanim_a..n controlled saves, 2026-08-29.
/// </summary>
public sealed class ExAnimation
{
    public const int Type2bpp = 0x0F, TypeStacked = 0x10, Type16x16 = 0x11, Type32x16 = 0x12, TypePalette = 0x13,
                     TypePaletteRotateRight = 0x18, TypePaletteRotateLeftReverse = 0x1B;
    /// <summary>Tiles per frame for the line codes 01-0E: 1..8, then 12, 16, 20, 24, 28, 32 (LM's engine,
    /// --exanimtypes: ctrl = tiles × 0x20).</summary>
    public static readonly int[] LineTiles = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 28, 32];
    public const int TriggerNone = 0, TriggerPow = 1, TriggerSilverPow = 2, TriggerOnOff = 3, TriggerStar = 4,
                     TriggerManual0 = 0x10, TriggerCustom0 = 0x20, TriggerOneShot0 = 0x30;

    /// <summary>Stateful triggers store 2×frames words (second half = triggered): the standard game
    /// events 01-0F and the Custom flags 20-2F; Manual and One-Shot do not.</summary>
    public static bool TriggerDoubles(int trigger) => trigger is (> 0 and < TriggerManual0) or (>= TriggerCustom0 and < TriggerOneShot0);

    /// <summary>
    /// The record's dest word is a raw VRAM word address (pinned by exanim_4; the engine passes
    /// it through unchanged for every type — oracle-verified). LM's dialog numbers destinations
    /// by the 8x8 editor instead: layer 1/2 tiles 000-3FF at word $0000 (word/16, BG12NBA=0),
    /// sprite tiles 400-5FF at $6000 (OBSEL=3), layer-3 2bpp tiles 1C00-1DFF at $4000
    /// (BG34NBA=4, 8 words per 2bpp tile) — vanilla's CHR bases, which LM keeps. Words outside
    /// those regions fall back to word/16 (LM's advanced raw-offset entry).
    /// </summary>
    public static int WordToLmTile(int word)
    {
        int w = word & 0x7FFF;
        return w >= 0x6000 ? 0x400 + (w - 0x6000) / 0x10
             : w is >= 0x4000 and < 0x5000 ? 0x1C00 + (w - 0x4000) / 8
             : w >> 4;
    }

    /// <summary>Inverse of <see cref="WordToLmTile"/>: LM dest tile number → raw VRAM word.</summary>
    public static int LmTileToWord(int tile) => tile switch
    {
        >= 0x1C00 and < 0x1E00 => 0x4000 + (tile - 0x1C00) * 8,
        >= 0x400 and < 0x600 => 0x6000 + (tile - 0x400) * 0x10,
        _ => (tile & 0x3FF) << 4,
    };
    /// <summary>Palette rotation types carry no frame data — the frame count is a delay.</summary>
    public static bool HasFrameWords(int type) => type < TypePaletteRotateRight;

    /// <summary>One slot, as the record stores it. <paramref name="Frames"/> is the raw word list
    /// (already doubled for a stateful trigger); <paramref name="AltFileIndex"/> is the record's,
    /// meaningful only when <see cref="AltFile"/>.</summary>
    public readonly record struct Slot(int Index, int Type, int Trigger, int FrameCount, int DestWord,
                                       ushort[] Frames, int AltFileIndex)
    {
        public bool AltFile => (DestWord & 0x8000) != 0;
        public bool IsPalette => Type >= TypePalette;
        /// <summary>Destination in LM's dest numbering (see <see cref="WordToLmTile"/>);
        /// the colour index for palette types.</summary>
        public int DestTile => WordToLmTile(DestWord);
        /// <summary>Palette types: first colour index (low byte) and how many (high byte + 1).</summary>
        public int DestColor => DestWord & 0xFF;
        public int Colors => ((DestWord >> 8) & 0x7F) + 1;
        /// <summary>8x8 tiles moved per frame.</summary>
        public int TileCount => Type switch
        {
            >= 1 and < Type2bpp => LineTiles[Type], Type2bpp => 1, TypeStacked => 2, Type16x16 => 4, Type32x16 => 8, _ => 0,
        };
        /// <summary>Destination tile of the k-th source tile: a line, or the block shapes (top row first).</summary>
        public int DestTileAt(int k) => Type switch
        {
            TypeStacked => DestTile + k * 0x10,
            Type16x16 => DestTile + (k & 1) + (k >> 1) * 0x10,
            Type32x16 => DestTile + (k & 3) + (k >> 2) * 0x10,
            _ => DestTile + k,
        };
        /// <summary>Whether the trigger keeps a state and so doubles the frame list.</summary>
        public bool Doubled => TriggerDoubles(Trigger);
        /// <summary>The frame word for display: the untriggered half.</summary>
        public int Frame(int phase) => Frames[phase % FrameCount];
        /// <summary>Frame's source in LM's tile numbering (RAM sources 0x600-based; alt file
        /// 0xC00 + 0x400×file).</summary>
        public int SrcTile(int f) => AltFile ? 0xC00 + AltFileIndex * 0x400 + Frames[f] / 0x20
                                             : (Frames[f] - 0x7D00) / 0x20 + 0x600;
        /// <summary>Alias kept for the RAM path: the $7E address the engine DMAs from.</summary>
        public ushort[] FrameSrcAddrs => Frames;

        /// <summary>Every decoded field on one line: frames as LM tile numbers with the raw word
        /// beside them — a $7E address, or a byte offset into the alternate file.</summary>
        public string Describe()
        {
            var s = this;
            var frames = string.Join(" ", Enumerable.Range(0, Frames.Length)
                .Select(f => s.IsPalette ? $"{s.Frames[f]:X4}" : $"{s.SrcTile(f):X3}(${s.Frames[f]:X4})"));
            string dest = IsPalette ? $"color {DestColor:X2} x{Colors}" : $"destTile {DestTile:X3} (word ${DestWord:X4})";
            string alt = AltFile ? $"  altfile {0x60 + AltFileIndex:X2}" : "";
            return $"slot {Index,2}: type {Type:X2} trigger {Trigger:X2}  {dest}  {FrameCount}{(Doubled ? " x2" : "")} frames: {frames}{alt}";
        }
    }

    /// <summary>Slots animated in <paramref name="level"/>; empty if the level has none.</summary>
    public static IReadOnlyList<Slot> ReadLevel(Rom rom, int level)
    {
        int baseSnes = rom.LmExAnimBase;                  // per-ROM; -1 = no ExAnimation ASM
        if (baseSnes < 0) return [];
        int ptr = rom.ReadValue(baseSnes + level * 3, 3);
        if ((ptr >> 16) == 0) return [];                 // FF 00 00 sentinel / not set
        return ReadRecord(rom, ptr);
    }

    /// <summary>The global list's slots (runs in every level); empty when the ROM has none.</summary>
    public static List<Slot> ReadGlobal(Rom rom)
        => rom.LmGlobalExAnimPtr < 0 ? [] : ReadRecord(rom, rom.LmGlobalExAnimPtr);

    /// <summary>A record at a SNES address, bounded by its RATS block when it has one so the
    /// last slot cannot bleed into the next block, and by the ROM otherwise (the overlay runs
    /// on every ROM, a stray pointer must not overrun).</summary>
    private static List<Slot> ReadRecord(Rom rom, int snes)
    {
        int fo = rom.FileOffset(snes);
        if (fo < 0 || fo + 8 > rom.Data.Length) return [];
        int n = Math.Min(0x1000, rom.Data.Length - fo);
        byte[] d = rom.Data;
        if (fo >= 8 && d[fo - 8] == 'S' && d[fo - 7] == 'T' && d[fo - 6] == 'A' && d[fo - 5] == 'R')
            n = Math.Min(n, (d[fo - 4] | d[fo - 3] << 8) + 1);
        return ParseSlots(new ReadOnlySpan<byte>(d, fo, n));
    }

    /// <summary>
    /// The inverse of <see cref="ParseSlots"/>: a record LM reads back as these slots. Header masks
    /// are the FF FF / 00 00 LM writes for a plain list, selector 0; the offset table spans up to the
    /// highest used slot number; blocks are packed in slot order. A slot's frame list is written as
    /// stored (already doubled for a stateful trigger; padded/truncated to the count the format
    /// implies, so a caller can hand over just the untriggered half).
    /// </summary>
    public static byte[] Encode(IEnumerable<Slot> slots, int altFileIndex = 0)
    {
        var list = slots.OrderBy(s => s.Index).ToList();
        int count = list.Count == 0 ? 0 : list[^1].Index + 1;
        var rec = new List<byte> { (byte)count, (byte)(altFileIndex & 3), 0xFF, 0xFF, 0, 0, 0, 0 };
        var table = new byte[count * 2];
        var blocks = new List<byte>();
        foreach (var s in list)
        {
            int off = table.Length + blocks.Count;
            table[s.Index * 2] = (byte)off; table[s.Index * 2 + 1] = (byte)(off >> 8);
            blocks.Add((byte)s.Type); blocks.Add((byte)s.Trigger); blocks.Add((byte)(s.FrameCount - 1));
            blocks.Add((byte)s.DestWord); blocks.Add((byte)(s.DestWord >> 8));
            int words = HasFrameWords(s.Type) ? s.FrameCount * (TriggerDoubles(s.Trigger) ? 2 : 1) : 0;
            for (int f = 0; f < words; f++)
            {
                ushort w = f < s.Frames.Length ? s.Frames[f] : (ushort)0;
                blocks.Add((byte)w); blocks.Add((byte)(w >> 8));
            }
        }
        rec.AddRange(table);
        rec.AddRange(blocks);
        return [.. rec];
    }

    /// <summary>Parse a record's slots (pure — unit-testable). Slots come back in slot-number order.</summary>
    public static List<Slot> ParseSlots(ReadOnlySpan<byte> rec)
    {
        var slots = new List<Slot>();
        if (rec.Length < 8) return slots;
        int count = rec[0], fileIndex = rec[1];
        int selector = rec[6] | rec[7] << 8;
        int table = 8 + System.Numerics.BitOperations.PopCount((uint)selector);
        for (int i = 0; i < count && table + i * 2 + 2 <= rec.Length; i++)
        {
            int off = rec[table + i * 2] | rec[table + i * 2 + 1] << 8;
            if (off == 0) continue;
            int p = table + off;
            if (p + 5 > rec.Length) break;
            int type = rec[p], trigger = rec[p + 1], frameCount = rec[p + 2] + 1;
            int destWord = rec[p + 3] | rec[p + 4] << 8;
            int want = HasFrameWords(type) ? frameCount * (TriggerDoubles(trigger) ? 2 : 1) : 0;
            int words = Math.Min(want, (rec.Length - (p + 5)) / 2);       // truncated record: keep what is there
            if (words < Math.Min(want, frameCount)) break;
            var frames = new ushort[words];
            for (int f = 0; f < words; f++)
                frames[f] = (ushort)(rec[p + 5 + f * 2] | rec[p + 6 + f * 2] << 8);
            slots.Add(new Slot(i, type, trigger, frameCount, destWord, frames, fileIndex));
        }
        return slots;
    }


    /// <summary>
    /// Resolve the global list by EMULATING LM's own engine (CONTRACT §12f): run the setup +
    /// processor under Cpu65816 for a given animation phase and read back the eight stride-7 DMA
    /// records LM builds at $7FC0C0. Returns each non-empty record's 7 raw bytes (record i at
    /// $7FC0C0 + i*7). Format-agnostic — no per-slot handler decode needed. Empty if the ROM has
    /// no global list or the engine can't be located.
    /// </summary>
    /// <summary>One resolved global-ExAnimation DMA job: at frame <see cref="Frame"/> LM's engine
    /// writes the GFX at <see cref="SrcSnes"/> (a ROM address) into FG <see cref="DestTile"/>.
    /// <see cref="Ctrl"/> 0 = the tile isn't rewritten that frame (keeps its prior source).</summary>
    public readonly record struct GlobalFrame(int Frame, int Slot, int Ctrl, int DestTile, int SrcSnes);

    /// <summary>
    /// Resolve the global list by EMULATING LM's own engine (CONTRACT §12f): run the setup once,
    /// then the processor for <paramref name="frames"/> consecutive ticks under Cpu65816, reading
    /// the eight stride-7 DMA records LM builds at $7FC0C0 (+0 ctrl, +2 VRAM dest word, +4 3-byte
    /// ROM source). Format-agnostic - no per-slot handler decode. Returns the per-frame timeline.
    /// $FE = <paramref name="level"/> + 1 (16-bit): setup walks that level's list, then the global
    /// one; 0 would set the skip bit. Level 0 has no list, so the default is the global timeline.
    /// </summary>
    public static List<GlobalFrame> ResolveGlobal(Rom rom, int frames, int level = 0)
    {
        var result = new List<GlobalFrame>();
        int setup = rom.LmExAnimSetupEntry, proc = rom.LmExAnimProcEntry;
        if (setup < 0 || proc < 0 || (rom.LmGlobalExAnimPtr < 0 && level <= 0)) return result;

        var cpu = new Cpu65816(rom);
        cpu.Ram7E[0xFE] = (byte)(level + 1); cpu.Ram7E[0xFF] = (byte)((level + 1) >> 8);
        try
        {
            cpu.CallLong(setup);                    // populate the $7FC0xx control block once
            for (int f = 0; f < frames; f++)
            {
                cpu.Ram7E[0x14] = (byte)f;          // advance the frame counter each tick
                cpu.CallLong(proc);                 // fills $7FC0C0 for slots whose timer fires now
                for (int i = 0; i < 8; i++)
                {
                    int b = 0xC0C0 + i * 7;
                    int ctrl = cpu.Ram7F[b] | cpu.Ram7F[b + 1] << 8;
                    int dest = cpu.Ram7F[b + 2] | cpu.Ram7F[b + 3] << 8;
                    int src = cpu.Ram7F[b + 4] | cpu.Ram7F[b + 5] << 8 | cpu.Ram7F[b + 6] << 16;
                    if (dest == 0 && src == 0) continue;
                    result.Add(new GlobalFrame(f, i, ctrl, dest >> 4, src));
                }
            }
        }
        catch { /* seeding gap / overrun - return whatever was collected */ }
        return result;
    }

    /// <summary>Current source for an animated dest tile at one display phase.</summary>
    public readonly record struct TileAnim(int SrcSnes, int TileCount);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Rom,
        Dictionary<int, TileAnim>[]> globalStateCache = new();

    /// <summary>
    /// Global ExAnimation as 4 display-phase snapshots (CONTRACT §12f), cached per ROM. Each
    /// snapshot maps a dest FG 8x8 tile → its current ROM GFX source for that phase. Built by
    /// emulating the engine (<see cref="ResolveGlobal"/>) across 8 frames per phase and carrying
    /// each tile's last source forward (the engine spreads its VRAM writes over several frames,
    /// so a tile keeps its source on the frames it isn't rewritten). ctrl = DMA byte count, so a
    /// record covers ctrl/0x20 consecutive tiles. Empty array if the ROM has no global list.
    /// </summary>
    /// <summary>Drop the cached phase snapshots after the global list was rewritten.</summary>
    public static void InvalidateGlobal(Rom rom) => globalStateCache.Remove(rom);

    public static Dictionary<int, TileAnim>[] GlobalStates(Rom rom)
    {
        if (globalStateCache.TryGetValue(rom, out var cached)) return cached;
        const int phases = 4, perPhase = 8;
        var states = new Dictionary<int, TileAnim>[phases];
        var timeline = ResolveGlobal(rom, phases * perPhase);
        var cur = new Dictionary<int, TileAnim>();
        int fi = 0;
        for (int p = 0; p < phases; p++)
        {
            for (; fi < timeline.Count && timeline[fi].Frame < (p + 1) * perPhase; fi++)
            {
                var g = timeline[fi];
                if (g.Ctrl == 0) continue;                 // tile not rewritten this frame
                cur[g.DestTile] = new TileAnim(g.SrcSnes, Math.Max(1, g.Ctrl / 0x20));
            }
            states[p] = new Dictionary<int, TileAnim>(cur); // snapshot the accumulated state
        }
        // AddOrUpdate: RebuildGraphics calls this from Parallel.For, so two threads can
        // miss TryGetValue together — a duplicate compute is fine, a duplicate Add throws.
        globalStateCache.AddOrUpdate(rom, states);
        return states;
    }

}
