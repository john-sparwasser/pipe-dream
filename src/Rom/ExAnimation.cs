namespace PipeDream;

/// <summary>
/// LM ExAnimation per-level slot data (CONTRACT §12e), read-only. The per-level table at
/// <see cref="TableSnes"/> holds one 24-bit record pointer per level (sentinel FF 00 00 =
/// none). The record is an 8-byte header (§12e: slot count + flag masks consumed by the
/// $108700 level-setup routine) followed by a packed slot array at record+8.
///
/// Per-slot layout (slot-relative), confirmed by the exanim_0..4 controlled diffs:
///   +0 word   ? (0x0002 observed)
///   +2 word   ? (0x0001 observed)
///   +4 byte   frameCount - 1
///   +5 word   destination VRAM word = dialog value * 0x10  (FG tile = word / 16 = dialog
///             value; same word/16 convention as vanilla animation, CONTRACT §12)
///   +7 ..     frame list, one 16-bit $7E RAM source address per frame
/// Frame source tile (LM's 0x600-based numbering): addr = $7D00 + (tile - 0x600) * 0x20.
/// </summary>
public sealed class ExAnimation
{
    private const int SlotArrayOffset = 8;    // record+8 = slot array ($7FC000 setup, §12e)

    public readonly record struct Slot(int Unknown0, int Unknown2, int FrameCount, int DestWord, ushort[] FrameSrcAddrs)
    {
        /// <summary>Destination FG 8x8 tile (VRAM word / 16).</summary>
        public int DestTile => DestWord >> 4;
        /// <summary>Frame's source tile in LM's 0x600-based numbering.</summary>
        public int SrcTile(int frame) => (FrameSrcAddrs[frame] - 0x7D00) / 0x20 + 0x600;
    }

    /// <summary>Slots animated in <paramref name="level"/>; empty if the level has none.</summary>
    public static IReadOnlyList<Slot> ReadLevel(Rom rom, int level)
    {
        int baseSnes = rom.LmExAnimBase;                  // per-ROM; -1 = no ExAnimation ASM
        if (baseSnes < 0) return [];
        int ptr = rom.ReadValue(baseSnes + level * 3, 3);
        if ((ptr >> 16) == 0) return [];                 // FF 00 00 sentinel / not set

        // Records are small; a 512-byte window covers any realistic slot array. Clamp to the
        // ROM so a stray/garbage pointer can't overrun (the overlay runs on every ROM).
        int fo = rom.FileOffset(ptr);
        if (fo < 0 || fo >= rom.Data.Length) return [];
        int n = Math.Min(512, rom.Data.Length - fo);
        var rec = new byte[n];
        Array.Copy(rom.Data, fo, rec, 0, n);
        return ParseSlots(rec);
    }

    /// <summary>
    /// A global-list slot (§12f): index + raw definition bytes. The block is a 7-byte header
    /// (fields still type-dependent, undecoded) followed by <see cref="FrameCount"/> 16-bit
    /// frame words. Frame words use the same 0x600-based tile numbering as the per-level slots
    /// (§12e): src addr = $7D00 + (tile - 0x600) * 0x20; tile 0x780 → $AD00 (custom source).
    /// </summary>
    public readonly record struct GlobalSlot(int Index, byte[] Raw)
    {
        public const int HeaderLen = 7;
        public int FrameCount => Raw.Length >= HeaderLen ? (Raw.Length - HeaderLen) / 2 : 0;
        public int FrameTile(int f) => Raw[HeaderLen + f * 2] | Raw[HeaderLen + f * 2 + 1] << 8;
        public int FrameSrcAddr(int f) => 0x7D00 + (FrameTile(f) - 0x600) * 0x20;
    }

