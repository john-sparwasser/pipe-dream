namespace PipeDream;

/// <summary>
/// Vanilla-base prep: stamp LM-equivalent structures into a verified-vanilla SMW image so
/// the full editing feature set (DM16 tile objects, extended Map16 pages + acts-like table,
/// per-level custom palettes, sprite bank relocation) lights up through the existing
/// LunarMagic.cs detectors with NO Lunar Magic round-trip.
///
/// "LM-equivalent" is doing real work in that sentence: the stamps satisfy OUR detectors at
/// LM's addresses, which is not the same as satisfying LM's. Opening a prepped base in Lunar
/// Magic is a stated REQUIREMENT that does not hold yet — CONTRACT §0 lists the known
/// divergences (v4's upload mechanism, the 4bpp buffer address, the missing VRAM patch).
/// Anything added here should shrink that list, not grow it.
///
/// All inserted 65816 is clean-room authored (Asm.cs) from the documented semantics:
/// CONTRACT §7/§7a-rev/§7b/§7e/§9d/§11 and LEVEL_PIPELINE_NOTES §E/§F. The handler ADDRESSES
/// match the ones the repo already dispatches on (PortedObjectEngine / CONTRACT §9d), so no
/// editor code changes. Deterministic by construction: ExpandTo(1MB) + a fixed stamp table
/// + RATS tags at constant offsets + FixChecksum — same input, byte-identical output.
///
/// Layout (all code in verified-0xFF vanilla freespace, tables in the expansion):
///   $00C17A  JSL $06F5D0 + NOP        Map16 def-lookup hijack (detector: LmMap16Defs)
///   $06F538+ range dispatch (v3) + def math; slots at LM's $06F552/$06F55B/$06F566/$06F56F
///            (ADC #imm / LDY #bank<<8 pairs — the EnsureMap16Tiles repatch targets)
///   $06F5D0  lookup entry (tile<0x200 vanilla $0FBE path; 0x200-0x3FFF extended; else blank)
///   $06F5F0  acts-like remap ($118000 table) — 4 vanilla JSL $00F545 sites repointed here
///   5 dispatch tables  objs 0x22/0x23/0x26/0x27/0x28/0x29 → $0DF08A/8E/130/150/160/FF50
///   ext table          0x02 → $0DE1B0 (secondary exit), 0x03 → $0DE1E0 (screen jump)
///   $05D8F5  JSL $0EF300 sprite stub (bank table $0EF100, level word → $010B)
///   $0095E9  JML $0EFC50 palette hook (thunk $00FF93, apply $0EFC90, table $0EF600)
///   $00A5BF  JSL $0EFC60 second palette hook (the fade-in mode step reloads the palette;
///            without a re-apply the custom staging — incl. the $0701 back color that
///            feeds COLDATA $2132 via $00AE47 — is wiped back to vanilla)
///   pc 0x87FF8 RATS acts-like table (identity &lt;0x200, 0x0130 above), data = $118000
///   pc 0x90000 RATS extended Map16 defs page 0x200-0x2FF (0x1004-word fill), data = $12:8008
/// PC 0x80000-0x87FF7 stays zero — first-fit territory for RomBuilder/palette allocations.
/// </summary>
/// <remarks>
/// This file is the driver and the address catalogue: what a prepped ROM looks like and how
/// Apply gets it there. The stamp lists, one per version, are in RomPrep.Stamps.cs; the 65816
/// routines they stamp are assembled in RomPrep.Code.cs.
/// </remarks>
public static partial class RomPrep
{
    /// <summary>Current prep version. V1 = editing unlocks (DM16/Map16/acts/palette/sprite
    /// banks); V2 adds the in-game GFX stage (Super-GFX-Bypass loader + ExGFX resolver);
    /// V3 widens the Map16 def lookup from one range to four (tiles 0x200-0x3FFF);
    /// V4 makes the GFX upload read four bit planes, so colours 8-15 of a palette row become
    /// paintable (LM's 4bpp mode); V5 stops the Direct-Map16 handlers from walking over the byte
    /// Lunar Magic reads as its level-access flag, which made every prepped base unopenable in
    /// LM (see <see cref="LmAccessFlag"/> and CONTRACT §0); V6 converts the FILES to 4bpp, which
    /// is what v4's reader has been waiting for — a v4/v5 base uploads garbage until it lands.
    /// V7 gives a screen exit a destination bit 8, so an exit can name levels $100-$1FF at all
    /// — vanilla takes that bit from the submap the player happens to be on, which is why a
    /// pipe to $105 was impossible to author (see <see cref="ExitHighByte"/>).
    /// V8 uploads 4bpp the way LM does, which is the only thing that makes LM read a prepped
    /// ROM's graphics as 4bpp rather than as noise; V9 reserves the balance that keeps the
    /// ROM's checksum reading as Super Mario World's own, so LM stops calling it tampered with.
    /// V15 adds the layer-3 TILEMAP bypass, hung off vanilla's own tilemap picker.
    /// V14 adds the LAYER-3 GFX bypass — LG1-LG4 out of the same per-level record, uploaded on
    /// every level load the way LM does it, so a repointed layer-3 slot reaches the console
    /// instead of stopping at the project file (see <see cref="AppendV14Stamps"/>).
    /// Version-keyed stamp lists keep every released version BYTE-FROZEN: a v1 project's
    /// pinned image must reproduce forever (golden-hash tested).</summary>
    public const int Version = 16;

