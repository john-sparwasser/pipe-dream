using Xunit;

namespace PipeDream.Tests;

/// <summary>Skips without the vanilla ROM, after.smc, the SA-1 level-save oracle, the extracted pack
/// beside it, or Asar (which rebuilds clean + pack).</summary>
public class Sa1LevelSaveFactAttribute : FactAttribute
{
    public Sa1LevelSaveFactAttribute()
    {
        foreach (var p in new[] { ReferenceRoms.Vanilla, ReferenceRoms.LmAfter, ReferenceRoms.Sa1LevelSave, Path.Combine(ReferenceRoms.Sa1Pack, "sa1.asm") })
            if (!File.Exists(p)) { Skip = "not present: " + p; return; }
        if (!Asar.Available) Skip = "Asar not available (PIPEDREAM_ASAR)";
    }
}

/// <summary>Skips without the level-save oracle's prerequisites, LM's LoROM ExAnimation install
/// (exanim_1.smc) or its SA-1 one.</summary>
public sealed class Sa1ExAnimSaveFactAttribute : Sa1LevelSaveFactAttribute
{
    public Sa1ExAnimSaveFactAttribute()
    {
        if (Skip != null) return;
        foreach (var p in new[] { ReferenceRoms.ExAnim1, ReferenceRoms.Sa1ExAnimSave })
            if (!File.Exists(p)) { Skip = "not present: " + p; return; }
    }
}

/// <summary>
/// Lunar Magic's SA-1 forms (reference/SA1_PORT.md, phase 2): the pinned diffs are what the oracles
/// say, the SA-1 form never writes a vanilla byte over the pack's, and the image ApplySa1 leaves
/// has the bytes the first boot needed.
/// </summary>
public class Sa1FormsTests
{
    private static string PackAsm => Path.Combine(ReferenceRoms.Sa1Pack, "sa1.asm");

    /// <summary>Clean + pack, and the PCs the pack wrote there.</summary>
    private static (Rom CleanPack, HashSet<int> Wrote) CleanPack() => PackOn(Rom.Load(ReferenceRoms.Vanilla));

    /// <summary>The pack applied to <paramref name="rom"/> in place, and the PCs it wrote.</summary>
    private static (Rom Rom, HashSet<int> Wrote) PackOn(Rom rom)
    {
        var r = Asar.Patch(rom, PackAsm);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        var wrote = new HashSet<int>();
        foreach (var b in r.Blocks)
            for (int i = 0; i < b.Length; i++) wrote.Add(Rom.SnesToPc(b.Snes + i));
        return (rom, wrote);
    }

    [Fact]
    public void the_diff_sets_bytes_and_drops_the_packs()
    {
        var overlay = new Dictionary<int, byte> { [Rom.SnesToPc(0x02ABF3)] = 0x7F, [Rom.SnesToPc(0x0EF554)] = 0x01 };
        foreach (var (snes, bytes, dropped) in LmSa1Forms.Parse("0EF554=61 1F8010=7A7B 02ABF3=.."))
        {
            int pc = Rom.SnesToPc(snes);
            if (bytes != null) for (int i = 0; i < bytes.Length; i++) overlay[pc + i] = bytes[i];
            else for (int i = 0; i < dropped; i++) overlay.Remove(pc + i);
        }
        Assert.Equal(0x61, overlay[Rom.SnesToPc(0x0EF554)]);
        Assert.Equal(0x7B, overlay[Rom.SnesToPc(0x1F8011)]);
        Assert.False(overlay.ContainsKey(Rom.SnesToPc(0x02ABF3)));
        Assert.Equal(3, overlay.Count);
    }

    /// <summary>The pinned diff IS the oracle's, by the rules, with nothing outside them.</summary>
    [Sa1LevelSaveFact]
    public void the_level_save_diff_regenerates_from_the_oracle()
    {
        var (cleanPack, wrote) = CleanPack();
        var report = new List<string>();
        var generated = LmSa1Forms.Generate(Rom.Load(ReferenceRoms.LmAfter), Rom.Load(ReferenceRoms.Sa1LevelSave), cleanPack, wrote, Remap(), report);
        Assert.Empty(report);
        Assert.Equal(Tokens(generated), Tokens(LmSa1Forms.LevelSave));
    }

    /// <summary>The pinned ExAnimation diff IS the oracle's — with the engine's call to our trampoline
    /// left as ours, not LM's address of its own.</summary>
    [Sa1ExAnimSaveFact]
    public void the_exanim_save_diff_regenerates_from_the_oracle()
    {
        var (cleanPack, wrote) = CleanPack();
        var report = new List<string>();
        var generated = LmSa1Forms.GenerateExAnim(Rom.Load(ReferenceRoms.ExAnim1), Rom.Load(ReferenceRoms.Sa1ExAnimSave), cleanPack, wrote, Remap(), report);
        Assert.Empty(report);
        Assert.Equal(Tokens(generated), Tokens(LmSa1Forms.ExAnimSave));
        Assert.DoesNotContain(Tokens(generated), t => t.StartsWith($"{LmExAnimEngine.EngineSnes + LmExAnimEngine.Sa1TrampolineOperand:X6}="));
    }

