using Xunit;

namespace PipeDream.Tests;

public class ProjectFileTests
{
    private static ProjectFile Sample()
    {
        var p = new ProjectFile
        {
            BaseRom = new ProjectFile.BaseRomInfo { Sha256 = "abc123", Size = 524800, Title = "SUPER MARIOWORLD" },
        };
        p.Map16.TileCount = 0x400;
        p.Map16.Slots["0D8000"] = "0011223344556677";
        p.Map16.Ext["205"] = "AABBCCDDEEFF0011";
        p.Map16.ActsAs["205"] = 0x130;
        var lvl = p.Level(0x105);
        lvl.Objects.Add(ProjectFile.ObjectDto.From(
            new LevelObject(true, 0x11, 3, 4, 5, 0x21, -1)));
        lvl.Objects.Add(ProjectFile.ObjectDto.From(
            LevelObject.MakeDm16(0x2A5, screen: 1, xNib: 2, y: 3, w: 20, h: 4)));
        lvl.SpriteMemory = 0x08;
        lvl.Buoyancy = 1;
        lvl.Sprites.Add(ProjectFile.SpriteDto.From(
            new Sprite(2, 5, 7, 1, 0x35, new byte[] { 1, 2 })));
        lvl.Palette[0x42] = 0x7FFF;
        lvl.GfxOverrides[7] = 0x113;
        p.Gfx["100"] = Convert.ToBase64String(new byte[] { 0x80, 0x40, 0x20 });
        p.Gfx["014"] = Convert.ToBase64String(new byte[] { 0x01, 0x02 });   // forked STOCK id ("014" hex key)
        return p;
    }

    [Fact]
    public void json_round_trip_preserves_every_field()
    {
        var a = Sample();
        var b = ProjectFile.FromJson(a.ToJson());

        Assert.Equal(a.SchemaVersion, b.SchemaVersion);
        Assert.Equal(a.BaseRom.Sha256, b.BaseRom.Sha256);
        Assert.Equal(a.BaseRom.Size, b.BaseRom.Size);
        Assert.Equal(a.BaseRom.Title, b.BaseRom.Title);
        Assert.Equal(a.Map16.TileCount, b.Map16.TileCount);
        Assert.Equal(a.Map16.Slots, b.Map16.Slots);
        Assert.Equal(a.Map16.Ext, b.Map16.Ext);
        Assert.Equal(a.Map16.ActsAs, b.Map16.ActsAs);
        Assert.Equal(a.Gfx, b.Gfx);

        var la = a.LevelOrNull(0x105)!;
        var lb = b.LevelOrNull(0x105)!;
        Assert.Equal(la.SpriteMemory, lb.SpriteMemory);
        Assert.Equal(la.Buoyancy, lb.Buoyancy);
        Assert.Equal(la.Palette, lb.Palette);
        Assert.Equal(la.GfxOverrides, lb.GfxOverrides);
        Assert.Equal(la.Objects.Count, lb.Objects.Count);
        for (int i = 0; i < la.Objects.Count; i++)
        {
            var oa = la.Objects[i].ToLevelObject();
            var ob = lb.Objects[i].ToLevelObject();
            Assert.Equal(oa.NewScreen, ob.NewScreen);
            Assert.Equal(oa.Number, ob.Number);
            Assert.Equal(oa.Screen, ob.Screen);
            Assert.Equal(oa.XNibble, ob.XNibble);
            Assert.Equal(oa.Y, ob.Y);
            Assert.Equal(oa.Byte3, ob.Byte3);
            Assert.Equal(oa.ExtraByte, ob.ExtraByte);
            Assert.Equal(oa.Dm16Tile, ob.Dm16Tile);
            Assert.Equal(oa.Dm16Page, ob.Dm16Page);
            Assert.Equal(oa.Dm16ExtX, ob.Dm16ExtX);
            Assert.Equal(oa.Dm16ExtH, ob.Dm16ExtH);
        }
        var sa = la.Sprites[0].ToSprite();
        var sb = lb.Sprites[0].ToSprite();
        Assert.Equal(sa.Screen, sb.Screen);
        Assert.Equal(sa.XNibble, sb.XNibble);
        Assert.Equal(sa.Y, sb.Y);
        Assert.Equal(sa.Extra, sb.Extra);
        Assert.Equal(sa.Number, sb.Number);
        Assert.Equal(sa.ExtraBytes, sb.ExtraBytes);
    }

    [Fact]
    public void dto_round_trip_reconstructs_dm16_geometry()
    {
        var o = LevelObject.MakeDm16(0x2A5, screen: 1, xNib: 2, y: 3, w: 20, h: 4);
        var back = ProjectFile.ObjectDto.From(o).ToLevelObject();
        Assert.Equal(o.Dm16Size(), back.Dm16Size());
        Assert.Equal(o.IsDm16, back.IsDm16);
        Assert.Equal(o.Dm16Tile, back.Dm16Tile);
    }

    [Fact]
    public void level_accessor_creates_once_and_keys_by_hex()
    {
        var p = new ProjectFile();
        var a = p.Level(0x105);
        Assert.Same(a, p.Level(0x105));
        Assert.True(p.Levels.ContainsKey("105"));
        Assert.Null(p.LevelOrNull(0x106));
    }
}
