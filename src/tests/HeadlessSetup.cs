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

/// <summary>
/// Redirects config.json to a throwaway folder for the whole test run.
///
/// <see cref="PipeDream.Config.Save"/> serialises the instance it is called on, so a test doing
/// `new Config().TouchRecentProject(...)` wrote a defaults-only config over the real user's
/// file — losing their vanilla ROM path, emulator, skipped update and recents on every
/// `dotnet test`. A module initializer is the only hook that runs before a test can load one.
/// </summary>
internal static class TestConfigDir
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Redirect() =>
        Environment.SetEnvironmentVariable(
            "PIPEDREAM_CONFIG_DIR",
            Path.Combine(Path.GetTempPath(), "pipe-dream-tests", Environment.ProcessId.ToString()));
}
