namespace PipeDream;

/// <summary>
/// LM ExAnimation per-level slot data (CONTRACT §12e), read-only. The per-level table at
/// <see cref="TableSnes"/> holds one 24-bit record pointer per level (sentinel FF 00 00 =
/// none). The record is an 8-byte header (§12e: slot count + flag masks consumed by the
/// $108700 level-setup routine) followed by a packed slot array at record+8.
///
/// Per-slot layout (slot-relative), confirmed by the exanim_0..3 controlled diffs:
///   +0 word   ? (0x0002 observed)
///   +2 word   ? (0x0001 observed)
///   +4 word   frameCount - 1
///   +6 byte   destination (dialog value / 0x10 for the two tested multiples of 16 — the
///             exact tile mapping is NOT yet pinned; a non-16-aligned dest test is needed
///             before this drives a render overlay)
///   +7 ..     frame list, one 16-bit $7E RAM source address per frame
/// Frame source tile (LM's 0x600-based numbering): addr = $7D00 + (tile - 0x600) * 0x20.
/// </summary>
public sealed class ExAnimation
{
    public const int TableSnes = 0x109278;   // verified on the exanim_* family; may be per-ROM
    private const int SlotArrayOffset = 8;    // record+8 = slot array ($7FC000 setup, §12e)

    public readonly record struct Slot(int Unknown0, int Unknown2, int FrameCount, int DestByte, ushort[] FrameSrcAddrs)
    {
        /// <summary>Frame's source tile in LM's 0x600-based numbering.</summary>
        public int SrcTile(int frame) => (FrameSrcAddrs[frame] - 0x7D00) / 0x20 + 0x600;
    }

    /// <summary>Slots animated in <paramref name="level"/>; empty if the level has none.</summary>
    public static IReadOnlyList<Slot> ReadLevel(Rom rom, int level)
    {
        int ptr = rom.ReadValue(TableSnes + level * 3, 3);
        if ((ptr >> 16) == 0) return [];                 // FF 00 00 sentinel / not set

        // Records are small; a 512-byte window covers any realistic slot array.
        var rec = new byte[512];
        for (int k = 0; k < rec.Length; k++) rec[k] = rom.ReadByte(ptr + k);
        return ParseSlots(rec);
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
            int frameCount = (rec[p + 4] | rec[p + 5] << 8) + 1;
            int dest = rec[p + 6];
            if (p + 7 + frameCount * 2 > rec.Length) break;   // truncated / bad count
            var src = new ushort[frameCount];
            for (int f = 0; f < frameCount; f++)
                src[f] = (ushort)(rec[p + 7 + f * 2] | rec[p + 8 + f * 2] << 8);
            slots.Add(new Slot(u0, u2, frameCount, dest, src));
            p += 7 + frameCount * 2;
        }
        return slots;
    }
}
