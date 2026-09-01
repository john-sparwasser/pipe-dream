namespace PipeDream;

/// <summary>
/// LUNAR MAGIC hack detection + expanded-table location/decode (CONTRACT §7).
///
/// A ROM saved by Lunar Magic carries LM's inserted ASM plus expanded data tables (extended
/// Map16 defs, per-level GFX bypass, custom palettes, sprite entry sizes...). LM places most
/// of those tables at per-ROM addresses, but the *code* that reads them is byte-stable — so
/// we locate each table by scanning for that code signature and reading the address baked in
/// as its operand (ScanOperand). Fixed-address hooks/tables are read directly. All of this is
/// detection + read/write of LM's structures; vanilla ROMs return "absent" from every probe.
///
/// Exposed as extension members on <see cref="Rom"/> (callers read `rom.HasDm16Hijack` etc.);
/// the per-ROM scan caches these fill live on Rom itself.
/// </summary>
public static class LunarMagic
{
    /// <summary>LM per-level custom palette pointer table (3 bytes/level, fixed address §7e).</summary>
    public const int LmPaletteTable = 0x0EF600;

    /// <summary>How many 0x1000-tile ranges the Map16 def lookup ladder can address
    /// (tiles 0x200-0x7FFF). One slot per range — see <c>LmMap16Slot</c>.</summary>
    public const int Map16RangeCount = 8;

    extension(Rom rom)
    {
        /// <summary>
        /// True if LM's Direct Map16 ASM is installed. LM repurposes the reserved object slots
        /// 0x23/0x27 by repointing their handlers away from the vanilla placeholder $0DB3E3
        /// (handler table entry for obj 0x23 is at $0DA4BB).
        /// </summary>
        public bool HasDm16Hijack => rom.ReadValue(0x0DA4BB, 3) != 0x0DB3E3;

        /// <summary>
        /// LM extended Map16 defs (tiles 0x200-0xFFF), decoded from LM's Map16-lookup hijack:
        /// $00C17A = JSL $06F5D0 → piecewise pointer math at fixed $06F540, whose 0x200-0xFFF
        /// branch is `ADC #imm : LDY #bank&lt;&lt;8` at fixed $06F552 (CONTRACT §7a-rev).
        /// def(tile) = bank:(imm + tile*8). Returns (imm, bank), bank 0 = no extended defs.
        /// NOTE: the RATS pointer at $02C2E1 is NOT reliable (points at a stale block in ShaoBase).
        /// </summary>
        public (int Imm, int Bank) LmMap16Defs => rom.LmMap16Slot(0);

        /// <summary>
        /// Address of range <paramref name="range"/>'s `ADC #imm16 : LDY #bank&lt;&lt;8` pair in the
        /// lookup ladder. One slot covers 0x1000 tiles and no more, because def =
        /// bank:(imm + tile*8) is 16-bit addressing into a 32KB LoROM window — 0x8000/8 =
        /// 0x1000 defs per bank. So the ladder is one slot per 0x1000 tiles:
        ///
        ///   range 0 = tiles 0x200-0x0FFF   $06F552    range 2 = 0x2000-0x2FFF  $06F566
        ///   range 1 = tiles 0x1000-0x1FFF  $06F55B    range 3 = 0x3000-0x3FFF  $06F56F
        ///
        /// Ranges 4-7 (0x4000+) live in a second chain at $06F593/$06F59C/$06F5A7/$06F5B0;
        /// no sampled ROM populates them, so they are read but never written.
        /// </summary>
        private static int SlotAddr(int range) => range switch
        {
            0 => 0x06F552, 1 => 0x06F55B, 2 => 0x06F566, 3 => 0x06F56F,
            4 => 0x06F593, 5 => 0x06F59C, 6 => 0x06F5A7, 7 => 0x06F5B0,
            _ => -1,
        };

        /// <summary>
        /// One range's def base: (imm, bank), bank 0 = that range has no defs installed.
        /// DogsOfWar is the reference for a populated range 1 ($1D:$4E5E) — every other
        /// sampled hack leaves ranges 1+ at bank 0, which is why they once looked unusable.
        /// </summary>
        public (int Imm, int Bank) LmMap16Slot(int range)
        {
            if (rom.ReadByte(0x00C17A) != 0x22 || rom.ReadValue(0x00C17B, 3) != 0x06F5D0) return (0, 0);
            int at = SlotAddr(range);
            if (at < 0) return (0, 0);
            // The slot must really be `ADC #imm16 : LDY #imm16` — a range our own prep left
            // as an untouched $FF region, or LM's `LDA #imm16` blank fallback, is not a slot.
            if (rom.ReadByte(at) != 0x69 || rom.ReadByte(at + 3) != 0xA0) return (0, 0);
            return (rom.ReadValue(at + 1, 2), rom.ReadValue(at + 4, 2) >> 8);
        }

        /// <summary>Def address for an extended tile, honouring which range it falls in.
        /// -1 when that range has no defs. The `& 0xFFFF` matters: the ladder reaches a slot
        /// after shifting, so range 2+ arrive with tile*8 already wrapped mod 0x10000, and
        /// the stored imm is chosen against that wrapped value.</summary>
        public int LmMap16DefAddr(int tile)
        {
            if (tile is < 0x200 or >= 0x8000) return -1;
            var (imm, bank) = rom.LmMap16Slot(tile >> 12);
            if (bank == 0) return -1;
            // LM's ladder reaches the range-2/3 (and 6/7) ADC with the carry SET — the second ASL
            // shifts tile bit 13 out and nothing clears it — so those slots store imm − 1 (LM's
            // own defaults are FFFF/7FFF). The pointer the game forms is imm + tile*8 + C.
            return (bank << 16) | ((imm + tile * 8 + SlotCarry(tile >> 12)) & 0xFFFF);
        }

        /// <summary>1 for the ranges whose slot is entered with the carry set (2, 3, 6, 7).</summary>
        private static int SlotCarry(int range) => (range & 2) != 0 ? 1 : 0;

        /// <summary>Kept for callers that just need a presence check: >= 0 when extended defs exist.</summary>
        public int LmMap16Base => rom.LmMap16Defs.Bank == 0 ? -1 : (rom.LmMap16Defs.Bank << 16) | rom.LmMap16Defs.Imm;

        /// <summary>LM's acts-like table (2 bytes/tile, per-ROM location): the behavior tile a Map16 tile
        /// acts as. Base 0 = the LM code slot exists but no table was allocated (all-vanilla behavior).</summary>
        public int ActsAs(int tile) => rom.LmActsAsBase <= 0 ? tile : rom.ReadValue(rom.LmActsAsBase + tile * 2, 2);

