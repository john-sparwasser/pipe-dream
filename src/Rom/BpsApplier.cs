namespace PipeDream;

/// <summary>
/// BPS patch application — the full spec (all four actions), not just what BpsWriter
/// emits, so foreign patches apply too. Verifies all three footer CRCs; throws
/// InvalidDataException with a specific message on any mismatch.
/// </summary>
public static class BpsApplier
{
    public static byte[] Apply(byte[] source, byte[] patch)
    {
        if (patch.Length < 4 + 12 || patch[0] != 'B' || patch[1] != 'P' || patch[2] != 'S' || patch[3] != '1')
            throw new InvalidDataException("not a BPS patch");
        uint patchCrcStored = ReadU32(patch, patch.Length - 4);
        if (Crc32.Compute(patch.AsSpan(0, patch.Length - 4)) != patchCrcStored)
            throw new InvalidDataException("BPS patch is corrupt (patch CRC mismatch)");

        int pos = 4;
        long sourceSize = ReadVarint(patch, ref pos);
        long targetSize = ReadVarint(patch, ref pos);
        long metaSize = ReadVarint(patch, ref pos);
        pos += (int)metaSize;

        if (source.Length != sourceSize)
            throw new InvalidDataException($"source size mismatch (patch expects {sourceSize} bytes, got {source.Length})");
        if (Crc32.Compute(source) != ReadU32(patch, patch.Length - 12))
            throw new InvalidDataException("source ROM does not match the one this patch was made from (CRC mismatch)");

        var target = new byte[targetSize];
        int outOff = 0, srcRel = 0, tgtRel = 0;
        int end = patch.Length - 12;
        while (pos < end)
        {
            long data = ReadVarint(patch, ref pos);
            int action = (int)(data & 3);
            int length = (int)(data >> 2) + 1;
            switch (action)
            {
                case 0:                                       // SourceRead
                    Array.Copy(source, outOff, target, outOff, length);
                    outOff += length;
                    break;
                case 1:                                       // TargetRead
                    Array.Copy(patch, pos, target, outOff, length);
                    pos += length; outOff += length;
                    break;
                case 2:                                       // SourceCopy
                {
                    long d = ReadVarint(patch, ref pos);
                    srcRel += (int)((d & 1) != 0 ? -(d >> 1) : d >> 1);
                    Array.Copy(source, srcRel, target, outOff, length);
                    srcRel += length; outOff += length;
                    break;
                }
                case 3:                                       // TargetCopy (may overlap — byte-wise)
                {
                    long d = ReadVarint(patch, ref pos);
                    tgtRel += (int)((d & 1) != 0 ? -(d >> 1) : d >> 1);
                    for (int k = 0; k < length; k++) target[outOff++] = target[tgtRel++];
                    break;
                }
            }
        }

        if (Crc32.Compute(target) != ReadU32(patch, patch.Length - 8))
            throw new InvalidDataException("patched output failed verification (target CRC mismatch)");
        return target;
    }

    private static long ReadVarint(byte[] p, ref int pos)
    {
        long data = 0, shift = 1;
        while (true)
        {
            byte x = p[pos++];
            data += (x & 0x7F) * shift;
            if ((x & 0x80) != 0) return data;
            shift <<= 7;
            data += shift;
        }
    }

    private static uint ReadU32(byte[] p, int at) =>
        (uint)(p[at] | (p[at + 1] << 8) | (p[at + 2] << 16) | (p[at + 3] << 24));
}
