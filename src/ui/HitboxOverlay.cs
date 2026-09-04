using Avalonia;
using Avalonia.Media;

namespace PipeDream.Ui;

/// <summary>
/// Hitboxes drawn over tiles, one way for every canvas: a translucent fill where the ground is,
/// red where it hurts, a bar along a ledge's top, and a dashed box for a slope's upper tile when
/// nothing is known about the slope below it.
/// </summary>
internal static class HitboxOverlay
{
    // Green for ground, red for ground that hurts. Outlines are drawn INSIDE the cell — the
    // rect is pulled in by half the stroke — so a thick line reads as the tile's own edge
    // rather than spilling over its neighbours.
    private const double Stroke = 5;
    private static readonly IBrush SafeFill = new SolidColorBrush(Color.FromArgb(0xA8, 0x4F, 0xE9, 0x7A));
    private static readonly IBrush HurtFill = new SolidColorBrush(Color.FromArgb(0xB0, 0xE9, 0x4F, 0x4F));
    private static readonly Pen SafeEdge = new(new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x8F, 0x45)), Stroke);
    private static readonly Pen HurtEdge = new(new SolidColorBrush(Color.FromArgb(0xF0, 0xC0, 0x2A, 0x2A)), Stroke);
    private static readonly Pen Unknown = new(new SolidColorBrush(Color.FromArgb(0xD0, 0x1E, 0x8F, 0x45)), Stroke) { DashStyle = DashStyle.Dash };

    public static void Draw(DrawingContext ctx, Hitbox hb, Rect cell)
    {
        double px = cell.Width / 16, half = Stroke / 2;
        var fill = hb.Hurts ? HurtFill : SafeFill;
        var edge = hb.Hurts ? HurtEdge : SafeEdge;
        switch (hb.Kind)
        {
            case HitKind.Solid:
                ctx.DrawRectangle(fill, edge, cell.Deflate(half));
                break;
            case HitKind.Ledge:
                ctx.DrawLine(edge, new Point(cell.X, cell.Y + half), new Point(cell.Right, cell.Y + half));
                break;
            case HitKind.Slope when hb.Surface is { } s:
                // One column at a time, the way the game reads it: a staircase, not a line. The
                // fill first, then the surface stroke over it, inward from where the ground starts.
                for (int x = 0; x < 16; x++)
                    if (s[x] < 16)
                        ctx.FillRectangle(fill, new Rect(cell.X + x * px, cell.Y + s[x] * px, px, (16 - s[x]) * px));
                for (int x = 0; x < 16; x++)
                {
                    if (s[x] >= 16) continue;
                    double y = cell.Y + s[x] * px + half;
                    ctx.DrawLine(edge, new Point(cell.X + x * px, y), new Point(cell.X + (x + 1) * px, y));
                }
                break;
            case HitKind.SlopeTop:
                ctx.DrawRectangle(null, Unknown, cell.Deflate(half));
                break;
        }
    }
}
