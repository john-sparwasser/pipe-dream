namespace PipeDream;

/// <summary>
/// BPS patch creation (byuu's beat format): header "BPS1", varint source/target/metadata
/// sizes, command stream, then CRC32 footer (source, target, patch). The encoder is
/// deliberately simple — SourceRead for bytes unchanged at the same offset, TargetRead
/// for raw new bytes, plus self-overlapping TargetCopy as RLE for periodic runs (byte/
/// word/dword fills — what ROM expansion regions are made of). No general SourceCopy/
/// TargetCopy delta matching. ponytail: full delta matching if patch size ever matters
/// beyond fills.
/// </summary>
public static class BpsWriter
{
    public static byte[] Create(byte[] source, byte[] target)
    {
        var p = new List<byte>(1024) { (byte)'B', (byte)'P', (byte)'S', (byte)'1' };
        WriteVarint(p, (ulong)source.Length);
        WriteVarint(p, (ulong)target.Length);
        WriteVarint(p, 0);                                   // no metadata

        int i = 0, tgtRel = 0;
        while (i < target.Length)
        {
            // Length of the unchanged-at-same-offset run vs the changed run from here.
            int same = 0;
            while (i + same < target.Length && i + same < source.Length &&
                   target[i + same] == source[i + same]) same++;
            // A tiny unchanged run costs more as a command than inlining it — 4 bytes is
            // roughly the break-even against TargetRead's raw bytes.
            if (same >= 4 || (same > 0 && i + same == target.Length))
            {
                WriteVarint(p, ((ulong)(same - 1) << 2) | 0);         // SourceRead
                i += same;
                continue;
            }
            // Periodic run (RLE): a self-overlapping TargetCopy replays a repeating
            // byte/word/dword pattern from a tiny seed — this is what keeps expansion
            // regions (zeros, 0x1004-word fills, 0x0130 acts fill) out of the patch.
            int run = RunLength(target, i, out int period);
            if (run >= 16)
            {
                WriteVarint(p, ((ulong)(period - 1) << 2) | 1);       // TargetRead: the seed
                for (int k = 0; k < period; k++) p.Add(target[i + k]);
                int d = i - tgtRel;                                    // copy-from = seed start
                WriteVarint(p, ((ulong)(run - period - 1) << 2) | 3); // TargetCopy, overlapping
                WriteVarint(p, d < 0 ? (((ulong)-d) << 1) | 1 : ((ulong)d) << 1);
                tgtRel = i + run - period;
                i += run;
                continue;
            }
            int diff = same;                                  // absorb the tiny same-run
            while (i + diff < target.Length &&
                   !(i + diff < source.Length && Matches4(source, target, i + diff)) &&
                   !(RunLength(target, i + diff, out _) >= 16))
                diff++;
            WriteVarint(p, ((ulong)(diff - 1) << 2) | 1);             // TargetRead
            for (int k = 0; k < diff; k++) p.Add(target[i + k]);
            i += diff;
        }

        WriteU32(p, Crc32.Compute(source));
        WriteU32(p, Crc32.Compute(target));
        WriteU32(p, Crc32.Compute(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(p)));
        return p.ToArray();
    }

    // Longest run at `at` where the data repeats with a small period (1, 2, or 4 bytes) —
    // the shortest period that reaches 16+ wins so the seed stays minimal.
    private static int RunLength(byte[] target, int at, out int period)
    {
        foreach (int per in stackalloc int[] { 1, 2, 4 })
        {
            if (at + per >= target.Length) break;
            int j = at + per;
            while (j < target.Length && target[j] == target[j - per]) j++;
            if (j - at >= 16) { period = per; return j - at; }
        }
        period = 0;
        return 0;
    }

    // A same-offset match of 4+ bytes starts here — worth ending the TargetRead run for.
    private static bool Matches4(byte[] source, byte[] target, int at)
    {
        if (target[at] != source[at]) return false;
        for (int k = 1; k < 4; k++)
            if (at + k >= target.Length || at + k >= source.Length || target[at + k] != source[at + k])
                return at + k == target.Length;               // matching straight to EOF also counts
        return true;
    }

    internal static void WriteVarint(List<byte> p, ulong data)
    {
        while (true)
        {
            byte x = (byte)(data & 0x7F);
            data >>= 7;
            if (data == 0) { p.Add((byte)(0x80 | x)); return; }
            p.Add(x);
            data--;
        }
    }

    private static void WriteU32(List<byte> p, uint v)
    {
        p.Add((byte)v); p.Add((byte)(v >> 8)); p.Add((byte)(v >> 16)); p.Add((byte)(v >> 24));
    }
}
