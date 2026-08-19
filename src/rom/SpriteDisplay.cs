using System.Text.Json;

namespace PipeDream;

/// <summary>
/// Static sprite display table, loaded from the embedded SpriteDisplay.json.
/// The JSON is a hand-editable spec (see reference/SPRITE_DISPLAY.md): per sprite it
/// holds a name, cell-relative display tiles (decomposed: x/y offset, 9-bit VRAM tile,
/// palette row, flips, 8/16 size), the sprite-clipping hitbox, and per-SP-slot GFX file
/// requirements for LM-style "loaded only" filtering. `--gen-spritedisplay` regenerates
/// it from a clean ROM (WARNING: overwrites hand edits — review with git diff).
/// </summary>
public static class SpriteDisplay
{
    public sealed record Entry(string Name, SpriteRender.Oam[] Oam, int[][] Req,
                               (int X, int Y, int W, int H)? Hitbox);

    private static Dictionary<int, Entry>? table;
    private static Dictionary<int, Entry> Table => table ??= LoadEmbedded();

    /// <summary>All sprite numbers in the table, ascending (the insert catalog).</summary>
    public static IEnumerable<int> Numbers => Table.Keys.OrderBy(n => n);

    public static bool TryGet(int number, out SpriteRender.Oam[] rel)
    {
        bool ok = Table.TryGetValue(number, out var e);
        rel = e?.Oam!;
        return ok;
    }

    public static string NameOf(int number) =>
        Table.TryGetValue(number, out var e) ? e.Name : "";

    public static (int X, int Y, int W, int H)? HitboxOf(int number) =>
        Table.TryGetValue(number, out var e) ? e.Hitbox : null;

    /// <summary>
    /// True when the level's SP1-4 files satisfy every slot requirement the sprite has.
    /// Sprites with no known requirements (never appear in a vanilla level) pass.
    /// </summary>
    public static bool IsLoaded(int number, int[] spFiles)
    {
        if (!Table.TryGetValue(number, out var e)) return true;
        for (int slot = 0; slot < 4; slot++)
            if (e.Req[slot].Length > 0 && !e.Req[slot].Contains(spFiles[slot]))
                return false;
        return true;
    }

    private static Dictionary<int, Entry> LoadEmbedded()
    {
        try
        {
            using var s = typeof(SpriteDisplay).Assembly.GetManifestResourceStream("SpriteDisplay.json");
            if (s is null) return new();
            using var r = new StreamReader(s);
            return Parse(r.ReadToEnd());
        }
        catch { return new(); }
    }

