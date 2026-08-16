namespace PipeDream;

/// <summary>
/// BPS patch creation (byuu's beat format): header "BPS1", varint source/target/metadata
/// sizes, command stream, then CRC32 footer (source, target, patch). The encoder is
/// deliberately simple — alternating SourceRead (bytes unchanged at the same offset) and
/// TargetRead (raw new bytes) runs. No SourceCopy/TargetCopy matching: ROM edits are
/// mostly in-place or appended, so same-offset runs already compress those to nothing.
/// ponytail: delta matching (actions 2/3) if patch size ever matters.
/// </summary>
public static class BpsWriter
{
    public static byte[] Create(byte[] source, byte[] target)
    {
        var p = new List<byte>(1024) { (byte)'B', (byte)'P', (byte)'S', (byte)'1' };
        WriteVarint(p, (ulong)source.Length);
        WriteVarint(p, (ulong)target.Length);
        WriteVarint(p, 0);                                   // no metadata

        int i = 0;
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
            int diff = same;                                  // absorb the tiny same-run
            while (i + diff < target.Length &&
                   !(i + diff < source.Length && Matches4(source, target, i + diff)))
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
