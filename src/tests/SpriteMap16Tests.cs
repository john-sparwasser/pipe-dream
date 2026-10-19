using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// Custom sprites drawn as Lunar Magic draws them (reference/PIXI_PORT.md, phase 3): a config's
/// display is Map16 tiles at pixel offsets, from Lunar Magic's own sprite Map16 below 0x300 and
/// the sprite's own from there, picked by the placement's extra bit and position or extra byte.
/// </summary>
public class SpriteMap16Tests
{
    private static PixiConfig.Display Showing(params int[] tiles) =>
        new() { Tiles = [.. tiles.Select((n, i) => new PixiConfig.Tile(i * 16, -i * 16, n))] };

    [Fact]
    public void lunar_magics_tiles_are_its_sprite_tiles_in_their_palettes()
    {
        // 030 is LM's green shell facing left: SP2's 8C/8D/9C/9D, X-flipped, palette 5.
        var oam = SpriteMap16.Oam(Showing(0x030), [], 100, 200);
        Assert.Equal([(100, 200, 0x8D), (100, 208, 0x9D), (108, 200, 0x8C), (108, 208, 0x9C)],
                     oam.Select(o => (o.X, o.Y, o.Tile)));
        Assert.All(oam, o => Assert.Equal(0x40 | 5 << 1, o.Attr));
        Assert.All(oam, o => Assert.False(o.Big));
        // 080 is the green Yoshi's head, from GFX33, which sprite Map16 numbers from 0x200.
        Assert.Equal(0x281, SpriteMap16.Oam(Showing(0x080), [], 0, 0)[0].Tile);
    }

    [Fact]
    public void a_sprites_own_map16_is_its_tiles_from_300()
    {
        byte[] own = [0x01, 0x0A, 0x02, 0x0A, 0x03, 0x0A, 0x04, 0x0A];   // palette 2, second page
        var oam = SpriteMap16.Oam(Showing(0x300, 0x301), own, 0, 0);
        Assert.Equal([0x201, 0x202, 0x203, 0x204], oam.Take(4).Select(o => o.Tile));
        Assert.All(oam.Take(4), o => Assert.Equal(2 << 1, o.Attr));
        // 301 is past what the sprite brought, so Lunar Magic's own 301 shows, at the second tile's offset.
        Assert.Equal([(16, -16, 0xE0), (16, -8, 0xF0), (24, -16, 0xE1), (24, -8, 0xF1)],
                     oam.Skip(4).Select(o => (o.X, o.Y, o.Tile)));
    }

    [Fact]
    public void a_placement_shows_the_display_for_its_extra_bit_and_position()
    {
        var c = new PixiConfig();
        var plain = Showing(0x030); plain.X = 0; plain.Y = 0;
        var right = Showing(0x031); right.X = 8; right.Y = 0;
        var set = Showing(0x032); set.ExtraBit = true;
        c.Displays.AddRange([plain, right, set]);
        Assert.Same(right, SpriteMap16.Pick(c, false, null, 0x18, 0x20));
        Assert.Same(plain, SpriteMap16.Pick(c, false, null, 0x13, 0x05));   // no display for 3,5: the first
        Assert.Same(set, SpriteMap16.Pick(c, true, null, 0x18, 0x20));

        c.Displays.RemoveAt(2);
        Assert.Same(plain, SpriteMap16.Pick(c, true, null, 0, 0));           // none for the extra bit: the first
        c.Displays[0].UseText = true;
        Assert.Null(SpriteMap16.Pick(c, false, null, 0, 0));                 // text: the box stands in
    }

    [Fact]
    public void an_extension_byte_display_is_picked_by_that_bytes_value()
    {
        var c = new PixiConfig { DisplayByExtraByte = true };
        var one = Showing(0x030); one.X = 1; one.Y = 4;
        var two = Showing(0x031); two.X = 1; two.Y = 7;
        c.Displays.AddRange([one, two]);
        Assert.Same(two, SpriteMap16.Pick(c, false, [0, 7], 0, 0));
        Assert.Same(one, SpriteMap16.Pick(c, false, [0, 4], 0, 0));
        Assert.Same(one, SpriteMap16.Pick(c, false, null, 0, 0));
    }

    [Fact]
    public void the_table_is_found_in_lunar_magic_by_its_first_tiles()
    {
        byte[] head = [0x00, 0x00, 0x10, 0x00, 0x01, 0x00, 0x11, 0x00, 0x00, 0x04, 0x10, 0x04, 0x01, 0x04, 0x11, 0x04, 0x87, 0x4C];
        var exe = new byte[0x3000];
        Random.Shared.NextBytes(exe.AsSpan(0, 0x100));
        head.CopyTo(exe, 0x123);
        var table = SpriteMap16.FindInLunarMagic(exe)!;
        Assert.Equal(SpriteMap16.Count * 8, table.Length);
        Assert.Equal(head, table[..head.Length]);
        Assert.Null(SpriteMap16.FindInLunarMagic(exe[..0x1000]));            // cut short
    }

    /// <summary>In the level, a placement of a sprite the project slots draws its display; one the
    /// project has no sprite for keeps the box, and the catalog's entries get thumbnails.</summary>
    [Fact]
    public void a_placed_custom_sprite_draws_its_display_in_the_level()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        var c = new PixiConfig { Type = 1 };
        var d = Showing(0x030, 0x031);
        c.Displays.Add(d);
        c.Collection.Add(new PixiConfig.CollectionEntry { Name = "Shells" });
        s.NewCustomSprite("shells", "sprite", out _);
        s.SetCustomSpriteConfig("shells", c.ToJson());
        s.SetCustomSpriteSlots("shells", "1A");
        s.ShowLevel(0x105);

        Assert.NotNull(s.SpriteCatalog().Items.Single(i => i.Extra >= 2).Thumb);
        Assert.True(s.Sprites!.Place(0x1A, 20, 10, 2, null));
        Assert.True(s.Sprites.Place(0x1B, 24, 10, 2, null));
        s.RefreshSprites();
        int n = s.Sprites.Sprites.Sprites.Count;
        Assert.Equal((320, 144, 352, 176), s.Sprites.Overlay!.PixelBounds(n - 2));
        Assert.Null(s.Sprites.Overlay.PixelBounds(n - 1));

        d.Tiles.RemoveAt(1);                                                 // an edited config redraws
        s.SetCustomSpriteConfig("shells", c.ToJson());
        Assert.Equal((320, 160, 336, 176), s.Sprites.Overlay!.PixelBounds(n - 2));
    }
}
