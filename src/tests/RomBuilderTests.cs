using Xunit;

namespace PipeDream.Tests;

/// <summary>Build pipeline: determinism, parse-back correctness, bank-cross safety.
/// All on synthetic ROMs — the vanilla degradation paths (no LM hooks) are exercised
/// implicitly since TestRom images carry no LM structures.</summary>
public class RomBuilderTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pdbuild-" + Guid.NewGuid().ToString("N")[..8]);
    private string SourceRom => Path.Combine(dir, "source.smc");

    public RomBuilderTests()
    {
        Directory.CreateDirectory(dir);
        var rom = Rom.FromBytes(TestRom.Image());
        int fo = rom.FileOffset(TestRom.LevelDataSnes);
        TestRom.LevelHeaderBytes.CopyTo(rom.Data, fo);
        rom.Data[fo + 5] = 0xFF;
        rom.SetLayer1Pointer(TestRom.TestLevel, TestRom.LevelDataSnes);
        // Terminated empty sprite stream in bank $07 so the vanilla in-place path has
        // a real stream to measure and overwrite.
        int sfo = rom.FileOffset(0x078000);
        rom.Data[sfo] = 0x00; rom.Data[sfo + 1] = 0xFF;
        rom.SetSpritePointerWord(TestRom.TestLevel, 0x8000);
        RatsWriter.FixChecksum(rom);
        File.WriteAllBytes(SourceRom, rom.Data);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void build_is_deterministic_and_the_built_rom_parses_back_the_projects_objects()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), SourceRom);
        var lvl = p.Data.Level(TestRom.TestLevel);
        lvl.Objects.Add(ProjectFile.ObjectDto.From(new LevelObject(false, 0x11, 0, 4, 8, 0x21, -1)));
        lvl.Objects.Add(ProjectFile.ObjectDto.From(new LevelObject(false, 0x12, 2, 3, 4, 0x00, -1)));
        p.Save();

        var (status1, out1) = RomBuilder.Build(p);
        Assert.NotNull(out1);
        byte[] first = File.ReadAllBytes(out1!);
        var (_, out2) = RomBuilder.Build(p);
        Assert.Equal(first, File.ReadAllBytes(out2!));        // same project → byte-identical ROM

        var built = Rom.Load(out1!);
        var parsed = LevelParser.Parse(built, TestRom.TestLevel);
        var objs = parsed.Objects.Where(o => !o.IsScreenJump).ToList();
        Assert.Equal(2, objs.Count);
        Assert.Equal(0x11, objs[0].Number);
        Assert.Equal((0, 4, 8), (objs[0].Screen, objs[0].XNibble, objs[0].Y));
        Assert.Equal(0x12, objs[1].Number);
        Assert.Equal((2, 3, 4), (objs[1].Screen, objs[1].XNibble, objs[1].Y));
        // checksum was fixed on save
        int fo = built.FileOffset(0x00FFDC);
        int comp = built.Data[fo] | (built.Data[fo + 1] << 8);
        int chk = built.Data[fo + 2] | (built.Data[fo + 3] << 8);
        Assert.Equal(0xFFFF, chk ^ comp);
    }

    [Fact]
    public void sprite_edits_on_a_vanilla_base_overwrite_in_place_when_they_fit()
    {
        var p = Project.Create(Path.Combine(dir, "proj2"), SourceRom);
        var lvl = p.Data.Level(TestRom.TestLevel);
        lvl.SpriteMemory = 0x08; lvl.Buoyancy = 1;            // header-only change: still 2 bytes
        p.Save();
        var (_, outPath) = RomBuilder.Build(p);
        var built = Rom.Load(outPath!);
        var sd = SpriteData.Parse(built, TestRom.TestLevel);
        Assert.Equal(0x08, sd.SpriteMemory);
        Assert.Equal(1, sd.Buoyancy);
        Assert.Empty(sd.Sprites);
    }

    [Fact]
    public void allocation_with_bank_guard_never_lets_data_straddle_a_bank_boundary()
    {
        var rom = TestRom.Create(size: 0x100000);
        // Fill expanded space so the next free byte sits just before a bank boundary:
        // a 16-byte block placed there would straddle 0x88000 without the guard.
        for (int pc = 0x80000; pc < 0x87FF4; pc++) rom.Data[rom.HeaderOffset + pc] = 1;
        int snes = RatsWriter.Allocate(rom, new byte[16], avoidBankCross: true);
        int pc0 = Rom.SnesToPc(snes);
        Assert.Equal(pc0 >> 15, (pc0 + 15) >> 15);            // whole data span in one bank
        Assert.Equal(0x88000, pc0);                            // bumped to the next bank start
    }
}
