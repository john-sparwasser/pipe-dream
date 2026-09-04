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
    private static readonly Pen ArmedPen = new(UiColors.Accent, 2);
    private static readonly Pen AntsUnder = new(Brushes.Black, 1);
    private static readonly Pen AntsOver = new(Brushes.White, 1) { DashStyle = DashStyle.Dash };
    private static readonly Pen RingUnder = new(Brushes.Black, 3);
    private static readonly Pen RingOver = new(Brushes.White, 1.5);
    private static readonly Pen RingSelected = new(UiColors.Selection, 1.5);

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

    /// <summary>The armed pick in a drawer — the tile the canvas will place.</summary>
    public static void Armed(DrawingContext ctx, Rect r) => ctx.DrawRectangle(null, ArmedPen, r);

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
}
