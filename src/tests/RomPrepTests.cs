using Xunit;

namespace PipeDream.Tests;

/// <summary>Skips when the LM reference ROM (behavioral parity oracle) or the vanilla ROM
/// is absent — the DM16 parity tests need both.</summary>
public sealed class LmRefRomFactAttribute : FactAttribute
{
    public LmRefRomFactAttribute()
    {
        if (!File.Exists(TestRom.RealRomPath)) Skip = "real ROM not present: " + TestRom.RealRomPath;
        else if (!File.Exists(RomPrepTests.AfterRomPath)) Skip = "LM reference ROM not present: " + RomPrepTests.AfterRomPath;
    }
}

public class RomPrepTests
{
    public static string AfterRomPath => ReferenceRoms.LmAfter;

    /// <summary>Golden SHA-256 (headerless) of the V1-prepped vanilla US ROM (frozen stamp
    /// list, 2026-08-16, incl. the B-preservation fix and the second palette hook). V1
    /// projects pin this image — the list must reproduce it forever.</summary>
    private const string GoldenPrepV1Sha256 = "a73872c55badc79300a7858c812d47d7286f1412e01f9d015305ce78c4df8898";

    /// <summary>Golden SHA-256 (headerless) of the V2-prepped vanilla US ROM (V1 stamps +
    /// the in-game GFX stage, 2026-08-17). Any stamp drift fails here.</summary>
    private const string GoldenPrepV2Sha256 = "f8b57e912c501197a8ac4e4ff2df569acf06c87ee5aec66be3336991e5a61af9";

    /// <summary>Golden SHA-256 (headerless) of the V3-prepped vanilla US ROM (V2 stamps + the
    /// four-range Map16 lookup ladder, 2026-08-18).</summary>
    private const string GoldenPrepV3Sha256 = "aa39003b7d17d49bd083fa118e76ba11d5191dcf63769d811b2b44ff29afed11";

    /// <summary>Golden SHA-256 (headerless) of the V4-prepped vanilla US ROM (V3 stamps + the
    /// four-bit-plane GFX upload, 2026-08-24).</summary>
    private const string GoldenPrepV4Sha256 = "758f64eb509f5d67de1b41077adb93156b33b588e795899ba4306fa2f23bc94d";

    /// <summary>Golden SHA-256 (headerless) of the V5-prepped vanilla US ROM (V4 stamps + the
    /// Direct-Map16 handlers restamped clear of LM's access flag, 2026-08-24).</summary>
    private const string GoldenPrepV5Sha256 = "12380ddd0bfff9d32150206c4dd9e6ed9fa80f7d03a78058f15be4e7ae7046b3";

    /// <summary>Golden SHA-256 (headerless) of the V6-prepped vanilla US ROM (V5 stamps + every
    /// tile-planar GFX file stored 4bpp, 2026-08-25).</summary>
    private const string GoldenPrepV6Sha256 = "be34b9ef972baf618ae45c272ecdad27309f1c365fb5bb695d90e67349cbfefa";

    /// <summary>Golden SHA-256 (headerless) of the V7-prepped vanilla US ROM (V6 stamps + the
    /// screen-exit destination high bit, 2026-08-27).</summary>
    private const string GoldenPrepV7Sha256 = "8d8c98bd0f8fba1c1014678363f0a0ee8860c15efda95848850084f3ec605f6a";

    /// <summary>Golden SHA-256 (headerless) of the V8-prepped vanilla US ROM (V7 stamps + the
    /// 4bpp upload in Lunar Magic's shape, with vanilla's plane-3 swap baked into files $01/$17,
    /// 2026-08-27).</summary>
    private const string GoldenPrepV8Sha256 = "4d42db9e1616990255416ed927c70de54ee930f267398fd9941cde3f376d90dc";

    /// <summary>Golden SHA-256 (headerless) of the V9-prepped vanilla US ROM (V8 stamps + the
    /// checksum balance, which lands the ROM back on Super Mario World's own $A0DA, 2026-08-27).</summary>
    private const string GoldenPrepV9Sha256 = "052f0eff5302795306b7be42af15eae6d9d83b197ced651e7c39501d9314da4f";

    /// <summary>Golden SHA-256 (headerless) of the V10-prepped vanilla US ROM (V9 stamps + Lunar
    /// Magic's method-2 entrance routines, its separate midway routine and its level-entry engine,
    /// 2026-08-27).</summary>
    private const string GoldenPrepV10Sha256 = "3c862300858f50295c2bab79e2f47d9549c1f8891d9d91a9dcf832608869ca78";
    private const string GoldenPrepV11Sha256 = "59a429e2b88bcf69635608110e1c8e196d008397b9e5dec759df2f612989e254";
    private const string GoldenPrepV12Sha256 = "37a2fe4e90a8996a6e22a82a801e90f9fd7d9ad1554c265cb15dd89aa0619a92";
    private const string GoldenPrepV13Sha256 = "b509e09cf2fcb6b253a05a15858ea4b587bcd51e3c97531824ebc69c5956972d";
    private const string GoldenPrepV14Sha256 = "18db2e75e03fd3c053a595aff71a20309e48cd014718b482360e4c2eeaed8105";

    private static Rom Prepped()
    {
        var rom = TestRom.Create();
        RomPrep.Apply(rom);
        return rom;
    }

