using Avalonia;
using Avalonia.Media;

namespace PipeDream.Ui;

/// <summary>
/// The chrome every canvas draws over its artwork — a settled selection, a live rubber band,
/// the armed pick in a drawer, a marching-ants marquee — drawn one way, from pens made once.
/// "These tiles are selected" has to read the same on the level, the Map16 sheet and the
/// background; when each canvas built its own Pen per frame they did not quite.
/// </summary>
internal static class Overlay
{
    private static readonly Pen SelectionPen = new(UiColors.Selection, 2.5);
    private static readonly Pen BandPen = new(UiColors.Band, 2);
    private static readonly Pen GrabPen = new(UiColors.Grab, 1.5);
    private static readonly Pen BrushPen = new(UiColors.Brush, 1.5);
    private static readonly Pen ArmedPen = new(UiColors.Accent, 3);
    private static readonly Pen ArmedHalo = new(UiColors.ArmedHalo, 7);
    private static readonly Pen AntsUnder = new(Brushes.Black, 1);
    private static readonly Pen AntsOver = new(Brushes.White, 1) { DashStyle = DashStyle.Dash };
    private static readonly Pen RingUnder = new(Brushes.Black, 3);
    private static readonly Pen RingOver = new(Brushes.White, 1.5);
    private static readonly Pen RingSelected = new(UiColors.Selection, 1.5);
    private static readonly Pen BadgeEdge = new(Brushes.Black, 1);
    private static readonly Pen Rung = new(Brushes.Black, 1.5);
    private static readonly Pen Cross = new(Brushes.Black, 2);
    private static readonly IBrush BadgeFill = new SolidColorBrush(Color.FromArgb(0xC8, 0x14, 0x14, 0x18));

    /// <summary>A settled selection: translucent fill under a ring.</summary>
    public static void Selection(DrawingContext ctx, Rect r) => ctx.DrawRectangle(UiColors.SelectionFill, SelectionPen, r);

    /// <summary>The selection ring alone — a preview of where a selection would be, or the one
    /// armed tile on a canvas.</summary>
    public static void Outline(DrawingContext ctx, Rect r) => ctx.DrawRectangle(null, SelectionPen, r);

    /// <summary>A live rubber band, before it settles into a selection.</summary>
    public static void Band(DrawingContext ctx, Rect r) => ctx.DrawRectangle(null, BandPen, r);

    /// <summary>A band that takes tiles as a brush rather than selecting — its own hue, because
    /// the two gestures look identical otherwise and do very different things.</summary>
    public static void Grab(DrawingContext ctx, Rect r) => ctx.DrawRectangle(null, GrabPen, r);

    /// <summary>Where a stamp would land.</summary>
    public static void Brush(DrawingContext ctx, Rect r) => ctx.DrawRectangle(null, BrushPen, r);

    /// <summary>The armed pick in a drawer — the tile the canvas will place. A soft halo under a
    /// solid ring: one tile in a sheet of 256 busy tiles needs more than a hairline to be found
    /// at a glance, and the halo reads against any art the ring alone would sink into.</summary>
    public static void Armed(DrawingContext ctx, Rect r)
    {
        ctx.DrawRectangle(null, ArmedHalo, r);
        ctx.DrawRectangle(null, ArmedPen, r);
    }

    /// <summary>Marching ants: solid dark under dashed white stays visible on any pixels.</summary>
    public static void Marquee(DrawingContext ctx, Rect r)
    {
        ctx.DrawRectangle(null, AntsUnder, r);
        ctx.DrawRectangle(null, AntsOver, r);
    }

    /// <summary>Black under white: a ring in one colour disappears against a swatch of that
    /// colour, which for a colour control is every colour.</summary>
    public static void Ring(DrawingContext ctx, Rect r)
    {
        ctx.DrawRectangle(null, RingUnder, r);
        ctx.DrawRectangle(null, RingOver, r);
    }

    /// <summary>The same ring in the selection colour — a run of swatches a preview points at,
    /// which is a selection rather than the one armed pick.</summary>
    public static void SelectionRing(DrawingContext ctx, Rect r)
    {
        ctx.DrawRectangle(null, RingUnder, r);
        ctx.DrawRectangle(null, RingSelected, r);
    }

    /// <summary>Badge text in the UI face, white unless told otherwise. Made separately from the
    /// draw because a badge is sized to its text first.</summary>
    public static FormattedText Text(string s, double size, IBrush? ink = null)
        => new(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
               Typeface.Default, size, ink ?? Brushes.White);

    /// <summary>Draw text so its CAP HEIGHT straddles <paramref name="midY"/>. FormattedText's
    /// Height carries the font's descent and line gap, and digits and capitals have no
    /// descenders, so centring on the line box parks the text against the top of a badge.
    /// DrawText takes the top of the line box, hence the Baseline term.</summary>
    public static void DrawText(DrawingContext ctx, FormattedText t, double size, double x, double midY)
        => ctx.DrawText(t, new Point(x, midY + size * 0.72 / 2 - t.Baseline));

    /// <summary>One label, centred on a point.</summary>
    public static void Label(DrawingContext ctx, string s, double size, Point centre, IBrush? ink = null)
    {
        var t = Text(s, size, ink);
        DrawText(ctx, t, size, centre.X - t.Width / 2, centre.Y);
    }

    /// <summary>A small text badge — digits on a dark box — with its top-left at <paramref name="at"/>.
    /// Returns the box so a caller can stack the next one under it.</summary>
    public static Rect Badge(DrawingContext ctx, string s, double size, Point at, IBrush? fill = null, IBrush? ink = null)
    {
        var t = Text(s, size, ink);
        var box = new Rect(at.X, at.Y, t.Width + size * 0.5, size * 1.3);
        ctx.DrawRectangle(fill ?? BadgeFill, BadgeEdge, box, 2, 2);
        DrawText(ctx, t, size, box.X + size * 0.25, box.Center.Y);
        return box;
    }

    /// <summary>The land an event step lays down: a filled, outlined footprint in the event hue.</summary>
    public static void EventPiece(DrawingContext ctx, Rect r) => ctx.DrawRectangle(UiColors.EventFill, EventEdge, r);
    private static readonly Pen EventEdge = new(UiColors.EventBadge, 1.5);

    /// <summary>Lunar Magic's colours over an invisible layer 1 path cell: a translucent fill in
    /// the kind's hue, rungs across a climb, an X where Mario can stand but not enter.</summary>
    public static void Path(DrawingContext ctx, Rect r, Overworld.PathKind kind)
    {
        switch (kind)
        {
            case Overworld.PathKind.Walk: ctx.FillRectangle(UiColors.PathWalk, r); break;
            case Overworld.PathKind.Swim: ctx.FillRectangle(UiColors.PathSwim, r); break;
            case Overworld.PathKind.Exit: ctx.FillRectangle(UiColors.PathExit, r); break;
            case Overworld.PathKind.Climb:
                ctx.FillRectangle(UiColors.PathClimb, r);
                for (int i = 1; i <= 3; i++)
                {
                    double y = r.Top + r.Height * i / 4;
                    ctx.DrawLine(Rung, new Point(r.Left + 2, y), new Point(r.Right - 2, y));
                }
                break;
            case Overworld.PathKind.Stop:
                ctx.DrawLine(Cross, new Point(r.Left + 3, r.Top + 3), new Point(r.Right - 3, r.Bottom - 3));
                ctx.DrawLine(Cross, new Point(r.Right - 3, r.Top + 3), new Point(r.Left + 3, r.Bottom - 3));
                break;
        }
    }
}
