namespace PipeDream;

// Entry point: debug CLI flags go to DebugCommands; everything else launches the editor.
class Program
{
    public static int Main(string[] args)
    {
        if (DebugCommands.TryDispatch(args) is int exitCode)
            return exitCode;

        // Plain args: optional project (.pdp) or ROM path (+ optional hex level) to open
        // at startup — also what a .pdp file-association double-click delivers.
        using var app = new EditorApp(
            args.FirstOrDefault(a => File.Exists(a) && !a.EndsWith(".pdp", StringComparison.OrdinalIgnoreCase)),
            args.Where(a => !File.Exists(a)).Select(a => int.TryParse(a,
                System.Globalization.NumberStyles.HexNumber, null, out int v) ? v : -1)
                .FirstOrDefault(v => v >= 0 && v < Rom.LevelCount, -1),
            args.FirstOrDefault(a => File.Exists(a) && a.EndsWith(".pdp", StringComparison.OrdinalIgnoreCase)));
        try
        { 
            app.Run();
        }
        catch (Exception e)
        {
            // Anything that escapes the frame loop: write a crash log next to the exe.
            var log = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e}\n\n");
            Console.Error.WriteLine(e);
            return 1;
        }
        return 0;
    }
}
