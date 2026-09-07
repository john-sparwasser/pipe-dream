namespace PipeDream;

// Overworld — where THIS ROM keeps the tables Lunar Magic relocates when it saves an overworld,
// read from the loader's own operands. The vanilla addresses are the constants in Overworld.cs.

public sealed partial class Overworld
{
    /// <summary>
    /// Where a ROM keeps the overworld tables Lunar Magic relocates, read from the operands of
    /// the code that loads them — the same bytes the game follows, so they are right on a ROM
    /// LM has saved and on vanilla alike. Layer 2: <c>LDA #$addr</c> at $04DC71 and $04DC8C
    /// with the shared bank at $04DC79. Map16: <c>LDX #$addr</c> at $04DCBB, bank at $04DCC0;
    /// LM's table is two pages (its "STAR" marker follows entry 0x200). Layer 1's second byte:
    /// LM replaces the translevel scan at $04D7F2 with two LZ2 blobs decompressed by $00B8DE —
    /// the per-tile level table into $7ED000, then the layer 1 high bytes into $7FC800 —
    /// each behind <c>LDX #$addr : STX $8A : LDA #$bank : STA $8C</c>. A blob address of 0
    /// means the ROM has none (vanilla).
    /// </summary>
    public readonly record struct Tables(int Layer2Low, int Layer2High, int Map16Defs, int Map16Count, int LevelTableBlob, int Layer1HighBlob)
    {
        public static Tables Of(Rom rom)
        {
            var d = rom.Data;
            int low = Overworld.Layer2Low, high = Overworld.Layer2High, defs = Overworld.Map16Defs;
            int p = rom.FileOffset(0x04DC71), q = rom.FileOffset(0x04DC8C);
            if (d[p] == 0xA9 && d[p + 7] == 0xA9 && d[q] == 0xA9)
            {
                int bank = d[p + 8] << 16;
                low = bank | d[p + 1] | d[p + 2] << 8;
                high = bank | d[q + 1] | d[q + 2] << 8;
            }
            p = rom.FileOffset(0x04DCBB);
            if (d[p] == 0xA2 && d[p + 5] == 0xA9) defs = d[p + 6] << 16 | d[p + 1] | d[p + 2] << 8;
            // The two blobs, in order, wherever they sit in LM's 0x40-byte rewrite of the scan.
            int level = 0, l1hi = 0;
            p = rom.FileOffset(0x04D7F2);
            for (int i = 0; i < 0x40; i++)
                if (d[p + i] == 0xA2 && d[p + i + 3] == 0x86 && d[p + i + 4] == 0x8A && d[p + i + 5] == 0xA9 && d[p + i + 7] == 0x85 && d[p + i + 8] == 0x8C)
                {
                    int at = d[p + i + 6] << 16 | d[p + i + 1] | d[p + i + 2] << 8;
                    if (level == 0) level = at; else if (l1hi == 0) l1hi = at;
                }
            return new(low, high, defs, defs == Overworld.Map16Defs ? VanillaMap16Count : 0x200, level, l1hi);
        }
    }
}