    // ---- pinned addresses (scanner contracts + PortedObjectEngine dispatch) ----
    public const int Map16LookupEntry = 0x06F5D0;  // JSL target at $00C17A
    public const int ActsRemapEntry = 0x06F5F0;    // JSL target at the 4 acts-like sites
    public const int ActsTableSnes = 0x118000;     // pc 0x88000 (RATS tag at 0x87FF8)
    public const int ActsTablePc = 0x88000;
    public const int Map16DefsPc = 0x90008;        // = $12:8008; imm $7008 / bank $12
    public const int Handler22 = 0x0DF08A, Handler23 = 0x0DF08E, Handler26 = 0x0DF130;
    public const int Handler27 = 0x0DF150, Handler28 = 0x0DF160, Handler29 = 0x0DFF50;
    public const int ExtHandler02 = 0x0DE1B0, ExtHandler03 = 0x0DE1E0;
    public const int B27Body = 0x0DF170;           // shared 0x27/0x29 parse+fill body

    /// <summary>
    /// The byte Lunar Magic reads as "the author restricted level access". It is NOT documented
    /// anywhere in LM's help — this was found by bisecting a prepped ROM against LM itself: any
    /// value but $FF and every LM operation dies with "Lunar Magic : Access Denied! The author
    /// of this hack has chosen to restrict level access."
    ///
    /// It sits in the middle of the vanilla $FF gap in bank $0D that our Direct-Map16 handlers
    /// live in, so v1-v4 walked straight over it and were unopenable in LM. LM's own code
    /// generation respects it: in a real LM hack (ShaoBase) the block ends at $0DF0F8 and
    /// leaves this byte $FF. V5 does the same, by branching over it — see CONTRACT §0.
    /// </summary>
    public const int LmAccessFlag = 0x0DF100;
    // ---- V10: entrance positions that are not on the vanilla grid — Lunar Magic's "method 2" ----
    /// <summary>LM's hook for the main entrance: `JSL $05DD30` over vanilla's `LSR : STA $192A`
    /// at $05D97D. The routine is bank-05 free space at a fixed address in every LM save.</summary>
    public const int LmMainEntranceHook = 0x05D97D, LmMainEntranceRoutine = 0x05DD30;
    /// <summary>LM's hook for secondary entrances: `LDA $FE00,Y : TYX : JSL $03BCE0` over
    /// vanilla's `LDA $FE00,Y : AND #$07 : STA $192A` at $05D833. The routine sits in bank 03
    /// free space, again at a fixed address.</summary>
    public const int LmSecondaryHook = 0x05D833, LmSecondaryRoutine = 0x03BCE0;
    /// <summary>Three 5-byte readers the secondary routine calls (`LDA long,X : RTL`): $05FE00,
    /// then the two per-ROM tables — Y high and FG/BG — whose addresses are the operands.</summary>
    public const int LmSecondaryReaders = 0x05DC80;
    /// <summary>Where our two secondary tables go (LM allocates its own; the reader operands
    /// carry the address, so any RATS block does). 0x200 records each.</summary>
    public const int SecondaryExtTagPc = 0x9AFF8, SecondaryExtPc = 0x9B000, SecondaryExtSize = 0xD00;
    public const int SecondaryYHighSnes = 0x13B000, SecondaryFgBgSnes = 0x13B200;
    /// <summary>LM's "separate midway settings": a routine hooked from $05D9E3 (and, 0xA0 bytes
    /// in, from $05D979 for exits that target the midway) reading four per-level tables 0x200
    /// apart — flags, position, FG/BG, Y high. Per-ROM in LM (juz $138008, ShaoBase $128008);
    /// the routine is byte-identical apart from those operands, so ours sits after the secondary
    /// tables with the tables in front of it.</summary>
    public const int LmMidwayHook = 0x05D9E3, LmExitArrivalHook = 0x05D979;
    public const int MidwayTablesSnes = 0x13B400, MidwayRoutineSnes = 0x13BC00;
    /// <summary>LM's midway fix: `STA $01` instead of `STA $95` at $05D9E7 and the following
    /// `JMP $05DA17` NOPped, so the midway screen goes through the shared tail and works on
    /// vertical levels too. Kept because the tail is what applies method 2's screen.</summary>
    public const int LmMidwayStore = 0x05D9E7;