    /// <summary>
    /// Global ExAnimation slots (CONTRACT §12f) — used by real hacks (per-level table empty).
    /// The record outer form is read straight from the engine ($13805A on ShaoBase):
    ///   +0 word   low byte = slot count; high byte = index into $03BCC0 (stride 3)
    ///   +2 word   AND mask into $7FC0FC (DMA-enable)
    ///   +4 word   OR  mask into $7FC0FC
    ///   +6 word   16-bit selector; one trailing byte per set bit → $7FC070,X (popcount bytes)
    ///   then      slot section: `count` 16-bit offsets (relative to the section start,
    ///             0x0000 = unused slot), followed by each slot's definition block.
    /// Per-slot internals are NOT decoded here — the frame encoding differs from the per-level
    /// form and is interpreted by ~12 type handlers ($10F32D dispatch); this returns the raw
    /// block per used slot so the format can be cracked from real data (§12f REMAINING #1).
    /// </summary>
    public static List<GlobalSlot> ReadGlobalRaw(Rom rom)
    {
        var result = new List<GlobalSlot>();
        int ptr = rom.LmGlobalExAnimPtr;
        if (ptr < 0) return result;
        int fo = rom.FileOffset(ptr);
        if (fo < 0 || fo + 8 > rom.Data.Length) return result;
        byte[] d = rom.Data;

        int count = d[fo];                                    // +0 low byte
        int selector = d[fo + 6] | d[fo + 7] << 8;            // +6 selector word
        int extra = System.Numerics.BitOperations.PopCount((uint)selector);
        int table = fo + 8 + extra;                           // slot section start ($7FC016 base)
        if (table + count * 2 > d.Length) return result;

        // The record lives in a RATS block ("STAR" + (size-1) little-endian at fo-8); use that
        // to bound the last slot so it can't bleed into the next block. Fall back to a 13-byte
        // cap (the largest real slot) if there's no RATS header.
        int recEnd = fo + Math.Min(512, d.Length - fo);
        if (fo >= 8 && d[fo - 8] == 'S' && d[fo - 7] == 'T' && d[fo - 6] == 'A' && d[fo - 5] == 'R')
            recEnd = Math.Min(recEnd, fo + (d[fo - 4] | d[fo - 3] << 8) + 1);

        // Gather (index, offset) for used slots, then size each block from the next-larger
        // offset (blocks are packed after the offset table; last one runs to the record end).
        var used = new List<(int idx, int off)>();
        for (int i = 0; i < count; i++)
        {
            int off = d[table + i * 2] | d[table + i * 2 + 1] << 8;
            if (off != 0) used.Add((i, off));
        }
        used.Sort((a, b) => a.off.CompareTo(b.off));
        for (int k = 0; k < used.Count; k++)
        {
            int start = table + used[k].off;
            int nextStart = k + 1 < used.Count ? table + used[k + 1].off : recEnd;
            int len = Math.Min(Math.Min(nextStart, recEnd) - start, 13);
            if (len <= 0) continue;
            var raw = new byte[len];
            Array.Copy(d, start, raw, 0, len);
            result.Add(new GlobalSlot(used[k].idx, raw));
        }
        return result;
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
    /// $FE (per-level index) is seeded 1 so setup takes the global path (0 sets the skip bit).
    /// </summary>
    public static List<GlobalFrame> ResolveGlobal(Rom rom, int frames)
    {
        var result = new List<GlobalFrame>();
        int setup = rom.LmExAnimSetupEntry, proc = rom.LmExAnimProcEntry;
        if (setup < 0 || proc < 0 || rom.LmGlobalExAnimPtr < 0) return result;

        var cpu = new Cpu65816(rom);
        cpu.Ram7E[0xFE] = 1;                        // nonzero -> setup runs the global path
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

    /// <summary>Parse the slot array out of a record's raw bytes (pure — unit-testable).</summary>
    public static List<Slot> ParseSlots(ReadOnlySpan<byte> rec)
    {
        var slots = new List<Slot>();
        int count = rec[0];                              // record+0 low byte = slot count
        int p = SlotArrayOffset;
        for (int i = 0; i < count && p + 7 <= rec.Length; i++)
        {
            int u0 = rec[p] | rec[p + 1] << 8;
            int u2 = rec[p + 2] | rec[p + 3] << 8;
            int frameCount = rec[p + 4] + 1;                  // +4 byte
            int destWord = rec[p + 5] | rec[p + 6] << 8;      // +5 word
            if (p + 7 + frameCount * 2 > rec.Length) break;   // truncated / bad count
            var src = new ushort[frameCount];
            for (int f = 0; f < frameCount; f++)
                src[f] = (ushort)(rec[p + 7 + f * 2] | rec[p + 8 + f * 2] << 8);
            slots.Add(new Slot(u0, u2, frameCount, destWord, src));
            p += 7 + frameCount * 2;
        }
        return slots;
    }
}
