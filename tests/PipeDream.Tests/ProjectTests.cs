using Xunit;

namespace PipeDream.Tests;

/// <summary>Project lifecycle on disk: create, hash pinning, open, base recovery,
/// atomic save. Uses synthetic ROM images — no real SMW file needed.</summary>
public class ProjectTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pdtest-" + Guid.NewGuid().ToString("N")[..8]);
    private string SourceRom => Path.Combine(dir, "source.smc");

    public ProjectTests()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(SourceRom, TestRom.Image());
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void create_copies_base_and_pins_its_hash()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), SourceRom);
        Assert.True(File.Exists(p.BaseRomPath));
        Assert.True(File.Exists(p.FilePath));
        Assert.Equal(RomHash.HeaderlessSha256File(SourceRom), p.Data.BaseRom.Sha256);
        Assert.Equal("SUPER MARIOWORLD", p.Data.BaseRom.Title);
        Assert.Null(p.ValidateBase());
    }

    [Fact]
    public void open_round_trips_saved_state()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), SourceRom);
        p.Data.Level(0x105).Objects.Add(ProjectFile.ObjectDto.From(new LevelObject(false, 0x11, 0, 4, 8, 0x21, -1)));
        p.Data.Map16.ActsAs["205"] = 0x130;
        p.Save();

        var re = Project.Open(p.FilePath);
        Assert.Null(re.ValidateBase());
        Assert.Single(re.Data.LevelOrNull(0x105)!.Objects);
        Assert.Equal(0x130, re.Data.Map16.ActsAs["205"]);
    }

    [Fact]
    public void missing_base_is_reported_and_recoverable_only_with_the_matching_rom()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), SourceRom);
        File.Delete(p.BaseRomPath);                       // simulate a shared bare .pdp

        var re = Project.Open(p.FilePath);
        Assert.NotNull(re.ValidateBase());

        // A different ROM is rejected; the matching one restores the base copy.
        string wrong = Path.Combine(dir, "wrong.smc");
        var img = TestRom.Image(); img[0x100] ^= 0xFF;
        File.WriteAllBytes(wrong, img);
        Assert.NotNull(re.AdoptBase(wrong));
        Assert.Null(re.AdoptBase(SourceRom));
        Assert.Null(re.ValidateBase());
    }

    [Fact]
    public void mismatched_base_copy_fails_validation()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), SourceRom);
        var img = File.ReadAllBytes(p.BaseRomPath); img[0x200] ^= 1;
        File.WriteAllBytes(p.BaseRomPath, img);
        Assert.NotNull(Project.Open(p.FilePath).ValidateBase());
    }

    [Fact]
    public void newer_schema_versions_are_refused()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), SourceRom);
        File.WriteAllText(p.FilePath, p.Data.ToJson().Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 99"));
        Assert.Throws<InvalidDataException>(() => Project.Open(p.FilePath));
    }

    [Fact]
    public void save_leaves_no_temp_file_and_survives_repeat_saves()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), SourceRom);
        p.Save(); p.Save();
        Assert.False(File.Exists(p.FilePath + ".tmp"));
        Assert.NotNull(ProjectFile.FromJson(File.ReadAllText(p.FilePath)));
    }

    /// <summary>Dirty drives the window title's unsaved marker, and the debounce is what
    /// bounds how much a hard crash can cost. Tick must not save early, and Save must clear.</summary>
    [Fact]
    public void dirty_tracks_unsaved_edits_and_the_autosave_is_debounced()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), SourceRom);
        Assert.False(p.Dirty);

        p.MarkDirty();
        Assert.True(p.Dirty);
        p.Tick();                       // debounce has not elapsed
        Assert.True(p.Dirty);

        p.Save();
        Assert.False(p.Dirty);
    }
}
