using Xunit;

namespace PipeDream.Tests;

/// <summary>BOWSIE's insertion (OwSpriteInsert): a sprite assembled through its shared routines and vldc9 system, then
/// Lunar Magic's list loaded by the system's own loader in the 65816 interpreter.</summary>
public class OwSpriteInsertTests
{
    private const string Bird = """
        !extra = 3
        init:
            LDA #$11
            STA $7FB000
            RTL
        main:
            %random_range()         ; a shared routine resolves (not run: it calls SMW's)
            RTL
        """;

    private static ProjectFile Project(string sprite = Bird)
    {
        var p = new ProjectFile();
        p.Sprites["bird"] = new() { Kind = PixiImport.Overworld, Source = sprite };
        p.SpriteSlots["overworld 05"] = "bird";
        return p;
    }

    private static int Long(Rom rom, int at) => rom.ReadByte(at) | rom.ReadByte(at + 1) << 8 | rom.ReadByte(at + 2) << 16;

    [AsarFact]
    public void a_sprite_goes_in_and_the_systems_loader_spawns_it_from_the_list_with_its_extra_bytes()
    {
        var rom = TestRom.Create(size: 0x100000);
        var r = OwSpriteInsert.Insert(rom, Project());
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Equal((3, 0), (Overworld.SpriteExtraCount(rom, 5), Overworld.SpriteExtraCount(rom, 6)));  // its !extra, in the ROM's table

        Assert.Null(Overworld.WriteSprites(rom, [new(0, 5, 10, 12, 2, [0xAA, 0xBB, 0xCC])]));
        var cpu = new Cpu65816(rom);
        cpu.CallNear(0x04F625);                                               // custom_ow_sprite_load_main, the main map
        int x = Enumerable.Range(0, 24).Select(i => 2 * i).Single(i => cpu.Ram7E[0x14C8 + i] != 0);
        Assert.Equal(5, cpu.Ram7E[0x14C8 + x]);
        Assert.Equal((80, 96, 16), (cpu.Ram7E[0x1588 + x], cpu.Ram7E[0x15B8 + x], cpu.Ram7E[0x15E8 + x]));   // 8px steps
        Assert.Equal((0xAA, 0xBB, 0xCC), (cpu.Ram7E[0x0DE0 + x], cpu.Ram7E[0x0DE1 + x], cpu.Ram7E[0x0E10 + x]));

        int init = Long(rom, Long(rom, OwSpriteInsert.Used + 4) + (5 - 1) * 3);   // BOWSIE's pointer to the init table
        cpu.CallLong(init);
        Assert.Equal(0x11, cpu.Ram7F[0xB000]);
    }

    [AsarFact]
    public void the_per_frame_loop_keeps_16_bit_immediates_and_an_empty_list_is_a_zero_pointer()
    {
        var rom = TestRom.Create(size: 0x100000);
        Assert.True(OwSpriteInsert.Insert(rom, Project()).Ok);
        // run_ow_sprite under REP #$21: an 8-bit LDA #$00 here ran the next bytes as JML $BD2EA2 (crashed in Mesen).
        Assert.Equal("A9 00 01 8D 5A 18 A9 00 00 8D 5C 18",
                     string.Join(" ", Enumerable.Range(0x04F770, 12).Select(a => $"{rom.ReadByte(a):X2}")));
        Assert.Null(Overworld.WriteSprites(rom, []));
        Assert.Equal(0, Long(rom, Overworld.SpriteList));       // BOWSIE's loader reads $FFFFFF as a list
    }

    [AsarFact]
    public void the_new_overworld_sprite_template_builds_with_no_extra_bytes()
    {
        var rom = TestRom.Create(size: 0x100000);
        var r = OwSpriteInsert.Insert(rom, Project(Services.EditorSession.SpriteTemplate("Bird", PixiImport.Overworld)));
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Equal(0, Overworld.SpriteExtraCount(rom, 5));
    }

    [AsarFact]
    public void a_sprite_without_main_or_with_an_error_names_its_file()
    {
        var r = OwSpriteInsert.Insert(TestRom.Create(size: 0x100000), Project("init:\n    RTL\n"));
        Assert.Contains("bird needs both init: and main:", Assert.Single(r.Errors));
        r = OwSpriteInsert.Insert(TestRom.Create(size: 0x100000), Project("init:\n    LDA nowhere\nmain:\n    RTL\n"));
        Assert.StartsWith("bird: ", Assert.Single(r.Errors));
    }

    [Fact]
    public void the_editor_reads_each_listed_sprites_extra_bytes_before_any_build()
    {
        var rom = TestRom.Create(size: 0x100000);
        var p = Project();
        p.Sprites["plain"] = new() { Kind = PixiImport.Overworld, Source = "init:\nmain:\n    RTL\n" };
        p.SpriteSlots["overworld 06"] = "plain";
        p.Sprites["level"] = new() { Kind = "sprite", Source = "" };
        p.SpriteSlots["07"] = "level";                                          // a PIXI slot is not an overworld one
        var counts = OwSpriteInsert.ExtraCounts(rom, p)!;
        Assert.Equal((3, 2, 0), (counts[5], counts[6], counts[7]));            // its !extra; BOWSIE's default 2; unlisted none
    }

    [Fact]
    public void overworld_slots_are_their_own_kind_and_stay_out_of_pixis_driver()
    {
        Assert.Equal("overworld 05", PixiImport.NormalSlot("Overworld 5"));
        Assert.Equal(PixiImport.Overworld, PixiImport.SlotKind("overworld 7E"));
        Assert.Null(PixiImport.SlotKind("overworld 00"));                       // 00 ends Lunar Magic's list
        Assert.Null(PixiImport.SlotKind("overworld 7F"));
        Assert.False(PixiImport.HasConfig(PixiImport.Overworld));
        Assert.False(PixiImport.IsPixiSlot("overworld 05"));
        Assert.True(PixiImport.IsPixiSlot("cluster 05"));
    }

    [Fact]
    public void placed_sprites_keep_their_fields_through_the_project_file()
    {
        var p = new ProjectFile();
        p.Overworld.Sprites = [new(3, 0x12, 63, 7, 31, [1, 2])];
        var s = Assert.Single(ProjectFile.FromJson(p.ToJson()).Overworld.Sprites!);
        Assert.Equal((3, 0x12, 63, 7, 31), (s.Submap, s.Number, s.X, s.Y, s.Z));
        Assert.Equal([1, 2], s.Extra);
    }
}