        /// <summary>First tile a range's defs cover (range 0 starts past the vanilla defs).</summary>
        private static int RangeStart(int range) => range == 0 ? 0x200 : range << 12;

        /// <summary>
        /// Exclusive tile ceiling for one range, bounded by the RATS block holding its def
        /// block — LM only allocates defs for the pages the hack uses, and the next
        /// allocation's "STAR" tag sits right past the last real def (reading to the bank end
        /// shows neighboring blocks as garbage tiles). Returns the range start when the range
        /// has no defs at all.
        /// </summary>
        private int RangeCeiling(int range)
        {
            int startTile = RangeStart(range);
            int addr = rom.LmMap16DefAddr(startTile);
            if (addr < 0) return startTile;
            int cap = (range + 1) << 12;
            int start = rom.FileOffset(addr) - rom.HeaderOffset;                   // first def (pc)
            foreach (var rat in RatsWriter.EnumerateRats(rom))
                if (start >= rat.PcOffset + 8 && start < rat.PcOffset + 8 + rat.Size)
                    return Math.Min(cap, startTile + (rat.PcOffset + 8 + rat.Size - start) / 8);
            return Math.Min(cap, startTile + (0x10000 - (addr & 0xFFFF)) / 8);     // no RATS: clip at bank end
        }

        /// <summary>
        /// Total Map16 tile count: 0x200 vanilla, plus every CONTIGUOUS extended range the
        /// ladder addresses. Contiguity matters because the editor treats the count as a flat
        /// ceiling: a hack with defs in range 2 but not range 1 has a hole the editor cannot
        /// paint through, so the count stops at the hole rather than aliasing across it.
        /// </summary>
        public int Map16TileCount
        {
            get
            {
                if (rom.map16TileCount >= 0) return rom.map16TileCount;
                int count = 0x200;
                for (int r = 0; r < Map16RangeCount; r++)
                {
                    if (RangeStart(r) > count) break;            // hole: stop before it
                    int end = rom.RangeCeiling(r);
                    if (end <= count) break;
                    count = end;
                    if (end < (r + 1) << 12) break;              // range only partly allocated
                }
                return rom.map16TileCount = count;
            }
        }

        /// <summary>Whether the in-game lookup ladder has a slot for this range at all — i.e.
        /// whether repatching it would actually be honoured in-game. True with bank 0 for a
        /// range whose slot exists but holds no defs yet, which is the state EnsureMap16Tiles
        /// grows out of; false for a base whose ladder stops short (our own prep v2 only ever
        /// emitted range 0, and routed everything above it to the blank fallback).</summary>
        public bool HasMap16Range(int range)
        {
            if (rom.ReadByte(0x00C17A) != 0x22 || rom.ReadValue(0x00C17B, 3) != 0x06F5D0) return false;
            // LM's ladder (v12) carries slots for tiles 0x4000-0x7FFF too, but nothing of ours
            // allocates there (Map16Layout: 0x3FFF is the ceiling), so they are not "ranges".
            if (range < 0 || !Map16Layout.CanAllocate(RangeStart(range))) return false;
            int at = SlotAddr(range);
            return at >= 0 && rom.ReadByte(at) == 0x69 && rom.ReadByte(at + 3) == 0xA0;
        }

        /// <summary>
        /// Grow the LM extended Map16 def region to cover at least <paramref name="minCount"/>
        /// tiles (page-granular) — the LM-free allocation path. Reproduces LM's own layout,
        /// learned from a before/after diff of an LM page allocation: a RATS block at a fresh
        /// bank (tag at the bank start, defs at +8), existing defs copied over, new tiles
        /// filled with LM's default-empty def (word 0x1004 x4), and the in-game lookup slot
        /// for that range repatched. The old block is left in place (stale), exactly as LM
        /// leaves stale blocks.
        ///
        /// One bank per range, because that IS the shape of the hijack: def =
        /// bank:(imm + tile*8) is 16-bit addressing into a 32KB LoROM window, so 0x8000/8 =
        /// 0x1000 defs is the hard per-slot ceiling. Growing past 0xFFF therefore means
        /// allocating range 1's bank and patching range 1's slot, not extending range 0's.
        /// </summary>
        public string? EnsureMap16Tiles(int minCount)
        {
            if (!rom.HasMap16Range(0)) return "ROM lacks LM's Map16 hijack — save it in Lunar Magic once first.";
            int newCount = (Math.Max(minCount, 0x201) + 0xFF) & ~0xFF;      // whole pages
            int top = (newCount - 1) >> 12;
            if (top >= Map16RangeCount) return $"Map16 tiles past 0x{(Map16RangeCount << 12) - 1:X} aren't supported.";
            for (int r = 0; r <= top; r++)
                if (!rom.HasMap16Range(r))
                    return $"this base's Map16 lookup only reaches tile 0x{RangeStart(r) - 1:X} — "
                         + "upgrade the base to prep v3 (File → Upgrade base).";

            if (newCount <= rom.Map16TileCount) return null;
            for (int r = 0; r <= top; r++)
                rom.GrowRange(r, Math.Min(newCount, (r + 1) << 12));
            rom.map16TileCount = -1;
            return null;
        }

        /// <summary>Reallocate one range's def block so it covers tiles up to <paramref name="want"/>
        /// (exclusive), copying whatever the range already had.</summary>
        private void GrowRange(int range, int want)
        {
            int startTile = RangeStart(range);
            int have = rom.RangeCeiling(range);
            if (have >= want) return;

            // A bank's LoROM window is $8000-$FFFF, so a block whose tag sits at the bank
            // start has 0x7FF8 bytes for defs — 8 short of a FULL 0x1000-tile range. Rather
            // than leave every high range one tile shy of its last page, a full range takes
            // two fresh banks and parks its tag in the first one's tail, so the defs fill
            // $8000-$FFFF exactly. The slack costs 32KB per full high range and nothing else.
            int dataBytes = (want - startTile) * 8;
            bool wholeBank = dataBytes > 0x7FF8;
            int tagPc = rom.ActualRomSize + (wholeBank ? 0x7FF8 : 0);
            rom.ExpandTo(rom.ActualRomSize + (wholeBank ? 0x10000 : 0x8000));
            int tagFo = tagPc + rom.HeaderOffset;
            rom.Data[tagFo] = 0x53; rom.Data[tagFo + 1] = 0x54; rom.Data[tagFo + 2] = 0x41; rom.Data[tagFo + 3] = 0x52;  // "STAR"
            rom.Data[tagFo + 4] = (byte)(dataBytes - 1); rom.Data[tagFo + 5] = (byte)((dataBytes - 1) >> 8);
            rom.Data[tagFo + 6] = (byte)~(dataBytes - 1); rom.Data[tagFo + 7] = (byte)(~(dataBytes - 1) >> 8);

            int dataFo = tagFo + 8;
            int copied = (have - startTile) * 8;
            if (copied > 0) Array.Copy(rom.Data, rom.FileOffset(rom.LmMap16DefAddr(startTile)), rom.Data, dataFo, copied);
            for (int i = copied; i < dataBytes; i += 2)                      // LM default-empty def
            { rom.Data[dataFo + i] = 0x04; rom.Data[dataFo + i + 1] = 0x10; }

            int dataSnes = Rom.PcToSnes(tagPc + 8);                          // bankaddr $8008 or $8000
            int newImm = ((dataSnes & 0xFFFF) - startTile * 8 - SlotCarry(range)) & 0xFFFF;   // range 0: $7008; 2/3 store imm − 1 (see LmMap16DefAddr)
            int slot = rom.FileOffset(SlotAddr(range) + 1);
            rom.Data[slot] = (byte)newImm; rom.Data[slot + 1] = (byte)(newImm >> 8);
            rom.Data[slot + 3] = 0x00; rom.Data[slot + 4] = (byte)(dataSnes >> 16); // LDY #bank<<8
            rom.map16TileCount = -1;                                         // the next range's growth re-reads the count
        }

