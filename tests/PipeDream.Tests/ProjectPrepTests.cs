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

    [RealRomFact]
    public void adopt_base_reproduces_a_v1_pin_with_the_frozen_v1_stamps()
    {
        // Simulate a legacy project created by the v1 editor: v1-prepped base + v1 pin.
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        File.Copy(TestRom.RealRomPath, p.BaseRomPath, overwrite: true);
        Assert.Null(RomPrep.PrepInPlace(p.BaseRomPath, version: 1));
        byte[] v1 = File.ReadAllBytes(p.BaseRomPath);
        p.Data.BaseRom.Sha256 = RomHash.HeaderlessSha256(v1);
        p.Data.BaseRom.Size = v1.Length;
        p.Data.BaseRom.PrepVersion = 1;
        p.Save();

        File.Delete(p.BaseRomPath);                       // shared bare .pdp scenario
        var re = Project.Open(p.FilePath);
        Assert.Null(re.AdoptBase(TestRom.RealRomPath));   // raw vanilla → v1 stamps → v1 pin
        Assert.Null(re.ValidateBase());
        Assert.False(Rom.Load(re.BaseRomPath).HasLmGfxLoader);   // still a v1 image
    }

    [RealRomFact]
    public void upgrade_base_prep_moves_a_v1_project_to_the_current_version()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        File.Copy(TestRom.RealRomPath, p.BaseRomPath, overwrite: true);
        Assert.Null(RomPrep.PrepInPlace(p.BaseRomPath, version: 1));
        p.Data.BaseRom.Sha256 = RomHash.HeaderlessSha256File(p.BaseRomPath);
        p.Data.BaseRom.PrepVersion = 1;
        p.Save();

        Assert.Null(p.UpgradeBasePrep(TestRom.RealRomPath));
        Assert.Equal(RomPrep.Version, p.Data.BaseRom.PrepVersion);
        Assert.Null(p.ValidateBase());
        var rom = Rom.Load(p.BaseRomPath);
        Assert.True(rom.HasLmGfxLoader);
        Assert.True(RomPrep.IsPrepped(rom));

        // idempotent guard: a current-version project refuses to "upgrade"
        Assert.NotNull(p.UpgradeBasePrep(TestRom.RealRomPath));
    }

    [RealRomFact]
    public void build_writes_gfx_blobs_and_bypass_records_round_trip()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        // an imported ExGFX file (raw planar at the base's bpp = 3bpp) + a slot override
        var import = new byte[0x300];                              // 32 tiles of 3bpp
        for (int i = 0; i < import.Length; i++) import[i] = (byte)(i * 5 + 1);
        p.Data.Gfx["100"] = Convert.ToBase64String(import);
        p.Data.Level(0x105).GfxOverrides[7] = 0x100;               // FG1 ← ExGFX 0x100
        p.Data.Level(0x105).GfxOverrides[3] = 0x101;               // BG2 (VRAM-patch warning)
        p.Save();

        var (status, outPath) = RomBuilder.Build(p);
        Assert.NotNull(outPath);
        Assert.Contains("BG2/BG3", status);                        // warning surfaced

        var built = Rom.Load(outPath!);
        var rec = built.LmGfxBypass(0x105);
        Assert.NotNull(rec);
        Assert.Equal(0x100, rec![7] & 0xFFF);
        Assert.Equal(0x101, rec[3] & 0xFFF);
        Assert.NotEqual(0, rec[0] & 0x8000);

        byte[]? decoded = Gfx.Cached(built, 0x100);                // through SourceSnes + LZ2
        Assert.NotNull(decoded);
        Assert.Equal(0xC00, decoded!.Length);                      // zero-padded full file
        Assert.Equal(import, decoded.Take(import.Length).ToArray());
        Assert.All(decoded.Skip(import.Length), b => Assert.Equal(0, b));

        // deterministic: building twice yields byte-identical ROMs
        byte[] first = File.ReadAllBytes(outPath!);
        RomBuilder.Build(p);
        Assert.Equal(first, File.ReadAllBytes(outPath!));
    }

    [RealRomFact]
    public void build_rewrites_vanilla_gfx_pointers_for_forked_stock_files()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var baseRom = Rom.Load(p.BaseRomPath);
        byte[] fork = (byte[])Gfx.DecompressFile(baseRom, 2).Clone();
        fork[5] ^= 0x55;                                           // one edited byte
        p.Data.Gfx["002"] = Convert.ToBase64String(fork);
        p.Data.Level(0x105).Objects.Add(ProjectFile.ObjectDto.From(
            new LevelObject(false, 0x14, 0, 0, 0x18, 0x2F, -1)));
        p.Save();

        var (_, outPath) = RomBuilder.Build(p);
        Assert.NotNull(outPath);
        var built = Rom.Load(outPath!);
        Assert.Equal(fork, Gfx.DecompressFile(built, 2));          // vanilla pointer repointed
    }

    [RealRomFact]
    public void build_on_a_v1_base_warns_instead_of_writing_gfx()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        File.Copy(TestRom.RealRomPath, p.BaseRomPath, overwrite: true);
        Assert.Null(RomPrep.PrepInPlace(p.BaseRomPath, version: 1));
        p.Data.BaseRom.Sha256 = RomHash.HeaderlessSha256File(p.BaseRomPath);
        p.Data.BaseRom.PrepVersion = 1;
        p.Data.Gfx["100"] = Convert.ToBase64String(new byte[0x60]);
        p.Data.Level(0x105).GfxOverrides[7] = 0x100;
        p.Save();

        var (status, outPath) = RomBuilder.Build(p);
        Assert.NotNull(outPath);
        Assert.Contains("Upgrade base to prep v", status);
        Assert.Null(Rom.Load(outPath!).LmGfxBypass(0x105));        // nothing written
    }

    [RealRomFact]
    public void a_header_edit_survives_the_build_and_leaves_other_levels_alone()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var baseRom = Rom.Load(p.BaseRomPath);
        var edited = LevelParser.Parse(baseRom, 0x105).Header with { Tileset = 3, Music = 5, Time = 1 };
        p.Data.Level(0x105).Header = Convert.ToHexString(edited.ToBytes());
        p.Save();

        var (_, outPath) = RomBuilder.Build(p);
        Assert.NotNull(outPath);
        var built = Rom.Load(outPath!);
        Assert.Equal(edited, LevelParser.Parse(built, 0x105).Header);
        // an untouched neighbour keeps the base ROM's header byte for byte
        Assert.Equal(LevelParser.Parse(baseRom, 0x106).Header, LevelParser.Parse(built, 0x106).Header);
    }

    [RealRomFact]
    public void an_edited_screen_exit_survives_the_build()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var baseRom = Rom.Load(p.BaseRomPath);
        // keep the level's real objects, retarget its exit, and add a second one
        var objs = LevelParser.Parse(baseRom, 0x105).Objects
            .Where(o => !o.IsScreenExit)
            .Append(LevelObject.ScreenExit(7, 0xD4, water: false, secondary: true))
            .Append(LevelObject.ScreenExit(0x0C, 0xE2, water: true, secondary: false))
            .ToList();
        p.Data.Level(0x105).Objects = objs.Select(ProjectFile.ObjectDto.From).ToList();
        p.Save();

        var (_, outPath) = RomBuilder.Build(p);
        Assert.NotNull(outPath);
        // NormalizeStream re-sorts the stream and re-derives screen jumps, so this asserts
        // the Y field (which is what the handler indexes by) came through untouched.
        var exits = LevelParser.Parse(Rom.Load(outPath!), 0x105).Objects
            .Where(o => o.IsScreenExit).OrderBy(o => o.ExitScreen).ToList();
        Assert.Equal(2, exits.Count);
        Assert.Equal((7, 0xD4, false, true),
                     (exits[0].ExitScreen, exits[0].ExitDestination, exits[0].ExitIsWater, exits[0].ExitUsesSecondary));
        Assert.Equal((0x0C, 0xE2, true, false),
                     (exits[1].ExitScreen, exits[1].ExitDestination, exits[1].ExitIsWater, exits[1].ExitUsesSecondary));
    }

    [RealRomFact]
    public void an_edited_secondary_entrance_survives_the_build()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var baseRom = Rom.Load(p.BaseRomPath);
        var edited = baseRom.ReadSecondaryEntrance(0xD4) with
        { DestinationLevel = 0xC5, MarioX = 3, MarioY = 9, EntranceAction = 2 };
        p.Data.Entrances["0D4"] = Convert.ToHexString(edited.ToBytes());
        p.Save();

        var (_, outPath) = RomBuilder.Build(p);
        Assert.NotNull(outPath);
        var built = Rom.Load(outPath!);
        Assert.Equal(edited, built.ReadSecondaryEntrance(0xD4));
        // an untouched neighbour still matches the base ROM
        Assert.Equal(baseRom.ReadSecondaryEntrance(0xD5), built.ReadSecondaryEntrance(0xD5));
    }

    [RealRomFact]
    public void an_edited_main_entrance_survives_the_build()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var baseRom = Rom.Load(p.BaseRomPath);
        var edited = baseRom.ReadMainEntrance(0x105) with
        { MarioX = 4, MarioY = 6, EntranceAction = 5, Layer2Scroll = 2 };
        p.Data.Level(0x105).MainEntrance = Convert.ToHexString(edited.ToBytes());
        p.Save();

        var (_, outPath) = RomBuilder.Build(p);
        Assert.NotNull(outPath);
        var built = Rom.Load(outPath!);
        Assert.Equal(edited, built.ReadMainEntrance(0x105));
        Assert.Equal(baseRom.ReadMainEntrance(0x106), built.ReadMainEntrance(0x106));
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