    // ---- V9: a ROM whose checksum still reads as Super Mario World's ----
    /// <summary>RATS tag for the checksum balance; the tunable bytes follow at +8. First-fit
    /// allocation skips RATS-tagged space, so reserving the head of the 0x80000 gap keeps the
    /// balance from being handed out to a level or a palette.</summary>
    public const int BalanceTagPc = 0x80000;
    public const int BalancePc = BalanceTagPc + 8;
    /// <summary>Enough bytes to reach any residue: 0x140 x 0xFF covers the full 16-bit range
    /// several times over, so the balance never runs out of room.</summary>
    public const int BalanceSize = 0x140;

    /// <summary>Super Mario World's own checksum. Lunar Magic knows this value: a self-consistent
    /// checksum that is not this one is what it calls "tampered with" (see
    /// <see cref="RatsWriter.FixChecksum"/>).</summary>
    public const int VanillaChecksum = 0xA0DA;

    /// <summary>Whether the checksum balance is reserved — a RATS block of exactly
    /// <see cref="BalanceSize"/> bytes at <see cref="BalanceTagPc"/>. This is V9's evidence, so
    /// an upgrade knows it still has work to do; without it Apply short-circuits and a v8 base
    /// upgrades to a v9 that has nowhere to put the balance.</summary>
    public static bool HasBalance(Rom rom)
    {
        int tag = BalanceTagPc + rom.HeaderOffset;
        return tag + 8 + BalanceSize <= rom.Data.Length
            && rom.Data[tag] == 0x53 && rom.Data[tag + 1] == 0x54
            && rom.Data[tag + 2] == 0x41 && rom.Data[tag + 3] == 0x52
            && (rom.Data[tag + 4] | (rom.Data[tag + 5] << 8)) == BalanceSize - 1;
    }

    // ---- V8: the 4bpp upload, in the shape Lunar Magic looks for ----
    /// <summary>Vanilla's `LDX #$07` opening the expand-upload's planes-0/1 loop ($00AA80's
    /// first inner loop). V8 replaces the loop, the tile loop and the routine's tail from here
    /// — the same 0x15 bytes LM replaces, at the same address.</summary>
    public const int Gfx4bppLoopSite = 0x00AACD;

    // ---- V7: screen-exit destination bit 8 ----
    /// <summary>Where the level-number high byte is decided. Vanilla inlines "am I on a submap?"
    /// at $05D7CE; this replaces those 4 bytes with a JSL here. The address is the one Lunar
    /// Magic uses for the same hijack, and the vanilla $FF run at $05DC46 is 954 bytes long, so
    /// a base prepped here and a base saved by LM agree on both the site and the data format.
    /// </summary>
    public const int ExitHighByte = 0x05DC50;
    /// <summary>The vanilla `BEQ +2 : LDA #$01` that hardcodes bit 8 from the submap flag.</summary>
    public const int ExitHighByteSite = 0x05D7CE;
    /// <summary>`LDA $0B : AND #$01` in the screen-exit object handler — the mask that throws
    /// away every exit flag but water. V7 widens it to #$0F so the whole X nibble survives into
    /// $19D8,X, which is where <see cref="ExitHighByte"/> reads them from.</summary>
    public const int ExitFlagMask = 0x0DA532;      // the operand byte of that AND

