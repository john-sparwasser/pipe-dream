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
    /// Version-keyed stamp lists keep every released version BYTE-FROZEN: a v1 project's
    /// pinned image must reproduce forever (golden-hash tested).</summary>
    public const int Version = 10;

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
    // ---- V10: entrance positions that are not on the vanilla grid ----
    /// <summary>The two `JMP $05DA17` at the end of the main and midway position setups. Each is
    /// repointed at a stub that overrides Mario's position from V10's table — three bytes for
    /// three bytes, and each site already knows which entrance it just placed, which is what
    /// makes telling main from midway free.</summary>
    public const int MainJmpSite = 0x05D9FE, MidwayJmpSite = 0x05D9E9;
    /// <summary>Where the two stubs live: the vanilla $FF run in bank 05, past V7's routine.</summary>
    public const int EntranceFix = 0x05DC90;
    /// <summary>8 bytes per level: main X, main Y, midway X, midway Y, each a 16-bit word with
    /// bit 15 of the Y word meaning "this one is placed freely". 0x1000 bytes for 512 levels.</summary>
    public const int Entrance2TagPc = 0x9AFF8, Entrance2Pc = 0x9B000;
    public const int Entrance2Size = 0x1000, Entrance2Snes = 0x13B000;

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
           && (version < 10 || rom.HasFreeEntrancePositions);

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
        RatsWriter.FixChecksum(rom);
        ResetScanCaches(rom);
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
        return s;
    }

    /// <summary>
    /// V10: an entrance that can stand anywhere, and a midway that is its own entrance.
    ///
    /// Vanilla stores no position. It stores a screen and two INDICES into the bank-05 tables
    /// ($05D750/58 and $05D730/40), so Mario can only start at one of 8 x 16 spots per screen —
    /// and the midway record carries only a screen, sharing the main entrance's spot inside it
    /// ($05D9E1 overrides just the X high byte). Both limits are the data's, not the game's:
    /// $94/$96 are plain 16-bit positions by the time the level runs.
    ///
    /// So this overrides them at the last moment. Every path through the entrance decode ends
    /// `JMP $05DA17`, and the two that matter arrive from different branches — $05D9FE having
    /// placed the main entrance, $05D9E9 the midway. Repointing each three-byte jump at its own
    /// stub is an exact fit AND tells the two apart for free, which reading a flag at $05DA17
    /// would not.
    ///
    /// Each stub writes $94/$96 (Mario's position, 16-bit each) straight from the table and then
    /// jumps where it always went. A record whose Y word has bit 15 clear is not placed freely,
    /// and the stub leaves vanilla's answer exactly as it found it — so an untouched level plays
    /// identically, byte for byte, which is what the VRAM-parity discipline asks of a stamp that
    /// runs on every level entry.
    ///
    /// The main stub also stands down when `$1B93` is set: that is a SECONDARY entry, whose
    /// position belongs to the entrance record and not to the level. Secondary records stay on
    /// the vanilla grid for now — they need their own table, indexed by record.
    ///
    /// Lunar Magic solves the same problem its own way ("method 2", hooked at $05D979 and gated
    /// by $192A bit 6 — reference/LM_PARITY.md). We do NOT match its layout: its tables are
    /// RATS-allocated at per-ROM addresses baked into code it generates, so there is nothing
    /// fixed to agree with. The consequence is written down rather than papered over — a ROM
    /// re-saved by LM keeps working, but the free positions revert to the grid.
    /// </summary>
    private static void AppendV10Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        s.Add((Entrance2TagPc, Rats(new byte[Entrance2Size])));

        var a = new Asm(EntranceFix);
        // Main: skip when a secondary exit placed Mario — that position is the record's.
        a.Label("main").LdaAbs(0x1B93).Bne("mainOut");
        Place(a, 0, "mainDone");
        a.Label("mainOut").JmpAbs(0xDA17);

        a.Label("mid");
        Place(a, 4, "midDone");
        a.JmpAbs(0xDA17);
        s.Add((Pc(EntranceFix), a.Bytes()));

        // Three bytes for three bytes: each JMP keeps its size and gains a stop on the way.
        s.Add((Pc(MainJmpSite), [0x4C, unchecked((byte)a.LabelAt("main")), (byte)(a.LabelAt("main") >> 8)]));
        s.Add((Pc(MidwayJmpSite), [0x4C, unchecked((byte)a.LabelAt("mid")), (byte)(a.LabelAt("mid") >> 8)]));

        // Entered with 8-bit A and 16-bit index. Widths are restored before leaving, because
        // $05DA17 expects exactly what vanilla left it.
        static void Place(Asm a, int offset, string done)
        {
            a.Rep(0x30);
            a.LdaDp(0x0E).Asl().Asl().Asl().Tax();                 // level x 8
            a.LdaLongX(Entrance2Snes + offset + 2).BitImm16(0x8000).Beq(done);
            a.AndImm16(0x7FFF).StaDp(0x96);                        // Mario Y
            a.LdaLongX(Entrance2Snes + offset).StaDp(0x94);        // Mario X
            a.Label(done).Sep(0x30);
        }
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
         .Beq("exit")                        // [SCAN]
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
         .StzDp(0x00)                        // decompress dest: $7E:AD00, or V4's $7F:A000
         .LdaImm8(version >= 4 ? BufHi : 0xAD).StaDp(0x01)
         .LdaImm8(version >= 4 ? BufBank : 0x7E).StaDp(0x02)
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
         .Label("exit")
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
        return a.Bytes();
    }
}
