namespace PipeDream;

// Overworld — the ways between maps: star and pipe warps, the exit tiles Mario walks off one map
// onto another, and where a Koopa Kid drops him. Read-only tables, linked to their destinations.

public sealed partial class Overworld
{
    /// <summary>A star, pipe or location teleport. Source in tiles of the map the submap lives
    /// on ($048431 word: submap&lt;&lt;8 | $1F1F, whose low five bits are the tile X; $048467:
    /// tile Y); destination in pixels ($04849D: X | submap&lt;&lt;9; $0484D3: Y). DestIndex is
    /// the entry whose source cell is this destination, or -1 — Lunar Magic's "N/A", a one-way
    /// trip.</summary>
    public readonly record struct Warp(int Index, int Submap, int X, int Y, int DestSubmap, int DestX, int DestY, int DestIndex);

    public const int WarpSources = 0x048431, WarpSourceYs = 0x048467, WarpDestXs = 0x04849D, WarpDestYs = 0x0484D3;

    /// <summary>How many warps the lookup at $048509 walks: 27 in vanilla; Lunar Magic's hook
    /// there counts them in its <c>LDX #$n*2</c> at hook+$F (zero on a hack with none).</summary>
    public int WarpCount
    {
        get
        {
            var d = Rom.Data;
            int p = Rom.FileOffset(0x048509);
            if (d[p] != 0x22) return 27;
            int hook = Rom.FileOffset(d[p + 1] | d[p + 2] << 8 | d[p + 3] << 16);
            return d[hook + 0xF] == 0xA2 ? (d[hook + 0x10] | d[hook + 0x11] << 8) / 2 : 27;
        }
    }

    private List<Warp>? warps;

    public IReadOnlyList<Warp> Warps => warps ??= ReadWarps();

    private List<Warp> ReadWarps()
    {
        var d = Rom.Data;
        int s = Rom.FileOffset(WarpSources), sy = Rom.FileOffset(WarpSourceYs), dx = Rom.FileOffset(WarpDestXs), dy = Rom.FileOffset(WarpDestYs);
        int n = WarpCount;
        var raw = new List<(int Sub, int X, int Y, int DSub, int DX, int DY)?>();
        for (int i = 0; i < n; i++)
        {
            int sw = d[s + 2 * i] | d[s + 2 * i + 1] << 8, yw = d[sy + 2 * i] | d[sy + 2 * i + 1] << 8;
            int dw = d[dx + 2 * i] | d[dx + 2 * i + 1] << 8, dyw = d[dy + 2 * i] | d[dy + 2 * i + 1] << 8;
            raw.Add(sw == 0xFFFF ? null : ((sw >> 8) & 0xF, sw & 0x1F, yw & 0x1F, (dw >> 9) & 0xF, dw & 0x1FF, dyw & 0x1FF));
        }
        var list = new List<Warp>();
        for (int i = 0; i < n; i++)
        {
            if (raw[i] is not { } w) continue;
            int dest = raw.FindIndex(o => o is { } q && q.Sub == w.DSub && q.X == w.DX >> 4 && q.Y == w.DY >> 4);
            list.Add(new(i, w.Sub, w.X, w.Y, w.DSub, w.DX, w.DY, dest));
        }
        return list;
    }

    /// <summary>An exit tile's teleport: walk onto a red path tile at the source and arrive at
    /// the destination on another map. Fourteen 5-byte entries ($049964 Y px, $049966 X px,
    /// $049968 submap; destinations $0499AA/AC/AE), walked at $049A3F. Positions are pixels
    /// in the table and tiles here. DestIndex as for <see cref="Warp"/>.</summary>
    public readonly record struct ExitPath(int Index, int Submap, int X, int Y, int DestSubmap, int DestX, int DestY, int DestIndex);

    public const int ExitSources = 0x049964, ExitDests = 0x0499AA, ExitCount = 14;

    private List<ExitPath>? exits;

    public IReadOnlyList<ExitPath> ExitPaths => exits ??= ReadExitPaths();

    private List<ExitPath> ReadExitPaths()
    {
        var d = Rom.Data;
        int s = Rom.FileOffset(ExitSources), t = Rom.FileOffset(ExitDests);
        var raw = new List<(int Sub, int X, int Y, int DSub, int DX, int DY)?>();
        for (int i = 0; i < ExitCount; i++)
        {
            int p = s + 5 * i, q = t + 5 * i;
            int y = d[p] | d[p + 1] << 8, x = d[p + 2] | d[p + 3] << 8;
            raw.Add(y == 0xFFFF ? null : (d[p + 4] & 0xF, (x & 0x1FF) >> 4, (y & 0x1FF) >> 4, d[q + 4] & 0xF, ((d[q + 2] | d[q + 3] << 8) & 0x1FF) >> 4, ((d[q] | d[q + 1] << 8) & 0x1FF) >> 4));
        }
        var list = new List<ExitPath>();
        for (int i = 0; i < ExitCount; i++)
        {
            if (raw[i] is not { } e) continue;
            // The arrival is one tile short of the tile that comes back, in the direction of
            // travel — so the link is the source a tile away, which is why LM asks which side.
            int dest = raw.FindIndex(o => o is { } q && q.Sub == e.DSub && Math.Abs(q.X - e.DX) + Math.Abs(q.Y - e.DY) <= 1);
            list.Add(new(i, e.Sub, e.X, e.Y, e.DSub, e.DX, e.DY, dest));
        }
        return list;
    }

    /// <summary>Where a Koopa Kid drops Mario on the main map when he fails the level it pulled
    /// him into: three positions, X px at $048E49 and Y px at $048E4F ($048EBD), in tiles here.</summary>
    public const int KoopaXs = 0x048E49, KoopaYs = 0x048E4F;

    public IReadOnlyList<(int X, int Y)> KoopaTeleports
    {
        get
        {
            var d = Rom.Data;
            int xs = Rom.FileOffset(KoopaXs), ys = Rom.FileOffset(KoopaYs);
            var list = new List<(int, int)>();
            for (int i = 0; i < 3; i++)
            {
                int x = d[xs + 2 * i] | d[xs + 2 * i + 1] << 8, y = d[ys + 2 * i] | d[ys + 2 * i + 1] << 8;
                if (x != 0xFFFF) list.Add(((x & 0x1FF) >> 4, (y & 0x1FF) >> 4));
            }
            return list;
        }
    }
}
