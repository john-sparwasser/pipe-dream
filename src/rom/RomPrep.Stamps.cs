namespace PipeDream;

/// <summary>
/// The stamp lists, one per prep version, in the order <see cref="BuildStamps"/> applies them.
/// Every released version is BYTE-FROZEN: a later version only appends to the list or restamps a
/// whole block over an earlier one, so a v1 project's pinned image reproduces forever (golden-hash
/// tested). The addresses are in RomPrep.cs; the routines the stamps carry are assembled in
/// RomPrep.Code.cs.
/// </summary>
public static partial class RomPrep
{
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
        // V17 carries LM's OVERWORLD ExAnimation hack (LM 2.40's separate one) beside the level
        // engine v11 carries — its own blob, record table and per-submap settings byte.
        if (version >= 17) AppendV17Stamps(s);
        // V18 restamps the record table (seven entries longer) over V2's and adds the overworld's
        // arming stub — later stamps win, so V2's bytes stay frozen and only the v18 image differs.
        if (version >= 18) AppendV18Stamps(s);
        // V19 restamps v18's arming stub in LM's own shape and adds LM's layout stamp, which is
        // what makes LM's Submap GFX dialog read the seven records v18 wrote.
        if (version >= 19) AppendV19Stamps(s);
        // V20 restamps the GFX block with the record fetch parked where LM reads the table from.
        if (version >= 20) AppendV20Stamps(s);
        // V21 restamps the midway blob with its tables moved into a block of their own.
        if (version >= 21) AppendV21Stamps(s);
        // V22 gives the midway blob itself a block of its own and repoints the two hooks; the
        // v10 copy at the secondary block's tail stays as frozen, unreferenced bytes.
        if (version >= 22) AppendV22Stamps(s);
        // V23 retires the GFX arm stub: vanilla bytes back at $0583B8, and the GFX block
        // restamped with the ExGFX pointer table's tail back to zero.
        if (version >= 23) AppendV23Stamps(s);
        // V24 restamps the palette sites with Lunar Magic's own engine over v1's stubs.
        if (version >= 24) AppendV24Stamps(s);
        // V25 takes the rest of LM's 4bpp mode: the wrapper repoints, the filter dispatch and body,
        // the GFX33 load, the $00A149 NOP, and the loader restamped with its AN2 pass.
        if (version >= 25) AppendV25Stamps(s);
        // V26 stamps LM's acts-like core, which a Map16 save writes over the v1 remap's address,
        // and moves the remap (and the four sites' JSLs) clear of it.
        if (version >= 26) AppendV26Stamps(s);
        return s;
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
    ///            §7d $7FC006/9). The overworld DOES reach the hook — its tileset is
    ///            0x11+submap, well under the ≥ $FE that branches away at $00AA1C — so
    ///            before v18 it re-applied whatever level record $FE was left holding.
    ///            V18's own hook at $00A140 arms $FE with the submap's record instead.
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

        // Separate midway settings — LM's blob, its five operands pointed at our tables and at
        // itself. V21 moves the tables and v22 the blob, so it is emitted per address.
        s.Add((Pc(MidwayRoutineSnes), MidwayRoutine(MidwayTablesSnes, MidwayRoutineSnes)));
        s.Add((Pc(LmMidwayHook), Jsl(MidwayRoutineSnes)));
        s.Add((Pc(LmExitArrivalHook), Jsl(MidwayRoutineSnes + 0xA0)));

