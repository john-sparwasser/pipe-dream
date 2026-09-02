using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace PipeDream.Ui;

/// <summary>The pointer shapes the canvases show, made once. A canvas that builds a Cursor per
/// pointer move allocates on every pixel of travel for nothing.</summary>
internal static class UiCursors
{
    public static readonly Cursor Hand = new(StandardCursorType.Hand);
    // ponytail: Windows has no stock open/closed-hand cursors; Hand + DragMove are the nearest
    // native pair. Custom bitmap cursors if the real grab hands ever matter.
    public static readonly Cursor Grab = new(StandardCursorType.DragMove);
    public static readonly Cursor Move = new(StandardCursorType.SizeAll);
    public static readonly Cursor SizeWE = new(StandardCursorType.SizeWestEast);
    public static readonly Cursor SizeNS = new(StandardCursorType.SizeNorthSouth);
    public static readonly Cursor SizeNWSE = new(StandardCursorType.TopLeftCorner);
    public static readonly Cursor SizeNESW = new(StandardCursorType.TopRightCorner);
}

/// <summary>
/// The grips on a selection's border — where they are, what grabbing one does, and how they are
/// drawn — for every canvas whose selection can be resized. An edge is a direction per axis:
/// (-1, 0) is the left edge, (1, 1) the bottom-right corner, (0, 0) no grip at all.
///
/// Grips are sized in SCREEN pixels, not cells: a sheet zoomed out has cells smaller than a
/// comfortable grab, and the grip has to stay grabbable either way.
/// </summary>
internal static class Grips
{
    /// <summary>Which grip of <paramref name="r"/> is under the point. <paramref name="grip"/> is
    /// the grab zone in screen pixels, never wider than a third of the rectangle — on a small
    /// selection an over-eager grip would swallow the middle and leave nowhere to grab it by,
    /// which is also what makes "both edges at once" impossible. Axes that cannot resize never
    /// report a grip.</summary>
    public static (int DX, int DY) EdgeAt(Point p, Rect r, double grip, bool wOk = true, bool hOk = true)
    {
        double g = Math.Min(grip, Math.Min(r.Width, r.Height) / 3);
        // Only ON the border: a point level with an edge but far above the rectangle is not a grip.
        if (p.X < r.Left - g || p.X > r.Right + g || p.Y < r.Top - g || p.Y > r.Bottom + g) return (0, 0);
        int dx = !wOk ? 0 : Math.Abs(p.X - r.Left) <= g ? -1 : Math.Abs(p.X - r.Right) <= g ? 1 : 0;
        int dy = !hOk ? 0 : Math.Abs(p.Y - r.Top) <= g ? -1 : Math.Abs(p.Y - r.Bottom) <= g ? 1 : 0;
        return (dx, dy);
    }

    /// <summary>The grabbed edge follows the pointer; the opposite one stays put. Never thinner
    /// than one cell — dragging an edge through its opposite pins it there.</summary>
    public static (int X, int Y, int W, int H) Resized((int X, int Y, int W, int H) from,
                                                       (int DX, int DY) edge, (int X, int Y) to)
    {
        int x0 = from.X, x1 = from.X + from.W - 1, y0 = from.Y, y1 = from.Y + from.H - 1;
        if (edge.DX < 0) x0 = Math.Min(to.X, x1);
        if (edge.DX > 0) x1 = Math.Max(to.X, x0);
        if (edge.DY < 0) y0 = Math.Min(to.Y, y1);
        if (edge.DY > 0) y1 = Math.Max(to.Y, y0);
        return (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>The edge as the level editor's bitmask: 1 left, 2 right, 4 top, 8 bottom.</summary>
    public static int Mask((int DX, int DY) e)
        => (e.DX < 0 ? 1 : 0) | (e.DX > 0 ? 2 : 0) | (e.DY < 0 ? 4 : 0) | (e.DY > 0 ? 8 : 0);

    /// <summary>The pointer that says which way this grip would pull.</summary>
    public static Cursor? CursorFor((int DX, int DY) e) => e switch
    {
        (0, 0) => null,
        (_, 0) => UiCursors.SizeWE,
        (0, _) => UiCursors.SizeNS,
        _ when e.DX == e.DY => UiCursors.SizeNWSE,     // top-left / bottom-right
        _ => UiCursors.SizeNESW,                        // top-right / bottom-left
    };

    private static readonly Pen KnobEdge = new(Brushes.Black, 1);

    /// <summary>Knobs on all four corners and on the midpoints of the edges that resize, drawn ON
    /// the border so the thing you grab is the thing you see.</summary>
    public static void Draw(DrawingContext ctx, Rect r, double size, bool wOk = true, bool hOk = true)
    {
        double mx = (r.Left + r.Right) / 2, my = (r.Top + r.Bottom) / 2, h = size / 2;
        void Knob(double x, double y)
            => ctx.DrawRectangle(UiColors.Selection, KnobEdge, new Rect(x - h, y - h, size, size));
        if (wOk) { Knob(r.Left, my); Knob(r.Right, my); }
        if (hOk) { Knob(mx, r.Top); Knob(mx, r.Bottom); }
        Knob(r.Left, r.Top); Knob(r.Right, r.Top); Knob(r.Left, r.Bottom); Knob(r.Right, r.Bottom);
    }
}

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
