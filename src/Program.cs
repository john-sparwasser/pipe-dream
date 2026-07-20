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

        int wi = Array.IndexOf(args, "--writedm16");
        if (wi >= 0)
            return WriteDm16(args, wi);

        int di = Array.IndexOf(args, "--dumpcell");
        if (di >= 0)
            return DumpCell(args, di);

        int mi = Array.IndexOf(args, "--markers");
        if (mi >= 0)
            return DumpMarkers(args, mi);

        using var app = new EditorApp();
        app.Run();
        return 0;
    }

    // --markers <rom> <levelHex> : debug — which object numbers still render as markers.
    private static int DumpMarkers(string[] args, int mi)
    {
        var rom = Rom.Load(args[mi + 1]);
        int level = Convert.ToInt32(args[mi + 2], 16);
        var lv = Level.Parse(rom, level);
        var grid = ObjectEngine.Render(rom, lv);
        var markers = new Dictionary<int, int>();
        for (int i = 0; i < grid.Tiles.Length; i++)
            if (grid.Tiles[i] != Map16Grid.Empty && (grid.Tiles[i] & ObjectEngine.Marker) != 0)
                markers[grid.Tiles[i] & 0xFF] = markers.GetValueOrDefault(grid.Tiles[i] & 0xFF) + 1;
        var objCounts = lv.Objects.Where(o => !o.IsScreenExit && !o.IsDm16)
            .GroupBy(o => (o.Extended, Num: o.Extended ? o.ExtendedNumber : o.Number))
            .ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"level 0x{level:X3}: {lv.Objects.Count} objects, tileset {lv.Header.Tileset}");
        foreach (var (num, cells) in markers.OrderByDescending(kv => kv.Value))
        {
            int std = objCounts.GetValueOrDefault((false, num));
            int ext = objCounts.GetValueOrDefault((true, num));
            string h = std > 0 ? $"  handler ${ObjectEngine.Handler(rom, lv.Header.Tileset, num):X6}" : "";
            Console.WriteLine($"  marker obj 0x{num:X2}: {cells} cells  (std x{std}, ext x{ext}){h}");
        }
        return 0;
    }

    // --dumpcell <rom> <levelHex> <cx> <cy> : debug — grid value + Map16 def words for a cell.
    private static int DumpCell(string[] args, int di)
    {
        var rom = Rom.Load(args[di + 1]);
        int level = Convert.ToInt32(args[di + 2], 16);
        int cx = int.Parse(args[di + 3]), cy = int.Parse(args[di + 4]);
        var lv = Level.Parse(rom, level);
        var grid = ObjectEngine.Render(rom, lv);
        int t = grid.Get(cx, cy);
        Console.WriteLine($"cell ({cx},{cy}) = 0x{t:X4}" +
                          ((t & ObjectEngine.Marker) != 0 ? " (MARKER)" : ""));
        if (t == Map16Grid.Empty || (t & ObjectEngine.Marker) != 0) return 0;
        var words = t >= Map16.FgTiles
            ? Map16.LmExtendedDef(rom, t)
            : Map16.Definition(rom, Map16.BuildDefPointers(rom, lv.Header.Tileset), t);
        foreach (var (w, q) in words.Zip(new[] { "TL", "BL", "TR", "BR" }))
            Console.WriteLine($"  {q}: raw {w.Raw:X4}  8x8 tile {w.Tile:X3} pal {w.Palette} " +
                              $"prio {(w.Priority ? 1 : 0)} flip {(w.FlipX ? "X" : "")}{(w.FlipY ? "Y" : "")}");
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
        var (px, w, h) = Map16.ComposeLevel(rom, lv.Header, grid, level);

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

    // --writedm16 <rom> <levelHex> <out> : inject known Direct-Map16 test tiles and save,
    // so the result can be opened in Lunar Magic to verify the encoding round-trips.
    private static int WriteDm16(string[] args, int wi)
    {
        string romPath = args.ElementAtOrDefault(wi + 1) ?? @"C:\SMW\Projects\.resources\after.smc";
        int level = Convert.ToInt32(args.ElementAtOrDefault(wi + 2) ?? "105", 16);
        string outPath = args.ElementAtOrDefault(wi + 3) ?? @"C:\SMW\Projects\.resources\test_dm16.smc";

        var rom = Rom.Load(romPath);
        if (!rom.HasDm16Hijack)
        {
            Console.WriteLine("ERROR: ROM lacks the LM Direct Map16 ASM — open/save it in LM once first.");
            return 1;
        }
        var lv = Level.Parse(rom, level);

        // Two known test placements in empty sky (screen 0): Form A (0x105) and Form B (0x205).
        var added = new[]
        {
            LevelObject.MakeDm16(0x105, screen: 0, xNib: 2, y: 8),
            LevelObject.MakeDm16(0x205, screen: 0, xNib: 5, y: 8),
        };
        var newObjs = added.Concat(lv.Objects).ToList();

        byte[] data = lv.Encode(rom, newObjs);
        if (rom.ActualRomSize < 0x180000) rom.ExpandTo(0x200000);
        int addr = rom.AllocateRats(data);
        rom.SetLayer1Pointer(level, addr);
        rom.SaveAs(outPath);
        Console.WriteLine($"wrote {outPath}: level 0x{level:X3}, added DM16 tiles " +
                          "0x105 @ (2,8) [Form A] and 0x205 @ (5,8) [Form B]; pointer -> $" + addr.ToString("X6"));

        // verify by reloading + re-parsing
        var re = Rom.Load(outPath);
        var rl = Level.Parse(re, level);
        var dm = rl.Objects.Where(o => o.IsDm16 && (o.Dm16Tile == 0x105 || o.Dm16Tile == 0x205)).ToList();
        Console.WriteLine("reload check: " + string.Join(" ", dm.Select(o => $"0x{o.Dm16Tile:X3}@({o.AbsoluteX},{o.Y})")));
        return 0;
    }
}
