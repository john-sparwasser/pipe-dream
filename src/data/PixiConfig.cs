using System.Text.Encodings.Web;
using System.Text.Json;

namespace PipeDream;

/// <summary>
/// A custom sprite's configuration, as PIXI's JSON holds it (reference/PIXI_PORT.md): what the
/// sprite table gets (type, acts-like, the six tweaker bytes, two extra property bytes), how many
/// extra bytes a placement carries, and how Lunar Magic shows and lists it (Map16, Displays,
/// Collection). A .cfg converts into the same thing, so a project keeps every config as JSON.
/// The JSON's key names are PIXI's own, typos included, since PIXI reads them by name.
/// </summary>
public sealed class PixiConfig
{
    /// <summary>0 tweaks a vanilla sprite (no ASM), 1 is a custom sprite, 2 a shooter or generator.</summary>
    public int Type { get; set; } = 1;
    public int ActLike { get; set; } = 0x36;
    /// <summary>$1656 $1662 $166E $167A $1686 $190F, in that order.</summary>
    public byte[] Tweak { get; set; } = new byte[6];
    public byte Prop1 { get; set; }
    public byte Prop2 { get; set; }
    public string AsmFile { get; set; } = "";
    /// <summary>Extra bytes a placement carries with the extra bit clear, and with it set (0-12).</summary>
    public int ExtraBytes { get; set; }
    public int ExtraBytesSet { get; set; }
    /// <summary>The sprite's own Map16 tiles, 8 bytes each (TL, BL, TR, BR tile words), which its
    /// displays number from 0x300.</summary>
    public byte[] Map16 { get; set; } = [];
    /// <summary>Displays are chosen by an extension byte's value rather than by the position nibbles.</summary>
    public bool DisplayByExtraByte { get; set; }
    public List<Display> Displays { get; set; } = [];
    public List<CollectionEntry> Collection { get; set; } = [];

    /// <summary>One way the sprite looks in the editor: for one extra bit and one position (X, Y
    /// nibbles 0-F) or, with <see cref="DisplayByExtraByte"/>, one extra byte (Index) holding Value.</summary>
    public sealed class Display
    {
        public string Description { get; set; } = "";
        public bool ExtraBit { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public bool UseText { get; set; }
        public string Text { get; set; } = "";
        public List<Tile> Tiles { get; set; } = [];
        /// <summary>Per SP slot, the GFX file the display wants loaded, if it says.</summary>
        public (int File, bool Separate)?[] Gfx { get; set; } = new (int, bool)?[4];
    }

    /// <summary>A Map16 tile at a pixel offset from the sprite's position.</summary>
    public sealed record Tile(int X, int Y, int Number, bool Translucent = false);

    /// <summary>One entry in Lunar Magic's sprite list: a name, and the extra bit and extra bytes a
    /// placement of it gets.</summary>
    public sealed class CollectionEntry
    {
        public string Name { get; set; } = "";
        public bool ExtraBit { get; set; }
        public byte[] Bytes { get; set; } = new byte[12];
    }

    // ---- The tweaker bytes, field by field ----

    /// <summary>A named part of a tweaker byte: one flag bit, or a number in Mask &lt;&lt; Shift.</summary>
    public sealed record Field(string Name, int Mask, int Shift = 0, bool Number = false);

    /// <summary>The six tweaker bytes' JSON keys and their fields, PIXI's names and bits.</summary>
    public static readonly (string Key, Field[] Fields)[] TweakFields =
    [
        ("$1656", [new("Object Clipping", 0x0F, 0, true), new("Can be jumped on", 0x10), new("Dies when jumped on", 0x20),
                   new("Hop in/kick shell", 0x40), new("Disappears in cloud of smoke", 0x80)]),
        ("$1662", [new("Sprite Clipping", 0x3F, 0, true), new("Use shell as death frame", 0x40),
                   new("Fall straight down when killed", 0x80)]),
        ("$166E", [new("Use second graphics page", 0x01), new("Palette", 0x07, 1, true), new("Disable fireball killing", 0x10),
                   new("Disable cape killing", 0x20), new("Disable water splash", 0x40), new("Don't interact with Layer 2", 0x80)]),
        ("$167A", [new("Don't disable cliping when starkilled", 0x01), new("Invincible to star/cape/fire/bounce blk.", 0x02),
                   new("Process when off screen", 0x04), new("Don't change into shell when stunned", 0x08),
                   new("Can't be kicked like shell", 0x10), new("Process interaction with Mario every frame", 0x20),
                   new("Gives power-up when eaten by yoshi", 0x40), new("Don't use default interaction with Mario", 0x80)]),
        ("$1686", [new("Inedible", 0x01), new("Stay in Yoshi's mouth", 0x02), new("Weird ground behaviour", 0x04),
                   new("Don't interact with other sprites", 0x08), new("Don't change direction if touched", 0x10),
                   new("Don't turn into coin when goal passed", 0x20), new("Spawn a new sprite", 0x40),
                   new("Don't interact with objects", 0x80)]),
        ("$190F", [new("Make platform passable from below", 0x01), new("Don't erase when goal passed", 0x02),
                   new("Can't be killed by sliding", 0x04), new("Takes 5 fireballs to kill", 0x08),
                   new("Can be jumped on with upwards Y speed", 0x10), new("Death frame two tiles high", 0x20),
                   new("Don't turn into a coin with silver POW", 0x40), new("Don't get stuck in walls (carryable sprites)", 0x80)]),
    ];

