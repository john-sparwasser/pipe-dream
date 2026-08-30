using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace PipeDream.Ui;

/// <summary>
/// How this editor puts pixel art on screen — ONE rule, shared by every surface that draws it: the
/// level, the Map16 sheet in both places it appears, the 8x8 CHR grid and the GFX sheet.
///
/// A source pixel that lands on a whole number of DEVICE pixels is blitted unsampled: the pixels
/// are the pixels. Anything else is drawn sharp-bilinear — nearest up to the next whole multiple
/// into a cached intermediate, then ONE filtered step down to the size asked for. Nearest alone at
/// a fractional scale gives some source pixels two screen pixels and their neighbours three, and a
/// grid of equal pixels drawn at unequal sizes is what makes zoomed art look like it is crawling;
/// filtering the source directly instead softens the middle of every pixel, not just its edge.
///
/// Device pixels, not layout pixels: on a 150% display a 2x zoom is exactly 3 device pixels while
/// 3x is 4.5, so the same zoom wants a different answer on a different monitor. That is why every
/// surface has to go through here rather than each deciding for itself — at 125% or 150%, a canvas
/// that "obviously" scales by whole numbers does not.
///
/// Both scaling steps happen in OFF-SCREEN render targets and the on-screen draw is a 1:1 device
/// blit, on purpose: the window's context stops honouring bitmap interpolation options (pushed or
/// attached) for a subtree that has been hidden and reshown — switching canvas modes, on a
/// fractional-DPI display — and a draw recorded as HighQuality comes out nearest, which reads as
/// jagged, uneven pixels. Off-screen contexts keep honouring them, and at exactly one device pixel
/// per bitmap pixel every interpolation mode gives the same answer, so the final draw cannot be
/// mis-filtered no matter what the compositor does with its options.
///
/// The cost is bounded by construction:
///   * only the part of the owner that is ON SCREEN goes through the intermediates, so they are
///     viewport-sized and never level-sized or sheet-sized;
///   * both are cached until their size changes, so a repaint is three blits and no allocation;
///   * if the oversampled one will not fit, the MULTIPLE comes down rather than the quality —
///     there is no blurry fallback mode to get stuck in.
/// </summary>
public sealed class PixelBlit
{
    /// <summary>~24M pixels, 96MB: a 4K viewport with the oversample on top. The multiple is
    /// reduced to fit rather than giving up, so this is a memory ceiling, not a quality switch.</summary>
    private const long Cap = 24_000_000;

    private RenderTargetBitmap? mid;
    private PixelSize midSize;

    /// <summary>Which path the last draw took: "exact", "sharp", "unfiltered" (no render target on
    /// this platform) or "blank". Diagnostic: the difference is visible, so it is worth being able
    /// to ask which one happened.</summary>
    internal string LastDraw { get; private set; } = "";

    /// <summary>The intermediate's size and how many have been ALLOCATED — the whole performance
    /// argument, and the two things a test can pin without being able to time a frame.</summary>
    internal PixelSize MidSize => mid is null ? default : midSize;
    internal int Builds { get; private set; }

    /// <summary>Which of the two final targets the last sharp draw put on screen. It has to
    /// alternate — see <see cref="Fin"/>.</summary>
    internal int FinIndex => finIx;

    /// <summary>True when one source pixel covers a whole number of device pixels at this scale.</summary>
    internal static bool Whole(double zoom, double scaling)
        => Math.Abs(zoom * scaling - Math.Round(zoom * scaling)) < 0.001;

