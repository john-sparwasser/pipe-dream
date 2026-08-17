using Xunit;

namespace PipeDream.Tests;

/// <summary>Project × RomPrep integration: creation preps verified-vanilla bases and pins
/// the PREPPED hash; AdoptBase reproduces a prepped base from any raw vanilla copy.
/// Real-ROM-gated — prep's hash gate requires the actual vanilla image.</summary>
public class ProjectPrepTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pdprep-" + Guid.NewGuid().ToString("N")[..8]);

    public ProjectPrepTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [RealRomFact]
    public void create_on_vanilla_preps_the_base_and_pins_the_prepped_hash()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        Assert.Equal(RomPrep.Version, p.Data.BaseRom.PrepVersion);
        Assert.NotEqual(RomHash.VanillaUsSha256, p.Data.BaseRom.Sha256);   // pin = prepped image
        Assert.Equal(RomHash.HeaderlessSha256File(p.BaseRomPath), p.Data.BaseRom.Sha256);
        Assert.Null(p.ValidateBase());
        var rom = Rom.Load(p.BaseRomPath);
        Assert.True(rom.HasDm16Hijack);
        Assert.True(rom.HasLmPaletteHook);
        Assert.True(rom.LmActsAsBase > 0);
        Assert.True(rom.LmSpriteBankTable >= 0);
        Assert.NotEqual(0, rom.LmMap16Defs.Bank);
    }

    [RealRomFact]
    public void adopt_base_reproduces_a_prepped_base_from_raw_vanilla()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        File.Delete(p.BaseRomPath);                       // shared bare .pdp scenario
        var re = Project.Open(p.FilePath);
        Assert.NotNull(re.ValidateBase());
        Assert.Null(re.AdoptBase(TestRom.RealRomPath));   // raw vanilla → deterministic re-prep
        Assert.Null(re.ValidateBase());
    }

    [RealRomFact]
    public void adopt_base_rejects_projects_prepped_by_a_newer_editor()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        p.Data.BaseRom.PrepVersion = RomPrep.Version + 1;
        p.Save();
        File.Delete(p.BaseRomPath);
        var re = Project.Open(p.FilePath);
        Assert.Contains("newer editor", re.AdoptBase(TestRom.RealRomPath));
    }

    [RealRomFact]
    public void exported_bps_applies_to_a_stock_vanilla_rom()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        p.Data.Level(0x105).Objects.Add(ProjectFile.ObjectDto.From(
            new LevelObject(false, 0x14, 0, 0, 0x18, 0x2F, -1)));
        p.Save();

        var (status, bpsPath) = RomBuilder.ExportBps(p, TestRom.RealRomPath);
        Assert.NotNull(bpsPath);
        Assert.Contains("stock vanilla", status);

        byte[] vanilla = RomHash.HeaderlessSpan(File.ReadAllBytes(TestRom.RealRomPath)).ToArray();
        byte[] built = RomHash.HeaderlessSpan(
            File.ReadAllBytes(Path.Combine(p.Folder, "build", p.Name + ".smc"))).ToArray();
        Assert.Equal(built, BpsApplier.Apply(vanilla, File.ReadAllBytes(bpsPath!)));
    }

    [Fact]
    public void bps_export_without_a_vanilla_source_diffs_the_project_base()
    {
        string src = Path.Combine(dir, "lmbase.smc");
        File.WriteAllBytes(src, TestRom.Image(dm16: true));
        var p = Project.Create(Path.Combine(dir, "projb"), src);
        var (status, bpsPath) = RomBuilder.ExportBps(p, vanillaRomPath: null);
        Assert.NotNull(bpsPath);
        Assert.Contains("project's base", status);
        byte[] baseRom = RomHash.HeaderlessSpan(File.ReadAllBytes(p.BaseRomPath)).ToArray();
        byte[] built = RomHash.HeaderlessSpan(
            File.ReadAllBytes(Path.Combine(p.Folder, "build", p.Name + ".smc"))).ToArray();
        Assert.Equal(built, BpsApplier.Apply(baseRom, File.ReadAllBytes(bpsPath!)));
    }

    [Fact]
    public void create_on_a_non_vanilla_base_stays_unprepped()
    {
        string src = Path.Combine(dir, "lmish.smc");
        File.WriteAllBytes(src, TestRom.Image(dm16: true));   // not vanilla-hashed
        var p = Project.Create(Path.Combine(dir, "proj2"), src);
        Assert.Equal(0, p.Data.BaseRom.PrepVersion);
        Assert.Equal(RomHash.HeaderlessSha256File(src), p.Data.BaseRom.Sha256);
    }
}