        // --- LM expanded-table bases (CONTRACT §7d) -----------------------------
        // LM bakes the addresses of its expanded tables into its inserted ASM as LDA long,X
        // operands; the surrounding code bytes are stable across ROMs, the operands are not.
        // Each base is found once by signature scan and cached on Rom (-2 = not scanned yet).

        /// <summary>
        /// 24-bit pointer to LM's GLOBAL ExAnimation record (runs in every level), or -1 if the
        /// hack has no global list (CONTRACT §12f). Unlike the per-level table this is not indexed;
        /// the address is baked into the engine as two immediates: `A9 &lt;bankword&gt; F0 ?? 85 01
        /// 8D 17 C0 A9 &lt;low16&gt;` — bankword's high byte is the record bank, low16 the offset.
        /// A zero bankword means the BEQ skips (no global list).
        /// </summary>
        public int LmGlobalExAnimPtr => rom.lmGlobalExAnimPtr != -2 ? rom.lmGlobalExAnimPtr
            : rom.lmGlobalExAnimPtr = ScanGlobalExAnim(rom);

        /// <summary>SNES address of LM's uncompressed ExAnimation source file 60+<paramref name="index"/>
        /// (0-3), from the fixed 4x3-byte table at $03BCC0 (reference/EXANIMATION.md §2); -1 when the
        /// file is not inserted (FF FF FF, or 00 00 00 once LM's ExAnimation ASM zeroed the table).</summary>
        public int LmAltExGfx(int index)
        {
            if (index is < 0 or > 3) return -1;
            int p = rom.ReadValue(Rom.LmAltExGfxTable + index * 3, 3);
            return p is 0 or 0xFFFFFF ? -1 : p;
        }

        /// <summary>Install (or replace) ExAnimation source file 60+<paramref name="index"/>: the raw
        /// 4bpp bytes go into a fresh RATS block, the old one is released, and the $03BCC0 entry is
        /// repointed — exactly what LM's Insert ExGFX does for these four files. Up to 32KB, kept
        /// in one bank because the engine addresses frames as bank:offset.</summary>
        public void SetLmAltExGfx(int index, byte[] data)
        {
            if (index is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(index));
            if (data.Length is 0 or > 0x8000) throw new ArgumentException("ExGFX 60-63 are 1..32768 bytes", nameof(data));
            int old = rom.LmAltExGfx(index);
            if (old >= 0) RatsWriter.Release(rom, old);
            int snes = RatsWriter.Allocate(rom, data, avoidBankCross: true);
            int fo = rom.FileOffset(Rom.LmAltExGfxTable + index * 3);
            rom.Data[fo] = (byte)snes; rom.Data[fo + 1] = (byte)(snes >> 8); rom.Data[fo + 2] = (byte)(snes >> 16);
        }

        /// <summary>True if a PIXI-family sprite tool hijacked the sprite main hook ($0185C3 is a
        /// JSL into an inserted bank instead of the vanilla `STZ $1491`). Init ($018172) is hijacked
        /// in lockstep. Our OAM capture must bypass these to run the vanilla routines (CONTRACT §11a).</summary>
        public bool HasPixiSpriteHook => rom.Data[rom.FileOffset(0x0185C3)] == 0x22;

        /// <summary>Base of PIXI's per-sprite config table (stride 0x10; first 3 bytes = the sprite's
        /// routine pointer), or -1. Located from the dispatch `LDA $xxxx,Y : STA $00 : LDA $yyyy,Y :
        /// STA $01` in the hijack bank; the table is read with DBR = that bank.</summary>
        public int PixiCustomTable => rom.pixiTable != -2 ? rom.pixiTable : rom.pixiTable = ScanPixiTable(rom);

        // NOTE: custom-ness cannot be derived from the config table (a routine pointer in an
        // inserted bank): PIXI shares one routine across numbers and fills unreplaced entries
        // too. Whether a PLACED sprite is custom is decided by its LM extra bits (2/3), which
        // the spawn code stores to $7FAB10,X — the only gate the hooks test.

        /// <summary>LM's per-level sprite-data BANK table, or -1 (vanilla: fixed bank $07).
        /// LM replaces the stream-pointer bank setup at $05D8F5 (vanilla `LDA #$07 : STA $D0`)
        /// with a JSL to `PHB : PHK : PLB : LDY $0E : LDA $xxxx,Y : STA $D0` — the LDA operand
        /// (in the JSL target's bank) is the table, 1 byte per level (CONTRACT §11).</summary>
        public int LmSpriteBankTable => rom.lmSpriteBankTable != -2 ? rom.lmSpriteBankTable
            : rom.lmSpriteBankTable = ScanSpriteBankTable(rom);

        /// <summary>
        /// SNES entry of LM's ExAnimation SETUP routine (populates the $7FC0xx control block from the
        /// record), or -1. Both engine routines open with `PHB : LDX #$7F : PHX : PLB` (DBR=$7F); the
        /// setup follows it with `LDA #$FF : STA $C019` (§12f — emulated to resolve the slots).
        /// </summary>
        public int LmExAnimSetupEntry => rom.lmExAnimSetupEntry != -2 ? rom.lmExAnimSetupEntry
            : rom.lmExAnimSetupEntry = ScanCodeAddr(rom, [0x8B, 0xA2, 0x7F, 0xDA, 0xAB, 0xA9, 0xFF, 0x8D, 0x19, 0xC0]);