    public const int SpriteStub = 0x0EF300, SpriteBankTable = 0x0EF100;
    /// <summary>LM's level-word mirror (`$05D8E2 → JSL`, 16-bit A): `$0E` → `$010B`, level+1 → `$FE`,
    /// Y = level*2. v10 restamps LM's 12-byte `$0EF300` stub (bank only) and adds this.</summary>
    public const int LmLevelWordStub = 0x0EF550;
    public const int PalTrampoline = 0x0EFC50, PalApply = 0x0EFC90, PalThunk = 0x00FF93;
    public const int PalHook2Stub = 0x0EFC60;      // second hook: re-apply after $00A5BC

    // ---- V2: in-game GFX stage (bank-0F FF tail $0FEF90-$0FFFFF + expansion tables) ----
    public const int GfxArmStub = 0x0FF770;        // JSL target at $0583B8 (LoadLevel)
    public const int GfxLoaderEntry = 0x0FF780;    // JSL target at $00AA50 (HasLmGfxLoader)
    public const int GfxThunks = 0x00FF9A;         // JSR $B8DE:RTL / JSR $AA80:RTL (bank-00 tail)
    public const int GfxBypassRecords = 0x129000;  // 0x20 B/level ×0x200 (RATS at pc 0x90FF8)
    public const int GfxRecordsPc = 0x91000;
    public const int ExGfxPtrTable = 0x138008;     // 3 B/file, files 0x100-0xFFF (RATS pc 0x98000)
    public const int ExGfxPtrPc = 0x98008;
    public const int GfxSlotTab = 0x0FF8A0;        // 8 words: record offset | $2117 page &lt;&lt; 8
    public const int GfxResolve = 0x0FF810;        // file# → $8A-$8C src ptr (near JSR)

    // ---- V4: the 4bpp decompression buffer (V13 moves it back; see GfxBuffer) ----
    /// <summary>Where a decompressed GFX file lands from V4 on. 0x1000 of the free
    /// $7F9CFB-$7FC7FF run; see AppendV4Stamps for why it cannot stay at $7EAD00.</summary>
    public const int Gfx4bppBuffer = 0x7FA000;
    private const int BufBase = Gfx4bppBuffer & 0xFFFF;
    private const byte BufHi = (BufBase >> 8) & 0xFF, BufBank = (Gfx4bppBuffer >> 16) & 0xFF;

    // ---- V13: the buffer back at vanilla's address, and the layer-3 tilemap's own ----
    /// <summary>Where a decompressed GFX file lands from V13 on — vanilla's and LM's $7EAD00.
    /// (V4-V12 used <see cref="Gfx4bppBuffer"/>.)</summary>
    public const int GfxBuffer = 0x7EAD00;

    /// <summary>
    /// The layer-3 TILEMAP's own decompression buffer, from v16 (see the note at the decompress
    /// call in <c>L3Map</c>). A tilemap file is 0x2000 bytes and the shared
    /// <see cref="GfxBuffer"/> has room for 0x1000, so sharing it overran the level's Map16 maps.
    /// v4-v12's GFX buffer address, free since v13 moved that back to vanilla's $7E:AD00.
    /// </summary>
    public const int L3MapBuffer = 0x7FA000;

    // ---- V10: LM's per-level layer-2/3 settings (stamped by LmLevelRender) ----
    /// <summary>LM's per-level layer-2/3 settings byte, one per level, read by the `$05803B` hook
    /// into `$7FC00B` (CONTRACT §10b). Stamped all-zero by `LmLevelRender` from v10.</summary>
    public const int Layer23Settings = 0x0EF310;

    /// <summary>The two bits that make a level's layer 2 a CUSTOM background rather than an object
    /// stream: bit 1 = do not fall through to `$058074` (the object path), bit 2 = skip the
    /// layer-2 map fill, because a custom stream carries its own page plane.</summary>
    public const int Layer23CustomBg = 0x06;

