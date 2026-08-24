using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PipeDream.Ui;

/// <summary>An Image for pixel art: draws its bitmap through <see cref="PixelBlit"/> instead of
/// letting the Image control scale it, so inline previews (the GFX drawer's bin cards) obey the
/// same one pixel rule as every canvas. Sized by <see cref="Zoom"/> (layout units per source
/// pixel), or by the width on offer when <see cref="Stretch"/> is set.</summary>
public sealed class PixelImage : Control
{
    private readonly PixelBlit blit = new();
    private double zoom = 2;

    /// <summary>A styled property, not a CLR one, so XAML templates can bind it.</summary>
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<PixelImage, Bitmap?>(nameof(Source));
    static PixelImage()
    {
        AffectsMeasure<PixelImage>(SourceProperty);
        AffectsRender<PixelImage>(SourceProperty);
    }

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double Zoom { get => zoom; set { zoom = value; InvalidateMeasure(); InvalidateVisual(); } }

    /// <summary>Fill the available width, height following the sheet's aspect. The zoom that
    /// implies is fractional, which is exactly what PixelBlit is for.</summary>
    public bool Stretch { get; set; }

    /// <summary>Round the bottom two corners by this radius — for a preview that closes a card
    /// whose header owns the top corners. A render clip, because a Border cannot round-clip a
    /// child that draws itself.</summary>
    public double BottomCornerRadius { get; set; }

    // PixelBlit only draws the part inside the ScrollViewer's viewport, and unlike the big
    // canvases this control is NOT the scrolled child — its bounds never change on scroll, so
    // nothing re-renders it. The viewport moving over it is exactly the signal to repaint.
    public PixelImage() => EffectiveViewportChanged += (_, _) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
        => Source is not { } b ? default
         : Stretch && !double.IsInfinity(availableSize.Width)
            ? new Size(availableSize.Width, availableSize.Width * b.PixelSize.Height / b.PixelSize.Width)
            : new Size(b.PixelSize.Width * zoom, b.PixelSize.Height * zoom);

    public override void Render(DrawingContext ctx)
    {
        if (Source is not { } b) return;
        var dst = Stretch ? new Rect(Bounds.Size)
            : new Rect(0, 0, b.PixelSize.Width * zoom, b.PixelSize.Height * zoom);
        using (BottomCornerRadius > 0 ? ctx.PushGeometryClip(BottomRounded(dst, BottomCornerRadius)) : default)
            blit.Draw(this, ctx, b, new Rect(0, 0, b.PixelSize.Width, b.PixelSize.Height), dst,
                      VisualRoot?.RenderScaling ?? 1);
    }

    private static StreamGeometry BottomRounded(Rect r, double rad)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(r.TopLeft, true);
        c.LineTo(r.TopRight);
        c.LineTo(new Point(r.Right, r.Bottom - rad));
        c.ArcTo(new Point(r.Right - rad, r.Bottom), new Size(rad, rad), 0, false, SweepDirection.Clockwise);
        c.LineTo(new Point(r.Left + rad, r.Bottom));
        c.ArcTo(new Point(r.Left, r.Bottom - rad), new Size(rad, rad), 0, false, SweepDirection.Clockwise);
        c.EndFigure(true);
        return g;
    }
}