    private static string[] Tokens(string s) => s.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Rules 1 and 2 as an invariant of the whole prep: at every byte the pack wrote inside a
    /// prep stamp, the SA-1 form either has a byte of our own (the pack remapped the instruction our
    /// hook displaced, and ours stands) or agrees with LM's SA-1 save — LM's own SA-1 byte, or
    /// nothing where LM left the pack's. A vanilla or LM LoROM byte written there undoes the pack's
    /// remap — $02ABF3 was the crash.</summary>
    [Sa1LevelSaveFact]
    public void the_sa1_form_agrees_with_lunar_magic_wherever_the_pack_wrote()
    {
        var prepped = Rom.Load(ReferenceRoms.Vanilla);
        RomPrep.Apply(prepped);
        var (_, wrote) = PackOn(prepped);                    // the pack's writes on OUR base: its code relocates past our RATS block
        var (cleanPack, _) = CleanPack();                     // the pack's own bytes as the oracle has them, unrelocated
        var vanilla = Rom.Load(ReferenceRoms.Vanilla);
        var after = Rom.Load(ReferenceRoms.LmAfter);
        var oracle = Rom.Load(ReferenceRoms.Sa1LevelSave);
        var lorom = Overlay(RomPrep.BuildStamps(LmSa1Forms.PrepVersion));
        var sa1 = Overlay(RomPrep.Sa1Stamps(LmSa1Forms.PrepVersion, Remap()));
        LmSa1Forms.Apply(sa1);
        // A hook's JSL/JML target is ours even where a byte of it equals LM's (LM's blocks keep the
        // in-bank offsets, so the high byte often does); LM's SA-1 save has its own copies' addresses there.
        var hookTargets = new HashSet<int>();
        foreach (var (snes, bytes) in LmLevelEntry.Hooks().Concat(LmLevelEntry.HeightHooks()))
            for (int i = 0; i < bytes.Length;)
                if (bytes[i] is 0x22 or 0x5C) { for (int k = 1; k <= 3; k++) hookTargets.Add(Rom.SnesToPc(snes + i + k)); i += 4; }
                else i++;
        var bad = new List<string>();
        int checked_ = 0;
        foreach (int pc in wrote)
        {
            if (pc >= vanilla.ActualRomSize || hookTargets.Contains(pc) || !lorom.TryGetValue(pc, out var l)) continue;
            int snes = Rom.PcToSnes(pc);
            byte v = vanilla.ReadByte(snes), a = after.ReadByte(snes);
            if (l != v && l != a) continue;                   // ours: the SA-1 form is Asm's, not LM's to judge
            checked_++;
            byte o = oracle.ReadByte(snes);
            if (sa1.TryGetValue(pc, out var s) ? s != o : o != cleanPack.ReadByte(snes))
                bad.Add($"${snes:X6}: LoROM {l:X2} (vanilla {v:X2}, LM {a:X2}) → SA-1 {(sa1.ContainsKey(pc) ? s.ToString("X2") : "pack's")}, LM's SA-1 save {o:X2}");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
        Assert.True(checked_ > 50, $"only {checked_} carried bytes at pack sites");
    }

    [RealRomFact]
    public void the_exanimation_hooks_displaced_bytes_are_vanillas()
    {
        var vanilla = Rom.Load(ReferenceRoms.Vanilla);
        foreach (var (snes, bytes) in LmExAnimEngine.DisplacedVanilla().Concat(LmOwExAnimEngine.DisplacedVanilla()))
            for (int i = 0; i < bytes.Length; i++)
                Assert.True(bytes[i] == vanilla.ReadByte(snes + i), $"${snes + i:X6}");
    }

    /// <summary>LM's SA-1 ExAnimation install, as its oracle holds it: the trampoline's bytes are the
    /// ones LM appended to its engine, and the engine's first stub call names it.</summary>
    [Sa1ExAnimSaveFact]
    public void the_sa1_engine_calls_lunar_magics_trampoline_at_our_address()
    {
        var oracle = Rom.Load(ReferenceRoms.Sa1ExAnimSave);
        int theirs = 0x10D2E4;                                                   // LM's engine in the oracle, behind its RATS tag
        int trampoline = oracle.ReadValue(theirs + LmExAnimEngine.Sa1TrampolineOperand, 3);
        Assert.Equal(theirs + LmExAnimEngine.Engine().Length, trampoline);
        var bytes = LmExAnimEngine.Sa1Trampoline();
        Assert.Equal(Convert.ToHexString(bytes), Convert.ToHexString(oracle.Data.AsSpan(oracle.FileOffset(trampoline), bytes.Length)));
        Assert.Equal(0x7FC020, oracle.ReadValue(theirs + 0x792, 3));          // the second call goes straight to the copied stub
        Assert.Equal(LmExAnimEngine.Sa1TrampolineSnes, (LmExAnimEngine.Engine(sa1: true)[LmExAnimEngine.Sa1TrampolineOperand + 2] << 16)
            | LmExAnimEngine.Engine(sa1: true)[LmExAnimEngine.Sa1TrampolineOperand + 1] << 8 | LmExAnimEngine.Engine(sa1: true)[LmExAnimEngine.Sa1TrampolineOperand]);
    }

    /// <summary>The first boot's bytes (reference/SA1_PORT.md, phase 2 findings), on prepped + pack +
    /// ApplySa1: the pack's relocated JML at $02ABF3 survives, LM's hook at $05D9A1 still names our
    /// block, LM's grown routine sits at $0DE1F0 with its marker at $0DE20C, the bank-$1F engine
    /// reads the moved RAM, the ExAnimation setup hook names our engine and the engine calls our
    /// trampoline, while the overworld ExAnimation hook is back to clean + pack.</summary>
    [Sa1LevelSaveFact]
    public void apply_sa1_leaves_the_image_the_first_boot_needed()
    {
        var rom = Rom.Load(ReferenceRoms.Vanilla);
        RomPrep.Apply(rom);
        var r = Asar.Patch(rom, PackAsm);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        int jml = rom.ReadValue(0x02ABF2, 4);                    // the pack's second JML, relocated by our RATS block
        Assert.Equal(0x5C, jml & 0xFF);
        RomPrep.ApplySa1(rom, RomPrep.Version, Remap(), r.Blocks.Select(b => (b.Snes, b.Length)));

        Assert.Equal(jml, rom.ReadValue(0x02ABF2, 4));
        var hook = LmLevelEntry.Hooks().Single(h => h.Snes == 0x05D9A1).Bytes;
        Assert.Equal(Convert.ToHexString(hook), Convert.ToHexString(rom.Data.AsSpan(rom.FileOffset(0x05D9A1), hook.Length)));
        Assert.Equal("C220A20EBF60C07F9F000041CACA10F4648AE220A7656B", Convert.ToHexString(rom.Data.AsSpan(rom.FileOffset(0x0DE1F0), 23)));
        Assert.Equal(0x01014D4C, rom.ReadValue(0x0DE20C, 4));                     // "LM 01 01"
        Assert.Equal(0x22, rom.ReadByte(0x0583AD));                               // JSL our engine : NOP
        Assert.Equal(LmExAnimEngine.SetupEntry, rom.ReadValue(0x0583AE, 3));
        Assert.Equal(LmExAnimEngine.Sa1TrampolineSnes, rom.ReadValue(LmExAnimEngine.EngineSnes + LmExAnimEngine.Sa1TrampolineOperand, 3));
        Assert.Equal(0x7FC020, rom.ReadValue(LmExAnimEngine.EngineSnes + 0x792, 3));
        Assert.Equal(0x3000A9, rom.ReadValue(LmExAnimEngine.EngineSnes + 0x47B, 3));   // LDA #$3000 : TCD, LM's SA-1 direct page
        Assert.Equal(Convert.ToHexString(LmExAnimEngine.Sa1Trampoline()), Convert.ToHexString(rom.Data.AsSpan(rom.FileOffset(LmExAnimEngine.Sa1TrampolineSnes), 16)));
        var owSetup = LmOwExAnimEngine.DisplacedVanilla().First(h => h.Snes == 0x048086).Bytes;
        Assert.Equal(Convert.ToHexString(owSetup), Convert.ToHexString(rom.Data.AsSpan(rom.FileOffset(0x048086), owSetup.Length)));   // the overworld engine's setup hook: vanilla's bytes, no JSL
    }

    private static Sa1Remap Remap() => Sa1Remap.FromSpriteTables(File.ReadAllText(Path.Combine(ReferenceRoms.Sa1Pack, "more_sprites", "sprite_tables.asm")));

    private static Dictionary<int, byte> Overlay(List<(int Pc, byte[] Bytes)> stamps)
    {
        var o = new Dictionary<int, byte>();
        foreach (var (pc, bytes) in stamps)
            for (int i = 0; i < bytes.Length; i++) o[pc + i] = bytes[i];
        return o;
    }
}