    /// <summary>
    /// Draw <paramref name="src"/> of <paramref name="bmp"/> into <paramref name="dst"/> of
    /// <paramref name="owner"/>, by the rule above. <paramref name="dst"/> may be far bigger than
    /// the window — a whole 8192px level, or a 512-row Map16 sheet — and only the visible part of
    /// it is drawn through the intermediate.
    /// </summary>
    public void Draw(Visual owner, DrawingContext ctx, IImage bmp, Rect src, Rect dst, double scaling)
    {
        FreeRetired();                 // last frame's replaced intermediates are safe to drop now
        if (src.Width < 1 || src.Height < 1 || dst.Width < 1 || dst.Height < 1) { LastDraw = "blank"; return; }
        double z = dst.Width / src.Width;

        if (Whole(z, scaling))
        {
            LastDraw = "exact";
            RenderOptions.SetBitmapInterpolationMode(owner, BitmapInterpolationMode.None);
            ctx.DrawImage(bmp, src, dst);
            return;
        }

        var vis = Visible(owner).Intersect(dst);
        if (vis.Width < 1 || vis.Height < 1) { LastDraw = "blank"; return; }

        // WHOLE source pixels covering the visible part, so the nearest step is exactly n and the
        // filtered step is the only fractional one in the chain.
        int sx = (int)Math.Floor(src.X + (vis.X - dst.X) / z);
        int sy = (int)Math.Floor(src.Y + (vis.Y - dst.Y) / z);
        int sw = (int)Math.Min(Math.Ceiling(vis.Width / z) + 1, src.Right - sx);
        int sh = (int)Math.Min(Math.Ceiling(vis.Height / z) + 1, src.Bottom - sy);
        if (sw < 1 || sh < 1) { LastDraw = "blank"; return; }

        // Device pixels per source pixel, rounded UP so the filtered step is a downscale. Comes
        // down instead of blowing the budget: 2x is still sharp-bilinear, and 2x always fits.
        int n = Math.Max(1, (int)Math.Ceiling(z * scaling - 0.001));
        while (n > 2 && (long)sw * n * sh * n > Cap) n--;

        var whole = new Rect(0, 0, sw * n, sh * n);
        var into = new Rect(dst.X + (sx - src.X) * z, dst.Y + (sy - src.Y) * z, sw * z, sh * z);
        if (Mid(new PixelSize(sw * n, sh * n)) is not { } rt)
        {
            // No render target here (a headless run without Skia). Unsampled is the honest answer:
            // wrong pixel widths beats a blurred picture, and it is what this did before.
            LastDraw = "unfiltered";
            RenderOptions.SetBitmapInterpolationMode(owner, BitmapInterpolationMode.None);
            ctx.DrawImage(bmp, new Rect(sx, sy, sw, sh), into);
            return;
        }

        // Device size of the visible part — the second intermediate, where the ONLY filtered
        // step happens. It has to happen in an off-screen context: the window's own context
        // stops honouring interpolation options once its subtree has been hidden and reshown
        // (mode switches), and a draw recorded as HighQuality comes out nearest. Off-screen
        // contexts keep honouring them, and the final on-screen blit below is 1:1 device
        // pixels, where every interpolation mode gives the same answer.
        int fw = (int)Math.Ceiling(vis.Width * scaling), fh = (int)Math.Ceiling(vis.Height * scaling);
        if (Fin(new PixelSize(fw, fh)) is not { } f2)
        {
            LastDraw = "unfiltered";
            RenderOptions.SetBitmapInterpolationMode(owner, BitmapInterpolationMode.None);
            ctx.DrawImage(bmp, new Rect(sx, sy, sw, sh), into);
            return;
        }

        using (var dc = rt.CreateDrawingContext())
        using (dc.PushRenderOptions(Options(BitmapInterpolationMode.None)))
            dc.DrawImage(bmp, new Rect(sx, sy, sw, sh), whole);

        using (var dc = f2.CreateDrawingContext())
        using (dc.PushRenderOptions(Options(BitmapInterpolationMode.HighQuality)))
            dc.DrawImage(rt, whole,
                new Rect((into.X - vis.X) * scaling, (into.Y - vis.Y) * scaling,
                         into.Width * scaling, into.Height * scaling));

        ctx.DrawImage(f2, new Rect(0, 0, fw, fh),
                      new Rect(vis.X, vis.Y, fw / scaling, fh / scaling));
        LastDraw = "sharp";
    }

