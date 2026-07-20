namespace PipeDream;

class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--selfcheck"))
            return RomSelfCheck.Run();

        int ri = Array.IndexOf(args, "--render");
        if (ri >= 0)
            return RenderLevel(args, ri);

        using var app = new EditorApp();
        app.Run();
        return 0;
    }

    // --render <rom> <levelHex> <out.png> [cropTilesW] : compose a level to PNG for inspection.
    private static int RenderLevel(string[] args, int ri)
    {
        string romPath = args.ElementAtOrDefault(ri + 1) ?? @"C:\SMW\Projects\.resources\SMW.smc";
        int level = Convert.ToInt32(args.ElementAtOrDefault(ri + 2) ?? "105", 16);
        string outPath = args.ElementAtOrDefault(ri + 3) ?? "level.png";
        int cropW = int.TryParse(args.ElementAtOrDefault(ri + 4), out var cw) ? cw : 0;

        var rom = Rom.Load(romPath);
        var lv = Level.Parse(rom, level);
        var grid = ObjectEngine.Render(rom, lv);
        var (px, w, h) = Map16.ComposeLevel(rom, lv.Header, grid);

        if (cropW > 0 && cropW * 16 < w)
        {
            int cw16 = cropW * 16;
            var crop = new uint[cw16 * h];
            for (int y = 0; y < h; y++)
                Array.Copy(px, y * w, crop, y * cw16, cw16);
            px = crop; w = cw16;
        }
        Png.Write(outPath, px, w, h);
        Console.WriteLine($"wrote {outPath}: {w}x{h}, level 0x{level:X3}, tileset {lv.Header.Tileset}");
        return 0;
    }
}
