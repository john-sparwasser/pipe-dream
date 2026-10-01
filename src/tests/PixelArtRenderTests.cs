using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Xunit;

namespace PipeDream.Ui.Tests;

/// <summary>
/// SNES art is pixel art: any window that opens must draw bitmaps unsampled, or every zoomed
/// tile, thumbnail and GFX sheet comes out blurred. App sets this once for all windows, so
/// this is the check that an Avalonia upgrade has not quietly dropped it.
/// </summary>
public class PixelArtRenderTests
{
    /// <summary>
    /// The rule, in one place for every pixel surface: unsampled only where it is EXACT. A whole number of device pixels per source pixel
    /// draws nearest — the pixels are the pixels. A fractional one has to be filtered instead:
    /// nearest at 2.1x gives some source pixels two screen pixels and others three, and a grid of
    /// equal pixels drawn at unequal sizes is what makes zoomed art crawl.
    ///
    /// Device pixels, not layout pixels: on a 150% display 2x is 3 device pixels (exact) while 3x
    /// is 4.5 (not), so the same zoom can want a different answer on a different monitor.
    /// </summary>
    /// <summary>
    /// A layer stacked on a Draw — an overlay, a block in flight — lands in the intermediate on the
    /// base's own source pixels, whatever part of the canvas is on screen: an overlay the size of
    /// the canvas covers exactly what the base does, and a block on whole cells lands on whole
    /// intermediate pixels, so nearest puts it down unblurred.
    /// </summary>
    [Fact]
    public void a_stacked_layer_lands_on_the_bases_own_pixels()
    {
        // A 512x256 canvas at 1.5x, scrolled so source pixel (40, 20) is the visible corner, 2x oversampled.
        var src = new Avalonia.Rect(0, 0, 512, 256);
        var dst = new Avalonia.Rect(0, 0, 768, 384);
        Assert.Equal(new Avalonia.Rect(-80, -40, 1024, 512), PixelBlit.InMid(dst, dst, src, 1.5, 40, 20, 2));
        // An 8x8 cell at (6, 4) — source pixels (48, 32) — is 8 pixels in and 12 down, 16 across.
        var cell = new Avalonia.Rect(48 * 1.5, 32 * 1.5, 8 * 1.5, 8 * 1.5);
        Assert.Equal(new Avalonia.Rect(16, 24, 16, 16), PixelBlit.InMid(cell, dst, src, 1.5, 40, 20, 2));
        // A base drawn from part of its bitmap (src not at 0) keeps the same frame.
        var part = new Avalonia.Rect(100, 0, 412, 256);
        Assert.Equal(new Avalonia.Rect(0, 0, 824, 512), PixelBlit.InMid(new Avalonia.Rect(0, 0, 618, 384), new Avalonia.Rect(0, 0, 618, 384), part, 1.5, 100, 0, 2));
    }

    [Fact]
    public void OnlyWholeDevicePixelScalesDrawUnsampled()
    {
        Assert.True(PixelBlit.Whole(1, 1));
        Assert.True(PixelBlit.Whole(8, 1));
        Assert.False(PixelBlit.Whole(2.1, 1));
        Assert.False(PixelBlit.Whole(1.9, 1));

        // 150%: 2x lands on 3 device pixels, 3x on 4.5, and 0.666… back on 1.
        Assert.True(PixelBlit.Whole(2, 1.5));
        Assert.False(PixelBlit.Whole(3, 1.5));
        Assert.True(PixelBlit.Whole(2.0 / 3, 1.5));

        // 125%: only every fourth whole zoom is exact.
        Assert.True(PixelBlit.Whole(4, 1.25));
        Assert.False(PixelBlit.Whole(3, 1.25));
    }

    [AvaloniaFact]
    public void WindowsDrawBitmapsUnsampled()
    {
        var w = new Window();
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(BitmapInterpolationMode.None, RenderOptions.GetBitmapInterpolationMode(w));
    }
}
