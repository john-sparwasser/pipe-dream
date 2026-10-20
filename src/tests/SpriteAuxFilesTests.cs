using System.Text;
using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// Lunar Magic's custom sprite files (reference/PIXI_PORT.md, phase 5). The layout was checked
/// byte for byte against PIXI 1.43's output for two real sprite sets (Tempest: identical;
/// QLDC2023: identical but for two PIXI bugs, kept out of ours — see the doc); these pin it
/// with configs of our own.
/// </summary>
public class SpriteAuxFilesTests
{
    private static string Text(byte[] b) => Encoding.UTF8.GetString(b);

    /// <summary>Slot 05: an XY sprite with two own tiles, a display at X=8 and one with the extra
    /// bit showing a text label, two list entries. Slot 0A: one own tile, picked by extension byte.</summary>
    private static ProjectFile Data()
    {
        var a = new PixiConfig { ExtraBytes = 2, ExtraBytesSet = 1, Map16 = [.. Enumerable.Range(1, 16).Select(i => (byte)i)] };
        a.Displays.Add(new PixiConfig.Display
        {
            Description = "Walks.\nTurns at walls.", X = 8,
            Tiles = [new(0, 0, 0x301), new(-8, 16, 0x30), new(0, -16, 0x300, Translucent: true)],
        });
        a.Displays[0].Gfx[3] = (0x11, false);
        a.Displays[0].Gfx[2] = (0x06, true);
        a.Displays.Add(new PixiConfig.Display { ExtraBit = true, UseText = true, Text = "Thing" });
        a.Collection.Add(new PixiConfig.CollectionEntry { Name = "Walker", Bytes = [0xAB, 0xCD, 0xEF, 0, 0, 0, 0, 0, 0, 0, 0, 0] });
        a.Collection.Add(new PixiConfig.CollectionEntry { Name = "Walker (bit)", ExtraBit = true, Bytes = [0x12, 0x34, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] });
        var b = new PixiConfig { DisplayByExtraByte = true, ExtraBytes = 1, Map16 = [9, 9, 9, 9, 9, 9, 9, 9] };
        b.Displays.Add(new PixiConfig.Display { Description = "Two", X = 0, Y = 2, Tiles = [new(0, 0, 0x300)] });
        b.Collection.Add(new PixiConfig.CollectionEntry { Name = "Picker" });

        var d = new ProjectFile();
        d.Sprites["a"] = new ProjectFile.SpriteEntry { Config = a.ToJson() };
        d.Sprites["b"] = new ProjectFile.SpriteEntry { Config = b.ToJson() };
        d.SpriteSlots["0A"] = "b";
        d.SpriteSlots["05"] = "a";
        d.SpriteSlots["105:B0"] = "a";                                    // per level: not in LM's lists
        return d;
    }

    [Fact]
    public void the_tooltips_list_each_display_with_its_tiles_moved_to_the_s16()
    {
        Assert.Equal(
            "05 0820 Walks.\\nTurns at walls.\r\n" +
            "05 0822 0,0,401 -8,16,30 0,-16,8400\r\n" +                    // own tiles from 0x400, in slot order
            "05 0828 7F,7F,8006,11\r\n" +
            "05 0032 0,0,*Thing*\r\n" +
            "0A 30220 Two\r\n" +                                         // extra byte 0 is the record's byte 3
            "0A 30222 0,0,402\r\n",
            Text(SpriteAuxFiles.Make(Data())[".ssc"]));
    }

    [Fact]
    public void the_sprite_list_has_an_entry_per_collection_item_with_its_bytes()
    {
        var f = SpriteAuxFiles.Make(Data());
        Assert.Equal("05\tWalker\r\n\tWalker (bit)\r\n0A\tPicker\r\n", Text(f[".mwt"]));
        Assert.Equal([0x00,
                      0x79, 0x70, 0x05, 0xAB, 0xCD,                         // extra 2: two bytes
                      0x7D, 0x70, 0x05, 0x12,                               // extra 3: one
                      0x79, 0x70, 0x0A, 0x00,
                      0xFF], f[".mw2"]);
        Assert.Equal(0x10 * 0x800, f[".s16"].Length);
        Assert.Equal([.. Enumerable.Range(1, 16).Select(i => (byte)i), 9, 9, 9, 9, 9, 9, 9, 9, 0], f[".s16"][..25]);
    }

    [Fact]
    public void no_global_sprite_writes_nothing()
    {
        var d = Data();
        d.SpriteSlots.Remove("05");
        d.SpriteSlots.Remove("0A");
        Assert.Empty(SpriteAuxFiles.Make(d));
    }

    /// <summary>The build writes them beside the ROM, and a build with no sprites removes them.</summary>
    [AsarRomFact]
    public void the_build_writes_them_beside_the_rom()
    {
        string dir = Directory.CreateTempSubdirectory("pdaux-").FullName;
        try
        {
            var (status, rom) = SpriteInsertTests.Build(dir);
            Assert.True(rom is not null, status);
            string build = Path.Combine(dir, "proj", "build");
            string smc = Directory.GetFiles(build, "*.smc").Single();
            foreach (string ext in SpriteAuxFiles.Extensions) Assert.True(File.Exists(Path.ChangeExtension(smc, ext)), ext);

            var p = Project.Open(Directory.GetFiles(Path.Combine(dir, "proj"), "*.pdp").Single());
            p.Data.SpriteSlots.Clear();
            p.Save();
            Assert.NotNull(RomBuilder.Build(p).outPath);
            foreach (string ext in SpriteAuxFiles.Extensions) Assert.False(File.Exists(Path.ChangeExtension(smc, ext)), ext);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
