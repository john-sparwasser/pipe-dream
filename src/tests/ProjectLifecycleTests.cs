using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// The seam between a project ON DISK and a working ROM — open, prep, edit, build. The unit
/// tests either side of this were green while the editor was unusable: a project pinned to a
/// raw vanilla base refused every LM-backed feature ("save it in Lunar Magic once first") and
/// the upgrade action was gated off, so there was no way out of that state. Nothing tested
/// the path, only its ends.
///
/// These run without ImGui: everything here is Project/RomBuilder, which is where the
/// lifecycle actually lives.
/// </summary>
public class ProjectLifecycleTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pdlife-" + Guid.NewGuid().ToString("N")[..8]);

    public ProjectLifecycleTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    /// <summary>Re-create the state the editor got stuck in: a project.pdp on disk pinning an
    /// UNPREPPED vanilla base, as projects created before prep existed did.</summary>
    private Project StaleV0Project()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        File.Copy(TestRom.RealRomPath, p.BaseRomPath, overwrite: true);
        p.Data.BaseRom.Sha256 = RomHash.HeaderlessSha256File(p.BaseRomPath);
        p.Data.BaseRom.Size = (int)new FileInfo(p.BaseRomPath).Length;
        p.Data.BaseRom.PrepVersion = 0;
        p.Save();
        return Project.Open(p.FilePath);          // reopen from disk, like the editor does
    }

    [RealRomFact]
    public void opening_a_stale_project_leaves_it_ready_to_edit()
    {
        var p = StaleV0Project();
        // Before: the state the user reported — nothing can be allocated at all.
        Assert.Contains("Lunar Magic", Rom.Load(p.BaseRomPath).EnsureMap16Tiles(0x300));

        Assert.Null(p.PrepareBaseOnOpen(TestRom.RealRomPath));

        Assert.Equal(RomPrep.Version, p.Data.BaseRom.PrepVersion);
        Assert.Null(p.ValidateBase());            // the pin was updated with the swap
        var rom = Rom.Load(p.BaseRomPath);
        Assert.True(RomPrep.IsPrepped(rom));
        Assert.Null(rom.EnsureMap16Tiles(0x1100));   // ...including pages past 0x0F
    }

    /// <summary>Opening is idempotent: the second open must not swap the base again (which
    /// would re-pin, and make every open dirty the project).</summary>
    [RealRomFact]
    public void opening_an_up_to_date_project_changes_nothing()
    {
        var p = StaleV0Project();
        Assert.Null(p.PrepareBaseOnOpen(TestRom.RealRomPath));
        string pin = p.Data.BaseRom.Sha256;
        var stamp = File.GetLastWriteTimeUtc(p.BaseRomPath);

        Assert.Null(p.PrepareBaseOnOpen(TestRom.RealRomPath));
        Assert.Equal(pin, p.Data.BaseRom.Sha256);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(p.BaseRomPath));
    }

    /// <summary>A project whose base is a foreign/LM ROM must be left alone — prepping
    /// replaces base.smc with a prepped vanilla and would discard the hack.</summary>
    [RealRomFact]
    public void opening_a_project_on_a_foreign_base_does_not_touch_it()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var bytes = File.ReadAllBytes(p.BaseRomPath);
        bytes[^1] ^= 0xFF;                        // no longer verified vanilla
        File.WriteAllBytes(p.BaseRomPath, bytes);
        p.Data.BaseRom.PrepVersion = 0;
        p.Save();

        // null = "nothing to do" (not "done"): a foreign base is not a candidate at all, so
        // opening it is silent rather than nagging about a prep it must never receive.
        Assert.Null(p.PrepareBaseOnOpen(TestRom.RealRomPath));
        Assert.False(p.CanUpgradeBasePrep);
        Assert.Equal(0, p.Data.BaseRom.PrepVersion);
        Assert.Equal(bytes, File.ReadAllBytes(p.BaseRomPath));   // byte-identical
    }

    /// <summary>End to end on the feature that started this: open a stale project, edit a tile
    /// on a page past 0x0F, build, and find it in the built ROM. Every layer has its own test;
    /// this is the one that fails if they disagree.</summary>
    [RealRomFact]
    public void a_high_page_edit_survives_open_to_built_rom()
    {
        var p = StaleV0Project();
        Assert.Null(p.PrepareBaseOnOpen(TestRom.RealRomPath));

        // Edit tile 0x1005 the way the Map16 editor records one: by tile number, because the
        // def region relocates on every allocation.
        p.Data.Map16.TileCount = 0x1100;
        p.Data.Map16.Ext[0x1005.ToString("X3")] = "AABBCCDDEEFF0011";
        p.Save();

        var (status, outPath) = RomBuilder.Build(p);
        Assert.NotNull(outPath);
        Assert.True(File.Exists(outPath), status);

        var built = Rom.Load(outPath!);
        Assert.True(built.Map16TileCount >= 0x1100, $"built count 0x{built.Map16TileCount:X}");
        int fo = Map16.DefFileOffset(built, 0, 0x1005);
        Assert.True(fo > 0, "tile 0x1005 has no def in the built ROM");
        Assert.Equal("AABBCCDDEEFF0011", Convert.ToHexString(built.Data.AsSpan(fo, 8)));

        // And the in-game lookup can actually reach it: the built ROM's ladder has range 1.
        Assert.True(built.HasMap16Range(1));
        Assert.NotEqual(0, built.LmMap16Slot(1).Bank);
    }

    /// <summary>Building twice from the same project must produce identical bytes even across
    /// the allocation that a high page forces — a build that appends a fresh bank each time
    /// would break BPS export and every hash the project pins.</summary>
    [RealRomFact]
    public void building_a_high_page_project_twice_is_byte_identical()
    {
        var p = StaleV0Project();
        Assert.Null(p.PrepareBaseOnOpen(TestRom.RealRomPath));
        p.Data.Map16.TileCount = 0x1100;
        p.Data.Map16.Ext[0x1005.ToString("X3")] = "1122334455667788";
        p.Save();

        var (_, a) = RomBuilder.Build(p);
        byte[] first = File.ReadAllBytes(a!);
        var (_, b) = RomBuilder.Build(p);
        Assert.Equal(first, File.ReadAllBytes(b!));
    }
}