        /// <summary>
        /// SNES entry of LM's ExAnimation PROCESSOR (fills the eight stride-7 $7FC0C0 DMA records for
        /// the current frame), or -1. Same DBR prologue, then `LDY $14 : CPY $C003` (§12f).
        /// </summary>
        public int LmExAnimProcEntry => rom.lmExAnimProcEntry != -2 ? rom.lmExAnimProcEntry
            : rom.lmExAnimProcEntry = ScanCodeAddr(rom, [0x8B, 0xA2, 0x7F, 0xDA, 0xAB, 0xA4, 0x14, 0xCC, 0x03, 0xC0]);

        /// <summary>
        /// Base of LM's per-level ExAnimation pointer table (3 bytes/level, 24-bit record ptr,
        /// FF 00 00 = none), or -1 if the hack lacks ExAnimation (CONTRACT §12e). Located from
        /// the record reader at $108700: `A5 FE F0 ?? 3A 0A 18 65 FE 3A AA BF <base+1,X>` (the
        /// first table access reads base+1, so subtract 1). Distinct from the GFX-bypass reader,
        /// which uses five ASLs (stride 0x20) instead of DEC/ASL/CLC/ADC (stride 3).
        /// </summary>
        public int LmExAnimBase => rom.lmExAnimBase != -2 ? rom.lmExAnimBase : rom.lmExAnimBase = ScanExAnimBase(rom);

        /// <summary>
        /// Base of LM's sprite entry-size table (0x400 bytes, byte size per (extraBits&lt;&lt;8)|sprite#,
        /// includes the 3 base bytes), or -1 = vanilla 3-byte entries. Located via the LDA long,X
        /// operand in LM's sprite-advance hijack (CONTRACT §11).
        /// </summary>
        public int LmSpriteSizeBase => rom.lmSpriteSizeBase != -2 ? rom.lmSpriteSizeBase
            : rom.lmSpriteSizeBase = rom.ReadByte(Rom.LmSpriteSizeFlag) == 0x42 ? rom.ReadValue(Rom.LmSpriteSizePtr, 3)
            : ScanOperand(rom, [0x4A, 0x4A, 0x29, 0x03, 0xEB, 0xC8, 0xC8, 0xB7, 0xCE, 0x88, 0x88,
                                0x08, 0xC2, 0x10, 0xDA, 0xAA, 0x98, 0x18, 0x7F], []);

        /// <summary>Bytes one sprite record takes in the list, base bytes included: the table's
        /// entry for (extra bits, number), or 3 without a table.</summary>
        public int SpriteEntrySize(int extra, int number)
            => rom.LmSpriteSizeBase is var b && b > 0 ? Math.Max(3, (int)rom.ReadByte(b + ((extra & 3) << 8) + (number & 0xFF))) : 3;

        /// <summary>Author the size table the way LM's help says to: a 0x400-byte table of 3s in
        /// free space, its address at <see cref="LmSpriteSizePtr"/> and 0x42 at
        /// <see cref="LmSpriteSizeFlag"/>, then this one entry. Sizes are per (extra bits, number),
        /// so every placement of that sprite reads the same length — max 0xF (LM's cap).</summary>
        public void SetSpriteEntrySize(int extra, int number, int size)
        {
            if (rom.LmSpriteSizeBase <= 0)
            {
                var table = new byte[0x400];
                Array.Fill(table, (byte)3);
                int snes = RatsWriter.Allocate(rom, table, avoidBankCross: true);
                int p = rom.FileOffset(Rom.LmSpriteSizePtr);
                rom.Data[p] = (byte)snes; rom.Data[p + 1] = (byte)(snes >> 8); rom.Data[p + 2] = (byte)(snes >> 16);
                rom.Data[p + 3] = 0x42;                                            // LmSpriteSizeFlag
                rom.lmSpriteSizeBase = -2;
            }
            rom.Data[rom.FileOffset(rom.LmSpriteSizeBase + ((extra & 3) << 8) + (number & 0xFF))] = (byte)Math.Clamp(size, 3, 0xF);
        }

        /// <summary>Base of LM's acts-like table, or -1 (from the remap reader in LM's $06F5D0 code).</summary>
        public int LmActsAsBase => rom.lmActsAsBase != -2 ? rom.lmActsAsBase
            : rom.lmActsAsBase = ScanOperand(rom, [0xA8, 0x0A, 0xAA, 0x30, -1, 0xBF], [0xC9, 0x00, 0x02]);

        /// <summary>Base of LM's per-level GFX bypass table (0x20 bytes/level), or -1.</summary>
        public int LmGfxBypassBase => rom.lmGfxBypassBase != -2 ? rom.lmGfxBypassBase
            : rom.lmGfxBypassBase = ScanOperand(rom, [0xA5, 0xFE, 0xF0, -1, 0x3A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0xAA, 0xBF], []);

        /// <summary>Base of LM's ExGFX 0x100+ pointer table (3 bytes/file), or -1.</summary>
        public int LmExGfxBase => rom.lmExGfxBase != -2 ? rom.lmExGfxBase
            : rom.lmExGfxBase = ScanOperand(rom, [0x38, 0xE9, 0x00, 0x01, 0x85, 0x8A, 0x0A, 0x18, 0x65, 0x8A, 0xAA, 0xBF], []);

        /// <summary>
        /// Per-level Super GFX Bypass record (16 words), or null if the hack is absent or the
        /// record is disabled. w0=AN2 (bit15 = bypass enabled), w1=AN1, w2=BG3, w3=BG2, w4=FG3,
        /// w5=BG1, w6=FG2, w7=FG1, w8-11=SP4..SP1. Slot value &amp; 0xFFF = GFX/ExGFX file#,
        /// 0x7F = slot uses the tileset default. Session overrides from Rom.GfxSlotOverrides
        /// are overlaid on the record. Words 12-15 are the LAYER-3 slots and belong to
        /// <see cref="LmLayer3Gfx"/>: they have their own enable bit, so an override on one of
        /// them must not turn this bypass on.
        /// </summary>
        public ushort[]? LmGfxBypass(int level)
        {
            var r = rom.LmGfxRecord(level);
            ushort[]? w = r is not null && (r[0] & 0x8000) != 0 ? r : null;
            foreach (var ((lvl, word), file) in rom.GfxSlotOverrides)
            {
                if (lvl != level || word is < 0 or > 11) continue;
                if (w is null) { w = new ushort[16]; Array.Fill(w, (ushort)0x7F); w[0] = 0x807F; }   // all slots "default"
                w[word] = (ushort)((w[word] & ~0xFFF) | (file & 0xFFF));
            }
            return w;
        }

