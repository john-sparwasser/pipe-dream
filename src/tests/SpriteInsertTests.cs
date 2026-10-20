using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// Custom sprites, phase 2 (reference/PIXI_PORT.md): our driver and the build step. A sprite in a
/// global slot is assembled, its INIT and MAIN land in the table PIXI's header points at, its
/// config's tweakers and record lengths land, and the hooks are in. That the sprite then RUNS is
/// measured in Mesen (the doc's status), with the ROM <see cref="Build"/> makes.
/// </summary>
public class SpriteInsertTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("pdspr-").FullName;
    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    /// <summary>A sprite that says it ran: INIT stores $11 and its first extra byte, MAIN counts frames.</summary>
    internal const string Probe = """
        print "INIT ",pc
            LDA #$11
            STA $0F5E
            LDA !extra_byte_1,x
            STA $0F60
            RTL
        print "MAIN ",pc
            PHB : PHK : PLB
            INC $0F5F
            PLB
            RTL
        """;

    /// <summary>A project with <see cref="Probe"/> in slot 1A, placed once in level 105 near the
    /// start as a custom sprite (extra bits 2) with extra bytes AB CD, built.</summary>
    internal static (string Status, Rom? Built) Build(string dir, string source = Probe, int extraBytes = 2, Dictionary<string, string>? pixiFiles = null,
                                                       Action<ProjectFile, Services.SpriteEdit, SpriteData>? more = null)
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var config = new PixiConfig { ActLike = 0x36, AsmFile = "probe.asm", ExtraBytes = extraBytes, ExtraBytesSet = 0 };
        config.Tweak[4] = 0x01;                                            // $1686: inedible, so it is visible in the table
        p.Data.Sprites["probe"] = new ProjectFile.SpriteEntry { Config = config.ToJson(), Source = source };
        p.Data.SpriteSlots["1A"] = "probe";
        foreach (var (path, text) in pixiFiles ?? []) p.Data.PixiFiles[path] = text;

        var baseRom = Rom.Load(p.BaseRomPath);
        var sd = SpriteData.Parse(baseRom, 0x105);
        var edit = new Services.SpriteEdit(sd, null, false);
        Assert.True(edit.Place(0x1A, 12, 22));
        Assert.True(edit.SetData(sd.Sprites.Count - 1, 0x1A, 2, [0xAB, 0xCD]));
        more?.Invoke(p.Data, edit, sd);
        var st = p.Data.Level(0x105);
        st.Objects = LevelParser.Parse(baseRom, 0x105).Objects.Select(ProjectFile.ObjectDto.From).ToList();
        st.SpriteMemory = sd.SpriteMemory; st.Buoyancy = sd.Buoyancy;
        st.Sprites = sd.Sprites.Select(ProjectFile.SpriteDto.From).ToList();
        p.Save();

        var (status, outPath) = RomBuilder.Build(p);
        return (status, outPath is null ? null : Rom.Load(outPath));
    }

    [AsarRomFact]
    public void a_slotted_sprite_lands_in_the_table_with_its_hooks()
    {
        var (status, rom) = Build(dir);
        Assert.True(rom is not null, status);
        var r = rom!;
        Assert.Equal("STSD", System.Text.Encoding.ASCII.GetString(r.Data, r.FileOffset(0x02FFE2), 4));
        int table = r.ReadValue(0x02FFEE, 3), e = table + 0x1A * 0x10;
        Assert.Equal(1, r.ReadByte(e));                                    // type: custom
        Assert.Equal(0x36, r.ReadByte(e + 1));                             // acts-like
        Assert.Equal(0x01, r.ReadByte(e + 6));                             // $1686
        int init = r.ReadValue(e + 8, 3), main = r.ReadValue(e + 11, 3);
        Assert.Equal(0xA9, r.ReadByte(init));                              // LDA #$11
        Assert.Equal(0x8B, r.ReadByte(main));                              // PHB
        // An empty number is SMW's own sprite: acts-like itself, its tweakers, pointers at an RTL.
        Assert.Equal(0x0F, r.ReadByte(table + 0x0F * 0x10 + 1));
        Assert.Equal(r.ReadByte(0x07F26C + 0x0F), r.ReadByte(table + 0x0F * 0x10 + 2));
        Assert.Equal(0x018021, r.ReadValue(table + 0x0F * 0x10 + 8, 3));
        // Record lengths: 3 + the config's counts, for custom (2) and custom with the extra bit (3).
        Assert.Equal(5, r.SpriteEntrySize(2, 0x1A));
        Assert.Equal(3, r.SpriteEntrySize(3, 0x1A));
        // The hooks.
        foreach (int at in (int[])[0x02A846, 0x018127, 0x018172, 0x0185C3, 0x0187A7, 0x07F785])
            Assert.Equal(0x5C, r.ReadByte(at));                            // JML
        foreach (int at in (int[])[0x02A94B, 0x02A963, 0x02A9C9])
            Assert.Equal(0x22, r.ReadByte(at));                            // JSL
        // Sprite 9B's INIT moved off $0187A7 to a stub that does what it did.
        int init9B = 0x010000 | r.ReadValue(0x0182B3, 2);
        Assert.NotEqual(0x0187A7, init9B);
        Assert.Equal([0x22, 0x59, 0xDA, 0x02, 0x60], r.Data.AsSpan(r.FileOffset(init9B), 5).ToArray());
        // The level reads back through the table: the placement carries its two bytes.
        Assert.Equal([0xAB, 0xCD], SpriteData.Parse(r, 0x105).Sprites.Single(s => s.Number == 0x1A && s.Extra == 2).ExtraBytes);
        // Placed after the level's own sprites but left of them, it is listed first: the loader
        // stops at the first record past the screen it loads, so an unsorted list never spawns it.
        var order = SpriteData.Parse(r, 0x105).Sprites.Select(s => (s.Band, s.Screen, s.XNibble)).ToList();
        Assert.Equal(order.Order(), order);
        Assert.Equal(0x1A, SpriteData.Parse(r, 0x105).Sprites[0].Number);
    }

    /// <summary>A routine goes in once, under PIXI's macro name (folders run together), and
    /// routines the routine calls come with it; one nobody calls stays out.</summary>
    [AsarRomFact]
    public void the_routines_a_sprite_calls_go_in_once()
    {
        var files = new Dictionary<string, string>
        {
            ["routines/Sub/Thing.asm"] = "    %Other()\n    LDA #$5A\n    RTL\n",
            ["routines/Other.asm"] = "    LDA #$A5\n    RTL\n",
            ["routines/Unused.asm"] = "    this is not even asm\n",
        };
        string source = Probe.Replace("    LDA #$11\n", "    %SubThing()\n    %SubThing()\n    LDA #$11\n");
        var (status, rom) = Build(dir, source, pixiFiles: files);
        Assert.True(rom is not null, status);
        var r = rom!;
        int init = r.ReadValue(r.ReadValue(0x02FFEE, 3) + 0x1A * 0x10 + 8, 3);
        Assert.Equal(0x22, r.ReadByte(init));                              // JSL SubThing
        int thing = r.ReadValue(init + 1, 3);
        Assert.Equal(thing, r.ReadValue(init + 5, 3));                     // the same copy, twice
        Assert.Equal(0x22, r.ReadByte(thing));                             // which JSLs Other
        Assert.Equal([0xA9, 0xA5, 0x6B], r.Data.AsSpan(r.FileOffset(r.ReadValue(thing + 1, 3)), 3).ToArray());
    }

    /// <summary>Adds the sprite <paramref name="name"/> of <paramref name="kind"/> in
    /// <paramref name="slot"/>, whose code stores <paramref name="mark"/> at $0F61 (INIT, or MAIN
    /// for shooters and generators, which only run MAIN) and counts frames at $0F62.</summary>
    internal static void Add(ProjectFile d, string name, string kind, string slot, int mark, int extraBytes = 0)
    {
        var config = new PixiConfig { Type = kind == "sprite" ? 1 : 2, ActLike = 0x36, AsmFile = name + ".asm", ExtraBytes = extraBytes };
        string body = kind == "sprite"
            ? $"print \"INIT \",pc\n    LDA #${mark:X2}\n    STA $0F61\n    RTL\nprint \"MAIN \",pc\n    RTL\n"
            : $"print \"MAIN \",pc\n    LDA #${mark:X2}\n    STA $0F61\n    INC $0F62\n    RTL\n";
        d.Sprites[name] = new ProjectFile.SpriteEntry { Kind = kind, Config = config.ToJson(), Source = body };
        d.SpriteSlots[slot] = name;
    }

    /// <summary>A level's own B0 wins over the global B0 there, and B0-BF records are 4 extra
    /// bytes long once any level has its own sprites.</summary>
    [AsarRomFact]
    public void a_levels_own_sprite_takes_its_slot_there()
    {
        var (status, rom) = Build(dir, more: (d, edit, sd) =>
        {
            Add(d, "everywhere", "sprite", "B0", 0x33);
            Add(d, "here", "sprite", "105:B0", 0x22, extraBytes: 1);
            Assert.True(edit.Place(0xB0, 14, 22));
            Assert.True(edit.SetData(sd.Sprites.Count - 1, 0xB0, 2, [1, 2, 3, 4]));
        });
        Assert.True(rom is not null, status);
        var r = rom!;
        Assert.Equal(7, r.SpriteEntrySize(2, 0xB0));
        Assert.Equal(7, r.SpriteEntrySize(3, 0xBF));
        int table = r.ReadValue(0x02FFEE, 3);
        int InitOf(int entry) => r.ReadByte(r.ReadValue(entry + 8, 3) + 1);          // the LDA #mark operand
        Assert.Equal(0x33, InitOf(table + 0xB0 * 0x10));                             // global B0
        int blocks = LevelBlocksOf(r);
        int level105 = r.ReadValue(blocks + 0x105 * 2, 2);
        Assert.NotEqual(0, level105);
        Assert.Equal(0x22, InitOf(table + level105 + 0xB0 * 0x10));                  // 105's own B0
        Assert.Equal(0, r.ReadValue(blocks + 0x106 * 2, 2));                          // 106 has none
    }

    /// <summary>The driver's LevelBlocks: the label is not in the ROM, so it is found as the
    /// operand of the one LDA long,X in EntryOf after `LDA $010B : AND #$01FF : ASL : TAX`.</summary>
    private static int LevelBlocksOf(Rom r)
    {
        byte[] pattern = [0xAD, 0x0B, 0x01, 0x29, 0xFF, 0x01, 0x0A, 0xAA, 0xBF];
        int at = r.Data.AsSpan().IndexOf(pattern);
        Assert.True(at >= 0, "EntryOf not found");
        return r.Data[at + 9] | r.Data[at + 10] << 8 | r.Data[at + 11] << 16;
    }

    /// <summary>A status entry point the sprite prints lands in its StatusTable entry, at the
    /// place the driver's StatusSlot gives its status; one it does not print stays 0.</summary>
    [AsarRomFact]
    public void the_status_entry_points_a_sprite_prints_land_in_the_status_table()
    {
        string source = Probe + "\nprint \"KICKED \",pc\n    LDA #$0A\n    RTL\nprint \"MOUTH \",pc\n    LDA #$07\n    RTL\n";
        var (status, rom) = Build(dir, source);
        Assert.True(rom is not null, status);
        var r = rom!;
        int e = StatusTableOf(r) + 0x1A * 0x10;
        Assert.Equal(0, r.ReadValue(e, 3));                                 // CARRIABLE: none
        Assert.Equal([0xA9, 0x0A], r.Data.AsSpan(r.FileOffset(r.ReadValue(e + 3, 3)), 2).ToArray());   // KICKED
        Assert.Equal(0, r.ReadValue(e + 6, 3));                             // CARRIED: none
        Assert.Equal([0xA9, 0x07], r.Data.AsSpan(r.FileOffset(r.ReadValue(e + 9, 3)), 2).ToArray());   // MOUTH
        Assert.Equal(0, r.ReadValue(e + 12, 3));                            // GOAL: none
        Assert.Equal(0, r.ReadValue(StatusTableOf(r) + 0x0F * 0x10 + 3, 3));   // an empty number has none
    }

    /// <summary>The driver's StatusTable, found as LevelBlocks is: the operand of CallStatus's
    /// `ADC $00 : TAX : SEP #$20 : LDA StatusTable+2,x`.</summary>
    private static int StatusTableOf(Rom r)
    {
        byte[] pattern = [0x65, 0x00, 0xAA, 0xE2, 0x20, 0xBF];
        int at = r.Data.AsSpan().IndexOf(pattern);
        Assert.True(at >= 0, "CallStatus not found");
        return (r.Data[at + 6] | r.Data[at + 7] << 8 | r.Data[at + 8] << 16) - 2;
    }

    /// <summary>Cluster sprite 02 (01 empty) and extended 00, with its CAPE: each MAIN lands in its
    /// kind's table at its list number, the CAPE in the extended cape table, and the three hooks
    /// are in.</summary>
    internal static void AddListed(ProjectFile d)
    {
        d.Sprites["sparkle"] = new() { Kind = "cluster", Source = "print \"MAIN \",pc\n    LDA #$C1\n    RTL\n" };
        d.SpriteSlots["cluster 02"] = "sparkle";
        d.Sprites["hammer"] = new() { Kind = "extended", Source = "print \"MAIN \",pc\n    LDA #$E1\n    RTL\nprint \"CAPE \",pc\n    LDA #$CA\n    RTL\n" };
        d.SpriteSlots["extended 00"] = "hammer";
    }

    [AsarRomFact]
    public void cluster_and_extended_sprites_land_in_their_tables()
    {
        var (status, rom) = Build(dir, more: (d, _, _) => AddListed(d));
        Assert.True(rom is not null, status);
        var r = rom!;
        foreach (int at in (int[])[0x02F815, 0x029B16, 0x029636]) Assert.Equal(0x5C, r.ReadByte(at));   // JML
        int OperandAt(int table, int n) => r.ReadByte(r.ReadValue(table + n * 3, 3) + 1);                  // its LDA #
        int cluster = TableOf(r, [0xE9, 0x09, 0x48, 0xA9], 0), extended = TableOf(r, [0xE9, 0x13, 0x48, 0xA9], 0), cape = TableOf(r, [0xE9, 0x13, 0x48, 0xA9], 1);
        Assert.Equal(0xC1, OperandAt(cluster, 2));
        Assert.Equal(0, r.ReadValue(cluster + 1 * 3, 3));
        Assert.Equal(0xE1, OperandAt(extended, 0));
        Assert.Equal(0xCA, OperandAt(cape, 0));
    }

    /// <summary>One sprite of each minor kind, at list numbers 00-04 in turn: each MAIN lands in its
    /// kind's table at its number, and the five loops are hooked.</summary>
    [AsarRomFact]
    public void minor_sprites_land_in_their_tables()
    {
        (string Kind, int Offset)[] kinds = [("minorextended", 0x0C), ("bounce", 0x08), ("smoke", 0x06), ("spinningcoin", 0x02), ("score", 0x16)];
        var (status, rom) = Build(dir, more: (d, _, _) =>
        {
            for (int i = 0; i < kinds.Length; i++)
            {
                d.Sprites[kinds[i].Kind] = new() { Kind = kinds[i].Kind, Source = $"print \"MAIN \",pc\n    LDA #${0xA0 + i:X2}\n    RTL\n" };
                d.SpriteSlots[$"{kinds[i].Kind} {i:X2}"] = kinds[i].Kind;
            }
        });
        Assert.True(rom is not null, status);
        var r = rom!;
        foreach (int at in (int[])[0x028B69, 0x02904D, 0x0296C0, 0x0299D7, 0x02ADBD]) Assert.Equal(0x5C, r.ReadByte(at));   // JML
        for (int i = 0; i < kinds.Length; i++)
        {
            int table = TableOf(r, [0xE9, (byte)kinds[i].Offset, 0x48, 0xA9], 0);
            Assert.Equal(0xA0 + i, r.ReadByte(r.ReadValue(table + i * 3, 3) + 1));
            if (i > 0) Assert.Equal(0, r.ReadValue(table, 3));                            // 00 is empty
        }
    }

    /// <summary>The level's load table moves to $7FAF00 for 255 sprites a level, as PIXI's does:
    /// no code in banks 01-03 addresses $1938 any more, each place that did calls the driver, and
    /// Lunar Magic is told the limit is 255.</summary>
    [AsarRomFact]
    public void the_load_table_moves_out_of_1938()
    {
        var (status, rom) = Build(dir);
        Assert.True(rom is not null, status);
        var r = rom!;
        byte[] absolute = [0xAD, 0xBD, 0xB9, 0x8D, 0x9D, 0x99, 0x9C, 0x9E, 0xFE, 0xDE, 0xEE, 0xCE, 0x1C, 0x0C];
        var left = new List<string>();
        for (int at = r.FileOffset(0x018000); at < r.FileOffset(0x03FFFF); at++)
            if (r.Data[at + 1] == 0x38 && r.Data[at + 2] == 0x19 && absolute.Contains(r.Data[at])) left.Add($"{at:X}");
        Assert.Empty(left);
        foreach (int at in (int[])[0x02A856, 0x02A8BB, 0x02ABF2, 0x02A936, 0x02FAE6, 0x01AC9C, 0x02AB99, 0x02D088, 0x02FF15, 0x038712, 0x03B8BA])
        {
            Assert.Contains(r.ReadByte(at), (byte[])[0x22, 0x5C]);                      // JSL, or JML
            int to = r.ReadValue(at + 1, 3);
            Assert.True(r.Data.AsSpan(r.FileOffset(to), 0x20).IndexOf([(byte)0x00, (byte)0xAF, (byte)0x7F]) >= 0, $"${at:X6} → ${to:X6} does not reach $7FAF00");
        }
        Assert.Equal(Rom.Load(TestRom.RealRomPath).ReadByte(0x0FFFE0) & 0xFE, r.ReadByte(0x0FFFE0));
    }

    /// <summary>A table one of the driver's run routines (ClusterRun, SmokeRun…) reads (the <paramref name="nth"/>
    /// one after `SBC #offset : PHA`), from its `LDA #bank : LDY #high : STY $04 : LDY #low`.</summary>
    private static int TableOf(Rom r, byte[] pattern, int nth)
    {
        int at = -1;
        for (int k = 0; k <= nth; k++)
        {
            int next = r.Data.AsSpan(at + 1).IndexOf(pattern);
            Assert.True(next >= 0, "driver code not found");
            at += 1 + next;
        }
        return r.Data[at + 4] << 16 | r.Data[at + 6] << 8 | r.Data[at + 10];
    }

    [AsarRomFact]
    public void a_sprite_that_does_not_assemble_fails_the_build_by_name()
    {
        var (status, rom) = Build(dir, "print \"INIT \",pc\n    LDA !nope\n");
        Assert.Null(rom);
        Assert.StartsWith("build failed: custom sprites: sprite probe: ", status);
        Assert.Contains("nope", status);
    }

    [AsarRomFact]
    public void a_sprite_without_main_is_refused()
    {
        var (status, rom) = Build(dir, "print \"INIT \",pc\n    RTL\n");
        Assert.Null(rom);
        Assert.Contains("no MAIN", status);
    }

    [AsarRomFact]
    public void a_placement_with_the_wrong_byte_count_gets_the_configs()
    {
        var (status, rom) = Build(dir, extraBytes: 3);
        Assert.True(rom is not null, status);
        Assert.Contains("custom sprite 1A placed with 2 extra byte(s); its config says 3", status);
        Assert.Equal([0xAB, 0xCD, 0x00], SpriteData.Parse(rom!, 0x105).Sprites.Single(s => s.Number == 0x1A && s.Extra == 2).ExtraBytes);
    }
}

/// <summary>Needs both Asar and the real ROM: a build onto a prepped SMW.</summary>
public sealed class AsarRomFactAttribute : FactAttribute
{
    public AsarRomFactAttribute()
    {
        if (!Asar.Available) Skip = "Asar library not found (set PIPEDREAM_ASAR to libasar's path)";
        else if (!File.Exists(TestRom.RealRomPath)) Skip = "real ROM not present: " + TestRom.RealRomPath;
    }
}