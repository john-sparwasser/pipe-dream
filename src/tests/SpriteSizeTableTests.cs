using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// Lunar Magic's sprite size table (help: "Custom Sprite List Sizes"): per (extra bits, number)
/// record lengths, registered at $0EF30C with 0x42 at $0EF30F, read by LM's level engine and
/// by PIXI. We read it where LM registers it and author it the same way.
/// </summary>
public class SpriteSizeTableTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pdsz-" + Guid.NewGuid().ToString("N")[..8]);
    public SpriteSizeTableTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    /// <summary>The registration and the older signature scan name the same table on the
    /// reference hacks — so the registration is the authority, and the scan is the fallback.</summary>
    [Fact]
    public void reference_hacks_register_their_table_at_lms_documented_address()
    {
        foreach (var path in new[] { ReferenceRoms.InProject("DogsOfWar", "dogs_of_war.smc"), ReferenceRoms.ShaoBase })
        {
            if (!File.Exists(path)) continue;
            var rom = Rom.Load(path);
            Assert.Equal(0x42, rom.ReadByte(Rom.LmSpriteSizeFlag));
            Assert.Equal(rom.ReadValue(Rom.LmSpriteSizePtr, 3), rom.LmSpriteSizeBase);
            Assert.True(rom.ReadValue(Rom.LmSpriteSizePtr, 3) > 0x108000);
        }
    }

    /// <summary>A base with no table gets one the moment a sprite needs a size: 0x400 bytes of 3
    /// in a RATS block, registered LM's way, with that entry set — and a placed sprite of that
    /// number carries the bytes the table says it has.</summary>
    [RealRomFact]
    public void authoring_a_size_installs_the_table_lms_way()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom, 10);
        Assert.Equal(-1, rom.LmSpriteSizeBase);
        Assert.Equal(3, rom.SpriteEntrySize(0, 0x0F));

        rom.SetSpriteEntrySize(1, 0x0F, 5);
        int b = rom.LmSpriteSizeBase;
        Assert.True(b > 0);
        Assert.Equal(0x42, rom.ReadByte(Rom.LmSpriteSizeFlag));
        Assert.Equal(b, rom.ReadValue(Rom.LmSpriteSizePtr, 3));
        Assert.Equal("STAR", System.Text.Encoding.ASCII.GetString(rom.Data, rom.FileOffset(b) - 8, 4));
        Assert.Equal(5, rom.SpriteEntrySize(1, 0x0F));
        Assert.Equal(3, rom.SpriteEntrySize(0, 0x0F));                 // other extra bits untouched
        Assert.All(Enumerable.Range(0, 0x400).Where(i => i != 0x10F), i => Assert.Equal(3, rom.ReadByte(b + i)));

        var sd = SpriteData.Parse(rom, 0x105);
        var edit = new Services.SpriteEdit(sd, null, false) { EntrySize = rom.SpriteEntrySize };
        rom.SetSpriteEntrySize(0, 0x0F, 4);
        Assert.True(edit.Place(0x0F, 20, 10));
        Assert.Equal(1, sd.Sprites[^1].ExtraBytes!.Length);
        Assert.Equal(sd, SpriteData.Parse(rom, 0x105) is var _ ? sd : sd);
    }

    /// <summary>End to end through the project: a sprite given extra bytes in the editor is
    /// written with them, the built ROM's table says how long its record is, and the parse of
    /// the built ROM (which reads that table) gets the bytes back. Vanilla base: no table before.</summary>
    [RealRomFact]
    public void built_rom_carries_the_table_and_the_bytes()
    {
        var p = Project.Create(Path.Combine(dir, "proj"), TestRom.RealRomPath);
        var baseRom = Rom.Load(p.BaseRomPath);
        var sd = SpriteData.Parse(baseRom, 0x105);
        var edit = new Services.SpriteEdit(sd, null, false);
        Assert.True(edit.Place(0x0F, 20, 10));
        Assert.True(edit.SetData(sd.Sprites.Count - 1, 0x0F, 2, [0xAB, 0xCD]));
        var st = p.Data.Level(0x105);
        st.Objects = LevelParser.Parse(baseRom, 0x105).Objects.Select(ProjectFile.ObjectDto.From).ToList();
        st.SpriteMemory = sd.SpriteMemory; st.Buoyancy = sd.Buoyancy;
        st.Sprites = sd.Sprites.Select(ProjectFile.SpriteDto.From).ToList();
        p.Save();

        var (status, outPath) = RomBuilder.Build(p);
        Assert.NotNull(outPath);
        var built = Rom.Load(outPath!);
        Assert.Equal(5, built.SpriteEntrySize(2, 0x0F));
        var back = SpriteData.Parse(built, 0x105);
        var s = back.Sprites.Single(x => x.Number == 0x0F && x.Extra == 2);
        Assert.Equal([0xAB, 0xCD], s.ExtraBytes);
        Assert.DoesNotContain("differing", status);
    }

    /// <summary>The dialog's parse: hex number, extra bits 0-3, up to 12 hex extra bytes.</summary>
    [Fact]
    public void sprite_data_fields_parse_as_lm_writes_them()
    {
        Assert.Equal((0x0F, 2, new byte[] { 0x00, 0x1F }), Ui.SpriteDataWindow.Parse("0F", "2", "00 1f"));
        Assert.Equal((0x0F, 0, (byte[]?)null), Ui.SpriteDataWindow.Parse("F", "0", ""));
        Assert.Null(Ui.SpriteDataWindow.Parse("0F", "4", ""));
        Assert.Null(Ui.SpriteDataWindow.Parse("0F", "0", "zz"));
        Assert.Null(Ui.SpriteDataWindow.Parse("0F", "0", string.Join(' ', Enumerable.Repeat("00", 13))));
    }

    /// <summary>A sprite's band survives the project file — it is part of where the sprite is.</summary>
    [Fact]
    public void a_sprites_band_round_trips_through_the_project_dto()
    {
        var s = new Sprite(1, 4, 8, 0, 0x0F, null, Band: 3);
        Assert.Equal(s, ProjectFile.SpriteDto.From(s).ToSprite());
    }
}
