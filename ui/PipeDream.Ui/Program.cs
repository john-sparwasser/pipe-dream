using Avalonia;

namespace PipeDream.Ui;

/// <summary>
/// Phase-0 spike host. Deliberately code-only (no XAML): the thing being priced here is
/// canvas throughput and headless testability, and the XAML toolchain is not on that path.
/// The real shell gets XAML in Phase 2.
///
/// Usage: PipeDream.Ui.exe [romPath] [levelHex]
/// </summary>
public static class Program
{
    public static string? RomPath;
    public static int LevelNum = 0x105;

    [STAThread]
    public static void Main(string[] args)
    {
        RomPath = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (args.Length > 1 && int.TryParse(args[1], System.Globalization.NumberStyles.HexNumber,
                                            null, out int lv)) LevelNum = lv;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