    private static int Hex(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? Convert.ToInt32(e.GetString(), 16) : e.GetInt32();

    public static Dictionary<int, Entry> Parse(string json)
    {
        var result = new Dictionary<int, Entry>();
        using var doc = JsonDocument.Parse(json);
        foreach (var p in doc.RootElement.GetProperty("sprites").EnumerateObject())
        {
            int num = Convert.ToInt32(p.Name, 16);
            string name = p.Value.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            var oam = p.Value.GetProperty("tiles").EnumerateArray().Select(t =>
            {
                int tile = Hex(t.GetProperty("tile"));
                int pal = t.GetProperty("pal").GetInt32();
                bool xf = t.TryGetProperty("xflip", out var x) && x.GetBoolean();
                bool yf = t.TryGetProperty("yflip", out var y) && y.GetBoolean();
                int size = t.TryGetProperty("size", out var sz) ? sz.GetInt32() : 16;
                int attr = (yf ? 0x80 : 0) | (xf ? 0x40 : 0) | ((pal & 7) << 1) | ((tile >> 8) & 1);
                return new SpriteRender.Oam(t.GetProperty("x").GetInt32(), t.GetProperty("y").GetInt32(),
                                            tile & 0x1FF, attr, size == 16);
            }).ToArray();
            var req = new int[4][];
            for (int slot = 0; slot < 4; slot++) req[slot] = Array.Empty<int>();
            if (p.Value.TryGetProperty("gfx", out var rq))
                foreach (var rp in rq.EnumerateObject())
                    req[int.Parse(rp.Name)] = rp.Value.EnumerateArray().Select(Hex).ToArray();
            (int, int, int, int)? hb = null;
            if (p.Value.TryGetProperty("hitbox", out var h))
                hb = (h.GetProperty("x").GetInt32(), h.GetProperty("y").GetInt32(),
                      h.GetProperty("w").GetInt32(), h.GetProperty("h").GetInt32());
            result[num] = new Entry(name, oam, req, hb);
        }
        return result;
    }

    /// <summary>Generate the JSON table from a clean ROM: canonical OAM capture, name,
    /// clipping hitbox ($03B56C tables via $1662), and slot/file requirements scanned
    /// from every level's sprite list.</summary>
    public static string Generate(Rom rom)
    {
        // Which files sit in each SP slot wherever each sprite number appears.
        var seen = new Dictionary<int, HashSet<int>[]>();
        for (int lvl = 0; lvl < Rom.LevelCount; lvl++)
        {
            SpriteData sd; int[] files;
            try
            {
                sd = SpriteData.Parse(rom, lvl);
                files = SpriteRender.ResolveSpFiles(rom, LevelParser.Parse(rom, lvl).Header, lvl);
            }
            catch { continue; }
            foreach (var s in sd.Sprites)
            {
                if (s.IsScrollCommand) continue;
                if (!seen.TryGetValue(s.Number, out var sets))
                    seen[s.Number] = sets = new[] { new HashSet<int>(), new HashSet<int>(), new HashSet<int>(), new HashSet<int>() };
                for (int slot = 0; slot < 4; slot++) sets[slot].Add(files[slot]);
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"_spec\": \"hand-editable sprite display table - see reference/SPRITE_DISPLAY.md; regenerate with --gen-spritedisplay (overwrites edits)\",");
        sb.AppendLine("  \"sprites\": {");
        var nums = Enumerable.Range(0x00, 0xC9).Concat(new[] { 0xDA, 0xDB, 0xDC, 0xDD, 0xDF });
        bool first = true;
        foreach (int n in nums)
        {
            var oam = SpriteRender.CaptureRelative(rom, n);
            if (oam is null) continue;
            int clip = SpriteRender.LastClip1662 & 0x3F;
            if (!first) sb.AppendLine(",");
            first = false;
            sb.AppendLine($"    \"{n:X2}\": {{");
            sb.AppendLine($"      \"name\": \"{Names.GetValueOrDefault(n, "")}\",");
            var tiles = oam.Select(o =>
            {
                string flips = ((o.Attr & 0x40) != 0 ? ", \"xflip\": true" : "") +
                               ((o.Attr & 0x80) != 0 ? ", \"yflip\": true" : "");
                return $"        {{ \"x\": {o.X}, \"y\": {o.Y}, \"tile\": \"0x{o.Tile:X3}\", " +
                       $"\"pal\": {(o.Attr >> 1) & 7}, \"size\": {(o.Big ? 16 : 8)}{flips} }}";
            });
            sb.AppendLine($"      \"tiles\": [");
            sb.AppendLine(string.Join(",\n", tiles));
            sb.Append("      ]");
            // Sprite<->sprite clipping (GetSpriteClippingA $03B69F): disp/size tables
            // $03B56C/$03B5A8/$03B5E4/$03B620 indexed by tweaker $1662 & 0x3F.
            int hx = (sbyte)rom.ReadByte(0x03B56C + clip), hw = rom.ReadByte(0x03B5A8 + clip);
            int hy = (sbyte)rom.ReadByte(0x03B5E4 + clip), hh = rom.ReadByte(0x03B620 + clip);
            sb.AppendLine(",");
            sb.Append($"      \"hitbox\": {{ \"x\": {hx}, \"y\": {hy}, \"w\": {hw}, \"h\": {hh} }}");
            var usedSlots = oam.Select(o => (o.Tile & 0x1FF) >> 7).Distinct().ToHashSet();
            if (seen.TryGetValue(n, out var sets))
            {
                var parts = usedSlots.Where(sl => sets[sl].Count > 0).OrderBy(sl => sl)
                    .Select(sl => $"\"{sl}\": [{string.Join(", ", sets[sl].OrderBy(f => f).Select(f => $"\"0x{f:X2}\""))}]").ToList();
                if (parts.Count > 0) { sb.AppendLine(","); sb.Append($"      \"gfx\": {{ {string.Join(", ", parts)} }}"); }
            }
            sb.AppendLine();
            sb.Append("    }");
        }
        sb.AppendLine();
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // Vanilla sprite names, from the SpriteMainPtr table comments in the SMW source code
    // (bank 1). DA-DF are the stationary-shell list numbers ($02A97E remap).
    private static readonly Dictionary<int, string> Names = new()
    {
        [0x00] = "Green Koopa, no shell",
        [0x01] = "Red Koopa, no shell",
        [0x02] = "Blue Koopa, no shell",
        [0x03] = "Yellow Koopa, no shell",
        [0x04] = "Green Koopa",
        [0x05] = "Red Koopa",
        [0x06] = "Blue Koopa",
        [0x07] = "Yellow Koopa",
        [0x08] = "Green Koopa, flying left",
        [0x09] = "Green bouncing Koopa",
        [0x0A] = "Red vertical flying Koopa",
        [0x0B] = "Red horizontal flying Koopa",
        [0x0C] = "Yellow Koopa with wings",
        [0x0D] = "Bob-omb",
        [0x0E] = "Keyhole",
        [0x0F] = "Goomba",
        [0x10] = "Bouncing Goomba with wings",
        [0x11] = "Buzzy Beetle",
        [0x12] = "Unused",
        [0x13] = "Spiny",
        [0x14] = "Spiny falling",
        [0x15] = "Fish, horizontal",
        [0x16] = "Fish, vertical",
        [0x17] = "Fish, created from generator",
        [0x18] = "Surface jumping fish",
        [0x19] = "Display text from level Message Box #1",
        [0x1A] = "Classic Piranha Plant",
        [0x1B] = "Bouncing football in place",
        [0x1C] = "Bullet Bill",
        [0x1D] = "Hopping flame",
        [0x1E] = "Lakitu",
        [0x1F] = "Magikoopa",
        [0x20] = "Magikoopa's magic",
        [0x21] = "Moving coin",
        [0x22] = "Green vertical net Koopa",
        [0x23] = "Red vertical net Koopa",
        [0x24] = "Green horizontal net Koopa",
        [0x25] = "Red horizontal net Koopa",
        [0x26] = "Thwomp",
        [0x27] = "Thwimp",
        [0x28] = "Big Boo",
        [0x29] = "Koopa Kid",
        [0x2A] = "Upside down Piranha Plant",
        [0x2B] = "Sumo Brother's fire lightning",
        [0x2C] = "Yoshi egg",
        [0x2D] = "Baby green Yoshi",
        [0x2E] = "Spike Top",
        [0x2F] = "Portable spring board",
        [0x30] = "Dry Bones, throws bones",
        [0x31] = "Bony Beetle",
        [0x32] = "Dry Bones, stay on ledge",
        [0x33] = "Fireball",
        [0x34] = "Boss fireball",
        [0x35] = "Green Yoshi",
        [0x36] = "Unused",
        [0x37] = "Boo",
        [0x38] = "Eerie",
        [0x39] = "Eerie, wave motion",
        [0x3A] = "Urchin, fixed",
        [0x3B] = "Urchin, wall detect",
        [0x3C] = "Urchin, wall follow",
        [0x3D] = "Rip Van Fish",
        [0x3E] = "POW",
        [0x3F] = "Para-Goomba",
        [0x40] = "Para-Bomb",
        [0x41] = "Dolphin, horizontal",
        [0x42] = "Dolphin2, horizontal",
        [0x43] = "Dolphin, vertical",
        [0x44] = "Torpedo Ted",
        [0x45] = "Directional coins",
        [0x46] = "Diggin' Chuck",
        [0x47] = "Swimming/Jumping fish",
        [0x48] = "Diggin' Chuck's rock",
        [0x49] = "Growing/shrinking pipe end",
        [0x4A] = "Goal Point Question Sphere",
        [0x4B] = "Pipe dwelling Lakitu",
        [0x4C] = "Exploding Block",
        [0x4D] = "Ground dwelling Monty Mole",
        [0x4E] = "Ledge dwelling Monty Mole",
        [0x4F] = "Jumping Piranha Plant",
        [0x50] = "Jumping Piranha Plant, spit fire",
        [0x51] = "Ninji",
        [0x52] = "Moving ledge hole in ghost house",
        [0x53] = "Throw block sprite",
        [0x54] = "Climbing net door",
        [0x55] = "Checkerboard platform, horizontal",
        [0x56] = "Flying rock platform, horizontal",
        [0x57] = "Checkerboard platform, vertical",
        [0x58] = "Flying rock platform, vertical",
        [0x59] = "Turn block bridge, horizontal and vertical",
        [0x5A] = "Turn block bridge, horizontal",
        [0x5B] = "Brown platform floating in water",
        [0x5C] = "Checkerboard platform that falls",
        [0x5D] = "Orange platform floating in water",
        [0x5E] = "Orange platform, goes on forever",
        [0x5F] = "Brown platform on a chain",
        [0x60] = "Flat green switch palace switch",
        [0x61] = "Floating skulls",
        [0x62] = "Brown platform, line-guided",
        [0x63] = "Checker/brown platform, line-guided",
        [0x64] = "Rope mechanism, line-guided",
        [0x65] = "Chainsaw, line-guided",
        [0x66] = "Upside down chainsaw, line-guided",
        [0x67] = "Grinder, line-guided",
        [0x68] = "Fuzz ball, line-guided",
        [0x69] = "Unused",
        [0x6A] = "Coin game cloud",
        [0x6B] = "Spring board, left wall",
        [0x6C] = "Spring board, right wall",
        [0x6D] = "Invisible solid block",
        [0x6E] = "Dino Rhino",
        [0x6F] = "Dino Torch",
        [0x70] = "Pokey",
        [0x71] = "Super Koopa, red cape",
        [0x72] = "Super Koopa, yellow cape",
        [0x73] = "Super Koopa, feather",
        [0x74] = "Mushroom",
        [0x75] = "Flower",
        [0x76] = "Star",
        [0x77] = "Feather",
        [0x78] = "1-Up",
        [0x79] = "Growing Vine",
        [0x7A] = "Firework",
        [0x7B] = "Goal Point",
        [0x7C] = "Princess Peach",
        [0x7D] = "Balloon",
        [0x7E] = "Flying Red coin",
        [0x7F] = "Flying yellow 1-Up",
        [0x80] = "Key",
        [0x81] = "Changing item from translucent block",
        [0x82] = "Bonus game sprite",
        [0x83] = "Left flying question block",
        [0x84] = "Flying question block",
        [0x85] = "Unused",
        [0x86] = "Wiggler",
        [0x87] = "Lakitu's cloud",
        [0x88] = "Unused (Winged cage sprite)",
        [0x89] = "Layer 3 smash",
        [0x8A] = "Bird from Yoshi's house",
        [0x8B] = "Puff of smoke from Yoshi's house",
        [0x8C] = "Fireplace smoke/exit from side screen",
        [0x8D] = "Ghost house exit sign and door",
        [0x8E] = "Invisible Warp Hole blocks",
        [0x8F] = "Scale platforms",
        [0x90] = "Large green gas bubble",
        [0x91] = "Chargin' Chuck",
        [0x92] = "Splittin' Chuck",
        [0x93] = "Bouncin' Chuck",
        [0x94] = "Whistlin' Chuck",
        [0x95] = "Clapin' Chuck",
        [0x96] = "Unused (Chargin' Chuck clone)",
        [0x97] = "Puntin' Chuck",
        [0x98] = "Pitchin' Chuck",
        [0x99] = "Volcano Lotus",
        [0x9A] = "Sumo Brother",
        [0x9B] = "Hammer Brother",
        [0x9C] = "Flying blocks for Hammer Brother",
        [0x9D] = "Bubble with sprite",
        [0x9E] = "Ball and Chain",
        [0x9F] = "Banzai Bill",
        [0xA0] = "Activates Bowser scene",
        [0xA1] = "Bowser's bowling ball",
        [0xA2] = "MechaKoopa",
        [0xA3] = "Grey platform on chain",
        [0xA4] = "Floating Spike ball",
        [0xA5] = "Fuzzball/Sparky, ground-guided",
        [0xA6] = "HotHead, ground-guided",
        [0xA7] = "Iggy's ball",
        [0xA8] = "Blargg",
        [0xA9] = "Reznor",
        [0xAA] = "Fishbone",
        [0xAB] = "Rex",
        [0xAC] = "Wooden Spike, moving down and up",
        [0xAD] = "Wooden Spike, moving up/down first",
        [0xAE] = "Fishin' Boo",
        [0xAF] = "Boo Block",
        [0xB0] = "Reflecting stream of Boo Buddies",
        [0xB1] = "Creating/Eating block",
        [0xB2] = "Falling Spike",
        [0xB3] = "Bowser statue fireball",
        [0xB4] = "Grinder, non-line-guided",
        [0xB5] = "Sinking fireball used in boss battles",
        [0xB6] = "Reflecting fireball",
        [0xB7] = "Carrot Top lift, upper right",
        [0xB8] = "Carrot Top lift, upper left",
        [0xB9] = "Info Box",
        [0xBA] = "Timed lift",
        [0xBB] = "Grey moving castle block",
        [0xBC] = "Bowser statue",
        [0xBD] = "Sliding Koopa without a shell",
        [0xBE] = "Swooper bat",
        [0xBF] = "Mega Mole",
        [0xC0] = "Grey platform on lava",
        [0xC1] = "Flying grey turnblocks",
        [0xC2] = "Blurp fish",
        [0xC3] = "Porcu-Puffer fish",
        [0xC4] = "Grey platform that falls",
        [0xC5] = "Big Boo Boss",
        [0xC6] = "Dark room with spot light",
        [0xC7] = "Invisible mushroom",
        [0xC8] = "Light switch block for dark room",
        [0xDA] = "Green Koopa shell",
        [0xDB] = "Red Koopa shell",
        [0xDC] = "Blue Koopa shell",
        [0xDD] = "Yellow Koopa shell",
        [0xDF] = "Carryable shell (unused)",
    };
}