        s.Add((Pc(Rom.LmEntranceFlags), new byte[0x200]));
        s.Add((Pc(Rom.LmEntranceYHigh), new byte[0x200]));
        s.Add((Pc(Rom.LmEntranceFgBg), Enumerable.Repeat((byte)0x1A, 0x200).ToArray()));
        s.Add((Pc(0x05D9C3), [0xAD]));                          // STA $13CD → LDA $13CD
        s.Add((Pc(LmMidwayStore), [0x85, 0x01, 0xEA, 0xEA, 0xEA]));
    }

    private static byte[] Long(int snes) => [(byte)snes, (byte)(snes >> 8), (byte)(snes >> 16)];
    private static byte[] Jsl(int snes) => [0x22, .. Long(snes)];

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

    /// <summary>
    /// V17: Lunar Magic's OVERWORLD ExAnimation hack (LmOwExAnimEngine), relocated into bank $1E
    /// behind the level engine's blocks: the 0xC20 blob, an empty 7-entry record table and the
    /// seven per-submap settings bytes, then its three hooks on the overworld's own paths
    /// ($048086 setup, $00A4E3 animated-tile upload, $0480E0 the per-frame tick) and the three
    /// frame-counter bytes LM moves from $13 to $14 in what is left of OW_Tile_Animation.
    /// Lunar Magic installs exactly this into a v16 base — a diff of one it saved over ours shows
    /// the same blob, the same hooks and nothing else — so a base prepped to v17 opens in its
    /// "Edit Submap ExAnimated Frames" dialog with the lists our writer put there.
    /// </summary>
    private static void AppendV17Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        s.Add((Pc(LmOwExAnimEngine.EngineSnes) - 8, Rats(LmOwExAnimEngine.Engine())));
        s.Add((Pc(LmOwExAnimEngine.TableSnes) - 8, Rats(LmOwExAnimEngine.EmptyTable())));
        s.Add((Pc(LmOwExAnimEngine.SettingsSnes) - 8, Rats(LmOwExAnimEngine.EmptySettings())));
        foreach (var (site, bytes) in LmOwExAnimEngine.Hooks()) s.Add((Pc(site), bytes));
    }

    /// <summary>
    /// Lunar Magic's separate-midway-settings blob (ShaoBase $10FDDF, juz $11FA63, DogsOfWar
    /// $12EF20 — byte-identical apart from these operands), with its four table operands pointed
    /// at <paramref name="tables"/> and its fifth at <paramref name="self"/>, where the blob sits.
    /// </summary>
    private static byte[] MidwayRoutine(int tables, int self)
    {
        var mid = Convert.FromHexString(
            "4A4A4A4AC21148A60EBF088012A8291003018301988920F06429084A4A4A85959829C78D2A19BF088212A829F0" +
            "8596980A0A0A0A8594BF00FC0629808504BF00FE06293F8DCD13BF088612297F0404293F8597A900EBBF08841285" +
            "028920D01FA82903AABF0CD705852098290C4A4AAABF08D705851C9829C00CCD1338686BAD1A1418D0F89C2A1984" +
            "0EA5022901850FFAFA5CB7D805FFFFFFFFFFFF4C4D10012C2A19501A48AD1A14F013B900F422DFFD10A40E9008FA" +
            "FA85015CA1D9056829384A4A6B");
        foreach (var (at, snes) in new[] { (0x0A, tables), (0x27, tables + 0x200),
                                           (0x48, tables + 0x600), (0x57, tables + 0x400),
                                           (0xAF, self) })
        {
            mid[at] = (byte)snes; mid[at + 1] = (byte)(snes >> 8); mid[at + 2] = (byte)(snes >> 16);
        }
        return mid;
    }

    /// <summary>
    /// V22: the midway routine in a RATS block of its own, the way Lunar Magic keeps it. V10 put
    /// the blob at the tail of the 0xD00 secondary-extension block, and LM's *Modify Secondary
    /// Entrances* save — which re-allocates the two secondary tables it finds through the reader
    /// operands into fresh blocks and RELEASES the old one, zeroing every byte of it — wiped the
    /// routine while `$05D9E3` and `$05D979` still jumped into it. Measured 2026-09-10 on a v21
    /// build; the same save on ShaoBase, whose routine has its own block, left it intact.
    ///
    /// The block is LM's exact shape (ShaoBase `$10FDD7`: tag, the 0xC4-byte blob, eight `$FF`,
    /// then its `LM 10 01` stamp — 0xD0 bytes), and the two hooks move with it. Nothing in the
    /// editor moves: <see cref="LunarMagic.HasFreeMidwayPosition"/> and
    /// <see cref="LunarMagic.LmMidwayTable"/> already follow the hook's operand. The v10 copy at
    /// `$13BC00` stays in the frozen list, unreferenced.
    /// </summary>
    private static void AppendV22Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        byte[] block = [.. MidwayRoutine(MidwayTablesV21Snes, MidwayRoutineV22Snes),
                        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x4C, 0x4D, 0x10, 0x01];
        if (block.Length != MidwayRoutineBlockSize) throw new InvalidOperationException("midway block is not LM's 0xD0");
        s.Add((MidwayRoutineTagPc, Rats(block)));
        s.Add((Pc(LmMidwayHook), Jsl(MidwayRoutineV22Snes)));
        s.Add((Pc(LmExitArrivalHook), Jsl(MidwayRoutineV22Snes + 0xA0)));
    }

    /// <summary>
    /// V21: give the four separate-midway tables a RATS block of their own, because Lunar Magic
    /// will not save a level otherwise. V10 pointed the blob 0x400 into the secondary block's
    /// run; LM follows that operand, looks for a tag 8 bytes ahead of the tables, and refuses
    /// with *"Existing data format or size not recognized! Midway entrance data"* — once per
    /// write attempt, so the box comes back as fast as it is dismissed.
    ///
    /// Measured 2026-09-10 with ShaoBase as the control (same edit, same save, silent): moving
    /// the tables into a 0x800 block with the data at tag + 8, LM's own shape, and changing
    /// nothing else makes the save silent and it writes the midway screen where our reader reads
    /// it (vanilla's `$05F400` table, since "separate settings" was off). Nothing in the editor
    /// moves with them: <see cref="LunarMagic.LmMidwayTable"/> already follows the blob's own
    /// operand rather than a constant.
    /// </summary>
    private static void AppendV21Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        s.Add((MidwayTablesTagPc, Rats(new byte[MidwayTablesSize])));
        s.Add((Pc(MidwayRoutineSnes), MidwayRoutine(MidwayTablesV21Snes, MidwayRoutineSnes)));
    }

    /// <summary>
    /// V23: move the v2 GFX arm stub out of Lunar Magic's ExGFX pointer table, where LM's ExGFX
    /// import was zeroing it from under the LoadLevel hook. Two measurements, 2026-09-10:
    ///
    /// - The stub sat at $0FF770 — the last sixteen bytes of LM's 0x80-0xFF pointer table
    ///   ($0FF600, 0x180 bytes; ids 0xFB-0xFF). `-ImportExGFX` re-initialises that table, so
    ///   afterwards `$0583B8` JSL'd into zeros and the ROM no longer built a level in Mesen.
    ///   Restoring vanilla's five bytes at `$0583B8` on that broken ROM, and nothing else, made it
    ///   build one again — the stub's re-arm has been redundant since v10, because LM's own
    ///   `$0EF550` stub stores level+1 into $FE at `$05D8E2`, before `$0583B8` runs.
    /// - But the hook cannot simply go: on a base with vanilla bytes at `$0583B8`, the same import
    ///   installed LM's ENTIRE GFX-bypass loader over ours (`$0FF780`-`$0FFE93`, a 0x6E00 records
    ///   block, the DM16 dispatch entries) and the ROM stopped booting. On a base with a JSL there
    ///   — any target; v2's pointed at $0FF770 — it did not. So a JSL at `$0583B8` is part of how
    ///   LM decides its bypass is installed, and it stays.
    ///
    /// LM's own target, `$0FF7F0`, needs 0x43 bytes where our resolver lives; the stub goes to
    /// `$0FF890` instead, the gap LM's layout leaves between its loader group and the slot tables,
    /// which both LM installs over our bases left alone. The sixteen bytes at $0FF770 go back to
    /// zero — pointer slots, "no file", as LM keeps them.
    /// </summary>
    private static void AppendV23Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        s.Add((Pc(0x0583B8), [0x22, GfxArmStubV23 & 0xFF, GfxArmStubV23 >> 8 & 0xFF, GfxArmStubV23 >> 16, 0xEA]));
        s.Add((Pc(GfxArmStub), GfxCode(23)));
    }

    /// <summary>
    /// V24: Lunar Magic's palette engine, byte for byte, over v1's private stubs. LM installs it as
    /// one unit — its ExGFX import wrote every one of these onto a v23 base (reference/LM_PARITY.md
    /// §2 "`-ImportExGFX`") — so after v24 that import lands on code that is already there:
    ///
    /// - `$0EFC00`-`$0EFCAF`: LM's block, identical in ShaoBase, BigEye and DogsOfWar. `$0EFC50`
    ///   restores LM's staging and runs the vanilla `UploadSpriteGFX`; `$0EFC80` backs it up again
    ///   and hands the level to `$05809E → $1FAFF1`, LM's first-load palette apply, which v10 has
    ///   carried since (byte-identical here and in ShaoBase). `$0EFC00` is LM's 4bpp RAM-expander
    ///   wrapper: it rides along because the block is one unit, but nothing hooks it yet — the
    ///   `$00A830`/`$03DDC9` repoints are part of LM's 4bpp mode, which stays ours (LM_PARITY §2).
    /// - `$0095E9`: `JML $0EFC50 : JSR LoadPalette : JML $0EFC80 : NOP NOP`. Its first four bytes
    ///   are what v1's hook was, which is why byte `$0095E9 == 0x5C` has always been the detector.
    /// - `$00A5BF → JSL $0EF570`, and the 0x57-byte routine there: `$FE` names the level, `$0EF583`
    ///   copies its `$0EF600` blob into the `$0701` staging, then `STZ $FE` and the displaced
    ///   `JSL $05BE8A`. This is the one LM did NOT write on import, so v1's `JSL $0EFC60` was left
    ///   pointing into the middle of LM's `$0EFC50` loop — the reason this transplant exists.
    ///   Clearing `$FE` is safe for our loader: no `UploadSpriteGFX` runs after `$00A5BF` inside a
    ///   level (the other callers are title and credits paths), the overworld skips that block on
    ///   `$0D9B` at `$00A5B2`, and the next LoadLevel re-arms through `$0EF550`.
    /// - `$0093F7 → JSL` LM's NMI-enable fix, in a 0x20 block of its own like LM's (ShaoBase
    ///   `$10EE10`): the same 13 bytes and `LM 00 01`.
    ///
    /// Retired: `$0EFC50/60/90` (under LM's block) and the bank-00 thunk at `$00FF93`, back to
    /// vanilla's $FF. The table at `$0EF600` and the blob format were LM's all along (CONTRACT §7e),
    /// so `Rom.WriteLmCustomPalette` and `Rom.LmCustomPalette` are untouched.
    /// </summary>
    private static void AppendV24Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        s.Add((Pc(LmPaletteEngine), Convert.FromHexString(
            "2228BA0008C23048DA5AA20000A00000A90800850AB7005A9B97007AC8C8E8E8C60AD0F1A90800850AE220B700" +
            "5A9B97007AC8C8E8C60AD0F2C220C00010D0D07AFA68286BFFFFFFFFFFFFFFFFFFFFFF08C23048DA5AA20000BF" +
            "00B97E9F00207EE8E8E00004D0F17AFA6828C220A9EC9548E2305CDAA900FFFFFFFFFFFFFFFF08C23048DA5AA2" +
            "0000BF00207E9F00B97EE8E8E00004D0F17AFA6828229E8005C220A9F59548E2205CF9A500FFFFFFFF")));
        s.Add((Pc(0x0095E9), [0x5C, 0x50, 0xFC, 0x0E, 0x20, 0xED, 0xAB, 0x5C, 0x80, 0xFC, 0x0E, 0xEA, 0xEA]));
        s.Add((Pc(0x00A5BF), [0x22, LmPaletteFadeIn & 0xFF, LmPaletteFadeIn >> 8 & 0xFF, LmPaletteFadeIn >> 16]));
        s.Add((Pc(LmPaletteFadeIn), Convert.FromHexString(
            "C230A5FEF0063A2083F564FEE230228ABE056BE2108B4BABC21085000A186500A8B900F68504C8B900F6D002AB" +
            "60AB8505E210A0008508B704990107E604E604A900011865048507B704990307B707990308C8C8D0F260")));
        s.Add((Pc(PalThunk), [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));   // v1's bank-00 thunk, retired
        s.Add((Pc(LmNmiFixHook), [0x22, LmNmiFix & 0xFF, LmNmiFix >> 8 & 0xFF, LmNmiFix >> 16, 0x60]));
        var nmi = new byte[0x20];
        Array.Fill(nmi, (byte)0xFF);
        Convert.FromHexString("A9812C124230FB70F98D00426B4C4D0001").CopyTo(nmi, 0);
        s.Add((LmNmiFixTagPc, Rats(nmi)));
    }

    /// <summary>
    /// V25: the rest of Lunar Magic's 4bpp mode, byte for byte — exactly the set an ExGFX import
    /// still wrote onto a v24 base (measured 2026-09-11; reference/LM_PARITY.md §2), so after this
    /// an import writes its file and bookkeeping and no code. The data these bytes expect is
    /// <c>BakeLmFourBppFiles</c>.
    ///
    ///   `$00A830`/`$03DDC9`  the two `JSL $00BA28` whose consumers read the buffer as THREE planes —
    ///                        the GFX0F expander into `$7F977B` and bank 03's sprite-GFX path — go
    ///                        through LM's `$0EFC00` wrapper (stamped by v24): decompress, then fold
    ///                        the 4bpp buffer back to 3bpp in place. The expanders themselves return
    ///                        to vanilla's bytes: v4 had rewritten their plane-2 loops (and the
    ///                        2-tile skip's `ADC #$0030`) to read four planes, LM leaves them alone.
    ///   `$00AA8D`/`$00AA91`  `CPY #$08`/`#$1E` → `#$32`: nothing reaches the filter path; its
    ///                        plane-3 synthesis lives in GFX1E/GFX08 instead.
    ///   `$00AB0B`            LM's filter body all the same (dead now, but it is what LM writes).
    ///   `$00B895`            GFX33 decompressed straight to `$7E7D00` as four planes, then GFX32 to
    ///                        `$7E2000` through the vanilla tail at `$00B8D7`; the 3bpp expander
    ///                        behind it is dead bytes.
    ///   `$00A149`            vanilla's GFX14 decompress after the uploads, NOPped — the loader's
    ///                        AN2 pass (<see cref="An2Pass"/>) owns the buffer's last file.
    /// </summary>
    private static void AppendV25Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        byte[] wrapper = [LmPaletteEngine & 0xFF, LmPaletteEngine >> 8 & 0xFF, LmPaletteEngine >> 16];
        s.Add((Pc(0x00A830), wrapper));
        s.Add((Pc(0x03DDC9), wrapper));
        s.Add((Pc(0x00A83E), [0x30, 0x00]));                                   // ADC #$0030: 2 tiles at 24 B
        s.Add((Pc(0x00A857), Convert.FromHexString("A00800A70029FF009F7B977FE8E8E60088D0F0")));   // GFX0F plane-2 loop
        s.Add((Pc(0x00A897), Convert.FromHexString("A00800A70029FF009DF60BE8E8E60088D0F1")));     // GFX00 plane-2 loop
        s.Add((Pc(0x00AA8D), [0x32]));
        s.Add((Pc(0x00AA91), [0x32]));
        s.Add((Pc(0x00AB0B), Convert.FromHexString(
            "A207A7008D1821EB07009DB21BE600E600CA10EEA207A70029FF00850CA700EB1DB21B250A050C8D1821E600E600CA10E5")));
        s.Add((Pc(0x00B895), Convert.FromHexString("7D8400A97E850220DEB8C230A0002084004CD7B8")));
        s.Add((Pc(0x00A149), [0xEA, 0xEA, 0xEA, 0xEA]));
        s.Add((Pc(GfxArmStub), GfxCode(25)));
    }

    /// <summary>
    /// V26: Lunar Magic's acts-like core, and our remap moved out from under it.
    ///
    /// *Editors ▸ 16x16 Tile Map Editor ▸ F9* on a v25 base left the ROM unable to build a level.
    /// Measured 2026-09-11 (reference/LM_PARITY.md §2): the save writes LM's 96-byte acts-like
    /// core over `$06F5E4`-`$06F643`, and v1 had parked our remap at `$06F5F0` — inside it. The
    /// four vanilla `JSL $00F545` sites still pointed there, where they now met LM's
    /// `STA $65 : LDY #$0000 : RTL`, so the level renderer got a clobbered stream pointer.
    ///
    /// LM did NOT repoint those four sites (it repoints them to per-site trampolines of its own
    /// on its bases, but it leaves a foreign hook alone — same conservatism as `$0583B8`, v23).
    /// So the fix is both halves: the remap moves to <see cref="ActsRemapEntryV26"/>, behind LM's
    /// trampolines where nothing writes, and LM's core goes down at LM's own address with LM's own
    /// bytes — including the `LM 10 01` marker by which LM knows the acts-like hack is installed,
    /// and with its lookup reading the acts-like table we already own at
    /// <see cref="ActsTableSnes"/>, which is the bank ShaoBase, BigEye and DogsOfWar carry too.
    /// The core is dead code on our base — nothing calls its wrapper, and its lookup is a second
    /// reader of the same table — but it is what makes an LM Map16 save leave us alone.
    ///
    /// The **Map16** hack's own marker (`LM 12 01` at <see cref="LmMap16Marker"/>) is deliberately
    /// NOT stamped, though a save writes it: with it in, LM reads the extended-def block as its own
    /// to manage, and since a fresh prep's page 2 is all default tiles it FREES our block at
    /// `$128000` (tag and all) and installs two more 0x8000 blocks of machinery. Measured, and the
    /// reason this version claims only the hack it actually carries.
    ///
    /// Also still LM's on a save, and deliberately not taken here: `$04DCFA → JSL $06F5E4` and the
    /// four `$058A65`/`B45`/`C33`/`D2A` → `JSL $06F540`, which route four more bank-05/04 render
    /// paths through the ladder. They are LM's code over the ladder we already stamp byte for byte
    /// (v12), self-consistent either way, but they change what those paths draw — a render change
    /// that wants its own Mesen pass, not a byte pass.
    /// </summary>
    private static void AppendV26Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        var core = Convert.FromHexString(
            "0A4B6202008254FFA40B84668565A000006BFFFFFFFFFFFF4C4D1001EAEA985C45F500EAAC9B0D100A7A7A" +
            "80F2EAEAEAEAEAEAEBAD9316DAC230A80AAA3016BF008011C90002B0F28403E230FA8D9316EBA8A30860BF" +
            "0080FFC90002B0DC80E8");
        core[LmActsTableOperand - LmActsCore] = (byte)(ActsTableSnes >> 16);
        s.Add((Pc(LmActsCore), core));
        s.Add((Pc(ActsRemapEntryV26), ActsRemap(ActsRemapEntryV26)));
        foreach (int site in ActsCallSites)
            s.Add((Pc(site), [0x22, ActsRemapEntryV26 & 0xFF,
                              ActsRemapEntryV26 >> 8 & 0xFF, ActsRemapEntryV26 >> 16]));
    }

    /// <summary>
    /// V18: Lunar Magic's Overworld ▸ Submap GFX — a per-submap FG/SP file list, so the six
    /// submaps and the main map stop sharing tileset row 0x11's four FG and four sprite files.
    ///
    /// LM keeps them in the SAME table as the per-level Super GFX Bypass records: seven more
    /// 16-word entries behind the 0x200 levels, submap N at index 0x200+N (0 the main map, 1-6
    /// LM's dialog order). Settled 2026-09-09 by installing the hack through that dialog on
    /// three copies of a v17 base and diffing (reference/OVERWORLD.md §4): a submap's record is
    /// byte-for-byte a level's — w0 AN2, w2/w3 FG6/FG5, w4-w7 FG4-FG1, w8-w11 SP4-SP1, w12-w15
    /// the layer-3 four — and LM fills all seven with the values the vanilla lists imply, with
    /// **no enable bit** in w0: once the hack is in, every submap reads its record.
    ///
    /// So the table grows by 0xE0 bytes (its RATS block has room: nothing of ours lives between
    /// $12D000 and the ExGFX pointers at $138008) and the loader learns that an index past the
    /// levels needs no enable bit (<see cref="EmitGfxLoader"/>). The hook is LM's: vanilla's
    /// `STA $20 : SEP #$20` at <see cref="OwGfxHook"/> — the last thing before the
    /// `JSR UploadSpriteGFX` whose tail at $00AA50 our loader owns — becomes a JSL to a stub at
    /// LM's own <see cref="OwGfxStub"/>. LM's stub points $7FC006 at the record; ours arms $FE,
    /// which is the same fact in the form our loader already reads (and it also stops the
    /// overworld load from re-applying whatever level record $FE was left holding).
    ///
    /// AN2 (w0) is carried in the record and shown in the drawer; through v24 it was not loaded
    /// (the overworld's animated-tile source stayed vanilla's `LDY #$14 : JSL $00BA28` at
    /// $00A147), and from v25 the loader's AN2 pass reads it and the JSL is NOPped as LM NOPs it.
    /// </summary>
    private static void AppendV18Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        var records = new byte[(OwGfxRecordIndex + Overworld.Submaps) * 0x20];
        for (int submap = 0; submap < Overworld.Submaps; submap++)
            Array.Copy(OwGfxVanillaRecord, 0, records, (OwGfxRecordIndex + submap) * 0x20, 0x20);
        s.Add((GfxRecordsPc - 8, Rats(records)));
        s.Add((Pc(GfxArmStub), GfxCode(18)));                    // the loader's enable test
        s.Add((Pc(OwGfxStub), OwGfxArmStub(18)));
        s.Add((Pc(OwGfxHook), [0x22, OwGfxStub & 0xFF, OwGfxStub >> 8 & 0xFF, OwGfxStub >> 16]));
    }

    /// <summary>
    /// V19: make Lunar Magic's own *Overworld ▸ Submap GFX* dialog read and write the seven
    /// records v18 gave the submaps. Two stamps, both measured 2026-09-10 rather than reasoned
    /// (reference/LM_PARITY.md §2 has the experiments):
    ///
    /// - LM's layout stamp `"LM" 03 01` at <see cref="OwGfxMarker"/>. Without it the dialog shows
    ///   the vanilla lists and its save writes nothing; with it, LM reads the records, writes an
    ///   edited slot straight into our table, and — because it now believes the hack is installed
    ///   — leaves our loader, our stub and our records alone (an LM overworld save over a v18
    ///   ROM changed 56 runs, none of them in `$0FF7xx-$0FFDxx` or the table).
    /// - The stub reshaped to LM's own instruction sequence, because the dialog takes the record
    ///   address from the `ADC #imm` at `$0FFAB0+9` and the `LDA #imm` at `+18` and nowhere else:
    ///   handing it a different immediate moved which table the dialog displayed.
    ///
    /// This is NOT the loader transplant §2 called for, and the probe is why: with the stamp in,
    /// LM never installs its newer loader over ours, so the ~2KB of LM code that transplant would
    /// carry buys nothing the two fields above do not. What stays divergent is unchanged — the
    /// loader is still ours, and LM's *level* Super GFX Bypass dialog still does not read a
    /// level's record (a separate, older gap this does not touch).
    /// </summary>
    private static void AppendV19Stamps(List<(int Pc, byte[] Bytes)> s)
    {
        s.Add((Pc(OwGfxMarker), OwGfxMarkerBytes));
        s.Add((Pc(OwGfxStub), OwGfxArmStub(19)));
    }

    /// <summary>
    /// V20: make Lunar Magic's *Level ▸ Super GFX Bypass* dialog read a level's record too. One
    /// change, and it is a code motion rather than new behaviour: the loader's record fetch moves
    /// to the end of its block so the `LDA base,X` operand lands on <see cref="LmGfxBaseOperand"/>,
    /// the fixed address LM takes the table's address from.
    ///
    /// Measured 2026-09-10 (reference/LM_PARITY.md §2). Our loader has always carried the same
    /// fetch idiom — the one our own scanner keys on — just 0x67 bytes earlier, and LM's dialog
    /// showed every slot as 0 because it never looks there: planting the address at that one
    /// offset made the dialog read our records. It also settled that the record LAYOUT was right
    /// all along (w7 FG1 … w0 AN2, exactly CONTRACT §7d): with the v19 layout stamp in, LM's
    /// dialog reads the eleven slots off precisely those words. Without the stamp it reads an
    /// OLDER layout, one word over, which is what made the first measurement look like a bug in
    /// ours.
    /// </summary>
    private static void AppendV20Stamps(List<(int Pc, byte[] Bytes)> s)
        => s.Add((Pc(GfxArmStub), GfxCode(20)));

    /// <summary>
    /// The sixteen words Lunar Magic writes into every submap's record when it installs the hack,
    /// as bytes: AN2 = GFX14 (the overworld's animated-tile source), AN1/FG5/FG6 = 0x7F (no such
    /// slot), FG1-FG4 = tileset row 0x11's `1C 1D 08 1E`, SP1-SP4 = sprite set 0x11's
    /// `10 0F 1C 1D`, and layer 3's vanilla `28 29 2A 2B` in the tail. Vanilla's own lists, so
    /// they are constants here rather than read back out of the image being stamped.
    /// </summary>
    private static readonly byte[] OwGfxVanillaRecord = Convert.FromHexString(
        "1400" + "7F00" + "7F00" + "7F00" + "1E00" + "0800" + "1D00" + "1C00" +
        "1D00" + "1C00" + "0F00" + "1000" + "2B00" + "2A00" + "2900" + "2800");

    /// <summary>
    /// $0FFAB0: point Lunar Magic's record cache at the submap's record, arm $FE with the same
    /// fact for our own loader, and do the two displaced instructions' work.
    ///
    /// The first 35 bytes are LM's stub instruction for instruction, because **its dialog parses
    /// them**: the record's low 16 bits are the `ADC #imm` operand at +9/+10 and the bank is the
    /// `LDA #imm` operand at +18, and that is the only place LM looks for the address (measured
    /// 2026-09-10 — feeding it a different immediate moved which table the dialog showed). Only
    /// LM's closing `RTL` is replaced, by the $FE arming our loader reads.
    ///
    /// Entered with 16-bit A (the `REP #$20` at $00A132) and X = submap * 2, from vanilla's own
    /// `LDX $0DB3 : LDA $1F11,X : ASL : TAX` at $00A12A — the same place LM's overworld
    /// ExAnimation setup reads the submap from. LM's half takes X unmasked, as LM does; ours
    /// masks, because TAX with a 16-bit X carries the accumulator's high byte in.
    /// </summary>
    /// <param name="version">V18 wrote only the arming; the LM half arrived in v19, and v18's
    /// image stays byte-frozen (a v18 project's pinned base must still reproduce).</param>
    private static byte[] OwGfxArmStub(int version)
    {
        int record = GfxBypassRecords + OwGfxRecordIndex * 0x20;   // the main map's, submap 0
        var a = new Asm(OwGfxStub);
        a.StaDp(0x20);                             // displaced `STA $20` (16-bit)
        if (version >= 19)
            // ---- LM's own 35 bytes ($0FFAB0-$0FFAD2), the fields at +9/+10 and +18 being what
            // its Submap GFX dialog reads the record address out of ----
            a.Txa().Asl().Asl().Asl().Asl()            // submap * 2 → submap * 0x20
             .Clc().AdcImm16(record & 0xFFFF)          // [LM +9/+10] record low 16
             .StaLong(0x7FC006)                        // LM's record cache
             .Sep(0x20)                                // displaced `SEP #$20`
             .LdaImm8(record >> 16).StaLong(0x7FC008)  // [LM +18] its bank
             .LdaImm8(0x42).StaLong(0x7FC009)          // #$42 = "the overworld's load" (a level is #$41)
             .LdaImm8(0x00).StaLong(0x7FC00B)
             .Rep(0x20);                               // back to 16-bit for the arming below
        // ---- ...and in place of LM's RTL, the same fact in the form our loader reads ----
        a.Txa().AndImm16(0x00FF).Lsr()             // X = submap * 2 → submap
         .Clc().AdcImm16(OwGfxRecordIndex + 1)     // what the loader wants in $FE: index + 1
         .StaDp(0xFE)
         .Sep(0x20)                                // v18: the displaced `SEP #$20` lands here
         .Rtl();
        return a.Bytes();
    }

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
}