    // ---- V14: layer-3 GFX bypass (LG1-LG4 = record words 15-12, gated by w0 bit 14) ----
    public const int L3SlotTab = 0x0FF8B0;         // 4 words, same shape as GfxSlotTab
    public const int L3Loop = 0x0FF8C0;            // the layer-3 upload pass (near JSR)
    /// <summary>LM's layer-3 VRAM destination table, at the fixed address our
    /// <see cref="LunarMagic.HasLmLayer3Gfx"/> probe reads: `$4C00 $4800 $4400 $4000` for
    /// LG4..LG1, i.e. LG1 at word $4000 and 0x400 words a slot (CONTRACT §12b).</summary>
    public const int L3DestTable = 0x0FFA7F;

    // ---- V15: layer-3 TILEMAP bypass (LT3 = record word 1, gated by w0 bit 13) ----
    /// <summary>LM's two hook sites. `$00A01F` is vanilla's `LDA $1BE3 : BEQ : DEC` — the head
    /// of the routine that picks a tilemap out of Layer3Ptr — and a JSL there is what
    /// <see cref="LunarMagic.HasLmLayer3Tilemap"/> probes. `$00A153` is `LDA #$06 : STA $12`,
    /// the instruction after the level's GFX are up, which is where the file is copied in.</summary>
    public const int L3OptHook = 0x00A01F;
    /// <summary>`JSR $871E : RTS` at the tail of vanilla's tilemap picker — the uploader call
    /// itself. Measured, not assumed: $00A153 (LM's other site) is never executed on the level
    /// load path this has to catch, while $00A01F and this run every time.</summary>
    public const int L3StripeHook = 0x00A041, L3StripeThunk = 0x00FFA2;
    public const int L3Opt = 0x0FF950;             // returns the layer-3 option, vanilla's way
    public const int L3Map = 0x0FF980;             // the LT3 file → VRAM
    /// <summary>LM's tilemap tables, at LM's own addresses. Sizes by the record's size field,
    /// VRAM destination words by its destination field, and the size of the status bar's own
    /// tilemap — the last two are the pair LM's help tells patch authors to edit when they
    /// shorten the status bar (CONTRACT §12b).</summary>
    public const int L3SizeTab = 0x0FFEB4, L3DestWordTab = 0x0FFEBC, L3BarSize = 0x0FFEC4;

    // ---- V16: the ADVANCED layer-3 bypass (initial position, blend, scroll rate) ----
    /// <summary>The nibble reader, inside LM's own `$0FFD80` block (332 B; our prep uses none of that
    /// range). The ADDRESS need not be LM's — <see cref="LunarMagic.HasLmLayer3Advanced"/> scans
    /// for the opening idiom rather than a fixed site — but the idiom itself is LM's instruction
    /// for instruction, so a prepped base answers that probe like an LM-saved one.</summary>
    public const int L3AdvRead = 0x0FFDB0;
    /// <summary>LM's nibble-PAIR helper, also at its own address: reads the high nibble at Y and
    /// the one two bytes lower, glues them into a byte, and leaves Y two lower again.</summary>
    public const int L3AdvPair = 0x0FFE82;
    /// <summary>The engine (level load) plus the per-frame scroll dispatcher and its kind table.
    /// LM's own `$0FFB20` block address (523 B); nothing of ours lives in `$0FFB20-$0FFD7F`.</summary>
    public const int L3Adv = 0x0FFB20;
    /// <summary>Vanilla's per-frame layer-3 scroll site: `LDA $1403 : BEQ +3` (5 bytes), which
    /// is exactly where LM puts its own `JSL : RTS`. Ours dispatches when the advanced group is
    /// on and otherwise unwinds the JSL and jumps back into vanilla, so an unbypassed level
    /// behaves identically.</summary>
    public const int L3ScrollHook = 0x05C40C;
    /// <summary>The per-frame dispatcher's own entry, so the hook above has an address to JSL.</summary>
    public const int L3Scroll = 0x0FFC40;
    /// <summary>Where the resolved 5-bit scroll codes are stashed for the per-frame pass. LM's
    /// own two bytes: it overwrites `$145E`'s high byte (which it has finished reading) with the
    /// horizontal code and uses `$1460` for the vertical.</summary>
    public const int L3CodeH = 0x145F, L3CodeV = 0x1460;
    /// <summary>Layer 3's scroll destinations and layer 1's camera, the pair every rate handler
    /// works from: `$22`/`$24` = layer 3 X/Y, `$1A`/`$1C` = layer 1 X/Y, `$146A`/`$146C` = the
    /// initial offsets the advanced group sets, `$1B78`/`$1B7A` = the scroll-sync mirrors.</summary>
    public const int L3ScrollX = 0x22, L3ScrollY = 0x24;

