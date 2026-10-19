using Avalonia.Controls;
using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// Custom sprites, phase 1 (reference/PIXI_PORT.md): PIXI's JSON and .cfg read into one model and
/// written back as JSON PIXI reads, a PIXI folder imported into the project, and the session's
/// library and slot operations. No insertion yet.
/// </summary>
public class PixiTests
{
    /// <summary>A config in PIXI's JSON shape, with something in every part.</summary>
    private const string Json = """
        {
          "$1656": { "Object Clipping": 3, "Can be jumped on": true, "Dies when jumped on": false,
                     "Hop in/kick shell": false, "Disappears in cloud of smoke": true },
          "$1662": { "Sprite Clipping": 20, "Use shell as death frame": false, "Fall straight down when killed": true },
          "$166E": { "Use second graphics page": true, "Palette": 5, "Disable fireball killing": false,
                     "Disable cape killing": false, "Disable water splash": true, "Don't interact with Layer 2": false },
          "$167A": { "Don't disable cliping when starkilled": true, "Invincible to star/cape/fire/bounce blk.": false,
                     "Process when off screen": true, "Don't change into shell when stunned": false,
                     "Can't be kicked like shell": false, "Process interaction with Mario every frame": false,
                     "Gives power-up when eaten by yoshi": false, "Don't use default interaction with Mario": true },
          "$1686": { "Inedible": true, "Stay in Yoshi's mouth": false, "Weird ground behaviour": false,
                     "Don't interact with other sprites": false, "Don't change direction if touched": false,
                     "Don't turn into coin when goal passed": false, "Spawn a new sprite": false, "Don't interact with objects": false },
          "$190F": { "Make platform passable from below": false, "Don't erase when goal passed": false,
                     "Can't be killed by sliding": false, "Takes 5 fireballs to kill": false,
                     "Can be jumped on with upwards Y speed": false, "Death frame two tiles high": false,
                     "Don't turn into a coin with silver POW": false, "Don't get stuck in walls (carryable sprites)": true },
          "AsmFile": "thing.asm",
          "ActLike": 54,
          "Type": 1,
          "Extra Property Byte 1": 1,
          "Extra Property Byte 2": 128,
          "Additional Byte Count (extra bit clear)": 2,
          "Additional Byte Count (extra bit set)": 4,
          "Map16": "AQIDBAUGBwg=",
          "Displays": [
            { "X": 0, "Y": 1, "Description": "Thing", "ExtraBit": false,
              "Tiles": [ { "X offset": -8, "Y offset": 0, "map16 tile": 768 }, { "X offset": 8, "Y offset": 0, "map16 tile": 33561 } ],
              "GFXInfo": { "1": { "Value": 18, "Separate": true } }, "DisplayText": "", "UseText": false },
            { "X": 0, "Y": 0, "Description": "Thing, set", "ExtraBit": true, "Tiles": [],
              "GFXInfo": {}, "DisplayText": "THING", "UseText": true }
          ],
          "Collection": [ { "Name": "Thing", "ExtraBit": false, "Extra Property Byte 1": 7, "Extra Property Byte 2": 9 } ],
          "DisplayType": "XY"
        }
        """;

