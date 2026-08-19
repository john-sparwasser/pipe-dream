using Avalonia;
using Avalonia.Headless;
using PipeDream.Ui;

[assembly: AvaloniaTestApplication(typeof(PipeDream.Ui.Tests.HeadlessSetup))]

namespace PipeDream.Ui.Tests;

/// <summary>
/// Boots the real Avalonia application into a headless platform for tests — a genuine visual
/// tree, layout and input, with no window and no GPU. This is the feedback loop the ImGui
/// editor never had: a UI change can be verified here instead of by asking a human to click.
/// </summary>
public static class HeadlessSetup
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            // Skia off: these tests assert layout, hit-testing and state, not pixels.
            UseHeadlessDrawing = true,
        });
}
