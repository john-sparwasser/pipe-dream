using Xunit;

namespace PipeDream.Tests;

/// <summary>Skips when the vanilla ROM or the SA-1 oracle (an LM-saved SA-1 hack) is absent.</summary>
public sealed class Sa1OracleFactAttribute : FactAttribute
{
    public Sa1OracleFactAttribute()
    {
        if (!File.Exists(TestRom.RealRomPath)) Skip = "real ROM not present: " + TestRom.RealRomPath;
        else if (!File.Exists(ReferenceRoms.Sgdq2024)) Skip = "SA-1 oracle not present: " + ReferenceRoms.Sgdq2024;
    }
}

/// <summary>
/// The SA-1 Pack's RAM moves (reference/SA1_PORT.md). The expected bytes are the pack's own output
/// at the same vanilla sites, read off a clean ROM it was applied to, so a disagreement is our
/// remap saying something different from the pack.
/// </summary>
public class Sa1RemapTests
{
    /// <summary>The tables these tests touch, in the pack's sprite_tables.asm format; the values are
    /// the More Sprites column of its Sprite-Remap doc.</summary>
    private const string Tables = """
        %define_sprite_table(sprite_num, $9E, $9E, $3200)
        %define_sprite_table(sprite_speed_y, $AA, $AA, $9E)
        %define_sprite_table(sprite_status, $14C8, $74C8, $3242)
        %define_sprite_table(sprite_y_high, $14D4, $74D4, $3258)
        %define_sprite_table(sprite_misc_1534, $1534, $7534, $32B0)
        %define_sprite_table(sprite_misc_187b, $187B, $787B, $3410)
        """;

    private static readonly Sa1Remap Remap = Sa1Remap.FromSpriteTables(Tables);

    [Theory]
    [InlineData(0x0019, 0x3019)]     // direct page, reached absolutely
    [InlineData(0x1925, 0x7925)]     // $0100-$1FFF → BW-RAM
    [InlineData(0x14C8, 0x3242)]     // a sprite table: its own move, not +$6000
    [InlineData(0x14D3, 0x324D)]     // ... for every one of its 12 slots
    [InlineData(0x00AA, 0x309E)]     // a direct-page sprite table that stayed in the direct page
    [InlineData(0x2118, 0x2118)]     // hardware
    [InlineData(0x8000, 0x8000)]     // ROM
    public void absolute_operands_move_by_the_packs_rules(int from, int to) => Assert.Equal(to, Remap.Abs(from));

    [Theory]
    [InlineData(0x7EC800, 0x40C800)]
    [InlineData(0x7FC9FF, 0x41C9FF)]
    [InlineData(0x7E1938, 0x418A00)]
    [InlineData(0x7E0100, 0x400100)]
    [InlineData(0x7E0019, 0x003019)]
    [InlineData(0x001925, 0x007925)]
    [InlineData(0x700010, 0x41C010)]
    [InlineData(0x7EB900, 0x7EB900)]   // W-RAM the pack leaves where it is
    [InlineData(0x0DB000, 0x0DB000)]
    public void long_operands_move_by_the_packs_rules(int from, int to) => Assert.Equal(to, Remap.Long(from));

    [Fact]
    public void a_direct_page_sprite_table_that_left_the_direct_page_has_no_byte()
    {
        Assert.Null(Remap.SpriteDp(0x9E));          // → $3200
        Assert.Equal(0x9E, Remap.SpriteDp(0xAA));   // → $309E, still direct page
        Assert.Equal(0x19, Remap.SpriteDp(0x19));
    }

    [Theory]
    // $00A2AF: STZ $1888 : STZ $1889 : LDA $1887 : BEQ : DEC $1887
    [InlineData("9C88189C8918AD8718F01BCE8718", "9C88789C8978AD8778F01BCE8778", true, true)]
    // $01C08C: STA $187B,X : AND #$01 : STA $14D4,X : STA $1534,X : RTS
    [InlineData("9D7B1829019DD4149D341560", "9D103429019D58329DB03260", true, true)]
    // $00AB0D: LDX #$07 : LDA [$00] : STA $2118 : XBA : ORA [$00] : STA $1BB2,X
    [InlineData("A207A7008D1821EB07009DB21B", "A207A7008D1821EB07009DB27B", false, true)]
    // a jump, a long call and a push are not RAM: JSR $1234 : JSL $001925 : PEA $1925
    [InlineData("20341222251900F42519", "20341222251900F42519", true, true)]
    public void code_moves_like_the_pack_moved_vanilla(string from, string to, bool m8, bool x8)
    {
        var problems = new List<string>();
        Assert.Equal(to, Convert.ToHexString(Remap.Code(Convert.FromHexString(from), m8, x8, problems)));
        Assert.Empty(problems);
    }

    /// <summary>$9E-$EF are scratch outside sprite code, and Lunar Magic's SA-1 VRAM code keeps them
    /// (`LDA $AB` stays `LDA $AB`), so code is not read as sprite code. TCD is reported.</summary>
    [Fact]
    public void direct_page_stays_and_a_direct_page_write_is_reported()
    {
        var problems = new List<string>();
        var code = Convert.FromHexString("A5ABB59E5B");        // LDA $AB : LDA $9E,X : TCD
        Assert.Equal(code, Remap.Code(code, true, true, problems));
        Assert.Single(problems);
    }

    /// <summary>The prep carries Lunar Magic's entrance routines byte for byte; LM writes its own SA-1
    /// forms into an SA-1 ROM. Our remap of the LoROM bytes must be exactly LM's SA-1 bytes.</summary>
    [Sa1OracleFact]
    public void lunar_magics_routines_remap_to_lunar_magics_own_sa1_forms()
    {
        var prepped = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(prepped);
        var oracle = Rom.Load(ReferenceRoms.Sgdq2024);
        foreach (var (snes, len) in new[] { (RomPrep.LmMainEntranceRoutine, 27), (RomPrep.LmSecondaryRoutine, 24) })
        {
            byte[] Read(Rom r) => [.. Enumerable.Range(0, len).Select(i => r.ReadByte(snes + i))];
            var problems = new List<string>();
            Assert.Equal(Convert.ToHexString(Read(oracle)), Convert.ToHexString(Remap.Code(Read(prepped), true, true, problems)));
            Assert.Empty(problems);
        }
    }
}