        /// <summary>The raw 16 words of a level's record, whatever its enable bits say, or null
        /// when the ROM has no table. Two independent features share it — the FG/BG/SP bypass on
        /// w0 bit 15 and the layer-3 GFX bypass on w0 bit 14 — so the gate belongs to the caller,
        /// not to the read.</summary>
        public ushort[]? LmGfxRecord(int level)
        {
            if (rom.LmGfxBypassBase < 0) return null;
            int fo = rom.FileOffset(rom.LmGfxBypassBase + level * 0x20);
            if (fo < 0 || fo + 0x20 > rom.Data.Length) return null;
            var r = new ushort[16];
            for (int i = 0; i < 16; i++) r[i] = (ushort)(rom.Data[fo + i * 2] | (rom.Data[fo + i * 2 + 1] << 8));
            return r;
        }

        /// <summary>
        /// The level's four layer-3 GFX files, LG1-LG4 in that order, or null when it does not
        /// bypass them (CONTRACT §12b).
        ///
        /// They are **words 12-15 of the same per-level record** as the Super GFX Bypass — the
        /// "tail (TBD, constant)" §7d left unnamed — in reverse slot order (w15 = LG1), gated by
        /// **w0 bit 14**, which is a different bit from the FG/BG/SP bypass on bit 15. LM's own
        /// loader at $0FF9E0 does exactly this: `LDA [record] : ASL : BPL default`, and its
        /// "default" is a fixed record at $0FFA6F whose tail is the vanilla `2B 2A 29 28`. A slot
        /// left at 0x7F keeps its vanilla file, the same convention as every other slot here.
        ///
        /// Session overrides from Rom.GfxSlotOverrides are overlaid the same way the FG/BG/SP
        /// bypass overlays its own words — pointing one LG slot somewhere is enough to turn the
        /// bypass on, with the other three left at 0x7F meaning "still the vanilla file".
        /// </summary>
        public int[]? LmLayer3Gfx(int level)
        {
            var r = rom.LmGfxRecord(level);
            int[]? lg = r is not null && (r[0] & 0x4000) != 0
                ? [.. Enumerable.Range(0, 4).Select(i => r[15 - i] & 0xFFF)] : null;
            foreach (var ((lvl, word), file) in rom.GfxSlotOverrides)
            {
                if (lvl != level || word is < 12 or > 15) continue;
                lg ??= [0x7F, 0x7F, 0x7F, 0x7F];
                lg[15 - word] = file & 0xFFF;      // w15 = LG1, so the word counts back down
            }
            return lg;
        }

        /// <summary>
        /// The level's layer-3 TILEMAP bypass — the LT3 file, where it lands, and how much of
        /// the window it fills — or null when the level does not bypass the tilemap
        /// (CONTRACT §12b).
        ///
        /// All of it is <b>word 1</b> of the same per-level record, gated by <b>w0 bit 13</b>:
        /// bits 0-11 are the file (0x7F = skip), bits 12-13 the size, bits 14-15 the
        /// destination. Three enables now live in w0 — bit 15 FG/BG/SP, bit 14 layer-3 GFX,
        /// bit 13 layer-3 tilemap — and they are independent.
        ///
        /// NOTE the collision: §7d and §12d both read w1's low bits as the ExAnimation AN1
        /// slot, traced from LM's own loader at $0FF7F0. Both cannot hold a file at once, so a
        /// level using AN1 and a bypassed layer-3 tilemap disagree about what w1 means. Which
        /// reader wins is NOT established — see §12b.
        /// </summary>
        public (int File, int Destination, int Size)? LmLayer3Tilemap(int level)
            => rom.LmGfxRecord(level) is { } r && (r[0] & 0x2000) != 0
               ? (r[1] & 0xFFF, r[1] >> 14 & 3, r[1] >> 12 & 3)
               : null;

        /// <summary>
        /// The level's advanced layer-3 bypass — how its layer 3 scrolls and blends — or null
        /// when the level leaves that to whatever its Layer 3 Option implies (CONTRACT §12b).
        ///
        /// Packed into the spare high nibbles of the same per-level record, so unlike the other
        /// three features here it has no bit in w0: its enable is the low bit of w12's nibble.
        /// Session overrides from <see cref="Rom.Layer3AdvancedOverrides"/> win, and one that
        /// holds null means "this level deliberately has none", which is why the lookup is a
        /// TryGetValue and not a null-coalesce.
        /// </summary>
        public Layer3.Advanced? LmLayer3Advanced(int level)
            => rom.Layer3AdvancedOverrides.TryGetValue(level, out var o) ? o
             : rom.LmGfxRecord(level) is { } r ? Layer3.ReadAdvanced(r) : null;

        /// <summary>True when LM's advanced layer-3 reader is installed — the routine that
        /// gathers those nibbles into $7FC01A-$7FC01C and $145E (CONTRACT §12b). Probed by the
        /// routine itself rather than an address: `LDY #$17 : LDA [$8A],Y : LSR x4 : STA $7FC01A`
        /// is its opening, and the operand it stores to is the confirmation.</summary>
        public bool HasLmLayer3Advanced
            => ScanOperand(rom, [0xA0, 0x17, 0xB7, 0x8A, 0x4A, 0x4A, 0x4A, 0x4A, 0x8F], []) == 0x7FC01A;

        /// <summary>True when LM's layer-3 TILEMAP loader is installed: a `JSL` replaces
        /// vanilla's `LDA $1BE3` at $00A01F, the head of the routine that picks a tilemap out of
        /// Layer3Ptr and runs it through the stripe uploader (CONTRACT §12b). Without it the
        /// record's bit 13 and word 1 are bytes nothing reads.</summary>
        public bool HasLmLayer3Tilemap => rom.ReadByte(0x00A01F) == 0x22;

        /// <summary>True when LM's LAYER-3 GFX loader is installed — the code that reads the
        /// record's bit 14 and its words 12-15 at all (CONTRACT §12b). Probed by its VRAM
        /// destination table at the fixed $0FFA7F inside LM's $0FF780 block: $4C00 $4800 $4400
        /// $4000, one per LG slot. Without it, vanilla's $00A993 streams GFX 28-2B whatever the
        /// record says, so an LG override is an editor-only preview.</summary>
        public bool HasLmLayer3Gfx
            => rom.ReadValue(0x0FFA7F, 4) == 0x48004C00 && rom.ReadValue(0x0FFA83, 4) == 0x40004400;

        /// <summary>True if LM's GFX bypass loader is installed: `JSL $0FF780` (22 80 F7 0F) at
        /// $00AA50 replaces the vanilla level-GFX loader (CONTRACT §7d). Gates the fixed ExGFX
        /// 0x80-0xFF table at $0FF600 — on vanilla/prepped ROMs those bytes are arbitrary data.</summary>
        public bool HasLmGfxLoader => rom.ReadValue(0x00AA50, 4) == 0x0FF78022;

