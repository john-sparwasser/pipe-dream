namespace PipeDream;

/// <summary>A level's expanded Map16 tilemap (16-bit tile indices; 0xFFFF = empty).</summary>
public sealed class Map16Grid
{
    public readonly int Width, Height;
    public readonly ushort[] Tiles;
    public const ushort Empty = 0xFFFF;

    public Map16Grid(int w, int h)
    {
        Width = w; Height = h;
        Tiles = new ushort[w * h];
        Array.Fill(Tiles, Empty);
    }

    public void Set(int x, int y, int tile)
    {
        if ((uint)x < (uint)Width && (uint)y < (uint)Height) Tiles[y * Width + x] = (ushort)tile;
    }
    public int Get(int x, int y)
        => (uint)x < (uint)Width && (uint)y < (uint)Height ? Tiles[y * Width + x] : Empty;
    public int PlacedCount()
    {
        int c = 0;
        foreach (var t in Tiles) if (t != Empty) c++;
        return c;
    }

    public Map16Grid Clone()
    {
        var g = new Map16Grid(Width, Height);
        Array.Copy(Tiles, g.Tiles, Tiles.Length);
        return g;
    }
}
