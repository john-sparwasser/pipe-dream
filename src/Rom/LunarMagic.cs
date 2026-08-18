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
            return (bank << 16) | ((imm + tile * 8) & 0xFFFF);
        }

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
            int newImm = ((dataSnes & 0xFFFF) - startTile * 8) & 0xFFFF;     // range 0: $7008
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
            : rom.lmSpriteSizeBase = ScanOperand(rom, [0x4A, 0x4A, 0x29, 0x03, 0xEB, 0xC8, 0xC8, 0xB7, 0xCE, 0x88, 0x88,
                                                       0x08, 0xC2, 0x10, 0xDA, 0xAA, 0x98, 0x18, 0x7F], []);

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
        /// are overlaid on the record.
        /// </summary>
        public ushort[]? LmGfxBypass(int level)
        {
            ushort[]? w = null;
            if (rom.LmGfxBypassBase >= 0)
            {
                int fo = rom.FileOffset(rom.LmGfxBypassBase + level * 0x20);
                if (fo >= 0 && fo + 0x20 <= rom.Data.Length)
                {
                    var r = new ushort[16];
                    for (int i = 0; i < 16; i++) r[i] = (ushort)(rom.Data[fo + i * 2] | (rom.Data[fo + i * 2 + 1] << 8));
                    if ((r[0] & 0x8000) != 0) w = r;
                }
            }
            foreach (var ((lvl, word), file) in rom.GfxSlotOverrides)
            {
                if (lvl != level || word is < 0 or > 15) continue;
                if (w is null) { w = new ushort[16]; Array.Fill(w, (ushort)0x7F); w[0] = 0x807F; }   // all slots "default"
                w[word] = (ushort)((w[word] & ~0xFFF) | (file & 0xFFF));
            }
            return w;
        }

        /// <summary>True if LM's GFX bypass loader is installed: `JSL $0FF780` (22 80 F7 0F) at
        /// $00AA50 replaces the vanilla level-GFX loader (CONTRACT §7d). Gates the fixed ExGFX
        /// 0x80-0xFF table at $0FF600 — on vanilla/prepped ROMs those bytes are arbitrary data.</summary>
        public bool HasLmGfxLoader => rom.ReadValue(0x00AA50, 4) == 0x0FF78022;

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
        int[] pat = [0x85, 0x01, 0x8D, 0x17, 0xC0, 0xA9]; // STA $01 / STA $C017 / LDA #low16
        int end = rom.Data.Length - pat.Length - 2;
        for (int i = rom.HeaderOffset + 5; i <= end; i++)
        {
            bool ok = true;
            for (int j = 0; j < pat.Length && ok; j++) ok = rom.Data[i + j] == pat[j];
            // preceding `A9 <blo> <bhi> F0 <rel>`: LDA opcode at i-5, BEQ opcode at i-2
            if (!ok || rom.Data[i - 5] != 0xA9 || rom.Data[i - 2] != 0xF0) continue;
            int bank = rom.Data[i - 3];                            // high byte of #bankword = record bank
            int low16 = rom.Data[i + 6] | rom.Data[i + 7] << 8;    // #low16 operand after the LDA
            if (bank == 0) return -1;                              // zero bankword = no global list
            return (bank << 16) | low16;
        }
        return -1;
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
