namespace PipeDream;

// Entry point: dispatch the debug CLI flags to DebugCommands, else launch the editor.
class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--selfcheck"))
            return DebugCommands.SelfCheck();

        int ri = Array.IndexOf(args, "--render");
        if (ri >= 0)
            return DebugCommands.RenderLevel(args, ri);

        int wi = Array.IndexOf(args, "--writedm16");
        if (wi >= 0)
            return DebugCommands.WriteDm16(args, wi);

        int di = Array.IndexOf(args, "--dumpcell");
        if (di >= 0)
            return DebugCommands.DumpCell(args, di);

        int mi = Array.IndexOf(args, "--markers");
        if (mi >= 0)
            return DebugCommands.DumpMarkers(args, mi);

        int gi = Array.IndexOf(args, "--gfxsheet");
        if (gi >= 0)
            return DebugCommands.GfxSheet(args, gi);

        int bi = Array.IndexOf(args, "--blobsheet");
        if (bi >= 0)
            return DebugCommands.BlobSheet(args, bi);

        int dfi = Array.IndexOf(args, "--diff");
        if (dfi >= 0)
            return DebugCommands.DiffRoms(args[dfi + 1], args[dfi + 2]);

        int gxi = Array.IndexOf(args, "--globalexanim");
        if (gxi >= 0) return DebugCommands.DumpGlobalExAnim(args[gxi + 1]);

        int pti = Array.IndexOf(args, "--pixitrace");
        if (pti >= 0)
            return DebugCommands.PixiTrace(args, pti);

        int spd = Array.IndexOf(args, "--sprites");
        if (spd >= 0)
            return DebugCommands.DumpSprites(args, spd);

        int tpng = Array.IndexOf(args, "--tilepng");
        if (tpng >= 0)
            return DebugCommands.TilePng(args, tpng);

        int m16 = Array.IndexOf(args, "--map16def");
        if (m16 >= 0)
            return DebugCommands.Map16Def(args, m16);

        int exi = Array.IndexOf(args, "--exanim");
        if (exi >= 0)
            return DebugCommands.DumpExAnim(args[exi + 1], Convert.ToInt32(args[exi + 2], 16));

        int dsi = Array.IndexOf(args, "--disasm");
        if (dsi >= 0)
            return DebugCommands.Disassemble(args, dsi);

        int si = Array.IndexOf(args, "--gen-spritedisplay");
        if (si >= 0)
            return DebugCommands.GenSpriteDisplay(args, si);

        // Plain args: optional ROM path (+ optional hex level) to open at startup.
        using var app = new EditorApp(
            args.FirstOrDefault(File.Exists),
            args.Where(a => !File.Exists(a)).Select(a => int.TryParse(a,
                System.Globalization.NumberStyles.HexNumber, null, out int v) ? v : -1)
                .FirstOrDefault(v => v >= 0 && v < Rom.LevelCount, -1));
        try { app.Run(); }
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
