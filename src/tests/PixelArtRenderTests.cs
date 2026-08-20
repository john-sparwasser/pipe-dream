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
    [AvaloniaFact]
    public void WindowsDrawBitmapsUnsampled()
    {
        var w = new Window();
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(BitmapInterpolationMode.None, RenderOptions.GetBitmapInterpolationMode(w));
    }
}
