using Xunit;

namespace PipeDream.Tests;

/// <summary>The SA-1 report's static half (reference/SA1_PORT.md, phase 4): what a resource's ASM
/// means for an SA-1 build, from its text.</summary>
public class Sa1CompatTests
{
    private static Sa1Compat.Result Scan(string asm, Sa1Compat.Cpu cpu = Sa1Compat.Cpu.Either) => Sa1Compat.Scan([("x.asm", asm)], cpu);

    [Fact]
    public void smws_ram_by_its_lorom_address_breaks_and_through_the_defines_does_not()
    {
        var r = Scan("    LDA $1DFB\n    STA $13CC|!addr\n    LDA $19\n    LDA $7E0019\n    LDA $0019,y\n");
        Assert.Equal(Sa1Compat.Fit.Breaks, r.OnSa1);
        Assert.Equal([1, 4, 5], r.Breaks.Select(f => f.Line));
        Assert.Contains("$7DFB", r.Breaks[0].Why);
        Assert.Equal(Sa1Compat.Fit.Ready, Scan("    LDA $1DFB|!addr\n    LDA $19\n    STA !14C8,x\n    JSR $8000\n    JML $00A242\n").OnSa1);
    }

    [Fact]
    public void literal_sprite_tables_break_by_either_address()
    {
        var r = Scan("    LDA $14C8,x\n    STA $9E,x\n    LDA $9E\n");
        Assert.Equal([1, 2], r.Breaks.Select(f => f.Line));            // $9E unindexed is not a slot's
        Assert.All(r.Breaks, f => Assert.Contains("22 slots", f.Why));
    }

    [Fact]
    public void the_lorom_branch_of_an_sa1_test_is_not_judged_for_sa1_nor_the_sa1_branch_for_lorom()
    {
        var r = Scan("""
            if !sa1
                LDA $3242,x
                STA $6DBE
            else
                LDA $14C8,x
                STA $0DBE
            endif
            if read1($00FFD5) == $23
                sa1rom
            endif
            if !SA1 == 0
                LDA $1DFB
            else
                STA $2250
            endif
            """);
        Assert.Empty(r.Breaks);
        Assert.Empty(r.NeedsSa1);
    }

    /// <summary>Found scanning two real hacks: the false alarms the first cut raised.</summary>
    [Fact]
    public void per_mapping_defines_spc700_code_jump_tables_and_the_packs_own_protocol_are_not_breaks()
    {
        Assert.Empty(Scan("if !sa1\n    !E4 = $322C\nelse\n    !E4 = $E4\nendif\n    LDA !E4,x\n").Breaks);   // sa1def style
        Assert.Empty(Scan("arch spc700\n    mov $1DFB, a\narch 65816\n").Breaks);
        Assert.Empty(Scan("    JSR ($0000,x)\n").Breaks);
        var r = Scan("    STA $0183\n    LDA $018A\n");                                   // invoke_snes, unguarded
        Assert.Empty(r.Breaks);
        Assert.Equal(2, r.NeedsSa1.Count);
        Assert.Single(Scan("    JMP ($0000)\n").Breaks);                                   // that one reads bank 0
    }

    [Fact]
    public void a_literal_behind_a_define_is_still_literal()
    {
        var r = Scan("!freeram = $1869\n!good = $1869|!addr\n    LDA !freeram\n    STA !good\n");
        var f = Assert.Single(r.Breaks);
        Assert.Equal(3, f.Line);
        Assert.Contains("!freeram", f.Why);
    }

    [Fact]
    public void the_multiplier_breaks_on_the_sa1_cpu_is_a_doubt_in_a_patch_and_fine_on_the_snes_cpu()
    {
        const string asm = "    STA $4202\n    LDA $4216\n";
        Assert.Equal(Sa1Compat.Fit.Breaks, Scan(asm, Sa1Compat.Cpu.Sa1).OnSa1);
        Assert.Equal(Sa1Compat.Fit.Check, Scan(asm, Sa1Compat.Cpu.Either).OnSa1);
        Assert.Equal(Sa1Compat.Fit.Ready, Scan(asm, Sa1Compat.Cpu.Snes).OnSa1);
        Assert.Equal(Sa1Compat.Fit.Breaks, Scan("    JSL $7F8000\n", Sa1Compat.Cpu.Sa1).OnSa1);     // work RAM, from the SA-1 CPU
        Assert.Equal(Sa1Compat.Fit.Ready, Scan("    JSL $7F8000\n", Sa1Compat.Cpu.Snes).OnSa1);
        Assert.Equal(Sa1Compat.Fit.Breaks, Scan("    LDA $7EC800,x\n", Sa1Compat.Cpu.Snes).OnSa1);  // a buffer the pack moves
    }