        /// <summary>True when the GFX upload reads FOUR bit planes from the file instead of
        /// synthesizing plane 3 (prep v4 / LM's 4bpp mode). The tell is the instruction after
        /// the plane-2/3 loop's `LDA [$00]`: vanilla masks the byte off (`AND #$00FF`, 29) where
        /// ours goes straight to `XBA` (EB), because it already has both planes. Gates whether
        /// colours 8-15 of a palette row can be painted at all — a 3bpp file has no plane to
        /// hold them.</summary>
        public bool HasGfx4bppUpload => rom.ReadByte(0x00AAE5) == 0xEB;

        /// <summary>
        /// True when the MAIN entrance can use Lunar Magic's "method 2" — a 16px-step position
        /// from $05DE00/$06FC00 instead of an index into vanilla's 8 x 16 grid. The tell is LM's
        /// hook `JSL $05DD30` at $05D97D, which every LM save installs and prep v10 stamps
        /// byte-for-byte (reference/LM_PARITY.md). Same rails, so a ROM saved by either tool
        /// reads the same here.
        /// </summary>
        public bool HasFreeEntrancePositions
            => rom.ReadValue(RomPrep.LmMainEntranceHook, 4) == 0x05DD3022;

        /// <summary>
        /// The same question for SECONDARY entrances: LM's `JSL $03BCE0` at $05D837, plus the
        /// reader at $05DC85 that names where the fifth (Y-high) table lives. The routine is
        /// fixed; the table address is per ROM, read from the reader's operand the way
        /// <see cref="LmMap16Slot"/> reads its slot.
        /// </summary>
        public bool HasFreeSecondaryPositions
            => rom.ReadValue(RomPrep.LmSecondaryHook + 4, 4) == 0x03BCE022
            && rom.ReadByte(RomPrep.LmSecondaryReaders + 5) == 0xBF;

        /// <summary>
        /// True when the MIDWAY entrance can have a position of its own: LM's separate-midway
        /// routine is hooked from $05D9E3 (`JSL`, its first bytes `LSR x4 : REP #$11`). Installed by
        /// LM on demand, not by every save; prep v10 installs it. Per-ROM address, so the hook
        /// operand is followed rather than compared.
        /// </summary>
        public bool HasFreeMidwayPosition
            => rom.ReadByte(RomPrep.LmMidwayHook) == 0x22
            && rom.ReadValue(rom.ReadValue(RomPrep.LmMidwayHook + 1, 3), 4) == 0x4A4A4A4A;

        /// <summary>SNES address of LM's midway flags table (the routine's `LDA long,X` operand at
        /// +0x0A); position, FG/BG and Y-high follow at +0x200/+0x400/+0x600. Only meaningful when
        /// <see cref="HasFreeMidwayPosition"/>.</summary>
        public int LmMidwayTable => rom.ReadValue(rom.ReadValue(RomPrep.LmMidwayHook + 1, 3) + 0x0A, 3);

        /// <summary>SNES address of LM's per-record Y-high table for secondary entrances (the
        /// operand of `LDA long,X` at $05DC85). Only meaningful when
        /// <see cref="HasFreeSecondaryPositions"/>.</summary>
        public int LmSecondaryYHighTable => rom.ReadValue(RomPrep.LmSecondaryReaders + 6, 3);
        /// <summary>...and its FG/BG table (the reader at $05DC8A): bit 7 = FG/BG relative to player.</summary>
        public int LmSecondaryFgBgTable => rom.ReadValue(RomPrep.LmSecondaryReaders + 11, 3);

        /// <summary>
        /// True when LM's level-entry engine is in — the `JSL` at $05DA17 into a routine that opens
        /// `SEP #$30 : REP #$11 : LDX $0E` — so a "FG/BG relative to player" bit in an entrance record
        /// actually moves the camera. Every LM save installs it; prep v10 transplants it (LmLevelEntry).
        /// </summary>
        public bool HasLmFgBgRelative
            => rom.ReadByte(0x05DA17) == 0x22
            && rom.ReadValue(rom.ReadValue(0x05DA18, 3), 4) == 0x11C230E2;

        /// <summary>
        /// True when LM's level-height half of the engine is in — the `JSL` at $05D9A1 into block
        /// B's vertical check (`LDA [$CE] : AND #$20 : STA $0BF5`). It reads a per-level height byte
        /// and a 32-entry LUT; both live in block B, which LM relocates per ROM.
        /// </summary>
        public bool HasLmLevelHeight
            => rom.ReadByte(0x05D9A1) == 0x22
            && rom.ReadValue(rom.ReadValue(0x05D9A2, 3), 4) == 0x2029CEA7;

        /// <summary>SNES address of LM's per-level height byte table — the operand of the
        /// `LDA long,X` 0x5B bytes into the vertical check (after.smc `$108DF7`, operand at +0x5C). The height LUT
        /// (32 words) follows 0x200 bytes later. Only meaningful when <see cref="HasLmLevelHeight"/>.</summary>
        public int LmLevelHeightTable => rom.ReadValue(rom.ReadValue(0x05D9A2, 3) + 0x5C, 3);

        /// <summary>The level's height byte: bits 0-4 index the LUT, bit 5 = extended sprite
        /// stream, bit 7 = "vertical positioning". Zero on a base without the engine.</summary>
        public int LmLevelHeightByte(int level)
            => rom.HasLmLevelHeight ? rom.ReadByte(rom.LmLevelHeightTable + (level & 0x1FF)) : 0;

        /// <summary>Height of a horizontal level in pixels, from LM's LUT: 0x1B0 (27 rows) for a
        /// vanilla base or an unset level, up to 0x3800 for a one-column level.</summary>
        public int LevelHeightPx(int level)
            => rom.HasLmLevelHeight
               ? rom.ReadValue(rom.LmLevelHeightTable + 0x200 + (rom.LmLevelHeightByte(level) & 0x1F) * 2, 2)
               : 0x1B0;

        /// <summary>Map16 rows in a horizontal level's column: <see cref="LevelHeightPx"/> / 16.</summary>
        public int LevelHeightRows(int level) => rom.LevelHeightPx(level) >> 4;

