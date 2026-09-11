using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// Lunar Magic's palette files, checked against files LM itself produced rather than against our
/// own reading of them — the whole point of the format is that the other tool can read it.
/// </summary>
public class PalFileTests
{
    /// <summary>A `.pal` LM exported from DogsOfWar. 768 bytes, and its first colours are the
    /// tell for the encoding: 5-bit channels shifted left 3, so 0x1D reads back as 0xE8 — NOT
    /// the bit-replicated 0xEF that <see cref="Palette.ToRgba"/> produces for display.</summary>
    private static string LmPalPath => ReferenceRoms.InProject("DogsOfWar", "107.pal");

    [Fact]
    public void lunar_magics_own_pal_file_decodes_to_five_bit_channels()
    {
        if (!File.Exists(LmPalPath)) return;                  // reference hack not present
        byte[] file = File.ReadAllBytes(LmPalPath);
        Assert.Equal(PalFile.LevelSize, file.Length);
        // Every byte is a 5-bit channel scaled by 8, so the low three bits are always clear.
        Assert.All(file, b => Assert.Equal(0, b & 7));
    }

    /// <summary>Round trip through our codec: a level's palette out and back in leaves the same
    /// colours, and re-importing what we just exported changes nothing.</summary>
    [RealRomFact]
    public void a_level_palette_round_trips_through_a_pal_file()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom);
        byte[] file = PalFile.ExportLevel(rom, 0x105);
        Assert.Equal(PalFile.LevelSize, file.Length);
        Assert.All(file, b => Assert.Equal(0, b & 7));

        var before = Palette.Load(rom, LevelParser.Parse(rom, 0x105).Header, 0x105).Bgr.ToArray();
        Assert.Null(PalFile.ImportLevel(rom, 0x105, file));
        var after = Palette.Load(rom, LevelParser.Parse(rom, 0x105).Header, 0x105).Bgr.ToArray();

        // Colour 0 of each row is the shared transparent slot, which the custom-palette blob
        // stores as zero by design (CONTRACT §7e) — the composer puts it back, so compare what
        // the composer says rather than the raw blob.
        for (int i = 0; i < 256; i++)
            if ((i & 15) != 0) Assert.Equal(before[i], after[i]);

        // ...and now that the level HAS a custom palette, exporting again is byte-identical.
        Assert.Equal(file, PalFile.ExportLevel(rom, 0x105));
    }

    /// <summary>A colour survives the trip: set one, export, and read it back out of the file at
    /// the right offset in LM's encoding.</summary>
    [RealRomFact]
    public void an_edited_colour_lands_at_its_own_offset_in_the_file()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom);
        var colors = new ushort[256];
        for (int i = 0; i < 256; i++) colors[i] = (ushort)(i * 7 & 0x7FFF);
        rom.WriteLmCustomPalette(0x105, colors[0], colors);

        byte[] file = PalFile.ExportLevel(rom, 0x105);
        for (int i = 1; i < 256; i++)
        {
            if ((i & 15) == 0) continue;                      // row colour 0: stored transparent
            // 0x64 is the global glint: the NMI rewrites it from $00B60C every four frames, so
            // Palette.Load applies it OVER a custom palette and the export shows the glint
            // rather than whatever the blob holds. Real, and the reason this loop has a hole.
            if (i == 0x64) continue;
            ushort c = colors[i];
            Assert.Equal((byte)((c & 0x1F) << 3), file[i * 3]);
            Assert.Equal((byte)((c >> 5 & 0x1F) << 3), file[i * 3 + 1]);
            Assert.Equal((byte)((c >> 10 & 0x1F) << 3), file[i * 3 + 2]);
        }
    }

    /// <summary>The shared palettes are a flat ROM range — LM's `smw.pal` IS `$00B0A0` onwards,
    /// measured against a file LM exported. So export is a copy, and import puts it back.</summary>
    [RealRomFact]
    public void shared_palettes_are_the_rom_range_lunar_magic_copies()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom);
        byte[] file = PalFile.ExportShared(rom);
        Assert.Equal(PalFile.SharedSize, file.Length);
        Assert.Equal(rom.Data.AsSpan(rom.FileOffset(PalFile.SharedSnes), PalFile.SharedSize).ToArray(), file);

        file[0] ^= 0xFF;
        Assert.Null(PalFile.ImportShared(rom, file));
        Assert.Equal(file[0], rom.Data[rom.FileOffset(PalFile.SharedSnes)]);
        Assert.NotNull(PalFile.ImportShared(rom, new byte[10]));     // and a wrong size is refused
    }
}