    /// <summary>The code -> kind table the per-frame dispatcher indexes, after the routines.</summary>
    public const int L3KindTabAddr = 0x0FFCE0;

    /// <summary>The auto-scroll block: the per-frame handler and the load-time seeding, both
    /// axes through one routine (see the comment at the code).</summary>
    public const int L3Auto = 0x0FFD00;

    /// <summary>LM's auto-scroll speed table, its own bytes, indexed by scroll code * 2.</summary>
    public const int L3SpeedTab = 0x0FFE00;

    /// <summary>The four vanilla `JSL $00F545` acts-like call sites (banks 00/01/02),
    /// repointed to our remap so gameplay collision resolves extended tiles.</summary>
    public static readonly int[] ActsCallSites = [0x00F4DD, 0x019533, 0x02961A, 0x02A6EB];

    /// <summary>True when the requested version's structures are present (also true on any
    /// LM-saved ROM — Apply must never stamp over foreign structures).</summary>
    public static bool IsPrepped(Rom rom, int version = Version)
        => rom.HasDm16Hijack && rom.LmMap16Defs.Bank != 0
           && rom.HasLmPaletteHook && rom.LmSpriteBankTable >= 0
           && (version < 2 || rom.HasLmGfxLoader)
           && (version < 3 || rom.HasMap16Range(1))    // the widened lookup ladder
           // V4's clause tests the PROPERTY — the ROM's graphics reach 16 colours — not our
           // particular encoding of it. Testing our instruction at $00AAE5 made IsPrepped false
           // for an LM 4bpp hack, and IsPrepped false is a licence to stamp over it (CONTRACT §0).
           && (version < 4 || rom.HasGfx4bppUpload || rom.HasLmGfx4bppHack)
           && (version < 5 || rom.ReadByte(LmAccessFlag) == 0xFF)
           // V6 leaves no stamp to look for — its evidence is the DATA, so the check is the
           // depth itself. An image whose GFX00 does not resolve (a synthetic test ROM) has
           // nothing to convert and nothing to claim, so it does not fail the test either.
           && (version < 6 || Gfx.RomBpp(rom) == 4 || Gfx.Cached(rom, 0) is null)
           && (version < 7 || rom.HasExitLevelHighBit)
           && (version < 8 || rom.HasLmGfx4bppHack)
           && (version < 9 || HasBalance(rom))
           && (version < 10 || (rom.HasFreeEntrancePositions && rom.HasFreeSecondaryPositions && rom.HasLmFgBgRelative && rom.HasLmLevelHeight))
           // V11: LM's ExAnimation engine is in — ours or LM's own, the property is the same.
           && (version < 11 || rom.LmExAnimBase >= 0)
           // V12: LM's ladder entry at $06F540 (CMP #$0400) — what LM's render engine JSLs to.
           && (version < 12 || rom.ReadValue(0x06F540, 3) == 0x0400C9)
           // V13: the overworld's tile reader takes 4bpp (LM's 4bpp-mode byte at $0480BD).
           && (version < 13 || rom.ReadByte(0x0480BD) == 0x10)
           // V14: LM's layer-3 VRAM destination table, at LM's own fixed address — the same
           // property an LM-saved ROM has, not our particular loader (CONTRACT §0).
           && (version < 14 || rom.HasLmLayer3Gfx)
           // V15: LM's JSL at the head of the tilemap picker.
           && (version < 15 || rom.HasLmLayer3Tilemap)
           // V16: the advanced nibble reader — the same idiom-scan property an LM-saved ROM has.
           && (version < 16 || rom.HasLmLayer3Advanced);

