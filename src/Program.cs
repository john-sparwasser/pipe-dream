using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Logging;

namespace PipeDream.Ui;

/// <summary>
/// The process entry point, for both halves of pipe-dream.
///
///   PipeDream.exe [rom-or-project.pdp] [levelHex]   opens the editor
///   PipeDream.exe --headless                        lists the ROM commands
///   PipeDream.exe --selfcheck                       runs one (headless is implied)
///
/// Local-development flags (what the repo's .vscode F5 profile passes):
///   --dev             dev mode: the Debug menu appears, and a .pdp argument that does not
///                     exist yet is created from the vanilla ROM instead of failing
///   --vanilla <smc>   configure the vanilla base ROM before the first-run prompt can ask
///
/// One executable rather than two. The commands themselves are storage-layer work reached
/// through <see cref="EditorSession.RunCommandLine"/>, so this stays a composition root: it
/// decides which half of the app to start and nothing else.
/// </summary>
public static partial class Program
{
    public static string? RomPath;
    public static int LevelNum = 0x105;
    public static bool DevMode;
    public static string? VanillaPath;

    [STAThread]
    public static int Main(string[] args)
    {
        if (EditorSession.IsCommandLine(args))
        {
            AttachParentConsole();
            return EditorSession.RunCommandLine(args);
        }

        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dev") DevMode = true;
            else if (args[i] == "--vanilla" && i + 1 < args.Length) VanillaPath = args[++i];
            else if (!args[i].StartsWith('-')) positional.Add(args[i]);
        }
        RomPath = positional.FirstOrDefault();
        if (positional.Count > 1 && int.TryParse(positional[1], System.Globalization.NumberStyles.HexNumber,
                                                 null, out int lv)) LevelNum = lv;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        // Areas listed = areas kept. LogArea.Control is dropped: its only regular output is
        // "PlatformImpl is null, couldn't handle input" for a dialog closed from inside the very
        // input event that closed it, which is noise. Binding/Property stay — those catch real
        // XAML mistakes that are otherwise silent.
        => AppBuilder.Configure<App>().UsePlatformDetect()
                     .LogToTrace(LogEventLevel.Warning, LogArea.Binding, LogArea.Property);

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
