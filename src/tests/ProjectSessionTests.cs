using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// The two directions between a project and a live ROM, tested without a window.
///
/// This code used to live inside the ImGui editor's LevelSession, written against its
/// EditorApp — so the save path could only run if a GUI existed, and a second front end had
/// to reimplement it. Both failure modes are invisible: an edit renders perfectly and is
/// silently absent from the .pdp, or a project opens and quietly renders without its edits.
/// </summary>
public class ProjectSessionTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pdsess-" + Guid.NewGuid().ToString("N")[..8]);

    public ProjectSessionTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    // ---- hydrate: project -> ROM ----

    [RealRomFact]
    public void hydrate_replays_gfx_imports_names_and_overrides_onto_a_fresh_rom()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var data = p.Data;
        data.Gfx["100"] = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        data.GfxNames["100"] = "my clouds";
        var lv = data.Level(0x105);
        lv.GfxOverrides[3] = 0x100;
        lv.Header = "21053" + "01D";                    // 5 header bytes as hex

        var rom = Rom.Load(p.BaseRomPath);
        Assert.Null(ProjectSession.Hydrate(rom, data));

        Assert.Equal([1, 2, 3, 4], rom.ImportedGfx[0x100]);
        Assert.Equal("my clouds", rom.ImportedGfxNames[0x100]);
        Assert.Equal(0x100, rom.GfxSlotOverrides[(0x105, 3)]);
        Assert.True(rom.LevelHeaderOverrides.ContainsKey(0x105));
    }

    /// <summary>Hydrate must run the SAME Map16 replay the build does, or the level you edit
    /// and the ROM you ship disagree about which tiles exist.</summary>
    [RealRomFact]
    public void hydrate_replays_map16_through_the_build_path()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        p.Data.Map16.TileCount = 0x500;
        p.Data.Map16.Ext[0x400.ToString("X3")] = "AABBCCDDEEFF0011";

        var rom = Rom.Load(p.BaseRomPath);
        Assert.Null(ProjectSession.Hydrate(rom, p.Data));

        Assert.True(rom.Map16TileCount >= 0x500);
        Assert.Equal("AABBCCDDEEFF0011",
                     Convert.ToHexString(rom.Data.AsSpan(Map16.DefFileOffset(rom, 0, 0x400), 8)));
    }

    // ---- stash: edit state -> project ----

    [RealRomFact]
    public void stash_records_objects_sprites_palette_and_the_roms_overrides()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var rom = Rom.Load(p.BaseRomPath);
        rom.GfxSlotOverrides[(0x105, 3)] = 0x101;
        rom.GfxSlotOverrides[(0x106, 3)] = 0x102;          // a DIFFERENT level's override
        rom.LevelHeaderOverrides[0x105] = [1, 2, 3, 4, 5];

        var st = new LevelEditState { Layer1 = [LevelObject.MakeDm16(0x100, 0, 2, 8)] };
        st.PaletteEdits[0x41] = 0x1234;
        st.Stash(p.Data, rom, 0x105);

        var s = p.Data.Level(0x105);
        Assert.Single(s.Objects);
        Assert.Equal(0x1234, s.Palette[0x41]);
        Assert.Equal("0102030405", s.Header);
        // Only THIS level's GFX overrides, or every level would inherit its neighbours'.
        Assert.Equal(0x101, s.GfxOverrides[3]);
        Assert.Single(s.GfxOverrides);
    }

    /// <summary>Layer 2 is recorded only once it differs from the base ROM's stream —
    /// otherwise merely visiting a level pins its unedited layer 2 into the project.</summary>
    [RealRomFact]
    public void an_unedited_layer_2_is_not_recorded()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var rom = Rom.Load(p.BaseRomPath);
        var baseL2 = new List<LevelObject> { LevelObject.MakeDm16(0x100, 0, 2, 8) };

        var st = new LevelEditState
        {
            Layer1 = [],
            BaseLayer2 = baseL2,
            Layer2 = [.. baseL2],                          // same content, different list
        };
        st.Stash(p.Data, rom, 0x105);
        Assert.Null(p.Data.Level(0x105).Layer2Objects);

        st.Layer2 = [LevelObject.MakeDm16(0x101, 0, 3, 8)];   // now it differs
        st.Stash(p.Data, rom, 0x105);
        Assert.NotNull(p.Data.Level(0x105).Layer2Objects);
    }

    /// <summary>An EMPTY layer-2 list still counts when the base had no stream at all: that
    /// is the background-image → object-mode conversion, and dropping it as "no edits" would
    /// silently undo the conversion on save.</summary>
    [RealRomFact]
    public void converting_a_background_level_to_an_empty_object_layer_is_recorded()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var rom = Rom.Load(p.BaseRomPath);

        var st = new LevelEditState { Layer1 = [], BaseLayer2 = null, Layer2 = [] };
        st.Stash(p.Data, rom, 0x105);

        Assert.NotNull(p.Data.Level(0x105).Layer2Objects);
        Assert.Empty(p.Data.Level(0x105).Layer2Objects!);
    }

    [RealRomFact]
    public void rom_wide_stash_keeps_names_only_for_imports_that_still_exist()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var rom = Rom.Load(p.BaseRomPath);
        rom.ImportedGfx[0x100] = [1, 2, 3];
        rom.ImportedGfxNames[0x100] = "kept";
        rom.ImportedGfxNames[0x200] = "orphan";           // named, but no blob

        LevelEditState.StashRomWide(p.Data, rom, tileset: 1);

        Assert.True(p.Data.Gfx.ContainsKey("100"));
        Assert.Equal("kept", p.Data.GfxNames["100"]);
        Assert.False(p.Data.GfxNames.ContainsKey("200"), "a removed import left a stray name");
    }

    /// <summary>The round trip: stash edits, hydrate them onto a fresh ROM, and find them.
    /// Each direction can be right on its own and still disagree about the format.</summary>
    [RealRomFact]
    public void edits_survive_a_stash_then_hydrate_round_trip()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var rom = Rom.Load(p.BaseRomPath);
        rom.ImportedGfx[0x100] = [9, 8, 7, 6];
        rom.GfxSlotOverrides[(0x105, 3)] = 0x100;
        rom.LevelHeaderOverrides[0x105] = [1, 2, 3, 4, 5];

        var st = new LevelEditState { Layer1 = [LevelObject.MakeDm16(0x100, 0, 2, 8)] };
        st.Stash(p.Data, rom, 0x105);
        LevelEditState.StashRomWide(p.Data, rom, tileset: 1);

        var fresh = Rom.Load(p.BaseRomPath);
        Assert.Null(ProjectSession.Hydrate(fresh, p.Data));

        Assert.Equal([9, 8, 7, 6], fresh.ImportedGfx[0x100]);
        Assert.Equal(0x100, fresh.GfxSlotOverrides[(0x105, 3)]);
        Assert.Equal([1, 2, 3, 4, 5], fresh.LevelHeaderOverrides[0x105]);
        Assert.Single(p.Data.Level(0x105).Objects);
    }
}