    /// <summary>Stamp the prep into the in-memory image (no-op when already present),
    /// fix the checksum, and reset every LunarMagic scan cache on the Rom. Applying
    /// version 2 to a v1 image restamps the (byte-identical) v1 list + the v2 additions —
    /// that is the upgrade path.</summary>
    public static void Apply(Rom rom, int version = Version)
    {
        if (IsPrepped(rom, version)) return;
        rom.ExpandTo(0x100000);                        // also writes size code at $FFD7
        foreach (var (pc, bytes) in BuildStamps(version))
            Array.Copy(bytes, 0, rom.Data, pc + rom.HeaderOffset, bytes.Length);
        if (version >= 6) ConvertGfxTo4bpp(rom, version);   // data, not a stamp: it allocates
        if (version >= 10) MigrateSecondaryDestinationBit(rom);  // data: depends on the records
        RatsWriter.FixChecksum(rom);
        ResetScanCaches(rom);
    }

    /// <summary>
    /// V10's one data migration, and Lunar Magic's: with $03BCE0 in, a secondary entrance's
    /// destination gets its ninth bit from the record ($05FE00 bit 3) instead of from the submap
    /// the player is on. Vanilla's records at index $100-$1FF are exactly the ones reached from a
    /// submap, so their destinations were $1xx all along — LM sets the bit on every one of them
    /// when it saves (after.smc), and so does this, or every submap secondary would land in $0xx.
    /// </summary>
    private static void MigrateSecondaryDestinationBit(Rom rom)
    {
        for (int i = 0x100; i < 0x200; i++) rom.Data[rom.FileOffset(0x05FE00 + i)] |= 0x08;
    }

    /// <summary>Prep a ROM file in place. The hash gate lives HERE (not in Apply) so unit
    /// tests can Apply to synthetic images. Returns an error message, or null on success.</summary>
    public static string? PrepInPlace(string path, int version = Version)
    {
        if (RomHash.HeaderlessSha256File(path) != RomHash.VanillaUsSha256)
            return "base is not a verified vanilla SMW (US) ROM — prep refused.";
        var rom = Rom.Load(path);
        // The v6 conversion allocates, and this prep does not auto-expand past 1MB. A ROM with
        // no room left is a thing to TELL the user about, not a stack trace out of a save path.
        try { Apply(rom, version); }
        catch (InvalidOperationException e) { return $"prep failed: {e.Message}"; }
        RatsWriter.SaveAs(rom, path);
        return null;
    }

    /// <summary>Where v6 parks the converted graphics: past the prep's own tables (the ExGFX
    /// pointer table ends around pc 0x9AD08) rather than at 0x80000, which is first-fit
    /// territory for RomBuilder's level/palette allocations — converted files would eat it.</summary>
    public const int GfxConvertBase = 0xA0000;

    /// <summary>
    /// V6: store every tile-planar GFX file as 4bpp, which is what v4's four-plane reader has
    /// been waiting for. Until this runs, a v4/v5 base reads 32 bytes per tile out of 24-byte
    /// tiles and uploads garbage — the editor looks fine because it decodes files directly, so
    /// only a built ROM shows it.
    ///
    /// Converting the BASE (rather than the build, or the project's imports) is what makes the
    /// rest follow for free: <see cref="Gfx.RomBpp"/> then reads 4 off the ROM itself, imports
    /// normalise to it, copy-on-write forks come out 4bpp, and the editor's palette row opens up
    /// to colours 8-15. No project-file change, and nothing has to track a per-file depth.
    ///
    /// Only <see cref="Gfx.IsTilePlanar3Bpp"/> ids are touched: the layer-3 2bpp files, the
    /// Mode 7 file and the animation blobs are read by routines that are NOT the tile uploader,
    /// and converting them would corrupt each one. Ascending ids into first-fit space keeps the
    /// result byte-reproducible, so the golden-hash discipline still holds.
    /// </summary>
    private static void ConvertGfxTo4bpp(Rom rom, int version)
    {
        Gfx.InvalidateCache(rom);                       // the stamps above moved the loader
        if (Gfx.RomBpp(rom) != 4)
            for (int id = 0; id < Gfx.Count; id++)
            {
                if (!Gfx.IsTilePlanar3Bpp(id) || Gfx.SourceSnes(rom, id) <= 0) continue;
                byte[] three;
                try { three = Gfx.DecompressFile(rom, id); }
                catch { continue; }                     // unreadable file: leave its pointer alone
                if (three.Length == 0 || three.Length % Gfx.TileBytes(3) != 0) continue;
                byte[] four = Gfx.NormalizeBpp(three, 3, 4, out _);
                if (version >= 8) BakeVanillaSwap(four, id);
                int snes = RatsWriter.Allocate(rom, Gfx.Lz2Compress(four), from: GfxConvertBase);
                rom.Data[rom.FileOffset(Gfx.PtrLow) + id] = (byte)snes;
                rom.Data[rom.FileOffset(Gfx.PtrHigh) + id] = (byte)(snes >> 8);
                rom.Data[rom.FileOffset(Gfx.PtrBank) + id] = (byte)(snes >> 16);
            }
        Gfx.InvalidateCache(rom);                       // depth probe and file cache both stale
    }

