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
public static class RomPrep
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
    /// <summary>The nibble reader, in LM's own `$0FFD80` block (332 B; our prep uses none of that
    /// range). The ADDRESS need not be LM's — <see cref="LunarMagic.HasLmLayer3Advanced"/> scans
    /// for the opening idiom rather than a fixed site — but the idiom itself is LM's instruction
    /// for instruction, so a prepped base answers that probe like an LM-saved one.</summary>
    public const int L3AdvRead = 0x0FFD80;
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

    private static int Pc(int snes) => Rom.SnesToPc(snes);

    // ---------------------------------------------------------------- stamps
    private static List<(int Pc, byte[] Bytes)> BuildStamps(int version)
    {
        var s = BuildV1Stamps();
        if (version >= 2) AppendV2Stamps(s);
        // V3 restamps the whole Map16-lookup block over V1's — later stamps win, so the
        // single-range lookup is replaced wholesale rather than V1's frozen list being edited.
        if (version >= 3) s.Add((Pc(0x06F538), Map16Lookup(3)));
        if (version >= 4) AppendV4Stamps(s);
        // V5 restamps the Direct-Map16 handler block over V1's, the way V3 restamps the Map16
        // lookup — V1's list stays byte-frozen and a v1 project's pinned image still reproduces.
        if (version >= 5) s.Add((Pc(Handler22), Dm16Handlers(5)));
        if (version >= 7) AppendV7Stamps(s);
        if (version >= 8) AppendV8Stamps(s);
        // V9 reserves the balance; FixChecksum is what fills it, on every write.
        if (version >= 9) s.Add((BalanceTagPc, Rats(new byte[BalanceSize])));
        if (version >= 10) AppendV10Stamps(s);
        if (version >= 11) AppendV11Stamps(s);
        // V12 restamps the Map16 ladder with LM's own bytes (see LmMap16Ladder) — same slot
        // addresses, so V3's scanner contract holds and EnsureMap16Tiles keeps writing there.
        if (version >= 12) s.Add((Pc(0x06F538), LmMap16Ladder()));
        if (version >= 13) AppendV13Stamps(s);
        // V14 restamps the whole GFX block, the way V3/V5/V12 restamp theirs: the layer-3 pass
        // is an extra tail on the same loader, so the earlier versions' bytes stay frozen and
        // only the v14 image differs.
        if (version >= 14) AppendV14Stamps(s);
        if (version >= 15) AppendV15Stamps(s);
        if (version >= 16) AppendV16Stamps(s);
        return s;
    }

    /// <summary>
    /// V16: the ADVANCED layer-3 bypass — the group that answers "I do not want the tileset's
    /// scroll and blend". Everything up to here made the layer-3 PICTURE per-level; this makes
    /// its BEHAVIOUR per-level, which is the half LM's help calls out: "the behavior and scrolling
    /// of the original setting will remain unless you enable the advanced bypass settings".
    ///
    /// The nine spare high nibbles are gathered by the reader; the once-per-level parts (colour
    /// math, subscreen, initial X/Y) ride the `$00A01F` seat v15 already owns; and the scroll
    /// rate is recomputed each frame at vanilla's own layer-3 scroll site, which is LM's hook too.
    /// The GFX loader image changes with the version the way v14/v15 changed it, so earlier
    /// versions' bytes stay frozen.
    /// </summary>
    private static void AppendV16Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        s.Add((Pc(GfxArmStub), GfxCode(16)));
        s.Add((Pc(L3Adv), L3AdvCode()));
        // `LDA $1403 : BEQ +3` (5 bytes) → `JSL dispatcher : RTS`. The dispatcher re-enters
        // vanilla itself when the group is off, so those 5 bytes are the whole hook.
        s.Add((Pc(L3ScrollHook), [0x22, (byte)(L3Scroll & 0xFF), (byte)(L3Scroll >> 8 & 0xFF),
                                  (byte)(L3Scroll >> 16), 0x60]));
    }

    /// <summary>
    /// V14: the layer-3 GFX bypass — LG1-LG4, the record's words 15-12 behind w0 bit 14.
    ///
    /// Vanilla decompresses GFX 28-2B into VRAM word $4000 exactly once, at $00A993 during boot,
    /// and never again — which is why layer-3 graphics are global in an unmodified game and why
    /// LM's help says the bypass only becomes per-level "once you save at least one level or
    /// submap that bypasses the layer 3 GFX". LM's answer is to redo that upload on every level
    /// load from inside the loader it already hooks at $00AA50, falling back to a fixed default
    /// record whose tail is `2B 2A 29 28`. Ours does the same, in the same place, from the same
    /// record: a level that bypasses gets its own files, and a level that does not gets 28-2B
    /// put back — without which one bypassed level would leak its layer 3 into the next.
    ///
    /// The upload is a STRAIGHT copy, not the 3bpp→4bpp expansion the other slots go through:
    /// layer 3 is two bit planes by construction ($00A993 streams 0x400 words per slot into a
    /// 128-tile window), so 0x800 bytes land as they are.
    ///
    /// <see cref="L3DestTable"/> goes down at LM's own fixed address, so a prepped base answers
    /// <see cref="LunarMagic.HasLmLayer3Gfx"/> the same way an LM-saved one does.
    /// </summary>
    private static void AppendV14Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        s.Add((Pc(GfxArmStub), GfxCode(14)));
        s.Add((Pc(L3DestTable), [0x00, 0x4C, 0x00, 0x48, 0x00, 0x44, 0x00, 0x40]));
    }

    /// <summary>
    /// V15: the layer-3 TILEMAP bypass — LT3, record word 1, behind w0 bit 13.
    ///
    /// Vanilla picks a tilemap by (level mode, layer-3 option), runs it through the stripe
    /// uploader at $00871E, and has no per-level say in it at all; two levels of the same mode
    /// and option get the same picture, which is why a "Tileset Specific" level on a tileset
    /// that has no image of its own gets the beta cage. LM's answer is to copy a flat 16-bit
    /// map straight into the window afterwards, and this does the same at the same two sites.
    ///
    /// $00A01F takes over vanilla's `LDA $1BE3 : BEQ : DEC`: our routine returns A = option-1
    /// with Z set when the option is 0, which is the contract the displaced `BEQ` reads, and a
    /// JSL there is also what the capability probe looks for. It does nothing else YET — it is
    /// the seat the advanced scroll group takes when that lands (§12b: LM's $109964 is both).
    ///
    /// $00A153 is where the copy happens, after the level's graphics are up so that the layer-3
    /// GFX v14 uploads are already in VRAM. The DESTINATION is a window offset applied to BOTH
    /// ends: a file byte lands at the window word its own offset names, and "Under Status Bar"
    /// simply starts 0xA0 words in — which is what LM means by "this is already taken into
    /// account when you set the appropriate destination", and why its help warns against
    /// shortening the file to dodge the status bar.
    /// </summary>
    private static void AppendV15Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        s.Add((Pc(GfxArmStub), GfxCode(15)));
        // JSL over `LDA $1BE3 : BEQ +$20 : DEC`, then the same branch one byte shorter — a JSL
        // is a byte longer than the LDA and eats the DEC, so both land on $00A044 as before.
        s.Add((Pc(L3OptHook), [0x22, (byte)(L3Opt & 0xFF), (byte)(L3Opt >> 8 & 0xFF), (byte)(L3Opt >> 16), 0xF0, 0x1F]));
        // The uploader call becomes a call to a bank-00 thunk that runs it and then copies the
        // bypass over the top; the RTS that followed it is untouched and still ends the routine.
        s.Add((Pc(L3StripeHook), [0x20, (byte)(L3StripeThunk & 0xFF), (byte)(L3StripeThunk >> 8 & 0xFF)]));
        s.Add((Pc(L3StripeThunk), [0x20, 0x1E, 0x87,                       // JSR $871E
                                   0x22, (byte)(L3Map & 0xFF), (byte)(L3Map >> 8 & 0xFF), (byte)(L3Map >> 16),
                                   0x60]));
        s.Add((Pc(L3SizeTab), [0x00, 0x20, 0x00, 0x10, 0x00, 0x08, 0x00, 0x00]));
        s.Add((Pc(L3DestWordTab), [0xA0, 0x50, 0x00, 0x50, 0x80, 0x50, 0x00, 0x58]));
        s.Add((Pc(L3BarSize), [0x40, 0x01]));       // 0x140 bytes = the status bar's 32x5 words
    }

    /// <summary>
    /// V13: the decompression buffer back at $7EAD00, and the overworld's 4bpp fixes — all of it
    /// Lunar Magic's own bytes (identical in ShaoBase, DogsOfWar and BigEye; absent from a plain
    /// 3bpp save such as exanim_1, so this IS LM's "4bpp mode" for the overworld).
    ///
    /// The upload loops are not the only readers of a decompressed GFX file. On overworld load,
    /// bank 04 copies eleven GFX1C tiles (water, clouds) out of the buffer into $0AF6 as 4bpp,
    /// and the overworld rotates and DMAs that RAM every frame. That reader has its OWN pointers:
    /// a table of buffer offsets at $048000 with the bank hard-coded to $7E ($048095, $04814F),
    /// and a 3bpp expander at $0480B9. V4 moved the buffer to $7FA000, so from v6 on it read
    /// stale $7EAD00 memory — every base since had a "streaky" overworld (traced in Mesen: VRAM
    /// tiles $75-$7F of page 0, filled from $0AF6). The ExAnimation write side was never involved.
    ///
    /// LM's answer, taken whole: the buffer stays at vanilla's $7EAD00; the table is rescaled
    /// to 32 bytes a tile; the expander copies 16 word rows (`LDA #$0008` → `#$0010`) and its
    /// plane-2 loop is cut (`RTS`); and because a 4bpp file now runs to $7EBCFF, the overworld
    /// sprite tables that lived at $7EB9xx/$7EBAxx move to $7FC5xx/$7FC6xx — 21 two-byte
    /// operand patches in $04F2B8-$04F3D0, every `$7EB9xx`/`$7EBAxx` reference bank 04 has. The
    /// layer-2 tile buffer at $7EB900 that V4 moved out of the way of is overrun by LM too, and
    /// every LM hack lives with it. V4's rescaled GFX00 pointers ($00A879/A87E/A8AE) go back to
    /// vanilla's: LM's `BRA` at $00A873 (which we carry) skips that expander anyway.
    /// </summary>
    private static void AppendV13Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        s.Add((Pc(0x00BA40), [GfxBuffer >> 8 & 0xFF]));                  // CODE_00BA28: LDA #$AD -> $01
        s.Add((Pc(0x00BA44), [GfxBuffer >> 16]));                        //             LDA #$7E -> $02
        s.Add((Pc(0x00A879), Word(0xA9, 0xB3F0)));                       // vanilla again (dead code past LM's BRA)
        s.Add((Pc(0x00A87E), Word(0xA9, 0x7EB3)));
        s.Add((Pc(0x00A8AE), Word(0xA9, 0xB570)));
        s.Add((Pc(GfxArmStub), GfxCode(13)));                            // our bypass loader seeds the same pointer
        // Bank 04, the overworld: LM's bytes.
        s.Add((Pc(0x048000), Convert.FromHexString(OwTileOffsetsLm)));
        s.Add((Pc(0x0480BD), [0x10]));   // LDA #$0008 -> #$0010 : 16 word rows = one 4bpp tile
        s.Add((Pc(0x0480D0), [0x60]));   // LDA [$00],Y -> RTS   : no plane-2 expansion
        foreach (var (at, hi) in OwSpriteRamMoves)
            s.Add((Pc(at), [hi, 0x7F]));                                 // $7EB9xx -> $7FC5xx, $7EBAxx -> $7FC6xx
    }

    /// <summary>$048000-$048085: the overworld's table of buffer offsets for its animated tiles
    /// (three water tiles, then the cloud frames), every entry vanilla's `$AD00 + tile*24`
    /// rescaled to `tile*32`. LM's bytes verbatim (ShaoBase = BigEye).</summary>
    private const string OwTileOffsetsLm =
        "00B720B740B700B520B540B560B580B5A0B5C0B5E0B500B620B640B660B680B6A0B6C0B6E0B600B720B740B760B780B7" +
        "A0B7C0B7E0B700B820B840B860B880B8A0B8C0B8E0B800B920B940B960B980B9A0B9C0B9E0B900BA20BA40BA60BA80BA" +
        "A0BAC0BAE0BA00BB20BB40BB60BB80BBA0BBC0BBE0BB00BC20BC40BC60BC80BCA0BCC0BCE0BC";

    /// <summary>The 21 long-address operands in bank 04 that name the overworld sprite tables
    /// (high byte $B9 or $BA in bank $7E); LM's 4bpp mode moves them up by $0C00 into bank $7F.</summary>
    private static readonly (int At, byte Hi)[] OwSpriteRamMoves =
    [
        (0x04F2B8, 0xC5), (0x04F2BF, 0xC5), (0x04F2C6, 0xC5), (0x04F2CD, 0xC5), (0x04F2D3, 0xC5), (0x04F2D7, 0xC5),
        (0x04F2E0, 0xC5), (0x04F2E7, 0xC6), (0x04F2ED, 0xC6), (0x04F32D, 0xC6), (0x04F33C, 0xC6), (0x04F340, 0xC5),
        (0x04F345, 0xC5), (0x04F39F, 0xC5), (0x04F3A8, 0xC6), (0x04F3AC, 0xC6), (0x04F3B0, 0xC5), (0x04F3C1, 0xC5),
        (0x04F3C5, 0xC5), (0x04F3CB, 0xC5), (0x04F3CF, 0xC5),
    ];

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

    /// <summary>LM's per-level layer-2/3 settings byte, one per level, read by the `$05803B` hook
    /// into `$7FC00B` (CONTRACT §10b). Stamped all-zero by `LmLevelRender` from v10.</summary>
    public const int Layer23Settings = 0x0EF310;

    /// <summary>The two bits that make a level's layer 2 a CUSTOM background rather than an object
    /// stream: bit 1 = do not fall through to `$058074` (the object path), bit 2 = skip the
    /// layer-2 map fill, because a custom stream carries its own page plane.</summary>
    public const int Layer23CustomBg = 0x06;

    /// <summary>
    /// V10: an entrance that can stand anywhere — on Lunar Magic's rails.
    ///
    /// Vanilla stores no position. It stores a screen and two INDICES into the bank-05 tables
    /// ($05D750/58 and $05D730/40), so Mario can only start at one of 8 x 16 spots per screen.
    /// Lunar Magic's "method 2" keeps the record and reinterprets the two index nibbles as 16px
    /// steps, adding a per-level flags byte ($05DE00: bit 5 = method 2, bits 3-4 = X high bits,
    /// bits 6-7 → $192A) and a Y high byte ($06FC00) for the main entrance; for secondary ones
    /// it uses the spare bits of $05FE00 (bit 6 = method 2, bits 4-5 = X high) plus a fifth
    /// table of Y high bytes. See MainEntrance / SecondaryEntrance for the full layout.
    ///
    /// Every LM save installs both routines, at fixed addresses and byte-identical across
    /// after.smc and every reference hack, so this stamps EXACTLY those bytes: $05DD30 hooked
    /// from $05D97D, $03BCE0 hooked from $05D833, and the three readers at $05DC80 whose
    /// operands name the secondary tables — LM's are RATS-allocated per ROM; ours sit at
    /// $13B000/$13B200, and Rom.LmSecondaryYHighTable reads whichever a ROM carries. The three
    /// tables LM initialises come with it: $05DE00 and $06FC00 zeroed (a set bit 5 in $FF would
    /// turn method 2 on everywhere), $06FE00 to LM's $1A — it lands in $13CD, which the midway
    /// tape at $00F2D8 tests for zero, and LM turns vanilla's `STA $13CD` at $05D9C3 into a
    /// load for the same reason.
    ///
    /// A previous v10 used private stubs on the two `JMP $05DA17` sites and its own table. LM
    /// wiped them on save (CONTRACT §0); a mechanism LM does not know is data LM loses.
    /// </summary>
    /// <summary>
    /// V12: Lunar Magic's Map16 ladder, byte-for-byte (after.smc $06F538-$06F5E3). V3 wrote a
    /// ladder of our own at the same SLOT addresses — enough for our scanner and for the vanilla
    /// hook at $00C17A/$00C25C — but LM's render engine (LmLevelRender, v10) JSLs LM's ENTRY at
    /// $06F540 about 150 times and reads the bank from $0B; ours had `BRA` mid-dispatcher there
    /// and stored $05, so every tile it drew resolved to the filler def. Measured in Mesen: the
    /// v10/v11 intro level was one repeated filler tile; a v9 base was fine. Now the entry, the
    /// $06F5D0 wrapper (LDY $0B : STY $05 for the vanilla caller) and the high-range dispatcher
    /// are LM's; only the four slot immediates are ours (V3's defaults — range 0 at $12:8008,
    /// ranges 1-3 empty), since EnsureMap16Tiles owns those bytes. LM's second wrapper at $06F5E4
    /// (its per-site acts-like path, which we do not take) is left out: our acts stub sits at $06F5F0.
    /// </summary>
    private static byte[] LmMap16Ladder()
    {
        var b = Convert.FromHexString(
            "FFFFFFFFFFFFFFFFC900049074C90000902E0AB0410AB01430096900F0A00000840B6B690080A00000840B6B" +
            "300969FFFFA00000840B6B69FF7FA00000840B6B850BAD301929000F0A650B0A0A6900F0A00000840B6B0AB0" +
            "14300969" + "0000A00000840B6B690080A00000840B6B300969FFFFA00000840B6B69FF7FA00000840B6BA8AD30" +
            "19C900109005A900058003A9000D850BB9BE0F6BC22098D40B4B6202008264FFA40B84057A840B6B");
        // Slot immediates ([SCAN] contract, LunarMagic.SlotAddr): slot0 $06F552 = +1A, slot1 $06F55B
        // = +23, slot2 $06F566 = +2E, slot3 $06F56F = +37; ADC imm at +1, LDY imm at +4.
        void Slot(int at, int imm, int bank) { b[at + 1] = (byte)imm; b[at + 2] = (byte)(imm >> 8); b[at + 4] = (byte)bank; b[at + 5] = (byte)(bank >> 8); }
        Slot(0x1A, 0x7008, 0x1200);
        Slot(0x23, 0x0008, 0x0000);
        Slot(0x2E, 0x0008, 0x0000);
        Slot(0x37, 0x0008, 0x0000);
        return b;
    }

    /// <summary>Bisect knob (debug builds of a ROM only, --prep10): which v10 groups to stamp.
    /// 1 entrances, 2 level-entry A/B, 4 height C-F, 8 render. All by default.</summary>
    internal static int V10Groups = 15;

    private static void AppendV10Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        if ((V10Groups & 1) != 0) V10Entrances(s);
        if ((V10Groups & 2) != 0)
        {
            s.Add((Pc(LmLevelEntry.BlockASnes) - 8, Rats(LmLevelEntry.BlockA())));
            s.Add((Pc(LmLevelEntry.BlockBSnes) - 8, Rats(LmLevelEntry.BlockB())));
            foreach (var (site, bytes) in LmLevelEntry.Hooks()) s.Add((Pc(site), bytes));
        }
        if ((V10Groups & 4) != 0)
        {
            s.Add((Pc(LmLevelEntry.BlockCSnes) - 8, Rats(LmLevelEntry.BlockC())));
            s.Add((Pc(LmLevelEntry.BlockDSnes) - 8, Rats(LmLevelEntry.BlockD())));
            s.Add((Pc(LmLevelEntry.BlockESnes) - 8, Rats(LmLevelEntry.BlockE())));
            s.Add((Pc(LmLevelEntry.BlockFSnes) - 8, Rats(LmLevelEntry.BlockF())));
            foreach (var (site, bytes) in LmLevelEntry.HeightHooks()) s.Add((Pc(site), bytes));
            foreach (var (site, bytes) in LmLevelEntry.InPlacePatches()) s.Add((Pc(site), bytes));
        }
        if ((V10Groups & 8) != 0)
        {
            s.Add((Pc(LmLevelRender.Bank1FSnes) - 8, Rats(LmLevelRender.Bank1F())));
            foreach (var (site, bytes) in LmLevelRender.Blocks()) s.Add((Pc(site), bytes));
            foreach (var (site, bytes) in LmLevelRender.InPlace()) s.Add((Pc(site), bytes));
            s.Add((Pc(Rom.LmEntranceLayer2), Enumerable.Repeat((byte)0x20, 0x200).ToArray()));
        }
    }

    private static void V10Entrances(List<(int Pc, byte[] Bytes)> s)
    {
        s.Add((Pc(LmMainEntranceHook), Jsl(LmMainEntranceRoutine)));
        s.Add((Pc(LmMainEntranceRoutine), Convert.FromHexString(
            "4A8D2A19BBBF00FC068504BF00FE068DCD13B900DEAA29C00C2A198A8920F02529180A0A0A0A85942A8595" +
            "B900F20A0A0A0A29700494B900F00A0A0A0A8596A504293F85976B")));

        s.Add((Pc(LmSecondaryHook), [0xB9, 0x00, 0xFE, 0xBB, .. Jsl(LmSecondaryRoutine)]));
        s.Add((Pc(LmSecondaryRoutine), Convert.FromHexString(
            "2280DC05A829870C2A199829084A4A4A850F2285DC058502297F8504228ADC05AA29C08DCD138A29200A0C2A19" +
            "A60EBF00FC0629800404BF00FE06293F0CCD13988940F01F29300A0A0A85942A8595A5014A29700494A5000A0A" +
            "0A0A8596A502293F8597A5023009A5004A4A4A4A85026BA90C8D00019CAE0D9CAF0D9CB00D988910F009A5008D" +
            "F61DEE9C1B988920F006A5018DEA1D982907C907D002A9808DD50D0AF006EECE13EEE91DFA68ABFA68E2305CF79300")));
        s.Add((Pc(LmSecondaryReaders), [0xBF, 0x00, 0xFE, 0x05, 0x6B,
                                        0xBF, .. Long(SecondaryYHighSnes), 0x6B,
                                        0xBF, .. Long(SecondaryFgBgSnes), 0x6B]));
        s.Add((SecondaryExtTagPc, Rats(new byte[SecondaryExtSize])));

        // Separate midway settings — LM's blob (ShaoBase $10FDDF, juz $11FA63, DogsOfWar $12EF20)
        // with its five operands pointed at our tables and at itself.
        var mid = Convert.FromHexString(
            "4A4A4A4AC21148A60EBF088012A8291003018301988920F06429084A4A4A85959829C78D2A19BF088212A829F0" +
            "8596980A0A0A0A8594BF00FC0629808504BF00FE06293F8DCD13BF088612297F0404293F8597A900EBBF08841285" +
            "028920D01FA82903AABF0CD705852098290C4A4AAABF08D705851C9829C00CCD1338686BAD1A1418D0F89C2A1984" +
            "0EA5022901850FFAFA5CB7D805FFFFFFFFFFFF4C4D10012C2A19501A48AD1A14F013B900F422DFFD10A40E9008FA" +
            "FA85015CA1D9056829384A4A6B");
        foreach (var (at, snes) in new[] { (0x0A, MidwayTablesSnes), (0x27, MidwayTablesSnes + 0x200),
                                           (0x48, MidwayTablesSnes + 0x600), (0x57, MidwayTablesSnes + 0x400),
                                           (0xAF, MidwayRoutineSnes) })
            Long(snes).CopyTo(mid, at);
        s.Add((Pc(MidwayRoutineSnes), mid));
        s.Add((Pc(LmMidwayHook), Jsl(MidwayRoutineSnes)));
        s.Add((Pc(LmExitArrivalHook), Jsl(MidwayRoutineSnes + 0xA0)));

        s.Add((Pc(Rom.LmEntranceFlags), new byte[0x200]));
        s.Add((Pc(Rom.LmEntranceYHigh), new byte[0x200]));
        s.Add((Pc(Rom.LmEntranceFgBg), Enumerable.Repeat((byte)0x1A, 0x200).ToArray()));
        s.Add((Pc(0x05D9C3), [0xAD]));                          // STA $13CD → LDA $13CD
        s.Add((Pc(LmMidwayStore), [0x85, 0x01, 0xEA, 0xEA, 0xEA]));

        static byte[] Long(int snes) => [(byte)snes, (byte)(snes >> 8), (byte)(snes >> 16)];
        static byte[] Jsl(int snes) => [0x22, .. Long(snes)];
    }

    /// <summary>
    /// V8: upload 4bpp the way Lunar Magic does, so LM can READ the graphics it is looking at.
    ///
    /// V4 made the upload four-plane by rewriting vanilla's SECOND inner loop in place, keeping
    /// the surrounding routine. It works in the game — but LM decides whether a ROM's GFX files
    /// are 4bpp by looking for its OWN hack, and ours does not look like it. Since v6 stores the
    /// files 4bpp, LM read every 4096-byte file as 3bpp: `-ExportGFX` came out 5456 bytes
    /// (170 tiles widened from 24-byte storage) and every level and 8x8 grid in LM was noise.
    /// Measured, then bisected: grafting exactly these 0x15 bytes out of an LM 4bpp hack
    /// (ShaoBase) into a v7 base flips the export back to 4096 (CONTRACT §0).
    ///
    /// The shape is also simply RIGHT for 4bpp storage, which is why adopting it costs nothing:
    /// a 4bpp tile is 32 bytes of exactly what VRAM wants, so the whole per-tile job is a
    /// verbatim copy, and vanilla's two-loop plane dance — the `$1BB2,X` row mask and the `$0A`
    /// plane-3 synthesis — has nothing left to do. The tile loop closes inside it and the
    /// routine returns where vanilla's second loop used to start.
    ///
    ///     LDX #$10 : [LDA [$00] : STA $2118 : INC $00 : INC $00 : NOP : DEX : BNE] : DEY : BPL
    ///     SEP #$20 : RTS
    ///
    /// The NOP is LM's and is kept deliberately: the byte lengths and branch offsets are what
    /// LM's detector reads, and a shorter loop is a ROM it calls 3bpp again.
    ///
    /// Only the MAIN path moves. The filter path ($00AB0D) keeps v4's rewrite, which uploads
    /// the same bytes by a different route — the parity test walks both.
    /// </summary>
    /// <summary>
    /// V11: Lunar Magic's ExAnimation engine (LmExAnimEngine), byte-for-byte LM's own code
    /// relocated into bank $1E, its two hook helpers, an empty per-level pointer table, the seven
    /// hooks, and $03BCC0 zeroed — what LM installs when the ExAnimation dialog is first used.
    /// A record written by RomBuilder into that table animates in-game exactly as it would in LM.
    /// </summary>
    private static void AppendV11Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        s.Add((Pc(LmExAnimEngine.EngineSnes) - 8, Rats(LmExAnimEngine.Engine())));
        s.Add((Pc(LmExAnimEngine.MvnSnes) - 8, Rats(LmExAnimEngine.Mvn())));
        s.Add((Pc(LmExAnimEngine.ClearSnes) - 8, Rats(LmExAnimEngine.Clear())));
        s.Add((Pc(LmExAnimEngine.TableSnes) - 8, Rats(LmExAnimEngine.EmptyTable())));
        foreach (var (site, bytes) in LmExAnimEngine.Hooks()) s.Add((Pc(site), bytes));
    }

    private static void AppendV8Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        var a = new Asm(Gfx4bppLoopSite);
        a.Label("tile").LdxImm8(0x10);
        a.Label("word").LdaIndLong(0x00).StaAbs(0x2118).IncDp(0x00).IncDp(0x00).Nop()
         .Dex().Bne("word");
        a.Dey().Bpl("tile");
        a.Sep(0x20).Rts().AssertAt(0x00AAE2);
        s.Add((Pc(Gfx4bppLoopSite), a.Bytes()));
    }

    /// <summary>
    /// V7: let a screen exit name a level above $0FF.
    ///
    /// Vanilla has no field for it. $05D7BD reads the destination byte into $0E and then decides
    /// the HIGH byte from the player's own position — `LDA $1F11,Y : BEQ +2 : LDA #$01 : STA $0F`
    /// — so the same exit lands in $005 from the main map and $105 from a submap, and an editor
    /// cannot express "go to $105" at all.
    ///
    /// Two stamps move that decision into the level data:
    ///
    ///   $0DA531  `AND #$01` → `AND #$0F`  — the object handler keeps the whole X nibble in
    ///            $19D8,X instead of just the water bit. Safe by inspection: $19D8,X has NO
    ///            reader anywhere in the vanilla image (only the store at $0DA533), so the
    ///            other three bits are free real estate.
    ///   $05D7CE  `BEQ +2 : LDA #$01` (4 bytes) → `JSL ExitHighByte` (4 bytes, exact fit).
    ///
    /// The flag layout is Lunar Magic's, deliberately: an exit authored here must work in a ROM
    /// LM later touches and vice versa, and the format is the interface. Bit 2 marks an extended
    /// exit — without it the routine falls back, so every vanilla exit in the ROM keeps behaving
    /// exactly as it did.
    ///
    ///   bit0  destination bit 8          bit2  "this exit is extended" (else fall back)
    ///   bit1  use the secondary table    bit3  entrance action → $192A bit 6
    ///   bits4-7  further high bits, only reachable through LM's word form (ext obj 0x02),
    ///            since the vanilla-form handler masks to a nibble.
    ///
    /// The fallback is NOT vanilla's submap test: with the exit tables now data-driven, an
    /// old-style exit takes bit 8 from whether the CURRENT level is a main level ($13BF >= $25),
    /// which is what the submap test was standing in for and does not depend on how the player
    /// arrived. That is also what LM does, so the two agree on untouched exits too.
    /// </summary>
    private static void AppendV7Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        s.Add((Pc(ExitFlagMask), [0x0F]));
        s.Add((Pc(ExitHighByteSite), [0x22, unchecked((byte)ExitHighByte),
                                      unchecked((byte)(ExitHighByte >> 8)), (byte)(ExitHighByte >> 16)]));

        var a = new Asm(ExitHighByte);
        // In: X = the screen index the caller already resolved, A = $1F11,Y (vanilla's answer,
        // which we ignore). Out: A = the level number's high byte, stored to $0F by the caller.
        a.LdaAbsX(0x19D8).BitImm8(0x04).Beq("plain");

        a.Pha().AndImm8(0x02).Lsr().StaAbs(0x1B93);       // bit1 → secondary-exit flag
        a.Pla().Pha().AndImm8(0x08).Asl().Asl().Asl().StaAbs(0x192A);   // bit3 → $192A bit 6
        // High byte = (flags >> 4) << 1 | bit0. The carry carries bit0 across the shifts, which
        // is cheaper than a scratch byte — and every DP byte around here is live ($0E/$0F are
        // mid-assembly, $00-$0D belong to the caller).
        a.Pla().Lsr().Php().Lsr().Lsr().Lsr().Plp().Rol().Rtl();

        // Not extended: no entrance action, and bit 8 says whether THIS level is a main level.
        a.Label("plain").StzAbs(0x192A);
        a.LdaAbs(0x13BF).CmpImm8(0x25).LdaImm8(0x00).Rol().Rtl();
        s.Add((Pc(ExitHighByte), a.Bytes()));
    }

    /// <summary>
    /// V4: make the GFX upload read FOUR bit planes, so a palette row's colours 8-15 are
    /// reachable — the whole point of 4bpp storage.
    ///
    /// Vanilla's expand-upload ($00AA80) sends 32 bytes per tile to VRAM but reads only 24: the
    /// first inner loop copies planes 0/1 verbatim (16 bytes) AND leaves a per-row mask of
    /// (plane0 | plane1) in $1BB2,X; the second reads ONE plane-2 byte per row and synthesizes
    /// the plane-3 byte as (plane2 | plane0 | plane1) ANDed with the $0A filter word — $FF00 for
    /// the handful of vanilla files that want the swap (files $01/$17 on tiles $6E/$6F/$7E/$7F,
    /// file $08 on tileset >= $11, file $1E), $0000 for everything else, which is what makes
    /// plane 3 zero and colours 8-15 unreachable on a vanilla ROM.
    ///
    /// Note WHERE the plane-2 term in that synthesis comes from: the loop's `LDA [$00]` is
    /// 16-bit, so it picks up the next row's byte too, and the `XBA` that follows is what moves
    /// this row's plane 2 into the high half before the mask is OR'd in. Miss that and the
    /// filtered files upload a subtly wrong plane 3 (caught by
    /// v4_uploads_a_converted_file_byte_identically_to_v3).
    ///
    /// So only that SECOND loop is wrong for 4bpp, and the fix keeps its shape exactly — the
    /// plane-2/3 word is read straight from the file, which is already the word VRAM wants
    /// (a 4bpp tile stores planes 2/3 row-interleaved right after planes 0/1), and the two
    /// halves of vanilla's `STA $0C` / `ORA $0C` dance collapse into one `ORA [$00]`:
    ///     LDA [$00] : XBA : ORA $1BB2,X : AND $0A : ORA [$00] : STA $2118 : INC $00 : INC $00
    /// The mask and the filter word still do precisely what they did before: with $0A = 0 the
    /// synthesis drops out and plane 3 is whatever the file says (0 for a converted vanilla
    /// file — byte-identical output), and with $0A = $FF00 it is OR'd on top, so every vanilla
    /// filter case survives untouched. That is what makes this a 22-byte edit instead of a new
    /// upload routine, and why no call site moves.
    ///
    /// 22 bytes replace 27 ($00AAE1-$00AAFB), so the tail is NOPped out to land execution on
    /// the `DEY` at $00AAFC that closes the per-tile loop.
    /// </summary>
    private static void AppendV4Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        // There are TWO copies of that loop, byte-for-byte identical, one per upload
        // implementation: $00AAE1 in the main path, $00AB21 in the FilterSomeRAM path (which is
        // where file $1E and file $08-on-tileset-$11 actually go, so patching only the first
        // leaves exactly the filtered files broken). Both get the same replacement.
        s.Add((Pc(0x00AAE1), Gfx4bppLoop(0x00AAE1, 0x00AAFC)));
        s.Add((Pc(0x00AB21), Gfx4bppLoop(0x00AB21, 0x00AB3C)));

        // The decompression buffer MOVES. A 4bpp file is 0x1000 and the vanilla buffer at
        // $7EAD00 has exactly 0xC00 before it hits the layer-2 Map16 tile buffer at $7EB900 —
        // and that buffer is built one game mode EARLIER (mode $11, $009716 JSL $05801E) than
        // the upload that would trample it (mode $12, $00A5B9), is never rebuilt, and is read
        // every time the camera crosses a 16-pixel boundary. So overrunning it corrupts layer 2
        // for the whole level; there is no ordering that makes 0x1000 fit there.
        //
        // $7FA000 instead: 0x1000 inside the free $7F9CFB-$7FC7FF run (above the Wiggler
        // segment tables, below the layer-1 page buffer at $7FC800), page-aligned, and no bank
        // crossing for the decompressor's [$00],Y writes. Only the pointer SEED moves — every
        // consumer walks $00-$02 and inherits the new base.
        s.Add((Pc(0x00BA40), [BufHi]));                 // CODE_00BA28: LDA #$A0 -> $01
        s.Add((Pc(0x00BA44), [BufBank]));               //             LDA #$7F -> $02

        // The two OTHER 3bpp readers: the GFX0F and GFX00 expanders, which unpack a file into a
        // RAM buffer rather than VRAM ($7F977B for the status-bar sheet, $0BF6 for GFX00). Each
        // has the same shape as the upload — a 2-byte-per-row loop for planes 0/1 then a
        // 1-byte-per-row loop that zero-extends plane 2 — so for 4bpp the second loop becomes a
        // copy of the first, and the whole expansion is a straight 2-bytes-per-row copy.
        s.Add((Pc(0x00A857), Gfx4bppExpand(0x00A857, 0x00A86A, a => a.StaLongX(0x7F977B))));
        s.Add((Pc(0x00A897), Gfx4bppExpand(0x00A897, 0x00A8A9, a => a.StaAbsX(0x0BF6))));

        // Three hardcoded pointers INTO the buffer, in the GFX00 expander: they both move with
        // the buffer and rescale, because they are tile offsets ($6F0 = tile $4A at 24 B/tile
        // becomes $940 at 32; $870 = tile $5A becomes $B40).
        s.Add((Pc(0x00A879), Word(0xA9, BufBase + 0x940)));    // LDA #$A940 : STA $00
        s.Add((Pc(0x00A87E), Word(0xA9, (BufBase + 0x940) >> 8 | BufBank << 8)));  // -> $01/$02
        s.Add((Pc(0x00A8AE), Word(0xA9, BufBase + 0xB40)));    // LDA #$AB40 : STA $00
        // ...and one pure rescale: a conditional 2-tile skip inside GFX0F, $30 at 24 B/tile.
        s.Add((Pc(0x00A83D), Word(0x69, 0x0040)));             // ADC #$0040

        // The prep's own loader seeds the same pointer, so it is restamped over V2's copy the
        // way V3 restamps the Map16 lookup — same length, so every label still resolves.
        s.Add((Pc(GfxArmStub), GfxCode(4)));
    }

    /// <summary>Where a decompressed GFX file lands from V4 on. 0x1000 of the free
    /// $7F9CFB-$7FC7FF run; see AppendV4Stamps for why it cannot stay at $7EAD00.</summary>
    public const int Gfx4bppBuffer = 0x7FA000;
    private const int BufBase = Gfx4bppBuffer & 0xFFFF;
    private const byte BufHi = (BufBase >> 8) & 0xFF, BufBank = (Gfx4bppBuffer >> 16) & 0xFF;

    /// <summary>An opcode plus a 16-bit immediate, for patching one instruction in place.</summary>
    private static byte[] Word(byte op, int v) => [op, (byte)v, (byte)(v >> 8)];

    /// <summary>A 3bpp RAM expander's plane-2 loop rewritten as a 4bpp copy: two bytes a row
    /// instead of one zero-extended byte, which makes it identical to the planes-0/1 loop above
    /// it. <paramref name="store"/> is the only difference between the two sites.</summary>
    private static byte[] Gfx4bppExpand(int at, int fallThrough, Func<Asm, Asm> store)
    {
        var a = new Asm(at);
        a.LdyImm16(0x0008).Label("row").LdaIndLong(0x00);
        store(a).Inx().Inx()
         .IncDp(0x00).IncDp(0x00)      // two bytes consumed per row, not one
         .Dey()
         .Bne("row")
         .PadTo(fallThrough, 0xEA);
        return a.Bytes();
    }

    /// <summary>The 4bpp plane-2/3 inner loop, assembled at its vanilla address so the branch
    /// resolves to the real target, and padded with NOPs out to <paramref name="fallThrough"/> —
    /// the `DEY` that closes the per-tile loop, which must stay put.</summary>
    private static byte[] Gfx4bppLoop(int at, int fallThrough)
    {
        var a = new Asm(at);
        a.LdxImm8(0x07)
         .Label("row")
         .LdaIndLong(0x00)         // authored word: low = plane 2, high = plane 3
         .Xba()                    // plane 2 into the high half, as vanilla does
         .OraAbsX(0x1BB2)          // high |= (plane0|plane1) mask for this row
         .AndDp(0x0A)              // filter word: $FF00 keeps the synthesis, $0000 drops it
         .OraIndLong(0x00)         // put the authored planes back underneath it
         .StaAbs(0x2118)
         .IncDp(0x00).IncDp(0x00)  // two bytes consumed per row, not one
         .Dex()
         .Bpl("row")
         .PadTo(fallThrough, 0xEA);   // fall through to the vanilla DEY
        return a.Bytes();
    }

    // V1 list — BYTE-FROZEN (GoldenPrepV1 test): never edit, only append via versions.
    private static List<(int Pc, byte[] Bytes)> BuildV1Stamps()
    {
        var s = new List<(int, byte[])>
        {
            // Map16 lookup hijack: vanilla `REP #$20 : LDA $0FBE,Y` (5 bytes) → JSL + NOP.
            (Pc(0x00C17A), [0x22, 0xD0, 0xF5, 0x06, 0xEA]),
            // Sprite bank hijack: vanilla `LDA #$07 : STA $D0` → JSL $0EF300 (CONTRACT §11).
            (Pc(0x05D8F5), [0x22, 0x00, 0xF3, 0x0E]),
            // Palette hook: vanilla `JSR UploadSpriteGFX : JSR LoadPalette` (6 bytes) →
            // JML $0EFC50 + NOP NOP; byte $0095E9 == 0x5C is the detector (CONTRACT §7e).
            (Pc(0x0095E9), [0x5C, 0x50, 0xFC, 0x0E, 0xEA, 0xEA]),
            // Second palette hook: a later load-mode step re-runs `JSR UploadSpriteGFX :
            // JSR LoadPalette` at $00A5B9, wiping the custom staging — LM repoints the
            // JSL $05BE8A right after it (observed in every palette-engine LM ROM); ours
            // re-applies then tail-calls the displaced $05BE8A.
            (Pc(0x00A5BF), [0x22, 0x60, 0xFC, 0x0E]),
            // Ext-object table: 0x02/0x03 (vanilla entries are 00 00 00). Entry 0x01 keeps
            // the vanilla screen-jump $0DA53D — identical semantics, no need to move it.
            (Pc(0x0DA115), [0xB0, 0xE1, 0x0D, 0xE0, 0xE1, 0x0D]),
        };

        // Acts-like remap: repoint the four vanilla JSL $00F545 sites at our remap.
        foreach (int site in ActsCallSites)
            s.Add((Pc(site), [0x22, 0xF0, 0xF5, 0x06]));

        // 5 tileset dispatch tables (dispatcher+0xA, entry (obj-1)*3): the 6 reserved
        // objects, vanilla placeholder E3 B3 0D → our handlers (CONTRACT §9d addresses).
        byte[] e22 = [0x8A, 0xF0, 0x0D, 0x8E, 0xF0, 0x0D];                          // 0x22, 0x23
        byte[] e26 = [0x30, 0xF1, 0x0D, 0x50, 0xF1, 0x0D, 0x60, 0xF1, 0x0D, 0x50, 0xFF, 0x0D]; // 0x26-0x29
        foreach (int d in new[] { 0x0DA44B, 0x0DC190, 0x0DCD90, 0x0DD990, 0x0DE890 })
        {
            s.Add((Pc(d + 0x0A + (0x22 - 1) * 3), e22));
            s.Add((Pc(d + 0x0A + (0x26 - 1) * 3), e26));
        }

        s.Add((Pc(0x06F54F), Map16Lookup(1)));
        s.Add((Pc(0x0DE1B0), ExtHandlers()));
        s.Add((Pc(Handler22), Dm16Handlers(1)));
        s.Add((Pc(Handler29), Handler29Code()));
        s.Add((Pc(SpriteStub), SpriteStubCode()));
        s.Add((Pc(PalThunk), [0x20, 0xDA, 0xA9, 0x20, 0xED, 0xAB, 0x6B]));  // JSR UploadSpriteGFX : JSR LoadPalette : RTL
        s.Add((Pc(PalTrampoline), PaletteStubs()));

        // Sprite bank table: 0x200 bytes, all vanilla bank $07.
        var banks = new byte[0x200];
        Array.Fill(banks, (byte)0x07);
        s.Add((Pc(SpriteBankTable), banks));

        // Palette pointer table: 3 bytes/level, all zero = "no custom palette" (§7e).
        s.Add((Pc(LunarMagic.LmPaletteTable), new byte[0x600]));

        s.Add((ActsTablePc - 8, ActsBlock()));
        s.Add((Map16DefsPc - 8, DefsBlock()));
        return s;
    }

    /// <summary>
    /// V2: the in-game GFX stage (CONTRACT §7d as the behavioral contract; LM ROMs used
    /// only to observe which vanilla bytes get displaced). Hook layout:
    ///   $0583B8  JSL ArmStub + NOP — LoadLevel arms $FE = level+1 ONCE per level load.
    ///            $FE is NOT cleared by the loader: the load sequence runs UploadSpriteGFX
    ///            twice (level-prepare $0095E9 and the fade-in GM04Load step), and with the
    ///            cache tests NOPped the second call re-uploads the vanilla files — the
    ///            record must be re-applied then too. This matches LM's own lifecycle
    ///            (record ptr + enabled flag cached until the next LoadLevel re-fetch,
    ///            §7d $7FC006/9). The overworld never reaches the hook: tileset ≥ $FE
    ///            branches away before the FG/BG tail ($00AA1C).
    ///   $00AA50  JSL GfxLoader : RTS (the HasLmGfxLoader detector) over the displaced
    ///            cache-update loop; $00AA06/$00AA47 cache-skip tests → NOP NOP (without
    ///            this, overrides silently no-op when the tileset was already cached).
    ///   $00FF9A  two bank-00 thunks: JSR $00B8DE (LC_LZ2 core; $8A-8C src, [$00] dest)
    ///            and JSR $00AA80 (the vanilla 3bpp expand-upload; needs 8-bit X/Y,
    ///            Y = vanilla file# for its filter cases, Y=0 for ExGFX).
    /// Tables: zeroed $0FF600 (ExGFX 0x80-0xFF), zeroed RATS blocks for the bypass records
    /// ($129000) and ExGFX 0x100+ pointers ($138008) — zero-filled keeps SourceSnes = -1
    /// and LmGfxBypass = null until a build writes real entries.
    /// </summary>
    private static void AppendV2Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        // Arm hook: displaced vanilla `LDA $1925 : CMP #$09` (5 bytes) → JSL + NOP.
        s.Add((Pc(0x0583B8), [0x22, 0x70, 0xF7, 0x0F, 0xEA]));
        // Loader hook over the cache-update loop (10 bytes): JSL : RTS : NOP×5.
        s.Add((Pc(0x00AA50), [0x22, 0x80, 0xF7, 0x0F, 0x60, 0xEA, 0xEA, 0xEA, 0xEA, 0xEA]));
        // Cache-skip tests (SP at $00AA06, FG/BG at $00AA47): BEQ +3 → NOP NOP.
        s.Add((Pc(0x00AA06), [0xEA, 0xEA]));
        s.Add((Pc(0x00AA47), [0xEA, 0xEA]));
        // Bank-00 thunks: the decompressor core and the expand-upload are near-RTS bank-00
        // routines; JSLable wrappers live in the verified-FF tail after V1's PalThunk.
        s.Add((Pc(GfxThunks), [0x20, 0xDE, 0xB8, 0x6B, 0x20, 0x80, 0xAA, 0x6B]));
        s.Add((Pc(Gfx.ExGfx80Table), new byte[0x180]));      // ExGFX 0x80-0xFF pointers: none
        s.Add((Pc(GfxArmStub), GfxCode(2)));
        s.Add((GfxRecordsPc - 8, Rats(new byte[0x200 * 0x20])));   // bypass records, all disabled
        s.Add((ExGfxPtrPc - 8, Rats(new byte[0xF00 * 3])));        // ExGFX 0x100+ pointers: none
    }

    private static byte[] Rats(byte[] data)
    {
        var b = new byte[8 + data.Length];
        b[0] = 0x53; b[1] = 0x54; b[2] = 0x41; b[3] = 0x52;             // "STAR"
        int sm1 = data.Length - 1, inv = sm1 ^ 0xFFFF;
        b[4] = (byte)sm1; b[5] = (byte)(sm1 >> 8); b[6] = (byte)inv; b[7] = (byte)(inv >> 8);
        data.CopyTo(b, 8);
        return b;
    }

    /// <summary>Acts-like table (2 bytes/tile, 0x4000 tiles): identity below 0x200 —
    /// vanilla behavior untouched — and LM's default 0x0130 (solid ground) above (§7b).</summary>
    private static byte[] ActsBlock()
    {
        var d = new byte[0x8000];
        for (int t = 0; t < 0x4000; t++)
        {
            int v = t < 0x200 ? t : 0x130;
            d[t * 2] = (byte)v; d[t * 2 + 1] = (byte)(v >> 8);
        }
        return Rats(d);
    }

    /// <summary>One page of extended Map16 defs (tiles 0x200-0x2FF), LM's default-empty
    /// def word 0x1004 ×4 per tile — the exact fill EnsureMap16Tiles grows with.</summary>
    private static byte[] DefsBlock()
    {
        var d = new byte[0x100 * 8];
        for (int i = 0; i < d.Length; i += 2) { d[i] = 0x04; d[i + 1] = 0x10; }
        return Rats(d);
    }

    /// <summary>
    /// Map16 def lookup + acts-like remap ($06F54F-$06F61x, bank-06 FF tail).
    ///
    /// Lookup entry $06F5D0 — JSL'd from $00C17A, replacing vanilla `REP #$20 : LDA $0FBE,Y`.
    /// Entry state (from the vanilla consumer at $00C143-$00C17A): M 8-bit, X/Y 16-bit,
    /// Y = tile*2, $06 pre-set to $0D (def bank), X precious. Must RTL with 16-bit A = def
    /// address low16 and $06 = def bank; the caller stores A to $04/$05 and re-loads Y.
    ///  - tile &lt; 0x200:  vanilla path — def ptr word from the RAM table $0FBE (bank $0D).
    ///  - 0x200-0xFFF:     def = bank:(imm + tile*8); the ADC imm at fixed $06F552 and LDY
    ///                     #bank&lt;&lt;8 at $06F555 are the EnsureMap16Tiles repatch slots
    ///                     (STY $05 puts the bank in $06; $05 is rewritten by the caller).
    ///  - ≥ 0x1000:        v1/v2: blank fallback $00:8000 (same family LM uses, §7a-rev).
    ///                     v3: three more slots at LM's own addresses — see below.
    ///
    /// V3 adds ranges 1-3 (tiles 0x1000-0x3FFF). A slot cannot cover more than 0x1000 tiles
    /// because `imm + tile*8` is 16-bit addressing into a 32KB LoROM window, so growing past
    /// 0xFFF means MORE slots, which is exactly why LM has a ladder. The slots go at LM's
    /// addresses ($06F55B/$06F566/$06F56F) so one reader and one EnsureMap16Tiles serve both
    /// LM-saved ROMs and ours; the dispatcher in front of them is our own.
    ///
    /// The dispatcher ($06F538-$06F551, filling the freespace right up to slot 0) reads the
    /// range out of the shifts it has to do anyway. Entering with A = tile*2:
    ///   ASL → C = tile bit14 (range ≥ 4, unsupported: our editor stops at 0x3FFF because
    ///         LM's Direct-Map16 page byte is 6 bits); A = tile*4
    ///   ASL → C = tile bit13 (range bit 1), N = tile bit12 (range bit 0); A = tile*8
    /// So two branches pick one of four slots with A already shifted, and the not-taken BCS
    /// leaves C clear for the ADC (the C=1 path CLCs). Slots 4-7 ($06F593+) are never
    /// emitted, which is what makes HasMap16Range(4) false on our bases and true on LM's.
    ///
    /// Acts remap $06F5F0 — JSL'd from the 4 vanilla `JSL $00F545` sites. Entry: M/X 8-bit,
    /// A = tile page (high plane byte), $1693 = tile low byte, X precious. Reads the acts
    /// word at $118000 + tile*2; a sane value (&lt; 0x200) replaces the tile, anything else
    /// keeps it. Exits split back (low → $1693, page → A) and JMLs the vanilla handler —
    /// identical to vanilla for tiles &lt; 0x200 (identity table). The TAY/ASL/TAX/BMI/LDA
    /// long,X + CMP #$0200 sequence is the LmActsAsBase scanner contract.
    /// </summary>
    private static byte[] Map16Lookup(int version)
    {
        var a = new Asm(version >= 3 ? 0x06F538 : 0x06F54F);
        if (version >= 3)
            a.Label("disp")                  // A = tile*2 (16-bit), tile >= 0x200
             .Asl()                          // A = tile*4, C = tile >= 0x4000
             .Bcs("toblank")
             .Asl()                          // A = tile*8, C = range bit1, N = range bit0
             .Bcs("hi")                      // ranges 2-3
             .Bmi("to1")
             .Bra("slot0")                   // range 0 (C clear: the BCS above fell through)
             .Label("hi").Clc()               // ranges 2-3 arrive with C set
             .Bmi("to3")
             .Jmp("slot2")
             .Label("to3").Jmp("slot3")
             .Label("to1").Jmp("slot1")
             .Label("toblank").Jmp("blank")
             .PadTo(0x06F552);               // the dispatcher ends flush against slot 0
        else
            a.Label("extdef")                // A = tile*2 (16-bit)
             .Asl().Asl()                    // tile*8
             .Clc();

        a.Label("slot0")
         .AssertAt(0x06F552).AdcImm16(0x7008)   // [SCAN slot 0] imm — def addr low16
         .AssertAt(0x06F555).LdyImm16(0x1200)   // [SCAN slot 0] bank<<8
         .StyDp(0x05)                        // $06 = bank ($05 is overwritten by the caller)
         .Rtl();

        if (version >= 3)
            // Ranges 1-3, bank 0 = "no defs here yet" — EnsureMap16Tiles fills them in.
            // Each block is 9 bytes, which is exactly LM's slot spacing.
            a.AssertAt(0x06F55B).Label("slot1").AdcImm16(0x0008).LdyImm16(0x0000).StyDp(0x05).Rtl()
             .PadTo(0x06F566).Label("slot2").AdcImm16(0x0008).LdyImm16(0x0000).StyDp(0x05).Rtl()
             .AssertAt(0x06F56F).Label("slot3").AdcImm16(0x0008).LdyImm16(0x0000).StyDp(0x05).Rtl();

        a.Label("blank")                     // out of range: defined blank region
         .LdaImm16(0x8000)
         .LdyImm16(0x0000)
         .StyDp(0x05)
         .Rtl();

        a.PadTo(Map16LookupEntry)
         .Rep(0x20)                          // the replaced vanilla instruction
         .Tya()                              // A = tile*2
         .CmpImm16(0x0400)
         .Bcs("ext")
         .LdaAbsY(0x0FBE)                    // vanilla def-pointer table read
         .Rtl()
         .Label("ext");
        if (version >= 3)
            a.Jmp("disp");
        else
            a.CmpImm16(0x2000)
             .Bcs("toblank")
             .Jmp("extdef")
             .Label("toblank")
             .Jmp("blank");

        // NOTE the hidden accumulator byte: vanilla $00F545 is pure 8-bit code, so the
        // caller's B (high accumulator byte) survives it. SMW's loaders run 8-bit LDA +
        // TAX with 16-bit X everywhere, where TAX transfers B:A — a leaked B poisons every
        // such table index by 0x100 (real bug: garbage entrance state → TIME UP on level
        // 104). The remap therefore saves the caller's B and restores it on exit.
        a.PadTo(ActsRemapEntry)
         .Phx()                              // X is precious at every call site
         .Xba()                              // B = page, A = caller's B
         .Pha()                              // save caller's B
         .LdaAbs(0x1693)                     // A = tile low (B = page)
         .Rep(0x30)                          // A = full tile; 16-bit X for the table index
         .Tay()                              // [SCAN] keep the original tile
         .Asl()                              // [SCAN] tile*2
         .Tax()                              // [SCAN]
         .Bmi("keep")                        // [SCAN] tile >= 0x4000: out of table range
         .LdaLongX(ActsTableSnes)            // [SCAN] acts word
         .CmpImm16(0x0200)                   // [SCAN suffix]
         .Bcc("use")                         // sane acts value (< 0x200): substitute it
         .Label("keep")
         .Tya()
         .Label("use")
         .Sep(0x30)                          // A = final low, B = final page
         .StaAbs(0x1693)                     // low byte back
         .Pla()                              // A = caller's B (B still = final page)
         .Xba()                              // A = page byte (as $00F545 expects), B restored
         .Plx()
         .Jml(0x00F545);
        return a.Bytes();
    }

    /// <summary>
    /// Extended-object handlers (vanilla-free $0DE1B0 gap, bank 0D). Engine convention
    /// (LEVEL_PIPELINE_NOTES §E/§F): 8-bit M/X, record bytes in $0A/$0B/$59, extras read
    /// via LDA [$65],Y then $65 advanced (the loader already stepped past the 3 base
    /// bytes), RTS return. Handlers may clobber $00-$0F and X/Y (loader reloads per object).
    ///
    /// 0x02 $0DE1B0 secondary exit: 2 extra bytes = exit word → $19B8,X / $19D8,X
    ///      (X = screen, exactly the vanilla screen-exit tables at $0DA512).
    /// 0x03 $0DE1E0 screen jump: screen := b2 → $1928 + $1BA1 (the vanilla $0DA53D pair).
    /// </summary>
    private static byte[] ExtHandlers()
    {
        var a = new Asm(ExtHandler02);
        a.LdyImm8(0x00)
         .LdaIndLongY(0x65).StaDp(0x00)      // exit word low
         .Iny()
         .LdaIndLongY(0x65).StaDp(0x01)      // exit word high
         .LdaImm8(0x02)                      // advance $65 past the 2 extras
         .Clc().AdcDp(0x65).StaDp(0x65)
         .LdaDp(0x66).AdcImm8(0x00).StaDp(0x66)
         .LdaDp(0x0A).AndImm8(0x1F).Tax()    // X = screen number
         .LdaDp(0x00).StaAbsX(0x19B8)
         .LdaDp(0x01).StaAbsX(0x19D8)
         .Rts();

        a.PadTo(ExtHandler03)
         .LdaDp(0x0B)                        // screen := b2 (CONTRACT §9c)
         .StaAbs(0x1928)
         .StaAbs(0x1BA1)
         .Rts();
        return a.Bytes();
    }

    /// <summary>
    /// Direct Map16 handlers ($0DF08A-$0DF1xx, the vanilla FF gap; entry addresses pinned
    /// by CONTRACT §9d / PortedObjectEngine). Stream forms per LevelParser:
    ///   0x22 (tile page 0) / 0x23 (page 1): 1 extra byte = tile low; $59 nibbles = w-1/h-1.
    ///   0x26 / 0x28: no-tile directives (RTS).
    ///   0x27: extras = page byte + tile low; page bit7 → +1 "run" byte (sizes stay
    ///         nibble); bits7+6 → +1 height byte and 7-bit width from $59. Page = &amp; 0x3F.
    /// RUN BYTE (decoded behaviorally against the LM reference): it describes a rectangular
    /// MULTI-TILE STAMP tiled across the fill — low nibble = stamp width-1, high nibble =
    /// stamp height-1; cell (c,r) gets tile low + (c mod stampW) + 0x10*(r mod stampH).
    /// Run 0 (and the 0x22/0x23/plain forms) degenerate to a constant fill.
    /// The fill body writes the page via STA [$6E],Y then the low byte through the VANILLA
    /// step-right $0DA95B (screen cross: +$1B0, INC $1BA1), row-stepping with the vanilla
    /// save/reload/down helpers $0DA6B1/$0DA6BA/$0DA97D — byte-for-byte the engine
    /// conventions every vanilla rectangle object uses.
    /// Scratch: $00 w-1, $01 h-1, $02 col ctr, $03 page, $06 col add, $07 col wrap ctr,
    /// $08 page OR (0x27=0 / 0x29=0x40), $09 row add, $0C tile low, $0D row wrap ctr,
    /// $0E raw page then row base, $0F run byte.
    /// </summary>
    private static byte[] Dm16Handlers(int version)
    {
        var a = new Asm(Handler22);
        a.LdaImm8(0x00).Bra("nib");          // 0x22: page 0
        a.AssertAt(Handler23)
         .LdaImm8(0x01)                      // 0x23: page 1, falls through
         .Label("nib")
         .StaDp(0x03)
         .LdyImm8(0x00)
         .LdaIndLongY(0x65).StaDp(0x0C)      // tile low
         .LdaImm8(0x01).JsrL("adv")          // consume 1 extra byte
         .LdaImm8(0x00).StaDp(0x0F)          // no run stamp
         .LdaDp(0x59).AndImm8(0x0F).StaDp(0x00).StaDp(0x02)
         .LdaDp(0x59).Lsr().Lsr().Lsr().Lsr().StaDp(0x01)
         // ---- shared w×h fill ----
         .Label("fill")
         .LdyDp(0x57)                        // packed position
         .Jsr(0x0DA6B1)                      // save plane pointer → $04/$05
         .LdaImm8(0x00).StaDp(0x09)          // row tile-add
         .LdaDp(0x0F).Lsr().Lsr().Lsr().Lsr().StaDp(0x0D)   // row wrap counter
         .Label("row")
         .LdaImm8(0x00).StaDp(0x06)          // column tile-add
         .LdaDp(0x0F).AndImm8(0x0F).StaDp(0x07)             // column wrap counter
         .LdaDp(0x0C).Clc().AdcDp(0x09).StaDp(0x0E)         // row base tile
         .Label("col")
         .LdaDp(0x03).StaIndLongY(0x6E)      // page byte (high plane)
         .LdaDp(0x0E).Clc().AdcDp(0x06)
         .Jsr(0x0DA95B)                      // low byte + step right (screen-cross aware)
         .DecDp(0x07).Bpl("cadv")
         .LdaImm8(0x00).StaDp(0x06)          // stamp column wrap
         .LdaDp(0x0F).AndImm8(0x0F).StaDp(0x07)
         .Bra("cnext")
         .Label("cadv").IncDp(0x06)
         .Label("cnext")
         .DecDp(0x02).Bpl("col")
         .Jsr(0x0DA6BA)                      // reload pointer (+ $1BA1 = $1928)
         .Jsr(0x0DA97D)                      // down one row
         .LdaDp(0x00);
        if (version >= 5)
            // Hop the byte LM reads as its access flag; the FF it leaves behind is never
            // executed. PadTo throws if the code ever grows past it, which is the point.
            a.Bra("lmflag").PadTo(LmAccessFlag + 1).Label("lmflag");
        a.StaDp(0x02)
         .DecDp(0x0D).Bpl("radv")
         .LdaImm8(0x00).StaDp(0x09)          // stamp row wrap
         .LdaDp(0x0F).Lsr().Lsr().Lsr().Lsr().StaDp(0x0D)
         .Bra("rnext")
         .Label("radv")
         .LdaDp(0x09).Clc().AdcImm8(0x10).StaDp(0x09)
         .Label("rnext")
         .DecDp(0x01).Bpl("row")
         .Rts()
         // advance $65 by A (8-bit) — the vanilla stream-pointer idiom ($0DA512)
         .Label("adv")
         .Clc().AdcDp(0x65).StaDp(0x65)
         .LdaDp(0x66).AdcImm8(0x00).StaDp(0x66)
         .Rts();

        a.PadTo(Handler26).Rts();            // 0x26: no-tile directive
        a.PadTo(Handler27)                   // 0x27 entry: FG pages (no OR), body below
         .LdaImm8(0x00).StaDp(0x08).Jmp("b27");
        a.PadTo(Handler28).Rts();            // 0x28: no-tile directive

        a.PadTo(B27Body)
         .Label("b27")                       // shared 0x27/0x29 body ($08 = page OR value)
         .LdyImm8(0x00)
         .LdaIndLongY(0x65).StaDp(0x0E)      // raw page byte
         .AndImm8(0x3F).OraDp(0x08).StaDp(0x03)
         .Iny()
         .LdaIndLongY(0x65).StaDp(0x0C)      // tile low
         .LdaImm8(0x00).StaDp(0x0F)          // default: constant fill
         .LdaDp(0x0E)
         .Bmi("ext")
         .LdaImm8(0x02).JsrL("adv")          // plain: page + low consumed
         .Label("nibsz")
         .LdaDp(0x59).AndImm8(0x0F).StaDp(0x00).StaDp(0x02)
         .LdaDp(0x59).Lsr().Lsr().Lsr().Lsr().StaDp(0x01)
         .Jmp("fill")
         .Label("ext")                       // bit7: +1 run byte at [2]
         .Iny()
         .LdaIndLongY(0x65).StaDp(0x0F)
         .LdaDp(0x0E).AndImm8(0x40).Bne("both")
         .LdaImm8(0x03).JsrL("adv")          // bit7 alone: sizes stay nibble (LM-confirmed)
         .Bra("nibsz")
         .Label("both")                      // bits 7+6: 7-bit width + height byte at [3]
         .Iny()
         .LdaIndLongY(0x65).StaDp(0x01)
         .LdaImm8(0x04).JsrL("adv")
         .LdaDp(0x59).AndImm8(0x7F).StaDp(0x00).StaDp(0x02)
         .Jmp("fill");
        return a.Bytes();
    }

    /// <summary>
    /// Obj 0x29 ($0DFF50): the BG-page DM16 form — same stream shape and fill as 0x27
    /// with the tile page ORed with 0x40 (CONTRACT §9d; behavior confirmed against the LM
    /// reference: the fill goes through the current pass's planes, page 0x40+).
    /// </summary>
    private static byte[] Handler29Code()
    {
        var a = new Asm(Handler29);
        a.LdaImm8(0x40).StaDp(0x08).JmpAbs(B27Body & 0xFFFF);
        return a.Bytes();
    }

    /// <summary>
    /// Sprite-pointer bank stub ($0EF300), JSL'd from $05D8F5 in place of vanilla
    /// `LDA #$07 : STA $D0`. The first ten bytes are the LmSpriteBankTable scanner
    /// contract: PHB PHK PLB / LDY $0E / LDA $F100,Y / STA $D0 (DBR = $0E → table at
    /// $0EF100, one bank byte per level). Then the level word is mirrored to $010B —
    /// PIXI-style per-level consumers and our palette apply read it (CONTRACT §11).
    /// Entry (bank 05 loader): M 8-bit, X/Y 16-bit, $0E = level word.
    /// </summary>
    private static byte[] SpriteStubCode()
    {
        var a = new Asm(SpriteStub);
        a.Phb().Phk().Plb()                  // [SCAN]
         .LdyDp(0x0E)                        // [SCAN] level (16-bit Y in-game)
         .LdaAbsY(0xF100)                    // [SCAN] per-level bank byte
         .StaDp(0xD0)                        // [SCAN]
         .Php()
         .Rep(0x20)
         .Pha()                              // 16-bit LDA $0E would leak the level's high
         .LdaDp(0x0E)                        // byte into B — the entrance decode right
         .StaAbs(0x010B)                     // after TAXes 8-bit loads with 16-bit X, so a
         .Pla()                              // dirty B shifts every table read by 0x100
         .Plp()
         .Plb()
         .Rtl();
        return a.Bytes();
    }

    /// <summary>
    /// Palette engine ($0EFC50/$0EFC90 + bank-00 thunk $00FF93). The hook at $0095E9
    /// displaces vanilla `JSR UploadSpriteGFX : JSR LoadPalette`; both are bank-00 RTS
    /// routines, so a 7-byte thunk in bank 00's verified-FF tail re-runs them (JSR : JSR :
    /// RTL) before the apply. Apply ($0EFC90, plain RTS subroutine so tests can CallNear):
    /// read the 3-byte pointer at $0EF600 + level*3 ($010B = level, set by the sprite stub
    /// during the level-data load that precedes this hook site); 0/FFFFFF = no custom
    /// palette; otherwise copy the 0x202 blob over the vanilla CGRAM staging — word 0 =
    /// back-area color at $0701, then 256 words at $0703 (verified against LoadPalette:
    /// STA $0701 at $00AC3E, LoadColors STA $0703,X). Preserves A/X/Y/P.
    /// </summary>
    private static byte[] PaletteStubs()
    {
        var a = new Asm(PalTrampoline);
        a.Jsl(PalThunk)                      // vanilla UploadSpriteGFX + LoadPalette
         .Jsr(PalApply & 0xFFFF)
         .Jml(0x0095EF);                     // resume vanilla (JSL $05809E)

        a.PadTo(PalHook2Stub)                // JSL'd from $00A5BF (after the 2nd LoadPalette)
         .Jsr(PalApply & 0xFFFF)
         .Jml(0x05BE8A);                     // displaced vanilla JSL target (RTL returns)

        a.PadTo(PalApply)
         .Php()
         .Rep(0x30)
         .Pha().Phx().Phy()
         .LdaAbs(0x010B)                     // level word
         .Asl()
         .Clc()
         .AdcAbs(0x010B)                     // *3
         .Tax()
         .LdaLongX(LunarMagic.LmPaletteTable)     // ptr low16 → $00/$01
         .StaDp(0x00)
         .LdaLongX(LunarMagic.LmPaletteTable + 1) // ptr mid+bank → $01/$02
         .StaDp(0x01)
         .LdaDp(0x00).OraDp(0x01).Beq("done")           // 0x000000 = none
         .LdaDp(0x00).AndDp(0x01).CmpImm16(0xFFFF).Beq("done")  // 0xFFFFFF = none
         .LdyImm16(0x0000)
         .Label("copy")
         .LdaIndLongY(0x00)
         .StaAbsY(0x0701)                    // word 0 → $0701 back, words 1-256 → $0703+
         .Iny().Iny()
         .CpyImm16(0x0202)
         .Bcc("copy")
         .Label("done")
         .Ply().Plx().Pla()
         .Plp()
         .Rts();
        return a.Bytes();
    }

    /// <summary>
    /// V2 GFX stage code ($0FF770-$0FF8xx, bank-0F FF tail).
    ///
    /// ArmStub $0FF770 — JSL'd from LoadLevel $0583B8. Arms $FE (16-bit) = level+1 from
    /// $010B (set by the sprite stub earlier in the load), then re-executes the displaced
    /// `LDA $1925 : CMP #$09`; RTL preserves the compare flags for the branch at $0583BD.
    ///
    /// GfxLoader $0FF780 — JSL'd from the FG/BG tail hook at $00AA50 (entry: 8-bit M/X,
    /// DBR=0). Runs the displaced cache-update loop, then, when $FE is armed and the
    /// level's bypass record (base $129000, 0x20 B/level) has bit15 of w0 set, uploads
    /// each non-0x7F slot: resolve file → src ptr $8A-$8C, decompress to $7E:AD00 (dest
    /// [$00], not advanced by the core), point VRAM ($2115=#$80 defensive, $2116=0,
    /// $2117 from SlotTab), and run the vanilla expand-upload with Y = file# (vanilla
    /// files keep their filters) or 0 (ExGFX). The armed/record fetch emits the
    /// LmGfxBypassBase scanner idiom (A5 FE F0 ?? 3A 0A×5 AA BF base) literally.
    ///
    /// Resolve (near JSR) — A = file 0x000-0xFFF → carry clear + $8A-$8C, or carry set
    /// (skip): &lt;0x34 vanilla tables $00B992/B9C4/B9F6; 0x80-0xFF fixed $0FF600;
    /// 0x100+ via the LmExGfxBase scanner idiom (38 E9 00 01 85 8A 0A 18 65 8A AA BF);
    /// 0x34-0x7F and null/FFFFFF pointers skip.
    ///
    /// SlotTab — 8 words, low byte = record byte-offset (FG1=w7 … SP4=w8), high byte =
    /// the $2117 VRAM page (FG1 $00/FG2 $08/BG1 $10/FG3 $18/SP1 $60/SP2 $68/SP3 $70/
    /// SP4 $78). Derivation: $00A9E7/$00AA28 fill $04-$07 backwards (STA $04,X with X
    /// counting 3→0 while the GFXLIST index counts up), and the upload loop pairs file
    /// $04,X with page DATA_00A9D6/DATA_00A9D2[X] — so GFXLIST index 0 (FG1/SP1, record
    /// word 7/11) lands in $07, i.e. X=3, i.e. page table entry 3. See
    /// slot_table_pairs_each_slot_with_the_vanilla_vram_page.
    /// Scratch: $03-$04 record base word, $06-$07 file# word, $0E/$0F slot recOff/vramHi,
    /// $00-$02 dest ptr, $8A-$8C src ptr — chosen OUTSIDE what the reused vanilla routines
    /// write (decompressor: $8A-$8F; expander: $0A/$0C + INC $00), and with 16-bit dp
    /// operands owning BOTH their bytes (a 16-bit ADC $03 reads $03-$04).
    /// </summary>
    private static byte[] GfxCode(int version)
    {
        var a = new Asm(GfxArmStub);
        a.Rep(0x20)
         .LdaAbs(0x010B)
         .IncA()
         .StaDp(0xFE)                        // arm: $FE-$FF = level+1
         .Sep(0x20)
         .LdaAbs(0x1925)                     // displaced vanilla bytes
         .CmpImm8(0x09)
         .Rtl();

        a.PadTo(GfxLoaderEntry)
         .LdxImm8(0x03)                      // displaced cache-update loop
         .Label("cache")
         .LdaDpX(0x04)
         .StaAbsX(0x0105)
         .Dex()
         .Bpl("cache")
         .Rep(0x30)
         .LdaDp(0xFE)                        // [SCAN] armed level+1
         .Beq("noL3")                        // [SCAN] not a level load — layer 3 too
         .DecA()                             // [SCAN]
         .Asl().Asl().Asl().Asl().Asl()      // [SCAN] level * 0x20
         .Tax()                              // [SCAN]
         .LdaLongX(GfxBypassRecords)         // [SCAN operand] record w0
         .AndImm16(0x8000)
         .Beq("exit")                        // record not enabled
         .StxDp(0x03)                        // record byte offset — NOT $0C: the vanilla
         .LdxImm16(0x0000)                   // expander writes $0A/$0C (and INC $00s the
         .Label("slot")                      // dest); the decompressor writes $8A-$8F.
         .Phx()                              // X = slot index * 2 (stack-preserved)
         .LdaLongX(GfxSlotTab)               // lo = record offset, hi = $2117 page
         .StaDp(0x0E)
         .AndImm16(0x00FF)
         .Clc().AdcDp(0x03)
         .Tax()
         .LdaLongX(GfxBypassRecords)         // slot word
         .AndImm16(0x0FFF)
         .CmpImm16(0x007F)
         .Beq("skip")                        // slot uses the tileset default
         .StaDp(0x06)                        // file# word ($06/$07 — clear of the $03/$04
         .JsrL("resolve")                    // base word the 16-bit ADC $03 reads)
         .Bcs("skip")                        // not inserted / invalid id
         .Sep(0x20)
         .StzDp(0x00)                        // decompress dest: $7E:AD00, or V4-V12's $7F:A000
         .LdaImm8(version is >= 4 and < 13 ? BufHi : (byte)(GfxBuffer >> 8 & 0xFF)).StaDp(0x01)
         .LdaImm8(version is >= 4 and < 13 ? BufBank : (byte)(GfxBuffer >> 16)).StaDp(0x02)
         .Jsl(GfxThunks)                     // LC_LZ2 core ($8A-$8C → [$00])
         .LdaImm8(0x80).StaAbs(0x2115)       // defensive: word-increment VRAM mode
         .StzAbs(0x2116)
         .LdaDp(0x0F).StaAbs(0x2117)         // slot's VRAM page
         .Sep(0x10)                          // expander requires 8-bit X/Y
         .LdyImm8(0x00)
         .LdaDp(0x07)
         .Bne("upload")                      // ExGFX 0x100+: no vanilla filter
         .LdaDp(0x06)
         .CmpImm8(0x34)
         .Bcs("upload")                      // ExGFX 0x80-0xFF: no filter
         .Tay()                              // vanilla file keeps its filter cases
         .Label("upload")
         .Jsl(GfxThunks + 4)                 // vanilla expand-upload ($00AA80)
         .Label("skip")
         .Rep(0x30)
         .Plx()
         .Inx().Inx()
         .CpxImm16(0x0010)
         .Bcc("slot")
         .Label("exit");
        // The layer-3 pass runs for EVERY armed level, bypassed or not — it is what puts 28-2B
        // back after a level that repointed them. Only reachable when $FE is armed, so it can
        // recompute the record base from it.
        if (version >= 14) a.Jsr(L3Loop);
        a.Label("noL3")
         .Sep(0x30)
         .Rtl();

        // ---- Resolve: A = file# → $8A-$8C source pointer, carry set = skip ----
        a.PadTo(GfxResolve)
         .Label("resolve")
         .CmpImm16(0x0034)
         .Bcs("r1")
         .Tax()                              // vanilla: three parallel byte tables
         .Sep(0x20)
         .LdaLongX(Gfx.PtrLow).StaDp(0x8A)
         .LdaLongX(Gfx.PtrHigh).StaDp(0x8B)
         .LdaLongX(Gfx.PtrBank).StaDp(0x8C)
         .Rep(0x20)
         .Clc()
         .Rts()
         .Label("r1")
         .CmpImm16(0x0080)
         .Bcc("bad")                         // 0x34-0x7F: invalid ids
         .CmpImm16(0x0100)
         .Bcc("e80")
         // 0x100+ — the LmExGfxBase scanner idiom (kept ahead of the 0x80 path so a
         // linear disasm sweep still carries 16-bit M through it)
         .Sec()                              // [SCAN]
         .SbcImm16(0x0100)                   // [SCAN]
         .StaDp(0x8A)                        // [SCAN]
         .Asl()                              // [SCAN]
         .Clc()                              // [SCAN]
         .AdcDp(0x8A)                        // [SCAN] *3
         .Tax()                              // [SCAN]
         .LdaLongX(ExGfxPtrTable)            // [SCAN operand] ptr low+mid
         .StaDp(0x8A)
         .Sep(0x20)
         .LdaLongX(ExGfxPtrTable + 2)        // bank
         .StaDp(0x8C)
         .Bra("chk")
         .Label("e80")                       // 0x80-0xFF via the fixed $0FF600 table
         .Sec().SbcImm16(0x0080)
         .StaDp(0x8A)
         .Asl()                              // *3 (ASL leaves carry clear: A ≤ 0x7F)
         .AdcDp(0x8A)
         .Tax()
         .LdaLongX(Gfx.ExGfx80Table)         // ptr low+mid (16-bit)
         .StaDp(0x8A)
         .Sep(0x20)
         .LdaLongX(Gfx.ExGfx80Table + 2)     // bank
         .StaDp(0x8C)
         .Label("chk")                       // 8-bit M: reject 000000 / FFFFFF pointers
         .LdaDp(0x8A).OraDp(0x8B).OraDp(0x8C).Beq("badr")
         .LdaDp(0x8A).AndDp(0x8B).AndDp(0x8C).CmpImm8(0xFF).Beq("badr")
         .Rep(0x20)
         .Clc()
         .Rts()
         .Label("badr")
         .Rep(0x20)
         .Label("bad")
         .Sec()
         .Rts();

        a.PadTo(GfxSlotTab)
         .Db(0x0E, 0x00,                     // FG1 (w7) → VRAM page $00
             0x0C, 0x08,                     // FG2 (w6) → $08
             0x0A, 0x10,                     // BG1 (w5) → $10
             0x08, 0x18,                     // FG3 (w4) → $18
             0x16, 0x60,                     // SP1 (w11) → $60
             0x14, 0x68,                     // SP2 (w10) → $68
             0x12, 0x70,                     // SP3 (w9) → $70
             0x10, 0x78);                    // SP4 (w8) → $78
        if (version < 14) return a.Bytes();

        a.PadTo(L3SlotTab)
         .Db(0x1E, 0x40,                     // LG1 (w15) → VRAM page $40 (word $4000)
             0x1C, 0x44,                     // LG2 (w14) → $44
             0x1A, 0x48,                     // LG3 (w13) → $48
             0x18, 0x4C);                    // LG4 (w12) → $4C

        // ---- Layer-3 pass: LG1-LG4 → VRAM $4000, 0x400 words each, no expansion ----
        a.PadTo(L3Loop)
         .Rep(0x30)
         .LdaDp(0xFE)                        // armed level+1 (the caller checked it)
         .DecA()
         .Asl().Asl().Asl().Asl().Asl()
         .Tax()
         .StxDp(0x03)                        // record byte offset — the bit-15 path may have
         .LdxImm16(0x0000)                   // branched here without setting it
         .Label("l3slot")
         .Phx()                              // X = slot index * 2 (stack-preserved)
         .LdaLongX(L3SlotTab)
         .StaDp(0x0E)                        // lo = record offset, hi = $2117 page
         .LdaDp(0x03).Tax()
         .LdaLongX(GfxBypassRecords)         // w0
         .AndImm16(0x4000)
         .Beq("l3van")                       // layer-3 bypass off: the vanilla file
         .LdaDp(0x0E)
         .AndImm16(0x00FF)
         .Clc().AdcDp(0x03)
         .Tax()
         .LdaLongX(GfxBypassRecords)         // slot word
         .AndImm16(0x0FFF)
         .CmpImm16(0x007F)
         .Bne("l3have")                      // 0x7F = this slot keeps its vanilla file
         .Label("l3van")
         .Plx().Phx()                        // recover the slot index X clobbered
         .Txa().Lsr()
         .Clc().AdcImm16(0x0028)             // LG(n) defaults to GFX 0x28+n
         .Label("l3have")
         .StaDp(0x06)
         .JsrL("resolve")
         .Bcs("l3skip")                      // not inserted / invalid id
         .Sep(0x20)
         .StzDp(0x00)                        // decompress dest: the shared buffer
         .LdaImm8(GfxBuffer >> 8 & 0xFF).StaDp(0x01)
         .LdaImm8(GfxBuffer >> 16).StaDp(0x02)
         .Jsl(GfxThunks)                     // LC_LZ2 core ($8A-$8C → [$00])
         .LdaImm8(0x80).StaAbs(0x2115)       // word-increment VRAM mode
         .StzAbs(0x2116)
         .LdaDp(0x0F).StaAbs(0x2117)         // slot's VRAM page
         .Rep(0x30)
         .LdxImm16(0x03FF)                   // 0x400 words — vanilla's own $00A9AC loop, which
         .LdyImm16(0x0000)                   // copies 2bpp straight through
         .Label("l3copy")
         .LdaIndLongY(0x00)
         .StaAbs(0x2118)
         .Iny().Iny()
         .Dex()
         .Bpl("l3copy")
         .Label("l3skip")
         .Rep(0x30)
         .Plx()
         .Inx().Inx()
         .CpxImm16(0x0008)
         .Bcc("l3slot")
         .Rts();
        if (version < 15) return a.Bytes();

        // ---- The layer-3 option, for the $00A01F hook ----
        // Vanilla's own three instructions, moved here so the JSL that replaces them can also
        // Entry is 8-bit M and X ($00A001's SEP #$20, and $00A007's `LDX #$07`), and the caller
        // reads only Z. Vanilla's own three instructions, moved here so the JSL that replaces them
        // can become the advanced group's entry later; the tilemap copy happens at the END of this
        // routine instead (L3StripeThunk), so this must keep answering with the real option — a 0
        // here would make the caller skip the upload AND the copy with it.
        // V16 takes the seat this comment reserved. The reader runs on EVERY armed load, so the
        // four RAM variables always describe the level actually being entered rather than the
        // last one that used the group; the engine runs only when $145E bit 0 is set. The answer
        // handed back is unchanged either way — the advanced group does NOT override the layer-3
        // option (LM's `AND #$0003` at $1099A4 is the initial-X index, not the option), so an
        // unbypassed level goes through here exactly as it did at v15.
        a.PadTo(L3Opt);
        if (version >= 16)
            // $00 IS LIVE ACROSS THIS SEAT. $009FC0 puts the level's mode*3 there and $00A026 —
            // two instructions after we return — adds it to the option to index Layer3Ptr. The
            // reader's nibble-pair helper and the engine's code resolution both use $00 as
            // scratch (LM's own helper does too, but LM reaches it from its GFX loader, not from
            // here), so leaving it clobbered indexed the tilemap pointer table with a wrong
            // entry: the stripe uploader then ran a pointer that is not a script, and the whole
            // screen came up dark. MEASURED — it is why the advanced group looked like it
            // "did not work" rather than like it broke something. $01 and $02 are safe: both are
            // WRITTEN further down that routine before anything reads them.
            a.LdaDp(0x00).Pha()
             .Jsr(L3AdvRead)
             .LdaAbs(0x145E).AndImm8(0x01).Beq("l3vanopt")
             .Jsr(L3Adv)                                 // the engine, in its own block
             .Label("l3vanopt")
             .Pla().StaDp(0x00);
        a.LdaAbs(0x1BE3)
         .DecA()
         .Tax()
         .Inx()
         .Rtl();

        // ---- The LT3 file → the layer-3 window, called from L3StripeThunk ----
        // Scratch is deliberately only $00-$02 and $8B-$8E — the bytes vanilla's own
        // `JSL $00BA28` clobbers four instructions earlier, so nothing here can be relying on
        // them. Everything else rides the stack. $00 survives the decompressor (only the
        // EXPANDER advances it, and this path does not use one), so it still points at the
        // buffer when the copy loop reads through it.
        a.PadTo(L3Map)
         .Php()
         .Phb()
         .Phk().Plb()                        // $010B and the PPU ports are absolute reads here
         .Rep(0x30)
         .LdaAbs(0x010B)                     // the level being loaded (see L3Opt)
         .AndImm16(0x01FF)
         .Asl().Asl().Asl().Asl().Asl()
         .Tax()
         .LdaLongX(GfxBypassRecords)         // w0
         .AndImm16(0x2000)                   // the tilemap enable, distinct from the other two
         .Bne("mgo")
         .Label("mdone0")                    // a near exit: the far one is out of branch range
         .Plb()
         .Plp()
         .Rtl()
         .Label("mgo")
         .Inx().Inx()
         .LdaLongX(GfxBypassRecords)         // w1: file in 0-11, size in 12-13, destination 14-15
         .Pha()
         .AndImm16(0x0FFF)
         .CmpImm16(0x007F)
         .Beq("mpop")                        // 0x7F = Skip File
         .JsrL("resolve")
         .Bcs("mpop")                        // not inserted / invalid id
         .Sep(0x20)
         // The tilemap gets its OWN buffer from v16 on. It is 0x2000 bytes — twice any GFX file —
         // and the shared $7E:AD00 buffer only has room to $7EBCFF, which a 4bpp file fills
         // exactly (§V13). Decompressing 0x2000 there ran to $7ECCFF and wrote straight through
         // the layer-2 map at $7E:B900, its page plane at $7E:BD00, and the LAYER-1 Map16 map at
         // $7E:C800 — which renders as one Map16 tile repeating in a grid across the level.
         // MEASURED, not deduced: `Layer3VramBoundsTests` pins the reach of both buffers.
         // $7F:A000 is v4-v12's old GFX buffer, freed when v13 moved back to $7E:AD00; 0x2000
         // bytes there end at $7FBFFF, clear of LM's record cache at $7FC006 and of the Map16
         // page plane at $7F:C800.
         .StzDp(0x00)
         .LdaImm8((version >= 16 ? L3MapBuffer : GfxBuffer) >> 8 & 0xFF).StaDp(0x01)
         .LdaImm8((version >= 16 ? L3MapBuffer : GfxBuffer) >> 16).StaDp(0x02)
         .Jsl(GfxThunks)                     // LC_LZ2 core: the map is an ordinary GFX file
         .Rep(0x30)
         .Pla()
         .Pha()
         // Destination word, and the SAME offset into the file — a file byte belongs at the
         // window word its own offset names, so both ends move together. That is what makes
         // "Under Status Bar" leave the status bar alone without the file being reshaped.
         .Xba()
         .Lsr().Lsr().Lsr().Lsr().Lsr().Lsr()
         .AndImm16(0x0003)
         .Asl()
         .Tax()
         .LdaLongX(L3DestWordTab)
         .StaDp(0x8B)                        // the VRAM word to start at
         .Sec()
         .SbcImm16(0x5000)
         .Asl()                              // window word offset → byte offset into the file
         .StaDp(0x8D)
         // length = size - that offset, in words, minus one for the DEX/BPL loop
         .Pla()
         .Xba()
         .Lsr().Lsr().Lsr().Lsr()
         .AndImm16(0x0003)
         .Asl()
         .Tax()
         .LdaLongX(L3SizeTab)
         .Sec()
         .SbcDp(0x8D)
         .Beq("mdone")                       // "Do not use", or a file the offset swallows
         .Bmi("mdone")
         .Lsr()
         .DecA()
         .Tax()                              // X = words - 1
         .Sep(0x20)
         .LdaImm8(0x80).StaAbs(0x2115)       // word-increment VRAM mode
         .Rep(0x20)
         .LdaDp(0x8B).StaAbs(0x2116)         // 16-bit: $2116/$2117 take the word address
         .LdyDp(0x8D)
         .Label("mcopy")
         .LdaIndLongY(0x00)
         .StaAbs(0x2118)
         .Iny().Iny()
         .Dex()
         .Bpl("mcopy")
         .Label("mdone")
         .Plb()
         .Plp()
         .Rtl()
         .Label("mpop")
         .Pla()
         .Bra("mdone");
        return a.Bytes();
    }

    /// <summary>The code -> kind table the per-frame dispatcher indexes, after the routines.</summary>
    public const int L3KindTabAddr = 0x0FFCE0;

    /// <summary>
    /// V16's own block, in LM's `$0FFB20` range and stamped separately from the GFX loader image.
    /// Separate because the loader block would otherwise grow over `L3DestTable` ($0FFA7F) and the
    /// v15 tables ($0FFEB4+) and, being stamped after them, pad them away. Ends before $0FFEB4.
    ///
    /// Emitted in ASCENDING address order, which `Asm` requires — `PadTo` only ever moves forward.
    /// </summary>
    private static byte[] L3AdvCode()
    {
        var a = new Asm(L3Adv);
        // ---- The engine + the per-frame dispatcher ----
        a.PadTo(L3Adv)
         .Label("engine")                                // 8-bit M/X in, from L3Opt
         // Let layer 3 scroll AT ALL. $13D5 is vanilla's "this level's layer 3 does not move"
         // flag: $00A012 sets it for every (mode, option) whose entry in the table at $009F88 is
         // negative, and $05BC3F is its only reader — it gates the JSR that reaches the
         // per-frame scroll routine, so with the flag up our dispatcher at $05C40C is never
         // called. MEASURED: 0 hits over a whole level, and forcing $13D5 to 0 from outside
         // turned it on. That is exactly the levels the advanced group exists for — a custom
         // layer 3 on a tileset whose own layer 3 is a static picture — so overriding the
         // scroll means overriding this too. We run after $00A012, so clearing wins.
         .StzAbs(0x13D5)
         // colour math: $7FC01A bit 2 → $40 bit 2 (LM uses TSB/TRB; same result)
         .LdaLong(0x7FC01A).AndImm8(0x04).Beq("nocg")
         .LdaDp(0x40).OraImm8(0x04).StaDp(0x40)
         .Bra("sub")
         .Label("nocg")
         .LdaDp(0x40).AndImm8(0xFB).StaDp(0x40)
         .Label("sub")
         // layer 3 to subscreen: bit 3 → $0D9D bit 10, mirrored to both screen-designation ports
         .LdaLong(0x7FC01A).AndImm8(0x08).Beq("xpos")
         .Rep(0x20)
         .LdaAbs(0x0D9D).AndImm16(0xFFFB).OraImm16(0x0400).StaAbs(0x0D9D)
         .StaAbs(0x212C).StaAbs(0x212E)
         .Sep(0x20)
         .Label("xpos")
         // initial X: bits 0-1 are an INDEX. index*0x40, except index 3 which is $100 — which is
         // why LM's own list reads 00/04/08/10 and skips 0C.
         .LdaLong(0x7FC01A).AndImm8(0x03)
         .Rep(0x20).AndImm16(0x00FF)
         .Xba().Lsr().Lsr()
         .CmpImm16(0x00C0).Bne("setx")
         .LdaImm16(0x0100)
         .Label("setx")
         .StaAbs(0x146A)
         .Sep(0x20)
         // initial Y: ($7FC01C : $145E & F8) is Y*8 in a 14-bit signed field. ASL ASL shifts the
         // two scroll bits out of the top and lands the sign in bit 15; CMP/ROR then shifts back
         // with that sign carried in, so the net is Y*16 with the sign preserved. LM's own trick.
         .LdaLong(0x7FC01C).Xba()
         .LdaAbs(0x145E).AndImm8(0xF8)
         .Rep(0x20)
         .Asl().Asl()
         .CmpImm16(0x8000).Ror()
         .StaAbs(0x146C)
         .Sep(0x20)
         // resolve the two 5-bit codes and stash them for the per-frame pass
         .LdaAbs(L3CodeH).AndImm8(0x0F).StaDp(0x00)                  // $145F low nibble
         .LdaLong(0x7FC01C).AndImm8(0x80).Beq("hdone")
         .LdaDp(0x00).OraImm8(0x10).StaDp(0x00)
         .Label("hdone")
         .LdaAbs(L3CodeH).Lsr().Lsr().Lsr().Lsr().StaDp(0x01)        // $145F high nibble
         .LdaLong(0x7FC01C).AndImm8(0x40).Beq("vdone")
         .LdaDp(0x01).OraImm8(0x10).StaDp(0x01)
         .Label("vdone")
         .LdaDp(0x00).StaAbs(L3CodeH)                                // safe: $145F is read out
         .LdaDp(0x01).StaAbs(L3CodeV)
         .Rts();

        // ---- One axis: A = camera, $06 = initial offset, X = code. Out: A = layer-3 position ----
        a.Label("axis")                                  // 16-bit M/X
         .StaDp(0x08)
         .Sep(0x20).LdaLongX(L3KindTabAddr).Rep(0x20).AndImm16(0x00FF)
         .Beq("hold")                                    // 0 = None: stay at the offset
         .CmpImm16(0x0001).Beq("oneone")                 // 1 = Constant: 1:1 with layer 1
         .CmpImm16(0x0008).Beq("fast")                   // 8 = Fast: 1.2x
         .CmpImm16(0x0009).Bcs("hold")                   // 9 = auto-scroll, not ported
         .Sec().SbcImm16(0x0001).Tax()                   // kinds 2-7 → shift 1-6
         .LdaDp(0x08)
         .Label("shift")
         .Lsr().Dex().Bne("shift")
         .Clc().AdcDp(0x06).Rts()
         .Label("oneone")
         .LdaDp(0x08).Clc().AdcDp(0x06).Rts()
         .Label("hold")
         .LdaDp(0x06).Rts()
         .Label("fast")                                  // offset + cam + cam/5, LM's divider use
         .LdaDp(0x08).StaAbs(0x4204)
         .Sep(0x20).LdaImm8(0x05).StaAbs(0x4206).Rep(0x20)
         .Xba().Xba()                                    // the divide needs 16 cycles to settle
         .LdaDp(0x08).Clc().AdcDp(0x06).AdcAbs(0x4214)
         .Rts();

        // ---- The per-frame dispatcher, long-called from vanilla's own scroll site ----
        a.PadTo(L3Scroll)
         .Label("scroll")                                // 8-bit M/X, from bank 05
         .LdaAbs(0x1931).Bmi("bail")                     // LM's own guard
         .LdaAbs(0x145E).AndImm8(0x01).Beq("bail")
         .Rep(0x30)
         .LdaAbs(0x146A).StaDp(0x06)
         .Sep(0x20).LdaAbs(L3CodeH).Rep(0x20).AndImm16(0x00FF).Tax()
         .LdaDp(0x1A).JsrL("axis").StaDp(L3ScrollX)
         .LdaAbs(0x146C).StaDp(0x06)
         .Sep(0x20).LdaAbs(L3CodeV).Rep(0x20).AndImm16(0x00FF).Tax()
         .LdaDp(0x1C).JsrL("axis").StaDp(L3ScrollY)
         .Sep(0x20)
         .LdaAbs(0x145E).AndImm8(0x02).Beq("sdone")      // scroll-sync fix
         .Rep(0x20)
         .LdaDp(L3ScrollX).StaAbs(0x1B78)
         .LdaDp(L3ScrollY).StaAbs(0x1B7A)
         .Sep(0x20)
         .Label("sdone")
         .Sep(0x30).Rtl()
         // Not enabled: drop the JSL's return address and re-enter vanilla where it would have
         // gone, so the unbypassed path keeps its exact behaviour instead of merely a similar one.
         .Label("bail")
         .Pla().Pla().Pla()
         .LdaAbs(0x1403).Beq("vanfall")
         .Jml(0x05C494)
         .Label("vanfall")
         .Jml(0x05C414);

        // code → kind: 0 hold, 1 one-to-one, 2-7 shift 1-6, 8 fast, 9 auto (not ported).
        // The ladder is LM's: 02 03 18 19 04 1A are >>1 .. >>6, which is exactly the dropdown
        // order Medium, Medium 2, Medium 3, Medium 4, Slow, Slow 2 (Layer3.ScrollCodes).
        var kinds = new byte[0x1B];
        Array.Fill(kinds, (byte)1);                      // LM sends its unused codes 12-17 here
        kinds[0x00] = 0; kinds[0x01] = 1;
        kinds[0x02] = 2; kinds[0x03] = 3; kinds[0x18] = 4; kinds[0x19] = 5;
        kinds[0x04] = 6; kinds[0x1A] = 7; kinds[0x05] = 8;
        for (int c = 0x06; c <= 0x11; c++) kinds[c] = 9;
        a.PadTo(L3KindTabAddr).Db(kinds);

        // ---- The reader. Near-called from the engine seat; 8-bit M/X in and out ----
        // LM loads the record pointer from its own $7FC006 cache, which we do not keep, so this
        // builds it from $FE (armed level + 1) the way the layer-3 GFX pass does. From the
        // `LDY #$17` on it is LM's opening instruction for instruction, which is what the
        // capability probe scans for.
        a.PadTo(L3AdvRead)
         .Rep(0x30)
         .LdaDp(0xFE).DecA()
         .Asl().Asl().Asl().Asl().Asl()                  // level * 0x20
         .Clc().AdcImm16(GfxBypassRecords & 0xFFFF)
         .StaDp(0x8A)
         .Sep(0x30)
         .LdaImm8(GfxBypassRecords >> 16).StaDp(0x8C)
         .LdyImm8(0x17).LdaIndLongY(0x8A).Lsr().Lsr().Lsr().Lsr().StaLong(0x7FC01A)
         .Dey().Dey().Jsr(L3AdvPair).StaLong(0x7FC01C)
         .LdyImm8(0x07).Jsr(L3AdvPair).StaLong(0x7FC01B)
         .LdyImm8(0x1F).Jsr(L3AdvPair).Xba()
         .Dey().Dey().Jsr(L3AdvPair)
         .Rep(0x20).StaAbs(0x145E).Sep(0x20)             // nib(w15..w12), 16-bit
         .Rts();

        // The nibble-pair helper, byte for byte LM's.
        a.PadTo(L3AdvPair)
         .LdaIndLongY(0x8A).AndImm8(0xF0).StaDp(0x00)
         .Dey().Dey()
         .LdaIndLongY(0x8A).Lsr().Lsr().Lsr().Lsr().OraDp(0x00)
         .Rts();
        return a.Bytes();
    }
}