    [Fact]
    public void sa1_only_code_needs_sa1_and_the_doubts_are_checks()
    {
        var r = Scan("sa1rom\n    STA $2250\n    LDA $3100\n    STA $404000\n    LDA $6100\n");
        Assert.Equal(5, r.NeedsSa1.Count);
        Assert.True(r.NeedsSa1Build);
        Assert.Equal(Sa1Compat.Fit.Check, Scan("lorom\n    LDX #$0B\n-   DEX\n    BPL -\n").OnSa1);
    }

    [Fact]
    public void labels_separators_and_comments_are_read_through()
    {
        var r = Scan("Main: LDA #$01 : STA $1DFB ; a song\n.sub STA $1DFC\n+   LDA $1DF9 ; $0DBE\n");
        Assert.Equal([1, 2, 3], r.Breaks.Select(f => f.Line));
    }

    /// <summary>The report over a project: each resource the build assembles, by kind and name, with its
    /// own CPU; what it says for the switch going on, and going off.</summary>
    [Fact]
    public void the_project_report_names_each_resource_for_the_switch_either_way()
    {
        var p = new ProjectFile();
        p.Patches.Add(new() { Name = "lives.asm", Source = "org $00F5F8\n    INC $0DBE\n" });
        p.Patches.Add(new() { Name = "aware.asm", Source = "org $00F5F8\n    INC $0DBE|!addr\n" });
        p.Patches.Add(new() { Name = "fast.asm", Source = "sa1rom\norg $008000\n    STA $2250\n" });
        p.Patches.Add(new() { Name = "off.asm", Source = "    LDA $1DFB\n", Enabled = false });           // not built, not reported
        p.BlockLibrary["mult.asm"] = "db $42\n    STA $4202\n    RTL\n";
        p.Blocks["200"] = "mult.asm";
        p.Uber.Files["level/mult.asm"] = "main:\n    STA $4202\n    RTL\n"u8.ToArray();             // the SNES CPU: fine

        var items = Sa1Report.Of(p);
        Assert.Equal(["patch lives.asm", "patch aware.asm", "patch fast.asm", "block mult.asm", "UberASM level/mult.asm"],
                     items.Select(i => $"{i.Kind} {i.Name}"));
        string on = Sa1Report.Summary(items, on: true);
        Assert.Contains("• patch lives.asm: line 2: INC $0DBE", on);
        Assert.Contains("• block mult.asm", on);
        Assert.DoesNotContain("aware.asm", on);
        Assert.DoesNotContain("level/mult.asm", on);
        string off = Sa1Report.Summary(items, on: false);
        Assert.StartsWith("Needs SA-1:", off);
        Assert.Contains("fast.asm", off);
        Assert.DoesNotContain("lives.asm", off);
        Assert.Equal("breaks on SA-1", Sa1Report.Badge(items[0].Result, sa1On: true));
        Assert.Null(Sa1Report.Badge(items[0].Result, sa1On: false));                             // off: quiet but for "needs SA-1"
        Assert.Null(Sa1Report.Badge(items[1].Result, sa1On: false));
        Assert.Equal("SA-1 ready", Sa1Report.Badge(items[1].Result, sa1On: true));
        Assert.Equal("needs SA-1", Sa1Report.Badge(items[2].Result, sa1On: false));
        Assert.Contains("and 1 more", Sa1Report.Summary(items, on: true, maxItems: 1));
    }

    /// <summary>Pipe Dream's own sprite routines run on the SA-1 CPU there and were written for it: none
    /// of them may come out as breaking, which also keeps the scanner honest about false alarms.</summary>
    [Fact]
    public void our_own_sprite_routines_are_ready_for_sa1()
    {
        var r = Sa1Compat.Scan(SpriteInsert.BuiltinRoutines().Select(kv => (kv.Key, kv.Value)), Sa1Compat.Cpu.Sa1);
        Assert.True(r.Breaks.Count == 0, string.Join("\n", r.Breaks));
    }
}
