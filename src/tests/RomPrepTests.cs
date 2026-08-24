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
        Assert.False(rom.HasLmVramPatch);            // BG2/BG3 stay editor-only
        Assert.Null(rom.LmGfxBypass(0x105));         // zeroed record = no bypass
        Assert.Equal(-1, Gfx.SourceSnes(rom, 0x100));
        Assert.Equal(-1, Gfx.SourceSnes(rom, 0x85));
    }

    [Fact]
    public void prep_triggers_no_false_positive_scanners()
    {
        var rom = Prepped();
        Assert.Equal(-1, rom.LmSpriteSizeBase);
        Assert.Equal(-1, rom.LmExAnimBase);
        Assert.Equal(-1, rom.LmGlobalExAnimPtr);
        Assert.Equal(-1, rom.LmExAnimSetupEntry);
        Assert.Equal(-1, rom.LmExAnimProcEntry);
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

        string lookup = Disasm.Dis(rom, RomPrep.Map16LookupEntry, 6, m8: true, x8: false);
        Assert.Contains("REP #$20", lookup);
        Assert.Contains("CMP #$0400", lookup);
        Assert.Contains("LDA $0FBE,Y", lookup);

        // The range dispatcher: two shifts, and the carry/sign they fall out of pick the slot.
        string disp = Disasm.Dis(rom, 0x06F538, 11, m8: false, x8: false);
        Assert.Contains("ASL", disp);
        Assert.Contains("JMP $F55B", disp);          // range 1's slot, at LM's address
        Assert.Contains("JMP $F566", disp);          // range 2
        Assert.Contains("JMP $F56F", disp);          // range 3

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
        Assert.Contains("JSR $A95B", fill);         // vanilla step-right primitive
        Assert.Contains("JSR $A97D", fill);         // vanilla step-down primitive
        Assert.Contains("LDA [$65],Y", fill);       // stream extras

        string ext02 = Disasm.Dis(rom, RomPrep.ExtHandler02, 22, m8: true, x8: true);
        Assert.Contains("STA $19B8,X", ext02);
        Assert.Contains("STA $19D8,X", ext02);

        string ext03 = Disasm.Dis(rom, RomPrep.ExtHandler03, 4, m8: true, x8: true);
        Assert.Contains("STA $1928", ext03);
        Assert.Contains("STA $1BA1", ext03);

        string stub = Disasm.Dis(rom, RomPrep.SpriteStub, 12, m8: true, x8: false);
        Assert.Contains("LDA $F100,Y", stub);
        Assert.Contains("STA $010B", stub);

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

        // 0x4000+ is past the emitted ladder: the defined blank, never a wrapped slot read.
        Assert.Equal((0x8000, 0x00), Run(0x4000));
        Assert.Equal((0x8000, 0x00), Run(0x7FFF));
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
            Assert.Null(RomPrep.PrepInPlace(tmp));                  // current (V5)
            string v5 = RomHash.HeaderlessSha256File(tmp);
            // Spelled out rather than left to the assertion message: xunit truncates a mismatch,
            // and this hash is what the NEXT version bump has to be told.
            Assert.True(GoldenPrepV5Sha256 == v5, $"V5 golden hash is now {v5}");
        }
        finally { File.Delete(tmp); }
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
        rom.Data[rfo + 1] = 0x80;                                  // w0 = 0x807F (enabled)
        rom.Data[rfo + 0x0E] = 0x00; rom.Data[rfo + 0x0F] = 0x01;  // FG1 (w7) = file 0x100

        Cpu65816 Armed()
        {
            var cpu = new Cpu65816(rom);
            cpu.Ram7E[0xFE] = 6;                                   // level 5 + 1
            cpu.Ram7E[0x1931] = 0;                                 // tileset (expander filter path)
            return cpu;
        }

        // V4 decompresses to $7F:A000, not $7E:AD00 — a 4bpp file does not fit under the
        // layer-2 tile buffer. Addressed off the constant so a future move cannot rot this.
        int bufAddr = RomPrep.Gfx4bppBuffer & 0xFFFF;
        static byte[] Buf(Cpu65816 c) => (RomPrep.Gfx4bppBuffer >> 16) == 0x7F ? c.Ram7F : c.Ram7E;

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

        // disabled record (w0 bit15 clear): nothing happens
        rom.Data[rfo + 1] = 0x00;
        var dis = Armed(); Buf(dis)[bufAddr] = 0xEE;
        dis.CallLong(RomPrep.GfxLoaderEntry, 20_000_000);
        Assert.Equal(0xEE, Buf(dis)[bufAddr]);
        rom.Data[rfo + 1] = 0x80;

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

        Cpu65816 Run(Rom rom)
        {
            var cpu = new Cpu65816(rom);
            cpu.Ram7E[0x0E] = 0x04; cpu.Ram7E[0x0F] = 0x01;              // level 0x104
            byte[] driver = [0x8B, 0x5C, 0xB7, 0xD8, 0x05];              // PHB : JML $05D8B7
            driver.CopyTo(cpu.Ram7F, 0x9000);
            cpu.CallLong(0x7F9000, 5_000_000);                           // tail PLB:RTL balances
            return cpu;
        }

        var a = Run(clean);
        var b = Run(prep);
        for (int i = 0; i < 0x10000; i++)
        {
            if (i is 0x010B or 0x010C || (i >= 0x0100 && i <= 0x01FF)) continue;
            if (a.Ram7E[i] != b.Ram7E[i])
                Assert.Fail($"RAM 7E:{i:X4} diverged: clean={a.Ram7E[i]:X2} prep={b.Ram7E[i]:X2}");
        }
        Assert.Equal(0x04, b.Ram7E[0x010B]);
        Assert.Equal(0x01, b.Ram7E[0x010C]);
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
}
