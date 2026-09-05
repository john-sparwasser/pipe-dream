namespace PipeDream;

/// <summary>
/// The 65816 routines the prep stamps, each assembled clean-room with <see cref="Asm"/> at its
/// pinned address. In stamp order: the Map16 lookup + acts-like remap (V1/V3), the extended and
/// Direct-Map16 object handlers, the sprite-bank and palette stubs, the GFX loader (V2, grown by
/// V4/V13/V14/V15/V16) and V16's advanced layer-3 block. The addresses are in RomPrep.cs; the
/// stamp lists that place these bytes are in RomPrep.Stamps.cs.
/// </summary>
public static partial class RomPrep
{
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

        EmitActsRemap(a);
        return a.Bytes();
    }

    /// <summary>$06F5F0: the acts-like remap the four vanilla `JSL $00F545` sites are repointed
    /// to (the second half of the Map16Lookup summary above).</summary>
    private static void EmitActsRemap(Asm a)
    {
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

        EmitDm16PageForms(a);
        return a.Bytes();
    }

    /// <summary>Objects 0x26-0x28 and the shared 0x27/0x29 body at $0DF170: the page-byte forms,
    /// which parse their extras and jump into the "fill" above.</summary>
    private static void EmitDm16PageForms(Asm a)
    {
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
        EmitGfxArmStub(a);
        EmitGfxLoader(a, version);
        EmitGfxResolve(a);
        EmitGfxSlotTab(a);
        if (version < 14) return a.Bytes();
        EmitL3Loop(a);
        if (version < 15) return a.Bytes();
        EmitL3Opt(a, version);
        EmitL3Map(a, version);
        return a.Bytes();
    }

    /// <summary>$0FF770: arm $FE = level+1, then the displaced `LDA $1925 : CMP #$09`.</summary>
    private static void EmitGfxArmStub(Asm a)
    {
        a.Rep(0x20)
         .LdaAbs(0x010B)
         .IncA()
         .StaDp(0xFE)                        // arm: $FE-$FF = level+1
         .Sep(0x20)
         .LdaAbs(0x1925)                     // displaced vanilla bytes
         .CmpImm8(0x09)
         .Rtl();
    }

    /// <summary>$0FF780: the per-level GFX loader — the displaced cache loop, then every
    /// non-0x7F slot of an enabled bypass record decompressed and uploaded (see GfxCode).</summary>
    private static void EmitGfxLoader(Asm a, int version)
    {
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
    }

    /// <summary>$0FF810: file# → $8A-$8C source pointer (vanilla tables, $0FF600, or the
    /// ExGFX 0x100+ table); carry set = skip.</summary>
    private static void EmitGfxResolve(Asm a)
    {
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
    }

    /// <summary>$0FF8A0: the eight (record offset, $2117 page) pairs, FG1..SP4.</summary>
    private static void EmitGfxSlotTab(Asm a)
    {
        a.PadTo(GfxSlotTab)
         .Db(0x0E, 0x00,                     // FG1 (w7) → VRAM page $00
             0x0C, 0x08,                     // FG2 (w6) → $08
             0x0A, 0x10,                     // BG1 (w5) → $10
             0x08, 0x18,                     // FG3 (w4) → $18
             0x16, 0x60,                     // SP1 (w11) → $60
             0x14, 0x68,                     // SP2 (w10) → $68
             0x12, 0x70,                     // SP3 (w9) → $70
             0x10, 0x78);                    // SP4 (w8) → $78
    }

    /// <summary>V14, $0FF8B0/$0FF8C0: the layer-3 slot table and the LG1-LG4 upload pass — a
    /// straight 2bpp copy, run on every armed load so an unbypassed level gets 28-2B back.</summary>
    private static void EmitL3Loop(Asm a)
    {
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
    }

    /// <summary>V15, $0FF950: the layer-3 option, vanilla's way, for the $00A01F hook; from V16
    /// also the seat that runs the advanced reader and engine.</summary>
    private static void EmitL3Opt(Asm a, int version)
    {
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
    }

    /// <summary>V15, $0FF980: the LT3 tilemap file → the layer-3 VRAM window, called from
    /// L3StripeThunk after vanilla's own stripe upload.</summary>
    private static void EmitL3Map(Asm a, int version)
    {
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
    }

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
        EmitL3Engine(a);
        EmitL3Axis(a);
        EmitL3Scroll(a);
        EmitL3KindTab(a);
        EmitL3Auto(a);
        EmitL3AdvRead(a);
        EmitL3SpeedTab(a);
        EmitL3AdvPair(a);
        return a.Bytes();
    }

    /// <summary>$0FFB20: the level-load engine — colour math, subscreen, initial X/Y, the two
    /// resolved scroll codes, and the auto-scroll seeding for both axes.</summary>
    private static void EmitL3Engine(Asm a)
    {
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
         // ...and arm the auto-scroll accumulators, once per axis. X = axis*2 for the pairs two
         // bytes apart ($1458/$145A, $146A/$146C, $22/$24), Y = axis for the ones one byte apart
         // ($145F/$1460, $145C/$145D, $0BE6/$0BE7) — LM's own layout, and the reason one shared
         // routine can serve both axes instead of two near-copies.
         .Rep(0x30)
         .LdxImm16(0).LdyImm16(0).JsrL("seed")
         .LdxImm16(2).LdyImm16(1).JsrL("seed")
         .Sep(0x30)
         .Rts();
    }

    /// <summary>One axis of the per-frame pass: camera + code → layer-3 position, by kind.</summary>
    private static void EmitL3Axis(Asm a)
    {
        // ---- One axis: A = camera, $06 = initial offset, X = code. Out: A = layer-3 position ----
        a.Label("axis")                                  // 16-bit M/X
         .StaDp(0x08)
         .Sep(0x20).LdaLongX(L3KindTabAddr).Rep(0x20).AndImm16(0x00FF)
         .Beq("hold")                                    // 0 = None: stay at the offset
         .CmpImm16(0x0001).Beq("oneone")                 // 1 = Constant: 1:1 with layer 1
         .CmpImm16(0x0008).Beq("fast")                   // 8 = Fast: 1.2x
         // 9 = one of the twelve auto-scrolls. A JSR rather than a branch: its block sits past
         // the kind table, out of rel8 reach.
         .CmpImm16(0x0009).Bcc("shifts")
         .JsrL("auto").Rts()
         .Label("shifts")
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
    }

    /// <summary>$0FFC40: the per-frame dispatcher JSL'd from $05C40C — both axes through
    /// "axis", the scroll-sync mirrors, or an exact re-entry into vanilla when the group is off.</summary>
    private static void EmitL3Scroll(Asm a)
    {
        // ---- The per-frame dispatcher, long-called from vanilla's own scroll site ----
        a.PadTo(L3Scroll)
         .Label("scroll")                                // 8-bit M/X, from bank 05
         .LdaAbs(0x1931).Bmi("bail")                     // LM's own guard
         .LdaAbs(0x145E).AndImm8(0x01).Beq("bail")
         .Rep(0x30)
         // $0A names the axis for the auto-scroll case, which is the only one whose state is
         // per-axis rather than derived from the camera.
         .StzDp(0x0A)
         .LdaAbs(0x146A).StaDp(0x06)
         .Sep(0x20).LdaAbs(L3CodeH).Rep(0x20).AndImm16(0x00FF).Tax()
         .LdaDp(0x1A).JsrL("axis").StaDp(L3ScrollX)
         .LdaImm16(1).StaDp(0x0A)
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
    }

    /// <summary>$0FFCE0: scroll code → kind, LM's ladder.</summary>
    private static void EmitL3KindTab(Asm a)
    {
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
    }

    /// <summary>$0FFD00: the auto-scroll block — the per-frame accumulator ("auto") and the
    /// load-time seeding ("seed"), one routine for both axes.</summary>
    private static void EmitL3Auto(Asm a)
    {
        // ---- Auto-scroll: the twelve rates that move layer 3 on their own ----
        // Not a fraction of the camera but a speed in 8.8 fixed point, accumulated. LM's layout,
        // its speed table and its guards; the tide variant (`$1403` != 0) is still not ported.
        //
        //   SEED (level load, both axes): speed → $1458/$145A, the fractional byte → $145C/$145D
        //   (seeded with the speed's low byte when the speed is negative, which is how LM makes
        //   the first whole pixel land on time), the initial position → $22/$24 (for auto-scroll
        //   LM's help calls X/Y "actual positions", not offsets), and the skip bit in
        //   $0BE6/$0BE7 so the first frame draws exactly where it was seeded.
        //
        //   PER FRAME: fraction += speed; the carried-out whole pixels, sign-extended, plus
        //   $17BD/$17BC — vanilla's accumulated camera delta, computed at $05BC0C just before it
        //   calls us — are added to the position. $9D (sprites locked / pause) freezes it.
        a.PadTo(L3Auto)
         .Label("auto")                                  // 16-bit M/X; $0A = axis, X = kind
         .LdaDp(0x0A).Tay()                              // Y = axis: the one-byte-apart pairs
         .Asl().Tax()                                    // X = axis*2: the two-byte-apart ones
         .Sep(0x20).LdaDp(0x9D).Rep(0x20).AndImm16(0x00FF)
         .Bne("autohold")                                // locked: hold the position we had
         // $04 = the camera delta, sign-extended from one byte. The only pair whose order is
         // REVERSED against the axis index ($17BD is horizontal, $17BC vertical), so it branches
         // rather than indexes.
         .Sep(0x20)
         .LdaDp(0x0A).Bne("autovd")
         .LdaAbs(0x17BD).Bra("autogd")
         .Label("autovd").LdaAbs(0x17BC)
         .Label("autogd")
         .Rep(0x20).AndImm16(0x00FF)
         .CmpImm16(0x0080).Bcc("autodp").OraImm16(0xFF00)
         .Label("autodp").StaDp(0x04)
         // One frame is skipped after seeding, so the level opens on the seeded position.
         .Sep(0x20)
         .LdaAbsY(0x0BE6).AndImm8(0x80).Beq("autorun")
         .LdaAbsY(0x0BE6).AndImm8(0x7F).StaAbsY(0x0BE6)
         .Rep(0x20).LdaImm16(0).Bra("autoadd")
         .Label("autorun")
         .Rep(0x20)
         .LdaAbsY(0x145C).AndImm16(0x00FF)               // the fraction carried from last frame
         .Clc().AdcAbsX(0x1458)
         .Phx().Sep(0x20).StaAbsY(0x145C).Rep(0x20).Plx()  // keep the new fraction (low byte)
         .AndImm16(0xFF00).Bpl("autow").OraImm16(0x00FF)
         .Label("autow").Xba()                            // whole pixels, signed
         .Label("autoadd")
         .Clc().AdcDp(0x04)
         .Clc().AdcDpX(L3ScrollX)                        // $22 or $24, whichever axis this is
         .Rts()
         .Label("autohold")
         .LdaDpX(L3ScrollX)
         .Rts()
         // The load-time half, called twice by the engine.
         .Label("seed")                                  // 16-bit M/X; X = axis*2, Y = axis
         .LdaAbsY(L3CodeH).AndImm16(0x00FF)
         .CmpImm16(0x0006).Bcc("seeddone")
         .CmpImm16(0x0012).Bcs("seeddone")
         .Asl().Phx().Tax()
         .LdaLongX(L3SpeedTab)
         .Plx()
         .StaAbsX(0x1458)
         // CMP rather than the BMI that reads naturally here: PLX above set N/Z from the pulled
         // index, not from the speed.
         .CmpImm16(0x8000).Bcs("seedfrac")               // negative speeds carry from their own
         .LdaImm16(0)                                    // low byte; positive ones start at 0
         .Label("seedfrac")
         .Sep(0x20).StaAbsY(0x145C).Rep(0x20)
         .LdaAbsX(0x146A).StaDpX(L3ScrollX)
         .Sep(0x20).LdaAbsY(0x0BE6).OraImm8(0x80).StaAbsY(0x0BE6).Rep(0x20)
         .Label("seeddone")
         .Rts();
    }

    /// <summary>$0FFDB0: the nibble reader — the record's nine spare high nibbles into
    /// $7FC01A-$7FC01C and $145E, LM's opening idiom instruction for instruction.</summary>
    private static void EmitL3AdvRead(Asm a)
    {
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
    }

    /// <summary>$0FFE00: LM's auto-scroll speed table, its own bytes.</summary>
    private static void EmitL3SpeedTab(Asm a)
    {
        // LM's own speed table ($109D3B), indexed by code*2 — 8.8 fixed point pixels per frame.
        // Codes 0-5 are the camera-relative rates and read 0 here; 06-09 and 10-11 climb
        // +$40 +$80 +$100 +$200 +$300 +$400 (Up/Left Slow..Fast 4) and 0A-0F are the same six
        // negated (Down/Right). $40 = a quarter pixel a frame, which is the fog/goldfish drift;
        // $400 = four pixels, the tide speed.
        a.PadTo(L3SpeedTab).Db(
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,   // 00-05
            0x40, 0x00, 0x80, 0x00, 0x00, 0x01, 0x00, 0x02,                           // 06-09
            0xC0, 0xFF, 0x80, 0xFF, 0x00, 0xFF, 0x00, 0xFE, 0x00, 0xFD, 0x00, 0xFC,   // 0A-0F
            0x00, 0x03, 0x00, 0x04);                                                  // 10-11
    }

    /// <summary>$0FFE82: LM's nibble-pair helper, byte for byte.</summary>
    private static void EmitL3AdvPair(Asm a)
    {
        // The nibble-pair helper, byte for byte LM's.
        a.PadTo(L3AdvPair)
         .LdaIndLongY(0x8A).AndImm8(0xF0).StaDp(0x00)
         .Dey().Dey()
         .LdaIndLongY(0x8A).Lsr().Lsr().Lsr().Lsr().OraDp(0x00)
         .Rts();
    }
}