    /// <summary>
    /// V8: put vanilla's plane-3 swap into the DATA, because the upload no longer does it.
    ///
    /// Vanilla's expand-upload synthesizes plane 3 as `plane0 | plane1 | plane2` for exactly
    /// four tiles of two files — `$00AA9B-$00AAC6` sets the `$0A` filter to `$FF00` only when
    /// the file is `$01` or `$17` and Y is `$6E`, `$6F`, `$7E` or `$7F`. Everything else on that
    /// path runs with `$0A` = 0, which is why a verbatim 32-byte copy is otherwise an exact
    /// substitute. (Files `$08` on tileset >= `$11` and `$1E` never reach this path at all —
    /// `$00AA96` jumps them to the filter path, which v4 still owns.)
    ///
    /// **Y is not the tile number.** It is `LDY #$7F` counting DOWN while the source pointer
    /// walks forward, so the tile it selects is `$7F - Y`: file tiles `$00`, `$01`, `$10`, `$11`.
    /// Read it as a tile number and the swap lands on four tiles that never wanted it and the
    /// four that did stay wrong — which is exactly what the VRAM parity test caught.
    ///
    /// Tileset does not enter into it here, which is what makes baking exact rather than the
    /// compromise LM makes with its own reconverted files.
    /// </summary>
    private static void BakeVanillaSwap(byte[] four, int id)
    {
        if (id is not (0x01 or 0x17)) return;
        foreach (int tile in (int[])[0x00, 0x01, 0x10, 0x11])
        {
            int b = tile * 32;
            if (b + 32 > four.Length) continue;
            for (int row = 0; row < 8; row++)
                four[b + 16 + row * 2 + 1] =
                    (byte)(four[b + row * 2] | four[b + row * 2 + 1] | four[b + 16 + row * 2]);
        }
    }

    private static void ResetScanCaches(Rom rom)
    {
        rom.lmActsAsBase = rom.lmGfxBypassBase = rom.lmExGfxBase = rom.lmSpriteSizeBase = rom.lmExAnimBase = -2;
        rom.lmGlobalExAnimPtr = -2;
        rom.lmExAnimSetupEntry = rom.lmExAnimProcEntry = -2;
        rom.pixiTable = -2;
        rom.lmSpriteBankTable = -2;
        rom.map16TileCount = -1;
    }

    /// <summary>
    /// A short hash of the CODE this prep stamps, at the current version — the answer to "is
    /// this base's prep actually what today's build produces?", which the version number alone
    /// cannot give.
    ///
    /// MEASURED HAZARD, not a hypothetical: a project pinned "PrepVersion 16" whose base had
    /// been stamped by an earlier build of v16 kept the old layer-3 tilemap buffer, so a fix
    /// that was in the source and in the tests never reached the ROM the user played, and the
    /// upgrade-on-open path saw matching version numbers and did nothing. Prep changes without
    /// a version bump every time a routine is edited, so the version is a release marker, not a
    /// content check.
    ///
    /// Cheap enough to run on every open: <see cref="BuildStamps"/> is pure and touches no file.
    /// </summary>
    public static string StampSignature
    {
        get
        {
            if (stampSignature is not null) return stampSignature;
            var h = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            foreach (var (pc, bytes) in BuildStamps(Version))
            {
                h.AppendData(BitConverter.GetBytes(pc));
                h.AppendData(bytes);
            }
            return stampSignature = Convert.ToHexString(h.GetHashAndReset())[..16];
        }
    }

    private static string? stampSignature;
}
