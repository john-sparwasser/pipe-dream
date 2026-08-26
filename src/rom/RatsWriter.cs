namespace PipeDream;

/// <summary>
/// The ROM write path (CONTRACT §2).
///
/// New or relocated data (edited levels, custom palettes, Map16) is appended to the
/// expanded area of the ROM (PC ≥ 0x80000) inside a RATS block, and a pointer table entry
/// is repointed at it. This class owns that machinery:
///
///   • RATS ("STAR" tag) — Lunar Magic's free-space protocol: an 8-byte tag ("STAR" +
///     size-1 + its inverse) precedes each protected block, so tools can walk the expanded
///     area and skip regions already in use. The inverse check is REQUIRED — random data
///     contains the ASCII bytes "STAR" often enough that the size/inverse pair is the only
///     reliable validity signal.
///   • Allocate — find free space, write the tag, copy the payload, return the SNES
///     address of the payload (just past the tag).
///   • FixChecksum / SaveAs — recompute $FFDC/$FFDE and write the file.
///
/// Level object/sprite ENCODING (bytes → stream) lives in LevelEncoder / SpriteData.Encode;
/// this class only places the encoded bytes. Growing the file is Rom.ExpandTo.
/// </summary>
public static class RatsWriter
{
    public readonly record struct Rat(int PcOffset, int Size);

    /// <summary>
    /// Enumerate valid RATS-protected regions in the expanded area (pc ≥ 0x80000).
    /// A tag is valid only when (size-1) XOR (~(size-1)) == 0xFFFF — required, because
    /// random data contains the ASCII bytes "STAR".
    /// </summary>
    public static IEnumerable<Rat> EnumerateRats(Rom rom)
    {
        int end = rom.Data.Length - 8;
        for (int pc = 0x80000; pc <= end - rom.HeaderOffset; )
        {
            int fo = pc + rom.HeaderOffset;
            if (rom.Data[fo] == 0x53 && rom.Data[fo + 1] == 0x54 && rom.Data[fo + 2] == 0x41 && rom.Data[fo + 3] == 0x52) // "STAR"
            {
                int sizeField = rom.Data[fo + 4] | (rom.Data[fo + 5] << 8);
                int invField = rom.Data[fo + 6] | (rom.Data[fo + 7] << 8);
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

    /// <summary>First free run of <paramref name="need"/> bytes in expanded space (PC ≥ 0x80000), skipping valid RATs.
    /// <paramref name="avoidBankCross"/>: keep the DATA span (after the 8-byte tag) inside one
    /// 0x8000 bank — level/sprite streams are addressed by a 16-bit runtime pointer with a
    /// fixed bank byte, so a stream straddling a bank boundary breaks in-game.
    /// <paramref name="from"/>: start the search past a region someone else owns — prep's GFX
    /// conversion parks in the tail so it does not eat the run RomBuilder allocates from.</summary>
    public static int FindFreeSpace(Rom rom, int need, bool avoidBankCross = false, int from = 0x80000)
    {
        int end = rom.Data.Length - rom.HeaderOffset;
        for (int p = from; p + need <= end;)
        {
            int fo = p + rom.HeaderOffset;
            if (rom.Data[fo] == 0x53 && rom.Data[fo + 1] == 0x54 && rom.Data[fo + 2] == 0x41 && rom.Data[fo + 3] == 0x52)
            {
                int sz = rom.Data[fo + 4] | (rom.Data[fo + 5] << 8), inv = rom.Data[fo + 6] | (rom.Data[fo + 7] << 8);
                if ((sz ^ inv) == 0xFFFF) { p += 8 + sz + 1; continue; }
            }
            if (avoidBankCross)
            {
                int dataStart = p + 8, dataEnd = p + need - 1;
                if (dataStart >> 15 != dataEnd >> 15)
                {   // bump so the tag ends exactly at the boundary and the data opens the next bank
                    p = ((dataStart >> 15) + 1 << 15) - 8;
                    continue;
                }
            }
            bool ok = true;
            for (int i = 0; i < need; i++)
                if (rom.Data[fo + i] != 0) { ok = false; p += i + 1; break; }
            if (ok) return p;
        }
        throw new InvalidOperationException("no free space (expand the ROM first)");
    }

    /// <summary>Write a RATS-protected block and return the SNES address of the data (after the tag).</summary>
    public static int Allocate(Rom rom, byte[] data, bool avoidBankCross = false, int from = 0x80000)
    {
        int pc = FindFreeSpace(rom, 8 + data.Length, avoidBankCross, from), fo = pc + rom.HeaderOffset;
        rom.Data[fo] = 0x53; rom.Data[fo + 1] = 0x54; rom.Data[fo + 2] = 0x41; rom.Data[fo + 3] = 0x52;   // "STAR"
        int sm1 = data.Length - 1;
        rom.Data[fo + 4] = (byte)sm1; rom.Data[fo + 5] = (byte)(sm1 >> 8);
        int invv = sm1 ^ 0xFFFF;
        rom.Data[fo + 6] = (byte)invv; rom.Data[fo + 7] = (byte)(invv >> 8);
        Array.Copy(data, 0, rom.Data, fo + 8, data.Length);
        return Rom.PcToSnes(pc + 8);
    }

    /// <summary>
    /// Recompute and write the SNES checksum ($FFDE) + complement ($FFDC). Assumes a
    /// power-of-two ROM size (our expanded ROMs are 1/2/4 MB). The placeholder-invariant
    /// trick: checksum computed with checksum=0/complement=0xFFFF equals the final sum.
    /// </summary>
    public static void FixChecksum(Rom rom)
    {
        int h = rom.HeaderOffset, size = rom.ActualRomSize;
        rom.Data[0x7FDC + h] = 0xFF; rom.Data[0x7FDD + h] = 0xFF;   // complement placeholder
        rom.Data[0x7FDE + h] = 0x00; rom.Data[0x7FDF + h] = 0x00;   // checksum placeholder
        long sum = 0;
        for (int i = 0; i < size; i++) sum += rom.Data[h + i];
        int chk = (int)(sum & 0xFFFF), comp = chk ^ 0xFFFF;
        rom.Data[0x7FDE + h] = (byte)chk; rom.Data[0x7FDF + h] = (byte)(chk >> 8);
        rom.Data[0x7FDC + h] = (byte)comp; rom.Data[0x7FDD + h] = (byte)(comp >> 8);
    }

    public static void SaveAs(Rom rom, string path) { FixChecksum(rom); File.WriteAllBytes(path, rom.Data); }
}
