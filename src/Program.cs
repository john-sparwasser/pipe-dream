namespace PipeDream;

/// <summary>
/// The ROM toolbelt's entry point: the debug and inspection commands in
/// <see cref="DebugCommands"/>, which have no UI equivalent and are the fastest way to look at a
/// ROM, prep one, or diff two.
///
/// The EDITOR is <c>ui/PipeDream.Ui</c>. This assembly used to host it as well, back when the
/// UI was drawn with ImGui; the interface layers were separated so the storage layer could stop
/// knowing about any of that, and the Avalonia editor talks to it through
/// <c>services/PipeDream.Services</c>.
/// </summary>
class Program
{
    public static int Main(string[] args)
    {
        if (DebugCommands.TryDispatch(args) is int exitCode)
            return exitCode;

        Console.Error.WriteLine("""
            pipe-dream — ROM tools

            This is the command-line half. The editor is a separate application:

                dotnet run --project ui/PipeDream.Ui [rom-or-project] [levelHex]

            Commands available here:
            """);
        DebugCommands.PrintUsage();
        return 1;
    }
}