    private static RenderOptions Options(BitmapInterpolationMode mode)
        => new() { BitmapInterpolationMode = mode };

    /// <summary>Intermediates outlive the frame that drew them: the on-screen DrawImage of a
    /// target is recorded now and executed by the compositor thread a little later, and in
    /// Avalonia.Skia a RenderTargetBitmap IS a WriteableBitmapImpl — disposing it on the resize
    /// that follows a mode switch (the drawer changes width, the viewport with it) is a
    /// use-after-free in sk_canvas_draw_image_rect. So a replaced target is only parked here,
    /// and freed on the NEXT draw, by which time its frame has been presented.</summary>
    private readonly List<RenderTargetBitmap> retired = [];

    private void Retire(RenderTargetBitmap? t) { if (t is not null) retired.Add(t); }

    private void FreeRetired()
    {
        foreach (var t in retired) t.Dispose();
        retired.Clear();
    }

    private RenderTargetBitmap? Mid(PixelSize size)
    {
        if (size.Width < 1 || size.Height < 1) return null;
        if (mid is not null && midSize == size) return mid;
        Retire(mid);
        mid = null;
        try { mid = new RenderTargetBitmap(size, new Vector(96, 96)); midSize = size; Builds++; }
        catch { mid = null; }
        return mid;
    }

    private readonly RenderTargetBitmap?[] fin = new RenderTargetBitmap?[2];
    private PixelSize finSize;
    private int finIx;

    /// <summary>
    /// The second intermediate: the visible part at DEVICE size, cached like the first — but kept
    /// as a PAIR that alternates per draw.
    ///
    /// The on-screen call is <c>DrawImage(fin, sameSrc, sameDst)</c> every repaint, and the
    /// compositor drops a recorded draw that is identical to the one already on screen. It cannot
    /// know this target's pixels were redrawn behind its back — unlike a WriteableBitmap, whose
    /// lock bumps a version — so a repaint whose ONLY change is inside the target (tile animation
    /// stepping a phase, an edit at a fractional zoom) never reached the screen. Alternating two
    /// targets makes each repaint a genuinely different draw. The cost is one extra
    /// viewport-sized surface.
    /// </summary>
    private RenderTargetBitmap? Fin(PixelSize size)
    {
        if (size.Width < 1 || size.Height < 1) return null;
        if (finSize != size)
        {
            for (int i = 0; i < fin.Length; i++) { Retire(fin[i]); fin[i] = null; }
            finSize = size;
        }
        finIx ^= 1;
        if (fin[finIx] is { } cached) return cached;
        try { fin[finIx] = new RenderTargetBitmap(size, new Vector(96, 96)); Builds++; }
        catch { fin[finIx] = null; }
        return fin[finIx];
    }

    /// <summary>
    /// The part of a control worth drawing through the intermediate, in its own coordinates.
    ///
    /// Never the whole control, even when the layout cannot say where the viewport is: coming back
    /// from another canvas mode the first repaint can land while the control is still marked
    /// not-effectively-visible, and TranslatePoint answers nothing then. A window-sized guess is
    /// wrong about WHERE for one frame; the full level would be wrong about HOW for as long as you
    /// look at it.
    /// </summary>
    private static Rect Visible(Visual v)
    {
        var all = new Rect(v.Bounds.Size);
        if (v.FindAncestorOfType<ScrollViewer>() is { } sv
            && sv.Viewport.Width > 1 && sv.Viewport.Height > 1
            && v.TranslatePoint(default, sv) is { } at)
            return all.Intersect(new Rect(-at.X, -at.Y, sv.Viewport.Width, sv.Viewport.Height));

        var root = v.GetVisualRoot();
        return all.Intersect(new Rect(0, 0, root?.ClientSize.Width ?? 1920, root?.ClientSize.Height ?? 1080));
    }
}
