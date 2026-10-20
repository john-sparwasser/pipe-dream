using System.Text;

namespace PipeDream;

/// <summary>
/// Lunar Magic's files for showing custom sprites in its editor (reference/PIXI_PORT.md, phase 5),
/// written beside the built ROM as PIXI writes them, so a build opened in Lunar Magic lists and
/// draws the project's sprites. Formats are Lunar Magic's help ("Custom Tooltips for Sprites",
/// "Custom Collections of Sprites", "Custom Map16 for Sprites"), checked against PIXI's output:
/// <list type="bullet">
/// <item><b>.ssc</b>: per display, a description line and a tile line, `NN TYPE text` (space
/// separated, CRLF). TYPE is the display's Y and X nibbles (or, picked by an extension byte, the
/// byte's number counting the three record bytes and the value it holds), then 20/30 for a custom
/// sprite with the extra bit clear/set, plus 2 for tiles and 8 for GFX files.</item>
/// <item><b>.mwt</b> / <b>.mw2</b>: the Add Sprites list, a line per Collection entry
/// (`NN&lt;tab&gt;name`, the number only on a sprite's first) and its sprite record, each flagged
/// as the start of the next group.</item>
/// <item><b>.s16</b>: sprite Map16 from tile 0x400: each sprite's own tiles (0x300 up in its
/// config) in slot order, its displays renumbered to match.</item>
/// </list>
/// Only global slots: Lunar Magic's lists are not per level.
/// </summary>
public static class SpriteAuxFiles
{
    public static readonly string[] Extensions = [".ssc", ".mwt", ".mw2", ".s16"];

    /// <summary>The four files for <paramref name="data"/>'s slotted sprites, by extension; none
    /// when no slotted sprite has a config that reads.</summary>
    public static Dictionary<string, byte[]> Make(ProjectFile data)
    {
        var sprites = new SortedDictionary<int, PixiConfig>();
        foreach (var (slot, name) in data.SpriteSlots)
        {
            if (slot.Contains(':') || !data.Sprites.TryGetValue(name, out var s)) continue;
            try { sprites[Convert.ToInt32(slot, 16)] = PixiConfig.Parse(s.Config); }
            catch (FormatException) { }
        }
        if (sprites.Count == 0) return [];

        var ssc = new StringBuilder();
        var mwt = new StringBuilder();
        var mw2 = new List<byte> { 0x00 };                 // the sprite data header, unused here
        var s16 = new List<byte>();
        foreach (var (n, c) in sprites)
        {
            int own = 0x400 + s16.Count / 8;               // where this sprite's own tiles land
            s16.AddRange(c.Map16);
            foreach (var d in c.Displays) Display(ssc, n, c, d, own);
            bool first = true;
            foreach (var e in c.Collection)
            {
                mwt.Append(first ? $"{n:X2}\t{e.Name}\r\n" : $"\t{e.Name}\r\n");
                first = false;
                int extra = e.ExtraBit ? 3 : 2;
                mw2.AddRange([(byte)(0x70 | extra << 2 | 1), 0x70, (byte)n]);
                mw2.AddRange(e.Bytes.Take(e.ExtraBit ? c.ExtraBytesSet : c.ExtraBytes));
            }
        }
        mw2.Add(0xFF);
        // PIXI's size: 0x10 pages. Lunar Magic reads up to 0x38.
        if (s16.Count > 0x38 * 0x800) s16.RemoveRange(0x38 * 0x800, s16.Count - 0x38 * 0x800);
        var map16 = new byte[Math.Max(0x10 * 0x800, s16.Count)];
        s16.CopyTo(map16);
        return new()
        {
            [".ssc"] = Encoding.UTF8.GetBytes(ssc.ToString()),
            [".mwt"] = Encoding.UTF8.GetBytes(mwt.ToString()),
            [".mw2"] = [.. mw2],
            [".s16"] = map16,
        };
    }

    private static void Display(StringBuilder ssc, int n, PixiConfig c, PixiConfig.Display d, int own)
    {
        string type = (c.DisplayByExtraByte ? $"{d.X + 3:X}{d.Y:X2}" : $"{d.Y & 15:X}{d.X & 15:X}") + (d.ExtraBit ? "3" : "2");
        if (d.Description != "") ssc.Append($"{n:X2} {type}0 {OneLine(d.Description)}\r\n");
        string? look = d.UseText ? $"0,0,*{OneLine(d.Text)}*"
                     : d.Tiles.Count > 0 ? string.Join(' ', d.Tiles.Select(t => $"{t.X},{t.Y},{Tile(t, c, own):X}")) : null;
        if (look is not null) ssc.Append($"{n:X2} {type}2 {look}\r\n");
        if (d.Gfx.Any(g => g is not null))
            ssc.Append($"{n:X2} {type}8 " + string.Join(',', d.Gfx.Select(g => g is { } f ? $"{f.File | (f.Separate ? 0x8000 : 0):X}" : "7F")) + "\r\n");
    }

    /// <summary>A tile's number in Lunar Magic's sprite Map16: the sprite's own (0x300 up) moved to
    /// where they sit in the .s16; 0x8000 shows it translucent.</summary>
    private static int Tile(PixiConfig.Tile t, PixiConfig c, int own)
        => (t.Number >= 0x300 && t.Number - 0x300 < c.Map16.Length / 8 ? own + t.Number - 0x300 : t.Number)
           | (t.Translucent ? 0x8000 : 0);

    /// <summary>A line break ends an .ssc line, so it is written as Lunar Magic's \n escape.</summary>
    private static string OneLine(string s) => s.Replace("\r\n", "\\n").Replace("\n", "\\n");
}
