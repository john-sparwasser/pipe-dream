using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PipeDream.Ui;

/// <summary>
/// The colour picker proper: a saturation/value square with a hue strip beside it, the same
/// shape the ImGui editor's ColorPicker3 had.
///
/// It is NATIVE to BGR555. Every sample drawn in the square is pushed through
/// <see cref="Palette.ToBgr555"/> and back, so what the square shows is the set of colours the
/// SNES can actually produce — the visible banding is the hardware, not a rendering artifact,
/// and what you click is exactly what you get. A 24-bit picker over a 15-bit palette would
/// quantise on commit and land a step away from the colour you aimed at.
///
/// H/S/V are the authoritative drag state, NOT the BGR555 word. Re-deriving them from the
/// quantised colour on every pointer move makes the crosshair stick and drift on the 5-bit
/// boundaries: at low saturation many hues collapse onto the same colour, so the hue you
/// picked would be forgotten the moment you dragged towards white.
/// </summary>
public class ColorPickerView : Control
{
    /// <summary>Sample resolution of the square. 64 is past the point where the 5-bit banding
    /// stops changing, so a finer grid would cost pixels to draw the same picture.</summary>
    private const int Samples = 64;

    public double SquareSize { get; set; } = 200;
    public double StripWidth { get; set; } = 22;
    public double Gap { get; set; } = 8;

    private double hue, sat, val;                 // 0..1
    private ushort color;
    private WriteableBitmap? square;
    private double squareHue = -1;                // hue the cached square was built for
    private bool draggingSquare, draggingStrip;

    /// <summary>The picked colour, BGR555. Setting it re-derives H/S/V — do that when a NEW
    /// colour arrives from outside, not from this control's own output.</summary>
    public ushort Bgr
    {
        get => color;
        set
        {
            if (color == value && squareHue >= 0) return;
            color = value;
            uint rgba = Palette.ToRgba(value);
            (hue, sat, val) = ToHsv((byte)(rgba & 0xFF), (byte)((rgba >> 8) & 0xFF),
                                   (byte)((rgba >> 16) & 0xFF));
            InvalidateVisual();
        }
    }

    public event EventHandler<ushort>? ColorChanged;

    public ColorPickerView() => Focusable = true;

    protected override Size MeasureOverride(Size available)
        => new(SquareSize + Gap + StripWidth, SquareSize);

    // ---- geometry ----

    private Rect SquareRect => new(0, 0, SquareSize, SquareSize);
    private Rect StripRect => new(SquareSize + Gap, 0, StripWidth, SquareSize);

    // ---- input ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        // Grabbing anywhere in a control's half claims the drag: clamping beats making the
        // user keep the pointer inside a 200px box while dragging to full saturation.
        if (p.X < SquareSize + Gap / 2) draggingSquare = true; else draggingStrip = true;
        Apply(p);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (draggingSquare || draggingStrip) Apply(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        draggingSquare = draggingStrip = false;
        e.Pointer.Capture(null);
    }

    private void Apply(Point p)
    {
        if (draggingSquare)
        {
            sat = Math.Clamp(p.X / SquareSize, 0, 1);
            val = Math.Clamp(1 - p.Y / SquareSize, 0, 1);
        }
        else
        {
            hue = Math.Clamp(p.Y / SquareSize, 0, 1);
        }

        var (r, g, b) = FromHsv(hue, sat, val);
        ushort next = Palette.ToBgr555(r, g, b);
        // The colour can hold still across several pixels of travel — 32 steps per channel is
        // coarse — so only a real change is reported. The crosshair still tracks the pointer.
        if (next != color) { color = next; ColorChanged?.Invoke(this, next); }
        InvalidateVisual();
    }

    // ---- HSV. The repo had no colour-space code; this is the whole of it. ----

    /// <summary>RGB8 → hue/saturation/value, each 0..1.</summary>
    internal static (double H, double S, double V) ToHsv(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = (g - b) / d % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
            if (h < 0) h += 1;
        }
        return (h, max <= 0 ? 0 : d / max, max);
    }

    /// <summary>Hue/saturation/value (0..1) → RGB8.</summary>
    internal static (byte R, byte G, byte B) FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h * 6 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h * 6) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static byte B8(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        return (B8(r + m), B8(g + m), B8(b + m));
    }

    // ---- render ----

    /// <summary>The square for the current hue, every sample snapped to what the SNES can
    /// show. Rebuilt only when the hue moves — dragging the crosshair does not change it.</summary>
    private WriteableBitmap Square()
    {
        if (square is not null && Math.Abs(squareHue - hue) < 1e-9) return square;
        var px = new uint[Samples * Samples];
        for (int y = 0; y < Samples; y++)
            for (int x = 0; x < Samples; x++)
            {
                var (r, g, b) = FromHsv(hue, x / (Samples - 1.0), 1 - y / (Samples - 1.0));
                px[y * Samples + x] = Palette.ToRgba(Palette.ToBgr555(r, g, b));
            }
        square?.Dispose();
        square = LevelBitmap.FromPixels(px, Samples, Samples);
        squareHue = hue;
        return square;
    }

    public override void Render(DrawingContext ctx)
    {
        var sq = SquareRect;
        // Nearest-neighbour: the banding is the point, and smoothing it would draw colours the
        // hardware cannot make.
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            ctx.DrawImage(Square(), new Rect(0, 0, Samples, Samples), sq);

        var strip = StripRect;
        for (int y = 0; y < Samples; y++)
        {
            var (r, g, b) = FromHsv(y / (Samples - 1.0), 1, 1);
            uint rgba = Palette.ToRgba(Palette.ToBgr555(r, g, b));
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb((byte)(rgba & 0xFF),
                                                               (byte)((rgba >> 8) & 0xFF),
                                                               (byte)((rgba >> 16) & 0xFF))),
                              new Rect(strip.X, strip.Y + y * strip.Height / Samples,
                                       strip.Width, strip.Height / Samples + 1));
        }

        var edge = new Pen(UiColors.PickerEdge);
        ctx.DrawRectangle(null, edge, sq);
        ctx.DrawRectangle(null, edge, strip);

        // Black under white on both markers: a ring in one colour vanishes against a swatch of
        // that colour, which for a colour picker is every colour.
        double cx = sq.X + sat * sq.Width, cy = sq.Y + (1 - val) * sq.Height;
        ctx.DrawEllipse(null, new Pen(Brushes.Black, 3), new Point(cx, cy), 6, 6);
        ctx.DrawEllipse(null, new Pen(Brushes.White, 1.5), new Point(cx, cy), 6, 6);

        double hy = strip.Y + hue * strip.Height;
        var caret = new Rect(strip.X - 2, hy - 2.5, strip.Width + 4, 5);
        ctx.DrawRectangle(null, new Pen(Brushes.Black, 3), caret);
        ctx.DrawRectangle(null, new Pen(Brushes.White, 1.5), caret);
    }
}
