namespace PipeDream;

class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--selfcheck"))
            return RomSelfCheck.Run();

        using var app = new EditorApp();
        app.Run();
        return 0;
    }
}
