using Avalonia;

namespace PipeDream.Ui;

/// <summary>
/// The rectangle arithmetic every grid canvas shares — the level, the Map16 sheet, the GFX
/// sheet, the background tilemap and the 8x8 picker all let you drag out a box of cells, move
/// it, and paint strokes across it. Each used to carry its own copy of the same five lines, and
/// the copies had drifted: two canvases interpolated fast strokes and two left holes, two clamped
/// a drag that ran past the edge and two stopped dead. This is the one copy.
///
/// Everything here is in CELLS of whatever grain the caller works at (16px level cells, 8x8
/// quadrants, sheet pixels); the caller owns the mapping to and from the screen.
/// </summary>
internal static class Lasso
{
    /// <summary>The inclusive rectangle two dragged cells span, whichever way the drag went.</summary>
    public static (int X, int Y, int W, int H) Span((int X, int Y) a, (int X, int Y) b)
        => (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X) + 1, Math.Abs(b.Y - a.Y) + 1);

    public static bool Contains((int X, int Y, int W, int H)? r, (int X, int Y) p)
        => r is { } s && p.X >= s.X && p.X < s.X + s.W && p.Y >= s.Y && p.Y < s.Y + s.H;

    /// <summary>Where a block lands when the cell grabbed at <paramref name="grab"/> is dragged to
    /// <paramref name="to"/>, kept inside a cols × rows grid — a fast drag past an edge parks the
    /// block against it rather than losing part of it.</summary>
    public static (int X, int Y, int W, int H) Moved((int X, int Y, int W, int H) from,
                                                     (int X, int Y) grab, (int X, int Y) to, int cols, int rows)
        => (Math.Clamp(from.X + to.X - grab.X, 0, Math.Max(0, cols - from.W)),
            Math.Clamp(from.Y + to.Y - grab.Y, 0, Math.Max(0, rows - from.H)),
            from.W, from.H);

    /// <summary>Screen point → cell, or null off the grid.</summary>
    public static (int X, int Y)? CellAt(Point p, double step, int cols, int rows)
    {
        if (step <= 0) return null;
        int x = (int)Math.Floor(p.X / step), y = (int)Math.Floor(p.Y / step);
        return (uint)x < (uint)cols && (uint)y < (uint)rows ? (x, y) : null;
    }

    /// <summary>Screen point → the nearest cell INSIDE the grid, for drags that run past its edge:
    /// the selection should land on the border, not stop wherever the pointer left the control.
    /// Null only when there is no grid.</summary>
    public static (int X, int Y)? Clamped(Point p, double step, int cols, int rows)
        => step <= 0 || cols <= 0 || rows <= 0 ? null
         : (Math.Clamp((int)Math.Floor(p.X / step), 0, cols - 1),
            Math.Clamp((int)Math.Floor(p.Y / step), 0, rows - 1));

    /// <summary>Cells on the line between two drag samples, exclusive of the start. At speed the
    /// pointer skips cells, and a stroke with holes in it is a bug rather than a style.</summary>
    public static IEnumerable<(int X, int Y)> Between((int X, int Y) a, (int X, int Y) b)
    {
        int steps = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        if (steps == 0) { yield return b; yield break; }
        for (int i = 1; i <= steps; i++)
            yield return (a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps);
    }
}
