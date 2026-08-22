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
    /// Unsampled is only right where it is EXACT. A whole number of device pixels per source pixel
    /// draws nearest — the pixels are the pixels. A fractional one has to be filtered instead:
    /// nearest at 2.1x gives some source pixels two screen pixels and others three, and a grid of
    /// equal pixels drawn at unequal sizes is what makes zoomed art crawl.
    ///
    /// Device pixels, not layout pixels: on a 150% display 2x is 3 device pixels (exact) while 3x
    /// is 4.5 (not), so the same zoom can want a different answer on a different monitor.
    /// </summary>
    [Fact]
    public void OnlyWholeDevicePixelZoomsDrawUnsampled()
    {
        Assert.True(LevelView.Unsampled(1, 1));
        Assert.True(LevelView.Unsampled(8, 1));
        Assert.False(LevelView.Unsampled(2.1, 1));
        Assert.False(LevelView.Unsampled(1.9, 1));

        // 150%: 2x lands on 3 device pixels, 3x on 4.5, and 0.666… back on 1.
        Assert.True(LevelView.Unsampled(2, 1.5));
        Assert.False(LevelView.Unsampled(3, 1.5));
        Assert.True(LevelView.Unsampled(2.0 / 3, 1.5));

        // 125%: only every fourth whole zoom is exact.
        Assert.True(LevelView.Unsampled(4, 1.25));
        Assert.False(LevelView.Unsampled(3, 1.25));
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
