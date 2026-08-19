using System.Runtime.InteropServices;
using Avalonia;

namespace PipeDream.Ui;

/// <summary>
/// The process entry point, for both halves of pipe-dream.
///
///   PipeDream.exe [rom-or-project] [levelHex]   opens the editor
///   PipeDream.exe --headless                    lists the ROM commands
///   PipeDream.exe --selfcheck                   runs one (headless is implied)
///
/// One executable rather than two. The commands themselves are storage-layer work reached
/// through <see cref="EditorSession.RunCommandLine"/>, so this stays a composition root: it
/// decides which half of the app to start and nothing else.
/// </summary>
public static partial class Program
{
    public static string? RomPath;
    public static int LevelNum = 0x105;

    [STAThread]
    public static int Main(string[] args)
    {
        if (EditorSession.IsCommandLine(args))
        {
            AttachParentConsole();
            return EditorSession.RunCommandLine(args);
        }

        RomPath = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (args.Length > 1 && int.TryParse(args[1], System.Globalization.NumberStyles.HexNumber,
                                            null, out int lv)) LevelNum = lv;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    /// <summary>
    /// Windows only, and the price of one executable: this is a GUI-subsystem binary, so Windows
    /// gives it no console and everything written to stdout goes nowhere — a command would run
    /// and print nothing at all. Borrowing the launching terminal's console fixes that.
    ///
    /// Making it a console-subsystem binary instead would trade this for a console window
    /// flashing up every time someone opens the editor, which is the worse deal. Unix has no
    /// subsystem distinction and needs none of this.
    /// </summary>
    private static void AttachParentConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (!AttachConsole(-1)) return;         // no parent console: output is redirected
            // The streams were bound before the console existed, so they have to be rebuilt.
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch { /* worst case the output is invisible, which is where we started */ }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