    // ---- Reading ----

    /// <summary>Read PIXI's JSON. Throws <see cref="FormatException"/> naming what is wrong, as PIXI
    /// would refuse it: the keys it requires are required here too.</summary>
    public static PixiConfig Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException e) { throw new FormatException("not valid JSON: " + e.Message); }
        using (doc)
        {
            var j = doc.RootElement;
            var c = new PixiConfig { ActLike = Int(j, "ActLike"), Type = Int(j, "Type") };
            // Like PIXI, only a sprite with code has an ASM file, property bytes and extra bytes.
            if (c.Type != 0)
            {
                c.AsmFile = Str(j, "AsmFile");
                c.Prop1 = (byte)Int(j, "Extra Property Byte 1");
                c.Prop2 = (byte)Int(j, "Extra Property Byte 2");
                c.ExtraBytes = Math.Clamp(Int(j, "Additional Byte Count (extra bit clear)"), 0, 12);
                c.ExtraBytesSet = Math.Clamp(Int(j, "Additional Byte Count (extra bit set)"), 0, 12);
            }
            for (int i = 0; i < 6; i++)
                if (j.TryGetProperty(TweakFields[i].Key, out var t)) c.Tweak[i] = ReadTweak(t, TweakFields[i].Fields);
            try { c.Map16 = j.TryGetProperty("Map16", out var m) ? Convert.FromBase64String(m.GetString() ?? "") : []; }
            catch (FormatException) { throw new FormatException("Map16 is not base64"); }
            c.Map16 = c.Map16[..(c.Map16.Length / 8 * 8)];
            c.DisplayByExtraByte = j.TryGetProperty("DisplayType", out var dt) && dt.GetString() switch
            {
                "XY" => false,
                "ExByte" => true,
                var other => throw new FormatException($"unknown DisplayType \"{other}\""),
            };
            if (j.TryGetProperty("Displays", out var ds))
                foreach (var d in ds.EnumerateArray()) c.Displays.Add(ReadDisplay(d, c.DisplayByExtraByte));
            if (j.TryGetProperty("Collection", out var cs))
                foreach (var e in cs.EnumerateArray())
                {
                    var entry = new CollectionEntry { Name = Str(e, "Name"), ExtraBit = Bool(e, "ExtraBit") };
                    for (int i = 0; i < 12; i++)
                        if (e.TryGetProperty($"Extra Property Byte {i + 1}", out var b)) entry.Bytes[i] = (byte)b.GetInt32();
                    c.Collection.Add(entry);
                }
            return c;
        }
    }

    private static byte ReadTweak(JsonElement t, Field[] fields)
    {
        int v = 0;
        foreach (var f in fields)
            if (t.TryGetProperty(f.Name, out var p))
                v |= f.Number ? (p.GetInt32() & f.Mask) << f.Shift : p.ValueKind == JsonValueKind.True ? f.Mask : 0;
        return (byte)v;
    }

    private static Display ReadDisplay(JsonElement d, bool byExtraByte)
    {
        var dis = new Display
        {
            Description = Str(d, "Description"),
            ExtraBit = Bool(d, "ExtraBit"),
            X = Int(d, byExtraByte ? "Index" : "X"),
            Y = Int(d, byExtraByte ? "Value" : "Y"),
            UseText = Bool(d, "UseText"),
            Text = d.TryGetProperty("DisplayText", out var dtx) ? dtx.GetString() ?? "" : "",
        };
        if (d.TryGetProperty("Tiles", out var ts))
            foreach (var t in ts.EnumerateArray())
            {
                int n = Int(t, "map16 tile");
                // Bit 15 of the tile number is the older way of saying translucent.
                bool translucent = t.TryGetProperty("Translucent", out var tr) && tr.GetBoolean() || (n & 0x8000) != 0;
                dis.Tiles.Add(new Tile(Int(t, "X offset"), Int(t, "Y offset"), n & 0x7FFF, translucent));
            }
        if (d.TryGetProperty("GFXInfo", out var g))
            for (int i = 0; i < 4; i++)
                if (g.TryGetProperty($"{i}", out var s))
                    dis.Gfx[i] = (Int(s, "Value"), Bool(s, "Separate"));
        return dis;
    }

    private static int Int(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.TryGetInt32(out int n) ? n
         : throw new FormatException($"\"{key}\" is missing or not a number");
    private static bool Bool(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean()
         : throw new FormatException($"\"{key}\" is missing or not true/false");
    private static string Str(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!
         : throw new FormatException($"\"{key}\" is missing or not text");

    /// <summary>
    /// Read a .cfg: six non-blank lines of hex — type, acts-like, the six tweaker bytes, the two
    /// property bytes, the ASM file, and `clear:set` extra-byte counts (an old single-number line
    /// means none). PIXI lists a .cfg sprite in Lunar Magic as two entries, "name (extra bit
    /// clear)" and "(extra bit set)", so the conversion gives it those two Collection entries.
    /// </summary>
    public static PixiConfig FromCfg(string text, string name)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length < 5) throw new FormatException($"a .cfg has six lines; this has {lines.Length}");
        var c = new PixiConfig { Type = Hex(lines[0], "type"), ActLike = Hex(lines[1], "acts-like") };
        var tweak = lines[2].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tweak.Length < 6) throw new FormatException("the tweaker line needs six bytes");
        for (int i = 0; i < 6; i++) c.Tweak[i] = (byte)Hex(tweak[i], "tweaker byte");
        var prop = lines[3].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (prop.Length < 2) throw new FormatException("the property line needs two bytes");
        c.Prop1 = (byte)Hex(prop[0], "property byte");
        c.Prop2 = (byte)Hex(prop[1], "property byte");
        c.AsmFile = lines[4];
        if (lines.Length > 5 && lines[5].IndexOf(':') is int colon and >= 0)
        {
            c.ExtraBytes = Hex(lines[5][..colon], "extra byte count");
            c.ExtraBytesSet = Hex(lines[5][(colon + 1)..], "extra byte count");
            if (c.ExtraBytes > 12 || c.ExtraBytesSet > 12) throw new FormatException("an extra byte count is 00-0C");
        }
        c.Collection.Add(new CollectionEntry { Name = name + " (extra bit clear)" });
        c.Collection.Add(new CollectionEntry { Name = name + " (extra bit set)", ExtraBit = true });
        return c;
    }

    private static int Hex(string s, string what)
        => int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int v) ? v
         : throw new FormatException($"the {what} \"{s}\" is not hex");

    // ---- Writing ----

    /// <summary>The JSON PIXI reads, laid out as its CFG Editor writes it: every key, every tweaker
    /// field, two-space indent.</summary>
    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            for (int i = 0; i < 6; i++)
            {
                w.WriteStartObject(TweakFields[i].Key);
                foreach (var f in TweakFields[i].Fields)
                    if (f.Number) w.WriteNumber(f.Name, (Tweak[i] >> f.Shift) & f.Mask);
                    else w.WriteBoolean(f.Name, (Tweak[i] & f.Mask) != 0);
                w.WriteEndObject();
            }
            w.WriteString("AsmFile", AsmFile);
            w.WriteNumber("ActLike", ActLike);
            w.WriteNumber("Type", Type);
            w.WriteNumber("Extra Property Byte 1", Prop1);
            w.WriteNumber("Extra Property Byte 2", Prop2);
            w.WriteNumber("Additional Byte Count (extra bit clear)", ExtraBytes);
            w.WriteNumber("Additional Byte Count (extra bit set)", ExtraBytesSet);
            w.WriteString("Map16", Convert.ToBase64String(Map16));
            w.WriteStartArray("Displays");
            foreach (var d in Displays) WriteDisplay(w, d);
            w.WriteEndArray();
            w.WriteStartArray("Collection");
            foreach (var e in Collection)
            {
                w.WriteStartObject();
                w.WriteString("Name", e.Name);
                w.WriteBoolean("ExtraBit", e.ExtraBit);
                for (int i = 0; i < 12; i++) w.WriteNumber($"Extra Property Byte {i + 1}", e.Bytes[i]);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("DisplayType", DisplayByExtraByte ? "ExByte" : "XY");
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private void WriteDisplay(Utf8JsonWriter w, Display d)
    {
        w.WriteStartObject();
        w.WriteNumber(DisplayByExtraByte ? "Index" : "X", d.X);
        w.WriteNumber(DisplayByExtraByte ? "Value" : "Y", d.Y);
        w.WriteString("Description", d.Description);
        w.WriteBoolean("ExtraBit", d.ExtraBit);
        w.WriteStartArray("Tiles");
        foreach (var t in d.Tiles)
        {
            w.WriteStartObject();
            w.WriteNumber("X offset", t.X);
            w.WriteNumber("Y offset", t.Y);
            w.WriteNumber("map16 tile", t.Number);
            if (t.Translucent) w.WriteBoolean("Translucent", true);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartObject("GFXInfo");
        for (int i = 0; i < 4; i++)
            if (d.Gfx[i] is { } g)
            {
                w.WriteStartObject($"{i}");
                w.WriteNumber("Value", g.File);
                w.WriteBoolean("Separate", g.Separate);
                w.WriteEndObject();
            }
        w.WriteEndObject();
        w.WriteString("DisplayText", d.Text);
        w.WriteBoolean("UseText", d.UseText);
        w.WriteEndObject();
    }
}
