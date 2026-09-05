using System.Text.Json;

namespace PipeDream;

/// <summary>
/// What a Map16 tile IS, by tileset, from the embedded Map16Tiles.json — the hand-edited
/// companion to <see cref="ActsAs"/>: that table names a behaviour code, this one names a tile.
/// A tileset's own line beats the tile's <c>all</c> line; a tile with neither describes as "".
/// </summary>
public static class Map16Tiles
{
    private static Dictionary<int, Dictionary<string, string>>? tiles;
    private static Dictionary<int, int>? spawns;

    /// <summary>The sprite a block releases when hit (SpriteDisplay numbering), or null.</summary>
    public static int? SpawnOf(int tile)
    {
        Load();
        return spawns!.TryGetValue(tile, out int s) ? s : null;
    }

    public static string Describe(int tile, int tileset)
    {
        Load();
        if (!tiles!.TryGetValue(tile, out var by)) return "";
        return by.TryGetValue(tileset.ToString("X"), out var own) ? own
             : by.TryGetValue("all", out var all) ? all : "";
    }

    private static void Load()
    {
        if (tiles is not null) return;
        tiles = [];
        spawns = [];
        try
        {
            using var s = typeof(Map16Tiles).Assembly.GetManifestResourceStream("Map16Tiles.json");
            if (s is null) return;
            using var r = new StreamReader(s);
            Parse(r.ReadToEnd());
        }
        catch { /* a malformed table costs the readout its words, not the editor its life */ }
    }

    internal static void Parse(string json)
    {
        tiles = [];
        spawns = [];
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("tiles", out var ts)) return;
        foreach (var t in ts.EnumerateObject())
        {
            var by = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (t.Value.TryGetProperty("actAsTilesets", out var sets))
                foreach (var p in sets.EnumerateObject()) by[p.Name] = p.Value.GetString() ?? "";
            int n = Convert.ToInt32(t.Name, 16);
            tiles[n] = by;
            if (t.Value.TryGetProperty("spawns", out var sp) && sp.GetString() is { Length: > 0 } hex)
                spawns[n] = Convert.ToInt32(hex, 16);
        }
    }
}
