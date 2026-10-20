using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PipeDream.Ui;
using Xunit.Sdk;

[assembly: AvaloniaTestApplication(typeof(PipeDream.Ui.Tests.HeadlessSetup))]
[assembly: PipeDream.Ui.Tests.CloseTestWindows]

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
/// Closes every window a test opened, once the test is done.
///
/// The UI tests open a MainWindow apiece and never close it, and an open window is never
/// garbage: its tile-animation timer is held by the dispatcher. Each one kept its session, ROM
/// and phase images (~85 MB) for the rest of the run, the suite grew past 14 GB, and whichever
/// UI test allocated a bitmap when the machine's commit limit ran out failed with
/// OutOfMemoryException — a different handful each run, all passing alone. Closing here rather
/// than in each of the ~80 tests also covers dialogs and tests that return early.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class CloseTestWindowsAttribute : BeforeAfterTestAttribute
{
    private static readonly List<Window> open = [];

    static CloseTestWindowsAttribute()
    {
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => open.Add(w));
        Window.WindowClosedEvent.AddClassHandler<Window>((w, _) => open.Remove(w));
    }

    public override void After(MethodInfo methodUnderTest)
    {
        // Only a UI test, whose After runs on the UI thread in the same job as its body, so no
        // other UI test is mid-way. A plain [Fact] ends on a pool thread while a UI test runs:
        // asking the dispatcher CheckAccess() from there let one through between tests, and it
        // closed the running test's window under it.
        if (!methodUnderTest.IsDefined(typeof(AvaloniaFactAttribute))
            && !methodUnderTest.IsDefined(typeof(AvaloniaTheoryAttribute))) return;
        // Newest first, so a dialog closes before the window that owns it.
        for (int i = open.Count - 1; i >= 0; i--)
            if (i < open.Count) open[i].Close();
        Dispatcher.UIThread.RunJobs();
    }
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
