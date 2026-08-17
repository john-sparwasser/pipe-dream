namespace PipeDream;

/// <summary>
/// Vanilla-base prep: stamp LM-equivalent structures into a verified-vanilla SMW image so
/// the full editing feature set (DM16 tile objects, extended Map16 pages + acts-like table,
/// per-level custom palettes, sprite bank relocation) lights up through the existing
/// LunarMagic.cs detectors with NO Lunar Magic round-trip.
///
/// All inserted 65816 is clean-room authored (Asm.cs) from the documented semantics:
/// CONTRACT §7/§7a-rev/§7b/§7e/§9d/§11 and LEVEL_PIPELINE_NOTES §E/§F. The handler ADDRESSES
/// match the ones the repo already dispatches on (PortedObjectEngine / CONTRACT §9d), so no
/// editor code changes. Deterministic by construction: ExpandTo(1MB) + a fixed stamp table
/// + RATS tags at constant offsets + FixChecksum — same input, byte-identical output.
///
/// Layout (all code in verified-0xFF vanilla freespace, tables in the expansion):
///   $00C17A  JSL $06F5D0 + NOP        Map16 def-lookup hijack (detector: LmMap16Defs)
///   $06F54F+ def math ($06F552 ADC #$7008 / $06F555 LDY #$1200 — EnsureMap16Tiles slots)
///   $06F5D0  lookup entry (tile<0x200 vanilla $0FBE path; 0x200-0xFFF extended; else blank)
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
    public const int Version = 1;

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
    public const int SpriteStub = 0x0EF300, SpriteBankTable = 0x0EF100;
    public const int PalTrampoline = 0x0EFC50, PalApply = 0x0EFC90, PalThunk = 0x00FF93;
    public const int PalHook2Stub = 0x0EFC60;      // second hook: re-apply after $00A5BC

    /// <summary>The four vanilla `JSL $00F545` acts-like call sites (banks 00/01/02),
    /// repointed to our remap so gameplay collision resolves extended tiles.</summary>
    public static readonly int[] ActsCallSites = [0x00F4DD, 0x019533, 0x02961A, 0x02A6EB];

    /// <summary>True when the prep's four structures are present (also true on any
    /// LM-saved ROM — Apply must never stamp over foreign structures).</summary>
    public static bool IsPrepped(Rom rom)
        => rom.HasDm16Hijack && rom.LmMap16Defs.Bank != 0
           && rom.HasLmPaletteHook && rom.LmSpriteBankTable >= 0;

    /// <summary>Stamp the prep into the in-memory image (no-op when already present),
    /// fix the checksum, and reset every LunarMagic scan cache on the Rom.</summary>
    public static void Apply(Rom rom)
    {
        if (IsPrepped(rom)) return;
        rom.ExpandTo(0x100000);                        // also writes size code at $FFD7
        foreach (var (pc, bytes) in BuildStamps())
            Array.Copy(bytes, 0, rom.Data, pc + rom.HeaderOffset, bytes.Length);
        RatsWriter.FixChecksum(rom);
        ResetScanCaches(rom);
    }

    /// <summary>Prep a ROM file in place. The hash gate lives HERE (not in Apply) so unit
    /// tests can Apply to synthetic images. Returns an error message, or null on success.</summary>
    public static string? PrepInPlace(string path)
    {
        if (RomHash.HeaderlessSha256File(path) != RomHash.VanillaUsSha256)
            return "base is not a verified vanilla SMW (US) ROM — prep refused.";
        var rom = Rom.Load(path);
        Apply(rom);
        RatsWriter.SaveAs(rom, path);
        return null;
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
    private static List<(int Pc, byte[] Bytes)> BuildStamps()
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

        s.Add((Pc(0x06F54F), Map16Lookup()));
        s.Add((Pc(0x0DE1B0), ExtHandlers()));
        s.Add((Pc(Handler22), Dm16Handlers()));
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
    ///  - ≥ 0x1000:        blank fallback $00:8000 (same family LM uses, §7a-rev).
    ///
    /// Acts remap $06F5F0 — JSL'd from the 4 vanilla `JSL $00F545` sites. Entry: M/X 8-bit,
    /// A = tile page (high plane byte), $1693 = tile low byte, X precious. Reads the acts
    /// word at $118000 + tile*2; a sane value (&lt; 0x200) replaces the tile, anything else
    /// keeps it. Exits split back (low → $1693, page → A) and JMLs the vanilla handler —
    /// identical to vanilla for tiles &lt; 0x200 (identity table). The TAY/ASL/TAX/BMI/LDA
    /// long,X + CMP #$0200 sequence is the LmActsAsBase scanner contract.
    /// </summary>
    private static byte[] Map16Lookup()
    {
        var a = new Asm(0x06F54F);
        a.Label("extdef")                    // A = tile*2 (16-bit)
         .Asl().Asl()                        // tile*8
         .Clc()
         .AssertAt(0x06F552).AdcImm16(0x7008)   // [SCAN slot] imm — def addr low16
         .AssertAt(0x06F555).LdyImm16(0x1200)   // [SCAN slot] bank<<8
         .StyDp(0x05)                        // $06 = bank ($05 is overwritten by the caller)
         .Rtl()
         .Label("blank")                     // tile >= 0x1000: defined blank region
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
         .Label("ext")
         .CmpImm16(0x2000)
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
    private static byte[] Dm16Handlers()
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
         .LdaDp(0x00).StaDp(0x02)
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
}
