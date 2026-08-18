using Xunit;

namespace PipeDream.Tests;

/// <summary>Layer-2 object editing (CONTRACT §10). Layer 2 uses the same stream format as
/// layer 1 but a different destination plane, and the pointer's BANK is what selects object
/// mode versus background image — so the mode and the data are the same decision.</summary>
public class Layer2Tests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pdl2-" + Guid.NewGuid().ToString("N")[..8]);

    public Layer2Tests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    [Fact]
    public void level_modes_that_never_load_layer_2_objects_are_the_documented_set()
    {
        int[] ignore = [0x00, 0x0A, 0x0C, 0x0D, 0x0E, 0x11, 0x1E];
        for (int mode = 0; mode < 0x20; mode++)
            Assert.Equal(!ignore.Contains(mode), Rom.LoadsLayer2Objects(mode));
        Assert.Equal(Rom.LoadsLayer2Objects(0x01), Rom.LoadsLayer2Objects(0x21));   // masked to 5 bits
    }

    [RealRomFact]
    public void setting_the_layer_2_pointer_switches_a_background_level_to_object_mode()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        // 0x105's layer 2 is a background image (bank $FF) — asserted by the self-check too.
        Assert.True(rom.Layer2IsBackground(0x105));

        rom.SetLayer2Pointer(0x105, 0x0C8000);
        Assert.False(rom.Layer2IsBackground(0x105));
        Assert.Equal(0x0C8000, rom.Layer2Pointer(0x105));

        rom.SetLayer2Pointer(0x105, 0xFF0000);
        Assert.True(rom.Layer2IsBackground(0x105));
    }

    // Vanilla's object-mode layer-2 levels, pinned rather than searched for: 0x009 carries
    // real content (62 objects), and 0x0C4 is object mode with an EMPTY stream — editable
    // from the start, which makes it the natural level to try layer-2 editing on.
    private const int ContentLevel = 0x009;
    private const int EmptyLevel = 0x0C4;

    /// <summary>Cross-check of the mode set from the other direction: every level whose
    /// layer-2 POINTER is in object mode should also have a level MODE that reads it. All 26
    /// of vanilla's agree — a wrong ignore-set would show up here as contradictions.</summary>
    [RealRomFact]
    public void every_object_mode_level_has_a_mode_that_loads_layer_2()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        int objectMode = 0;
        for (int lvl = 0; lvl < Rom.LevelCount; lvl++)
        {
            if (rom.Layer2IsBackground(lvl)) continue;
            List<LevelObject>? l2;
            try { l2 = LevelParser.ParseLayer2(rom, lvl); } catch { continue; }
            if (l2 is null) continue;
            objectMode++;
            int mode = LevelParser.Parse(rom, lvl).Header.LevelMode;
            Assert.True(Rom.LoadsLayer2Objects(mode),
                        $"level {lvl:X3} has an object-mode layer 2 but mode {mode:X2} never loads it");
        }
        Assert.Equal(26, objectMode);
    }

    [RealRomFact]
    public void a_layer_2_stream_round_trips_through_encode_and_parse()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var objs = LevelParser.ParseLayer2(rom, ContentLevel)!;
        Assert.NotEmpty(objs);

        var lv = LevelParser.Parse(rom, ContentLevel);
        byte[] enc = LevelEncoder.Encode(lv, LevelEncoder.NormalizeStream(objs));
        var reparsed = LevelParser.ParseEncoded(rom, enc);

        // Every real object survives; NormalizeStream re-derives the screen jumps around them.
        Assert.Equal(objs.Where(o => !o.IsScreenJump).Count(),
                     reparsed.Count(o => !o.IsScreenJump));
        // The stream carries its own 5-byte header, which the game skips ($0583FB).
        Assert.Equal(lv.Header.ToBytes(), enc[..5]);
    }

    [RealRomFact]
    public void the_object_engine_renders_layer_2_into_its_own_plane()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var lv = LevelParser.Parse(rom, ContentLevel);
        byte[] enc = LevelEncoder.Encode(lv, LevelEncoder.NormalizeStream(LevelParser.ParseLayer2(rom, ContentLevel)!));

        // Same bytes, different destination plane. Rendering the layer-2 stream through the
        // layer-2 path must reproduce what the ROM's own layer-2 render produces.
        var direct = ObjectEngine.RenderLayer2(rom, lv.Header, ContentLevel);
        var asL2 = ObjectEngine.RenderEmulatedStream(rom, lv.Header, enc, 1);
        Assert.NotNull(direct);
        for (int y = 0; y < direct!.Height; y++)
            for (int x = 0; x < direct.Width; x++)
                if (direct.Get(x, y) != asL2.Get(x, y))
                    Assert.Fail($"layer-2 render differs at ({x},{y}): {direct.Get(x, y):X4} vs {asL2.Get(x, y):X4}");
    }

    [RealRomFact]
    public void an_object_mode_level_with_an_empty_stream_is_editable_from_blank()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        // 0x0C4 ships object mode with nothing in it, so the editor offers layer 2 directly
        // (no conversion) and anything placed there is the user's own.
        Assert.False(rom.Layer2IsBackground(EmptyLevel));
        Assert.Empty(LevelParser.ParseLayer2(rom, EmptyLevel)!);
        Assert.True(Rom.LoadsLayer2Objects(LevelParser.Parse(rom, EmptyLevel).Header.LevelMode));
    }

    [RealRomFact]
    public void an_edited_layer_2_survives_the_build_and_converts_the_pointer()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var baseRom = Rom.Load(p.BaseRomPath);
        Assert.True(baseRom.Layer2IsBackground(0x105));       // starts as a background image

        // The realistic conversion: a background level's mode (0x00 here) is one of the ones
        // that never loads layer-2 objects — that is WHY it uses a background image. So the
        // stream and a layer-2-capable level mode have to go together.
        Assert.False(Rom.LoadsLayer2Objects(LevelParser.Parse(baseRom, 0x105).Header.LevelMode));
        var hdr = LevelParser.Parse(baseRom, 0x105).Header with { LevelMode = 0x01 };
        var l2 = new List<LevelObject> { new(false, 0x14, 0, 0, 20, 0x1F, -1) };
        p.Data.Level(0x105).Header = Convert.ToHexString(hdr.ToBytes());
        p.Data.Level(0x105).Objects = LevelParser.Parse(baseRom, 0x105).Objects
            .Select(ProjectFile.ObjectDto.From).ToList();
        p.Data.Level(0x105).Layer2Objects = l2.Select(ProjectFile.ObjectDto.From).ToList();
        p.Save();

        var (status, outPath) = RomBuilder.Build(p);
        Assert.NotNull(outPath);
        var built = Rom.Load(outPath!);

        Assert.False(built.Layer2IsBackground(0x105));        // bank rewritten => object mode
        var readBack = LevelParser.ParseLayer2(built, 0x105);
        Assert.NotNull(readBack);
        Assert.Contains(readBack!, o => o.Number == 0x14 && o.Y == 20);
        // Mode 0x01 does load layer-2 objects, so there is nothing to warn about.
        Assert.DoesNotContain("never loads them", status);

        // An untouched neighbour keeps the base ROM's layer-2 pointer exactly.
        Assert.Equal(baseRom.Layer2Pointer(0x106), built.Layer2Pointer(0x106));
    }

    [RealRomFact]
    public void writing_layer_2_objects_for_a_mode_that_ignores_them_warns()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var baseRom = Rom.Load(p.BaseRomPath);
        var hdr = LevelParser.Parse(baseRom, 0x105).Header with { LevelMode = 0x00 };   // mode 0 ignores L2
        p.Data.Level(0x105).Header = Convert.ToHexString(hdr.ToBytes());
        p.Data.Level(0x105).Objects = new();
        p.Data.Level(0x105).Layer2Objects = new() { ProjectFile.ObjectDto.From(new LevelObject(false, 0x14, 0, 0, 20, 0x1F, -1)) };
        p.Save();

        var (status, outPath) = RomBuilder.Build(p);
        Assert.NotNull(outPath);
        Assert.Contains("never loads them", status);
    }
}
