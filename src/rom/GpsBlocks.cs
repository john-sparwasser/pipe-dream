namespace PipeDream;

/// <summary>
/// Custom blocks inserted by GPS (Gopher Popcorn Stew), read off the ROM: no assembler, only
/// GPS's own tables. The layout is GPS 1.4.5's main.asm, and finding it is what gps.exe's
/// clean_hack does before a re-insert:
///
///   $06F690..$06F7E0  twelve contact stubs: PHB : PHX : REP #$30 : LDA.w #off : JSL block_execute
///   after block_execute, in the same freecode block:
///     "GPS_VeRsIoN"  db asm version  [db pages 40+ flag, version 2 on]
///                    dl banks  dl pointers  dw banks length  dw pointers length
///                    [dl pointers2  dw pointers2 length, when the pages 40+ flag is 1]
///
/// banks[tile] is the block's bank, 0 = not inserted; pointers[tile*2] is its low word. Tiles
/// 0x4000-0x7FFF (LM's pages 40+) take their word from pointers2, and only exist with the flag.
/// A block is `db $42` (or `db $37`, which adds the two wall contacts) and then one JMP per
/// contact, in <see cref="Contacts"/> order.
/// </summary>
public sealed class GpsBlocks
{
    public const int Hook = 0x06F690;

    /// <summary>The JMPs after a block's header byte, in order: the stubs pass offset id*3+1.</summary>
    public static readonly string[] Contacts =
    [
        "Mario below", "Mario above", "Mario side", "sprite V", "sprite H", "cape", "fireball",
        "top corner", "body inside", "head inside", "wall feet", "wall body",
    ];

    public int AsmVersion { get; }
    public IReadOnlyDictionary<int, GpsBlock> Blocks { get; }
    /// <summary>The tables' SNES addresses, which a re-insert frees. Pointers2 is -1 without pages 40+.</summary>
    public (int Banks, int Pointers, int Pointers2) Tables { get; }

    private GpsBlocks(int asmVersion, Dictionary<int, GpsBlock> blocks, (int, int, int) tables)
    {
        AsmVersion = asmVersion;
        Blocks = blocks;
        Tables = tables;
    }

    /// <summary>GPS's blocks on this ROM, or null when GPS is not installed. The marker is only
    /// looked for behind the hook's own JSL target, so a stray "GPS_VeRsIoN" elsewhere (a ROM
    /// GPS was removed from) does not count.</summary>
    public static GpsBlocks? Read(Rom rom)
    {
        var d = rom.Data;
        if (rom.ReadByte(Hook) != 0x8B || rom.ReadByte(Hook + 7) != 0x22) return null;
        int target = rom.ReadValue(Hook + 8, 3), exec = rom.FileOffset(target);
        if (exec >= d.Length) return null;
        int bankEnd = Math.Min(d.Length, exec - (target & 0x7FFF) + 0x8000);
        int at = d.AsSpan(exec, bankEnd - exec).IndexOf("GPS_VeRsIoN"u8);
        if (at < 0) return null;
        int m = exec + at;

        int Field(int off, int n) { int v = 0; for (int i = 0; i < n; i++) v |= d[m + off + i] << (8 * i); return v; }
        int asm = d[m + 11];
        int v = asm == 1 ? 0 : 1;                                 // version 1 has no pages 40+ byte
        bool pages40 = v == 1 && d[m + 12] == 1;
        var tables = (Banks: Field(12 + v, 3), Pointers: Field(15 + v, 3), Pointers2: pages40 ? Field(23, 3) : -1);
        int banks = rom.FileOffset(tables.Banks), bankLen = Field(18 + v, 2);
        int ptrs = rom.FileOffset(tables.Pointers), ptrLen = Field(20 + v, 2);
        int ptrs2 = pages40 ? rom.FileOffset(tables.Pointers2) : 0, ptr2Len = pages40 ? Field(26, 2) : 0;
        if (banks + bankLen > d.Length || ptrs + ptrLen > d.Length || ptrs2 + ptr2Len > d.Length) return null;

        // Read by file offset, as gps.exe copies them: a 32KB table runs on past its bank.
        var blocks = new Dictionary<int, GpsBlock>();
        for (int tile = 0x200; tile < (pages40 ? 0x8000 : 0x4000); tile++)
        {
            int bank = tile < bankLen ? d[banks + tile] : 0;
            if (bank == 0) continue;
            var (table, len, idx) = tile < 0x4000 ? (ptrs, ptrLen, tile * 2) : (ptrs2, ptr2Len, (tile - 0x4000) * 2);
            if (idx + 1 >= len) continue;
            int addr = bank << 16 | d[table + idx] | d[table + idx + 1] << 8;
            if (Block(rom, tile, addr) is { } b) blocks[tile] = b;
        }
        return new GpsBlocks(asm, blocks, tables);
    }

    /// <summary>One block's header and the contacts it acts on. A contact whose JMP lands on a
    /// bare RTL does nothing; anything else (code, or not a JMP at all) is counted as handled.</summary>
    private static GpsBlock? Block(Rom rom, int tile, int addr)
    {
        int fo = rom.FileOffset(addr);
        if (fo + 1 + Contacts.Length * 3 > rom.Data.Length) return null;
        int header = rom.Data[fo], handled = 0;
        for (int i = 0; i < (header == 0x37 ? 12 : 10); i++)
        {
            int j = fo + 1 + i * 3;
            bool bare = rom.Data[j] == 0x4C
                && (addr & 0xFF0000 | rom.Data[j + 1] | rom.Data[j + 2] << 8) is var target
                && rom.FileOffset(target) < rom.Data.Length && rom.ReadByte(target) == 0x6B;
            if (!bare) handled |= 1 << i;
        }
        return new GpsBlock(tile, addr, header, handled);
    }
}

/// <summary>One GPS block: the Map16 tile it is on, its code's SNES address, its header byte
/// ($42, or $37 with the wall contacts) and a bit per <see cref="GpsBlocks.Contacts"/> entry it
/// acts on.</summary>
public sealed record GpsBlock(int Tile, int Addr, int Header, int Handled)
{
    public IEnumerable<string> HandledContacts
        => GpsBlocks.Contacts.Where((_, i) => (Handled >> i & 1) != 0);
}
