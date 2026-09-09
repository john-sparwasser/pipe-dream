namespace PipeDream;

// Overworld — the ways between maps: star and pipe warps, the exit tiles Mario walks off one map
// onto another, and where a Koopa Kid drops him. Linked to their destinations; the warp table is
// editable in place, the other two are read-only.

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

    /// <summary>
    /// The four warp tables as one edited block, in the ROM's own order: <c>[0, 2n)</c> the source
    /// words, <c>[2n, 4n)</c> the source Ys, <c>[4n, 6n)</c> the destination Xs, <c>[6n, 8n)</c>
    /// the destination Ys, for <see cref="WarpCount"/> slots. The ROM's edited copy, as the level
    /// tables are, so a link shows on the map at once and the build writes it back.
    ///
    /// The four are fixed arrays butted against each other ($048431 + 0x36 IS $048467), so a slot
    /// is only ever reused, never added: a table with none free is full until something is
    /// unlinked. Lunar Magic grows it to 0x100 by moving it and patching the count operand behind
    /// the <c>JSL</c> at $048509; ponytail: not ours yet.
    /// </summary>
    public byte[] WarpTable => Rom.OwWarps ??= ReadWarpTable();

    private byte[] ReadWarpTable()
    {
        int n = WarpCount;
        var table = new byte[8 * n];
        int k = 0;
        foreach (int at in new[] { WarpSources, WarpSourceYs, WarpDestXs, WarpDestYs })
            Rom.Data.AsSpan(Rom.FileOffset(at), 2 * n).CopyTo(table.AsSpan(2 * n * k++));
        return table;
    }

    /// <summary>Write the edited warp block back where the game reads it — four arrays in place,
    /// none of which can fail to fit, since the block was read at the ROM's own count.</summary>
    public static void WriteWarps(Rom rom, byte[] table)
    {
        int n = table.Length / 8, k = 0;
        foreach (int at in new[] { WarpSources, WarpSourceYs, WarpDestXs, WarpDestYs })
            table.AsSpan(2 * n * k++, 2 * n).CopyTo(rom.Data.AsSpan(rom.FileOffset(at)));
    }

    private List<Warp>? warps;

    public IReadOnlyList<Warp> Warps => warps ??= ReadWarps();

    /// <summary>A slot no warp uses: its source is $FFFF, which Mario's tile never equals, so the
    /// game's search walks past it. -1 when every slot is taken. <paramref name="besides"/> skips
    /// one already spoken for, so both ends of a link can be claimed before either is written.</summary>
    public int FreeWarpSlot(int besides = -1)
    {
        var t = WarpTable;
        int n = t.Length / 8;
        for (int i = 0; i < n; i++) if (i != besides && Word(t, i) == 0xFFFF) return i;
        return -1;
    }

    /// <summary>Point a slot's source at a tile and its destination at another, both in tiles.
    /// The destination is stored as the tile's CENTRE in pixels (x*16+8), which is where vanilla
    /// puts every one of its own — Mario lands on the tile, not on its corner.</summary>
    public void SetWarp(int slot, int submap, int x, int y, int destSubmap, int destX, int destY)
    {
        var t = WarpTable;
        int n = t.Length / 8;
        if ((uint)slot >= n) return;
        SetWord(t, 0 * n + slot, submap << 8 | x & 0x1F);
        SetWord(t, 1 * n + slot, y & 0x1F);
        SetWord(t, 2 * n + slot, destSubmap << 9 | (destX * 16 + 8) & 0x1FF);
        SetWord(t, 3 * n + slot, (destY * 16 + 8) & 0x1FF);
        warps = null;
    }

    /// <summary>Free a slot: $FFFF everywhere, the shape the reader and the game both take for
    /// "no warp here".</summary>
    public void ClearWarp(int slot)
    {
        var t = WarpTable;
        int n = t.Length / 8;
        if ((uint)slot >= n) return;
        for (int k = 0; k < 4; k++) SetWord(t, k * n + slot, 0xFFFF);
        warps = null;
    }

    private static int Word(byte[] t, int i) => t[2 * i] | t[2 * i + 1] << 8;

    private static void SetWord(byte[] t, int i, int v) { t[2 * i] = (byte)v; t[2 * i + 1] = (byte)(v >> 8); }

    /// <summary>The layer 1 tiles a warp can sit under: vanilla's own sources wear the pipes 0x5A
    /// and 0x82 and the star 0x5F, and 0x5B is the pipe that obeys the exit directions. Lunar
    /// Magic links "star/pipe/exit tiles"; these are the star and pipe half of that list.</summary>
    public static bool IsWarpTile(int tile) => tile is 0x5A or 0x5B or 0x5F or 0x82;

    private List<Warp> ReadWarps()
    {
        var t = WarpTable;
        int n = t.Length / 8;
        var raw = new List<(int Sub, int X, int Y, int DSub, int DX, int DY)?>();
        for (int i = 0; i < n; i++)
        {
            int sw = Word(t, i), yw = Word(t, n + i), dw = Word(t, 2 * n + i), dyw = Word(t, 3 * n + i);
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

    /// <summary>
    /// An exit tile's teleport: walk onto a red path tile at the source and arrive on another map,
    /// still walking the same way. Fourteen entries, walked at $049A3F, in three tables that sit
    /// back to back: source ($049964, 5 bytes — Y px, X px, submap), destination ($0499AA, the
    /// same shape) and the tile the arrival counts as ($0499F0, 2 bytes — tile Y, tile X).
    ///
    /// The table stores Mario's PIXEL position, and that carries the direction with it: he is
    /// centred on the axis he is not moving along and eight pixels short of the tile's centre on
    /// the one he is (<see cref="StepInto"/>). So the same bytes read as "tile 20 walking down"
    /// or "tile 21 walking up", and the tile a source belongs to is the one of the two that is
    /// actually an exit tile. <see cref="Dir"/> is that direction, as
    /// <see cref="DirectionNames"/> numbers them; DestIndex as for <see cref="Warp"/>.
    /// </summary>
    public readonly record struct ExitPath(int Index, int Submap, int X, int Y, int Dir,
                                           int DestSubmap, int DestX, int DestY, int DestIndex);

    public const int ExitSources = 0x049964, ExitDests = 0x0499AA, ExitDestTiles = 0x0499F0, ExitCount = 14;

    /// <summary>The three exit tables as one edited block, in the ROM's own order and byte for
    /// byte: 14 sources, 14 destinations, 14 arrival tiles. Contiguous in the ROM ($049964 + 0x46
    /// IS $0499AA, and + 0x46 again is $0499F0), and fixed — as with the warps, a slot is reused,
    /// never added.</summary>
    public byte[] ExitTable => Rom.OwExits ??= Rom.Data.AsSpan(Rom.FileOffset(ExitSources), 5 * ExitCount * 2 + 2 * ExitCount).ToArray();

    public static void WriteExits(Rom rom, byte[] table)
        => table.CopyTo(rom.Data.AsSpan(rom.FileOffset(ExitSources)));

    /// <summary>Where Mario is, in pixels, at the moment he steps into a tile travelling
    /// <paramref name="dir"/> (<see cref="DirectionNames"/>): centred across his path and a
    /// tile short along it. Both halves of an entry are this — the source is the player entering
    /// the tile he leaves from, the destination the player entering the tile he arrives at.</summary>
    public static (int X, int Y) StepInto(int tx, int ty, int dir) => dir switch
    {
        0 => (tx * 16 + 8, ty * 16 + 16),      // up:    coming from below
        1 => (tx * 16 + 8, ty * 16),           // down:  coming from above
        2 => (tx * 16 + 16, ty * 16 + 8),      // left:  coming from the right
        _ => (tx * 16, ty * 16 + 8),           // right: coming from the left
    };

    /// <summary>The way Mario travels through an exit tile, worked out from the map: away from
    /// its one walkable neighbour, since an exit tile is the end of a path and he leaves by the
    /// side the path does not use. -1 when the map does not say — no neighbour to come from, or
    /// several. (All 13 of vanilla's exit-tile entries agree with this rule, measured
    /// 2026-09-08; it is the "side to enter from" Lunar Magic asks for in its link dialog.)</summary>
    public int TravelDirOf(int x, int y, bool submapMap)
    {
        int found = -1;
        for (int dir = 0; dir < 4; dir++)
        {
            var (nx, ny) = Neighbour(x, y, dir);
            if ((uint)nx >= Cols || (uint)ny >= Rows) continue;
            if (KindOf(Layer1At(nx, ny, submapMap)) is PathKind.None or PathKind.Exit) continue;
            if (found >= 0) return -1;                     // two ways in: the map cannot say which
            found = dir;
        }
        return found < 0 ? -1 : found ^ 1;                 // travel away from it: up<->down, left<->right
    }

    /// <summary>The cell one step from another, in <see cref="DirectionNames"/> order.</summary>
    public static (int X, int Y) Neighbour(int x, int y, int dir)
        => dir switch { 0 => (x, y - 1), 1 => (x, y + 1), 2 => (x - 1, y), _ => (x + 1, y) };

    private List<ExitPath>? exits;

    public IReadOnlyList<ExitPath> ExitPaths => exits ??= ReadExitPaths();

    /// <summary>A slot no exit uses: $FFFF where its Y would be, which no position matches.</summary>
    public int FreeExitSlot(int besides = -1)
    {
        for (int i = 0; i < ExitCount; i++)
            if (i != besides && WordAt(ExitTable, 5 * i) == 0xFFFF) return i;
        return -1;
    }

    /// <summary>Point a slot at a tile — the player walking through it in <paramref name="dir"/> —
    /// and land him on another, still walking that way. The arrival tile goes in the third table,
    /// which is what the game gives the player as his position on the new map.</summary>
    public void SetExitPath(int slot, int submap, int x, int y, int dir, int destSubmap, int destX, int destY)
    {
        if ((uint)slot >= ExitCount) return;
        var t = ExitTable;
        var (sx, sy) = StepInto(x, y, dir);
        var (dx, dy) = StepInto(destX, destY, dir);        // he arrives still travelling the same way
        SetWordAt(t, 5 * slot, sy);
        SetWordAt(t, 5 * slot + 2, sx);
        t[5 * slot + 4] = (byte)submap;
        int q = 5 * ExitCount;
        SetWordAt(t, q + 5 * slot, dy);
        SetWordAt(t, q + 5 * slot + 2, dx);
        t[q + 5 * slot + 4] = (byte)destSubmap;
        int r = 10 * ExitCount;
        t[r + 2 * slot] = (byte)destY;
        t[r + 2 * slot + 1] = (byte)destX;
        exits = null;
    }

    public void ClearExitPath(int slot)
    {
        if ((uint)slot >= ExitCount) return;
        var t = ExitTable;
        for (int k = 0; k < 4; k++) t[5 * slot + k] = 0xFF;
        t[5 * slot + 4] = 0;
        exits = null;
    }

    private static int WordAt(byte[] t, int off) => t[off] | t[off + 1] << 8;

    private static void SetWordAt(byte[] t, int off, int v) { t[off] = (byte)v; t[off + 1] = (byte)(v >> 8); }

    private List<ExitPath> ReadExitPaths()
    {
        var t = ExitTable;
        int q = 5 * ExitCount;
        var raw = new List<(int Sub, int X, int Y, int Dir, int DSub, int DX, int DY)?>();
        for (int i = 0; i < ExitCount; i++)
        {
            int y = WordAt(t, 5 * i), x = WordAt(t, 5 * i + 2), sub = t[5 * i + 4] & 0xF;
            if (y == 0xFFFF) { raw.Add(null); continue; }
            var (sx, sy, dir) = Decode(x, y, sub != 0);
            var (dx, dy, _) = Decode(WordAt(t, q + 5 * i + 2), WordAt(t, q + 5 * i), (t[q + 5 * i + 4] & 0xF) != 0, dir);
            raw.Add((sub, sx, sy, dir, t[q + 5 * i + 4] & 0xF, dx, dy));
        }
        var list = new List<ExitPath>();
        for (int i = 0; i < ExitCount; i++)
        {
            if (raw[i] is not { } e) continue;
            int dest = raw.FindIndex(o => o is { } p && p.Sub == e.DSub && p.X == e.DX && p.Y == e.DY);
            list.Add(new(i, e.Sub, e.X, e.Y, e.Dir, e.DSub, e.DX, e.DY, dest));
        }
        return list;
    }

    /// <summary>A stored position back into a tile and a direction. The pixel pair says which axis
    /// the player is moving along; along it the tile is either the one the position sits on or the
    /// one before, and the exit tile on the map is the one that settles it. With
    /// <paramref name="known"/> the direction is already known (a destination travels the same way
    /// as the source that reaches it) and only the tile is worked out.</summary>
    private (int X, int Y, int Dir) Decode(int x, int y, bool submapMap, int known = -1)
    {
        bool vertical = (x & 0xF) == 8;
        int near = vertical ? 1 : 3;                            // down / right: the tile the position sits on
        int far = vertical ? 0 : 2;                             // up / left: one tile back along the way
        (int X, int Y) At(int dir) => dir == near ? (x >> 4, y >> 4)
                                                  : vertical ? (x >> 4, (y >> 4) - 1) : ((x >> 4) - 1, y >> 4);
        if (known >= 0) { var (kx, ky) = At(known); return (kx, ky, known); }
        foreach (int dir in new[] { near, far })
        {
            var (tx, ty) = At(dir);
            if ((uint)tx < Cols && (uint)ty < Rows && KindOf(Layer1At(tx, ty, submapMap)) == PathKind.Exit)
                return (tx, ty, dir);
        }
        var (nx, ny) = At(near);
        return (nx, ny, near);                                  // no exit tile either side: take it as read
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