    private static Rom PreppedReal()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom);
        return rom;
    }

    // ---------------------------------------------------------------- unit

    [Fact]
    public void apply_is_deterministic_and_double_apply_is_a_noop()
    {
        var a = Prepped();
        var b = Prepped();
        Assert.Equal(a.Data, b.Data);

        var once = (byte[])a.Data.Clone();
        RomPrep.Apply(a);                        // IsPrepped → no-op
        Assert.Equal(once, a.Data);

        // Every released version is deterministic, and upgrading through them lands on
        // exactly the image a direct prep at the current version produces.
        var v1a = TestRom.Create(); RomPrep.Apply(v1a, 1);
        var v1b = TestRom.Create(); RomPrep.Apply(v1b, 1);
        Assert.Equal(v1a.Data, v1b.Data);
        for (int v = 2; v <= RomPrep.Version; v++) RomPrep.Apply(v1a, v);
        Assert.Equal(a.Data, v1a.Data);
    }

    [Fact]
    public void is_prepped_flips_after_apply_and_the_image_grows_to_1mb()
    {
        var rom = TestRom.Create();
        Assert.False(RomPrep.IsPrepped(rom));
        RomPrep.Apply(rom);
        Assert.True(RomPrep.IsPrepped(rom));
        Assert.Equal(0x100000, rom.ActualRomSize);
        Assert.Equal(0x0A, rom.Data[0x7FD7 + rom.HeaderOffset]);   // size code = 1MB
    }

    [Fact]
    public void prep_satisfies_the_lm_scanner_contracts()
    {
        var rom = Prepped();
        Assert.True(rom.HasDm16Hijack);
        Assert.Equal((0x7008, 0x12), rom.LmMap16Defs);
        Assert.Equal(0x300, rom.Map16TileCount);
        // Ranges 1-3 exist as slots but hold no defs, so they neither resolve nor extend
        // the count — they are the empty sockets EnsureMap16Tiles fills.
        Assert.Equal((0x0008, 0x00), rom.LmMap16Slot(1));
        Assert.True(rom.HasMap16Range(3) && !rom.HasMap16Range(4));
        Assert.Equal(RomPrep.ActsTableSnes, rom.LmActsAsBase);
        Assert.True(rom.HasLmPaletteHook);
        Assert.Equal(RomPrep.SpriteBankTable, rom.LmSpriteBankTable);

        // acts-like data: identity below 0x200, LM default 0x130 above
        Assert.Equal(0x000, rom.ActsAs(0x000));
        Assert.Equal(0x105, rom.ActsAs(0x105));
        Assert.Equal(0x1FF, rom.ActsAs(0x1FF));
        Assert.Equal(0x130, rom.ActsAs(0x205));
        Assert.Equal(0x130, rom.ActsAs(0x2FF));

        // extended defs: LM's default-empty word 0x1004 ×4 for the seeded page
        int fo = rom.FileOffset(0x128008);
        for (int i = 0; i < 8; i += 2) { Assert.Equal(0x04, rom.Data[fo + i]); Assert.Equal(0x10, rom.Data[fo + i + 1]); }

        // V2: GFX loader + zeroed tables (records disabled, no ExGFX inserted yet)
        Assert.True(rom.HasLmGfxLoader);
        Assert.Equal(RomPrep.GfxBypassRecords, rom.LmGfxBypassBase);
        Assert.Equal(RomPrep.ExGfxPtrTable, rom.LmExGfxBase);
        Assert.True(rom.HasLmVramPatch);             // v10 carries LM's VRAM patch (LmLevelRender): BG2/BG3 upload in-game
        Assert.Null(rom.LmGfxBypass(0x105));         // zeroed record = no bypass
        Assert.Equal(-1, Gfx.SourceSnes(rom, 0x100));
        Assert.Equal(-1, Gfx.SourceSnes(rom, 0x85));
    }

    [Fact]
    public void prep_triggers_no_false_positive_scanners()
    {
        var rom = Prepped();
        Assert.Equal(-1, rom.LmSpriteSizeBase);
        // V11 carries LM's ExAnimation engine, so these are real hits, at our addresses.
        Assert.Equal(LmExAnimEngine.TableSnes, rom.LmExAnimBase);
        Assert.Equal(-1, rom.LmGlobalExAnimPtr);          // no global list until one is written
        Assert.Equal(LmExAnimEngine.SetupEntry + 2, rom.LmExAnimSetupEntry);   // the PHB prologue after SEP #$30
        Assert.Equal(LmExAnimEngine.ProcEntry, rom.LmExAnimProcEntry);
        Assert.False(rom.HasPixiSpriteHook);
    }

    [Fact]
    public void prep_v1_stays_scanner_negative_for_the_v2_structures()
    {
        var rom = TestRom.Create();
        RomPrep.Apply(rom, 1);
        Assert.False(rom.HasLmGfxLoader);
        Assert.Equal(-1, rom.LmGfxBypassBase);
        Assert.Equal(-1, rom.LmExGfxBase);
        Assert.True(RomPrep.IsPrepped(rom, 1));
        Assert.False(RomPrep.IsPrepped(rom, 2));
    }

    [Fact]
    public void dispatch_and_ext_tables_point_at_the_documented_handlers()
    {
        var rom = Prepped();
        foreach (int d in new[] { 0x0DA44B, 0x0DC190, 0x0DCD90, 0x0DD990, 0x0DE890 })
        {
            Assert.Equal(RomPrep.Handler22, rom.ReadValue(d + 0x0A + (0x22 - 1) * 3, 3));
            Assert.Equal(RomPrep.Handler23, rom.ReadValue(d + 0x0A + (0x23 - 1) * 3, 3));
            Assert.Equal(RomPrep.Handler26, rom.ReadValue(d + 0x0A + (0x26 - 1) * 3, 3));
            Assert.Equal(RomPrep.Handler27, rom.ReadValue(d + 0x0A + (0x27 - 1) * 3, 3));
            Assert.Equal(RomPrep.Handler28, rom.ReadValue(d + 0x0A + (0x28 - 1) * 3, 3));
            Assert.Equal(RomPrep.Handler29, rom.ReadValue(d + 0x0A + (0x29 - 1) * 3, 3));
        }
        Assert.Equal(RomPrep.ExtHandler02, rom.ReadValue(0x0DA10F + 2 * 3, 3));
        Assert.Equal(RomPrep.ExtHandler03, rom.ReadValue(0x0DA10F + 3 * 3, 3));
    }

    [Fact]
    public void ensure_map16_tiles_grows_a_prepped_image()
    {
        var rom = Prepped();
        Assert.Null(rom.EnsureMap16Tiles(0x400));
        Assert.Equal(0x400, rom.Map16TileCount);
        var (imm, bank) = rom.LmMap16Defs;
        Assert.Equal(0x7008, imm);                 // fresh bank, data at bankaddr $8008
        Assert.True(bank >= 0x20);                 // appended past the 1MB image
        // old page contents copied: tile 0x205's def is still the empty fill
        int fo = rom.FileOffset((bank << 16) | (imm + 0x205 * 8));
        Assert.Equal(0x04, rom.Data[fo]);
        Assert.Equal(0x10, rom.Data[fo + 1]);
    }

    [Fact]
    public void write_lm_custom_palette_round_trips_on_a_prepped_image()
    {
        var rom = Prepped();
        Assert.Null(rom.LmCustomPalette(0x105));   // zeroed table = no custom palettes
        var colors = new ushort[256];
        for (int i = 0; i < 256; i++) colors[i] = (ushort)(0x1234 + i);
        rom.WriteLmCustomPalette(0x105, 0x7FFF, colors);
        var read = rom.LmCustomPalette(0x105);
        Assert.NotNull(read);
        Assert.Equal(0x7FFF, read.Value.Back);
        for (int i = 0; i < 256; i++)
            Assert.Equal((i & 15) == 0 ? 0 : colors[i], read.Value.Colors[i]);   // row color 0 stored 0
    }

    [Fact]
    public void prep_in_place_refuses_a_non_vanilla_base()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "pd_prep_refuse.smc");
        File.WriteAllBytes(tmp, TestRom.Image());
        try { Assert.NotNull(RomPrep.PrepInPlace(tmp)); }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void disasm_shows_the_expected_instruction_streams()
    {
        var rom = Prepped();

        // V12: LM's own ladder. The $06F5D0 wrapper (the vanilla hook's target) hands the tile to
        // LM's entry at $06F540 and moves the bank from LM's $0B into the caller's $05.
        string lookup = Disasm.Dis(rom, RomPrep.Map16LookupEntry, 8, m8: true, x8: false);
        Assert.Contains("REP #$20", lookup);
        Assert.Contains("BRL $06F540", lookup);
        Assert.Contains("STY $05", lookup);
        string entry = Disasm.Dis(rom, 0x06F540, 12, m8: false, x8: false);
        Assert.Contains("CMP #$0400", entry);        // tiles < 0x200 take the vanilla $0FBE path
        Assert.Contains("ASL", entry);               // then two shifts pick the range slot
        Assert.Contains("STY $0B", entry);
        Assert.Contains("LDA $0FBE,Y", Disasm.Dis(rom, 0x06F5B9, 10, m8: false, x8: false));

        string extdef = Disasm.Dis(rom, 0x06F552, 4, m8: false, x8: false);
        Assert.Contains("ADC #$7008", extdef);
        Assert.Contains("LDY #$1200", extdef);
        // Ranges 1-3 are real code, so EnsureMap16Tiles' repatch lands on instructions.
        foreach (int slot in new[] { 0x06F55B, 0x06F566, 0x06F56F })
        {
            string s = Disasm.Dis(rom, slot, 4, m8: false, x8: false);
            Assert.Contains("ADC #$0008", s);
            Assert.Contains("LDY #$0000", s);
        }

        string remap = Disasm.Dis(rom, RomPrep.ActsRemapEntry, 22, m8: true, x8: true);
        Assert.Contains("LDA $118000,X", remap);
        Assert.Contains("CMP #$0200", remap);
        Assert.Contains("JML $00F545", remap);

        string fill = Disasm.Dis(rom, RomPrep.Handler22, 70, m8: true, x8: true);
        Assert.Contains("STA [$6E],Y", fill);
        Assert.Contains("JSR $FEA0", fill);         // LM's step helpers (v10 restamps LM's own DM16 handlers)
        Assert.Contains("JSR $FED0", fill);
        Assert.Contains("LDA [$65],Y", fill);       // stream extras

        string ext02 = Disasm.Dis(rom, RomPrep.ExtHandler02, 22, m8: true, x8: true);
        Assert.Contains("STA $19B8,X", ext02);
        Assert.Contains("STA $19D8,X", ext02);

        // v10 restamps LM's own ext 01/03 handlers: both set $8B (the 32-row band) and share a tail.
        string ext03 = Disasm.Dis(rom, RomPrep.ExtHandler03, 6, m8: true, x8: true);
        Assert.Contains("STA $8B", ext03);
        Assert.Contains("BRA $0DE1D9", ext03);
        string ext01 = Disasm.Dis(rom, 0x0DE1D0, 7, m8: true, x8: true);
        Assert.Contains("STA $8B", ext01);
        Assert.Contains("STA $1928", ext01);
        Assert.Contains("STA $1BA1", ext01);

        string stub = Disasm.Dis(rom, RomPrep.SpriteStub, 8, m8: true, x8: false);
        Assert.Contains("LDA $F100,Y", stub);
        Assert.Contains("STA $D0", stub);
        string word = Disasm.Dis(rom, RomPrep.LmLevelWordStub, 6, m8: false, x8: false);
        Assert.Contains("STA $010B", word);

        string pal = Disasm.Dis(rom, RomPrep.PalApply, 30, m8: true, x8: true);
        Assert.Contains("LDA $010B", pal);
        Assert.Contains("STA $0701,Y", pal);
        Assert.Contains("CPY #$0202", pal);

        string arm = Disasm.Dis(rom, RomPrep.GfxArmStub, 8, m8: true, x8: true);
        Assert.Contains("LDA $010B", arm);
        Assert.Contains("STA $FE", arm);
        Assert.Contains("CMP #$09", arm);

        string loader = Disasm.Dis(rom, RomPrep.GfxLoaderEntry, 50, m8: true, x8: true);
        Assert.Contains("LDA $FE", loader);
        Assert.Contains("LDA $129000,X", loader);
        Assert.Contains("AND #$8000", loader);
        Assert.Contains("JSL $00FF9A", loader);
        Assert.Contains("STA $2117", loader);

        string res = Disasm.Dis(rom, RomPrep.GfxResolve, 45, m8: false, x8: false);
        Assert.Contains("SBC #$0100", res);          // the LmExGfxBase scanner idiom
        Assert.Contains("LDA $138008,X", res);
        Assert.Contains("LDA $00B992,X", res);       // vanilla pointer tables
    }

    [Fact]
    public void sprite_stub_sets_bank_and_level_word()
    {
        var rom = Prepped();
        rom.Data[rom.FileOffset(RomPrep.SpriteBankTable + 0x42)] = 0x13;   // relocated level
        var cpu = new Cpu65816(rom);
        cpu.Ram7E[0x0E] = 0x42;                    // level word low (loader sets $0E)
        // LM's two-part shape (v10): $05D8E2 JSLs $0EF550 with 16-bit A for the level word, then
        // $05D8F5 JSLs the $0EF300 stub for the sprite bank.
        cpu.PresetWidths(m8: false, x8: false);
        cpu.CallLong(RomPrep.LmLevelWordStub, 100_000);
        cpu.PresetWidths(m8: true, x8: false);
        cpu.CallLong(RomPrep.SpriteStub, 100_000);
        Assert.Equal(0x13, cpu.Ram7E[0xD0]);
        Assert.Equal(0x42, cpu.Ram7E[0x010B]);
        Assert.Equal(0x00, cpu.Ram7E[0x010C]);

        cpu = new Cpu65816(rom);
        cpu.Ram7E[0x0E] = 0x07;                    // untouched level → vanilla bank $07
        cpu.CallLong(RomPrep.SpriteStub, 100_000);
        Assert.Equal(0x07, cpu.Ram7E[0xD0]);
    }

    [Fact]
    public void palette_apply_copies_the_blob_and_ignores_null_pointers()
    {
        var rom = Prepped();
        var colors = new ushort[256];
        for (int i = 0; i < 256; i++) colors[i] = (ushort)(0x4000 | i);
        rom.WriteLmCustomPalette(0x05, 0x2345, colors);

        var cpu = new Cpu65816(rom);
        cpu.Ram7E[0x010B] = 0x05;
        cpu.CallNear(RomPrep.PalApply, 200_000);
        Assert.Equal(0x45, cpu.Ram7E[0x0701]);     // back-area color word
        Assert.Equal(0x23, cpu.Ram7E[0x0702]);
        for (int i = 1; i < 256; i++)              // colors at $0703+ (row color 0 stored 0)
        {
            int expect = (i & 15) == 0 ? 0 : 0x4000 | i;
            Assert.Equal(expect, cpu.Ram7E[0x0703 + i * 2] | (cpu.Ram7E[0x0704 + i * 2] << 8));
        }

        // level without a custom palette (pointer 0): staging untouched
        cpu = new Cpu65816(rom);
        cpu.Ram7E[0x010B] = 0x06;
        cpu.Ram7E[0x0701] = 0xAB; cpu.Ram7E[0x0703] = 0xCD;
        cpu.CallNear(RomPrep.PalApply, 200_000);
        Assert.Equal(0xAB, cpu.Ram7E[0x0701]);
        Assert.Equal(0xCD, cpu.Ram7E[0x0703]);

        // pointer FFFFFF: also untouched
        int tfo = rom.FileOffset(LunarMagic.LmPaletteTable + 0x07 * 3);
        rom.Data[tfo] = rom.Data[tfo + 1] = rom.Data[tfo + 2] = 0xFF;
        cpu = new Cpu65816(rom);
        cpu.Ram7E[0x010B] = 0x07;
        cpu.Ram7E[0x0701] = 0xAB;
        cpu.CallNear(RomPrep.PalApply, 200_000);
        Assert.Equal(0xAB, cpu.Ram7E[0x0701]);
    }

    /// <summary>
    /// Run the inserted Map16 def lookup and check it returns the address the C# reader
    /// predicts, for a tile in EVERY range. The dispatcher derives the range from the carry
    /// and sign the two shifts produce as a side effect, which is compact but entirely
    /// hand-derived — the only honest check is executing it.
    ///
    /// Entry contract (from the vanilla consumer at $00C143-$00C17A): M 8-bit, X/Y 16-bit,
    /// Y = tile*2, $06 pre-set to the def bank. Exit: 16-bit A = def low16, $06 = def bank.
    /// </summary>
    [Fact]
    public void the_inserted_lookup_dispatches_every_range_to_its_own_slot()
    {
        var rom = Prepped();
        Assert.Null(rom.EnsureMap16Tiles(0x3100));         // populate all four ranges
        foreach (int r in new[] { 0, 1, 2, 3 })
            Assert.NotEqual(0, rom.LmMap16Slot(r).Bank);

        (int addr, int bank) Run(int tile)
        {
            var cpu = new Cpu65816(rom);
            cpu.PresetWidths(m8: true, x8: false);
            cpu.PresetY(tile * 2);
            cpu.Ram7E[0x06] = 0x0D;                        // vanilla pre-set (def bank)
            cpu.CallLong(RomPrep.Map16LookupEntry, 100_000);
            return (cpu.Acc & 0xFFFF, cpu.Ram7E[0x06]);
        }

        // One tile per range, plus each range's first and last, all against the reader.
        foreach (int tile in new[] { 0x200, 0x205, 0xFFF, 0x1000, 0x1234, 0x1FFF,
                                     0x2000, 0x2ABC, 0x2FFF, 0x3000, 0x30FF })
        {
            int want = rom.LmMap16DefAddr(tile);
            Assert.True(want > 0, $"reader has no def for tile {tile:X}");
            Assert.Equal((want & 0xFFFF, want >> 16), Run(tile));
        }

        // Tiles below 0x200 still take the vanilla $0FBE RAM-table path, untouched.
        var cpu0 = new Cpu65816(rom);
        cpu0.PresetWidths(m8: true, x8: false);
        cpu0.PresetY(0x105 * 2);
        cpu0.Ram7E[0x0FBE + 0x105 * 2] = 0x34;
        cpu0.Ram7E[0x0FBF + 0x105 * 2] = 0x12;
        cpu0.CallLong(RomPrep.Map16LookupEntry, 100_000);
        Assert.Equal(0x1234, cpu0.Acc & 0xFFFF);

        // 0x4000+ falls into LM's high-range slots, which no allocation of ours ever fills: bank
        // 0 = "no defs here", exactly what HasMap16Range/LmMap16DefAddr say about those ranges.
        Assert.Equal(0x00, Run(0x4000).bank);
        Assert.Equal(0x00, Run(0x7FFF).bank);
    }

    // ---------------------------------------------------------------- real ROM

    [RealRomFact]
    public void vanilla_stamp_targets_hold_freespace_or_documented_placeholders()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        void AllFf(int snes, int len)
        {
            int fo = rom.FileOffset(snes);
            for (int i = 0; i < len; i++) Assert.Equal(0xFF, rom.Data[fo + i]);
        }
        AllFf(0x06F538, 0x118);                    // range dispatch + Map16 lookup + acts remap
        AllFf(RomPrep.ExtHandler02, 0x50);         // ext handlers
        AllFf(RomPrep.Handler22, 0x276);           // DM16 handlers ($0DF08A-$0DF2FF)
        AllFf(RomPrep.Handler29, 0xB0);            // BG form
        AllFf(RomPrep.SpriteBankTable, 0x200);     // bank table
        AllFf(RomPrep.SpriteStub, 0x40);           // sprite stub
        AllFf(LunarMagic.LmPaletteTable, 0x600);   // palette pointer table
        AllFf(RomPrep.PalTrampoline, 0x100);       // palette stubs
        AllFf(RomPrep.PalThunk, 0x07);             // bank-00 thunk
        AllFf(RomPrep.GfxThunks, 0x08);            // V2 bank-00 thunks
        AllFf(Gfx.ExGfx80Table, 0x180);            // V2 ExGFX 0x80-0xFF table
        AllFf(RomPrep.GfxArmStub, 0x150);          // V2 armstub + loader + resolver + SlotTab

        // patch sites hold the exact vanilla bytes we displace
        Assert.Equal([0xC2, 0x20, 0xB9, 0xBE, 0x0F], rom.Data.AsSpan(rom.FileOffset(0x00C17A), 5).ToArray());
        Assert.Equal([0x20, 0xDA, 0xA9, 0x20, 0xED, 0xAB], rom.Data.AsSpan(rom.FileOffset(0x0095E9), 6).ToArray());
        Assert.Equal([0x22, 0x8A, 0xBE, 0x05], rom.Data.AsSpan(rom.FileOffset(0x00A5BF), 4).ToArray());
        // V2 hook sites: displaced vanilla bytes
        Assert.Equal([0xAD, 0x25, 0x19, 0xC9, 0x09], rom.Data.AsSpan(rom.FileOffset(0x0583B8), 5).ToArray());
        Assert.Equal([0xA2, 0x03, 0xB5, 0x04, 0x9D, 0x05, 0x01, 0xCA, 0x10, 0xF8],
                     rom.Data.AsSpan(rom.FileOffset(0x00AA50), 10).ToArray());
        Assert.Equal([0xF0, 0x03], rom.Data.AsSpan(rom.FileOffset(0x00AA06), 2).ToArray());
        Assert.Equal([0xF0, 0x03], rom.Data.AsSpan(rom.FileOffset(0x00AA47), 2).ToArray());
        Assert.Equal([0xA9, 0x07, 0x85, 0xD0], rom.Data.AsSpan(rom.FileOffset(0x05D8F5), 4).ToArray());
        foreach (int site in RomPrep.ActsCallSites)
            Assert.Equal([0x22, 0x45, 0xF5, 0x00], rom.Data.AsSpan(rom.FileOffset(site), 4).ToArray());

        // dispatch-table entries: vanilla placeholder $0DB3E3; ext 0x02/0x03: empty
        foreach (int d in new[] { 0x0DA44B, 0x0DC190, 0x0DCD90, 0x0DD990, 0x0DE890 })
            foreach (int obj in new[] { 0x22, 0x23, 0x26, 0x27, 0x28, 0x29 })
                Assert.Equal(0x0DB3E3, rom.ReadValue(d + 0x0A + (obj - 1) * 3, 3));
        Assert.Equal(0, rom.ReadValue(0x0DA115, 3));
        Assert.Equal(0, rom.ReadValue(0x0DA118, 3));
    }

    [RealRomFact]
    public void prepped_vanilla_matches_the_golden_hashes_for_both_versions()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "pd_prep_golden.smc");
        try
        {
            File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
            Assert.Null(RomPrep.PrepInPlace(tmp, version: 1));      // frozen V1 stamp list
            Assert.Equal(GoldenPrepV1Sha256, RomHash.HeaderlessSha256File(tmp));

            File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
            Assert.Null(RomPrep.PrepInPlace(tmp, version: 2));      // frozen V2 stamp list
            Assert.Equal(GoldenPrepV2Sha256, RomHash.HeaderlessSha256File(tmp));

            File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
            Assert.Null(RomPrep.PrepInPlace(tmp, version: 3));      // frozen V3 stamp list
            Assert.Equal(GoldenPrepV3Sha256, RomHash.HeaderlessSha256File(tmp));

            File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
            Assert.Null(RomPrep.PrepInPlace(tmp, version: 4));      // frozen V4 stamp list
            Assert.Equal(GoldenPrepV4Sha256, RomHash.HeaderlessSha256File(tmp));

            File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
            Assert.Null(RomPrep.PrepInPlace(tmp, version: 5));      // frozen V5 stamp list
            Assert.Equal(GoldenPrepV5Sha256, RomHash.HeaderlessSha256File(tmp));

            File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
            Assert.Null(RomPrep.PrepInPlace(tmp, version: 6));      // frozen V6 stamp list
            Assert.Equal(GoldenPrepV6Sha256, RomHash.HeaderlessSha256File(tmp));

            File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
            Assert.Null(RomPrep.PrepInPlace(tmp, version: 7));      // frozen V7 stamp list
            Assert.Equal(GoldenPrepV7Sha256, RomHash.HeaderlessSha256File(tmp));

            File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
            Assert.Null(RomPrep.PrepInPlace(tmp, version: 8));      // frozen V8 stamp list
            Assert.Equal(GoldenPrepV8Sha256, RomHash.HeaderlessSha256File(tmp));

            File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
            Assert.Null(RomPrep.PrepInPlace(tmp, version: 9));      // frozen V9 stamp list
            Assert.Equal(GoldenPrepV9Sha256, RomHash.HeaderlessSha256File(tmp));

            File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
            Assert.Null(RomPrep.PrepInPlace(tmp, version: 10));     // frozen V10 stamp list
            Assert.Equal(GoldenPrepV10Sha256, RomHash.HeaderlessSha256File(tmp));

            File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
            Assert.Null(RomPrep.PrepInPlace(tmp, version: 11));     // frozen V11 stamp list
            Assert.Equal(GoldenPrepV11Sha256, RomHash.HeaderlessSha256File(tmp));

            File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
            Assert.Null(RomPrep.PrepInPlace(tmp, version: 12));     // frozen V12 stamp list
            Assert.Equal(GoldenPrepV12Sha256, RomHash.HeaderlessSha256File(tmp));

            File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
            Assert.Null(RomPrep.PrepInPlace(tmp, version: 13));     // frozen V13 stamp list
            Assert.Equal(GoldenPrepV13Sha256, RomHash.HeaderlessSha256File(tmp));

            File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
            Assert.Null(RomPrep.PrepInPlace(tmp));                  // current (V14)
            string v14 = RomHash.HeaderlessSha256File(tmp);
            // Spelled out rather than left to the assertion message: xunit truncates a mismatch,
            // and this hash is what the NEXT version bump has to be told.
            Assert.True(GoldenPrepV14Sha256 == v14, $"V14 golden hash is now {v14}");
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>
    /// V14: the layer-3 pass. Three properties, and the third is the one that is easy to miss —
    /// a level that does NOT bypass has to get 28-2B put back, or the last bypassed level's
    /// layer 3 follows the player into the next one. That is why LM re-uploads unconditionally
    /// and falls back to a default record, and why this runs on every armed load.
    ///
    /// Asserted through VramLog rather than the decompression buffer: four slots share that
    /// buffer, so only the last one is still in it when the routine returns, whereas VRAM is
    /// where the contract actually lives.
    /// </summary>
    [RealRomFact]
    public void layer_3_slots_upload_to_their_vram_pages_and_fall_back_to_the_vanilla_files()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom);
        Assert.True(rom.HasLmLayer3Gfx);

        int rfo = rom.FileOffset(RomPrep.GfxBypassRecords + 5 * 0x20);
        for (int w = 0; w < 16; w++) { rom.Data[rfo + w * 2] = 0x7F; rom.Data[rfo + w * 2 + 1] = 0; }

        Cpu65816 Run(int w0hi)
        {
            rom.Data[rfo + 1] = (byte)w0hi;
            var cpu = new Cpu65816(rom) { VramLog = [] };
            cpu.Ram7E[0xFE] = 6;                                   // level 5 + 1
            cpu.Ram7E[0x1931] = 0;
            cpu.CallLong(RomPrep.GfxLoaderEntry, 40_000_000);
            return cpu;
        }

        // Bypass off: the four vanilla files, in LG1..LG4 order, 0x800 bytes each.
        var expect = new List<byte>();
        foreach (int f in Layer3.VanillaGfx) expect.AddRange(Gfx.DecompressFile(rom, f)[..0x800]);
        Assert.Equal(expect, Run(0x00).VramLog);

        // Bit 14 on with every slot left at 0x7F means the same thing — "this slot keeps its
        // vanilla file" — so the bytes must not move.
        Assert.Equal(expect, Run(0x40).VramLog);

        // Repoint LG3 (w13) at a vanilla file of its own; only that quarter changes.
        rom.Data[rfo + 13 * 2] = 0x00;                             // w13 = GFX 00
        var moved = Run(0x40).VramLog!;
        Assert.Equal(expect[..0x1000], moved[..0x1000]);           // LG1, LG2 untouched
        Assert.Equal(Gfx.DecompressFile(rom, 0)[..0x800], moved[0x1000..0x1800]);
        Assert.Equal(expect[0x1800..], moved[0x1800..]);           // LG4 untouched
    }

    /// <summary>SlotTab must send each record word to the VRAM page vanilla would have used
    /// for that GFXLIST index. $00A9E7/$00AA28 fill $04-$07 backwards (STA $04,X, X counting
    /// 3→0, index counting up) and the upload loop pairs file $04,X with page table entry X,
    /// so GFXLIST index i uses page-table entry 3-i. Derived from the ROM's own tables here
    /// rather than restated as constants — a silently wrong page uploads imported GFX to
    /// VRAM nobody reads, which looks exactly like "the loader never ran".</summary>
    [RealRomFact]
    public void slot_table_pairs_each_slot_with_the_vanilla_vram_page()
    {
        var rom = PreppedReal();
        int tab = rom.FileOffset(RomPrep.GfxSlotTab);
        // record byte-offsets in SlotTab order: FG1,FG2,BG1,FG3 then SP1..SP4
        int[] recOff = [0x0E, 0x0C, 0x0A, 0x08, 0x16, 0x14, 0x12, 0x10];
        for (int i = 0; i < 8; i++)
        {
            bool sprite = i >= 4;
            int pageTable = sprite ? 0x00A9D2 : 0x00A9D6;   // DATA_00A9D2 / DATA_00A9D6
            int expected = rom.ReadByte(pageTable + (3 - i % 4));
            Assert.Equal(recOff[i], rom.Data[tab + i * 2]);
            Assert.Equal(expected, rom.Data[tab + i * 2 + 1]);
        }
    }

    [RealRomFact]
    public void gfx_arm_stub_arms_fe_and_preserves_the_mode_compare()
    {
        var rom = PreppedReal();
        (int Fe, byte Marker) Run(int mode)
        {
            var cpu = new Cpu65816(rom);
            cpu.Ram7E[0x010B] = 0x05; cpu.Ram7E[0x010C] = 0x01;    // level 0x105
            cpu.Ram7E[0x1925] = (byte)mode;
            // driver: JSL armstub : BEQ eq : LDA #$01 : BRA w / eq: LDA #$02 / w: STA $7FF000 : RTL
            byte[] d =
            [
                0x22, 0x70, 0xF7, 0x0F,        // JSL GfxArmStub
                0xF0, 0x04,                    // BEQ eq (flags must survive the RTL)
                0xA9, 0x01, 0x80, 0x02,        // LDA #$01 : BRA w
                0xA9, 0x02,                    // eq: LDA #$02
                0x8F, 0x00, 0xF0, 0x7F,        // w: STA $7FF000
                0x6B,
            ];
            d.CopyTo(cpu.Ram7F, 0x9000);
            cpu.CallLong(0x7F9000, 100_000);
            return (cpu.Ram7E[0xFE] | (cpu.Ram7E[0xFF] << 8), cpu.Ram7F[0xF000]);
        }
        Assert.Equal((0x0106, (byte)0x02), Run(0x09));   // boss mode: displaced CMP sets Z
        Assert.Equal((0x0106, (byte)0x01), Run(0x00));   // normal mode: Z clear
    }

    /// <summary>THE V2 emulator end-to-end: armed record + pointer + compressed blob →
    /// the loader decompresses the import to $7E:AD00 and runs the vanilla expand-upload
    /// over the full file. The arm persists (the fade-in load step re-runs UploadSpriteGFX
    /// with the cache tests NOPped, so the record must re-apply there too — LM lifecycle);
    /// unarmed and disabled variants touch nothing.</summary>
    [RealRomFact]
    public void gfx_loader_uploads_an_armed_override_end_to_end()
    {
        var rom = PreppedReal();
        // V4 reads FOUR bit planes, so a full file — and what the uploader consumes — is
        // 128 tiles x 32 bytes. (Through v3 this was 0xC00: RomBpp 3 x 8 x 128.)
        int full = 128 * Gfx.TileBytes(4);
        var import = new byte[0x400];                              // partial file: zero-padded
        for (int i = 0; i < import.Length; i++) import[i] = (byte)(i * 7 + 3);
        var padded = new byte[full];
        import.CopyTo(padded, 0);
        int blobSnes = RatsWriter.Allocate(rom, Gfx.Lz2Compress(padded));

        int pfo = rom.FileOffset(RomPrep.ExGfxPtrTable);           // ExGFX 0x100 pointer
        rom.Data[pfo] = (byte)blobSnes; rom.Data[pfo + 1] = (byte)(blobSnes >> 8); rom.Data[pfo + 2] = (byte)(blobSnes >> 16);

        int rfo = rom.FileOffset(RomPrep.GfxBypassRecords + 5 * 0x20);   // level 5 record
        for (int w = 0; w < 16; w++) { rom.Data[rfo + w * 2] = 0x7F; rom.Data[rfo + w * 2 + 1] = 0; }
        rom.Data[rfo + 1] = 0xC0;                                  // w0 = 0xC07F: both enables
        rom.Data[rfo + 0x0E] = 0x00; rom.Data[rfo + 0x0F] = 0x01;  // FG1 (w7) = file 0x100
        // V14's layer-3 pass runs on EVERY armed load and shares the decompression buffer, so
        // it would overwrite what this test reads there. Point its four slots at a dead id
        // (0x34-0x7F resolves to "skip") to hold it inert — the layer-3 half has its own test.
        for (int w = 12; w <= 15; w++) { rom.Data[rfo + w * 2] = 0x40; rom.Data[rfo + w * 2 + 1] = 0x00; }

        Cpu65816 Armed()
        {
            var cpu = new Cpu65816(rom);
            cpu.Ram7E[0xFE] = 6;                                   // level 5 + 1
            cpu.Ram7E[0x1931] = 0;                                 // tileset (expander filter path)
            return cpu;
        }

        // V13 decompresses where vanilla and LM do, $7E:AD00 (V4-V12 had $7F:A000, which the
        // overworld's own reader never followed). Addressed off the constant so a move cannot rot this.
        int bufAddr = RomPrep.GfxBuffer & 0xFFFF;
        static byte[] Buf(Cpu65816 c) => (RomPrep.GfxBuffer >> 16) == 0x7F ? c.Ram7F : c.Ram7E;

        var c = Armed();
        c.CallLong(RomPrep.GfxLoaderEntry, 20_000_000);
        for (int i = 0; i < full; i++)
            if (Buf(c)[bufAddr + i] != padded[i])
                Assert.Fail($"upload buffer diverges at +{i:X}: {Buf(c)[bufAddr + i]:X2} != {padded[i]:X2}");
        Assert.Equal(6, c.Ram7E[0xFE] | (c.Ram7E[0xFF] << 8));     // arm persists
        Assert.Equal(bufAddr + full, c.Ram7E[0x00] | (c.Ram7E[0x01] << 8));  // expander consumed all

        // second call re-applies (the fade-in step's re-upload needs this)
        Buf(c)[bufAddr] ^= 0xFF;
        c.CallLong(RomPrep.GfxLoaderEntry, 20_000_000);
        Assert.Equal(padded[0], Buf(c)[bufAddr]);

        // unarmed: nothing happens
        var u = Armed(); u.Ram7E[0xFE] = 0; Buf(u)[bufAddr] = 0xEE;
        u.CallLong(RomPrep.GfxLoaderEntry, 20_000_000);
        Assert.Equal(0xEE, Buf(u)[bufAddr]);

        // disabled record (w0 bit15 clear): nothing happens. Bit 14 stays on so the layer-3
        // pass keeps taking its dead slots rather than falling back to the vanilla 28-2B.
        rom.Data[rfo + 1] = 0x40;
        var dis = Armed(); Buf(dis)[bufAddr] = 0xEE;
        dis.CallLong(RomPrep.GfxLoaderEntry, 20_000_000);
        Assert.Equal(0xEE, Buf(dis)[bufAddr]);
        rom.Data[rfo + 1] = 0xC0;

        // vanilla-file override resolves through the vanilla tables (filters keep working)
        rom.Data[rfo + 0x0E] = 0x02; rom.Data[rfo + 0x0F] = 0x00;  // FG1 = vanilla GFX02
        var v = Armed();
        v.CallLong(RomPrep.GfxLoaderEntry, 20_000_000);
        byte[] gfx2 = Gfx.DecompressFile(rom, 2);
        for (int i = 0; i < gfx2.Length; i++)
            if (Buf(v)[bufAddr + i] != gfx2[i])
                Assert.Fail($"vanilla-file upload diverges at +{i:X}");
    }

    /// <summary>
    /// V4's 4bpp upload is PARITY, not new behaviour: uploading a converted (3bpp→4bpp,
    /// plane 3 zero-filled) file through v4 must put byte-identical data in VRAM to uploading
    /// the original 3bpp file through v3 — for a plain file AND for every vanilla filter case,
    /// where v3 synthesizes plane 3 from (plane0|plane1) and v4 has to keep doing so.
    ///
    /// This is the test that says the swapped inner loop is right. It compares the bytes the
    /// routine SENT to the VRAM data port, not the buffer it read — the buffer is identical by
    /// construction and would prove nothing.
    /// </summary>
    [RealRomFact]
    public void v4_uploads_a_converted_file_byte_identically_to_v3()
    {
        // Files 0x01 and 0x17 filter tiles 0x6E/0x6F/0x7E/0x7F; 0x1E filters every tile;
        // 0x08 filters only on tileset >= 0x11 — so it is run at both tilesets.
        foreach (var (file, tileset) in new[] { (0x02, 0x00), (0x01, 0x00), (0x17, 0x00),
                                               (0x1E, 0x00), (0x08, 0x00), (0x08, 0x11) })
        {
            var v3 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v3, 3);
            var v4 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v4, 4);
            Assert.False(v3.HasGfx4bppUpload);
            Assert.True(v4.HasGfx4bppUpload);

            // v4's ROM carries the SAME file converted to 4bpp, as a project import would.
            byte[] src3 = Gfx.DecompressFile(v3, file);
            v4.ImportedGfx[file] = Gfx.NormalizeBpp(src3, 3, 4, out bool dropped);
            Assert.False(dropped);
            Gfx.InvalidateCache(v4);

            var a = UploadVram(v3, file, tileset, src3);
            var b = UploadVram(v4, file, tileset, v4.ImportedGfx[file]);
            Assert.Equal(0x1000, a.Count);                  // 128 tiles × 32 VRAM bytes
            if (!a.SequenceEqual(b))
            {
                int at = a.Zip(b).ToList().FindIndex(p => p.First != p.Second);
                Assert.Fail($"file {file:X2} tileset {tileset:X2}: VRAM diverges at +{at:X} "
                          + $"(tile {at / 32:X2} byte {at % 32:X2}): v3 {a[at]:X2} != v4 {b[at]:X2}");
            }
        }
    }


    /// <summary>
    /// V6 converts the FILES to 4bpp — the half of v4 that was missing, and without which a
    /// prepped base uploads 32-byte tiles read out of 24-byte storage.
    ///
    /// Per file: every tile-planar id decompresses from the converted base to exactly
    /// NormalizeBpp(original), and every id the list excludes is left completely alone —
    /// same pointer, same bytes. Those exclusions are not fussiness: layer 3 (0x28-0x2B) and
    /// 0x2F are 2bpp, 0x27 is Mode 7, and 0x32/0x33 are the animation blobs, each read by a
    /// routine that is not the tile uploader.
    /// </summary>
    [RealRomFact]
    public void v6_converts_every_tile_planar_file_and_leaves_the_rest_alone()
    {
        var v5 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v5, 5);
        var v6 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v6, 6);

        Assert.Equal(3, Gfx.RomBpp(v5));
        Assert.Equal(4, Gfx.RomBpp(v6));               // ...which is what opens colours 8-15
        Assert.True(RomPrep.IsPrepped(v6, 6));
        Assert.False(RomPrep.IsPrepped(v5, 6));

        int converted = 0;
        for (int id = 0; id < Gfx.Count; id++)
        {
            if (Gfx.SourceSnes(v5, id) <= 0) continue;
            byte[] before = Gfx.DecompressFile(v5, id);
            if (!Gfx.IsTilePlanar3Bpp(id))
            {
                Assert.Equal(Gfx.SourceSnes(v5, id), Gfx.SourceSnes(v6, id));    // never moved
                Assert.Equal(before, Gfx.DecompressFile(v6, id));
                continue;
            }
            byte[] want = Gfx.NormalizeBpp(before, 3, 4, out bool dropped);
            Assert.False(dropped);
            Assert.Equal(want, Gfx.DecompressFile(v6, id));
            Assert.NotEqual(Gfx.SourceSnes(v5, id), Gfx.SourceSnes(v6, id));     // repointed
            converted++;
        }
        Assert.True(converted > 40, $"only {converted} files converted");

        // The converted data parks past the prep's tables, so RomBuilder's first-fit run at
        // 0x80000 is still free for the levels and palettes it allocates there.
        Assert.True(RatsWriter.FindFreeSpace(v6, 0x1000) < RomPrep.GfxConvertBase);
    }

    /// <summary>

    /// <summary>
    /// The conversion is LOSSLESS at the pixel level, so a v6 base must draw exactly what a v5
    /// base drew — including the animated tiles, which is where this first went wrong: the
    /// animation source is one of the files v6 skips (its reader at $00B8AD does its own
    /// 3bpp-to-4bpp step and prep never patched it), so once RomBpp read 4 the overlay decoded
    /// a 24-byte-per-tile blob at 32 and garbled every muncher, lava tile and question block in
    /// the level view.
    /// </summary>
    [RealRomFact]
    public void v6_draws_every_tile_including_the_animated_ones_exactly_as_v5_did()
    {
        var v5 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v5, 5);
        var v6 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v6, 6);
        Assert.Equal(3, Gfx.FileBpp(v6, 0x33));         // the AN1 blob (GFX33 in LM's numbering) stayed three planes deep
        Assert.Equal(4, Gfx.RomBpp(v6));                // ...even though the tile files did not

        foreach (int tileset in new[] { 0x00, 0x01, 0x05 })
            foreach (int phase in new[] { 0, 1, 2, 3 })
            {
                var a = Gfx.FgTiles.Load(v5, tileset, level: 0x105, animPhase: phase);
                var b = Gfx.FgTiles.Load(v6, tileset, level: 0x105, animPhase: phase);
                for (int tile = 0; tile < 0x400; tile++)
                    if (!a.Fetch(tile).SequenceEqual(b.Fetch(tile)))
                        Assert.Fail($"tileset {tileset:X2} phase {phase}: tile {tile:X3} differs "
                                  + "between a v5 and a v6 base");
            }
    }

    /// <summary>
    /// The point of the conversion, end to end: the v4 upload reads four planes out of a v6
    /// base's own files and sends VRAM exactly what a v3 base sent from the 3bpp originals.
    /// Same bar as v4_uploads_a_converted_file_byte_identically_to_v3, but with the base
    /// supplying the converted file rather than the test doing it by hand.
    /// </summary>
    [RealRomFact]
    public void v6_uploads_its_own_converted_files_byte_identically_to_v3()
    {
        var v3 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v3, 3);
        var v6 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v6, 6);
        foreach (var (file, tileset) in new[] { (0x02, 0x00), (0x01, 0x00), (0x17, 0x00),
                                               (0x1E, 0x00), (0x08, 0x00), (0x08, 0x11) })
        {
            var a = UploadVram(v3, file, tileset, Gfx.DecompressFile(v3, file));
            var b = UploadVram(v6, file, tileset, Gfx.DecompressFile(v6, file));
            Assert.Equal(0x1000, a.Count);
            if (!a.SequenceEqual(b))
            {
                int at = a.Zip(b).ToList().FindIndex(p => p.First != p.Second);
                Assert.Fail($"file {file:X2} tileset {tileset:X2}: VRAM diverges at +{at:X} "
                          + $"(tile {at / 32:X2} byte {at % 32:X2}): v3 {a[at]:X2} != v6 {b[at]:X2}");
            }
        }
    }

    // ---- V9: a checksum Lunar Magic does not call tampered with ----

    /// <summary>The ROM's real sum, by the rule the hardware uses — checksum field counted as
    /// 0x0000 and its complement as 0xFFFF, which is the placeholder trick FixChecksum relies
    /// on.</summary>
    private static (int Stored, int Computed) Checksum(Rom rom)
    {
        int h = rom.HeaderOffset, size = rom.ActualRomSize;
        int stored = rom.Data[0x7FDE + h] | (rom.Data[0x7FDF + h] << 8);
        long sum = 0;
        for (int i = 0; i < size; i++) sum += rom.Data[h + i];
        sum -= rom.Data[0x7FDC + h] + rom.Data[0x7FDD + h] + rom.Data[0x7FDE + h] + rom.Data[0x7FDF + h];
        sum += 0xFF + 0xFF;                    // the complement placeholder
        return (stored, (int)(sum & 0xFFFF));
    }

    /// <summary>
    /// V9 lands the ROM back on Super Mario World's own checksum — genuinely, by balancing, not
    /// by writing the number over a different reality. Both halves matter: LM calls a stored
    /// value that disagrees with the contents "incorrect", and one that agrees but is not SMW's
    /// "tampered with", and a prepped base used to get the second.
    /// </summary>
    [RealRomFact]
    public void v9_balances_the_rom_back_onto_super_mario_worlds_checksum()
    {
        var v8 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v8, 8);
        var v9 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v9, 9);

        var (storedOld, computedOld) = Checksum(v8);
        Assert.Equal(storedOld, computedOld);                       // v8 was always self-consistent
        Assert.NotEqual(RomPrep.VanillaChecksum, storedOld);        // ...just not SMW's number

        var (stored, computed) = Checksum(v9);
        Assert.Equal(RomPrep.VanillaChecksum, stored);
        Assert.Equal(stored, computed);
        Assert.True(RomPrep.HasBalance(v9));

        // And it holds after the ROM grows, which is what a build does to it: allocate, fix,
        // still $A0DA. The balance is RATS-tagged, so the allocation cannot land on top of it.
        int snes = RatsWriter.Allocate(v9, [.. Enumerable.Repeat((byte)0xA5, 0x400)]);
        Assert.True(snes > 0);
        RatsWriter.FixChecksum(v9);
        var (after, afterComputed) = Checksum(v9);
        Assert.Equal(RomPrep.VanillaChecksum, after);
        Assert.Equal(after, afterComputed);
    }

    // ---- V8: the upload in the shape LM reads as 4bpp ----

    /// <summary>
    /// V8 changes HOW the upload works, so the bar is the one v4 set: every file, every filter
    /// case, byte-identical VRAM against v3 reading the same artwork at 3bpp. If a verbatim
    /// 32-byte copy loses something vanilla's plane dance was doing, it shows up here rather
    /// than as a subtly wrong tile in a level three months from now.
    /// </summary>
    [RealRomFact]
    public void v8_uploads_every_file_byte_identically_to_v3()
    {
        foreach (var (file, tileset) in new[] { (0x02, 0x00), (0x01, 0x00), (0x17, 0x00),
                                                (0x1E, 0x00), (0x08, 0x00), (0x08, 0x11) })
        {
            var v3 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v3, 3);
            var v8 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v8, 8);

            // The base supplies the converted file, so the swap v8 bakes in is part of what is
            // being judged rather than something the test re-does by hand.
            var a = UploadVram(v3, file, tileset, Gfx.DecompressFile(v3, file));
            var b = UploadVram(v8, file, tileset, Gfx.DecompressFile(v8, file));
            Assert.Equal(0x1000, a.Count);
            if (!a.SequenceEqual(b))
            {
                int at = a.Zip(b).ToList().FindIndex(p => p.First != p.Second);
                Assert.Fail($"file {file:X2} tileset {tileset:X2}: VRAM diverges at +{at:X} "
                          + $"(tile {at / 32:X2} byte {at % 32:X2}): v3 {a[at]:X2} != v8 {b[at]:X2}");
            }
        }
    }

    /// <summary>The point of v8: LM decides a ROM's files are 4bpp by finding ITS hack, and a
    /// real LM 4bpp hack must read as prepped so <see cref="RomPrep.Apply"/> never stamps over
    /// one.</summary>
    [LmRefRomFact]
    public void v8_wears_the_4bpp_hack_lunar_magic_looks_for()
    {
        var v7 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v7, 7);
        var v8 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v8, 8);
        var shao = Rom.Load(ReferenceRoms.ShaoBase);

        Assert.False(v7.HasLmGfx4bppHack);        // v4's mechanism: correct in game, invisible to LM
        Assert.True(v8.HasLmGfx4bppHack);
        Assert.True(shao.HasLmGfx4bppHack);       // and an LM hack reads the same way

        // The bytes are the interface here — LM reads their lengths and branch offsets.
        for (int i = 0; i < 0x15; i++)
            Assert.Equal(shao.ReadByte(RomPrep.Gfx4bppLoopSite + i),
                         v8.ReadByte(RomPrep.Gfx4bppLoopSite + i));

        // IsPrepped must stay true for a foreign LM hack, or Apply would stamp over it.
        Assert.True(RomPrep.IsPrepped(shao, 4));
    }

    /// <summary>Every converted file, decompressed by VANILLA's own routine ($00BA28: Y = file,
    /// tables → $8A, buffer → $00, LC_LZ2 core) under emulation, equals what our decoder reads.
    /// The editor only ever uses our decoder, so a compressor quirk vanilla's decoder disagrees
    /// with shows up in the game alone — as a file whose tail tiles are garbage.</summary>
    [RealRomFact]
    public void vanilla_decompressor_agrees_with_ours_on_every_converted_file()
    {
        var rom = PreppedReal();
        int bufAddr = RomPrep.GfxBuffer & 0xFFFF;
        for (int id = 0; id < Gfx.Count; id++)
        {
            if (!Gfx.IsTilePlanar3Bpp(id) || Gfx.SourceSnes(rom, id) <= 0) continue;
            var ours = Gfx.DecompressFile(rom, id);
            var cpu = new Cpu65816(rom);
            cpu.PresetY(id);
            cpu.CallNear(0x00BA28);
            var buf = (RomPrep.GfxBuffer >> 16) == 0x7F ? cpu.Ram7F : cpu.Ram7E;
            for (int i = 0; i < ours.Length; i++)
                if (buf[bufAddr + i] != ours[i])
                    Assert.Fail($"GFX{id:X2}: vanilla's decompressor diverges at +{i:X} (tile {i / 32:X2}): {buf[bufAddr + i]:X2} != {ours[i]:X2}");
        }
    }

    /// <summary>V13: the overworld's own GFX1C reader ($0480B9) takes a 4bpp buffer, in exactly
    /// Lunar Magic's two bytes — on a fresh prep and on a v12 base upgraded in place. Emulated:
    /// the routine expands 11 tiles of a 4bpp buffer into $0AF6 verbatim (vanilla's version
    /// reads it as 3bpp and produces the "streaky" overworld).</summary>
    [LmRefRomFact]
    public void v13_overworld_tile_reader_takes_4bpp_like_lunar_magic()
    {
        var shao = Rom.Load(ReferenceRoms.ShaoBase);
        var fresh = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(fresh, 13);
        var upgraded = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(upgraded, 12); RomPrep.Apply(upgraded, 13);
        Assert.True(RomPrep.IsPrepped(upgraded, 13));

        // Every v13 site carries LM's byte: buffer seeds, the $048000 table, the reader, and
        // all 21 overworld sprite-RAM operands (whole range compared, so a missed one shows).
        var sites = new List<int> { 0x00BA40, 0x00BA44, 0x0480BD, 0x0480D0 };
        sites.AddRange(Enumerable.Range(0x048000, 0x86));
        sites.AddRange(Enumerable.Range(0x04F2B0, 0x128));
        foreach (var rom in new[] { fresh, upgraded })
            foreach (int a in sites)
                Assert.True(shao.ReadByte(a) == rom.ReadByte(a), $"${a:X6}: {rom.ReadByte(a):X2} != LM's {shao.ReadByte(a):X2}");

        // One tile through the reader: [$00] = a 4bpp tile in the buffer, X = 0 → $0AF6 gets it.
        var cpu = new Cpu65816(fresh);
        var tile = Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray();
        int buf = RomPrep.GfxBuffer;
        for (int i = 0; i < 32; i++) cpu.Ram7E[(buf & 0xFFFF) + i] = tile[i];
        cpu.Ram7E[0x00] = (byte)buf; cpu.Ram7E[0x01] = (byte)(buf >> 8); cpu.Ram7E[0x02] = (byte)(buf >> 16);
        cpu.PresetX(0);
        cpu.PresetWidths(m8: false, x8: false);                    // the caller runs it under REP #$30
        cpu.CallNear(0x0480B9);
        for (int i = 0; i < 32; i++) Assert.Equal(tile[i], cpu.Ram7E[0x0AF6 + i]);
    }

    /// <summary>Run one GFX file through a prepped ROM's upload and return the bytes it sent to
    /// the VRAM data port. The file is placed in the decompress buffer by hand and the upload
    /// entered directly, so the comparison isolates the upload from the resolver around it.</summary>
    private static List<byte> UploadVram(Rom rom, int file, int tileset, byte[] data)
    {
        var cpu = new Cpu65816(rom) { VramLog = [] };
        for (int i = 0; i < data.Length; i++) cpu.Ram7E[0xAD00 + i] = data[i];
        cpu.Ram7E[0x00] = 0x00; cpu.Ram7E[0x01] = 0xAD; cpu.Ram7E[0x02] = 0x7E;   // [$00] = src
        cpu.Ram7E[0x1931] = (byte)tileset;
        cpu.PresetWidths(m8: true, x8: true);      // entry state: 8-bit M and X/Y
        cpu.PresetY(file);                         // Y = file#, for the filter cases
        cpu.CallNear(0x00AA80, 20_000_000);
        return cpu.VramLog!;
    }

    /// <summary>
    /// The upload is not the only 3bpp reader: GFX0F and GFX00 are also expanded into RAM
    /// buffers ($7F977B for the status-bar sheet, $0BF6) by their own loops, and V4 has to
    /// rewrite those too — plus the three hardcoded pointers INTO the decompression buffer that
    /// the GFX00 half carries, which both move with the buffer and rescale with the depth.
    /// Same parity bar as the upload: converted file through v4 == original through v3.
    /// </summary>
    [RealRomFact]
    public void v4_expands_gfx00_and_gfx0f_into_ram_byte_identically_to_v3()
    {
        // $1425 gates a 2-tile skip inside GFX0F whose stride is depth-scaled ($30 -> $40), so
        // both sides of that branch have to be walked.
        foreach (int flag in new[] { 0x00, 0x01 })
        {
            var v3 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v3, 3);
            var v4 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v4, 4);
            Convert4bpp(v4, 0x0F);
            Convert4bpp(v4, 0x00);

            var (sheet3, low3) = ExpandRam(v3, flag);
            var (sheet4, low4) = ExpandRam(v4, flag);
            AssertBytesEqual(sheet3, sheet4, $"$7F977B sheet (flag {flag})");
            AssertBytesEqual(low3, low4, $"$0BF6 buffer (flag {flag})");
        }
    }

    /// <summary>Put a 4bpp conversion of a vanilla file into the ROM and repoint its pointer
    /// table entry — what a converted project's build does, in miniature.</summary>
    private static void Convert4bpp(Rom rom, int file)
    {
        byte[] four = Gfx.NormalizeBpp(Gfx.DecompressFile(rom, file), 3, 4, out bool dropped);
        Assert.False(dropped);
        Assert.Equal(0x1000, four.Length);
        int snes = RatsWriter.Allocate(rom, Gfx.Lz2Compress(four));
        rom.Data[rom.FileOffset(Gfx.PtrLow + file)] = (byte)snes;
        rom.Data[rom.FileOffset(Gfx.PtrHigh + file)] = (byte)(snes >> 8);
        rom.Data[rom.FileOffset(Gfx.PtrBank + file)] = (byte)(snes >> 16);
        Gfx.InvalidateCache(rom);
    }

    /// <summary>Run the GFX0F+GFX00 expander ($00A82D, RTS at $00A8C2) and return both RAM
    /// buffers it fills.</summary>
    private static (byte[] Sheet, byte[] Low) ExpandRam(Rom rom, int flag)
    {
        var cpu = new Cpu65816(rom);
        cpu.Ram7E[0x1425] = (byte)flag;
        cpu.PresetWidths(m8: true, x8: true);
        cpu.CallNear(0x00A82D, 30_000_000);
        return (cpu.Ram7F[0x977B..(0x977B + 0x300)], cpu.Ram7E[0x0BF6..(0x0BF6 + 0x180)]);
    }

    private static void AssertBytesEqual(byte[] a, byte[] b, string what)
    {
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                Assert.Fail($"{what} diverges at +{i:X}: v3 {a[i]:X2} != v4 {b[i]:X2}");
    }

    /// <summary>
    /// $0DF100 is the byte Lunar Magic reads as "the author restricted level access" — an
    /// undocumented flag sitting inside the vanilla $FF gap our Direct-Map16 handlers occupy.
    /// V1-V4 wrote code straight over it, which made every prepped base refuse to open in LM
    /// with "Access Denied"; V5 branches around it. Verified against the real Lunar Magic
    /// binary, but pinned here so the property cannot regress without a ROM in the loop.
    /// </summary>
    [RealRomFact]
    public void v5_leaves_lunar_magics_level_access_flag_alone()
    {
        var v5 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v5, 5);
        Assert.Equal(0xFF, v5.ReadByte(RomPrep.LmAccessFlag));

        // The handler block still has to work: its entry points are pinned, and the branch that
        // hops the flag must not have pushed the block into GFX handler 0x26's slot.
        foreach (int entry in new[] { RomPrep.Handler22, RomPrep.Handler23, RomPrep.Handler26,
                                      RomPrep.Handler27, RomPrep.Handler28 })
            Assert.NotEqual(0xFF, v5.ReadByte(entry));

        // ...and the flag really is the difference: v4 is the same ROM with that byte clobbered.
        var v4 = Rom.Load(TestRom.RealRomPath); RomPrep.Apply(v4, 4);
        Assert.NotEqual(0xFF, v4.ReadByte(RomPrep.LmAccessFlag));
    }

    [RealRomFact]
    public void normal_levels_render_identically_pre_and_post_prep()
    {
        var clean = Rom.Load(TestRom.RealRomPath);
        var prep = PreppedReal();
        foreach (int lvl in new[] { 0x001, 0x002, 0x101, 0x105, 0x106 })
        {
            var a = ObjectEngine.Render(clean, LevelParser.Parse(clean, lvl));
            var b = ObjectEngine.Render(prep, LevelParser.Parse(prep, lvl));
            AssertGridsEqual(a, b, $"level {lvl:X3}");
        }
    }

    [RealRomFact]
    public void sprite_capture_is_identical_pre_and_post_prep()
    {
        // Sprite OAM capture runs the block probes that JSL through the acts-like remap
        // ($019533/$02A6EB) on a prepped image — identity table ⇒ identical OAM output.
        var clean = Rom.Load(TestRom.RealRomPath);
        var prep = PreppedReal();
        bool remapRan = false;
        foreach (int num in new[] { 0x06, 0x0F })          // walkers: probe ground each frame
        {
            var a = SpriteRender.Capture(clean, new Sprite(1, 4, 10, 0, num));
            List<SpriteRender.Oam>? b;
            // The trace channel is global statics — hold the gate from setting the flag
            // through reading the results, or a parallel test's capture replaces them mid-read.
            lock (SpriteRender.TraceGate)
            {
                SpriteRender.Trace = true;
                b = SpriteRender.Capture(prep, new Sprite(1, 4, 10, 0, num));
                SpriteRender.Trace = false;
                remapRan |= SpriteRender.LastPcHot?.Any(pc =>
                    pc >= RomPrep.ActsRemapEntry && pc < RomPrep.ActsRemapEntry + 0x30) == true;
            }
            Assert.NotNull(a);
            Assert.Equal(a, b);
        }
        Assert.True(remapRan, "acts-like remap never executed — test is vacuous");
    }

    [RealRomFact]
    public void custom_back_color_survives_the_fade_in_palette_reload()
    {
        // Regression (blue-backdrop bug): the fade-in mode step re-runs `JSR
        // UploadSpriteGFX : JSR LoadPalette` at $00A5B9, wiping the staging the first hook
        // customized. The second hook ($00A5BF JSL) must re-apply AFTER that reload. The
        // in-game backdrop is NOT CGRAM color 0 — it is COLDATA $2132, fed per-frame from
        // the $0701 back-color word ($00AE47) — so assert the RAM home, not a register.
        var prep = PreppedReal();
        var colors = new ushort[256];
        for (int i = 0; i < 256; i++) colors[i] = (ushort)(0x2000 | i);
        prep.WriteLmCustomPalette(0x105, 0x7FFF, colors);

        var cpu = new Cpu65816(prep);
        cpu.Ram7F[0x8000] = 0x6B;                          // JSL $7F8000 utility → RTL stub
        cpu.Ram7E[0x010B] = 0x05; cpu.Ram7E[0x010C] = 0x01;
        cpu.Ram7E[0x1930] = 0x01; cpu.Ram7E[0x192B] = 0x09;   // header mirrors for LoadPalette
        cpu.CallNear(0x00A5B9, 30_000_000);                // the whole fade-in mode step

        Assert.Equal(0x7FFF, cpu.Ram7E[0x0701] | (cpu.Ram7E[0x0702] << 8));   // $2132 home
        Assert.Equal(0x2011, cpu.Ram7E[0x0703 + 0x11 * 2] | (cpu.Ram7E[0x0704 + 0x11 * 2] << 8));
        // the fade copy ($00A5D8) propagates the custom values to the display buffer too
        Assert.Equal(0x7FFF, cpu.Ram7E[0x0903] | (cpu.Ram7E[0x0904] << 8));
    }

    [RealRomFact]
    public void level_pointer_chain_leaves_identical_ram_except_the_level_word()
    {
        // Regression (level-104 TIME UP bug): the sprite stub once leaked the level's high
        // byte into B; the entrance decode right after runs 8-bit `LDA table : TAX` with
        // 16-bit X, where TAX transfers B:A — every table index shifted by 0x100 (garbage
        // Mario spawn/scroll/boundary state). Run the whole CODE_05D8B7 chain (level
        // pointers + sprite stub + entrance decode) for level 0x104 on clean vs prepped:
        // RAM must match exactly, except the intended $010B/C level word and dead bytes
        // below the stack pointer (the stub's deeper call chain leaves residue there).
        var clean = Rom.Load(TestRom.RealRomPath);
        var prep = PreppedReal();

        // Both a vertical level ($104, the original bug) and a horizontal one ($105): LM's engine
        // takes different branches for the two.
        foreach (int level in new[] { 0x104, 0x105 })
        {
            Cpu65816 Run(Rom rom)
            {
                var cpu = new Cpu65816(rom);
                cpu.Ram7E[0x0E] = (byte)level; cpu.Ram7E[0x0F] = (byte)(level >> 8);
                // PHB : LDA #$05 : PHA : PLB : JML $05D8B7 — bank-05 code reads its tables DBR-relative.
                byte[] driver = [0x8B, 0xA9, 0x05, 0x48, 0xAB, 0x5C, 0xB7, 0xD8, 0x05];
                driver.CopyTo(cpu.Ram7F, 0x9000);
                cpu.CallLong(0x7F9000, 5_000_000);                           // tail PLB:RTL balances
                return cpu;
            }

            var a = Run(clean);
            var b = Run(prep);
            var diverged = new List<string>();
            for (int i = 0; i < 0x10000; i++)
            {
                if (i is 0x010B or 0x010C || (i >= 0x0100 && i <= 0x01FF)) continue;
                // $13CD: vanilla puts the midway screen there; LM's $05DD30 (v10) puts its $06FE00
                // byte there and turns vanilla's store into a load. The one reader ($00F2D8) only
                // tests it for zero, and LM's $1A keeps it non-zero — see AppendV10Stamps.
                if (i == 0x13CD) continue;
                // LM's level-entry engine (v10, LmLevelEntry) owns these: its `TSB $5A` marker, $0BE6
                // bit 14, the per-screen tilemap pointer tables it rebuilds (checked below), and the
                // level height $13D7 / $1936 that vanilla never had a RAM home for.
                if (i is 0x06 or 0x5B or 0x0BE7 or 0x13D7 or 0x13D8 or 0x1936 or 0x1937 || (i >= 0x0BF6 && i < 0x0CF6)) continue;
                // $FE/$FF: LM's $0EF550 level-word mirror leaves level+1 there (its sprite loader's
                // "next level" scratch).
                if (i is 0xFE or 0xFF) continue;
                if (a.Ram7E[i] != b.Ram7E[i])
                    diverged.Add($"7E:{i:X4} clean={a.Ram7E[i]:X2} prep={b.Ram7E[i]:X2}");
            }
            Assert.True(diverged.Count == 0, $"level {level:X3}: " + string.Join(" | ", diverged));
            // The pointer tables at vanilla height: 32 screens of 0x1B0 bytes from $7E:C800 (low
            // plane) and $7F:C800 (high plane), which is what vanilla's own table at $00A88B says too.
            for (int i = 0; i < 0x20; i++)
            {
                int lo = b.Ram7E[0x0BF6 + i * 3] | (b.Ram7E[0x0BF7 + i * 3] << 8) | (b.Ram7E[0x0BF8 + i * 3] << 16);
                int hi = b.Ram7E[0x0C56 + i * 3] | (b.Ram7E[0x0C57 + i * 3] << 8) | (b.Ram7E[0x0C58 + i * 3] << 16);
                Assert.Equal(0x7EC800 + i * 0x1B0, lo);
                Assert.Equal(0x7FC800 + i * 0x1B0, hi);
                Assert.Equal(lo & 0xFF, b.Ram7E[0x0CB6 + i]);                 // the block probe's copies
                Assert.Equal((lo >> 8) & 0xFF, b.Ram7E[0x0CD6 + i]);
            }
            // Both levels are horizontal: $13D7 is the vanilla height, $06 (block A's scratch) is
            // height less a screen.
            Assert.Equal(0x01B0, b.Ram7E[0x13D7] | (b.Ram7E[0x13D8] << 8));
            Assert.Equal(0xC0, b.Ram7E[0x06]);
            Assert.Equal(0x80, b.Ram7E[0x5B] & 0x80);
            Assert.Equal(level & 0xFF, b.Ram7E[0x010B]);
            Assert.Equal(level >> 8, b.Ram7E[0x010C]);
        }
    }

    /// <summary>
    /// The height engine at a NON-vanilla height: set level $105's height byte to LUT index 0x17
    /// (0x950 px, 149 rows — DogsOfWar's $10F) and run the load chain. $13D7 is the LUT value, the
    /// per-screen tilemap pointers step by it, and the block probe's copies follow.
    /// </summary>
    [RealRomFact]
    public void a_taller_level_gets_its_height_into_ram_and_its_pointer_tables_restrided()
    {
        var prep = PreppedReal();
        prep.Data[prep.FileOffset(prep.LmLevelHeightTable + 0x105)] = 0x17;
        Assert.Equal(0x950, prep.LevelHeightPx(0x105));
        Assert.Equal(149, prep.LevelHeightRows(0x105));

        var cpu = new Cpu65816(prep);
        cpu.Ram7E[0x0E] = 0x05; cpu.Ram7E[0x0F] = 0x01;
        // PHB : LDA #$05 : PHA : PLB : JML $05D8B7 — bank-05 code reads its tables DBR-relative.
                byte[] driver = [0x8B, 0xA9, 0x05, 0x48, 0xAB, 0x5C, 0xB7, 0xD8, 0x05];
        driver.CopyTo(cpu.Ram7F, 0x9000);
        cpu.CallLong(0x7F9000, 5_000_000);
        var b = cpu.Ram7E;
        Assert.Equal(0x0950, b[0x13D7] | (b[0x13D8] << 8));
        Assert.Equal(0x0940, b[0x1936] | (b[0x1937] << 8));
        // 0x3800 / 0x950 = 6 columns fit; LM's builder stops at the first pointer past $FFFF.
        for (int i = 0; i < 6; i++)
        {
            int lo = b[0x0BF6 + i * 3] | (b[0x0BF7 + i * 3] << 8) | (b[0x0BF8 + i * 3] << 16);
            Assert.Equal(0x7EC800 + i * 0x950, lo);
            Assert.Equal(lo & 0xFF, b[0x0CB6 + i]);                   // block-probe copies
            Assert.Equal((lo >> 8) & 0xFF, b[0x0CD6 + i]);
        }
    }

    [RealRomFact]
    public void acts_remap_is_transparent_for_every_vanilla_tile()
    {
        // The remap must be a perfect no-op for vanilla data: same A return value, same
        // $1693/$1423 effects, caller's B preserved, for all 512 tiles. Driver in $7F RAM
        // seeds B with a sentinel (LDA #$5A : XBA), calls the target with A = page, then
        // captures the returned A and B to $7FF000/1.
        var clean = Rom.Load(TestRom.RealRomPath);
        var prep = PreppedReal();

        (byte A, byte B, byte Low, byte B1423) Run(Rom rom, int target, int tile)
        {
            var cpu = new Cpu65816(rom);
            cpu.Ram7E[0x1693] = (byte)tile;
            byte[] driver =
            [
                0xA9, 0x5A, 0xEB,                          // LDA #$5A : XBA — B = sentinel
                0xA9, (byte)(tile >> 8),                   // LDA #page
                0x22, (byte)target, (byte)(target >> 8), (byte)(target >> 16),
                0x8F, 0x00, 0xF0, 0x7F,                    // STA $7FF000  (returned A)
                0xEB, 0x8F, 0x01, 0xF0, 0x7F,              // XBA : STA $7FF001  (returned B)
                0x6B,
            ];
            driver.CopyTo(cpu.Ram7F, 0x9000);
            cpu.CallLong(0x7F9000, 500_000);
            return (cpu.Ram7F[0xF000], cpu.Ram7F[0xF001], cpu.Ram7E[0x1693], cpu.Ram7E[0x1423]);
        }

        for (int tile = 0; tile < 0x200; tile++)
        {
            var v = Run(clean, 0x00F545, tile);
            var p = Run(prep, RomPrep.ActsRemapEntry, tile);
            if (v != p)
                Assert.Fail($"tile {tile:X3}: clean A={v.A:X2} B={v.B:X2} $1693={v.Low:X2} " +
                            $"prep A={p.A:X2} B={p.B:X2} $1693={p.Low:X2}");
            Assert.Equal(0x5A, p.B);                       // caller's B preserved
        }

        // and an extended tile resolves to its acts value (default 0x130)
        var e = Run(prep, RomPrep.ActsRemapEntry, 0x2A5);
        Assert.Equal((0x01, 0x5A, 0x30), (e.A, e.B, e.Low));
    }

    // ---------------------------------------------------------------- parity oracle

    /// <summary>Encode one 3-byte record (+ extras); layout per CONTRACT §4.</summary>
    private static IEnumerable<byte> Rec(int num, int y, int x, int b3, bool ns = false, params int[] extras)
    {
        yield return (byte)((ns ? 0x80 : 0) | ((num & 0x30) << 1) | (y & 0x1F));
        yield return (byte)(((num & 0x0F) << 4) | (x & 0x0F));
        yield return (byte)b3;
        foreach (int e in extras) yield return (byte)e;
    }

    private static readonly byte[] HeaderH = [0x02, 0x00, 0x00, 0x00, 0x00];   // mode 0, tileset 0
    private static readonly byte[] HeaderV = [0x02, 0x02, 0x00, 0x00, 0x00];   // mode 2 = vertical

    /// <summary>The oracle stream: every DM16/ext form plus normal objects after them —
    /// any consumed-length mismatch desyncs the tail and fails loudly.</summary>
    private static byte[] OracleStream(byte[] header)
    {
        var b = new List<byte>(header);
        b.AddRange(Rec(0x22, y: 2, x: 1, b3: 0x21, ns: false, 0x47));                 // 2x3 tile 0x047
        b.AddRange(Rec(0x23, y: 8, x: 6, b3: 0x10, ns: false, 0x30));                 // 1x2 tile 0x130
        b.AddRange(Rec(0x27, y: 12, x: 3, b3: 0x13, ns: false, 0x02, 0x22));          // plain: 4x2 tile 0x222
        b.AddRange(Rec(0x27, y: 5, x: 9, b3: 0x22, ns: false, 0xC2, 0x33, 0x00, 0x02)); // ext: 35x3 tile 0x233
        b.AddRange(Rec(0x29, y: 20, x: 0, b3: 0x21, ns: false, 0x01, 0x10));          // BG form: tile 0x4110
        b.AddRange(Rec(0x00, y: 3, x: 0, b3: 0x02, ns: false, 0x55, 0x01));           // secondary exit, screen 3
        b.AddRange(Rec(0x00, y: 0, x: 7, b3: 0x03));                                  // screen jump → 7
        b.AddRange(Rec(0x01, y: 10, x: 2, b3: 0x22));                                 // normal rect on screen 7
        b.Add(0xFF);
        return b.ToArray();
    }

    private static void AssertGridsEqual(Map16Grid a, Map16Grid b, string what)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.Get(x, y) != b.Get(x, y))
                    Assert.Fail($"{what}: cell ({x},{y}) differs: {a.Get(x, y):X4} vs {b.Get(x, y):X4}");
    }

    [LmRefRomFact]
    public void dm16_stream_parity_with_the_lm_reference()
    {
        var prep = PreppedReal();
        var lm = Rom.Load(AfterRomPath);
        byte[] enc = OracleStream(HeaderH);
        var hdr = new LevelHeader(HeaderH);

        var gPrep = ObjectEngine.RenderEmulatedStream(prep, hdr, enc, 0);
        var prepCpu = ObjectEngine.LastCpu!;
        var gLm = ObjectEngine.RenderEmulatedStream(lm, hdr, enc, 0);
        var lmCpu = ObjectEngine.LastCpu!;

        AssertGridsEqual(gLm, gPrep, "horizontal oracle");

        // secondary exit RAM effects + screen retarget, equal across ROMs
        Assert.Equal(lmCpu.Ram7E[0x19B8 + 3], prepCpu.Ram7E[0x19B8 + 3]);
        Assert.Equal(lmCpu.Ram7E[0x19D8 + 3], prepCpu.Ram7E[0x19D8 + 3]);
        Assert.Equal(0x55, prepCpu.Ram7E[0x19B8 + 3]);
        Assert.Equal(0x07, prepCpu.Ram7E[0x1928]);

        // spot-check the intended tiles landed (guards against "both wrong" parity)
        Assert.Equal(0x047, gPrep.Get(1, 2));
        Assert.Equal(0x047, gPrep.Get(2, 4));
        Assert.Equal(0x130, gPrep.Get(6, 8));
        Assert.Equal(0x130, gPrep.Get(6, 9));
        Assert.Equal(0x222, gPrep.Get(3, 12));
        Assert.Equal(0x222, gPrep.Get(6, 13));
        Assert.Equal(0x233, gPrep.Get(9, 5));          // extended form spans 35 columns
        Assert.Equal(0x233, gPrep.Get(9 + 34, 7));
        Assert.Equal(0x4110, gPrep.Get(0, 20));        // 0x29: BG-page tile (page | 0x40)
    }

    [LmRefRomFact]
    public void vertical_dm16_stream_parity_with_the_lm_reference()
    {
        var prep = PreppedReal();
        var lm = Rom.Load(AfterRomPath);
        byte[] enc = OracleStream(HeaderV);
        var hdr = new LevelHeader(HeaderV);
        var gPrep = ObjectEngine.RenderEmulatedStream(prep, hdr, enc, 0);
        var gLm = ObjectEngine.RenderEmulatedStream(lm, hdr, enc, 0);
        AssertGridsEqual(gLm, gPrep, "vertical oracle");
        Assert.Contains(gPrep.Tiles, t => t == 0x047);   // non-vacuity: content rendered
    }

    [LmRefRomFact]
    public void bit7_only_extended_form_parity_with_the_lm_reference()
    {
        // Page bit7 WITHOUT bit6: +1 run byte, no height byte (LevelParser contract).
        var prep = PreppedReal();
        var lm = Rom.Load(AfterRomPath);
        var b = new List<byte>(HeaderH);
        b.AddRange(Rec(0x27, y: 4, x: 2, b3: 0x1D, ns: false, 0x82, 0x11, 0x00));   // page 2 | bit7
        b.AddRange(Rec(0x27, y: 9, x: 1, b3: 0x12, ns: false, 0x83, 0x20, 0x37));   // nonzero run byte
        b.AddRange(Rec(0x27, y: 14, x: 0, b3: 0x14, ns: false, 0xC1, 0x21, 0x15, 0x01)); // 7+6, run 0x15
        b.AddRange(Rec(0x01, y: 20, x: 0, b3: 0x11));                               // sync sentinel
        b.Add(0xFF);
        var hdr = new LevelHeader(HeaderH);
        var gPrep = ObjectEngine.RenderEmulatedStream(prep, hdr, b.ToArray(), 0);
        var gLm = ObjectEngine.RenderEmulatedStream(lm, hdr, b.ToArray(), 0);
        AssertGridsEqual(gLm, gPrep, "bit7-only form");
    }

    [RealRomFact]
    public void ported_engine_agrees_on_the_dm16_forms()
    {
        var prep = PreppedReal();
        byte[] enc = OracleStream(HeaderH);
        var hdr = new LevelHeader(HeaderH);
        var emulated = ObjectEngine.RenderEmulatedStream(prep, hdr, enc, 0);
        var ported = PortedObjectEngine.Render(prep, hdr, LevelParser.ParseEncoded(prep, enc));

        // agreement on every DM16 footprint cell (the ported engine leaves blanks Empty)
        foreach (var (x, y, t) in new[]
                 {
                     (1, 2, 0x047), (2, 4, 0x047),
                     (6, 8, 0x130), (6, 9, 0x130),
                     (3, 12, 0x222), (6, 13, 0x222),
                     (9, 5, 0x233), (9 + 34, 7, 0x233),
                 })
        {
            Assert.Equal(t, emulated.Get(x, y));
            Assert.Equal(t, ported.Get(x, y));
        }
    }

    // ---- V7: a screen exit that can name a level above $0FF ----

    /// <summary>Run the ROM's own level-number-high-byte routine and read what it decides.
    /// Returns (highByte, $1B93 secondary flag, $192A entrance action).</summary>
    private static (int High, int Secondary, int Action) RunExitHighByte(Rom rom, int screen, int flags,
                                                                        int translevel = 0x25)
    {
        var cpu = new Cpu65816(rom);
        cpu.PresetWidths(m8: true, x8: true);
        cpu.Ram7E[0x19D8 + screen] = (byte)flags;
        cpu.Ram7E[0x13BF] = (byte)translevel;
        cpu.PresetX(screen);
        cpu.CallLong(RomPrep.ExitHighByte);
        return (cpu.Acc & 0xFF, cpu.Ram7E[0x1B93], cpu.Ram7E[0x192A]);
    }

    /// <summary>
    /// The V7 patch, run as code. Vanilla decides the destination's bit 8 from the submap the
    /// player is standing on, so "exit to $105" is inexpressible; this takes it from the exit's
    /// own flags instead. Every case is checked against the ROM's real behaviour, not the
    /// stamp bytes — the bytes are only interesting if they run like this.
    /// </summary>
    [RealRomFact]
    public void v7_takes_the_destinations_ninth_bit_from_the_exit_flags()
    {
        var rom = PreppedReal();
        Assert.True(rom.HasExitLevelHighBit);

        // Extended (bit2), bit0 = the ninth bit of the destination.
        Assert.Equal((1, 0, 0), RunExitHighByte(rom, screen: 3, flags: 0x05));
        Assert.Equal((0, 0, 0), RunExitHighByte(rom, screen: 3, flags: 0x04));
        // bit1 arms the secondary-entrance table, bit3 the entrance action ($192A bit 6).
        Assert.Equal((1, 1, 0), RunExitHighByte(rom, screen: 7, flags: 0x07));
        Assert.Equal((1, 0, 0x40), RunExitHighByte(rom, screen: 7, flags: 0x0D));
        // Indexed per screen, like the tables it reads.
        Assert.Equal((0, 0, 0), RunExitHighByte(rom, screen: 0x1F, flags: 0x04));

        // WITHOUT bit2 nothing is extended and the old rule applies: bit 8 says whether the
        // level being left is a main level. Every exit already in the ROM is this case.
        Assert.Equal(1, RunExitHighByte(rom, screen: 1, flags: 0x01, translevel: 0x25).High);
        Assert.Equal(0, RunExitHighByte(rom, screen: 1, flags: 0x01, translevel: 0x24).High);
    }

    /// <summary>The point of matching LM's site and flag layout: an exit authored in either
    /// editor must mean the same thing in the other. Same inputs, same answers, both ROMs.</summary>
    [LmRefRomFact]
    public void v7_decides_the_high_byte_exactly_as_lunar_magic_does()
    {
        var ours = PreppedReal();
        var lm = Rom.Load(AfterRomPath);
        Assert.True(lm.HasExitLevelHighBit);          // LM's own patch, detected the same way

        foreach (int flags in new[] { 0x00, 0x01, 0x04, 0x05, 0x06, 0x07, 0x0C, 0x0D, 0x0F })
            foreach (int tl in new[] { 0x10, 0x25 })
                Assert.Equal(RunExitHighByte(lm, 5, flags, tl), RunExitHighByte(ours, 5, flags, tl));
    }

    /// <summary>The other half: the object handler has to KEEP the flags. Vanilla masks the X
    /// nibble down to the water bit, so bits 1-3 never reached $19D8,X to be read back.</summary>
    [RealRomFact]
    public void v7_keeps_the_whole_exit_nibble_in_ram()
    {
        var vanilla = Rom.Load(TestRom.RealRomPath);
        var prepped = PreppedReal();
        Assert.Equal(0x01, vanilla.ReadByte(RomPrep.ExitFlagMask));
        Assert.Equal(0x0F, prepped.ReadByte(RomPrep.ExitFlagMask));
        Assert.False(vanilla.HasExitLevelHighBit);
    }

    /// <summary>V11's engine RUNS: a global slot written into a prepped image is resolved by
    /// LM's own processor (emulated) to the DMA records the game would perform — dest tile A0,
    /// one 0x20-byte tile per frame, source inside our file-60 block. And the per-level table
    /// round-trips a record through the same writer LM's layout implies.</summary>
    [RealRomFact]
    public void v11_exanimation_engine_runs_a_written_slot()
    {
        var rom = PreppedReal();
        rom.SetLmAltExGfx(0, Enumerable.Range(0, 0x400).Select(i => (byte)i).ToArray());
        var slot = new ExAnimation.Slot(0, 1, ExAnimation.TriggerNone, 3, 0x8A00, [0x0020, 0x00A0, 0x0140], 0);
        Assert.Null(rom.WriteGlobalExAnim([slot], 0));
        Assert.True(rom.LmGlobalExAnimPtr > 0);
        var back = Assert.Single(ExAnimation.ReadGlobal(rom));
        Assert.Equal((slot.Type, slot.Trigger, slot.FrameCount, slot.DestWord), (back.Type, back.Trigger, back.FrameCount, back.DestWord));
        Assert.Equal(slot.Frames, back.Frames);

        var frames = ExAnimation.ResolveGlobal(rom, 32).Where(f => f.Ctrl != 0).ToList();
        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.Equal(0xA0, f.DestTile));
        Assert.All(frames, f => Assert.Equal(0x20, f.Ctrl));
        int file = rom.LmAltExGfx(0);
        Assert.Contains(frames, f => f.SrcSnes == file + 0x20);
        Assert.Contains(frames, f => f.SrcSnes == file + 0xA0);

        Assert.Null(rom.WriteLevelExAnim(0x105, [slot with { Index = 5, DestWord = 0x0A00, Frames = [0x7D20, 0x87A0, 0x9240] }], 0));
        var lvl = Assert.Single(ExAnimation.ReadLevel(rom, 0x105));
        Assert.Equal(5, lvl.Index);
        Assert.Equal(0x601, lvl.SrcTile(0));
        Assert.Null(rom.WriteLevelExAnim(0x105, [], 0));
        Assert.Empty(ExAnimation.ReadLevel(rom, 0x105));
        Assert.Null(rom.WriteGlobalExAnim([], 0));
        Assert.Equal(-1, rom.LmGlobalExAnimPtr);
    }
}
