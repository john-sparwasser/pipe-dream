namespace PipeDream;

/// <summary>
/// Rom — SAVING / the write path (CONTRACT §2).
///
/// New or relocated data (edited levels, custom palettes, Map16) is appended to the
/// expanded area of the ROM (PC ≥ 0x80000) inside a RATS block, and a pointer table entry
/// is repointed at it. This partial owns that machinery:
///
///   • RATS ("STAR" tag) — Lunar Magic's free-space protocol: an 8-byte tag ("STAR" +
///     size-1 + its inverse) precedes each protected block, so tools can walk the expanded
///     area and skip regions already in use. The inverse check is REQUIRED — random data
///     contains the ASCII bytes "STAR" often enough that the size/inverse pair is the only
///     reliable validity signal.
///   • ExpandTo — grow the file and update the $FFD7 size code.
///   • AllocateRats — find free space, write the tag, copy the payload, return the SNES
///     address of the payload (just past the tag).
///   • FixChecksum / SaveAs — recompute $FFDC/$FFDE and write the file.
///
/// Level object/sprite ENCODING (bytes → stream) lives in Level.Encode / SpriteData.Encode;
/// this partial only places the encoded bytes and repoints the table.
/// </summary>
public sealed partial class Rom
{
    public readonly record struct Rat(int PcOffset, int Size);

    /// <summary>
    /// Enumerate valid RATS-protected regions in the expanded area (pc ≥ 0x80000).
    /// A tag is valid only when (size-1) XOR (~(size-1)) == 0xFFFF — required, because
    /// random data contains the ASCII bytes "STAR".
    /// </summary>
    public IEnumerable<Rat> EnumerateRats()
    {
        int end = Data.Length - 8;
        for (int pc = 0x80000; pc <= end - HeaderOffset; )
        {
            int fo = pc + HeaderOffset;
            if (Data[fo] == 0x53 && Data[fo + 1] == 0x54 && Data[fo + 2] == 0x41 && Data[fo + 3] == 0x52) // "STAR"
            {
                int sizeField = Data[fo + 4] | (Data[fo + 5] << 8);
                int invField = Data[fo + 6] | (Data[fo + 7] << 8);
                if ((sizeField ^ invField) == 0xFFFF)
                {
                    int size = sizeField + 1; // stored value is size-1
                    yield return new Rat(pc, size);
                    pc += 8 + size;
                    continue;
                }
            }
            pc++;
        }
    }

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

    /// <summary>First free run of <paramref name="need"/> bytes in expanded space (PC ≥ 0x80000), skipping valid RATs.</summary>
    public int FindFreeSpace(int need)
    {
        int end = Data.Length - HeaderOffset;
        for (int p = 0x80000; p + need <= end;)
        {
            int fo = p + HeaderOffset;
            if (Data[fo] == 0x53 && Data[fo + 1] == 0x54 && Data[fo + 2] == 0x41 && Data[fo + 3] == 0x52)
            {
                int sz = Data[fo + 4] | (Data[fo + 5] << 8), inv = Data[fo + 6] | (Data[fo + 7] << 8);
                if ((sz ^ inv) == 0xFFFF) { p += 8 + sz + 1; continue; }
            }
            bool ok = true;
            for (int i = 0; i < need; i++)
                if (Data[fo + i] != 0) { ok = false; p += i + 1; break; }
            if (ok) return p;
        }
        throw new InvalidOperationException("no free space (expand the ROM first)");
    }

    /// <summary>Write a RATS-protected block and return the SNES address of the data (after the tag).</summary>
    public int AllocateRats(byte[] data)
    {
        int pc = FindFreeSpace(8 + data.Length), fo = pc + HeaderOffset;
        Data[fo] = 0x53; Data[fo + 1] = 0x54; Data[fo + 2] = 0x41; Data[fo + 3] = 0x52;   // "STAR"
        int sm1 = data.Length - 1;
        Data[fo + 4] = (byte)sm1; Data[fo + 5] = (byte)(sm1 >> 8);
        int invv = sm1 ^ 0xFFFF;
        Data[fo + 6] = (byte)invv; Data[fo + 7] = (byte)(invv >> 8);
        Array.Copy(data, 0, Data, fo + 8, data.Length);
        return PcToSnes(pc + 8);
    }

    /// <summary>Repoint a level's Layer 1 table entry (see Rom.LevelData) at a SNES address.</summary>
    public void SetLayer1Pointer(int level, int snes)
    {
        int fo = FileOffset(Layer1TableSnes + level * 3);
        Data[fo] = (byte)snes; Data[fo + 1] = (byte)(snes >> 8); Data[fo + 2] = (byte)(snes >> 16);
    }

    /// <summary>
    /// Recompute and write the SNES checksum ($FFDE) + complement ($FFDC). Assumes a
    /// power-of-two ROM size (our expanded ROMs are 1/2/4 MB). The placeholder-invariant
    /// trick: checksum computed with checksum=0/complement=0xFFFF equals the final sum.
    /// </summary>
    public void FixChecksum()
    {
        int h = HeaderOffset, size = ActualRomSize;
        Data[0x7FDC + h] = 0xFF; Data[0x7FDD + h] = 0xFF;   // complement placeholder
        Data[0x7FDE + h] = 0x00; Data[0x7FDF + h] = 0x00;   // checksum placeholder
        long sum = 0;
        for (int i = 0; i < size; i++) sum += Data[h + i];
        int chk = (int)(sum & 0xFFFF), comp = chk ^ 0xFFFF;
        Data[0x7FDE + h] = (byte)chk; Data[0x7FDF + h] = (byte)(chk >> 8);
        Data[0x7FDC + h] = (byte)comp; Data[0x7FDD + h] = (byte)(comp >> 8);
    }

    public void SaveAs(string path) { FixChecksum(); File.WriteAllBytes(path, Data); }
}
