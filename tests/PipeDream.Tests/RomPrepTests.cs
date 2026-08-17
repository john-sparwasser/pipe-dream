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
    public const string AfterRomPath = @"C:\SMW\Projects\.resources\after.smc";

    /// <summary>Golden SHA-256 (headerless) of the prepped vanilla US ROM, computed from
    /// RomPrep V1's frozen stamp tables (2026-08-16, incl. the B-preservation fix and the
    /// second palette hook at $00A5BF). Any stamp drift fails here — this hash is the
    /// shared-.pdp determinism guarantee.</summary>
    private const string GoldenPrepSha256 = "a73872c55badc79300a7858c812d47d7286f1412e01f9d015305ce78c4df8898";

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
    }

    [Fact]
    public void prep_triggers_no_false_positive_scanners()
    {
        var rom = Prepped();
        Assert.Equal(-1, rom.LmSpriteSizeBase);
        Assert.Equal(-1, rom.LmGfxBypassBase);
        Assert.Equal(-1, rom.LmExGfxBase);
        Assert.Equal(-1, rom.LmExAnimBase);
        Assert.Equal(-1, rom.LmGlobalExAnimPtr);
        Assert.Equal(-1, rom.LmExAnimSetupEntry);
        Assert.Equal(-1, rom.LmExAnimProcEntry);
        Assert.False(rom.HasPixiSpriteHook);
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

        string extdef = Disasm.Dis(rom, 0x06F54F, 6, m8: false, x8: false);
        Assert.Contains("ADC #$7008", extdef);
        Assert.Contains("LDY #$1200", extdef);

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
        AllFf(0x06F540, 0x110);                    // Map16 lookup + acts remap
        AllFf(RomPrep.ExtHandler02, 0x50);         // ext handlers
        AllFf(RomPrep.Handler22, 0x276);           // DM16 handlers ($0DF08A-$0DF2FF)
        AllFf(RomPrep.Handler29, 0xB0);            // BG form
        AllFf(RomPrep.SpriteBankTable, 0x200);     // bank table
        AllFf(RomPrep.SpriteStub, 0x40);           // sprite stub
        AllFf(LunarMagic.LmPaletteTable, 0x600);   // palette pointer table
        AllFf(RomPrep.PalTrampoline, 0x100);       // palette stubs
        AllFf(RomPrep.PalThunk, 0x07);             // bank-00 thunk

        // patch sites hold the exact vanilla bytes we displace
        Assert.Equal([0xC2, 0x20, 0xB9, 0xBE, 0x0F], rom.Data.AsSpan(rom.FileOffset(0x00C17A), 5).ToArray());
        Assert.Equal([0x20, 0xDA, 0xA9, 0x20, 0xED, 0xAB], rom.Data.AsSpan(rom.FileOffset(0x0095E9), 6).ToArray());
        Assert.Equal([0x22, 0x8A, 0xBE, 0x05], rom.Data.AsSpan(rom.FileOffset(0x00A5BF), 4).ToArray());
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
    public void prepped_vanilla_matches_the_golden_hash()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "pd_prep_golden.smc");
        File.Copy(TestRom.RealRomPath, tmp, overwrite: true);
        try
        {
            Assert.Null(RomPrep.PrepInPlace(tmp));
            Assert.Equal(GoldenPrepSha256, RomHash.HeaderlessSha256File(tmp));
        }
        finally { File.Delete(tmp); }
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
            SpriteRender.Trace = true;
            var b = SpriteRender.Capture(prep, new Sprite(1, 4, 10, 0, num));
            SpriteRender.Trace = false;
            Assert.NotNull(a);
            Assert.Equal(a, b);
            remapRan |= SpriteRender.LastPcHot?.Any(pc =>
                pc >= RomPrep.ActsRemapEntry && pc < RomPrep.ActsRemapEntry + 0x30) == true;
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
