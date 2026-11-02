using System.Text.Json;

namespace PipeDream;

// Overworld — what a custom overworld sprite looks like on the map: BOWSIE's tooltip JSON (the .json
// beside a sprite's .asm, kept as its Config) read into OAM tiles, and drawn from the submap's SP1-SP4
// in its sprite colours. The list itself is Overworld.Sprites.cs.

public sealed partial class Overworld
{
    /// <summary>A sprite's look: its tooltip and its tiles, X and Y in pixels from the sprite's position.</summary>
    public sealed record SpriteLook(string Tooltip, IReadOnlyList<SpriteRender.Oam> Tiles);

    /// <summary>What Edit config starts a sprite with none on: BOWSIE's format, one 16x16 tile.</summary>
    public const string SpriteLookTemplate = """
        {
          "tooltip": "What this sprite is",
          "no_tiles": 1,
          "tile_1": { "is_16x16": true, "tile_num": 0, "x_offset": 0, "y_offset": 0,
                      "x_flip": false, "y_flip": false, "priority": 2, "palette": 0, "second_page": false }
        }
        """;

    /// <summary>Read BOWSIE's tooltip JSON (bowsie src/include/map16.cpp): <c>tooltip</c>, <c>no_tiles</c> and
    /// <c>tile_1</c>..<c>tile_N</c>, each with <c>tile_num</c> (SP1-SP4, 00-FF; <c>second_page</c> adds 100), offsets,
    /// flips, <c>palette</c> (sprite row 0-7) and <c>is_16x16</c>. Null for an empty text; a bad one throws
    /// <see cref="FormatException"/> saying what is wrong.</summary>
    public static SpriteLook? ParseSpriteLook(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int n = root.TryGetProperty("no_tiles", out var nt) ? nt.GetInt32() : 0;
            if (n < 0) throw new FormatException("no_tiles must not be negative");
            var tiles = new List<SpriteRender.Oam>();
            for (int i = 1; i <= n; i++)
            {
                if (!root.TryGetProperty($"tile_{i}", out var t)) throw new FormatException($"tile_{i} is missing (no_tiles is {n})");
                int Int(string k) => t.TryGetProperty(k, out var v) ? v.GetInt32() : 0;
                bool Bool(string k) => t.TryGetProperty(k, out var v) && v.GetBoolean();
                if (!t.TryGetProperty("tile_num", out _)) throw new FormatException($"tile_{i} has no tile_num");
                int attr = (Bool("y_flip") ? 0x80 : 0) | (Bool("x_flip") ? 0x40 : 0) | (Int("priority") & 3) << 4 | (Int("palette") & 7) << 1;
                tiles.Add(new(Int("x_offset"), Int("y_offset"), (Int("tile_num") & 0xFF) | (Bool("second_page") ? 0x100 : 0), attr, Bool("is_16x16")));
            }
            return new(root.TryGetProperty("tooltip", out var tip) ? tip.GetString() ?? "" : "", tiles);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new FormatException(e.Message);
        }
    }

    private readonly Dictionary<(string Json, int Submap), (uint[] Px, int W, int H, int X, int Y)?> spriteArt = [];

    /// <summary>A look drawn in a submap's sprite graphics and colours: the pixels, their size, and where their
    /// top-left sits from the sprite's position. Null for no tiles. Cached by the JSON; a GFX or colour edit drops it.</summary>
    public (uint[] Px, int W, int H, int X, int Y)? SpriteArt(SpriteLook look, string json, int submap)
    {
        if (spriteArt.TryGetValue((json, submap), out var done)) return done;
        if (look.Tiles.Count == 0) return spriteArt[(json, submap)] = null;
        int x0 = look.Tiles.Min(t => t.X), y0 = look.Tiles.Min(t => t.Y);
        int w = look.Tiles.Max(t => t.X + (t.Big ? 16 : 8)) - x0, h = look.Tiles.Max(t => t.Y + (t.Big ? 16 : 8)) - y0;
        var rec = Rom.OwGfxBypass(submap) ?? VanillaGfxRecord(Rom);
        var sp = SpriteRender.LoadSpTiles(Rom, [rec[11] & 0xFFF, rec[10] & 0xFFF, rec[9] & 0xFFF, rec[8] & 0xFFF]);
        var px = new uint[w * h];
        // Draw takes OAM order, the first on top; the list is painted in order, the last tile on top.
        SpriteRender.Draw(px, w, h, [.. look.Tiles.Reverse().Select(t => t with { X = t.X - x0, Y = t.Y - y0 })], sp, PaletteOf(submap));
        return spriteArt[(json, submap)] = (px, w, h, x0, y0);
    }
}