        /// <summary>
        /// True when the GFX upload is the one Lunar Magic recognizes as its 4bpp hack: the
        /// planes-0/1 loop replaced by a verbatim 32-byte-per-tile copy that returns where
        /// vanilla's second loop began ($00AACD = `LDX #$10`, $00AAE1 = `RTS`).
        ///
        /// This is what decides whether LM reads a ROM's GFX files as 4bpp or 3bpp — get it
        /// wrong and LM renders every level as noise while the game is perfectly fine. True for
        /// a v8-prepped base and for any LM 4bpp hack; false for vanilla, a plain LM save, and
        /// our own v4-v7 bases.
        /// </summary>
        public bool HasLmGfx4bppHack
            => rom.ReadByte(0x00AACD) == 0xA2 && rom.ReadByte(0x00AACE) == 0x10
            && rom.ReadByte(0x00AAE1) == 0x60;

        /// <summary>
        /// True when a screen exit's destination can name a level above $0FF — i.e. the level
        /// number's high byte comes from the exit's own flags rather than from the submap the
        /// player is standing on.
        ///
        /// Two independent halves, both required: the decision at $05D7CE must be a JSL (vanilla
        /// has `BEQ +2 : LDA #$01` there), and the object handler must keep the whole X nibble
        /// ($0DA532 = #$0F, vanilla #$01). True for a v7-prepped base AND for any LM-saved ROM —
        /// same sites, same flag layout.
        /// </summary>
        public bool HasExitLevelHighBit
            => rom.ReadByte(0x05D7CE) == 0x22 && rom.ReadByte(0x0DA532) == 0x0F;

        /// <summary>True if LM's VRAM reorganization patch is installed ($0081E2 = JML).
        /// Without it the BG2/BG3 bypass slots are never uploaded (option_vram.htm) —
        /// vanilla and prepped bases lack it, so those slots stay editor-only.</summary>
        public bool HasLmVramPatch => rom.ReadByte(0x0081E2) == 0x5C;

        /// <summary>True if LM's palette engine is installed: a JML hook at $0095E9 replaces the
        /// vanilla JSR UploadSpriteGFX / JSR LoadPalette pair (CONTRACT §7e).</summary>
        public bool HasLmPaletteHook => rom.ReadByte(0x0095E9) == 0x5C;

        /// <summary>
        /// LM custom palette for a level, or null if none. The pointer table entry (0/0xFFFFFF =
        /// none) leads to a RATS-tagged 0x202-byte blob: word 0 = back-area color, then 256
        /// BGR555 words — a full CGRAM image (each row's color 0 is stored as 0/transparent).
        /// </summary>
        public (ushort Back, ushort[] Colors)? LmCustomPalette(int level)
        {
            if (!rom.HasLmPaletteHook) return null;      // vanilla ROMs have unrelated data at $0EF600
            int ptr = rom.ReadValue(LmPaletteTable + level * 3, 3);
            if (ptr == 0 || ptr == 0xFFFFFF) return null;
            int fo = rom.FileOffset(ptr);
            if (fo < 8 || fo + 0x202 > rom.Data.Length) return null;
            if (rom.Data[fo - 8] != 'S' || rom.Data[fo - 7] != 'T' || rom.Data[fo - 6] != 'A' || rom.Data[fo - 5] != 'R')
                return null;
            ushort back = (ushort)(rom.Data[fo] | (rom.Data[fo + 1] << 8));
            var colors = new ushort[256];
            for (int i = 0; i < 256; i++)
                colors[i] = (ushort)(rom.Data[fo + 2 + i * 2] | (rom.Data[fo + 3 + i * 2] << 8));
            return (back, colors);
        }

        /// <summary>
        /// Write a level's LM custom palette (§7e): the same 0x202 blob LmCustomPalette reads
        /// (word 0 = back color, 256 CGRAM words with row color-0 slots zeroed). Overwrites an
        /// existing blob in place (the size is fixed); otherwise allocates a RATS block and
        /// points the $0EF600 table entry at it. Requires LM's palette hook — without the
        /// $0095E9 JML the game would never read the table.
        /// </summary>
        public void WriteLmCustomPalette(int level, ushort back, ushort[] colors)
        {
            if (!rom.HasLmPaletteHook)
                throw new InvalidOperationException("ROM lacks LM's palette ASM — save it in Lunar Magic once first.");
            var blob = new byte[0x202];
            blob[0] = (byte)back; blob[1] = (byte)(back >> 8);
            for (int i = 0; i < 256; i++)
            {
                ushort c = (i & 15) == 0 ? (ushort)0 : colors[i];
                blob[2 + i * 2] = (byte)c; blob[3 + i * 2] = (byte)(c >> 8);
            }

            int ptr = rom.ReadValue(LmPaletteTable + level * 3, 3);
            if (ptr != 0 && ptr != 0xFFFFFF)
            {
                int fo = rom.FileOffset(ptr);
                if (fo >= 8 && fo + 0x202 <= rom.Data.Length &&
                    rom.Data[fo - 8] == 'S' && rom.Data[fo - 7] == 'T' && rom.Data[fo - 6] == 'A' && rom.Data[fo - 5] == 'R')
                {
                    Array.Copy(blob, 0, rom.Data, fo, 0x202);
                    return;
                }
            }
            int addr = RatsWriter.Allocate(rom, blob);
            int tfo = rom.FileOffset(LmPaletteTable + level * 3);
            rom.Data[tfo] = (byte)addr; rom.Data[tfo + 1] = (byte)(addr >> 8); rom.Data[tfo + 2] = (byte)(addr >> 16);
        }
    }

    private static int ScanPixiTable(Rom rom)
    {
        if (!rom.HasPixiSpriteHook) return -1;
        // The dispatch lives in the main hook's target bank; scan only there so the generic
        // `LDA $x,Y : STA $00 : LDA $y,Y : STA $01` pattern can't false-match vanilla code.
        int pcBank = rom.Data[rom.FileOffset(0x0185C3) + 3] & 0x7F;   // JSL operand bank -> PC bank
        int lo0 = pcBank * 0x8000 + rom.HeaderOffset, hi0 = Math.Min((pcBank + 1) * 0x8000 + rom.HeaderOffset, rom.Data.Length) - 10;
        for (int i = lo0; i <= hi0; i++)
            if (rom.Data[i] == 0xB9 && rom.Data[i + 3] == 0x85 && rom.Data[i + 4] == 0x00 &&
                rom.Data[i + 5] == 0xB9 && rom.Data[i + 8] == 0x85 && rom.Data[i + 9] == 0x01)
                return (pcBank << 16) | (rom.Data[i + 1] | rom.Data[i + 2] << 8);
        return -1;
    }