    [Fact]
    public void a_json_config_reads_every_part()
    {
        var c = PixiConfig.Parse(Json);
        Assert.Equal((1, 0x36, "thing.asm"), (c.Type, c.ActLike, c.AsmFile));
        Assert.Equal([0x93, 0x94, 0x4B, 0x85, 0x01, 0x80], c.Tweak);
        Assert.Equal((1, 0x80, 2, 4), (c.Prop1, c.Prop2, c.ExtraBytes, c.ExtraBytesSet));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], c.Map16);
        Assert.Equal(2, c.Displays.Count);
        var d = c.Displays[0];
        Assert.Equal((0, 1, "Thing", false), (d.X, d.Y, d.Description, d.ExtraBit));
        // Bit 15 of a tile number is the older spelling of Translucent.
        Assert.Equal([new(-8, 0, 0x300), new(8, 0, 0x319, true)], d.Tiles);
        Assert.Equal((18, true), d.Gfx[1]);
        Assert.Null(d.Gfx[0]);
        Assert.True(c.Displays[1].UseText);
        Assert.Equal("THING", c.Displays[1].Text);
        Assert.Equal("Thing", c.Collection[0].Name);
        Assert.Equal([7, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], c.Collection[0].Bytes);   // missing bytes are 0, as PIXI
    }

    [Fact]
    public void written_json_reads_back_the_same()
    {
        var c = PixiConfig.Parse(Json);
        string written = c.ToJson();
        var back = PixiConfig.Parse(written);
        Assert.Equal(written, back.ToJson());
        Assert.Equal(c.Tweak, back.Tweak);
        Assert.Equal(c.Displays[0].Tiles, back.Displays[0].Tiles);
        // PIXI's names reach the file unescaped, apostrophes and all, since PIXI reads them by name.
        Assert.Contains("\"Don't interact with Layer 2\": false", written);
        Assert.Contains("\"Translucent\": true", written);
    }

    [Fact]
    public void the_extension_byte_display_type_reads_index_and_value()
    {
        string json = Json.Replace("\"DisplayType\": \"XY\"", "\"DisplayType\": \"ExByte\"")
                          .Replace("\"X\": 0, \"Y\": 1", "\"Index\": 1, \"Value\": 66")
                          .Replace("\"X\": 0, \"Y\": 0", "\"Index\": 1, \"Value\": 0");
        var c = PixiConfig.Parse(json);
        Assert.True(c.DisplayByExtraByte);
        Assert.Equal((1, 66), (c.Displays[0].X, c.Displays[0].Y));
        Assert.Contains("\"Index\": 1", c.ToJson());
        Assert.Contains("unknown DisplayType", Assert.Throws<FormatException>(() => PixiConfig.Parse(Json.Replace("\"XY\"", "\"ZZ\""))).Message);
    }

    [Fact]
    public void a_config_missing_what_pixi_requires_is_refused_by_name()
    {
        var e = Assert.Throws<FormatException>(() => PixiConfig.Parse(Json.Replace("\"ActLike\": 54,", "")));
        Assert.Contains("\"ActLike\"", e.Message);
        Assert.Contains("not valid JSON", Assert.Throws<FormatException>(() => PixiConfig.Parse("{")).Message);
        // A tweak (type 0) has no code, so PIXI does not ask it for the code's settings.
        var tweak = PixiConfig.Parse("""{ "Type": 0, "ActLike": 2 }""");
        Assert.Equal((0, 2), (tweak.Type, tweak.ActLike));
    }

    [Fact]
    public void a_cfg_converts_line_for_line()
    {
        var c = PixiConfig.FromCfg("01\n36\n\n93 94 4B 85 01 80\n01 80\nthing.asm\n2:4\n", "thing");
        Assert.Equal((1, 0x36, "thing.asm"), (c.Type, c.ActLike, c.AsmFile));
        Assert.Equal([0x93, 0x94, 0x4B, 0x85, 0x01, 0x80], c.Tweak);
        Assert.Equal((1, 0x80, 2, 4), (c.Prop1, c.Prop2, c.ExtraBytes, c.ExtraBytesSet));
        Assert.Equal(["thing (extra bit clear)", "thing (extra bit set)"], c.Collection.Select(e => e.Name));
        Assert.True(c.Collection[1].ExtraBit);
        Assert.Equal(c.Tweak, PixiConfig.Parse(c.ToJson()).Tweak);

        // An old sprite_tool .cfg ends in one number, which PIXI reads as no extra bytes.
        Assert.Equal((0, 0), (PixiConfig.FromCfg("1\n36\n0 0 0 0 0 0\n0 0\nx.asm\n2\n", "x") is var o ? (o.ExtraBytes, o.ExtraBytesSet) : default));
        Assert.Contains("00-0C", Assert.Throws<FormatException>(() => PixiConfig.FromCfg("1\n36\n0 0 0 0 0 0\n0 0\nx.asm\n0D:0\n", "x")).Message);
        Assert.Contains("six bytes", Assert.Throws<FormatException>(() => PixiConfig.FromCfg("1\n36\n0 0\n0 0\nx.asm\n", "x")).Message);
    }

    [Fact]
    public void slots_are_the_ranges_pixi_runs()
    {
        Assert.Equal("sprite", PixiImport.SlotKind("00"));
        Assert.Equal("sprite", PixiImport.SlotKind("BF"));
        Assert.Equal("shooter", PixiImport.SlotKind("C0"));
        Assert.Equal("generator", PixiImport.SlotKind("DF"));
        Assert.Null(PixiImport.SlotKind("E0"));                  // PIXI runs nothing custom there
        Assert.Equal("sprite", PixiImport.SlotKind("105:B0"));
        Assert.Null(PixiImport.SlotKind("105:A0"));              // per-level is B0-BF only
        Assert.Null(PixiImport.SlotKind("200:B0"));
        Assert.Equal("105:B0", PixiImport.NormalSlot("105:b0"));
        Assert.Equal("0A", PixiImport.NormalSlot(" a "));
        Assert.Null(PixiImport.NormalSlot("zz"));
    }

    /// <summary>A PIXI folder with a json and a cfg sprite, a per-level line, a shooter, a cluster
    /// sprite, a broken line and the shared files.</summary>
    private static string PixiFolder()
    {
        string dir = Directory.CreateTempSubdirectory("pipedream-pixi-").FullName;
        void Put(string path, string text)
        {
            string full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Put("list.txt", """
            ; my sprites
            00 thing.json
            01 thing.json        ; the same sprite in two slots
            105:B0 walker.cfg display
            C0 bullets.cfg
            zz nope.json
            CLUSTER:
            00 sparkle.asm
            SPRITE:
            02 gone.json
            """);
        Put("sprites/thing.json", Json);
        Put("sprites/thing.asm", "print \"INIT \",pc\nRTL\nincsrc thing_tables.asm\n");
        Put("sprites/thing_tables.asm", "db 1,2,3\n");
        Put("sprites/walker.cfg", "1\n36\n0 0 0 0 0 0\n0 0\nwalker.asm\n0:0\n");
        Put("sprites/walker.asm", "; walker\n");
        Put("shooters/bullets.cfg", "2\n0\n0 0 0 0 0 0\n0 0\nbullets.asm\n0:0\n");
        Put("shooters/bullets.asm", "; bullets\n");
        Put("routines/GetDrawInfo.asm", "; routine\n");
        Put("routines/Spawn/Smoke.asm", "; nested routine\n");
        Put("asm/ExtraDefines/mine.asm", "!x = 1\n");
        Put("sprites/_header.asm", "; sprite header\n");
        return dir;
    }

    [Fact]
    public void a_pixi_folder_reads_its_sprites_slots_and_shared_files()
    {
        var f = PixiImport.ReadFolder(PixiFolder());

        Assert.Equal(["bullets", "thing", "walker"], f.Sprites.Keys.Order());
        Assert.Equal("shooter", f.Sprites["bullets"].Kind);
        Assert.Equal("db 1,2,3\n", System.Text.Encoding.UTF8.GetString(f.Sprites["thing"].Files["thing_tables.asm"]));
        Assert.Equal(new Dictionary<string, string> { ["00"] = "thing", ["01"] = "thing", ["105:B0"] = "walker", ["C0"] = "bullets" }, f.Slots);
        // Stored as JSON, whatever it came in as.
        Assert.Equal(["walker (extra bit clear)", "walker (extra bit set)"], PixiConfig.Parse(f.Sprites["walker"].Config).Collection.Select(c => c.Name));
        Assert.Equal(["ExtraDefines/mine.asm", "routines/GetDrawInfo.asm", "routines/Spawn/Smoke.asm", "sprites/_header.asm"], f.Files.Keys.Order());

        Assert.Contains(f.Problems, p => p.Contains("line 6") && p.Contains("zz nope.json"));
        Assert.Contains(f.Problems, p => p.StartsWith("sprites/gone.json:"));
        Assert.Contains(f.Problems, p => p.StartsWith("1 cluster, extended or other"));
    }

    [Fact]
    public void sprites_slots_and_shared_files_round_trip_through_the_project_file()
    {
        var data = new ProjectFile();
        data.Sprites["thing"] = new() { Kind = "shooter", Config = Json, Source = "; asm\n", Files = { ["t.bin"] = [0, 0xFF] } };
        data.SpriteSlots["105:B0"] = "thing";
        data.PixiFiles["routines/GetDrawInfo.asm"] = "; r\n";

        var back = ProjectFile.FromJson(data.ToJson());

        Assert.Equal(("shooter", Json, "; asm\n"), (back.Sprites["thing"].Kind, back.Sprites["thing"].Config, back.Sprites["thing"].Source));
        Assert.Equal([0, 0xFF], back.Sprites["thing"].Files["t.bin"]);
        Assert.Equal("thing", back.SpriteSlots["105:B0"]);
        Assert.Equal("; r\n", back.PixiFiles["routines/GetDrawInfo.asm"]);
        Assert.Empty(ProjectFile.FromJson("{}").Sprites);                            // an older .pdp
    }

    [Fact]
    public void the_library_and_slots_through_the_session()
    {
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        Assert.Empty(s.CustomSprites);

        Assert.Equal("added Hopper", s.NewCustomSprite("Hopper", "sprite", out var made));
        Assert.Equal("Hopper", made);
        Assert.Equal("Hopper is already in the project", s.NewCustomSprite("Hopper", "sprite", out _));
        Assert.Contains("print \"MAIN \",pc", s.CustomSpriteSource("Hopper"));
        Assert.Equal("Hopper in slot 1A", s.SetSpriteSlot("1a", "Hopper"));
        Assert.Equal("slot C0 holds a shooter, and Hopper is a sprite", s.SetSpriteSlot("C0", "Hopper"));
        Assert.Equal("\"E0\" is not a sprite slot (00-DF, or level:B0-BF)", s.SetSpriteSlot("E0", "Hopper"));
        var row = s.CustomSprites[0];
        Assert.Equal(("Hopper", "sprite", 0x36, "Hopper"), (row.Name, row.Kind, row.ActLike, row.Description));
        Assert.Equal(["1A"], row.Slots);

        Assert.Contains("config not saved", s.SetCustomSpriteConfig("Hopper", "{"));
        var c = PixiConfig.Parse(s.CustomSpriteConfig("Hopper")!);
        c.ActLike = 0x0F;
        Assert.Equal("Hopper config saved", s.SetCustomSpriteConfig("Hopper", c.ToJson()));
        Assert.Equal(0x0F, s.CustomSprites[0].ActLike);

        Assert.StartsWith("imported 3 sprite(s) into 4 slot(s), 4 shared file(s); ", s.ImportPixiFolder(PixiFolder()));
        Assert.Equal(4, s.CustomSprites.Count);
        Assert.Equal("that folder has no list.txt — pick PIXI's own folder", s.ImportPixiFolder(dir));

        Assert.Equal("deleted thing", s.DeleteCustomSprite("thing"));
        Assert.DoesNotContain(s.CustomSprites, r => r.Slots.Contains("00"));        // its slots went with it
        Assert.Equal("slot 1A emptied", s.SetSpriteSlot("1A", null));
    }

    [Fact]
    public void a_sprites_slots_are_set_as_a_list()
    {
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        s.NewCustomSprite("Hopper", "sprite", out _);
        s.NewCustomSprite("Gun", "shooter", out _);

        Assert.Equal("Hopper in slot(s) 105:B0, 1A", s.SetCustomSpriteSlots("Hopper", "1a, 105:b0"));
        Assert.Equal(["105:B0", "1A"], s.CustomSprites.Single(r => r.Name == "Hopper").Slots);
        Assert.Equal("Hopper in slot(s) 1B", s.SetCustomSpriteSlots("Hopper", "1B"));     // the others are let go
        Assert.Equal(["1B"], s.CustomSprites.Single(r => r.Name == "Hopper").Slots);
        Assert.Equal("slot C0 holds a shooter, and Hopper is a sprite", s.SetCustomSpriteSlots("Hopper", "1B C0"));
        Assert.Equal(["1B"], s.CustomSprites.Single(r => r.Name == "Hopper").Slots);       // refused whole
        Assert.Equal("Gun in slot(s) C0", s.SetCustomSpriteSlots("Gun", "C0"));
        Assert.Equal("Hopper is in no slot", s.SetCustomSpriteSlots("Hopper", ""));
    }

    /// <summary>The Custom sprites window lists the library with kinds and slots, and the
    /// buttons that act on one sprite wait for a selection.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_custom_sprites_window_lists_the_library()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        s.ImportPixiFolder(PixiFolder());

        var w = new Ui.CustomSpritesWindow(s);
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var list = w.GetControl<ListBox>("List");
        var rows = ((System.Collections.IEnumerable)list.ItemsSource!).Cast<Ui.CustomSpritesWindow.Row>().ToList();
        Assert.Equal([new("bullets", "shooter", "C0", "bullets (extra bit clear)"),
                      new("thing", "sprite", "00, 01", "Thing"),
                      new("walker", "sprite", "105:B0", "walker (extra bit clear)")], rows);
        Assert.False(w.GetControl<Button>("SlotsButton").IsEnabled);
        list.SelectedIndex = 1;
        Assert.True(w.GetControl<Button>("SlotsButton").IsEnabled);
        Assert.True(w.GetControl<Button>("ConfigButton").IsEnabled);
        w.Close();
    }

    /// <summary>The Sprites catalog offers each slotted custom sprite under its Lunar Magic list
    /// names, with the custom extra bits (3 for an extra-bit entry) and the entry's extra bytes,
    /// and placing one keeps them. A level's own B0 shows only in that level.</summary>
    [Fact]
    public void the_sprite_catalog_offers_custom_sprites_by_their_list_names()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        var c = PixiConfig.Parse(Json);                                   // two extra bytes clear, four set
        c.Collection.Add(new PixiConfig.CollectionEntry { Name = "Thing, fast", ExtraBit = true, Bytes = [1, 2, 3, 4, 5, 0, 0, 0, 0, 0, 0, 0] });
        s.NewCustomSprite("thing", "sprite", out _);
        s.SetCustomSpriteConfig("thing", c.ToJson());
        s.SetCustomSpriteSlots("thing", "1A");
        s.NewCustomSprite("local", "sprite", out _);
        s.SetCustomSpriteSlots("local", "106:B0");
        s.ShowLevel(0x105);

        var custom = s.SpriteCatalog().Items.Where(i => i.Extra >= 2).ToList();
        Assert.Equal(["1A  Thing  · custom", "1A  Thing, fast  · custom"], custom.Select(i => i.Label));
        Assert.Equal(2, custom[0].Extra);
        Assert.Equal([7, 9], custom[0].ExtraBytes);
        Assert.Equal(3, custom[1].Extra);
        Assert.Equal([1, 2, 3, 4], custom[1].ExtraBytes);
        Assert.Contains(s.SpriteCatalog().Items, i => i.Number == 0x1A && i.Extra == 0);   // SMW's 1A is still there

        Assert.True(s.Sprites!.Place(0x1A, 20, 10, custom[1].Extra, custom[1].ExtraBytes));
        var placed = s.Sprites.Sprites.Sprites[^1];
        Assert.Equal((0x1A, 3), (placed.Number, placed.Extra));
        Assert.Equal([1, 2, 3, 4], placed.ExtraBytes);

        s.ShowLevel(0x106);
        Assert.Contains(s.SpriteCatalog().Items, i => i.Number == 0xB0 && i.Extra == 2);
    }
}