    private static int ScanSpriteBankTable(Rom rom)
    {
        if (rom.Data[rom.FileOffset(0x05D8F5)] != 0x22) return -1;      // vanilla LDA #$07
        int t = rom.ReadValue(0x05D8F6, 3);
        int fo = rom.FileOffset(t);
        if (fo + 10 > rom.Data.Length ||
            rom.Data[fo] != 0x8B || rom.Data[fo + 1] != 0x4B || rom.Data[fo + 2] != 0xAB ||
            rom.Data[fo + 3] != 0xA4 || rom.Data[fo + 4] != 0x0E || rom.Data[fo + 5] != 0xB9 ||
            rom.Data[fo + 8] != 0x85 || rom.Data[fo + 9] != 0xD0) return -1;
        return (t & 0x7F0000) | rom.Data[fo + 6] | (rom.Data[fo + 7] << 8);
    }

    /// <summary>SNES address of the first byte matching <paramref name="pat"/> (-1 = wildcard), or -1.
    /// Computes the LoROM SNES address directly (bank = PC&gt;&gt;15) so it's correct in the expanded
    /// high banks where <see cref="Rom.PcToSnes"/>'s bank-0 mapping would be wrong.</summary>
    private static int ScanCodeAddr(Rom rom, int[] pat)
    {
        for (int i = rom.HeaderOffset; i <= rom.Data.Length - pat.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < pat.Length && ok; j++) ok = pat[j] < 0 || rom.Data[i + j] == pat[j];
            if (!ok) continue;
            int pc = i - rom.HeaderOffset;
            return ((pc >> 15) << 16) | (pc & 0x7FFF) | 0x8000;
        }
        return -1;
    }

    private static int ScanGlobalExAnim(Rom rom)
    {
        int i = GlobalExAnimAnchor(rom);
        if (i < 0) return -1;
        int bank = rom.Data[i - 3];                            // high byte of #bankword = record bank
        int low16 = rom.Data[i + 6] | rom.Data[i + 7] << 8;    // #low16 operand after the LDA
        return bank == 0 ? -1 : (bank << 16) | low16;          // zero bankword = no global list
    }

    /// <summary>File offset of the `STA $01 : STA $C017 : LDA #low16` that follows the engine's
    /// `LDA #bankword : BEQ` — the two immediates that name the global list live at i-4/i-3
    /// (bankword) and i+6/i+7 (low16). -1 when the ROM has no ExAnimation engine.</summary>
    private static int GlobalExAnimAnchor(Rom rom)
    {
        int[] pat = [0x85, 0x01, 0x8D, 0x17, 0xC0, 0xA9];
        int end = rom.Data.Length - pat.Length - 2;
        for (int i = rom.HeaderOffset + 5; i <= end; i++)
        {
            bool ok = true;
            for (int j = 0; j < pat.Length && ok; j++) ok = rom.Data[i + j] == pat[j];
            // preceding `A9 <blo> <bhi> F0 <rel>`: LDA opcode at i-5, BEQ opcode at i-2
            if (ok && rom.Data[i - 5] == 0xA9 && rom.Data[i - 2] == 0xF0) return i;
        }
        return -1;
    }

    extension(Rom rom)
    {
        /// <summary>
        /// Give <paramref name="level"/> these ExAnimation slots (none = remove its record): the
        /// old record's RATS block is released, the new one allocated, and the per-level table
        /// entry repointed — what LM does on every save. Error text when the base has no
        /// ExAnimation engine (prep v11 / an LM-saved ROM).
        /// </summary>
        public string? WriteLevelExAnim(int level, IReadOnlyList<ExAnimation.Slot> slots, int altFileIndex = 0)
        {
            int table = rom.LmExAnimBase;
            if (table < 0) return "this base has no ExAnimation engine — File → Upgrade base to prep v" + RomPrep.Version;
            int entry = rom.FileOffset(table + level * 3);
            int old = rom.ReadValue(table + level * 3, 3);
            if ((old >> 16) != 0) RatsWriter.Release(rom, old);
            int ptr = 0x0000FF;                                                   // FF 00 00 = none
            if (slots.Count > 0) ptr = RatsWriter.Allocate(rom, ExAnimation.Encode(slots, altFileIndex), avoidBankCross: true);
            rom.Data[entry] = (byte)ptr; rom.Data[entry + 1] = (byte)(ptr >> 8); rom.Data[entry + 2] = (byte)(ptr >> 16);
            return null;
        }

        /// <summary>The global list (runs in every level): same record shape, pointed at by the
        /// two immediates LM baked into the engine's setup routine. None = zero bankword.</summary>
        public string? WriteGlobalExAnim(IReadOnlyList<ExAnimation.Slot> slots, int altFileIndex = 0)
        {
            int i = GlobalExAnimAnchor(rom);
            if (i < 0) return "this base has no ExAnimation engine — File → Upgrade base to prep v" + RomPrep.Version;
            int old = rom.LmGlobalExAnimPtr;
            if (old >= 0) RatsWriter.Release(rom, old);
            int ptr = slots.Count > 0 ? RatsWriter.Allocate(rom, ExAnimation.Encode(slots, altFileIndex), avoidBankCross: true) : 0;
            rom.Data[i - 4] = 0; rom.Data[i - 3] = (byte)(ptr >> 16);            // #bankword = bank << 8
            rom.Data[i + 6] = (byte)ptr; rom.Data[i + 7] = (byte)(ptr >> 8);     // #low16
            rom.lmGlobalExAnimPtr = -2;
            ExAnimation.InvalidateGlobal(rom);
            return null;
        }
    }

    private static int ScanExAnimBase(Rom rom)
    {
        int o = ScanOperand(rom, [0xA5, 0xFE, 0xF0, -1, 0x3A, 0x0A, 0x18, 0x65, 0xFE, 0x3A, 0xAA, 0xBF], []);
        return o < 0 ? -1 : o - 1;
    }

    /// <summary>
    /// Find the little-endian 3-byte operand that sits between <paramref name="prefix"/> and
    /// <paramref name="suffix"/> code bytes (-1 in prefix = wildcard). Returns -1 if not found.
    /// This is how LM's per-ROM table addresses are recovered: the surrounding opcodes are
    /// byte-stable, only the baked-in operand address varies.
    /// </summary>
    private static int ScanOperand(Rom rom, int[] prefix, byte[] suffix)
    {
        int end = rom.Data.Length - prefix.Length - 3 - suffix.Length;
        for (int i = rom.HeaderOffset; i <= end; i++)
        {
            bool ok = true;
            for (int j = 0; j < prefix.Length && ok; j++)
                ok = prefix[j] < 0 || rom.Data[i + j] == prefix[j];
            for (int j = 0; j < suffix.Length && ok; j++)
                ok = rom.Data[i + prefix.Length + 3 + j] == suffix[j];
            if (!ok) continue;
            int p = i + prefix.Length;
            return rom.Data[p] | (rom.Data[p + 1] << 8) | (rom.Data[p + 2] << 16);
        }
        return -1;
    }
}
